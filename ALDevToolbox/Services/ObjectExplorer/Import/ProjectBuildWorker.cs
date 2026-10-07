using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// One of the workers that run project and pull request builds from
/// <see cref="ProjectBuildQueue"/>. <see cref="ProjectBuildQueue.MaxConcurrency"/> of
/// them wait side by side (#1137) and the queue's limit decides how many build at once
/// (#1164), each one build at a time, with the same job code
/// <see cref="ReleaseImportWorker"/> runs for imports. Each has a heartbeat of its own,
/// so one stuck build shows as one stuck worker.
/// </summary>
public sealed class ProjectBuildWorker : ReleaseImportWorker
{
    private readonly ProjectBuildQueue _queue;

    public ProjectBuildWorker(
        int slot,
        ProjectBuildQueue queue,
        IServiceProvider services,
        ILogger<ReleaseImportWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(queue.Reader, services, logger, heartbeats, $"{nameof(ProjectBuildWorker)} {slot}")
    {
        _queue = queue;
    }

    protected override void OnJobFinished(ReleaseImportJob job) => _queue.Complete(job);
}
