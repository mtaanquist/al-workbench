using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>Allowed, or refused with a sentence saying why.</summary>
public sealed record PolicyDecision(bool Allowed, string? Reason)
{
    /// <summary>
    /// The policy the decision was judged against (set by <see cref="IGatewayPolicy.Check"/>).
    /// A verb reads tiers, databases and scripts from this one, never from a later
    /// <see cref="IGatewayPolicy.InForce"/>, so what it acts on is what was allowed.
    /// </summary>
    public GatewayPolicy? Policy { get; init; }

    public static readonly PolicyDecision Allow = new(true, null);

    public static PolicyDecision Refuse(string reason) => new(false, reason);
}

/// <summary>
/// An app a <c>publish-install</c> command says it will publish. Every field is the
/// server's claim: the policy check refuses on it early, and the verb must check the
/// <c>.app</c> itself (its publisher, its signature, and that the files are the apps
/// declared here) before publishing anything (#1375).
/// </summary>
/// <param name="File">The name of the command's file that holds the app.</param>
/// <param name="Publisher">The publisher the command declares.</param>
/// <param name="Signed">Whether the command declares the app signed.</param>
/// <param name="Id">The app id the command declares; the policy check does not need it, the verb does.</param>
/// <param name="Name">The app name the command declares, when it sends one.</param>
/// <param name="Version">The version the command declares; the verb needs it.</param>
public sealed record AppToPublish(string File, string Publisher, bool Signed, string? Id = null, string? Name = null, string? Version = null);

/// <summary>
/// What the local policy check needs to know about a command, read from its
/// parameters (<see cref="GatewayCommandParameters"/>).
/// </summary>
public sealed record PolicyRequest(
    GatewayVerb Verb,
    TierRef? Target,
    string Tenant,
    IReadOnlyList<AppToPublish> Apps,
    bool UnpublishPrevious,
    bool StartedByAgent,
    string SyncMode = GatewaySyncModes.Add)
{
    /// <summary>
    /// Reads the request out of a command, or says why it cannot. A parameter of the
    /// wrong shape refuses the command rather than being read as absent, so a
    /// malformed command can never slip past a check that looks for the parameter.
    /// </summary>
    public static PolicyRequest? FromCommand(GatewayCommandMessage command, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(command);
        problem = null;
        var parameters = command.Parameters ?? new JsonObject();
        if (!TryText(parameters, GatewayCommandParameters.Machine, out var machine, ref problem)
            || !TryText(parameters, GatewayCommandParameters.Instance, out var instance, ref problem)
            || !TryText(parameters, GatewayCommandParameters.Tenant, out var tenant, ref problem))
        {
            return null;
        }
        if ((machine is null) != (instance is null))
        {
            problem = "The command names a machine or a server instance but not both.";
            return null;
        }

        var apps = new List<AppToPublish>();
        if (parameters[GatewayCommandParameters.Apps] is { } appsNode)
        {
            if (appsNode is not JsonArray array)
            {
                problem = "The command's list of apps is not a list.";
                return null;
            }
            foreach (var item in array)
            {
                if (item is not JsonObject app
                    || !TryText(app, GatewayCommandParameters.AppPublisher, out var publisher, ref problem)
                    || publisher is null)
                {
                    problem ??= "An app in the command has no publisher.";
                    return null;
                }
                if (!TryText(app, GatewayCommandParameters.AppFile, out var file, ref problem)
                    || !TryText(app, GatewayCommandParameters.AppId, out var id, ref problem)
                    || !TryText(app, GatewayCommandParameters.AppName, out var name, ref problem)
                    || !TryText(app, GatewayCommandParameters.AppVersion, out var version, ref problem))
                {
                    return null;
                }
                if (app[GatewayCommandParameters.AppSigned] is { } signedNode
                    && (signedNode.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)))
                {
                    problem = "An app in the command says whether it is signed in a form the gateway does not read.";
                    return null;
                }
                var signed = app[GatewayCommandParameters.AppSigned]?.GetValue<bool>() ?? false;
                apps.Add(new AppToPublish(file ?? string.Empty, publisher, signed, id, name, version));
            }
        }
        if (command.Verb == GatewayVerb.PublishInstall && (problem = AppsMatchFiles(apps, command.Files)) is not null)
        {
            return null;
        }

        var unpublish = false;
        if (parameters[GatewayCommandParameters.UnpublishPrevious] is { } unpublishNode)
        {
            if (unpublishNode.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            {
                problem = "The command says whether to unpublish previous versions in a form the gateway does not read.";
                return null;
            }
            unpublish = unpublishNode.GetValue<bool>();
        }

        if (!TryText(parameters, GatewayCommandParameters.SyncMode, out var syncMode, ref problem))
        {
            return null;
        }
        syncMode ??= GatewaySyncModes.Add;
        if (!GatewaySyncModes.All.Contains(syncMode, StringComparer.Ordinal))
        {
            problem = $"The command asks for a schema sync mode the gateway does not know; it takes {string.Join(" or ", GatewaySyncModes.All)}.";
            return null;
        }

        return new PolicyRequest(
            command.Verb,
            machine is null ? null : new TierRef(machine, instance!),
            tenant ?? GatewayCommandParameters.DefaultTenant,
            apps,
            unpublish,
            command.StartedByAgent,
            syncMode);
    }

    /// <summary>
    /// A <c>publish-install</c> must say what it publishes, one app for each file it
    /// carries. Without that, an empty or missing list would pass every publisher and
    /// signing rule while the verb published whatever files came with the command.
    /// </summary>
    private static string? AppsMatchFiles(IReadOnlyList<AppToPublish> apps, IReadOnlyList<GatewayCommandFileMessage> files)
    {
        if (apps.Count == 0)
        {
            return "The command does not list the apps it would publish, so the gateway cannot check them against its settings.";
        }
        var names = files.Select(f => f.Name).ToList();
        var named = apps.Select(a => a.File).ToList();
        if (named.Any(string.IsNullOrEmpty)
            || named.Distinct(StringComparer.Ordinal).Count() != named.Count
            || names.Distinct(StringComparer.Ordinal).Count() != names.Count
            || !named.Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return "The command's list of apps does not match its files one to one, so the gateway cannot tell which publisher each file is from.";
        }
        return null;
    }

    private static bool TryText(JsonObject parameters, string name, out string? value, ref string? problem)
    {
        value = null;
        var node = parameters[name];
        if (node is null) return true;
        if (node.GetValueKind() != System.Text.Json.JsonValueKind.String || string.IsNullOrWhiteSpace(node.GetValue<string>()))
        {
            problem = $"The command's '{name}' is not text the gateway can read.";
            return false;
        }
        value = node.GetValue<string>().Trim();
        return true;
    }
}

/// <summary>
/// Decides whether the local policy allows a command, before any verb runs and
/// before its files are downloaded. The gateway's own check: AL Workbench decides
/// what to ask, but whatever it sends, a tier the file does not list is never
/// touched and a permission the file does not give is never used. See
/// <c>.design/on-prem-gateway.md</c>, "The local policy file" and "MCP parity".
///
/// <para>What each verb needs:</para>
/// <list type="bullet">
/// <item><c>refresh-inventory</c>: <c>inventory</c> on the tier it names; with no tier
/// named, it reads only the tiers that allow <c>inventory</c>. Reads are never limited
/// by <c>change_hours</c> or by who asked.</item>
/// <item><c>publish-install</c>: sent to the database's publishing tier; <c>publish</c>
/// there, the tenant and every publisher on its lists, <c>allow_unsigned</c> for an
/// unsigned app, <c>allow_force_sync</c> for a ForceSync, <c>unpublish</c> when previous versions are removed, <c>scripts</c>
/// when the database names a before or after script, and <c>restart</c> on every tier
/// of the restart group.</item>
/// <item><c>import-licence</c>: sent to the publishing tier of a database with a
/// <c>licence_target</c>; <c>licence</c> there (and the tenant, for a tenant licence),
/// and <c>restart</c> on every tier of the restart group.</item>
/// <item><c>restart-group</c>: <c>service_control</c> on every tier of the group.</item>
/// <item><c>restart-instance</c>, <c>stop-instance</c>, <c>start-instance</c>:
/// <c>service_control</c> on the tier.</item>
/// </list>
/// <para>Every change is then refused when any tier it touches is production and an AI
/// assistant asked for it, or when it is outside a touched tier's <c>change_hours</c>.</para>
/// </summary>
public static class PolicyEnforcer
{
    private const string Settings = "the customer's gateway settings";

    public static PolicyDecision Check(GatewayPolicy policy, PolicyRequest request, DateTimeOffset utcNow, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Verb == GatewayVerb.RefreshInventory)
        {
            if (request.Target is null)
            {
                return policy.Instances.Any(i => i.Allows(PolicyPermission.Inventory))
                    ? PolicyDecision.Allow
                    : PolicyDecision.Refuse($"No service tier in {Settings} (gateway.toml) allows inventory, so there is nothing the gateway may read.");
            }
            var read = policy.Find(request.Target);
            if (read is null) return NotListed(request.Target);
            return Need(read, PolicyPermission.Inventory) ?? PolicyDecision.Allow;
        }

        if (request.Target is null)
        {
            return PolicyDecision.Refuse("The command does not say which service tier it is for, so the gateway does not run it.");
        }
        var tier = policy.Find(request.Target);
        if (tier is null) return NotListed(request.Target);
        var database = policy.DatabaseOf(tier.Tier);
        var group = (database?.Restart ?? [tier.Tier]).Select(t => policy.Find(t)!).ToList();
        var touched = new List<EffectiveInstance> { tier };

        PolicyDecision? refusal;
        switch (request.Verb)
        {
            case GatewayVerb.PublishInstall:
                refusal = PublishingTier(database, tier, "Apps")
                    ?? Need(tier, PolicyPermission.Publish)
                    ?? Tenant(tier, request.Tenant)
                    ?? Apps(tier, request.Apps)
                    ?? ForceSync(tier, request.SyncMode)
                    ?? (request.UnpublishPrevious ? Need(tier, PolicyPermission.Unpublish) : null)
                    ?? (database is { Before: not null } or { After: not null } ? Need(tier, PolicyPermission.Scripts) : null)
                    ?? NeedAll(group, PolicyPermission.Restart);
                touched.AddRange(group);
                break;

            case GatewayVerb.ImportLicence:
                if (database is null || database.LicenceTarget is null)
                {
                    return PolicyDecision.Refuse(
                        $"Not allowed by {Settings}: no [[database]] section with a licence_target includes {tier.Tier}, so the gateway does not know where to import a licence.");
                }
                refusal = PublishingTier(database, tier, "Licences")
                    ?? Need(tier, PolicyPermission.Licence)
                    ?? (database.LicenceTarget == LicenceTarget.Tenant ? Tenant(tier, request.Tenant) : null)
                    ?? NeedAll(group, PolicyPermission.Restart);
                touched.AddRange(group);
                break;

            case GatewayVerb.RestartGroup:
                refusal = NeedAll(group, PolicyPermission.ServiceControl);
                touched.AddRange(group);
                break;

            case GatewayVerb.RestartInstance:
            case GatewayVerb.StopInstance:
            case GatewayVerb.StartInstance:
                refusal = Need(tier, PolicyPermission.ServiceControl);
                break;

            default:
                return PolicyDecision.Refuse($"The gateway's settings have no rule for '{GatewayVerbs.ToWire(request.Verb)}' commands, so it does not run them.");
        }
        if (refusal is not null) return refusal;

        foreach (var each in touched.Distinct())
        {
            if (request.StartedByAgent && each.Environment == PolicyEnvironment.Production)
            {
                return PolicyDecision.Refuse(
                    $"{each.Tier} is a production service tier, and AI assistants cannot change on-premises production. Start this from AL Workbench in the browser.");
            }
        }
        foreach (var each in touched.Distinct())
        {
            if (each.ChangeHours is { } window && !window.Contains(utcNow, zone))
            {
                return PolicyDecision.Refuse(
                    $"Outside the hours set on the customer's gateway: {each.Tier} allows changes only from {window.Start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)} to {window.End.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}, local time.");
            }
        }
        return PolicyDecision.Allow;
    }

    private static PolicyDecision NotListed(TierRef tier) => PolicyDecision.Refuse(
        $"{tier} is not listed in {Settings} (gateway.toml), so the gateway does not touch it.");

    private static PolicyDecision? Need(EffectiveInstance tier, PolicyPermission permission) => tier.Allows(permission)
        ? null
        : PolicyDecision.Refuse($"Not allowed by {Settings}: {tier.Tier} does not allow '{PolicyWords.Permissions[permission]}'.");

    private static PolicyDecision? NeedAll(IEnumerable<EffectiveInstance> tiers, PolicyPermission permission) =>
        tiers.Select(t => Need(t, permission)).FirstOrDefault(r => r is not null);

    private static PolicyDecision? PublishingTier(PolicyDatabase? database, EffectiveInstance tier, string what) =>
        database is null || database.PublishOn.Equals(tier.Tier)
            ? null
            : PolicyDecision.Refuse($"Not allowed by {Settings}: {what} for the database \"{database.Name}\" go through {database.PublishOn}, not {tier.Tier}.");

    private static PolicyDecision? Tenant(EffectiveInstance tier, string tenant) => tier.AllowsTenant(tenant)
        ? null
        : PolicyDecision.Refuse($"Not allowed by {Settings}: {tier.Tier} may not act on tenant '{tenant}'.");

    /// <summary>ForceSync can drop tables and fields with their data, so the tier has to allow it by name.</summary>
    internal static PolicyDecision? ForceSync(EffectiveInstance tier, string syncMode) =>
        syncMode != GatewaySyncModes.ForceSync || tier.AllowForceSync
            ? null
            : PolicyDecision.Refuse($"Not allowed by {Settings}: {tier.Tier} does not allow ForceSync (allow_force_sync).");

    private static PolicyDecision? Apps(EffectiveInstance tier, IReadOnlyList<AppToPublish> apps)
    {
        foreach (var app in apps)
        {
            if (!tier.AllowsPublisher(app.Publisher))
            {
                return PolicyDecision.Refuse($"Not allowed by {Settings}: {tier.Tier} does not take apps from the publisher '{app.Publisher}'.");
            }
            // A declared signature is a claim, not proof: with allow_unsigned off, the
            // verb checks the .app's own signature before publishing (#1375).
            if (!app.Signed && !tier.AllowUnsigned)
            {
                return PolicyDecision.Refuse($"Not allowed by {Settings}: {tier.Tier} does not take unsigned apps.");
            }
        }
        return null;
    }
}
