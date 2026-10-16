using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <param name="Choices">The actions offered for the tier's state, each with why not, when not.</param>
/// <param name="Latest">The newest service tier action on this environment, with how it went; null when there has been none.</param>
/// <param name="State">
/// The tier's state as far as AL Workbench knows it: what the last finished action left it in
/// when that is newer than the last inventory, else the inventory's word. Null when not read.
/// </param>
/// <param name="StateFromAction">True when <paramref name="State"/> comes from the last action rather than the inventory.</param>
/// <param name="SharedRefusal">
/// The <c>shared_restart</c> refusal, said once above the buttons when it holds them back;
/// null when it does not apply. The choices it holds back carry no reason of their own.
/// </param>
public sealed record ServiceTierActionsView(
    bool CanManage,
    string TierName,
    int GroupTierCount,
    IReadOnlyList<ServiceTierActionChoice> Choices,
    UpgradeActionRow? Latest,
    string? State = null,
    bool StateFromAction = false,
    string? SharedRefusal = null)
{
    /// <summary>True while the newest action is still waiting for the gateway or running.</summary>
    public bool InFlight => Latest?.TierProgress is not null;
}

/// <summary>The open sessions an action would end, as the gateway last counted them.</summary>
/// <param name="OnTiers">Sessions on the tiers the action stops; null when none of them was counted.</param>
/// <param name="OnDatabase">Sessions on every tier the gateways report on the same database; null when none was counted.</param>
/// <param name="DatabaseTiers">How many tiers the gateways report on that database.</param>
/// <param name="CountedAt">When the oldest of the counts was read; null when nothing was counted.</param>
/// <param name="Uncounted">How many of the tiers the action stops have no count, so <paramref name="OnTiers"/> is a floor.</param>
public sealed record ServiceTierSessions(int? OnTiers, int? OnDatabase, int DatabaseTiers, DateTime? CountedAt, int Uncounted = 0);

/// <summary>What one action will do, as its confirmation says before anything is sent.</summary>
/// <param name="IsProduction">
/// The action reaches Production: this environment is Production, another environment
/// linked to a touched tier is, or the customer's settings call a touched tier production.
/// </param>
/// <param name="Tiers">The tiers it stops or starts, <c>SERVER / INSTANCE</c>, in order.</param>
/// <param name="OneByOne">A group restart brings each tier back before the next goes down.</param>
/// <param name="LeftAlone">Tiers on the same database that a group restart leaves running.</param>
/// <param name="Refusal">Why it cannot be done; null when it can.</param>
/// <param name="OtherProduction">
/// Other environments' names (<c>Solution Environment</c>) that are Production and linked to a
/// touched tier, when this environment is not Production itself.
/// </param>
/// <param name="IsOwnProduction">This environment itself is Production.</param>
public sealed record ServiceTierActionPlan(
    ServiceTierAction Action,
    string EnvironmentName,
    string SolutionName,
    bool IsProduction,
    IReadOnlyList<string> Tiers,
    bool OneByOne,
    IReadOnlyList<string> LeftAlone,
    ServiceTierSessions Sessions,
    string? Refusal,
    IReadOnlyList<string>? OtherProduction = null,
    bool IsOwnProduction = false)
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

    /// <summary>
    /// The first key of the transaction lock that lets one request at a time per gateway
    /// decide whether something else is already working on its tiers ("STAC").
    /// </summary>
    private const int TierActionLockClass = 0x53_54_41_43;

    /// <summary>The verbs that stop, start or restart tiers, so overlap with a tier action.</summary>
    private static readonly GatewayVerb[] TierVerbs =
    [
        GatewayVerb.PublishInstall, GatewayVerb.ImportLicence, GatewayVerb.RestartGroup,
        GatewayVerb.RestartInstance, GatewayVerb.StopInstance, GatewayVerb.StartInstance,
    ];

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
        var pending = await PendingAsync(facts, ct);
        var (state, fromAction) = await StateAsync(facts, ct);

        var stopped = IsStopped(state);
        var running = IsRunning(state);
        var offered = new List<ServiceTierAction>();
        if (!stopped) offered.Add(ServiceTierAction.Restart);
        if ((group?.Tiers.Count ?? 0) > 1) offered.Add(ServiceTierAction.RestartAll);
        if (!running) offered.Add(ServiceTierAction.Start);
        // Last: the one action that leaves people unable to work until someone acts again.
        if (!stopped) offered.Add(ServiceTierAction.Stop);

        var choices = offered
            .Select(a => new ServiceTierActionChoice(a, Refusal(a, facts, group, tier, pending, forDialog: true)))
            .ToList();
        // The shared rule holds back restart and stop for one reason; the card says it once.
        var shared = choices.Select(c => c.Unavailable).FirstOrDefault(IsSharedRefusal);
        if (shared is not null)
        {
            choices = choices.Select(c => IsSharedRefusal(c.Unavailable) ? c with { Unavailable = string.Empty } : c).ToList();
        }
        return new ServiceTierActionsView(canManage, facts.TierName, group?.Tiers.Count ?? 1, choices, latest, state, fromAction, shared);
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
        var gatewayId = facts.GatewayId!.Value;
        var now = Now;

        // The command, the history line and the audit row become visible together: a crash
        // in between leaves none of them, so nothing reaches a server that the page cannot name.
        long commandId;
        OeEnvironmentUpgradeAction row;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // One request per gateway at a time gets past here, and only after the one
                // before it committed: two people pressing Restart at once send one restart,
                // and the second is told what is already running.
                await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({TierActionLockClass}, {gatewayId})", ct);
                var busy = Busy(plan.Tiers, await GroupTiersAsync(facts, ct), await PendingAsync(facts, ct));
                if (busy is not null)
                {
                    throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = busy });
                }

                try
                {
                    commandId = await _commands.EnqueueAsync(new GatewayCommandRequest(
                        gatewayId, verb, parameters, RequestedByUserId: userId), ct);
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
            catch
            {
                // The command EnqueueAsync saved, and the rows added after it, are rolled back
                // with the transaction but would stay tracked; the next save on this scoped
                // context would then write a history line for a command that does not exist.
                _db.ChangeTracker.Clear();
                throw;
            }
        }
        _commands.Wake(gatewayId);

        _logger.LogInformation(
            "{Action} asked for on environment {EnvironmentId} ({Tier}) by user {UserId}: {Verb} command {CommandId} queued, history row {ActionId}.",
            action, environmentId, facts.TierName, userId, GatewayVerbs.ToWire(verb), commandId, row.Id);
        return row.Id;
    }

    private sealed record EnvironmentRow(int ProjectId, string Name, int? CreatedBy, string ProjectName);

    /// <summary>A command on the gateway that stops, starts or restarts tiers and has not finished.</summary>
    private sealed record PendingCommand(GatewayVerb Verb, GatewayCommandStatus Status, string? Tier);

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
        var pending = await PendingAsync(facts, ct);
        var tiers = action == ServiceTierAction.RestartAll && group is not null ? group.Tiers : [facts.TierName];
        var sessions = await SessionsAsync(facts, tiers, ct);

        // Production is about the people on the tiers, not the page the button is on: a Test
        // environment's tier can also carry the same Solution's Production tenant, and a
        // restart of every tier can reach a tier the customer calls production.
        var ownProduction = string.Equals(facts.EnvironmentType, "Production", StringComparison.OrdinalIgnoreCase);
        var otherProduction = ownProduction ? [] : await OtherProductionAsync(facts, tiers, ct);
        var policyProduction = facts.Policy is { State: GatewayPolicyStates.Valid } policy
            && tiers.Any(n => PolicyEntry(policy, n) is { } entry
                              && string.Equals(entry.Environment?.Trim(), "production", StringComparison.OrdinalIgnoreCase));
        return new ServiceTierActionPlan(
            action,
            facts.EnvironmentName,
            env.ProjectName,
            ownProduction || otherProduction.Count > 0 || policyProduction,
            tiers,
            OneByOne: action == ServiceTierAction.RestartAll && group?.OneByOne == true,
            action == ServiceTierAction.RestartAll ? group?.MissingFromGroup ?? [] : [],
            sessions,
            Refusal(action, facts, group, tier, pending, forDialog),
            otherProduction,
            ownProduction);
    }

    /// <summary>Production environments other than this one linked to any of <paramref name="tiers"/>, as <c>Solution Environment</c>.</summary>
    private async Task<List<string>> OtherProductionAsync(OnPremisesDeployFacts facts, IReadOnlyList<string> tiers, CancellationToken ct)
    {
        var ids = await TierIdsAsync(facts, tiers, ct);
        if (ids.Count == 0) return [];
        var rows = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises
                        && e.MissingSince == null
                        && e.Id != facts.EnvironmentId
                        && e.GatewayInstanceId != null
                        && ids.Contains(e.GatewayInstanceId.Value))
            .Select(e => new { Solution = e.Project!.Name, e.Name, e.Type })
            .ToListAsync(ct);
        return rows.Where(r => string.Equals(r.Type, "Production", StringComparison.OrdinalIgnoreCase))
            .Select(r => $"{r.Solution} {r.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The live gateway instances on this environment's gateway named by <paramref name="tiers"/>.</summary>
    private async Task<List<int>> TierIdsAsync(OnPremisesDeployFacts facts, IReadOnlyList<string> tiers, CancellationToken ct)
    {
        var all = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.GatewayId == facts.GatewayId && (i.MissingSince == null || i.Id == facts.InstanceId))
            .Select(i => new { i.Id, i.Machine, i.Name })
            .ToListAsync(ct);
        return all.Where(i => tiers.Any(n => Same(n, $"{i.Machine} / {i.Name}"))).Select(i => i.Id).ToList();
    }

    /// <summary>
    /// Why <paramref name="action"/> cannot be done now; null when it can. In the order the
    /// gateway would refuse, so the first thing said is the first thing to fix.
    /// </summary>
    private string? Refusal(ServiceTierAction action, OnPremisesDeployFacts facts, RestartGroupPlan? group, RestartGroupPlan? tier,
        IReadOnlyList<PendingCommand> pending, bool forDialog)
    {
        var notAllowed = OnPremisesDeployFacts.NotAllowed;
        if (facts.EnvironmentUnlinked)
        {
            return $"{facts.EnvironmentName} is no longer linked to a service tier. An administrator can link it again on the Solution's Environments tab.";
        }
        if (facts.InstanceMissing)
        {
            return $"The gateway no longer reports {facts.TierName}, so it can't be restarted from here. Ask the customer's IT to check the gateway.";
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
        if (Busy(touched, group?.Tiers ?? [facts.TierName], pending) is { } busy)
        {
            return busy;
        }
        // Someone else's users are on these tiers, and the customer has not said that is
        // allowed. Only AL Workbench knows which Solutions use a tier, so this never reaches
        // the gateway. Starting a tier signs nobody out, so it is not held to the rule.
        return action switch
        {
            ServiceTierAction.RestartAll when group is { Refused: true } => SharedRefusal(group, sameTier: false, forDialog),
            ServiceTierAction.Restart or ServiceTierAction.Stop when tier is { Refused: true } => SharedRefusal(tier, sameTier: true, forDialog),
            _ => null,
        };
    }

    /// <summary>The wording every <c>shared_restart</c> refusal from this service starts with, so the card can tell it apart.</summary>
    private const string SharedRefusalMarker = "would also sign out people using ";

    private static bool IsSharedRefusal(string? reason) =>
        reason is not null && reason.Contains(SharedRefusalMarker, StringComparison.Ordinal);

    /// <summary>
    /// The <c>shared_restart</c> refusal for a tier action: the effect first, then what the
    /// customer's IT can change. <c>shared_restart</c> is a line in the gateway's settings on
    /// the customer's server, with no screen in AL Workbench, so it is named as that, as
    /// <see cref="RestartGroupPlan.RefusalFor"/> names it for deployments.
    /// </summary>
    internal static string SharedRefusal(RestartGroupPlan plan, bool sameTier, bool forDialog)
    {
        var others = plan.OtherSolutions == 1 ? "another Solution" : $"{plan.OtherSolutions} other Solutions";
        var where = sameTier ? "the same service tier" : "the same database";
        return $"Restarting {string.Join(", ", plan.Tiers)} {SharedRefusalMarker}{others} on {where}. "
            + "Ask the customer's IT to allow shared restarts for this database in the AL Workbench gateway's settings (the shared_restart setting), "
            + $"or to give each Solution its own service tier{(sameTier ? "" : "s")}."
            + (forDialog ? "" : " Nothing was sent.");
    }

    /// <summary>
    /// What is already working on <paramref name="touched"/>, as the plain sentence the person
    /// reads; null when nothing is. A deployment, a licence import and a restart of every tier
    /// reach the whole restart group <paramref name="groupTiers"/> when the tier they name is
    /// in it; the single-tier verbs reach their own tier.
    /// </summary>
    private static string? Busy(IReadOnlyList<string> touched, IReadOnlyList<string> groupTiers, IReadOnlyList<PendingCommand> pending)
    {
        foreach (var command in pending)
        {
            if (command.Tier is not { } tier) continue;
            var groupWide = command.Verb is GatewayVerb.PublishInstall or GatewayVerb.ImportLicence or GatewayVerb.RestartGroup;
            IReadOnlyList<string> reach = groupWide && groupTiers.Any(g => Same(g, tier)) ? groupTiers : [tier];
            if (!reach.Any(r => touched.Any(t => Same(t, r)))) continue;

            var what = command.Verb switch
            {
                GatewayVerb.PublishInstall => "A deployment to this server",
                GatewayVerb.ImportLicence => "A licence import on this server",
                GatewayVerb.RestartGroup => "A restart of every service tier on this database",
                GatewayVerb.StopInstance => $"A stop of {tier}",
                GatewayVerb.StartInstance => $"A start of {tier}",
                _ => $"A restart of {tier}",
            };
            var where = command.Status == GatewayCommandStatus.Running ? "is still running" : "is still waiting for the gateway";
            return $"{what} {where}; try again when it has finished.";
        }
        return null;
    }

    /// <summary>
    /// The unfinished commands on this environment's gateway that stop, start or restart tiers,
    /// oldest first. Inside <see cref="RequestAsync"/> this is read under the gateway's lock.
    /// </summary>
    private async Task<List<PendingCommand>> PendingAsync(OnPremisesDeployFacts facts, CancellationToken ct)
    {
        if (facts.GatewayId is not { } gatewayId) return [];
        var rows = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId
                        && (c.Status == GatewayCommandStatus.Queued || c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running)
                        && TierVerbs.Contains(c.Verb))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Select(c => new { c.Verb, c.Status, c.ParametersJson })
            .ToListAsync(ct);
        return rows.Select(c => new PendingCommand(c.Verb, c.Status, ServiceTierActionOutcome.TierOf(c.ParametersJson))).ToList();
    }

    private async Task<IReadOnlyList<string>> GroupTiersAsync(OnPremisesDeployFacts facts, CancellationToken ct) =>
        (await RestartGroupPlan.LoadAsync(_db, facts, ct))?.Tiers ?? [facts.TierName];

    /// <summary>
    /// The tier's state for what the page offers: what the last finished action on it left it
    /// in, when that finished after the inventory last read the tier, else the inventory's
    /// word. So a tier stopped a moment ago offers Start at once rather than after the next
    /// inventory, up to 15 minutes later.
    /// </summary>
    private async Task<(string? State, bool FromAction)> StateAsync(OnPremisesDeployFacts facts, CancellationToken ct)
    {
        if (facts.GatewayId is not { } gatewayId || facts.InstanceId is not { } instanceId) return (facts.InstanceState, false);
        var readAt = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.Id == instanceId)
            .Select(i => i.ReadAt)
            .FirstOrDefaultAsync(ct);
        var finished = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId
                        && c.Status == GatewayCommandStatus.Succeeded
                        && c.FinishedAt != null
                        && (readAt == null || c.FinishedAt > readAt)
                        && (c.Verb == GatewayVerb.RestartInstance || c.Verb == GatewayVerb.StopInstance
                            || c.Verb == GatewayVerb.StartInstance || c.Verb == GatewayVerb.RestartGroup))
            .OrderByDescending(c => c.FinishedAt).ThenByDescending(c => c.Id)
            .Select(c => new { c.Verb, c.ParametersJson, c.ResultJson })
            .Take(20)
            .ToListAsync(ct);
        foreach (var command in finished)
        {
            var result = ParseObject(command.ResultJson);
            if (command.Verb == GatewayVerb.RestartGroup)
            {
                var tier = GatewayRestartGroupResult.FromJson(result)?.Tiers
                    .FirstOrDefault(t => Same($"{t.Machine} / {t.Instance}", facts.TierName));
                if (tier is null) continue;
                return tier.Outcome == GatewayRestartTierOutcomes.Restarted ? ("Running", true) : (tier.State ?? facts.InstanceState, tier.State is not null);
            }
            if (!Same(ServiceTierActionOutcome.TierOf(command.ParametersJson), facts.TierName)) continue;
            return GatewayServiceTierResult.FromJson(result)?.State is { } state ? (state, true) : (facts.InstanceState, false);
        }
        return (facts.InstanceState, false);
    }

    private static JsonObject? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
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
            counted.Count == 0 ? null : counted.Min(t => t.ReadAt),
            acted.Count(t => t.SessionCount is null));
    }

    private static bool IsStopped(string? state) => Same(state, "Stopped");

    private static bool IsRunning(string? state) => Same(state, "Running");

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
