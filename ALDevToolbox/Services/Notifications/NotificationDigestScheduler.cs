using ALDevToolbox.Data;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Hosted service that sends the daily and weekly notification digests
/// (issue #1037). Polls every quarter of an hour and lets
/// <see cref="NotificationDigestService"/> send whatever is past its cut-off,
/// org by org inside each org's <see cref="AmbientOrganizationScope"/>; the
/// service keeps no state, so a restart or a missed hour only delays a digest.
/// Opt out with <c>DISABLE_NOTIFICATION_DIGEST_SCHEDULER=1</c>. See
/// <c>.design/notifications.md</c>.
/// </summary>
public sealed class NotificationDigestScheduler : PolledScheduler
{
    private readonly IServiceProvider _services;
    private readonly ILogger<NotificationDigestScheduler> _logger;

    public NotificationDigestScheduler(
        IServiceProvider services,
        ILogger<NotificationDigestScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A digest is rendered and queued, not sent, so a sweep is quick.
        : base(logger, heartbeats, nameof(NotificationDigestScheduler),
            pollInterval: TimeSpan.FromMinutes(15),
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(45),
            disableEnvVar: "DISABLE_NOTIFICATION_DIGEST_SCHEDULER")
    {
        _services = services;
        _logger = logger;
    }

    protected override Task TickAsync(CancellationToken ct) => SweepAsync(ct);

    /// <summary>One pass over every active org. Internal so a test can drive it. Returns how many digests were queued.</summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Which orgs to sweep; the organisations table carries no tenant filter.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var sent = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                sent += await scope.ServiceProvider.GetRequiredService<NotificationDigestService>()
                    .SendDueAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "NotificationDigestScheduler sweep failed for org {OrgId}.", orgId);
            }
        }
        return sent;
    }
}
