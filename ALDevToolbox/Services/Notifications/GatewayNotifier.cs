using System.Globalization;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Gateways;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells the organisation's Admins when an on-premises gateway goes offline (three
/// missed heartbeats, <see cref="GatewayService.OnlineWindow"/>) and when it comes
/// back. Each change is told once: <see cref="Gateway.NotifiedOnline"/> is claimed
/// with a compare-and-set before sending, so however often the sweep runs nobody
/// hears it twice. A crash between the claim and the send loses that notice, the
/// better failure for an advisory one than a duplicate. A gateway's first connection
/// is recorded without a notice: it was never offline. See
/// <c>.design/on-prem-gateway.md</c>, "Online and offline".
/// </summary>
public sealed class GatewayNotifier
{
    /// <summary>The page every gateway notification links to.</summary>
    public const string GatewaysPath = "/admin/administration/gateways";

    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<GatewayNotifier> _logger;

    public GatewayNotifier(AppDbContext db, NotificationService notifications, TimeProvider clock, ILogger<GatewayNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Looks at every approved, unrevoked gateway in the current organisation and
    /// announces each change of online state since the last look. Returns how many
    /// changes were announced. Never throws, except when <paramref name="ct"/> itself
    /// is cancelled.
    /// </summary>
    public async Task<int> NotifyTransitionsAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send gateway online/offline notifications.");
            return 0;
        }
    }

    private async Task<int> SendAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var gateways = await _db.Gateways.AsNoTracking()
            .Where(g => g.RevokedAt == null && g.State == GatewayState.Approved)
            .Select(g => new { g.Id, g.Name, g.MachineName, g.State, g.LastSeenAt, g.RevokedAt, g.NotifiedOnline })
            .ToListAsync(ct).ConfigureAwait(false);

        List<int>? admins = null;
        var announced = 0;
        foreach (var gateway in gateways)
        {
            var online = GatewayService.StatusOf(gateway.State, gateway.LastSeenAt, gateway.RevokedAt, now) == GatewayStatus.Online;
            bool? next = (online, gateway.NotifiedOnline) switch
            {
                (true, not true) => true,
                // Offline only counts once it had been online: a gateway never heard
                // from is still being set up.
                (false, true) => false,
                _ => null,
            };
            if (next is not { } nowOnline) continue;

            var previous = gateway.NotifiedOnline;
            var claimed = await _db.Gateways
                .Where(g => g.Id == gateway.Id && g.NotifiedOnline == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.NotifiedOnline, nowOnline), ct).ConfigureAwait(false);
            if (claimed == 0) continue;
            // First connection: recorded, not announced.
            if (previous is null) continue;

            admins ??= await _db.Users.AsNoTracking()
                .Where(u => u.Role == UserRole.Admin && u.Status == UserStatus.Active)
                .Select(u => u.Id)
                .ToListAsync(ct).ConfigureAwait(false);
            var offline = !nowOnline;
            var lastSeen = gateway.LastSeenAt is { } seen
                ? seen.ToString("d MMM yyyy, HH:mm 'UTC'", CultureInfo.InvariantCulture)
                : "never";
            var title = GatewayStatusEmail.SubjectFor(offline, gateway.Name);
            var detail = offline
                ? $"{gateway.MachineName} was last heard from {lastSeen}. Work for its service tiers fails until it is back."
                : $"{gateway.MachineName} is checking in again.";
            await _notifications.NotifyAsync(new Notification(
                NotificationCategory.Gateways,
                admins,
                new NotificationSummary(title, detail, GatewaysPath, SolutionName: null),
                (email, token) => GatewayStatusEmail.RenderAsync(
                    email.Renderer, email.Recipient.DisplayName, email.OrganizationName, gateway.Name, gateway.MachineName,
                    offline, lastSeen, email.ItemUrl, email.SettingsUrl, token)),
                ct).ConfigureAwait(false);
            announced++;
            _logger.LogInformation("Gateway {GatewayId} is {State}; told {Count} admin(s).",
                gateway.Id, offline ? "offline" : "back online", admins.Count);
        }
        return announced;
    }
}
