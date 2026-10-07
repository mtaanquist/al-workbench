using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Drains <see cref="ProjectDiscoveryQueue"/> and warms each project's
/// discovered-extensions cache off the request thread, so the pipeline editor's
/// checklist appears instantly from cache instead of cloning on every open.
/// Discovery only writes a denormalised cache on the project row, and a failure
/// leaves the prior good list intact.
///
/// <para>
/// One project at a time (the channel is single-reader). Each job runs in its own
/// DI scope under the requesting user's <see cref="AmbientOrganizationScope"/>
/// identity so the EF query filter and the repo-token lookup behave exactly as
/// they would in the original request. The in-flight flag is cleared in
/// <c>finally</c> regardless of outcome.
/// </para>
/// </summary>
public sealed class ProjectDiscoveryWorker : QueueDrainWorker<ProjectDiscoveryJob>
{
    private readonly ProjectDiscoveryQueue _queue;
    private readonly IServiceProvider _services;

    public ProjectDiscoveryWorker(
        ProjectDiscoveryQueue queue,
        IServiceProvider services,
        ILogger<ProjectDiscoveryWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A single discovery clones only trees + app.json blobs and is bounded by
        // DiscoveryCloneTimeout (3 min) per repo; 15 minutes leaves ample margin for a
        // many-repo project while still catching a wedged job.
        : base(queue.Reader, logger, heartbeats, nameof(ProjectDiscoveryWorker), TimeSpan.FromMinutes(15))
    {
        _queue = queue;
        _services = services;
    }

    protected override async Task RunJobAsync(ProjectDiscoveryJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var buildService = scope.ServiceProvider.GetRequiredService<ProjectBuildService>();
        await buildService.DiscoverExtensionsForCacheAsync(job.ProjectId, ct).ConfigureAwait(false);
        // A one-extension pipeline is named after its extension, so a rename that
        // discovery just picked up carries through to the pipelines' names (#1135).
        var pipelines = scope.ServiceProvider.GetRequiredService<PipelineService>();
        await pipelines.RefreshGeneratedNamesAsync(job.ProjectId, ct).ConfigureAwait(false);
    }

    protected override void OnJobFinished(ProjectDiscoveryJob job) => _queue.Complete(job.ProjectId);

    protected override string Describe(ProjectDiscoveryJob job) => $"ProjectId={job.ProjectId}";
}
