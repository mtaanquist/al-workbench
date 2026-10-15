using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// Which service tiers a deployment to one linked on-premises environment will restart
/// once its apps are installed (#1368), as the customer's gateway settings last reported
/// them: the restart group of the database the publishing tier is listed under, how it
/// restarts, and the customer's before and after scripts. The Deploy dialog warns with
/// it; <c>GatewayDeliveryTarget</c> refuses with <see cref="Refusal"/> before anything is
/// queued. See <c>.design/on-prem-gateway.md</c>, "The restart group, as built".
/// <para>
/// <c>shared_restart</c> is enforced here and not on the gateway: only AL Workbench knows
/// which solutions are linked to which tiers. Solutions are counted under the caller's
/// query filter, so this sees its own organisation's links only.
/// </para>
/// </summary>
/// <param name="Tiers">The tiers that restart, as <c>SERVER / INSTANCE</c>, in the settings' order.</param>
/// <param name="OneByOne">True for <c>one-by-one</c>; false for stop all, then start all.</param>
/// <param name="Before">The customer's before script, or null.</param>
/// <param name="After">The customer's after script, or null.</param>
/// <param name="SharedRestart">The settings say restarting these tiers may affect more than one customer.</param>
/// <param name="OtherSolutions">How many other solutions have an environment linked to one of these tiers.</param>
/// <param name="MissingFromGroup">Tiers on the same database that are not in the group, so keep running the old version.</param>
/// <param name="Known">False when the gateway has not reported valid settings yet: the tiers are then a guess (the publishing tier alone).</param>
public sealed record RestartGroupPlan(
    IReadOnlyList<string> Tiers,
    bool OneByOne,
    string? Before,
    string? After,
    bool SharedRestart,
    int OtherSolutions,
    IReadOnlyList<string> MissingFromGroup,
    bool Known)
{
    /// <summary>
    /// Why the deployment may not run: its restart would reach environments of other
    /// solutions, and the settings do not say that is allowed. Null when it may.
    /// </summary>
    public string? Refusal => Refused
        ? $"{OnPremisesDeployFacts.NotAllowed}: {SharedReason} Ask the customer's IT to allow a shared restart, or link each solution to its own service tiers. Nothing was sent."
        : null;

    /// <summary>
    /// <see cref="Refusal"/> as the Deploy dialog says it, before anything could be sent:
    /// ending with what to do next, naming the tiers.
    /// </summary>
    public string? DialogRefusal => Refused
        ? $"{OnPremisesDeployFacts.NotAllowed}: {SharedReason} Ask the customer's IT to allow a shared restart of {string.Join(", ", Tiers)}, or to link each solution to its own service tiers, then try again."
        : null;

    private bool Refused => Known && !SharedRestart && OtherSolutions > 0;

    private string SharedReason => "the service tiers that would restart also run "
        + (OtherSolutions == 1 ? "another solution's environment" : $"the environments of {OtherSolutions} other solutions")
        + ", and restarting them would sign their users out too.";

    /// <summary>The plan for <paramref name="environmentId"/>, or null when it is not a linked on-premises environment.</summary>
    public static async Task<RestartGroupPlan?> LoadAsync(AppDbContext db, int environmentId, CancellationToken ct)
    {
        var facts = await OnPremisesDeployFacts.LoadAsync(db, environmentId, ct);
        return facts is null ? null : await LoadAsync(db, facts, ct);
    }

    /// <summary>The plan for the environment <paramref name="facts"/> were read for.</summary>
    public static async Task<RestartGroupPlan?> LoadAsync(AppDbContext db, OnPremisesDeployFacts facts, CancellationToken ct)
    {
        if (facts.EnvironmentUnlinked || facts.GatewayId is not { } gatewayId) return null;
        var (publishMachine, publishInstance) = facts.PublishingTier;
        var known = facts.Policy is { State: GatewayPolicyStates.Valid };
        var database = known
            ? facts.Policy!.Databases.FirstOrDefault(d =>
                Same(d.PublishOn.Machine, publishMachine) && Same(d.PublishOn.Instance, publishInstance))
            : null;
        IReadOnlyList<GatewayPolicyTier> group = database is { Restart.Count: > 0 }
            ? database.Restart
            : [new GatewayPolicyTier { Machine = publishMachine, Instance = publishInstance }];

        var projectId = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == facts.EnvironmentId)
            .Select(e => e.ProjectId)
            .FirstOrDefaultAsync(ct);

        // The gateway's live tiers, with their database, to find the group's tiers by name
        // and the ones on the same database the group leaves out.
        var tiers = await db.GatewayInstances.AsNoTracking()
            .Where(i => i.GatewayId == gatewayId && i.MissingSince == null)
            .Select(i => new { i.Id, i.Machine, i.Name, i.DatabaseServer, i.DatabaseInstance, i.DatabaseName })
            .ToListAsync(ct);
        var inGroup = tiers.Where(t => group.Any(g => Same(g.Machine, t.Machine) && Same(g.Instance, t.Name))).ToList();
        var groupIds = inGroup.Select(t => t.Id).ToList();

        var otherSolutions = groupIds.Count == 0 ? 0 : await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises
                        && e.MissingSince == null
                        && e.ProjectId != projectId
                        && e.GatewayInstanceId != null
                        && groupIds.Contains(e.GatewayInstanceId.Value))
            .Select(e => e.ProjectId)
            .Distinct()
            .CountAsync(ct);

        var reporting = facts.GatewayMachine;
        var key = DatabaseKey.For(facts.DatabaseServer, facts.DatabaseInstance, facts.DatabaseName, reporting);
        var missing = key is null
            ? []
            : tiers
                .Where(t => !groupIds.Contains(t.Id)
                            && DatabaseKey.For(t.DatabaseServer, t.DatabaseInstance, t.DatabaseName, reporting) == key)
                .Select(t => $"{t.Machine} / {t.Name}")
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

        return new RestartGroupPlan(
            group.Select(g => $"{g.Machine} / {g.Instance}").ToList(),
            OneByOne: string.Equals(database?.RestartMode, "one-by-one", StringComparison.OrdinalIgnoreCase),
            Before: string.IsNullOrWhiteSpace(database?.Before) ? null : database.Before,
            After: string.IsNullOrWhiteSpace(database?.After) ? null : database.After,
            SharedRestart: database?.SharedRestart == true,
            otherSolutions,
            missing,
            known);
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
