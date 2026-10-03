using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Drains <see cref="DeliveryQueue"/> and runs each BC publish off the request
/// thread. One delivery at a time (single-reader channel), each in its own DI scope
/// under the triggering user's <see cref="AmbientOrganizationScope"/> identity so the
/// org query filter and credential resolution behave as they did in the request.
/// <see cref="DeliveryService"/> captures every failure onto the persisted delivery
/// row, so the base loop's try/catch is only the last-resort net that keeps one bad
/// delivery from killing the worker. See <c>.design/saas-delivery.md</c>
/// ("Services &amp; seams").
/// </summary>
public sealed class DeliveryWorker : QueueDrainWorker<DeliveryJob>
{
    private readonly DeliveryQueue _queue;
    private readonly IServiceProvider _services;

    public DeliveryWorker(
        DeliveryQueue queue,
        IServiceProvider services,
        ILogger<DeliveryWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A single publish uploads a handful of apps and polls install status; 30
        // minutes is generous headroom while still catching a wedged run.
        : base(queue.Reader, logger, heartbeats, nameof(DeliveryWorker), TimeSpan.FromMinutes(30))
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
            // never announced twice (#1036). The notifier never throws.
            await scope.ServiceProvider.GetRequiredService<Notifications.DeploymentNotifier>()
                .FinishedAsync(job.DeliveryId, ct).ConfigureAwait(false);
        }
    }

    protected override void OnJobFinished(DeliveryJob job) => _queue.Complete(job.DeliveryId);

    protected override string Describe(DeliveryJob job) => $"DeliveryId={job.DeliveryId}";
}
