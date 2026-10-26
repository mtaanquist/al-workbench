using System.Globalization;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Sealed;

/// <summary>
/// The background half of Sealed, for the organisation in scope: closes links past their
/// expiry (dropping the ciphertext), tells senders their links were opened or locked,
/// and deletes closed links once <see cref="SealedLinkService.RetentionAfterClose"/> has
/// passed. Run org by org by <see cref="SealedLinkScheduler"/>, so every query here stays
/// behind the tenant filter. See <c>.design/sealed.md</c>.
/// </summary>
public sealed class SealedLinkSweepService
{
    /// <summary>Where the sender's links are listed; the notification and email point here.</summary>
    public const string LinksPath = "/sealed";

    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<SealedLinkSweepService> _logger;

    public SealedLinkSweepService(
        AppDbContext db,
        NotificationService notifications,
        TimeProvider clock,
        ILogger<SealedLinkSweepService> logger)
    {
        _db = db;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>One pass for the organisation in scope. Returns how many notifications were sent.</summary>
    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        var expired = await _db.SealedLinks
            .Where(l => l.ClosedAt == null && l.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Ciphertext, (byte[]?)null)
                .SetProperty(l => l.ClosedAt, l => l.ExpiresAt)
                .SetProperty(l => l.ClosedReason, SealedLinkClosedReason.Expired), ct);

        var sent = await NotifyAsync(ct);

        var cutoff = now - SealedLinkService.RetentionAfterClose;
        var deleted = await _db.SealedLinks
            .Where(l => l.ClosedAt != null && l.ClosedAt < cutoff)
            .ExecuteDeleteAsync(ct);

        if (expired > 0 || deleted > 0)
        {
            _logger.LogInformation("Sealed links: {Expired} expired, {Deleted} deleted after retention.", expired, deleted);
        }
        return sent;
    }

    private async Task<int> NotifyAsync(CancellationToken ct)
    {
        var pending = await _db.SealedLinks.AsNoTracking()
            .Where(l => l.ViewCount > l.NotifiedViewCount
                || (l.ClosedReason == SealedLinkClosedReason.LockedOut && !l.LockoutNotified))
            .Select(l => new
            {
                l.Id,
                l.CreatedByUserId,
                l.Label,
                l.ViewCount,
                l.NotifiedViewCount,
                l.MaxViews,
                l.ClosedReason,
                l.LockoutNotified,
                LastOpen = l.Events
                    .Where(e => e.Kind == SealedLinkEventKind.Opened)
                    .OrderByDescending(e => e.OccurredAt)
                    .Select(e => new { e.OccurredAt, e.IpAddress })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var sent = 0;
        foreach (var link in pending)
        {
            var name = DisplayName(link.Label);
            if (link.ViewCount > link.NotifiedViewCount)
            {
                var viewsLeft = link.ClosedReason is null or SealedLinkClosedReason.UsedUp
                    ? Math.Max(0, link.MaxViews - link.ViewCount)
                    : 0;
                var openedLine = OpenedLine(link.LastOpen?.OccurredAt, link.LastOpen?.IpAddress);
                var detail = $"Opened {link.ViewCount} of {link.MaxViews} {(link.MaxViews == 1 ? "time" : "times")}"
                    + (link.LastOpen is null ? "." : $", last {openedLine}.");
                await SendAsync(link.CreatedByUserId, link.Id, lockedOut: false, name, detail, openedLine, viewsLeft, ct);
                await _db.SealedLinks
                    .Where(l => l.Id == link.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.NotifiedViewCount, link.ViewCount), ct);
                sent++;
            }
            if (link.ClosedReason == SealedLinkClosedReason.LockedOut && !link.LockoutNotified)
            {
                await SendAsync(link.CreatedByUserId, link.Id, lockedOut: true, name,
                    link.ViewCount == 0
                        ? "Nobody saw the secret. Whoever tried had the whole link."
                        : $"The secret had been shown {link.ViewCount} {(link.ViewCount == 1 ? "time" : "times")} before it locked. Whoever tried had the whole link.",
                    string.Empty, 0, ct);
                await _db.SealedLinks
                    .Where(l => l.Id == link.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.LockoutNotified, true), ct);
                sent++;
            }
        }
        return sent;
    }

    private Task SendAsync(
        int userId, int linkId, bool lockedOut, string name, string detail, string openedLine, int viewsLeft, CancellationToken ct) =>
        _notifications.NotifyAsync(new Notification(
            NotificationCategory.SealedLinks,
            [userId],
            new NotificationSummary(SealedLinkEmail.SubjectFor(lockedOut, name), detail, LinksPath, null),
            (email, token) => SealedLinkEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, name, lockedOut, openedLine,
                viewsLeft, SealedLinkService.MaxPassphraseAttempts, email.ItemUrl, email.SettingsUrl, token)),
            ct);

    /// <summary>What a link is called in a notification: the sender's note, or a stand-in.</summary>
    internal static string DisplayName(string? label) =>
        string.IsNullOrWhiteSpace(label) ? "(no note)" : label;

    /// <summary>"at 12:40 UTC on 26 Oct 2026 from 203.0.113.4", leaving out whatever is unknown.</summary>
    internal static string OpenedLine(DateTime? at, string? ip)
    {
        var line = at is { } when
            ? "at " + DateTime.SpecifyKind(when, DateTimeKind.Utc).ToString("HH:mm 'UTC on' d MMM yyyy", CultureInfo.InvariantCulture)
            : string.Empty;
        if (!string.IsNullOrWhiteSpace(ip)) line = (line.Length > 0 ? line + " " : string.Empty) + "from " + ip;
        return line;
    }
}
