using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Who follows a solution, and so hears about its own events, such as Business
/// Central update dates (issue #1048). The owner and the people on the solution's
/// People list follow by default, so someone added to the list later starts
/// following without anything to backfill; anyone may follow a solution they can
/// see, and anyone may stop. See <c>.design/notifications.md</c>, "Following a
/// solution".
/// </summary>
public sealed class ProjectFollowService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly TimeProvider _clock;
    private readonly ILogger<ProjectFollowService> _logger;

    public ProjectFollowService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        TimeProvider clock,
        ILogger<ProjectFollowService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether the signed-in person follows solution <paramref name="projectId"/>, by choice or by default.</summary>
    public async Task<bool> IsFollowingAsync(int projectId, CancellationToken ct = default)
    {
        if (_orgContext.CurrentUserId is not { } userId) return false;

        var choice = await _db.OeProjectFollowers.AsNoTracking()
            .Where(f => f.ProjectId == projectId && f.UserId == userId)
            .Select(f => (bool?)f.Following)
            .FirstOrDefaultAsync(ct);
        return choice ?? await FollowsByDefaultAsync(projectId, userId, ct);
    }

    /// <summary>
    /// Follows or stops following solution <paramref name="projectId"/> for the signed-in
    /// person. Throws <see cref="ProjectAccessDeniedException"/> for a Private solution
    /// they cannot see.
    /// </summary>
    public async Task SetFollowingAsync(int projectId, bool following, CancellationToken ct = default)
    {
        var userId = _orgContext.CurrentUserId
            ?? throw new InvalidOperationException("Following a solution needs a signed-in user.");
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Following a solution needs an organisation.");
        await _access.EnsureCanViewAsync(projectId, ct);
        if (!await _db.OeProjects.AsNoTracking().AnyAsync(p => p.Id == projectId && p.DeletedAt == null, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Solution"] = "This solution no longer exists.",
            });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        // One statement, so a double click cannot insert twice and trip the unique index.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO oe_project_followers (organization_id, project_id, user_id, following, updated_at)
            VALUES ({orgId}, {projectId}, {userId}, {following}, {now})
            ON CONFLICT (project_id, user_id)
            DO UPDATE SET following = EXCLUDED.following, updated_at = EXCLUDED.updated_at
            """, ct);
        _logger.LogInformation("User {UserId} {Action} solution {ProjectId}.",
            userId, following ? "followed" : "stopped following", projectId);
    }

    /// <summary>
    /// The active people who follow solution <paramref name="projectId"/> and can still
    /// see it, for a notifier. Someone who lost access to a Private solution (taken off
    /// its team) is left out without losing their choice, so it applies again if access
    /// comes back. Reads no signed-in user, so a background job can call it inside the
    /// organisation's scope.
    /// </summary>
    public async Task<List<int>> ListFollowerIdsAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.CreatedByUserId, p.Visibility })
            .FirstOrDefaultAsync(ct);
        if (project is null) return [];

        var choices = await _db.OeProjectFollowers.AsNoTracking()
            .Where(f => f.ProjectId == projectId)
            .ToDictionaryAsync(f => f.UserId, f => f.Following, ct);
        var defaults = await _db.OeProjectPeople.AsNoTracking()
            .Where(p => p.ProjectId == projectId)
            .Select(p => p.UserId)
            .ToListAsync(ct);
        if (project.CreatedByUserId is { } owner) defaults.Add(owner);

        var candidates = defaults.Where(id => !choices.TryGetValue(id, out var following) || following)
            .Concat(choices.Where(c => c.Value).Select(c => c.Key))
            .Distinct()
            .ToList();
        if (candidates.Count == 0) return [];

        var users = _db.Users.AsNoTracking()
            .Where(u => candidates.Contains(u.Id) && u.Status == UserStatus.Active);
        if (project.Visibility == ProjectVisibility.Private)
        {
            // CanViewAsync for each of them: the owner, an admin, or a member of one of its teams.
            var members = _db.OeProjectTeams
                .Where(t => t.ProjectId == projectId)
                .SelectMany(t => t.Team!.Members.Select(m => m.UserId));
            users = users.Where(u => u.Id == project.CreatedByUserId
                                     || u.Role == UserRole.Admin
                                     || u.IsSiteAdmin
                                     || members.Contains(u.Id));
        }
        return await users.OrderBy(u => u.Id).Select(u => u.Id).ToListAsync(ct);
    }

    private async Task<bool> FollowsByDefaultAsync(int projectId, int userId, CancellationToken ct) =>
        await _db.OeProjects.AsNoTracking().AnyAsync(p => p.Id == projectId && p.CreatedByUserId == userId, ct)
        || await _db.OeProjectPeople.AsNoTracking().AnyAsync(p => p.ProjectId == projectId && p.UserId == userId, ct);
}
