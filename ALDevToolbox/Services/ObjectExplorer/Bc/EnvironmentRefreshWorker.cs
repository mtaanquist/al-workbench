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
        // supplies the singletons.
        Operations.MaintenanceModeState? maintenance = null,
        TimeProvider? clock = null)
        // One project is a token call plus two admin-center calls per environment, so 15
        // minutes is ample even for a customer with many sandboxes while still catching a
        // wedged job.
        : base(queue.Reader, logger, heartbeats, nameof(EnvironmentRefreshWorker), TimeSpan.FromMinutes(15), maintenance)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        _maintenance = maintenance;
        _clock = clock ?? TimeProvider.System;
    }

    private readonly Operations.MaintenanceModeState? _maintenance;
    private readonly TimeProvider _clock;

    /// <summary>
    /// This worker applies its own maintenance policy in <see cref="RunJobAsync"/>
    /// (#1321) without touching the database, so the base does not hold its jobs. A
    /// job it started while the site was open is counted, so a restore waits for it
    /// to finish; one it picks up during maintenance only defers and is not (#1538).
    /// </summary>
    protected override bool HoldsJobsDuringMaintenance => false;

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
    private int _runThrottleBreaths;

    /// <summary>The longest a throttled customer is left out of the scheduled refresh.</summary>
    internal static readonly TimeSpan MaxDeferral = TimeSpan.FromDays(1);

    /// <summary>How many longer breaths after a throttled customer one run takes, at most.</summary>
    internal const int MaxThrottleBreathsPerRun = 5;

    private int _runRequests;
    private long _runStarted;

    /// <summary>How many solutions the run being drained has counted so far. For tests.</summary>
    internal int SolutionsInRun => _runSolutions;

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
        _runSolutions++;

        // The run's summary and reset go in the finally: a job that throws (a solution
        // deleted since it was queued, a database error mid-mirror) must not carry the
        // counters into the next run, which would log the wrong totals and start with
        // fewer throttle breaths.
        TimeSpan pause;
        try
        {
            pause = await RefreshOneAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_queue.Reader.Count == 0)
            {
                _logger.LogInformation(
                    "Environment refresh run finished: {Solutions} solution(s), about {Requests} Business Central request(s), in {Elapsed}.",
                    _runSolutions, _runRequests, System.Diagnostics.Stopwatch.GetElapsedTime(_runStarted));
                _runSolutions = 0;
                _runRequests = 0;
                _runThrottleBreaths = 0;
            }
        }

        if (_queue.Reader.Count > 0 && pause > TimeSpan.Zero)
        {
            await Task.Delay(pause, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Refreshes one solution and returns how long to wait before the next.</summary>
    private async Task<TimeSpan> RefreshOneAsync(EnvironmentRefreshJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var connections = scope.ServiceProvider.GetRequiredService<ProjectConnectionService>();
        var result = await connections.RefreshEnvironmentsUnattendedAsync(job.ProjectId, ct).ConfigureAwait(false);
        _runRequests += 2 + (result.IsSuccess ? 2 + 3 * result.EnvironmentCount : 0);

        // Business Central asked us to slow down for this customer. The scheduled sweep
        // leaves them alone until the wait has passed, however often it runs.
        var pause = PauseBetweenSolutions;
        if (result.RetryAfter is { } asked)
        {
            // A day at most: a nonsense Retry-After must not switch a customer off until
            // the next restart.
            var wait = asked > MaxDeferral ? MaxDeferral : asked;
            _queue.DeferUntil(job.ProjectId, _clock.GetUtcNow().UtcDateTime + wait);
            _logger.LogWarning(
                "Business Central asked us to wait {Wait} before asking about project {ProjectId} again; the scheduled refresh skips it until then.",
                wait, job.ProjectId);
            // And a longer breath before the next customer, in case the limit is on our
            // side of the conversation (one app registration serves many customers)
            // rather than theirs. Bounded, so one customer cannot hold up the rest.
            // A few such breaths per run at most, so a registration throttled across the
            // board costs minutes, not one per customer, before the morning jobs.
            if (_runThrottleBreaths < MaxThrottleBreathsPerRun)
            {
                _runThrottleBreaths++;
                var breath = wait < BcThrottleHandler.MaxWait ? wait : BcThrottleHandler.MaxWait;
                if (breath > pause) pause = breath;
            }
        }
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
        return pause;
    }

    protected override void OnJobFinished(EnvironmentRefreshJob job) => _queue.Complete(job.ProjectId);

    protected override string Describe(EnvironmentRefreshJob job) => $"ProjectId={job.ProjectId}";
}
