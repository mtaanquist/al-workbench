using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Teams and their membership: who exists, who is on them, and who may change
/// that. Org-scoped via the EF query filter; every mutation runs inside an
/// authenticated request (<see cref="RequireOrganizationId"/> throws otherwise)
/// and re-checks authorisation itself, so a page that forgot to hide a button
/// still can't perform the action. Validation throws
/// <see cref="PlanValidationException"/> with field-keyed errors so forms render
/// them inline. See <c>.design/teams-and-visibility.md</c>.
///
/// <para>Two authorisation levels, deliberately different:</para>
/// <list type="bullet">
///   <item><b>Administer</b> (create, delete) — org Admin or SiteAdmin. Creating
///   and destroying teams is an org-shaping act.</item>
///   <item><b>Manage</b> (rename, add / remove members, promote) — the above plus
///   a member of that team flagged <see cref="TeamMember.IsManager"/>. This is the
///   non-admin surface behind <c>/teams/{id}</c>.</item>
/// </list>
///
/// <para>What membership <em>grants</em> lives in
/// <see cref="Services.ObjectExplorer.ProjectAccess"/>: being on a team assigned to
/// a project lets you see and change it. That is why deleting a team is refused
/// while it is the last team on a non-Public project.</para>
/// </summary>
public sealed class TeamService
{
    /// <summary>Longest team name we accept. Mirrored as <c>maxlength</c> on the form input.</summary>
    public const int MaxNameLength = 80;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<TeamService> _logger;

    public TeamService(AppDbContext db, IOrganizationContext orgContext, ILogger<TeamService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; team mutation called outside an authenticated request.");

    // ---------------------------------------------------------------- gates

    /// <summary>
    /// True when the current user may manage <paramref name="teamId"/>: SiteAdmin,
    /// or an org Admin, or a member of that team flagged as a manager. False for an
    /// unauthenticated caller (a background worker running under an ambient org
    /// scope has no user) — never throws, so callers can use it to decide what to
    /// render.
    /// </summary>
    public async Task<bool> CanManageTeamAsync(int teamId, CancellationToken ct = default)
    {
        if (_orgContext.IsSiteAdmin) return true;

        var userId = _orgContext.CurrentUserId;
        if (userId is null) return false;

        if (await IsOrgAdminAsync(userId.Value, ct).ConfigureAwait(false)) return true;

        return await _db.TeamMembers.AsNoTracking()
            .AnyAsync(m => m.TeamId == teamId && m.UserId == userId.Value && m.IsManager, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Throws <see cref="ProjectAccessDeniedException"/> when the current user may
    /// not manage <paramref name="teamId"/>.
    /// </summary>
    public async Task EnsureCanManageTeamAsync(int teamId, CancellationToken ct = default)
    {
        if (!await CanManageTeamAsync(teamId, ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "Only a team manager or an organisation admin can change this team.");
        }
    }

    /// <summary>
    /// True when the current user may create and delete teams — SiteAdmin or org
    /// Admin. Exposed so the Teams admin page can render read-only rather than
    /// erroring on click.
    /// </summary>
    public async Task<bool> CanAdministerTeamsAsync(CancellationToken ct = default)
    {
        if (_orgContext.IsSiteAdmin) return true;
        var userId = _orgContext.CurrentUserId;
        return userId is not null && await IsOrgAdminAsync(userId.Value, ct).ConfigureAwait(false);
    }

    private async Task EnsureCanAdministerTeamsAsync(CancellationToken ct)
    {
        if (!await CanAdministerTeamsAsync(ct).ConfigureAwait(false))
        {
            throw new ProjectAccessDeniedException(
                "Only an organisation admin can create or delete teams.");
        }
    }

    /// <summary>Reads the role off the user row (org-scoped by the query filter).</summary>
    private async Task<bool> IsOrgAdminAsync(int userId, CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Role)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false) == UserRole.Admin;

    // ---------------------------------------------------------------- reads

    /// <summary>Every team in the current org with its member count and managers, by name.</summary>
    public async Task<List<TeamListRow>> ListTeamsAsync(CancellationToken ct = default) =>
        await _db.Teams.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new TeamListRow(
                t.Id,
                t.Name,
                t.Members.Count,
                t.Members.Where(m => m.IsManager)
                    .OrderBy(m => m.User!.DisplayName)
                    .Select(m => m.User!.DisplayName)
                    .ToList(),
                // Managers first, then by name: the person a reader is checking
                // for is most often the one who runs the account.
                t.Members
                    .OrderByDescending(m => m.IsManager)
                    .ThenBy(m => m.User!.DisplayName)
                    .Select(m => m.User!.DisplayName)
                    .ToList()))
            .ToListAsync(ct);

    /// <summary>
    /// The teams the signed-in user is on, by name — the <c>/teams</c> index.
    /// Empty (never null) when nobody is signed in.
    /// </summary>
    public async Task<List<TeamListRow>> ListMyTeamsAsync(CancellationToken ct = default)
    {
        var userId = _orgContext.CurrentUserId;
        if (userId is null) return new List<TeamListRow>();

        return await _db.Teams.AsNoTracking()
            .Where(t => t.Members.Any(m => m.UserId == userId.Value))
            .OrderBy(t => t.Name)
            .Select(t => new TeamListRow(
                t.Id,
                t.Name,
                t.Members.Count,
                t.Members.Where(m => m.IsManager)
                    .OrderBy(m => m.User!.DisplayName)
                    .Select(m => m.User!.DisplayName)
                    .ToList(),
                // Managers first, then by name: the person a reader is checking
                // for is most often the one who runs the account.
                t.Members
                    .OrderByDescending(m => m.IsManager)
                    .ThenBy(m => m.User!.DisplayName)
                    .Select(m => m.User!.DisplayName)
                    .ToList()))
            .ToListAsync(ct);
    }

    /// <summary>
    /// One team and its roster (managers first, then by name), or null when it
    /// doesn't exist in this org.
    /// </summary>
    public async Task<TeamRoster?> GetTeamAsync(int id, CancellationToken ct = default)
    {
        var team = await _db.Teams.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync(ct);
        if (team is null) return null;

        var members = await _db.TeamMembers.AsNoTracking()
            .Where(m => m.TeamId == id)
            .OrderByDescending(m => m.IsManager)
            .ThenBy(m => m.User!.DisplayName)
            .Select(m => new TeamMemberRow(
                m.Id,
                m.UserId,
                m.User!.DisplayName,
                m.User!.Email,
                m.User!.Role,
                m.IsManager,
                m.ManagesUpdates))
            .ToListAsync(ct);

        return new TeamRoster(team.Id, team.Name, members);
    }

    /// <summary>
    /// Active org members not yet on <paramref name="teamId"/> — the "Add member"
    /// picker's options, by name. Disabled accounts are left out: adding someone
    /// who can't sign in is never what the manager meant.
    /// </summary>
    public async Task<List<TeamCandidateRow>> ListAddableUsersAsync(int teamId, CancellationToken ct = default) =>
        await _db.Users.AsNoTracking()
            .Where(u => u.Status == UserStatus.Active
                        && !_db.TeamMembers.Any(m => m.TeamId == teamId && m.UserId == u.Id))
            .OrderBy(u => u.DisplayName)
            .Select(u => new TeamCandidateRow(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);

    // --------------------------------------------------------------- writes

    /// <summary>Creates a team. Org Admin / SiteAdmin only. Returns the new id.</summary>
    public async Task<int> CreateTeamAsync(string name, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureCanAdministerTeamsAsync(ct).ConfigureAwait(false);

        var clean = await ValidateNameAsync(name, existingId: null, ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        var team = new Team
        {
            OrganizationId = orgId,
            Name = clean,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Teams.Add(team);
        await SaveTranslatingNameClashAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Created team {TeamId} ({Name}) for org {OrgId}.", team.Id, clean, orgId);
        return team.Id;
    }

    /// <summary>Renames a team. Managers and admins.</summary>
    public async Task RenameTeamAsync(int id, string name, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await EnsureCanManageTeamAsync(id, ct).ConfigureAwait(false);

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw Validation("Name", "This team no longer exists.");

        var clean = await ValidateNameAsync(name, existingId: id, ct).ConfigureAwait(false);
        var previous = team.Name;
        team.Name = clean;
        team.UpdatedAt = DateTime.UtcNow;
        await SaveTranslatingNameClashAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Renamed team {TeamId} from {PreviousName} to {Name}.", id, previous, clean);
    }

    /// <summary>
    /// Deletes a team outright; its membership rows cascade. Org Admin / SiteAdmin
    /// only — see <see cref="Team"/> for why this is a hard delete.
    /// </summary>
    /// <remarks>
    /// Refused while this team is the <em>last</em> team assigned to a non-Public
    /// project: deleting it would leave that project with a Private or Read-only
    /// setting and nobody granted by it. Auto-resetting those projects to Public
    /// instead would silently expose exactly the projects this feature exists to
    /// hide, so the refusal names them and leaves the choice with the admin. See
    /// <c>.design/teams-and-visibility.md</c>.
    /// </remarks>
    public async Task DeleteTeamAsync(int id, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await EnsureCanAdministerTeamsAsync(ct).ConfigureAwait(false);

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (team is null) return;

        var blocking = await _db.OeProjectTeams.AsNoTracking()
            .Where(pt => pt.TeamId == id
                         && pt.Project!.DeletedAt == null
                         && pt.Project.Visibility != ProjectVisibility.Public
                         && pt.Project.Teams.Count == 1)
            .OrderBy(pt => pt.Project!.Name)
            .Select(pt => pt.Project!.Name)
            .ToListAsync(ct);
        if (blocking.Count > 0)
        {
            throw Validation("Name", blocking.Count == 1
                ? $"{team.Name} is the only team on the solution {blocking[0]}. Give that solution another team, or make it public, then delete this team."
                : $"{team.Name} is the only team on these solutions: {string.Join(", ", blocking)}. Give them another team, or make them public, then delete this team.");
        }

        // Load the membership so the cascade runs through the change tracker and
        // each removed row gets its own audit entry.
        var members = await _db.TeamMembers.Where(m => m.TeamId == id).ToListAsync(ct);
        _db.TeamMembers.RemoveRange(members);
        _db.Teams.Remove(team);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Deleted team {TeamId} ({Name}) and its {MemberCount} membership row(s).",
            id, team.Name, members.Count);
    }

    /// <summary>Adds an org member to a team. Managers and admins.</summary>
    public async Task<int> AddMemberAsync(int teamId, int userId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await EnsureCanManageTeamAsync(teamId, ct).ConfigureAwait(false);

        var teamExists = await _db.Teams.AsNoTracking().AnyAsync(t => t.Id == teamId, ct);
        if (!teamExists) throw Validation("UserId", "This team no longer exists.");

        // Org-scoped by the query filter: someone from another organisation simply
        // isn't found, which is the right answer and the right error.
        var user = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Status, u.DisplayName })
            .FirstOrDefaultAsync(ct);
        if (user is null) throw Validation("UserId", "Pick somebody in your organisation.");
        if (user.Status != UserStatus.Active) throw Validation("UserId", "That account is disabled.");

        var already = await _db.TeamMembers.AsNoTracking()
            .AnyAsync(m => m.TeamId == teamId && m.UserId == userId, ct);
        if (already) throw Validation("UserId", "That person is already on this team.");

        var member = new TeamMember
        {
            OrganizationId = orgId,
            TeamId = teamId,
            UserId = userId,
            IsManager = false,
            CreatedAt = DateTime.UtcNow,
        };
        _db.TeamMembers.Add(member);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Added user {UserId} to team {TeamId} as member {MemberId}.",
            userId, teamId, member.Id);
        return member.Id;
    }

    /// <summary>Removes a membership row. Managers and admins.</summary>
    public async Task RemoveMemberAsync(int teamId, int memberId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await EnsureCanManageTeamAsync(teamId, ct).ConfigureAwait(false);

        var member = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == teamId, ct);
        if (member is null) return;

        _db.TeamMembers.Remove(member);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Removed user {UserId} from team {TeamId}.", member.UserId, teamId);
    }

    /// <summary>
    /// Promotes or demotes a member. Managers and admins. There is deliberately no
    /// last-manager guard: a team with no manager is a valid state that org Admins
    /// can always recover from, and a guard would strand the last manager on a team
    /// they wanted to leave.
    /// </summary>
    public async Task SetManagerAsync(int teamId, int memberId, bool isManager, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await EnsureCanManageTeamAsync(teamId, ct).ConfigureAwait(false);

        var member = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == teamId, ct);
        if (member is null) return;
        if (member.IsManager == isManager) return;

        member.IsManager = isManager;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Set manager={IsManager} for user {UserId} on team {TeamId}.",
            isManager, member.UserId, teamId);
    }

    /// <summary>
    /// Grants or withdraws <see cref="TeamMember.ManagesUpdates"/> for one member of
    /// <paramref name="teamId"/>. Managers and admins, same gate as the manager
    /// toggle — but a different capability: it lets the member schedule Business
    /// Central platform updates on the projects this team is assigned to, and grants
    /// nothing over the team itself. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="SetManagerAsync"/>, a membership row that isn't there is an
    /// error rather than a no-op: this call hands out a grant that reaches other
    /// people's production tenants, so a caller aiming at the wrong row should hear
    /// about it instead of believing the grant landed.
    /// </remarks>
    public async Task SetManagesUpdatesAsync(int teamId, int memberId, bool managesUpdates, CancellationToken ct = default)
    {
        RequireOrganizationId();
        await EnsureCanManageTeamAsync(teamId, ct).ConfigureAwait(false);

        var member = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == teamId, ct);
        if (member is null) throw Validation("MemberId", "That person is no longer on this team.");
        if (member.ManagesUpdates == managesUpdates) return;

        member.ManagesUpdates = managesUpdates;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Set manages-updates={ManagesUpdates} for user {UserId} on team {TeamId}.",
            managesUpdates, member.UserId, teamId);
    }

    // ----------------------------------------------------------- validation

    /// <summary>
    /// Trims and checks a team name. Uniqueness is per-org and case-insensitive; we
    /// pre-check for a friendly inline error, and the functional unique index
    /// created by the <c>AddTeams</c> migration is the backstop. Org-scoping comes
    /// from the ambient query filter, so no explicit organization_id predicate.
    /// </summary>
    private async Task<string> ValidateNameAsync(string? name, int? existingId, CancellationToken ct)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw Validation("Name", "Enter a team name.");
        if (clean.Length > MaxNameLength) throw Validation("Name", $"Keep the name under {MaxNameLength} characters.");

        var clash = await _db.Teams.AsNoTracking()
            .AnyAsync(t => t.Id != (existingId ?? 0) && t.Name.ToLower() == clean.ToLower(), ct);
        // States the rule *and* the way out of it — an error that only restates
        // the rule leaves the user to guess that a different name is the fix.
        if (clash) throw Validation("Name", "Another team already uses this name. Pick a different one.");

        return clean;
    }

    /// <summary>
    /// Saves, turning the name-uniqueness backstop into the same field-keyed error
    /// the pre-check gives. <see cref="ValidateNameAsync"/> reads before it writes,
    /// so two admins naming a team the same thing at once leave one of them to be
    /// caught by the functional unique index — and that has to read as an inline
    /// "pick a different name", not a 500. See issue #702.
    /// </summary>
    private async Task SaveTranslatingNameClashAsync(CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            throw Validation("Name", "Another team already uses this name. Pick a different one.");
        }
    }

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>
/// A team as it appears in a list: its name, how many people are on it, and who
/// manages it. Manager <em>names</em> rather than a count, because a reader
/// scanning the list wants to know who to ask, and "2" does not tell them.
/// </summary>
public sealed record TeamListRow(
    int Id,
    string Name,
    int MemberCount,
    IReadOnlyList<string> ManagerNames,
    IReadOnlyList<string> MemberNames)
{
    /// <summary>True when nobody manages this team — org Admins do, in that case.</summary>
    public bool HasManager => ManagerNames.Count > 0;

    /// <summary>
    /// The roster as a reader scans it: the first few names, then "+n". Two names
    /// is what fits a card line, and picking the wrong team is the failure the
    /// project Access tab exists to prevent — a count alone cannot catch it.
    /// Empty string for a team with nobody on it; the caller shows the count.
    /// </summary>
    public string MemberSummary(int show = 2)
    {
        if (MemberNames.Count == 0) return string.Empty;
        var shown = string.Join(", ", MemberNames.Take(show));
        var rest = MemberNames.Count - Math.Min(show, MemberNames.Count);
        return rest > 0 ? $"{shown} +{rest}" : shown;
    }
}

/// <summary>One person on a team, joined to their account for display.</summary>
public sealed record TeamMemberRow(
    int MemberId,
    int UserId,
    string DisplayName,
    string Email,
    UserRole Role,
    bool IsManager,
    bool ManagesUpdates);

/// <summary>A team and its roster — what <c>/teams/{id}</c> renders.</summary>
public sealed record TeamRoster(int Id, string Name, IReadOnlyList<TeamMemberRow> Members);

/// <summary>Somebody who could be added to a team — the picker's options.</summary>
public sealed record TeamCandidateRow(int UserId, string DisplayName, string Email);
