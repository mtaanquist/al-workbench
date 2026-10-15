using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Drains <see cref="EnvironmentRefreshQueue"/> and re-reads each project's Business
/// Central environments off the request thread, re-mirroring the next-platform-update
/// columns the fleet page lists.
///
/// <para>
/// One project at a time (the channel is single-reader). Each job runs in its own DI
/// scope under the job's <see cref="AmbientOrganizationScope"/> identity so the EF query
/// filter behaves exactly as it would in a request, and goes through the unattended
/// refresh path — a sweep nobody asked for must not present itself as the consultant's
/// own "Test connection" result. The in-flight flag is cleared in <c>finally</c>
/// regardless of outcome; a project whose credentials have gone stale simply logs and
/// leaves the previous mirror in place.
/// </para>
/// </summary>
public sealed class EnvironmentRefreshWorker : QueueDrainWorker<EnvironmentRefreshJob>
{
    private readonly EnvironmentRefreshQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<EnvironmentRefreshWorker> _logger;

    public EnvironmentRefreshWorker(
        EnvironmentRefreshQueue queue,
        IServiceProvider services,
        ILogger<EnvironmentRefreshWorker> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so a worker built by hand keeps compiling; production DI always
        // supplies the singleton.
        Operations.MaintenanceModeState? maintenance = null)
        // One project is a token call plus two admin-center calls per environment, so 15
        // minutes is ample even for a customer with many sandboxes while still catching a
        // wedged job.
        : base(queue.Reader, logger, heartbeats, nameof(EnvironmentRefreshWorker), TimeSpan.FromMinutes(15))
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        _maintenance = maintenance;
    }

    private readonly Operations.MaintenanceModeState? _maintenance;

    /// <summary>
    /// A breath between one customer and the next. The sweep is a run of back-to-back
    /// requests that nobody is waiting on, so it costs nothing to be unhurried: a second
    /// per customer is under two minutes across a hundred of them. Settable for tests.
    /// </summary>
    internal TimeSpan PauseBetweenSolutions { get; set; } = TimeSpan.FromSeconds(1);

    // The run being drained: how many solutions, how many Business Central requests
    // (a token and the list, then the two storage reads and three per environment - see
    // MirrorBcEnvironmentDetailsAsync),
    // and since when. Logged when the queue empties, so how long a night's sweep really
    // takes is a line in the log rather than an estimate.
    private int _runSolutions;
    private int _runRequests;
    private long _runStarted;

    protected override async Task RunJobAsync(EnvironmentRefreshJob job, CancellationToken ct)
    {
        // A queued job is dropped while a restore runs or a failed one holds the
        // site closed (#1321): it only refreshes a mirror, which the next run
        // refreshes again, and the rows it would write to may not be there.
        if (_maintenance?.IsActive == true)
        {
            _logger.LogInformation("Skipping {Job}: maintenance mode is active.", Describe(job));
            return;
        }

        if (_runSolutions == 0) _runStarted = System.Diagnostics.Stopwatch.GetTimestamp();

        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<ProjectConnectionService>();
        var result = await connections.RefreshEnvironmentsUnattendedAsync(job.ProjectId, ct).ConfigureAwait(false);
        _runSolutions++;
        _runRequests += 2 + (result.IsSuccess ? 2 + 3 * result.EnvironmentCount : 0);
        if (result.IsSuccess)
        {
            _logger.LogInformation(
                "Refreshed Business Central environments for project {ProjectId}: {Count} environment(s).",
                job.ProjectId, result.EnvironmentCount);
        }
        else
        {
            // Not an error on our side: a customer's credentials expire, GDAP lapses. The
            // previous mirror and its age stay visible, which is what the page shows.
            _logger.LogWarning(
                "Couldn't refresh Business Central environments for project {ProjectId}: {Message}",
                job.ProjectId, result.Message);
        }

        if (_queue.Reader.Count == 0)
        {
            _logger.LogInformation(
                "Environment refresh run finished: {Solutions} solution(s), about {Requests} Business Central request(s), in {Elapsed}.",
                _runSolutions, _runRequests, System.Diagnostics.Stopwatch.GetElapsedTime(_runStarted));
            _runSolutions = 0;
            _runRequests = 0;
        }
        else if (PauseBetweenSolutions > TimeSpan.Zero)
        {
            await Task.Delay(PauseBetweenSolutions, ct).ConfigureAwait(false);
        }
    }

    protected override void OnJobFinished(EnvironmentRefreshJob job) => _queue.Complete(job.ProjectId);

    protected override string Describe(EnvironmentRefreshJob job) => $"ProjectId={job.ProjectId}";
}
