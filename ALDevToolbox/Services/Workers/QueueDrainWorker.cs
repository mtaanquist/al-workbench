using System.Threading.Channels;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// Drains a <see cref="JobQueue{TJob}"/> one job at a time and runs each job off the
/// request thread; work that runs side by side registers several of these over one
/// multi-reader queue (the build and delivery workers). Subclasses supply only
/// <see cref="RunJobAsync"/>; this base owns the loop, the heartbeat bracket, the
/// last-resort try/catch that keeps one bad job from killing the worker, and the
/// in-flight release in <c>finally</c>.
///
/// <para>
/// It also owns the maintenance gate: a job picked up while a full restore runs, or
/// while a failed one holds the site closed, waits until the site reopens and then runs
/// (#1538). It is held, not dropped, because not every queue has a scheduler that would
/// offer the job again. The wait sits outside the heartbeat's active bracket, so a long
/// restore never reads as a stuck job. See <c>.design/deployment.md</c>.
/// </para>
/// </summary>
public abstract class QueueDrainWorker<TJob> : BackgroundService
{
    private readonly ChannelReader<TJob> _reader;
    private readonly ILogger _logger;
    private readonly string _name;
    private readonly MaintenanceModeState? _maintenance;

    /// <summary>How often a held job looks again at whether maintenance mode is over.</summary>
    internal static readonly TimeSpan MaintenancePollInterval = TimeSpan.FromSeconds(1);

    protected WorkerHeartbeat Heartbeat { get; }

    /// <param name="maxActiveDuration">
    /// The longest legitimate single job. Queue-driven workers legitimately sit idle
    /// between jobs, so they opt out of the idle-silence check entirely (see
    /// <see cref="WorkerHeartbeat"/>).
    /// </param>
    /// <param name="maintenance">
    /// The process-wide maintenance flag; jobs wait while it is on. Required so a new
    /// worker has to pass it on; null only for a worker a test builds by hand.
    /// </param>
    protected QueueDrainWorker(
        ChannelReader<TJob> reader,
        ILogger logger,
        WorkerHeartbeatRegistry heartbeats,
        string name,
        TimeSpan maxActiveDuration,
        MaintenanceModeState? maintenance)
    {
        _maintenance = maintenance;
        _reader = reader;
        _logger = logger;
        _name = name;
        Heartbeat = heartbeats.Register(name, maxActiveDuration, maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            var active = false;
            try
            {
                // Marked started before the gate, so a restore that turns maintenance
                // on and then counts what is running either sees this job or holds it.
                OnJobStarting(job);
                await WaitWhileMaintenanceAsync(job, stoppingToken).ConfigureAwait(false);
                Heartbeat.BeginActive();
                active = true;
                await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Jobs capture their own failures on the row or cache they own; this is
                // the last-resort net so one bad job never kills the worker loop.
                _logger.LogError(ex, "{Worker} tripped on {Job}.", _name, Describe(job));
            }
            finally
            {
                OnJobFinished(job);
                if (active) Heartbeat.EndActive();
            }
        }
    }

    private async Task WaitWhileMaintenanceAsync(TJob job, CancellationToken ct)
    {
        if (_maintenance?.IsActive != true) return;
        _logger.LogInformation(
            "{Worker} is holding {Job}: maintenance mode is active. It runs once the site reopens.",
            _name, Describe(job));
        while (_maintenance.IsActive)
        {
            await Task.Delay(MaintenancePollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one job. Implementations enter the job's org scope and its own DI scope.</summary>
    protected abstract Task RunJobAsync(TJob job, CancellationToken ct);

    /// <summary>
    /// Called as each job is picked up, before <see cref="RunJobAsync"/>. A queue that
    /// re-runs a key requested while it ran marks it running here.
    /// </summary>
    protected virtual void OnJobStarting(TJob job) { }

    /// <summary>
    /// Called in <c>finally</c> after every job, success or failure. Queues with a
    /// dedupe gate release it here.
    /// </summary>
    protected virtual void OnJobFinished(TJob job) { }

    /// <summary>How the job is named in the failure log. Defaults to the record's own text.</summary>
    protected virtual string Describe(TJob job) => job?.ToString() ?? "(null)";
}
