using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>What someone can do to an on-premises service tier from its environment page (#1371).</summary>
public enum ServiceTierAction
{
    /// <summary>Stop and start the environment's own service tier.</summary>
    Restart,

    /// <summary>Restart every tier of the database's restart group, as a deployment does.</summary>
    RestartAll,

    /// <summary>Stop the environment's own service tier, and leave it stopped.</summary>
    Stop,

    /// <summary>Start the environment's own service tier.</summary>
    Start,
}

/// <summary>One action the page offers, and why it cannot be taken right now (null when it can).</summary>
public sealed record ServiceTierActionChoice(ServiceTierAction Action, string? Unavailable);

/// <summary>What the environment page's service tier actions show.</summary>
/// <param name="CanManage">The person manages the Solution, so may act at all.</param>
/// <param name="TierName">The environment's service tier, <c>SERVER / INSTANCE</c>.</param>
/// <param name="GroupTierCount">How many tiers restart with the database's restart group; "Restart all" is offered from two.</param>
/// <param name="Choices">The actions offered for the tier's last known state, each with why not, when not.</param>
/// <param name="Latest">The newest service tier action on this environment, with how it went; null when there has been none.</param>
public sealed record ServiceTierActionsView(
    bool CanManage,
    string TierName,
    int GroupTierCount,
    IReadOnlyList<ServiceTierActionChoice> Choices,
    UpgradeActionRow? Latest)
{
    /// <summary>True while the newest action is still waiting for the gateway or running.</summary>
    public bool InFlight => Latest?.TierProgress is not null;
}

/// <summary>The open sessions an action would end, as the gateway last counted them.</summary>
/// <param name="OnTiers">Sessions on the tiers the action stops; null when none of them was counted.</param>
/// <param name="OnDatabase">Sessions on every tier the gateways report on the same database; null when none was counted.</param>
/// <param name="DatabaseTiers">How many tiers the gateways report on that database.</param>
/// <param name="CountedAt">When the oldest of the counts was read; null when nothing was counted.</param>
public sealed record ServiceTierSessions(int? OnTiers, int? OnDatabase, int DatabaseTiers, DateTime? CountedAt);

/// <summary>What one action will do, as its confirmation says before anything is sent.</summary>
/// <param name="Tiers">The tiers it stops or starts, <c>SERVER / INSTANCE</c>, in order.</param>
/// <param name="OneByOne">A group restart brings each tier back before the next goes down.</param>
/// <param name="LeftAlone">Tiers on the same database that a group restart leaves running.</param>
/// <param name="Refusal">Why it cannot be done; null when it can.</param>
public sealed record ServiceTierActionPlan(
    ServiceTierAction Action,
    string EnvironmentName,
    string SolutionName,
    bool IsProduction,
    IReadOnlyList<string> Tiers,
    bool OneByOne,
    IReadOnlyList<string> LeftAlone,
    ServiceTierSessions Sessions,
    string? Refusal)
{
    /// <summary>True for the actions that sign people out (all but Start).</summary>
    public bool SignsPeopleOut => Action != ServiceTierAction.Start;
}

/// <summary>
/// Restarting, stopping and starting a linked on-premises environment's service tier, or
/// restarting its database's whole restart group, from the environment page (#1371). Each
/// is one gateway command (<c>restart-instance</c>, <c>stop-instance</c>,
/// <c>start-instance</c>, <c>restart-group</c>), recorded in the environment's Workbench
/// history as an <see cref="OeEnvironmentUpgradeAction"/> that points at it, and written
/// to the audit log with who and when. See <c>.design/on-prem-gateway.md</c>, "Service tier
/// actions, as built".
///
/// <para>Only people who manage the Solution act. Everything the gateway would refuse is
/// refused here first, in plainer words; the gateway checks its own settings again whatever
/// this says. The <c>shared_restart</c> rule is enforced only here, through
/// <see cref="RestartGroupPlan"/>, since only AL Workbench knows which Solutions use a tier:
/// for the group as deployments and licence imports do, and for a single tier's restart or
/// stop on that tier alone. Starting a stopped tier signs nobody out, so it is not held to
/// it. Runs under the query filter throughout; no <c>IgnoreQueryFilters()</c>.</para>
/// </summary>
public sealed class OnPremisesTierActionService
{
    /// <summary>The permission every tier acted on needs in the customer's gateway settings.</summary>
    public const string ServiceControlPermission = "service_control";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly GatewayCommandService _commands;
    private readonly TimeProvider _clock;
    private readonly ILogger<OnPremisesTierActionService> _logger;

    public OnPremisesTierActionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        GatewayCommandService commands,
        TimeProvider clock,
        ILogger<OnPremisesTierActionService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _commands = commands;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// What the page offers for <paramref name="environmentId"/>, or null when it is not a
    /// linked on-premises environment. Throws <see cref="ProjectAccessDeniedException"/> when
    /// the person may not see its Solution.
    /// </summary>
    public async Task<ServiceTierActionsView?> GetViewAsync(int environmentId, CancellationToken ct = default)
    {
        var env = await EnvironmentAsync(environmentId, ct);
        if (env is null) return null;
        await _access.EnsureCanViewAsync(env.ProjectId, ct);
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct);
        if (facts is null || facts.EnvironmentUnlinked) return null;

        var canManage = await _access.CanManageAsync(env.ProjectId, env.CreatedBy, ct);
        var latest = await LatestAsync(env.ProjectId, environmentId, ct);
        var group = await RestartGroupPlan.LoadAsync(_db, facts, ct);
        var tier = await RestartGroupPlan.LoadForTierAsync(_db, facts, ct);

        var stopped = IsStopped(facts.InstanceState);
        var running = IsRunning(facts.InstanceState);
        var offered = new List<ServiceTierAction>();
        if (!stopped) offered.Add(ServiceTierAction.Restart);
        if ((group?.Tiers.Count ?? 0) > 1) offered.Add(ServiceTierAction.RestartAll);
        if (!stopped) offered.Add(ServiceTierAction.Stop);
        if (!running) offered.Add(ServiceTierAction.Start);

        var choices = offered
            .Select(a => new ServiceTierActionChoice(a, Refusal(a, facts, group, tier, latest, forDialog: true)))
            .ToList();
        return new ServiceTierActionsView(canManage, facts.TierName, group?.Tiers.Count ?? 1, choices, latest);
    }

    /// <summary>
    /// What <paramref name="action"/> on <paramref name="environmentId"/> will do, for its
    /// confirmation, or null when it is not a linked on-premises environment. The request
    /// asks everything again.
    /// </summary>
    public async Task<ServiceTierActionPlan?> GetPlanAsync(int environmentId, ServiceTierAction action, CancellationToken ct = default)
    {
        var env = await EnvironmentAsync(environmentId, ct);
        if (env is null) return null;
        await _access.EnsureCanViewAsync(env.ProjectId, ct);
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct);
        if (facts is null || facts.EnvironmentUnlinked) return null;
        return await PlanAsync(env, facts, action, forDialog: true, ct);
    }

    /// <summary>
    /// Queues <paramref name="action"/> on the environment's service tier through its gateway,
    /// records it in the environment's history and the audit log, and wakes the gateway.
    /// Returns the history row's id.
    /// </summary>
    /// <exception cref="ProjectAccessDeniedException">The person may not manage the Solution.</exception>
    /// <exception cref="PlanValidationException">
    /// Keyed <c>action</c> for an action that does not exist, and <c>environment</c> for
    /// anything about the environment, its gateway or the customer's settings. Nothing is
    /// recorded or sent.
    /// </exception>
    public async Task<int> RequestAsync(int environmentId, ServiceTierAction action, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; service tier action made outside an authenticated request.");
        if (!Enum.IsDefined(action))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["action"] = "That is not something AL Workbench can do to a service tier." });
        }
        var env = await EnvironmentAsync(environmentId, ct) ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["environment"] = "That environment no longer exists, or is not an on-premises one.",
        });
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);

        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That environment no longer exists." });
        var plan = await PlanAsync(env, facts, action, forDialog: false, ct);
        if (plan.Refusal is { } refusal)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = refusal });
        }

        var (verb, kind, words) = action switch
        {
            ServiceTierAction.Restart => (GatewayVerb.RestartInstance, UpgradeActionKind.RestartServiceTier, "Restart service tier"),
            ServiceTierAction.RestartAll => (GatewayVerb.RestartGroup, UpgradeActionKind.RestartServiceTiers, "Restart all service tiers on the database"),
            ServiceTierAction.Stop => (GatewayVerb.StopInstance, UpgradeActionKind.StopServiceTier, "Stop service tier"),
            _ => (GatewayVerb.StartInstance, UpgradeActionKind.StartServiceTier, "Start service tier"),
        };
        // The command names the environment's own tier; for the group the gateway finds the
        // rest in its own settings, never in the command.
        var parameters = new GatewayServiceTierParameters { Machine = facts.Machine!, Instance = facts.InstanceName! }.ToParameters();
        var userId = _orgContext.CurrentUserId;
        var actor = await AuditActor.ResolveAsync(_db, userId, ct);
        var now = Now;

        // The command, the history line and the audit row become visible together: a crash
        // in between leaves none of them, so nothing reaches a server that the page cannot name.
        long commandId;
        OeEnvironmentUpgradeAction row;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                commandId = await _commands.EnqueueAsync(new GatewayCommandRequest(
                    facts.GatewayId!.Value, verb, parameters, RequestedByUserId: userId), ct);
            }
            catch (PlanValidationException ex)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["environment"] = string.Join(" ", ex.Errors.Values) + " Nothing was sent.",
                });
            }
            row = new OeEnvironmentUpgradeAction
            {
                OrganizationId = orgId,
                ProjectId = env.ProjectId,
                EnvironmentId = environmentId,
                Kind = kind,
                Status = UpgradeActionStatus.Sent,
                RequestedByUserId = userId,
                RequestedBy = actor,
                RequestedAt = now,
                ExecuteAfter = now,
                SentAt = now,
                GatewayCommandId = commandId,
            };
            _db.OeEnvironmentUpgradeActions.Add(row);
            // The history table is itself a log and is not audited row by row; the act that
            // reaches the customer's server is, as ProjectConnectionService's writes are.
            _db.AuditLog.Add(new AuditLogEntry
            {
                Timestamp = now,
                ChangedBy = actor,
                ChangedByUserId = userId,
                OrganizationId = orgId,
                EntityType = AuditEntityType.ProjectEnvironment,
                EntityId = environmentId,
                Action = AuditAction.Updated,
                EntityName = env.Name,
                SnapshotJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["Action"] = words,
                    ["Project"] = plan.SolutionName,
                    ["Name"] = env.Name,
                    ["ServiceTier"] = facts.TierName,
                    ["ServiceTiers"] = plan.Tiers,
                    ["GatewayCommandId"] = commandId,
                }, PersistenceJson.Options),
            });
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        _commands.Wake(facts.GatewayId.Value);

        _logger.LogInformation(
            "{Action} asked for on environment {EnvironmentId} ({Tier}) by user {UserId}: {Verb} command {CommandId} queued, history row {ActionId}.",
            action, environmentId, facts.TierName, userId, GatewayVerbs.ToWire(verb), commandId, row.Id);
        return row.Id;
    }

    private sealed record EnvironmentRow(int ProjectId, string Name, int? CreatedBy, string ProjectName);

    private async Task<EnvironmentRow?> EnvironmentAsync(int environmentId, CancellationToken ct) =>
        await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new EnvironmentRow(e.ProjectId, e.Name, e.Project!.CreatedByUserId, e.Project.Name))
            .FirstOrDefaultAsync(ct);

    private async Task<ServiceTierActionPlan> PlanAsync(EnvironmentRow env, OnPremisesDeployFacts facts, ServiceTierAction action,
        bool forDialog, CancellationToken ct)
    {
        var group = await RestartGroupPlan.LoadAsync(_db, facts, ct);
        var tier = await RestartGroupPlan.LoadForTierAsync(_db, facts, ct);
        var latest = await LatestAsync(env.ProjectId, facts.EnvironmentId, ct);
        var tiers = action == ServiceTierAction.RestartAll && group is not null ? group.Tiers : [facts.TierName];
        var sessions = await SessionsAsync(facts, tiers, ct);
        return new ServiceTierActionPlan(
            action,
            facts.EnvironmentName,
            env.ProjectName,
            string.Equals(facts.EnvironmentType, "Production", StringComparison.OrdinalIgnoreCase),
            tiers,
            OneByOne: action == ServiceTierAction.RestartAll && group?.OneByOne == true,
            action == ServiceTierAction.RestartAll ? group?.MissingFromGroup ?? [] : [],
            sessions,
            Refusal(action, facts, group, tier, latest, forDialog));
    }

    /// <summary>
    /// Why <paramref name="action"/> cannot be done now; null when it can. In the order the
    /// gateway would refuse, so the first thing said is the first thing to fix.
    /// </summary>
    private string? Refusal(ServiceTierAction action, OnPremisesDeployFacts facts, RestartGroupPlan? group, RestartGroupPlan? tier,
        UpgradeActionRow? latest, bool forDialog)
    {
        var notAllowed = OnPremisesDeployFacts.NotAllowed;
        if (facts.EnvironmentUnlinked)
        {
            return $"{facts.EnvironmentName} is no longer linked to a service tier. An administrator can link it again on the Solution's Environments tab.";
        }
        if (facts.InstanceMissing)
        {
            return $"The gateway no longer reports {facts.TierName}. Check the service tier with the customer's IT.";
        }
        switch (facts.GatewayStatusAt(Now))
        {
            case GatewayStatus.Online:
                break;
            case GatewayStatus.Revoked:
                return $"The gateway {facts.GatewayName} has been revoked. An administrator can set up a gateway under Administration → Gateways.";
            case GatewayStatus.WaitingForApproval:
                return $"The gateway {facts.GatewayName} is waiting for an administrator to approve it under Administration → Gateways.";
            default:
                return "The gateway is offline, so it can't reach the service tier. Ask the customer's IT to check that the gateway service is running"
                    + (string.IsNullOrWhiteSpace(facts.GatewayMachine) ? "" : $" on {facts.GatewayMachine}")
                    + ", then try again.";
        }
        if (facts.Policy is null)
        {
            return "The gateway has not reported its settings yet, so what it may do is not known. Wait a minute for its next check-in, then try again.";
        }
        if (facts.Policy.State != GatewayPolicyStates.Valid)
        {
            return facts.Policy.State switch
            {
                GatewayPolicyStates.Unsafe =>
                    $"{notAllowed}: the gateway's program files can be changed by people who are not administrators, so it refuses every change until the customer's IT fixes that.",
                GatewayPolicyStates.Invalid =>
                    $"{notAllowed}: the settings file on the gateway's server has mistakes, so the gateway only reads until the customer's IT corrects it.",
                _ => $"{notAllowed}: the gateway has no settings file, so it allows nothing yet. Ask the customer's IT to set it up.",
            };
        }
        var touched = action == ServiceTierAction.RestartAll && group is not null ? group.Tiers : [facts.TierName];
        var notControllable = touched
            .Where(name => PolicyEntry(facts.Policy, name) is not { } entry
                           || !entry.Allow.Contains(ServiceControlPermission, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (notControllable.Count > 0)
        {
            return $"{notAllowed}: {string.Join(", ", notControllable)} may not be stopped, started or restarted from AL Workbench. Ask the customer's IT to allow it.";
        }
        if (latest is { TierProgress: { } progress })
        {
            return progress == ServiceTierProgress.Waiting
                ? "The last action on this service tier is still waiting for the gateway to pick it up. Wait for it to finish, then try again."
                : "The gateway is still carrying out the last action on this service tier. Wait for it to finish, then try again.";
        }
        // Someone else's users are on these tiers, and the customer has not said that is
        // allowed. Only AL Workbench knows which Solutions use a tier, so this never reaches
        // the gateway. Starting a tier signs nobody out, so it is not held to the rule.
        return action switch
        {
            ServiceTierAction.RestartAll when group is { Refused: true } => SharedRefusal("Restarting", group, forDialog),
            // One sentence for both, so the card says it once rather than twice over.
            ServiceTierAction.Restart or ServiceTierAction.Stop when tier is { Refused: true } => SharedRefusal("Restarting or stopping", tier, forDialog),
            _ => null,
        };
    }

    /// <summary>
    /// The <c>shared_restart</c> refusal for a tier action, in the shape deployments and
    /// licence imports give it: the effect first, then what the customer's IT can change.
    /// </summary>
    internal static string SharedRefusal(string doing, RestartGroupPlan plan, bool forDialog)
    {
        var tiers = string.Join(", ", plan.Tiers);
        var them = plan.Tiers.Count == 1 ? "it" : "them";
        var others = plan.OtherSolutions == 1
            ? $"another Solution that also uses {them}"
            : $"{plan.OtherSolutions} other Solutions that also use {them}";
        return $"{doing} {tiers} would also sign out the users of {others}. "
            + "Ask the customer's IT to allow shared restarts for this database in the gateway's settings file (shared_restart), or to give each Solution its own service tiers"
            + (forDialog ? ", then try again." : ". Nothing was sent.");
    }

    private static GatewayPolicyInstance? PolicyEntry(GatewayPolicyReport policy, string tierName)
    {
        var slash = tierName.IndexOf(" / ", StringComparison.Ordinal);
        if (slash < 0) return null;
        var machine = tierName[..slash];
        var instance = tierName[(slash + 3)..];
        return policy.Instances.FirstOrDefault(i => Same(i.Machine, machine) && Same(i.Name, instance));
    }

    /// <summary>The newest service tier action on the environment, read against its command.</summary>
    private async Task<UpgradeActionRow?> LatestAsync(int projectId, int environmentId, CancellationToken ct)
    {
        var a = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.EnvironmentId == environmentId
                        && (x.Kind == UpgradeActionKind.RestartServiceTier || x.Kind == UpgradeActionKind.RestartServiceTiers
                            || x.Kind == UpgradeActionKind.StopServiceTier || x.Kind == UpgradeActionKind.StartServiceTier))
            .OrderByDescending(x => x.RequestedAt).ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id, x.ProjectId, x.EnvironmentId, x.Kind, x.Status, x.RequestedBy, x.RequestedAt, x.ExecuteAfter,
                x.SentAt, x.Outcome, x.CancelledBy, x.CancelledAt, x.GatewayCommandId,
            })
            .FirstOrDefaultAsync(ct);
        if (a is null) return null;
        var row = new UpgradeActionRow(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status, a.RequestedBy, a.RequestedAt,
            a.ExecuteAfter, a.SentAt, a.Outcome, a.CancelledBy, a.CancelledAt, GatewayCommandId: a.GatewayCommandId);
        return (await ServiceTierActionOutcome.ApplyAsync(_db, [row], ct))[0];
    }

    /// <summary>
    /// The sessions open on <paramref name="tiers"/> and across every tier on the same
    /// database, as the gateways last counted them: the whole tier, every tenant on it,
    /// since stopping a tier signs out everyone on it. Tiers are matched on the database's
    /// server, instance and name however each gateway spelled them (<see cref="DatabaseKey"/>).
    /// </summary>
    private async Task<ServiceTierSessions> SessionsAsync(OnPremisesDeployFacts facts, IReadOnlyList<string> tiers, CancellationToken ct)
    {
        var all = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.MissingSince == null || i.Id == facts.InstanceId)
            .Select(i => new
            {
                i.Id, i.GatewayId, i.Machine, i.Name, i.DatabaseServer, i.DatabaseInstance, i.DatabaseName,
                i.SessionCount, i.ReadAt, GatewayMachine = i.Gateway!.MachineName,
            })
            .ToListAsync(ct);
        var linked = all.FirstOrDefault(t => t.Id == facts.InstanceId);
        if (linked is null) return new ServiceTierSessions(null, null, 0, null);
        var key = DatabaseKey.For(linked.DatabaseServer, linked.DatabaseInstance, linked.DatabaseName, linked.GatewayMachine);
        var sharing = key is null
            ? [linked]
            : all.Where(t => t.Id == linked.Id
                             || DatabaseKey.For(t.DatabaseServer, t.DatabaseInstance, t.DatabaseName, t.GatewayMachine) == key).ToList();
        var acted = all.Where(t => t.GatewayId == linked.GatewayId
                                   && tiers.Any(n => Same(n, $"{t.Machine} / {t.Name}"))).ToList();
        if (acted.Count == 0) acted = [linked];

        static int? Sum(IEnumerable<int?> counts)
        {
            int? total = null;
            foreach (var count in counts)
            {
                if (count is { } n) total = (total ?? 0) + n;
            }
            return total;
        }
        var counted = sharing.Concat(acted).Where(t => t.SessionCount is not null && t.ReadAt is not null).ToList();
        return new ServiceTierSessions(
            Sum(acted.Select(t => t.SessionCount)),
            Sum(sharing.Select(t => t.SessionCount)),
            sharing.Count,
            counted.Count == 0 ? null : counted.Min(t => t.ReadAt));
    }

    private static bool IsStopped(string? state) => Same(state, "Stopped");

    private static bool IsRunning(string? state) => Same(state, "Running");

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
