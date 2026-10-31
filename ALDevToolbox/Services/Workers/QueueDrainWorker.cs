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
/// It also owns the worker's side of a full restore (#1538). While maintenance mode is
/// on it picks up nothing new. Every job it runs is counted on
/// <see cref="MaintenanceModeState.ActiveJobs"/>, which the restore waits on (with a
/// time limit) before it drops anything. A job that was taken off the queue just as
/// the restore began is held until the site reopens; if the restore replaced the
/// database meanwhile (<see cref="MaintenanceModeState.Epoch"/> moved on) it is
/// dropped, because it belongs to the old one. The waits sit outside the heartbeat's
/// active bracket, so a long restore never reads as a stuck job. See
/// <c>.design/deployment.md</c>.
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

    /// <summary>The maintenance flag this worker was given. Internal so a test can check DI handed it on.</summary>
    internal MaintenanceModeState? Maintenance => _maintenance;

    /// <summary>Raised as a job starts being held for maintenance mode. Test seam.</summary>
    internal event Action<TJob>? Holding;

    /// <summary>
    /// Raised as a worker that does not hold jobs runs one uncounted, so its own
    /// policy can defer it, while maintenance mode is on. Test seam.
    /// </summary>
    internal event Action<TJob>? Deferring;

    /// <summary>
    /// False for a worker that applies its own maintenance policy inside
    /// <see cref="RunJobAsync"/> (hold back and let a scheduler offer the job again, or
    /// put it back on the queue), and whose job does not touch the database while
    /// maintenance mode is on. Such a worker keeps picking jobs up during a restore. A
    /// job it started before maintenance mode came on is counted, so the restore waits
    /// for it; one it picks up during maintenance runs uncounted, since all it does is
    /// defer.
    /// </summary>
    protected virtual bool HoldsJobsDuringMaintenance => true;

    /// <param name="maxActiveDuration">
    /// The longest legitimate single job. Queue-driven workers legitimately sit idle
    /// between jobs, so they opt out of the idle-silence check entirely (see
    /// <see cref="WorkerHeartbeat"/>).
    /// </param>
    /// <param name="maintenance">
    /// The process-wide maintenance flag. Required so a new worker has to pass it on;
    /// null only for a worker a test builds by hand.
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
        var holds = _maintenance is not null && HoldsJobsDuringMaintenance;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Pick up nothing new while a restore runs: whatever is queued stays
                // queued, and the queue drops it afterwards if it is from before.
                if (holds) await WaitUntilOpenAsync(stoppingToken).ConfigureAwait(false);
                if (!await _reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false)) return;
                if (holds && _maintenance!.IsActive) continue;
                if (!_reader.TryRead(out var job)) continue;
                await RunOneAsync(job, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunOneAsync(TJob job, CancellationToken stoppingToken)
    {
        var epoch = _maintenance?.Epoch ?? 0;
        var counted = false;
        var active = false;
        try
        {
            OnJobStarting(job);
            if (_maintenance is not null)
            {
                if (HoldsJobsDuringMaintenance)
                {
                    // Taken just as a restore began: it has not touched the database,
                    // so hold it rather than make the restore wait for it.
                    if (!_maintenance.TryBeginJob())
                    {
                        _logger.LogInformation(
                            "{Worker} is holding {Job}: maintenance mode is active.", _name, Describe(job));
                        Holding?.Invoke(job);
                        do
                        {
                            await WaitUntilOpenAsync(stoppingToken).ConfigureAwait(false);
                        }
                        while (!_maintenance.TryBeginJob());
                    }
                    counted = true;
                    if (_maintenance.Epoch != epoch)
                    {
                        _logger.LogWarning(
                            "{Worker} dropped {Job}: a restore replaced the database it was queued against.",
                            _name, Describe(job));
                        return;
                    }
                }
                else if (_maintenance.TryBeginJob())
                {
                    counted = true;
                }
                else
                {
                    // Maintenance mode is on: the job's own policy holds it back or
                    // puts it back on the queue without touching the database, so it
                    // runs uncounted. Counting it would keep the restore waiting on a
                    // job that defers, and requeues, for as long as the site is closed.
                    Deferring?.Invoke(job);
                }
            }
            Heartbeat.BeginActive();
            active = true;
            // A counted job is one the restore waits for, so it may finish its work
            // on the database even once maintenance mode is on (#1571).
            using var allowDatabase = counted ? _maintenance!.AllowDatabase() : null;
            await RunJobAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Jobs capture their own failures on the row or cache they own; this is
            // the last-resort net so one bad job never kills the worker loop.
            _logger.LogError(ex, "{Worker} tripped on {Job}.", _name, Describe(job));
        }
        finally
        {
            if (counted) _maintenance!.EndJob();
            OnJobFinished(job);
            if (active) Heartbeat.EndActive();
        }
    }

    private async Task WaitUntilOpenAsync(CancellationToken ct)
    {
        while (_maintenance!.IsActive)
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
