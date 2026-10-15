using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Follows on-premises deployments once their run has handed them to a gateway command
/// (#1367): every few seconds, per organisation, it copies the command's new progress
/// lines into the deployment's log, which the deployment page re-reads live, and finishes
/// a deployment whose command has ended, through
/// <see cref="DeliveryService.FollowGatewayDeliveriesAsync"/>, then tells the person behind
/// it. A command can run for hours (a large data upgrade), so no
/// <see cref="DeliveryWorker"/> lane waits on it.
///
/// <para>
/// A scheduler rather than a hook in the command service's completion path: progress has
/// to be copied while the command runs, a command can end without the gateway ever
/// reporting (expired, gone quiet, the gateway revoked), and a deployment given up after a
/// day needs a clock. One poll covers all three; the completion path would only cover one.
/// The deployment's state lives on its row, so a restart simply picks up where it was.
/// </para>
///
/// <para>Like <see cref="DeliveryScheduler"/>, it visits one organisation at a time inside
/// that organisation's <see cref="AmbientOrganizationScope"/>, so nothing here crosses the
/// tenant fence. Opt out with <c>DISABLE_GATEWAY_DELIVERY_FOLLOWER=1</c>; on-premises
/// deployments then stay installing until it is back on, and no second deployment to the
/// same service tier starts meanwhile. See <c>.design/on-prem-gateway.md</c>.</para>
/// </summary>
public sealed class GatewayDeliveryFollower : PolledScheduler
{
    public const string DisableEnvVar = "DISABLE_GATEWAY_DELIVERY_FOLLOWER";

    private readonly IServiceProvider _services;
    private readonly ILogger<GatewayDeliveryFollower> _logger;

    public GatewayDeliveryFollower(IServiceProvider services, ILogger<GatewayDeliveryFollower> logger, WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(GatewayDeliveryFollower),
            // Short, because the deployment page shows the gateway's lines as they arrive.
            pollInterval: TimeSpan.FromSeconds(5),
            // A pass reads one command per deployment in flight and appends a few lines.
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(1),
            disableEnvVar: DisableEnvVar)
    {
        _services = services;
        _logger = logger;
    }

    protected override Task TickAsync(CancellationToken ct) => FollowAsync(ct);

    /// <summary>One pass over every organisation. Internal so a test can drive it without the loop.</summary>
    internal async Task FollowAsync(CancellationToken ct)
    {
        foreach (var (orgId, isSystem) in await SweptOrganizations.ListAsync(_services, ct).ConfigureAwait(false))
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                var finished = await scope.ServiceProvider.GetRequiredService<DeliveryService>()
                    .FollowGatewayDeliveriesAsync(ct).ConfigureAwait(false);
                if (finished.Count == 0) continue;
                // The run that queued the command told nobody: it was not over then (#1036).
                var notifier = scope.ServiceProvider.GetRequiredService<Notifications.DeploymentNotifier>();
                foreach (var id in finished)
                {
                    await notifier.NotifyAsync(id, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Following on-premises deployments failed for organisation {OrganizationId}.", orgId);
            }
        }
    }
}
