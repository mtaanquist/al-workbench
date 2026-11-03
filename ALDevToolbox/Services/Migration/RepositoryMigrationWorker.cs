using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// Drains <see cref="RepositoryMigrationQueue"/> one preview at a time, each in
/// its own DI scope under the identity of the Admin who asked, so their Azure
/// DevOps token is the one used (issue #1699).
/// </summary>
public sealed class RepositoryMigrationWorker : QueueDrainWorker<MigrationPreviewJob>
{
    private readonly RepositoryMigrationQueue _queue;
    private readonly IServiceProvider _services;

    public RepositoryMigrationWorker(
        RepositoryMigrationQueue queue,
        IServiceProvider services,
        ILogger<RepositoryMigrationWorker> logger,
        WorkerHeartbeatRegistry heartbeats,
        Operations.MaintenanceModeState? maintenance = null)
        // Each clone has a 30 minute limit and a preview usually has a handful of
        // repositories; three hours still catches a wedged job.
        : base(queue.Reader, logger, heartbeats, nameof(RepositoryMigrationWorker), TimeSpan.FromHours(3), maintenance)
    {
        _queue = queue;
        _services = services;
    }

    protected override async Task RunJobAsync(MigrationPreviewJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var previewer = scope.ServiceProvider.GetRequiredService<RepositoryMigrationPreviewer>();
        await previewer.RunAsync(job, ct).ConfigureAwait(false);
    }

    // A job that ended without recording an outcome (it threw before it could)
    // must not leave the page showing "running" until the preview is evicted.
    protected override void OnJobFinished(MigrationPreviewJob job) =>
        _queue.FailIfActive(job.Id, "The preview stopped unexpectedly. Try again. If it fails again, ask whoever runs AL Workbench for your company to look into it.");

    protected override void OnJobDropped(MigrationPreviewJob job) =>
        _queue.MarkFailed(job.Id, "The server was restored from a backup while this preview waited. Start it again.");

    protected override string Describe(MigrationPreviewJob job) => $"MigrationPreview={job.Id}";
}
