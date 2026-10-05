using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// What a notification asks its recipients to do, stored on each in-app copy
/// (<see cref="Domain.Entities.UserNotification.Subject"/>), and the one call that
/// clears those copies once it is done. Whoever does it, and however they got there
/// (the email link, the notification, or the page itself), nobody is left with an
/// unread request that is already settled. See <c>.design/notifications.md</c>.
/// </summary>
public static class NotificationSubject
{
    /// <summary>A deployment waiting for approval.</summary>
    public static string Delivery(int deliveryId) => $"delivery:{deliveryId}";

    /// <summary>An environment on a planned upgrade, ready to check.</summary>
    public static string UpgradeLine(int lineId) => $"upgrade-line:{lineId}";

    /// <summary>
    /// Marks every recipient's unread notification about <paramref name="subjects"/> read
    /// and returns how many changed. Called after the work itself is saved, through the
    /// caller's context, so it stays behind the organisation filter. Never throws, except
    /// when <paramref name="ct"/> itself is cancelled: the approval or check already
    /// happened, and a notification left unread must not report it as failed.
    /// </summary>
    public static async Task<int> MarkDoneAsync(
        AppDbContext db, IReadOnlyCollection<string> subjects, DateTime now, ILogger logger, CancellationToken ct)
    {
        if (subjects.Count == 0) return 0;
        try
        {
            return await db.UserNotifications
                .Where(n => n.Subject != null && subjects.Contains(n.Subject) && n.ReadAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not mark notifications about {Subjects} read.", string.Join(", ", subjects));
            return 0;
        }
    }
}
