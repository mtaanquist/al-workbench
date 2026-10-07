using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Drains <see cref="DeliveryQueue"/> and runs each BC publish off the request
/// thread. <see cref="Lanes"/> of these wait side by side, as the build workers do, each
/// one delivery at a time and never two to one environment
/// (<see cref="DeliveryQueue.TryEnterEnvironment"/>, #1139), each in its own DI scope
/// under the triggering user's <see cref="AmbientOrganizationScope"/> identity so the
/// org query filter and credential resolution behave as they did in the request.
/// <see cref="DeliveryService"/> captures every failure onto the persisted delivery
/// row, so the base loop's try/catch is only the last-resort net that keeps one bad
/// delivery from killing the worker. See <c>.design/saas-delivery.md</c>
/// ("Services &amp; seams").
/// </summary>
public sealed class DeliveryWorker : QueueDrainWorker<DeliveryJob>
{
    /// <summary>
    /// How many deployments run at once. Most of a deployment is waiting on Business
    /// Central to install, so a few lanes let one environment's evening window not hold
    /// up every other customer's, without hammering the shared app registration.
    /// </summary>
    internal const int Lanes = 4;

    private readonly DeliveryQueue _queue;
    private readonly IServiceProvider _services;

    public DeliveryWorker(
        int slot,
        DeliveryQueue queue,
        IServiceProvider services,
        ILogger<DeliveryWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A single publish uploads a handful of apps and polls install status; 30
        // minutes is generous headroom while still catching a wedged run.
        : base(queue.Reader, logger, heartbeats, $"{nameof(DeliveryWorker)} {slot}", TimeSpan.FromMinutes(30))
    {
        _queue = queue;
        _services = services;
    }

    protected override async Task RunJobAsync(DeliveryJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var deliveries = scope.ServiceProvider.GetRequiredService<DeliveryService>();
        if (await deliveries.RunDeliveryAsync(job.DeliveryId, ct).ConfigureAwait(false))
        {
            // Only when this run claimed it, so a delivery another run already took is
            // never announced twice (#1036). A run cut off by a shutdown has saved its
            // failure, and nothing after the restart reports it, so the notification gets a
            // short grace once shutdown starts, whether it began before or after (#1179).
            // The notifier never throws.
            using var grace = new CancellationTokenSource();
            using var onShutdown = ct.Register(() => grace.CancelAfter(ShutdownNotifyGrace));
            await scope.ServiceProvider.GetRequiredService<Notifications.DeploymentNotifier>()
                .NotifyAsync(job.DeliveryId, grace.Token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// How long a deployment's notification may take once shutdown has started. Short:
    /// the lanes stop one after another, and <c>docker stop</c> kills the process ten
    /// seconds after asking it to stop.
    /// </summary>
    private static readonly TimeSpan ShutdownNotifyGrace = TimeSpan.FromSeconds(3);

    protected override void OnJobFinished(DeliveryJob job) => _queue.Complete(job.DeliveryId);

    protected override string Describe(DeliveryJob job) => $"DeliveryId={job.DeliveryId}";
}
