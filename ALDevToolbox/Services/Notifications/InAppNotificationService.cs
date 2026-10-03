using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
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
        return await db.UserNotifications.AsNoTracking()
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
    }

    /// <summary>The signed-in person's notifications, newest first, at most <see cref="PageSize"/>.</summary>
    public async Task<List<InAppNotificationRow>> ListForCurrentUserAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == userId)
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
        var path = await db.UserNotifications.AsNoTracking()
            .Where(n => n.Id == id && n.UserId == userId)
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

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No signed-in user; notifications belong to one.");
}
