using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Hosted service that enqueues <em>scheduled</em> deliveries when their time comes —
/// the time-based half of SaaS delivery (the immediate "Deploy now" path enqueues
/// straight from the request). Mirrors <see cref="ReleaseAutoImportScheduler"/>: poll
/// on a short interval, enumerate active orgs, and do the per-org work inside that org's
/// <see cref="AmbientOrganizationScope"/> so the EF query filter behaves exactly as in a
/// request. The <em>only</em> cross-org read is the active-org enumeration, which needs
/// no bypass because the organisations table carries no tenant filter; the due-delivery
/// query and the publish itself stay org-scoped — no <c>IgnoreQueryFilters()</c> on
/// <c>oe_project_deliveries</c>, per the design's tenant-isolation fence.
///
/// <para>
/// Restart-resume falls out for free: a scheduled row survives a restart and is picked
/// up on the next due sweep. A delivery left mid-publish when the process died is
/// reconciled to <c>failed</c> on the first sweep per org that gets through it. Only rows
/// claimed before this process started count: <see cref="DeliveryWorker"/> is already
/// draining by the first sweep, and a deployment it claimed since is running, not
/// orphaned (#1114).
/// Opt out with <c>DISABLE_DELIVERY_SCHEDULER=1</c>. See <c>.design/saas-delivery.md</c>.
/// </para>
/// </summary>
public sealed class DeliveryScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<DeliveryScheduler> _logger;

    // When this process started: a delivery claimed before then was orphaned by the restart.
    private readonly DateTime _startedAtUtc;

    // Orgs whose orphaned deliveries have been failed. An org whose check threw is tried
    // again on the next sweep rather than left with deliveries stuck in progress.
    private readonly HashSet<int> _reconciledOrgs = new();

    public DeliveryScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<DeliveryScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // Polls every 30s; a sweep only resolves + enqueues (the publish runs on
        // DeliveryWorker), so a 10-minute active ceiling is ample even for many orgs.
        : base(logger, heartbeats, nameof(DeliveryScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_DELIVERY_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        // Constructed while the host is built, before any worker drains a job.
        _startedAtUtc = clock.GetUtcNow().UtcDateTime;
    }

    protected override Task TickAsync(CancellationToken ct) => SweepAsync(ct);

    /// <summary>
    /// One pass over every active org. Internal so a test can drive it directly against a
    /// seeded database without the <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;

        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one sanctioned cross-org read: which orgs to sweep. Only pending
            // signups are skipped. Unlike ReleaseAutoImportScheduler we do NOT skip the
            // system org: in single-tenant (and fresh bootstrap-admin) deployments the
            // working org IS the system org, so its scheduled deliveries must run.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                var deliveries = scope.ServiceProvider.GetRequiredService<DeliveryService>();

                if (!_reconciledOrgs.Contains(orgId))
                {
                    var interrupted = await deliveries.FailInterruptedDeliveriesAsync(_startedAtUtc, ct).ConfigureAwait(false);
                    _reconciledOrgs.Add(orgId);
                    // A deployment cut off by a restart failed, and the person behind it
                    // needs to hear that as much as any other failure (#1036).
                    var notifier = scope.ServiceProvider.GetRequiredService<Notifications.DeploymentNotifier>();
                    foreach (var id in interrupted)
                    {
                        await notifier.NotifyAsync(id, ct).ConfigureAwait(false);
                    }
                }

                var enqueued = await deliveries.EnqueueDueDeliveriesAsync(nowUtc, ct).ConfigureAwait(false);
                if (enqueued > 0)
                {
                    _logger.LogInformation("DeliveryScheduler enqueued {Count} due delivery(ies) for org {OrgId}.", enqueued, orgId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DeliveryScheduler sweep failed for org {OrgId}.", orgId);
            }
        }
    }
}
