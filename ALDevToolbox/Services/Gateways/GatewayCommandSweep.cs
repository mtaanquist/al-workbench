using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// The on-premises gateway sweep, every 30 seconds per organisation: expires queued
/// commands nobody collected by their claim deadline ("The gateway was offline"),
/// fails claimed or running ones nobody has reported on for ten minutes, clears
/// spent request tokens, and tells the organisation's Admins when a gateway goes
/// offline or comes back.
///
/// <para>Like <see cref="ObjectExplorer.Delivery.DeliveryScheduler"/>, it visits one
/// organisation at a time inside that organisation's
/// <see cref="AmbientOrganizationScope"/>, so every read and write behaves exactly as
/// in a request and nothing here crosses the tenant fence. Opt out with
/// <c>DISABLE_GATEWAY_COMMAND_SWEEP=1</c>; commands then wait past their deadline
/// until it is back on. See <c>.design/on-prem-gateway.md</c>, "Commands".</para>
/// </summary>
public sealed class GatewayCommandSweep : PolledScheduler
{
    public const string DisableEnvVar = "DISABLE_GATEWAY_COMMAND_SWEEP";

    private readonly IServiceProvider _services;
    private readonly ILogger<GatewayCommandSweep> _logger;

    public GatewayCommandSweep(IServiceProvider services, ILogger<GatewayCommandSweep> logger, WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(GatewayCommandSweep),
            pollInterval: TimeSpan.FromSeconds(30),
            // A tick is a handful of updates per organisation, and a few notifications.
            maxActiveDuration: TimeSpan.FromMinutes(5),
            maxIdleSilence: TimeSpan.FromMinutes(3),
            disableEnvVar: DisableEnvVar)
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
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<GatewayCommandService>().SweepAsync(ct).ConfigureAwait(false);
                await scope.ServiceProvider.GetRequiredService<GatewayNotifier>().NotifyTransitionsAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Gateway command sweep failed for organisation {OrganizationId}.", orgId);
            }
        }
    }
}
