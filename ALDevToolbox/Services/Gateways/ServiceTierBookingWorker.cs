using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// Sends the service tier restarts (#1788) and the app uploads (#1786) booked for later on
/// on-premises environments when they come due, and tells the person who booked one when
/// it was refused or the gateway failed it. The pending row
/// in Postgres is the queue, as for <see cref="ObjectExplorer.Bc.UpgradeActionWorker"/>, so a
/// booking for tonight survives this afternoon's deploy.
///
/// <para>Each booking runs as the person who booked it, inside the organisation's
/// <see cref="AmbientOrganizationScope"/> with their user id, so the audit row names them
/// and whether they may still manage the Solution is asked again at that moment. Nothing
/// here crosses the tenant fence. Opt out with <c>DISABLE_SERVICE_TIER_BOOKINGS=1</c>;
/// bookings then wait until it is back on, and give up two hours past their time. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".</para>
/// </summary>
public sealed class ServiceTierBookingWorker : PolledScheduler
{
    public const string DisableEnvVar = "DISABLE_SERVICE_TIER_BOOKINGS";

    private readonly IServiceProvider _services;
    private readonly ILogger<ServiceTierBookingWorker> _logger;

    public ServiceTierBookingWorker(IServiceProvider services, ILogger<ServiceTierBookingWorker> logger, WorkerHeartbeatRegistry heartbeats,
        Operations.MaintenanceModeState? maintenance = null)
        : base(logger, heartbeats, nameof(ServiceTierBookingWorker),
            pollInterval: TimeSpan.FromSeconds(30),
            // A tick sends a few commands and marks a few rows; nothing waits on a gateway.
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(3),
            disableEnvVar: DisableEnvVar,
            maintenance: maintenance)
    {
        _services = services;
        _logger = logger;
    }

    protected override Task TickAsync(CancellationToken ct) => SweepAsync(ct);

    /// <summary>One pass over every organisation. Internal so a test can drive it without the loop.</summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        foreach (var (orgId, isSystem) in await SweptOrganizations.ListAsync(_services, ct).ConfigureAwait(false))
        {
            try
            {
                await SweepOrganizationAsync(orgId, isSystem, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Service tier booking sweep failed for organisation {OrganizationId}.", orgId);
            }
        }
    }

    private async Task SweepOrganizationAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        List<(int Id, int? RequestedBy)> restarts;
        List<(int Id, int? RequestedBy)> uploads;
        using (AmbientOrganizationScope.Enter(AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem)))
        await using (var scope = _services.CreateAsyncScope())
        {
            var tiers = scope.ServiceProvider.GetRequiredService<OnPremisesTierActionService>();
            var apps = scope.ServiceProvider.GetRequiredService<OnPremisesAppUploadService>();
            restarts = await tiers.DueBookingsAsync(ct).ConfigureAwait(false);
            uploads = await apps.DueBookingsAsync(ct).ConfigureAwait(false);
            var failedLater = await tiers.SettleFailedBookingsAsync(ct).ConfigureAwait(false);
            failedLater.AddRange(await apps.SettleFailedBookingsAsync(ct).ConfigureAwait(false));
            var notifier = scope.ServiceProvider.GetRequiredService<UpgradeActionNotifier>();
            foreach (var id in failedLater) await notifier.NotifyAsync(id, ct: ct).ConfigureAwait(false);
        }

        await RunEachAsync(orgId, isSystem, restarts, "Booked restart",
            (services, id, token) => services.GetRequiredService<OnPremisesTierActionService>().RunBookingAsync(id, byWorker: true, token), ct).ConfigureAwait(false);
        await RunEachAsync(orgId, isSystem, uploads, "Booked upload",
            (services, id, token) => services.GetRequiredService<OnPremisesAppUploadService>().RunBookingAsync(id, token), ct).ConfigureAwait(false);
    }

    private async Task RunEachAsync(int orgId, bool isSystem, List<(int Id, int? RequestedBy)> due, string what,
        Func<IServiceProvider, int, CancellationToken, Task<ServiceTierBookingRun>> run, CancellationToken ct)
    {
        foreach (var (id, requestedBy) in due)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                // As the person who booked it: the audit row names them, and their right to
                // manage the Solution is asked again now.
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem, requestedBy));
                await using var scope = _services.CreateAsyncScope();
                var outcome = await run(scope.ServiceProvider, id, ct).ConfigureAwait(false);
                if (outcome == ServiceTierBookingRun.Failed)
                {
                    await scope.ServiceProvider.GetRequiredService<UpgradeActionNotifier>().NotifyAsync(id, ct: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "{What} {ActionId} could not be run; it is tried again on the next sweep.", what, id);
            }
        }
    }
}
