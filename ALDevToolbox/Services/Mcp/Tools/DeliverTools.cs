using System.ComponentModel;
using System.Globalization;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// Read-only MCP tools over the Deliver area - a solution's customer information, its
/// Business Central environments, the Upgrades fleet, and deliveries across solutions -
/// so an assistant can answer the operations questions the pages answer (issue #912).
///
/// <para><b>Every tool here only reads, and a test holds it to that.</b>
/// <c>DeliverToolsTests</c> walks this class and fails if any tool is not
/// <c>ReadOnly = true</c>, so a write cannot be added here by accident; the one write in
/// the area, <c>deploy_build</c>, stays in <see cref="DeliveryTools"/> behind its own
/// gate.</para>
///
/// <para><b>No tool calls a customer's tenant.</b> Environment facts come from the mirror
/// the nightly sweep and each Refresh write (<c>oe_project_environments</c>,
/// <c>oe_environment_apps</c>); sessions and Business Central's own operations log are
/// live reads made with the customer's credentials and stay on the environment page.
/// Every mirrored fact carries the time it was read, so an assistant can say how fresh
/// its answer is.</para>
///
/// <para><b>The gates are the pages' gates.</b> Everything is reached through the same
/// service methods the pages call, which apply
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>; a Private solution the caller is
/// not on is absent from every list and answers "does not exist" by id or name. The
/// Upgrades list asks for the environment-updates grant, as its page does. See
/// <c>.design/saas-delivery.md</c> ("MCP parity") and
/// <c>.design/teams-and-visibility.md</c>.</para>
/// </summary>
[McpServerToolType]
public sealed class DeliverTools
{
    private readonly ProjectService _projects;
    private readonly ProjectCustomerInfoService _customerInfo;
    private readonly ProjectConnectionService _connections;
    private readonly UpgradeFleetService _fleet;
    private readonly UpgradeActionService _upgradeActions;
    private readonly EnvironmentUpgradeService _plannedUpgrades;
    private readonly CustomerModuleService _modules;
    private readonly DeliveryFeedService _deliveries;
    private readonly ProjectAccess _access;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<DeliverTools> _logger;

    /// <summary>How many Workbench history lines <c>get_environment</c> carries; the rest are one call away.</summary>
    private const int RecentHistoryCount = 5;

    public DeliverTools(
        ProjectService projects,
        ProjectCustomerInfoService customerInfo,
        ProjectConnectionService connections,
        UpgradeFleetService fleet,
        UpgradeActionService upgradeActions,
        EnvironmentUpgradeService plannedUpgrades,
        CustomerModuleService modules,
        DeliveryFeedService deliveries,
        ProjectAccess access,
        IOrganizationContext orgContext,
        ILogger<DeliverTools> logger)
    {
        _projects = projects;
        _customerInfo = customerInfo;
        _connections = connections;
        _fleet = fleet;
        _upgradeActions = upgradeActions;
        _plannedUpgrades = plannedUpgrades;
        _modules = modules;
        _deliveries = deliveries;
        _access = access;
        _orgContext = orgContext;
        _logger = logger;
    }

    // ── Solutions ───────────────────────────────────────────────────────

    [McpServerTool(Name = "get_solution", ReadOnly = true)]
    [Description("Returns one solution's basics: where its Business Central runs (hosting, and whether that is on-premises), the Business Central version and web client address, visibility, license and user experience, the customer's Microsoft tenant id, whether the Business Central online connection is set up, and a one-line summary of each of its environments. For an online solution with a working connection, the version and address are what the production environment last reported (versionFromBusinessCentral = true, with versionReadAt); otherwise they are what somebody typed. Environment facts are the workbench's mirror as of the nightly sweep or the last Refresh, not a live read. Use get_environment for one environment in full.")]
    public async Task<SolutionDetail> GetSolutionAsync(
        [Description("Solution name or numeric id (from list_solutions).")] string solution,
        CancellationToken ct = default)
    {
        var (id, project) = await ResolveAsync(solution, ct);
        var basics = await Guard(solution, () => _customerInfo.GetBasicsAsync(id, ct))
            ?? throw NotFound(solution);

        BusinessCentralConnection? connection = null;
        if (!basics.IsOnPremises)
        {
            var status = await Guard(solution, () => _connections.GetConnectionAsync(id, ct));
            if (status is not null)
            {
                connection = new BusinessCentralConnection(
                    status.IsConfigured, status.VerifiedAt, status.EffectiveSecretExpiresAt, status.UsesOrganizationRegistration);
            }
        }

        var environments = (await _fleet.ListFleetAsync(includeSoftDeleted: true, ct))
            .Where(r => r.ProjectId == id)
            .Select(r => new SolutionEnvironmentBrief(
                r.EnvironmentId, r.EnvironmentName, r.EnvironmentType, r.Status, r.Version,
                r.IsSoftDeleted, r.EnvironmentFetchedAt))
            .ToList();

        return new SolutionDetail(
            id,
            project.Name,
            project.ShortName,
            project.Visibility.ToString(),
            basics.HostingType?.ToString(),
            basics.IsOnPremises,
            basics.EffectiveVersion,
            basics.EffectiveClientUrl,
            basics.FromBusinessCentral,
            basics.FetchedAt,
            basics.LicenseType?.ToString(),
            basics.UserExperience?.ToString(),
            basics.VoiceAccountNumber,
            basics.TenantId,
            connection,
            environments);
    }

    // ── Environments ────────────────────────────────────────────────────

    [McpServerTool(Name = "list_environments", ReadOnly = true)]
    [Description("Lists the Business Central environments of every solution you can see, with filters. Each row gives the solution, the environment's name, type (Production/Sandbox), status, version, database size and the customer tenant's storage use against its quota (tenantStorageUse is a fraction: 0.8 = 80%, above 1 = over the quota), Microsoft's update window, and the next platform update (target version, date, the latest date it can be moved to, and its status). Everything is the workbench's mirror as of the nightly sweep or the last Refresh, not a live read: each row says when its facts were read (environmentReadAt, nextUpdateReadAt, updateWindowReadAt). Deleted environments are left out unless includeDeleted is true. Use an environmentId with get_environment.")]
    public async Task<IReadOnlyList<EnvironmentSummary>> ListEnvironmentsAsync(
        [Description("Optional solution name or numeric id, to list only that solution's environments.")] string? solution = null,
        [Description("Optional environment type, e.g. 'Production' or 'Sandbox'.")] string? type = null,
        [Description("Optional status as Business Central reports it, e.g. 'Active' or 'Upgrading'.")] string? status = null,
        [Description("Optional version prefix: '25' matches every 25.x, '25.3' matches 25.3.x.")] string? version = null,
        [Description("Optional fraction: only environments whose tenant uses more than this share of its storage quota, e.g. 0.8 for over 80%.")] double? storageOver = null,
        [Description("Optional date (yyyy-MM-dd, UTC): only environments whose next platform update is scheduled before it.")] string? nextUpdateBefore = null,
        [Description("Include environments the customer has deleted but Business Central still keeps. Default false.")] bool includeDeleted = false,
        CancellationToken ct = default)
    {
        int? projectId = string.IsNullOrWhiteSpace(solution) ? null : (await ResolveAsync(solution, ct)).Id;
        var before = ParseDate(nextUpdateBefore, "nextUpdateBefore");
        var prefix = version?.Trim();

        IEnumerable<EnvironmentDetailRow> rows = await _fleet.ListFleetDetailsAsync(includeDeleted, ct);
        if (projectId is { } pid) rows = rows.Where(r => r.Fleet.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(type))
            rows = rows.Where(r => string.Equals(r.Fleet.EnvironmentType, type.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(status))
            rows = rows.Where(r => string.Equals(r.Fleet.Status, status.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(prefix))
            rows = rows.Where(r => r.Fleet.Version is { } v
                && (v == prefix || v.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)));
        if (storageOver is { } threshold)
            rows = rows.Where(r => r.Fleet.TenantStorageUse is { } use && use > threshold);
        if (before is { } cutoff)
            rows = rows.Where(r => r.Fleet.NextUpdateDate is { } date && date < cutoff);

        return rows.Select(ToSummary).ToList();
    }

    [McpServerTool(Name = "get_environment", ReadOnly = true)]
    [Description("Returns one Business Central environment in full: everything list_environments gives, plus its country and Azure region, the daily window our own deliveries prefer, how AppSource apps update, the apps Business Central last reported installed (name, publisher, version, and whether this workbench has delivered it), and the latest few lines of its Workbench history. All of it is the workbench's mirror as of the nightly sweep or the last Refresh, not a live read - installedAppsReadAt says when the app list was read, and is null when it never has been. Business Central's own operations log and who is signed in are live reads and are not available here; list_environment_history has the full Workbench history.")]
    public async Task<EnvironmentDetail> GetEnvironmentAsync(
        [Description("Environment id (from list_environments or get_solution).")] int environmentId,
        CancellationToken ct = default)
    {
        var row = await _fleet.GetEnvironmentAsync(environmentId, ct) ?? throw EnvironmentNotFound(environmentId);
        var apps = await _fleet.ListInstalledAppsAsync(environmentId, ct) ?? [];
        var history = await Guard(environmentId, () => _upgradeActions.ListEnvironmentActivityAsync(row.Fleet.ProjectId, environmentId, ct));

        return new EnvironmentDetail(
            ToSummary(row),
            row.CountryCode,
            row.LocationName,
            row.DeliveryWindowStart is { } s && row.DeliveryWindowEnd is { } e
                ? new TimeWindow(Clock(s), Clock(e), row.Fleet.TimeZone ?? "UTC")
                : null,
            row.AppSourceAppsUpdateCadence,
            apps.Select(a => new InstalledApp(a.AppId, a.Name, a.Publisher, a.Version, a.DeliveredFromWorkbench)).ToList(),
            apps.Count == 0 ? null : apps.Max(a => a.FetchedAt),
            history.Take(RecentHistoryCount).Select(ToHistory).ToList());
    }

    [McpServerTool(Name = "list_environment_history", ReadOnly = true)]
    [Description("Lists an environment's Workbench history, newest first: what was done to it from this workbench and by whom - update dates moved, updates started or booked for a later slot (and cancelled), apps updated or uploaded, sessions ended, copies made, deleted environments recovered - with each line's status ('Pending', 'Sent', 'Failed', 'Cancelled') and outcome. Work done in Business Central's admin centre, or by Microsoft, is not in it. At most the latest 50 lines.")]
    public async Task<IReadOnlyList<EnvironmentHistoryEntry>> ListEnvironmentHistoryAsync(
        [Description("Environment id (from list_environments or get_solution).")] int environmentId,
        CancellationToken ct = default)
    {
        var row = await _fleet.GetEnvironmentAsync(environmentId, ct) ?? throw EnvironmentNotFound(environmentId);
        var history = await Guard(environmentId, () => _upgradeActions.ListEnvironmentActivityAsync(row.Fleet.ProjectId, environmentId, ct));
        return history.Select(ToHistory).ToList();
    }

    [McpServerTool(Name = "list_upgrades", ReadOnly = true)]
    [Description("Lists the Upgrades fleet: for every live environment of every solution you can see, the platform update Business Central has waiting - target version, when it is scheduled, the latest date it can be moved to, and its status - grouped by solution with Production first, plus any update move booked from this workbench that has not run yet. canChangeDate says whether you hold the environment-updates permission for that solution. Requires the environment-updates permission on at least one team, as the Upgrades page does. The update facts are the workbench's mirror as of the nightly sweep or the last Refresh, not a live read; each row carries nextUpdateReadAt.")]
    public async Task<IReadOnlyList<UpgradeSolutionGroup>> ListUpgradesAsync(CancellationToken ct = default)
    {
        await RequireEnvironmentOpsAsync(
            "You need permission to manage environment updates to see the Upgrades list. An administrator can grant it on one of your teams. list_environments shows the next update of every environment you can see.",
            ct);

        var rows = await _fleet.ListFleetAsync(includeSoftDeleted: false, ct);
        var pending = (await _upgradeActions.ListPendingAsync(ct)).ToLookup(a => a.EnvironmentId);

        // ListFleetAsync already orders by solution, Production first, then name - the
        // page's order - and GroupBy keeps it.
        return rows
            .GroupBy(r => (r.ProjectId, r.ProjectName, r.TimeZone))
            .Select(g => new UpgradeSolutionGroup(
                g.Key.ProjectId,
                g.Key.ProjectName,
                g.Key.TimeZone ?? "UTC",
                g.Select(r => new UpgradeEnvironmentRow(
                    r.EnvironmentId,
                    r.EnvironmentName,
                    r.EnvironmentType,
                    r.Version,
                    ToNextUpdate(r),
                    r.CanAct,
                    r.CanPushDate,
                    pending[r.EnvironmentId]
                        .Select(a => new PendingUpgradeAction(a.Kind.ToString(), a.ExecuteAfter, a.RequestedByName))
                        .ToList(),
                    r.FetchedAt)).ToList()))
            .ToList();
    }

    // ── Planned upgrades (#984) ─────────────────────────────────────────

    [McpServerTool(Name = "list_planned_upgrades", ReadOnly = true)]
    [Description("Lists the planned upgrades: named waves of environments the upgrade team moves, starts and checks together (e.g. '28.5 in November 2026'). For each: its id, name, target Business Central version (Major.Minor), the planned slot if one was set, the note, who created it and when, who marked it done and when, its status, and how many of its environments are in each state. Status is 'Planned' (nothing has gone further than a moved date), 'InProgress' (something is booked or running, or the wave is part way through), 'Updated' (every environment has an answer and it waits for someone to mark it done) or 'Done' (marked done, in the archive). Open upgrades come first, soonest planned slot first; with includeArchived the done ones follow, most recently closed first. Only environments of solutions you can see are counted. Requires the environment-updates permission on at least one team, as the Upgrades page does. Use get_upgrade for one upgrade's environments.")]
    public async Task<IReadOnlyList<PlannedUpgradeSummary>> ListPlannedUpgradesAsync(
        [Description("Also list the upgrades marked done. Default false: open upgrades only.")] bool includeArchived = false,
        [Description("Optional text to match, case-insensitively, against the upgrade's name or target version, e.g. 'november' or '28.5'.")] string? search = null,
        CancellationToken ct = default)
    {
        await RequireEnvironmentOpsAsync(
            "You need permission to manage environment updates to see the planned upgrades. An administrator can grant it on one of your teams.",
            ct);

        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var open = (await _plannedUpgrades.ListOpenAsync(ct))
            .Where(u => term is null
                        || u.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || u.TargetVersion.Contains(term, StringComparison.OrdinalIgnoreCase));
        var archived = includeArchived
            ? await _plannedUpgrades.ListArchivedAsync(term, ct)
            : [];

        return open.Concat(archived).Select(ToPlannedSummary).ToList();
    }

    [McpServerTool(Name = "get_upgrade", ReadOnly = true)]
    [Description("Returns one planned upgrade in full: its name, target version, planned slot, note, who created it and who marked it done, its status and counts (as list_planned_upgrades describes them), and every environment on it. For each environment: the solution, the environment's name and type, its current version, the platform update Business Central has waiting (as list_upgrades shows it), its state, the last action taken on it from this upgrade, who is assigned to check it, and the after-upgrade check (who ticked it, when, and their note). The state is one word: 'Planned' (on the upgrade, nothing done from it yet), 'DateMoved' (its update date was moved out to the latest allowed, and that is the last thing done), 'Booked' (a start is booked for a later slot, or Business Central was told to start and has not yet), 'Running' (Business Central is updating it now), 'Updated' (it is on the target version or later), 'Failed' (the last action from this upgrade failed, or Business Central reports the update failed) or 'Checked' (someone did the after-upgrade check and ticked it). Environments of solutions you cannot see are left out. Facts about the environment are the workbench's mirror as of the nightly sweep or the last Refresh, not a live read; each carries environmentReadAt and nextUpdateReadAt. Requires the environment-updates permission on at least one team. Read-only: moving, starting and checking are done on the upgrade's page in the web UI.")]
    public async Task<PlannedUpgradeDetail> GetUpgradeAsync(
        [Description("The upgrade's numeric id, from list_planned_upgrades.")] int upgradeId,
        CancellationToken ct = default)
    {
        await RequireEnvironmentOpsAsync(
            "You need permission to manage environment updates to read a planned upgrade. An administrator can grant it on one of your teams.",
            ct);

        var detail = await _plannedUpgrades.GetAsync(upgradeId, ct)
            ?? throw new McpException(
                $"Upgrade {upgradeId} does not exist or is not visible to you. Call list_planned_upgrades to see the upgrades you can read.");

        var lines = detail.Lines.Select(l => new PlannedUpgradeLine(
                l.Environment.EnvironmentId,
                l.Environment.ProjectId,
                l.Environment.ProjectName,
                l.Environment.EnvironmentName,
                l.Environment.EnvironmentType,
                l.Environment.Version,
                ToNextUpdate(l.Environment),
                l.State.ToString(),
                l.LastAction is null ? null : ToHistory(l.LastAction),
                l.AssigneeName,
                l.CheckedAt,
                l.CheckedBy is { Length: > 0 } who ? UpgradeActionRow.NameOf(who) : null,
                l.Note,
                l.Environment.EnvironmentFetchedAt,
                l.Environment.FetchedAt))
            .ToList();
        return new PlannedUpgradeDetail(ToPlannedSummary(detail.Upgrade), lines);
    }

    /// <summary>The Upgrades page's gate: the environment-updates grant on at least one team.</summary>
    private async Task RequireEnvironmentOpsAsync(string refusal, CancellationToken ct)
    {
        var snapshot = await _access.GetSnapshotAsync(ct);
        if (!snapshot.CanUseEnvironmentOps) throw new McpException(refusal);
    }

    private static PlannedUpgradeSummary ToPlannedSummary(EnvironmentUpgradeSummary u) => new(
        u.Id,
        u.Name,
        u.TargetVersion,
        u.PlannedAt,
        u.Note,
        UpgradeActionRow.NameOf(u.CreatedBy),
        u.CreatedAt,
        u.ClosedBy is { Length: > 0 } who ? UpgradeActionRow.NameOf(who) : null,
        u.ClosedAt,
        u.Status.ToString(),
        u.LineCount,
        new UpgradeStateCounts(
            u.Count(UpgradeLineState.Planned),
            u.Count(UpgradeLineState.DateMoved),
            u.Count(UpgradeLineState.Booked),
            u.Count(UpgradeLineState.Running),
            u.Count(UpgradeLineState.Updated),
            u.Count(UpgradeLineState.Failed),
            u.Count(UpgradeLineState.Checked)));

    // ── Deliveries ──────────────────────────────────────────────────────

    [McpServerTool(Name = "list_recent_deployments", ReadOnly = true)]
    [Description("Lists deployments across every solution you can see, newest first: for each, the solution, the deployment pipeline, the target environment, the build, its status ('proposed'/'scheduled'/'claimed'/'uploading'/'installing'/'deployed'/'failed'/'cancelled'/'handed_off'/'dismissed': 'proposed' is a deployment the pipeline prepared from a new build that is waiting for a person to approve it in the web UI, with nothing sent yet; 'dismissed' is such a prepared deployment that a person dismissed or a newer build replaced before anyone approved it, so nothing was ever sent; 'handed_off' means Business Central accepted the apps and installs them in its own later window), when it was requested, started and finished, who triggered it, and - for a failed one - the failure message and each app's result. These are the workbench's own records of what it deployed. Use it for 'did the deployment go alright?' or 'what failed to deploy this week?'; list_deployments has one deployment pipeline's full history.")]
    public async Task<IReadOnlyList<RecentDelivery>> ListRecentDeliveriesAsync(
        [Description("Optional status to keep, e.g. 'failed' or 'deployed'.")] string? status = null,
        [Description("Optional date (yyyy-MM-dd, UTC): only deployments requested on or after it.")] string? since = null,
        [Description("How many to return, newest first. Default 50, at most 200.")] int limit = 50,
        CancellationToken ct = default)
    {
        var wanted = status?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(wanted) && !KnownDeliveryStatuses.Contains(wanted))
        {
            throw new McpException(
                $"'{status}' is not a deployment status. Use one of: {string.Join(", ", KnownDeliveryStatuses)}.");
        }

        var rows = await _deliveries.ListRecentAsync(wanted, ParseDate(since, "since"), limit, ct);
        return rows.Select(d => new RecentDelivery(
            d.Id, d.ProjectId, d.ProjectName, d.ReleasePipelineId, d.ReleasePipelineName, d.EnvironmentName,
            d.BuildId, d.Status, d.CreatedAt, d.ScheduledFor, d.StartedAt, d.FinishedAt, d.FailureMessage,
            d.TriggeredByName, d.Apps)).ToList();
    }

    // ── Customer information ────────────────────────────────────────────

    [McpServerTool(Name = "list_customer_contacts", ReadOnly = true)]
    [Description("Lists who to call or write to about a solution's customer, from its Customer tab: each contact's name, which side they are on ('Customer', 'HostingPartner', 'MicrosoftPartner' or 'Internal'), company, email address and phone number. Hand-kept by your colleagues, not read from Business Central. Contact details are personal data: use them to answer the question asked, and do not copy them anywhere else.")]
    public async Task<CustomerContacts> ListCustomerContactsAsync(
        [Description("Solution name or numeric id (from list_solutions).")] string solution,
        CancellationToken ct = default)
    {
        var (id, project) = await ResolveAsync(solution, ct);
        var contacts = await Guard(solution, () => _customerInfo.ListContactsAsync(id, ct));

        // The Customer tab records no view, so this is the floor issue #912 asked for: a
        // line naming who read which solution's contact details through an assistant.
        _logger.LogInformation(
            "User {UserId} read the {ContactCount} contact(s) of solution {ProjectId} through MCP.",
            _orgContext.CurrentUserId, contacts.Count, id);

        return new CustomerContacts(
            id,
            project.Name,
            contacts.Select(c => new ContactRow(c.Type.ToString(), c.Name, c.Company, c.Email, c.Phone)).ToList());
    }

    [McpServerTool(Name = "get_customer_access", ReadOnly = true)]
    [Description("Returns how to get into a solution's customer system, from its Customer tab: the 'getting in' notes (VPN, remote desktop, which account to ask for), the hosting notes, where Business Central runs and its web client address, and the other systems it is integrated with and in which direction. Hand-kept by your colleagues; the notes never hold passwords. The web client address is the production environment's as last read by the nightly sweep or Refresh when the solution is online and connected (addressReadAt), otherwise what was typed.")]
    public async Task<CustomerAccess> GetCustomerAccessAsync(
        [Description("Solution name or numeric id (from list_solutions).")] string solution,
        CancellationToken ct = default)
    {
        var (id, project) = await ResolveAsync(solution, ct);
        var all = await Guard(solution, () => _customerInfo.GetAllAsync(id, ct)) ?? throw NotFound(solution);

        return new CustomerAccess(
            id,
            project.Name,
            all.Basics.HostingType?.ToString(),
            all.Basics.IsOnPremises,
            all.Basics.EffectiveClientUrl,
            all.Basics.FetchedAt,
            all.Notes.AccessDescription,
            all.Notes.HostingNotes,
            all.Integrations.Select(i => new IntegrationRow(i.Name, i.Direction.ToString())).ToList());
    }

    [McpServerTool(Name = "list_customer_knowledge", ReadOnly = true)]
    [Description("Answers 'who here knows this customer?' or 'which customers does this colleague know?', from the Customer tab's list of colleagues who know a customer. Give exactly one of solution or person. With solution: each colleague's name, email, role ('Consultant', 'Developer', 'ProjectLeader', 'Architect') and the areas they know, plus the solution's free-text knowledge notes. With person (part of a name, or an exact email): every solution you can see that the matching colleague(s) are listed on, with their role and areas. Hand-kept by your colleagues.")]
    public async Task<CustomerKnowledge> ListCustomerKnowledgeAsync(
        [Description("Solution name or numeric id - to list who knows this customer.")] string? solution = null,
        [Description("Colleague's name (or part of it) or exact email - to list which customers they know.")] string? person = null,
        CancellationToken ct = default)
    {
        var bySolution = !string.IsNullOrWhiteSpace(solution);
        var byPerson = !string.IsNullOrWhiteSpace(person);
        if (bySolution == byPerson)
        {
            throw new McpException("Give exactly one of solution (who knows this customer) or person (which customers this colleague knows).");
        }

        if (byPerson)
        {
            var known = await _customerInfo.ListCustomersKnownByAsync(person!, ct);
            return new CustomerKnowledge(
                known.Select(k => new KnowledgeRow(k.ProjectId, k.ProjectName, k.Name, k.Email, k.Role.ToString(), k.Areas)).ToList(),
                null);
        }

        var (id, project) = await ResolveAsync(solution!, ct);
        var people = await Guard(solution!, () => _customerInfo.ListPeopleAsync(id, ct));
        var notes = await Guard(solution!, () => _customerInfo.GetNotesAsync(id, ct));
        return new CustomerKnowledge(
            people.Select(p => new KnowledgeRow(id, project.Name, p.Name, p.Email, p.Role.ToString(), p.Areas)).ToList(),
            notes?.KnowledgeNotes);
    }

    [McpServerTool(Name = "list_customer_modules", ReadOnly = true)]
    [Description("Answers 'which customers have module X?' and 'which modules does this customer have?' from your organisation's catalogue of third-party modules. With no arguments: the catalogue (name, publisher, app id). With module: every solution you can see that has it, and at which version. With solution: that solution's modules and versions. Give at most one of module or solution. For an online solution the version is what its production environment last reported installed (source 'business_central', read at readAt by the nightly sweep or a Refresh - not a live read); for an on-premises one it is what somebody typed (source 'typed').")]
    public async Task<IReadOnlyList<CustomerModuleRow>> ListCustomerModulesAsync(
        [Description("Optional module name or numeric id from the catalogue - to list which solutions have it.")] string? module = null,
        [Description("Optional solution name or numeric id - to list its modules.")] string? solution = null,
        CancellationToken ct = default)
    {
        var byModule = !string.IsNullOrWhiteSpace(module);
        var bySolution = !string.IsNullOrWhiteSpace(solution);
        if (byModule && bySolution)
        {
            throw new McpException("Give module or solution, not both.");
        }

        var catalog = await _modules.ListCatalogAsync(ct);
        if (bySolution)
        {
            var (id, project) = await ResolveAsync(solution!, ct);
            var held = await Guard(solution!, () => _modules.GetSolutionModulesAsync(id, ct));
            return held.Modules.Select(m => ToModuleRow(m, id, project.Name, held)).ToList();
        }

        if (!byModule)
        {
            // The count the admin page shows is left out on purpose: it counts every
            // solution, including Private ones this caller cannot see.
            return catalog
                .Select(m => new CustomerModuleRow(m.Id, m.Name, m.Publisher, m.AppId, null, null, null, null, null, null, null))
                .ToList();
        }

        var wanted = module!.Trim();
        var entry = (int.TryParse(wanted, out var moduleId)
                ? catalog.FirstOrDefault(m => m.Id == moduleId)
                : catalog.FirstOrDefault(m => string.Equals(m.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            ?? throw new McpException($"Module '{module}' is not in the catalogue. Call list_customer_modules with no arguments to see it.");

        var holders = await _modules.ListProjectIdsWithModuleAsync(entry.Id, ct);
        var visible = await _projects.ListSolutionOptionsAsync(ct: ct);
        var rows = new List<CustomerModuleRow>();
        foreach (var option in visible.Where(o => holders.Contains(o.Id)))
        {
            var held = await _modules.GetSolutionModulesAsync(option.Id, ct);
            var match = held.Modules.FirstOrDefault(m => m.ModuleId == entry.Id);
            rows.Add(match is not null
                ? ToModuleRow(match, option.Id, option.Name, held)
                : new CustomerModuleRow(entry.Id, entry.Name, entry.Publisher, entry.AppId, option.Id, option.Name,
                    null, held.ReadFromEnvironment ? "business_central" : "typed", held.EnvironmentName, held.FetchedAt, null));
        }
        return rows;
    }

    // ── Shaping ─────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<string> KnownDeliveryStatuses =
    [
        ProjectDeliveryStatus.Scheduled, ProjectDeliveryStatus.Claimed, ProjectDeliveryStatus.Uploading,
        ProjectDeliveryStatus.Installing, ProjectDeliveryStatus.Deployed, ProjectDeliveryStatus.Failed,
        ProjectDeliveryStatus.Cancelled, ProjectDeliveryStatus.HandedOff, ProjectDeliveryStatus.Proposed,
        ProjectDeliveryStatus.Dismissed,
    ];

    private static EnvironmentSummary ToSummary(EnvironmentDetailRow row)
    {
        var r = row.Fleet;
        return new EnvironmentSummary(
            r.ProjectId,
            r.ProjectName,
            r.EnvironmentId,
            r.EnvironmentName,
            r.EnvironmentType,
            r.Status,
            r.Version,
            r.IsSoftDeleted,
            r.SoftDeletedOn,
            r.HardDeletePendingOn,
            Mb(r.DatabaseKb),
            Mb(r.TenantUsedKb),
            Mb(r.TenantQuotaKb),
            r.TenantStorageUse is { } use ? Math.Round(use, 3) : null,
            row.BcUpdateWindowStart is { } s && row.BcUpdateWindowEnd is { } e
                ? new TimeWindow(Clock(s), Clock(e), row.BcUpdateWindowTimeZoneIana ?? r.TimeZone ?? "UTC")
                : null,
            ToNextUpdate(r),
            r.EnvironmentFetchedAt,
            r.FetchedAt,
            row.BcUpdateWindowFetchedAt,
            r.BusinessCentralUrl);
    }

    private static NextPlatformUpdate? ToNextUpdate(UpgradeFleetRow r) => r.HasUpdate
        ? new NextPlatformUpdate(r.NextUpdateVersion!, r.NextUpdateType, r.NextUpdateStatus, r.NextUpdateDate,
            r.EffectiveLatestDate, r.NextUpdateIgnoresWindow)
        : null;

    private static EnvironmentHistoryEntry ToHistory(UpgradeActionRow a) => new(
        a.Id, a.Kind.ToString(), a.Status.ToString(), a.IsBooking, a.RequestedByName, a.RequestedAt,
        a.ExecuteAfter, a.SentAt, a.Outcome, a.CancelledByName, a.CancelledAt);

    private static CustomerModuleRow ToModuleRow(SolutionModule m, int projectId, string projectName, SolutionModules held) => new(
        m.ModuleId, m.Name, m.Publisher, null, projectId, projectName, m.Version,
        held.ReadFromEnvironment ? "business_central" : "typed",
        held.ReadFromEnvironment ? held.EnvironmentName : null,
        held.ReadFromEnvironment ? held.FetchedAt : null,
        m.Note);

    private static double? Mb(long? kb) => kb is { } k ? Math.Round(k / 1024d, 1) : null;

    private static string Clock(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static DateTime? ParseDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }
        throw new McpException($"{name} must be a date like 2026-10-01.");
    }

    // ── Resolution and refusals ─────────────────────────────────────────

    /// <summary>
    /// A solution by name or id, through <see cref="ProjectService.ResolveProjectAsync"/> -
    /// the visibility fence's MCP choke point - and then its row for the name.
    /// </summary>
    private async Task<(int Id, OeProject Project)> ResolveAsync(string solution, CancellationToken ct)
    {
        var id = await _projects.ResolveProjectAsync(solution, ct);
        var project = await Guard(solution, () => _projects.GetProjectAsync(id, ct)) ?? throw NotFound(solution);
        return (id, project);
    }

    /// <summary>
    /// Runs a gated service read and answers a refusal exactly as an unknown solution:
    /// a Private solution is absent, not locked. Resolution has already applied the same
    /// fence, so this only matters if the solution's access changes between the two reads.
    /// </summary>
    private static async Task<T> Guard<T>(object what, Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (ProjectAccessDeniedException)
        {
            throw what is int environmentId ? EnvironmentNotFound(environmentId) : NotFound(what.ToString() ?? string.Empty);
        }
    }

    private static McpException NotFound(string solution) =>
        new($"Solution '{solution}' was not found. Call list_solutions to see available solutions.");

    private static McpException EnvironmentNotFound(int environmentId) =>
        new($"Environment {environmentId} was not found. Call list_environments to see the environments you can read.");
}

// ── DTOs (agent-facing: "solution", never "project") ────────────────────

/// <summary>One solution's basics, for <c>get_solution</c>.</summary>
/// <param name="Hosting">'MicrosoftCloud', 'OurCloud', 'HostingPartner', 'CustomerHardware', or null when nobody has said (treated as online).</param>
/// <param name="VersionFromBusinessCentral">True when <paramref name="Version"/> and <paramref name="WebClientUrl"/> are what the production environment last reported rather than what was typed.</param>
/// <param name="VersionReadAt">When Business Central last reported them; null when typed.</param>
public sealed record SolutionDetail(
    int SolutionId,
    string SolutionName,
    string? ShortName,
    string Visibility,
    string? Hosting,
    bool OnPremises,
    string? Version,
    string? WebClientUrl,
    bool VersionFromBusinessCentral,
    DateTime? VersionReadAt,
    string? LicenseType,
    string? UserExperience,
    string? VoiceAccountNumber,
    Guid? TenantId,
    BusinessCentralConnection? Connection,
    IReadOnlyList<SolutionEnvironmentBrief> Environments);

/// <summary>Whether the solution's Business Central online connection is set up - never the secret.</summary>
public sealed record BusinessCentralConnection(
    bool Configured, DateTime? VerifiedAt, DateTime? SecretExpiresAt, bool UsesOrganizationRegistration);

/// <summary>One environment of a solution, in a line.</summary>
public sealed record SolutionEnvironmentBrief(
    int EnvironmentId, string Name, string Type, string? Status, string? Version, bool Deleted, DateTime? ReadAt);

/// <summary>A daily window, as clock times in <paramref name="TimeZone"/> (IANA).</summary>
public sealed record TimeWindow(string Start, string End, string TimeZone);

/// <summary>The platform update Business Central has waiting for an environment.</summary>
/// <param name="LatestDate">The last date the update can still be moved to.</param>
/// <param name="IgnoresWindow">True when it is set to run regardless of Microsoft's update window.</param>
public sealed record NextPlatformUpdate(
    string Version, string? Type, string? Status, DateTime? Date, DateTime? LatestDate, bool? IgnoresWindow);

/// <summary>One environment in <c>list_environments</c>.</summary>
/// <param name="TenantStorageUse">What the customer's tenant uses as a fraction of its quota; above 1 is over it.</param>
/// <param name="UpdateWindow">Microsoft's platform-update window.</param>
/// <param name="EnvironmentReadAt">When the environment's status, type and version were last read.</param>
/// <param name="NextUpdateReadAt">When the next-update facts were last read.</param>
/// <param name="UpdateWindowReadAt">When the update window was last read.</param>
public sealed record EnvironmentSummary(
    int SolutionId,
    string SolutionName,
    int EnvironmentId,
    string Name,
    string Type,
    string? Status,
    string? Version,
    bool Deleted,
    DateTime? DeletedOn,
    DateTime? HardDeletePendingOn,
    double? DatabaseMb,
    double? TenantStorageUsedMb,
    double? TenantStorageQuotaMb,
    double? TenantStorageUse,
    TimeWindow? UpdateWindow,
    NextPlatformUpdate? NextUpdate,
    DateTime? EnvironmentReadAt,
    DateTime? NextUpdateReadAt,
    DateTime? UpdateWindowReadAt,
    string? BusinessCentralUrl);

/// <summary>One environment in full, for <c>get_environment</c>.</summary>
/// <param name="DeliveryWindow">The daily window our own deliveries prefer, in the solution's time zone; null for "any time".</param>
/// <param name="InstalledAppsReadAt">When the installed apps were last read; null when never.</param>
public sealed record EnvironmentDetail(
    EnvironmentSummary Environment,
    string? CountryCode,
    string? AzureRegion,
    TimeWindow? DeliveryWindow,
    string? AppSourceAppsUpdateCadence,
    IReadOnlyList<InstalledApp> InstalledApps,
    DateTime? InstalledAppsReadAt,
    IReadOnlyList<EnvironmentHistoryEntry> RecentHistory);

/// <summary>An app Business Central last reported installed.</summary>
public sealed record InstalledApp(Guid AppId, string Name, string Publisher, string Version, bool DeliveredFromWorkbench);

/// <summary>One line of an environment's Workbench history.</summary>
/// <param name="Action">'PushDateToLatest', 'RunNow', 'UpdateApp', 'UploadApp', 'RecoverEnvironment', 'CopyEnvironment' or 'CancelSession'.</param>
/// <param name="IsBooking">True when it was booked for a later slot (ExecuteAfter) rather than done there and then.</param>
public sealed record EnvironmentHistoryEntry(
    int Id,
    string Action,
    string Status,
    bool IsBooking,
    string RequestedBy,
    DateTime RequestedAt,
    DateTime ExecuteAfter,
    DateTime? SentAt,
    string? Outcome,
    string? CancelledBy,
    DateTime? CancelledAt);

/// <summary>One solution's environments on the Upgrades list.</summary>
public sealed record UpgradeSolutionGroup(
    int SolutionId, string SolutionName, string TimeZone, IReadOnlyList<UpgradeEnvironmentRow> Environments);

/// <summary>One environment on the Upgrades list.</summary>
/// <param name="CanChangeDate">True when you hold the environment-updates permission for this solution.</param>
/// <param name="CanMoveDateLater">True when the update's date can still be moved further out.</param>
public sealed record UpgradeEnvironmentRow(
    int EnvironmentId,
    string Name,
    string Type,
    string? CurrentVersion,
    NextPlatformUpdate? NextUpdate,
    bool CanChangeDate,
    bool CanMoveDateLater,
    IReadOnlyList<PendingUpgradeAction> Booked,
    DateTime? NextUpdateReadAt);

/// <summary>An update move booked from this workbench that has not run yet.</summary>
public sealed record PendingUpgradeAction(string Action, DateTime RunsAt, string RequestedBy);

/// <summary>A planned upgrade's header, for <c>list_planned_upgrades</c> and <c>get_upgrade</c>.</summary>
/// <param name="TargetVersion">The Business Central release the wave goes to, as Major.Minor.</param>
/// <param name="PlannedAt">The slot the team has in mind; advisory, nothing fires from it.</param>
/// <param name="Status">'Planned', 'InProgress', 'Updated' or 'Done'.</param>
/// <param name="EnvironmentCount">How many of its environments you can see.</param>
public sealed record PlannedUpgradeSummary(
    int UpgradeId,
    string Name,
    string TargetVersion,
    DateTime? PlannedAt,
    string? Note,
    string CreatedBy,
    DateTime CreatedAt,
    string? ClosedBy,
    DateTime? ClosedAt,
    string Status,
    int EnvironmentCount,
    UpgradeStateCounts Counts);

/// <summary>How many of an upgrade's visible environments are in each state.</summary>
public sealed record UpgradeStateCounts(
    int Planned, int DateMoved, int Booked, int Running, int Updated, int Failed, int Checked);

/// <summary>One planned upgrade with its environments, for <c>get_upgrade</c>.</summary>
public sealed record PlannedUpgradeDetail(PlannedUpgradeSummary Upgrade, IReadOnlyList<PlannedUpgradeLine> Environments);

/// <summary>One environment on a planned upgrade.</summary>
/// <param name="State">'Planned', 'DateMoved', 'Booked', 'Running', 'Updated', 'Failed' or 'Checked'.</param>
/// <param name="LastAction">The last thing done to it from this upgrade; null when nothing has been.</param>
/// <param name="AssignedTo">Who is to do the after-upgrade check; null when nobody is assigned.</param>
/// <param name="CheckNote">The note left with the check; kept when the check is unticked.</param>
/// <param name="EnvironmentReadAt">When the environment's version was last read.</param>
/// <param name="NextUpdateReadAt">When the next-update facts were last read.</param>
public sealed record PlannedUpgradeLine(
    int EnvironmentId,
    int SolutionId,
    string SolutionName,
    string EnvironmentName,
    string EnvironmentType,
    string? CurrentVersion,
    NextPlatformUpdate? NextUpdate,
    string State,
    EnvironmentHistoryEntry? LastAction,
    string? AssignedTo,
    DateTime? CheckedAt,
    string? CheckedBy,
    string? CheckNote,
    DateTime? EnvironmentReadAt,
    DateTime? NextUpdateReadAt);

/// <summary>
/// One deployment in <c>list_recent_deployments</c>. MCP-only, so its member names are the
/// agent-facing ones (Solution, Deployment), not the entity's (Project, Delivery).
/// </summary>
/// <param name="Apps">Each app's result; filled only for a failed deployment.</param>
public sealed record RecentDelivery(
    int DeploymentId,
    int SolutionId,
    string SolutionName,
    int DeploymentPipelineId,
    string DeploymentPipelineName,
    string EnvironmentName,
    int BuildId,
    string Status,
    DateTime RequestedAt,
    DateTime ScheduledFor,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? TriggeredBy,
    IReadOnlyList<DeliveryAppRow> Apps);

/// <summary>A solution's contacts, for <c>list_customer_contacts</c>.</summary>
public sealed record CustomerContacts(int SolutionId, string SolutionName, IReadOnlyList<ContactRow> Contacts);

/// <summary>One contact.</summary>
public sealed record ContactRow(string Side, string Name, string? Company, string? Email, string? Phone);

/// <summary>How to get into a customer's system, for <c>get_customer_access</c>.</summary>
/// <param name="AddressReadAt">When Business Central last reported the web client address; null when it was typed.</param>
public sealed record CustomerAccess(
    int SolutionId,
    string SolutionName,
    string? Hosting,
    bool OnPremises,
    string? WebClientUrl,
    DateTime? AddressReadAt,
    string? GettingIn,
    string? HostingNotes,
    IReadOnlyList<IntegrationRow> Integrations);

/// <summary>Another system the customer's Business Central talks to; <paramref name="Direction"/> is 'Inbound', 'Outbound' or 'Both', seen from Business Central.</summary>
public sealed record IntegrationRow(string Name, string Direction);

/// <summary>The answer to <c>list_customer_knowledge</c>.</summary>
/// <param name="Notes">The solution's free-text knowledge notes; null when listing by person.</param>
public sealed record CustomerKnowledge(IReadOnlyList<KnowledgeRow> People, string? Notes);

/// <summary>A colleague who knows a customer, and what about.</summary>
public sealed record KnowledgeRow(
    int SolutionId, string SolutionName, string PersonName, string PersonEmail, string Role, string? Areas);

/// <summary>
/// A catalogue module, and - when the question named a module or a solution - one
/// solution that has it. The solution fields are null for a plain catalogue row.
/// </summary>
/// <param name="Source">'business_central' when read from the production environment, 'typed' when recorded by hand.</param>
/// <param name="ReadAt">When Business Central last reported it; null when typed.</param>
public sealed record CustomerModuleRow(
    int ModuleId,
    string Module,
    string? Publisher,
    Guid? AppId,
    int? SolutionId,
    string? SolutionName,
    string? Version,
    string? Source,
    string? EnvironmentName,
    DateTime? ReadAt,
    string? Note);
