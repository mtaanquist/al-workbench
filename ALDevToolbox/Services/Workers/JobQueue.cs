using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// A bounded in-process hand-off from a request (or a scheduler sweep) to a
/// <see cref="QueueDrainWorker{TJob}"/>. A <see cref="Channel{T}"/> — not an external
/// broker — keeps the "no external services" fence of <c>.design/architecture.md</c>
/// intact while giving the worker a back-pressure point.
///
/// <para>
/// This is the plain variant: every enqueue is written through. Use
/// <see cref="JobQueue{TJob, TKey}"/> when repeat work for the same subject should
/// coalesce instead.
/// </para>
/// </summary>
public abstract class JobQueue<TJob>
{
    private readonly Channel<TJob> _channel;

    /// <param name="capacity">
    /// Channel bound. Pick it from the cost of one queued job, not from the expected
    /// arrival rate: a full channel simply makes the writer wait.
    /// </param>
    /// <param name="singleReader">False when the worker drains it with several lanes.</param>
    protected JobQueue(int capacity, bool singleReader = true) =>
        _channel = Channel.CreateBounded<TJob>(new BoundedChannelOptions(capacity)
        {
            SingleReader = singleReader,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<TJob> Reader => _channel.Reader;

    /// <summary>
    /// The write half, for the rare caller that must not block when the channel
    /// is full - a request thread answering an external caller that will retry,
    /// rather than a sweep that can afford to wait.
    /// </summary>
    protected ChannelWriter<TJob> Writer => _channel.Writer;

    /// <summary>Queues <paramref name="job"/>, waiting if the channel is full.</summary>
    public ValueTask EnqueueAsync(TJob job, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(job, ct);
}

/// <summary>
/// A <see cref="JobQueue{TJob}"/> with an in-memory dedupe gate keyed by
/// <typeparamref name="TKey"/>: enqueuing a job whose key is already queued or running
/// is a no-op, so a double-click (or a sweep offering a subject someone just refreshed
/// by hand) coalesces into the run already in flight.
///
/// <para>
/// The gate is in memory only, never persisted — these queues carry disposable work, so
/// a restart drops queued entries rather than stranding a flag. The worker clears the
/// flag in <c>finally</c> via <see cref="Complete"/> regardless of outcome.
/// </para>
/// </summary>
public abstract class JobQueue<TJob, TKey> : JobQueue<TJob>
    where TKey : notnull
{
    private readonly Func<TJob, TKey> _keySelector;
    private readonly ConcurrentDictionary<TKey, byte> _inFlight = new();

    protected JobQueue(int capacity, Func<TJob, TKey> keySelector, bool singleReader = true)
        : base(capacity, singleReader) => _keySelector = keySelector;

    /// <summary>
    /// Queues <paramref name="job"/> only if there is room right now, and unless one with
    /// the same key is already queued or running. For a caller that must never wait on a
    /// full queue because the work is picked up again later anyway. True when enqueued.
    /// </summary>
    public bool TryEnqueue(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = _keySelector(job);
        if (!_inFlight.TryAdd(key, 0)) return false;
        if (Writer.TryWrite(job)) return true;
        _inFlight.TryRemove(key, out _);
        return false;
    }

    /// <summary>
    /// Enqueues <paramref name="job"/> unless one with the same key is already queued or
    /// running. Returns <c>true</c> when enqueued, <c>false</c> when coalesced.
    /// </summary>
    public new async ValueTask<bool> EnqueueAsync(TJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = _keySelector(job);
        // Claim the in-flight slot first so a concurrent enqueue for the same key loses
        // the race and coalesces. Released by Complete() (worker) or here if the write
        // itself fails.
        if (!_inFlight.TryAdd(key, 0)) return false;
        try
        {
            await base.EnqueueAsync(job, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            _inFlight.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>True while a job with this key is queued or running.</summary>
    public bool IsInFlight(TKey key) => _inFlight.ContainsKey(key);

    /// <summary>Clears the in-flight flag once the worker has finished (success or failure).</summary>
    public void Complete(TKey key) => _inFlight.TryRemove(key, out _);
}
