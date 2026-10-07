using System.Threading.Channels;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// Drains a <see cref="JobQueue{TJob}"/> one job at a time and runs each job off the
/// request thread; work that runs side by side registers several of these over one
/// multi-reader queue (the build and delivery workers). Subclasses supply only
/// <see cref="RunJobAsync"/>; this base owns the loop, the heartbeat bracket, the
/// last-resort try/catch that keeps one bad job from killing the worker, and the
/// in-flight release in <c>finally</c>.
/// </summary>
public abstract class QueueDrainWorker<TJob> : BackgroundService
{
    private readonly ChannelReader<TJob> _reader;
    private readonly ILogger _logger;
    private readonly string _name;

    protected WorkerHeartbeat Heartbeat { get; }

    /// <param name="maxActiveDuration">
    /// The longest legitimate single job. Queue-driven workers legitimately sit idle
    /// between jobs, so they opt out of the idle-silence check entirely (see
    /// <see cref="WorkerHeartbeat"/>).
    /// </param>
    protected QueueDrainWorker(
        ChannelReader<TJob> reader,
        ILogger logger,
        WorkerHeartbeatRegistry heartbeats,
        string name,
        TimeSpan maxActiveDuration)
    {
        _reader = reader;
        _logger = logger;
        _name = name;
        Heartbeat = heartbeats.Register(name, maxActiveDuration, maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            Heartbeat.BeginActive();
            try
            {
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
                Heartbeat.EndActive();
            }
        }
    }

    /// <summary>Runs one job. Implementations enter the job's org scope and its own DI scope.</summary>
    protected abstract Task RunJobAsync(TJob job, CancellationToken ct);

    /// <summary>
    /// Called in <c>finally</c> after every job, success or failure. Queues with a
    /// dedupe gate release it here.
    /// </summary>
    protected virtual void OnJobFinished(TJob job) { }

    /// <summary>How the job is named in the failure log. Defaults to the record's own text.</summary>
    protected virtual string Describe(TJob job) => job?.ToString() ?? "(null)";
}
