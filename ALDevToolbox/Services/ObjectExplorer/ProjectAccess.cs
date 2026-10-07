using System.Linq.Expressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// The single source of truth for project authorization, on several axes. See
/// <c>.design/teams-and-visibility.md</c> and <c>.design/artifacts.md</c>.
///
/// <list type="bullet">
///   <item><b>View</b> (<see cref="CanViewAsync"/>) — everyone in the org, unless
///   the project is <see cref="ProjectVisibility.Private"/>, in which case only its
///   owner, a member of an assigned team, an org Admin, or a SiteAdmin.</item>
///   <item><b>Manage</b> (<see cref="CanManageAsync"/>) — adding and removing
///   repositories, editing settings, triggering builds and deliveries: everyone in
///   the org on a <see cref="ProjectVisibility.Public"/> project, and otherwise the
///   owner, an org Admin, a SiteAdmin, or a member of a team assigned to it.</item>
///   <item><b>Delete</b> (<see cref="EnsureCanDeleteAsync"/>) — deliberately
///   stricter than manage: owner, org Admin, SiteAdmin only. A team grant is about
///   doing the work, not about ending it.</item>
///   <item><b>Environment updates</b>
///   (<see cref="CanManageEnvironmentUpdatesAsync"/>) — scheduling Business Central
///   platform updates on the customer's tenant: an org Admin, a SiteAdmin, or a
///   member of an assigned team who holds <c>ManagesUpdates</c>. Independent of
///   manage in both directions.</item>
/// </list>
///
/// <para>Shared by every project-scoped service (<see cref="ProjectService"/>,
/// <see cref="PipelineService"/>, <see cref="ArtifactService"/>,
/// <see cref="DeliveryService"/>, <see cref="ProjectBuildImporter"/> and friends)
/// so the rules live in one place. List queries use
/// <see cref="VisibleProjectPredicate"/> rather than a per-row check.</para>
/// </summary>
public sealed class ProjectAccess
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    /// <summary>
    /// The caller's identity and team memberships, resolved once per DI scope. In a
    /// Blazor circuit that scope is the circuit, so a membership change made while a
    /// page is open is not picked up until the page reloads. That staleness is
    /// accepted deliberately: it matches the consistency model of the circuit-scoped
    /// <see cref="AppDbContext"/> the same page reads through, and the alternative
    /// (a database round-trip per authorization check) buys freshness nobody asked
    /// for. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private AccessSnapshot? _snapshot;

    public ProjectAccess(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    /// <summary>
    /// Who the caller is, for the purposes of project access: their user id (null
    /// for a background worker running under an ambient org scope), whether they
    /// bypass visibility, and the teams they are on.
    /// </summary>
    /// <param name="UserId">Null when nobody is signed in — grants nothing, never throws.</param>
    /// <param name="TeamIds">The teams this user is on, in the acting org.</param>
    /// <param name="UpdateOpsTeamIds">
    /// The subset of <paramref name="TeamIds"/> where this user's membership carries
    /// <see cref="TeamMember.ManagesUpdates"/> — the teams through which they may run
    /// Business Central platform-update actions.
    /// </param>
    public sealed record AccessSnapshot(
        int? UserId,
        bool IsSiteAdmin,
        bool IsOrgAdmin,
        IReadOnlySet<int> TeamIds,
        IReadOnlySet<int> UpdateOpsTeamIds)
    {
        /// <summary>True when the caller sees every project regardless of its visibility.</summary>
        public bool BypassesVisibility => IsSiteAdmin || IsOrgAdmin;

        /// <summary>
        /// True when the caller may run environment-update actions on <em>something</em> —
        /// what gates the Upgrades page and its sidebar entry. Says nothing about any
        /// particular project; <see cref="CanManageEnvironmentUpdatesAsync"/> does that.
        /// </summary>
        public bool CanUseEnvironmentOps => IsSiteAdmin || IsOrgAdmin || UpdateOpsTeamIds.Count > 0;
    }

    /// <summary>
    /// The caller's snapshot, loaded on first use and cached for the DI scope.
    /// Public so list-query callers can pass it straight to
    /// <see cref="VisibleProjectPredicate"/> without a second lookup.
    /// </summary>
    public async Task<AccessSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_snapshot is not null) return _snapshot;

        var userId = _orgContext.CurrentUserId;
        if (userId is null)
        {
            // No signed-in user: a background worker under an ambient org scope, or
            // a pre-auth render. No grants, and nothing to look up.
            return _snapshot = new AccessSnapshot(
                null, _orgContext.IsSiteAdmin, false, new HashSet<int>(), new HashSet<int>());
        }

        var isOrgAdmin = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == UserRole.Admin;

        // Both sets come from the one membership read — the update-ops teams are a
        // subset of the same rows, not a second round-trip.
        var memberships = await _db.TeamMembers.AsNoTracking()
            .Where(m => m.UserId == userId.Value)
            .Select(m => new { m.TeamId, m.ManagesUpdates })
            .ToListAsync(ct).ConfigureAwait(false);

        return _snapshot = new AccessSnapshot(
            userId,
            _orgContext.IsSiteAdmin,
            isOrgAdmin,
            memberships.Select(m => m.TeamId).ToHashSet(),
            memberships.Where(m => m.ManagesUpdates).Select(m => m.TeamId).ToHashSet());
    }

    // ── Manage axis ─────────────────────────────────────────────────────

    /// <summary>
    /// True when the current user may manage project <paramref name="projectId"/>,
    /// owned by <paramref name="createdByUserId"/>: they're a SiteAdmin, they own it,
    /// they're an org Admin, the project is <see cref="ProjectVisibility.Public"/>, or
    /// they're on a team assigned to it. Legacy ownerless projects (null owner) are
    /// manageable by Admin/SiteAdmin and assigned teams.
    ///
    /// <para><b>Public means everyone in the organisation manages it.</b> The three
    /// levels are one ladder — Public is open both ways, Read-only narrows writing to
    /// the assigned teams, Private narrows reading to them as well — so a level that
    /// says "anyone may see this" is not also the level that reserves every write to
    /// two people. Delete is the carve-out and stays with the owner and the admins
    /// (<see cref="CanDeleteAsync"/>), as does the environment-updates axis
    /// (<see cref="CanManageEnvironmentUpdatesAsync"/>), which is a team grant and
    /// never follows from this one. See <c>.design/teams-and-visibility.md</c>.</para>
    /// </summary>
    public async Task<bool> CanManageAsync(int projectId, int? createdByUserId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSiteAdmin) return true;
        if (snapshot.UserId is null) return false;
        if (createdByUserId is not null && createdByUserId == snapshot.UserId) return true;
        if (snapshot.IsOrgAdmin) return true;

        // A project that isn't in this org reads as null and grants nothing: the
        // org query filter has already decided it doesn't exist for this caller.
        var row = await GetProjectRowAsync(projectId, ct).ConfigureAwait(false);
        if (row is { Visibility: ProjectVisibility.Public }) return true;

        return await IsOnAnAssignedTeamAsync(projectId, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not manage project <paramref name="projectId"/>.
    /// </summary>
    public async Task EnsureCanManageAsync(int projectId, int? createdByUserId, CancellationToken ct = default)
    {
        if (!await CanManageAsync(projectId, createdByUserId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException();
        }
    }

    /// <summary>
    /// The list-query form of <see cref="CanManageAsync"/>: which projects
    /// <paramref name="snapshot"/> may manage. For a list that offers a manage-gated
    /// action per row and would otherwise have to ask once per row.
    ///
    /// <para>Compose it <em>alongside</em> <see cref="VisibleProjectPredicate"/>, not
    /// instead of it: this axis answers "may act", never "may see". Keep it in step with
    /// <see cref="CanManageAsync"/> — the service re-checks there on every write, and
    /// this only decides what is offered.</para>
    /// </summary>
    public static Expression<Func<OeProject, bool>> ManageProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.IsSiteAdmin || snapshot.IsOrgAdmin) return _ => true;
        if (snapshot.UserId is null) return _ => false;

        // Written out longhand, like the other predicates in this file: EF has to
        // translate the whole tree to SQL and an invoked expression variable doesn't
        // survive that trip.
        var userId = snapshot.UserId.Value;
        var teamIds = snapshot.TeamIds.ToList();
        return p => p.Visibility == ProjectVisibility.Public
                    || p.CreatedByUserId == userId
                    || p.Teams.Any(t => teamIds.Contains(t.TeamId));
    }

    // ── Environment-update axis (a different axis from manage) ──────────

    /// <summary>
    /// True when the current user may run Business Central platform-update actions on
    /// project <paramref name="projectId"/>: a SiteAdmin, an org Admin, or a member of
    /// a team assigned to the project whose membership carries
    /// <see cref="TeamMember.ManagesUpdates"/>.
    ///
    /// <para>Deliberately <em>not</em> the same axis as <see cref="CanManageAsync"/>:
    /// managing a project (settings, builds, deliveries) does not grant the update
    /// flag, and holding the flag does not make somebody a project manager. Owning the
    /// project doesn't grant it either — pushing an update date acts on the customer's
    /// production tenant, which is a narrower thing to hand out than the project. A
    /// Public project with no teams is therefore admin-only for these actions, by
    /// construction. See <c>.design/teams-and-visibility.md</c> and issue #657.</para>
    /// </summary>
    public async Task<bool> CanManageEnvironmentUpdatesAsync(int projectId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSiteAdmin) return true;
        if (snapshot.UserId is null) return false;
        if (snapshot.IsOrgAdmin) return true;
        if (snapshot.UpdateOpsTeamIds.Count == 0) return false;

        var teamIds = snapshot.UpdateOpsTeamIds.ToList();
        return await _db.OeProjectTeams.AsNoTracking()
            .AnyAsync(t => t.ProjectId == projectId && teamIds.Contains(t.TeamId), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may not
    /// run environment-update actions on project <paramref name="projectId"/>.
    /// </summary>
    public async Task EnsureCanManageEnvironmentUpdatesAsync(int projectId, CancellationToken ct = default)
    {
        if (!await CanManageEnvironmentUpdatesAsync(projectId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "You need permission to manage environment updates for this project's team.");
        }
    }

    /// <summary>
    /// The list-query form of <see cref="CanManageEnvironmentUpdatesAsync"/>: which
    /// projects <paramref name="snapshot"/> may run update actions on. Returns
    /// "everything" for a snapshot that bypasses visibility, so the caller doesn't have
    /// to special-case it, and "nothing" for a user holding the flag nowhere.
    ///
    /// <para>Compose it <em>alongside</em> <see cref="VisibleProjectPredicate"/>, not
    /// instead of it: this axis answers "may act", never "may see".</para>
    /// </summary>
    public static Expression<Func<OeProject, bool>> UpdateOpsProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.IsSiteAdmin || snapshot.IsOrgAdmin) return _ => true;
        if (snapshot.UserId is null || snapshot.UpdateOpsTeamIds.Count == 0) return _ => false;

        // Written out longhand, like the other predicates in this file: EF has to
        // translate the whole tree to SQL and an invoked expression variable doesn't
        // survive that trip. Keep this in step with CanManageEnvironmentUpdatesAsync.
        var updateOpsTeamIds = snapshot.UpdateOpsTeamIds.ToList();
        return p => p.Teams.Any(t => updateOpsTeamIds.Contains(t.TeamId));
    }

    /// <summary>
    /// True for an org Admin or a SiteAdmin. For the settings every solution in the
    /// organisation shares, where owning or managing one solution is not enough.
    /// </summary>
    public async Task<bool> IsOrganizationAdminAsync(CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.IsSiteAdmin || (snapshot.UserId is not null && snapshot.IsOrgAdmin);
    }

    // ── Delete axis (stricter than manage — no team grant) ──────────────

    /// <summary>
    /// True when the current user may <em>delete</em> the project owned by
    /// <paramref name="createdByUserId"/> — the owner, an org Admin, or a SiteAdmin.
    /// Being on an assigned team is deliberately not enough. Takes no project id
    /// because team assignment doesn't enter into it.
    /// </summary>
    public async Task<bool> CanDeleteAsync(int? createdByUserId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.IsSiteAdmin) return true;
        if (snapshot.UserId is null) return false;
        if (createdByUserId is not null && createdByUserId == snapshot.UserId) return true;
        return snapshot.IsOrgAdmin;
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not delete the project owned by <paramref name="createdByUserId"/>.
    /// </summary>
    public async Task EnsureCanDeleteAsync(int? createdByUserId, CancellationToken ct = default)
    {
        if (!await CanDeleteAsync(createdByUserId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "Only the project's owner or an organisation admin can delete it.");
        }
    }

    /// <summary>
    /// True when the current user may delete a build or deployment pipeline: an org
    /// Admin or a SiteAdmin. Stricter than deleting the solution itself, because a
    /// deleted deployment pipeline takes its waiting deployments with it; everyone who
    /// may manage the solution can disable a pipeline instead (#1131).
    /// </summary>
    public Task<bool> CanDeletePipelinesAsync(CancellationToken ct = default) => IsOrganizationAdminAsync(ct);

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> unless the current user may
    /// delete pipelines (<see cref="CanDeletePipelinesAsync"/>).
    /// </summary>
    public async Task EnsureCanDeletePipelinesAsync(CancellationToken ct = default)
    {
        if (!await CanDeletePipelinesAsync(ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "Only an admin can delete a pipeline. Disable it instead to stop it running.");
        }
    }

    // ── View axis ───────────────────────────────────────────────────────

    /// <summary>
    /// True when the current user may see project <paramref name="projectId"/> at
    /// all. False only for a <see cref="ProjectVisibility.Private"/> project the
    /// caller neither owns, nor has a team on, nor administers. A project that
    /// doesn't exist in this org reads as visible — the caller's read will come back
    /// empty on its own, and answering "denied" here would confirm it exists
    /// somewhere else.
    /// </summary>
    public async Task<bool> CanViewAsync(int projectId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.BypassesVisibility) return true;

        var row = await GetProjectRowAsync(projectId, ct).ConfigureAwait(false);
        if (row is null) return true;
        if (row.Visibility != ProjectVisibility.Private) return true;

        if (snapshot.UserId is null) return false;
        if (row.CreatedByUserId is not null && row.CreatedByUserId == snapshot.UserId) return true;
        return await IsOnAnAssignedTeamAsync(projectId, snapshot, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Which of <paramref name="userIds"/> may see project <paramref name="projectId"/>:
    /// <see cref="CanViewAsync"/> asked for each of them rather than the signed-in user,
    /// so a notifier can leave out someone taken off a Private solution's team. Everyone
    /// for a project that isn't Private, or isn't in this org (the caller's own reads
    /// come back empty for it). Reads no signed-in user, so a background job can call it
    /// inside the organisation's scope.
    /// </summary>
    public static async Task<List<int>> WhoCanViewAsync(
        AppDbContext db, int projectId, IReadOnlyCollection<int> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0) return [];
        var project = await db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Visibility, p.CreatedByUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (project is null || project.Visibility != ProjectVisibility.Private) return userIds.Distinct().ToList();

        // The owner, an org Admin, a SiteAdmin, or a member of one of its teams.
        var members = db.OeProjectTeams
            .Where(t => t.ProjectId == projectId)
            .SelectMany(t => t.Team!.Members.Select(m => m.UserId));
        return await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)
                        && (u.Id == project.CreatedByUserId
                            || u.Role == UserRole.Admin
                            || u.IsSiteAdmin
                            || members.Contains(u.Id)))
            .Select(u => u.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not see project <paramref name="projectId"/>. Callers that render a page
    /// should map this onto their not-found state — a Private project must not be
    /// confirmed to exist beyond the locked name the projects list already shows.
    /// </summary>
    public async Task EnsureCanViewAsync(int projectId, CancellationToken ct = default)
    {
        if (!await CanViewAsync(projectId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "This project is private. Ask its owner or a member of its team for access.");
        }
    }

    /// <summary>
    /// The list-query form of <see cref="CanViewAsync"/>: which projects
    /// <paramref name="snapshot"/> may see. Compose it into a query over
    /// <c>Project</c> instead of checking row by row. Returns "everything" when the
    /// snapshot bypasses visibility, so the caller doesn't have to special-case it.
    /// </summary>
    public static Expression<Func<OeProject, bool>> VisibleProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BypassesVisibility) return _ => true;

        var userId = snapshot.UserId;
        var teamIds = snapshot.TeamIds.ToList();
        return p => p.Visibility != ProjectVisibility.Private
                    || (userId != null && p.CreatedByUserId == userId)
                    || p.Teams.Any(t => teamIds.Contains(t.TeamId));
    }

    /// <summary>
    /// The projects user <paramref name="userId"/> can see, for a query that runs
    /// without that user's <see cref="AccessSnapshot"/> (a short-lived context of its
    /// own). Same rule as <see cref="WhoCanViewAsync"/>: anything not Private, or
    /// owned by them, or assigned to a team they are on. An org Admin or SiteAdmin
    /// sees everything; the caller checks that separately, as the snapshot does.
    /// </summary>
    public static Expression<Func<OeProject, bool>> VisibleToUserPredicate(int userId) =>
        p => p.Visibility != ProjectVisibility.Private
             || p.CreatedByUserId == userId
             || p.Teams.Any(t => t.Team!.Members.Any(m => m.UserId == userId));

    /// <summary>
    /// The complement of <see cref="VisibleProjectPredicate"/>: the projects that
    /// appear in <c>/projects</c> as a locked, name-only row. Written out longhand
    /// rather than negated at the call site so both halves of the split read the
    /// same way and a reader can check them against each other.
    /// </summary>
    public static Expression<Func<OeProject, bool>> LockedProjectPredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BypassesVisibility) return _ => false;

        var userId = snapshot.UserId;
        var teamIds = snapshot.TeamIds.ToList();
        return p => p.Visibility == ProjectVisibility.Private
                    && (userId == null || p.CreatedByUserId != userId)
                    && !p.Teams.Any(t => teamIds.Contains(t.TeamId));
    }

    // ── View axis, keyed by release ─────────────────────────────────────

    /// <summary>
    /// Release visibility answers already resolved in this DI scope. The source
    /// viewer asks the same question several times while rendering one page
    /// (header, tree, outline, dependencies), and the answer cannot change within
    /// a scope for the same reason <see cref="_snapshot"/> is cached.
    /// </summary>
    private readonly Dictionary<int, bool> _releaseVisibility = new();

    /// <summary>
    /// True when the current user may see release <paramref name="releaseId"/>.
    /// A <c>Release</c> has no project of its own: it belongs to one when a project
    /// build produced it (<c>oe_project_builds.release_id</c>) or when it was
    /// imported under a project (<c>oe_import_jobs.project_id</c>). It is hidden
    /// only when one of those links points at a
    /// <see cref="ProjectVisibility.Private"/> project the caller cannot view;
    /// a release linked to nothing stays governed by the org query filter alone.
    ///
    /// <para>This strengthens the callers' existing checks rather than replacing
    /// them — <c>ObjectSearchService</c> and <c>ReferenceQueryService</c> still
    /// confirm the release exists in the caller's org first, which is the tenant
    /// fence their raw SQL depends on.</para>
    /// </summary>
    public async Task<bool> IsReleaseVisibleAsync(int releaseId, CancellationToken ct = default)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        if (snapshot.BypassesVisibility) return true;
        if (_releaseVisibility.TryGetValue(releaseId, out var cached)) return cached;

        var blocked = await _db.OeProjects.AsNoTracking()
            .Where(LockedProjectPredicate(snapshot))
            .Where(LinkedToRelease(_db, releaseId))
            .AnyAsync(ct).ConfigureAwait(false);

        return _releaseVisibility[releaseId] = !blocked;
    }

    /// <summary>
    /// The list-query form of <see cref="IsReleaseVisibleAsync"/>: which releases
    /// <paramref name="snapshot"/> may see, as a NOT-EXISTS over the two linkages.
    /// Kept deliberately in step with <see cref="LockedProjectPredicate"/> — the
    /// project half of the rule is that predicate, reached through the same two
    /// links the per-row check walks, so a change to one belongs in the other.
    /// Returns "everything" when the snapshot bypasses visibility.
    ///
    /// <para>An instance method rather than a static one because the subquery has
    /// to reach <c>oe_import_jobs</c>, which no navigation property on
    /// <c>Release</c> exposes.</para>
    /// </summary>
    public Expression<Func<OeRelease, bool>> VisibleReleasePredicate(AccessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.BypassesVisibility) return _ => true;

        var db = _db;
        var userId = snapshot.UserId;
        var teamIds = snapshot.TeamIds.ToList();
        // The inner Where is LockedProjectPredicate written out again: EF has to
        // translate this whole tree to SQL, and an invoked expression variable
        // doesn't survive that trip. Keep the two in step.
        return r => !db.OeProjects
            .Where(p => p.Visibility == ProjectVisibility.Private
                        && (userId == null || p.CreatedByUserId != userId)
                        && !p.Teams.Any(t => teamIds.Contains(t.TeamId)))
            .Any(p => p.Builds.Any(b => b.ReleaseId == r.Id)
                      || db.OeImportJobs.Any(j => j.ProjectId == p.Id && j.ReleaseId == r.Id));
    }

    /// <summary>
    /// The two ways a project owns a release: it produced it as a build, or the
    /// release was imported under the project. Shared by
    /// <see cref="IsReleaseVisibleAsync"/> and, in inlined form, by
    /// <see cref="VisibleReleasePredicate"/>.
    /// </summary>
    private static Expression<Func<OeProject, bool>> LinkedToRelease(AppDbContext db, int releaseId)
        => p => p.Builds.Any(b => b.ReleaseId == releaseId)
                || db.OeImportJobs.Any(j => j.ProjectId == p.Id && j.ReleaseId == releaseId);

    /// <summary>
    /// The one fact both the view and the manage axis need about a project — what
    /// level it is at, and who owns it — cached for the DI scope beside
    /// <see cref="_snapshot"/> and for the same reason. A page that asks both
    /// questions about one project (every detail page does) pays one read, not two,
    /// and the answer cannot change under it mid-render. A project that is not in
    /// this org is absent from the dictionary's value, not from the dictionary: the
    /// org query filter has already decided it does not exist for this caller, and
    /// each axis says what that means for it.
    /// </summary>
    private readonly Dictionary<int, ProjectRow?> _projectRows = new();

    /// <summary>The cached <c>(visibility, owner)</c> for one project; null when it isn't in this org.</summary>
    private async Task<ProjectRow?> GetProjectRowAsync(int projectId, CancellationToken ct)
    {
        if (_projectRows.TryGetValue(projectId, out var cached)) return cached;

        var row = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => new ProjectRow(p.Visibility, p.CreatedByUserId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return _projectRows[projectId] = row;
    }

    private sealed record ProjectRow(ProjectVisibility Visibility, int? CreatedByUserId);

    /// <summary>True when the snapshot's user is on at least one team assigned to the project.</summary>
    private async Task<bool> IsOnAnAssignedTeamAsync(int projectId, AccessSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.TeamIds.Count == 0) return false;
        var teamIds = snapshot.TeamIds.ToList();
        return await _db.OeProjectTeams.AsNoTracking()
            .AnyAsync(t => t.ProjectId == projectId && teamIds.Contains(t.TeamId), ct)
            .ConfigureAwait(false);
    }
}
