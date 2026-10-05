using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>One row of the Notifications page.</summary>
public sealed record InAppNotificationRow(
    int Id,
    NotificationCategory Category,
    string Title,
    string? Detail,
    DateTime CreatedAt,
    bool Read);

/// <summary>
/// The signed-in person's own in-app notifications (issue #1043): the header
/// count, the Notifications page, opening one and marking them all read. Every
/// query names the current user on top of the organisation query filter, so a
/// person only ever sees and changes their own. See <c>.design/notifications.md</c>.
///
/// <para>
/// Opens a short-lived context per call through the factory, like
/// <see cref="Organizations.DisplayTimeZone"/>: the header count renders in the
/// layout on every page, alongside whatever queries the page itself has in
/// flight, and a <see cref="AppDbContext"/> allows one operation at a time.
/// </para>
/// </summary>
public sealed class InAppNotificationService
{
    /// <summary>The most the page lists. Older ones are pruned after 30 days anyway.</summary>
    public const int PageSize = 200;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<InAppNotificationService> _logger;

    public InAppNotificationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<InAppNotificationService> logger)
    {
        _dbFactory = dbFactory;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>How many of the signed-in person's notifications are unread; 0 when nobody is signed in.</summary>
    public async Task<int> CountUnreadForCurrentUserAsync(CancellationToken ct = default)
    {
        if (_orgContext.CurrentUserId is not { } userId) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await (await VisibleAsync(db, userId, ct))
            .CountAsync(n => n.ReadAt == null, ct);
    }

    /// <summary>The signed-in person's notifications, newest first, at most <see cref="PageSize"/>.</summary>
    public async Task<List<InAppNotificationRow>> ListForCurrentUserAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await (await VisibleAsync(db, userId, ct))
            .OrderByDescending(n => n.CreatedAt)
            .ThenByDescending(n => n.Id)
            .Take(PageSize)
            .Select(n => new InAppNotificationRow(
                n.Id, n.Category, n.Title, n.Detail, n.CreatedAt, n.ReadAt != null))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks one of the signed-in person's notifications read and returns the
    /// path it is about, or null when it is not theirs or no longer exists.
    /// </summary>
    public async Task<string?> OpenForCurrentUserAsync(int id, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var path = await (await VisibleAsync(db, userId, ct))
            .Where(n => n.Id == id)
            .Select(n => n.Path)
            .FirstOrDefaultAsync(ct);
        if (path is null) return null;

        var now = _clock.GetUtcNow().UtcDateTime;
        await db.UserNotifications
            .Where(n => n.Id == id && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return path;
    }

    /// <summary>
    /// Marks the signed-in person's unread notifications about <paramref name="path"/> read,
    /// as if they had opened them from the list: they are looking at the page each is about,
    /// most often having followed the link in its email. <paramref name="path"/> is the
    /// page's path and query, compared exactly with the stored one. Returns how many
    /// changed; 0 when nobody is signed in.
    /// </summary>
    public async Task<int> MarkPageReadForCurrentUserAsync(string path, CancellationToken ct = default)
    {
        if (_orgContext.CurrentUserId is not { } userId) return 0;
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.UserNotifications
            .Where(n => n.UserId == userId && n.ReadAt == null && n.Path == path)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }

    /// <summary>
    /// Marks the signed-in person's notifications read, up to and including id
    /// <paramref name="upToId"/>: the newest one the page showed, so one that
    /// arrived after the page loaded stays unread. Returns how many changed.
    /// </summary>
    public async Task<int> MarkAllReadForCurrentUserAsync(int upToId, CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var marked = await db.UserNotifications
            .Where(n => n.UserId == userId && n.ReadAt == null && n.Id <= upToId)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        _logger.LogInformation("User {UserId} marked {Count} notifications read.", userId, marked);
        return marked;
    }

    /// <summary>
    /// The person's own notifications, less those about a Private solution they can
    /// no longer see (taken off its team since): access is rechecked on every read,
    /// not only when the notification was written. An org Admin or SiteAdmin sees
    /// every solution, so nothing is left out for them.
    /// </summary>
    private static async Task<IQueryable<UserNotification>> VisibleAsync(
        AppDbContext db, int userId, CancellationToken ct)
    {
        var mine = db.UserNotifications.AsNoTracking().Where(n => n.UserId == userId);
        var seesEverything = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.Role == UserRole.Admin || u.IsSiteAdmin)
            .FirstOrDefaultAsync(ct);
        if (seesEverything) return mine;
        var visible = db.OeProjects.Where(ProjectAccess.VisibleToUserPredicate(userId));
        return mine.Where(n => n.ProjectId == null || visible.Any(p => p.Id == n.ProjectId));
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No signed-in user; notifications belong to one.");
}
