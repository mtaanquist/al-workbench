using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// What deploying to one linked on-premises environment depends on, read in one go from
/// the environment row, its service tier and its gateway: which tier publishes, what the
/// customer's gateway settings (the policy file the gateway reports) allow there, and
/// whether the gateway can be reached. Read by <c>GatewayDeliveryTarget</c> before it
/// queues anything, by <c>DeliveryService</c> and <c>ReleasePipelineService</c> to refuse
/// what the gateway would refuse anyway (a Force sync it does not allow), and by the
/// pipeline editor to offer only what is allowed. See <c>.design/on-prem-gateway.md</c>,
/// "Deploying to on-premises".
/// <para>
/// Everything here is as the gateway last reported it. The gateway checks its own file
/// again before it runs anything, whatever this says; these reads only let AL Workbench
/// say no sooner and in plainer words.
/// </para>
/// </summary>
public sealed record OnPremisesDeployFacts(
    int EnvironmentId,
    string EnvironmentName,
    string EnvironmentType,
    string? EnvironmentStatus,
    bool EnvironmentUnlinked,
    string? TenantId,
    int? InstanceId,
    string? Machine,
    string? InstanceName,
    string? InstanceState,
    bool InstanceMissing,
    bool TenantMissing,
    bool Multitenant,
    string? DatabaseServer,
    string? DatabaseInstance,
    string? DatabaseName,
    int? GatewayId,
    string? GatewayName,
    string? GatewayMachine,
    GatewayState? GatewayState,
    DateTime? GatewayLastSeenAt,
    DateTime? GatewayRevokedAt,
    GatewayPolicyReport? Policy)
{
    /// <summary>The permission <c>publish</c> in the policy file.</summary>
    public const string PublishPermission = "publish";

    /// <summary>The words every policy refusal starts with, the same the environment page greys things out with.</summary>
    public const string NotAllowed = "Not allowed by the customer's gateway settings";

    /// <summary>The linked tier as people read it: <c>SERVER / INSTANCE</c>.</summary>
    public string TierName => $"{Machine} / {InstanceName}";

    /// <summary>The gateway's state as the Gateways page shows it, at <paramref name="now"/>.</summary>
    public GatewayStatus GatewayStatusAt(DateTime now) =>
        GatewayState is { } state
            ? GatewayService.StatusOf(state, GatewayLastSeenAt, GatewayRevokedAt, now)
            : GatewayStatus.Revoked;

    /// <summary>
    /// The tier that publishes for the linked tier's database: the policy's
    /// <c>publish_on</c> for the database the linked tier is listed under, else the linked
    /// tier itself. One tier of a database publishes; the others only restart.
    /// </summary>
    public (string Machine, string Instance) PublishingTier
    {
        get
        {
            var machine = Machine ?? string.Empty;
            var instance = InstanceName ?? string.Empty;
            var database = Policy?.Databases.FirstOrDefault(d =>
                SameTier(d.PublishOn, machine, instance) || d.Restart.Any(t => SameTier(t, machine, instance)));
            return database is not null && !string.IsNullOrWhiteSpace(database.PublishOn.Machine)
                ? (database.PublishOn.Machine, database.PublishOn.Instance)
                : (machine, instance);
        }
    }

    /// <summary>The policy's entry for the publishing tier; null when there is no policy or the tier is not in it.</summary>
    public GatewayPolicyInstance? PublishingPolicy
    {
        get
        {
            var (machine, instance) = PublishingTier;
            return Policy?.Instances.FirstOrDefault(i =>
                Same(i.Machine, machine) && Same(i.Name, instance));
        }
    }

    /// <summary>
    /// Whether a deployment may sync with ForceSync: only when the publishing tier's
    /// policy says <c>allow_force_sync = true</c>. No report, or an older gateway that does
    /// not report the setting, reads as not allowed, which is what the gateway does too.
    /// </summary>
    public bool AllowForceSync =>
        Policy is { State: GatewayPolicyStates.Valid } && PublishingPolicy is { AllowForceSync: true };

    /// <summary>
    /// Why the customer's gateway settings would refuse a deployment to this tier, as the
    /// gateway last reported them; null when they would let it through, or when the gateway
    /// has not reported its settings yet (it then decides for itself).
    /// </summary>
    public string? PolicyRefusal
    {
        get
        {
            if (Policy is null) return null;
            var (machine, instance) = PublishingTier;
            var tier = $"{machine} / {instance}";
            if (Policy.State != GatewayPolicyStates.Valid)
            {
                return Policy.State switch
                {
                    GatewayPolicyStates.Unsafe =>
                        $"{NotAllowed}: the gateway's program files can be changed by people who are not administrators, so it refuses every change until the customer's IT fixes that. Nothing was sent.",
                    GatewayPolicyStates.Invalid =>
                        $"{NotAllowed}: the settings file on the gateway's server has mistakes, so the gateway only reads until the customer's IT corrects it. Nothing was sent.",
                    _ =>
                        $"{NotAllowed}: the gateway has no settings file, so it allows nothing yet. Ask the customer's IT to set it up. Nothing was sent.",
                };
            }
            var entry = PublishingPolicy;
            if (entry is null)
            {
                return $"{NotAllowed}: {tier} is not one of the service tiers the gateway may change. Ask the customer's IT to add it. Nothing was sent.";
            }
            if (!entry.Allow.Contains(PublishPermission, StringComparer.OrdinalIgnoreCase))
            {
                return $"{NotAllowed}: apps may not be published on {tier}. Ask the customer's IT to allow it. Nothing was sent.";
            }
            return null;
        }
    }

    /// <summary>
    /// Reads the facts for <paramref name="environmentId"/>, or null when there is no such
    /// environment or it is not an on-premises one. Under the caller's query filter.
    /// </summary>
    public static async Task<OnPremisesDeployFacts?> LoadAsync(AppDbContext db, int environmentId, CancellationToken ct)
    {
        var env = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new
            {
                e.Id, e.Name, e.Type, e.Status, e.MissingSince, e.TenantId, e.GatewayInstanceId,
            })
            .FirstOrDefaultAsync(ct);
        if (env is null) return null;

        var instance = env.GatewayInstanceId is not { } instanceId ? null : await db.GatewayInstances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Select(i => new
            {
                i.Id, i.Machine, i.Name, i.State, i.MissingSince, i.Multitenant,
                i.DatabaseServer, i.DatabaseInstance, i.DatabaseName,
                LiveTenants = i.Tenants.Count(t => t.MissingSince == null),
                i.GatewayId,
                GatewayName = i.Gateway!.Name,
                GatewayMachine = i.Gateway.MachineName,
                GatewayState = i.Gateway.State,
                i.Gateway.LastSeenAt,
                i.Gateway.RevokedAt,
                i.Gateway.EffectivePolicyJson,
            })
            .FirstOrDefaultAsync(ct);

        var tenantMissing = false;
        if (instance is not null && env.TenantId is { } tenantId)
        {
            var tenant = tenantId.ToLower();
            tenantMissing = await db.GatewayInstanceTenants.AsNoTracking()
                .Where(t => t.InstanceId == instance.Id && t.TenantId.ToLower() == tenant)
                .AnyAsync(t => t.MissingSince != null, ct);
        }

        return new OnPremisesDeployFacts(
            env.Id, env.Name, env.Type, env.Status,
            EnvironmentUnlinked: env.MissingSince is not null || instance is null,
            env.TenantId,
            instance?.Id, instance?.Machine, instance?.Name, instance?.State,
            InstanceMissing: instance?.MissingSince is not null,
            TenantMissing: tenantMissing,
            Multitenant: instance is not null && (instance.Multitenant == true || instance.LiveTenants > 1),
            instance?.DatabaseServer, instance?.DatabaseInstance, instance?.DatabaseName,
            instance?.GatewayId, instance?.GatewayName, instance?.GatewayMachine, instance?.GatewayState,
            instance?.LastSeenAt, instance?.RevokedAt,
            ParsePolicy(instance?.EffectivePolicyJson));
    }

    /// <summary>Whether a linked on-premises environment's tier lets a deployment sync with ForceSync. False for anything else.</summary>
    public static async Task<bool> AllowsForceSyncAsync(AppDbContext db, int environmentId, CancellationToken ct) =>
        await LoadAsync(db, environmentId, ct) is { AllowForceSync: true };

    /// <summary>Why a deployment may not use ForceSync on <paramref name="environmentName"/>.</summary>
    public static string ForceSyncRefusal(string environmentName) =>
        $"{NotAllowed}: {environmentName} doesn't allow Force sync. Use Add, or ask the customer's IT to allow Force sync on that service tier.";

    /// <summary>The policy report as stored, or null when there is none or it can't be read.</summary>
    internal static GatewayPolicyReport? ParsePolicy(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<GatewayPolicyReport>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool SameTier(GatewayPolicyTier tier, string machine, string instance) =>
        Same(tier.Machine, machine) && Same(tier.Instance, instance);

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
