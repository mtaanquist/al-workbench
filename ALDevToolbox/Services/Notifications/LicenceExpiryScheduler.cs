using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Sends the licence expiry notices for on-premises environments (#1369), once an hour
/// per organisation, through <see cref="LicenceExpiryNotifier"/>. Visits one organisation
/// at a time inside its <see cref="AmbientOrganizationScope"/>, like
/// <see cref="Gateways.GatewayCommandSweep"/>, so nothing crosses the tenant fence. Opt
/// out with <c>DISABLE_LICENCE_EXPIRY_NOTICES=1</c>; the page still shows the expiry.
/// See <c>.design/on-prem-gateway.md</c>, "Licences, as built".
/// </summary>
public sealed class LicenceExpiryScheduler : PolledScheduler
{
    public const string DisableEnvVar = "DISABLE_LICENCE_EXPIRY_NOTICES";

    private readonly IServiceProvider _services;
    private readonly ILogger<LicenceExpiryScheduler> _logger;

    public LicenceExpiryScheduler(IServiceProvider services, ILogger<LicenceExpiryScheduler> logger, WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(LicenceExpiryScheduler),
            pollInterval: TimeSpan.FromHours(1),
            // One read per organisation and a notification now and then.
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromHours(2),
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
                await scope.ServiceProvider.GetRequiredService<LicenceExpiryNotifier>().NotifyDueAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Licence expiry notices failed for organisation {OrganizationId}.", orgId);
            }
        }
    }
}
