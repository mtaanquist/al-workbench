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
///
/// <para>
/// A queue built with <c>rerunIfRequestedWhileRunning</c> treats a request that arrives
/// while its key is <em>running</em> (the worker called <see cref="Start"/>) differently:
/// the running job may already have read the inputs the request is about, so instead of
/// being dropped it is remembered and queued once the running job completes. Several
/// such requests still collapse into one re-run (the latest). A request for a key that is
/// only queued coalesces as before, since the queued job has not read anything yet.
/// </para>
/// </summary>
public abstract class JobQueue<TJob, TKey> : JobQueue<TJob>
    where TKey : notnull
{
    private readonly Func<TJob, TKey> _keySelector;
    private readonly bool _rerunIfRequestedWhileRunning;
    private readonly Lock _gate = new();
    private readonly Dictionary<TKey, Slot> _inFlight = new();

    private sealed class Slot
    {
        public bool Running;
        public bool HasRerun;
        public TJob? Rerun;
    }

    protected JobQueue(int capacity, Func<TJob, TKey> keySelector, bool singleReader = true, bool rerunIfRequestedWhileRunning = false)
        : base(capacity, singleReader)
    {
        _keySelector = keySelector;
        _rerunIfRequestedWhileRunning = rerunIfRequestedWhileRunning;
    }

    /// <summary>
    /// Claims the key's in-flight slot. False when one is already queued or running; on
    /// a re-running queue, a request for a running key is kept for after it.
    /// </summary>
    private bool TryClaim(TJob job, TKey key)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var slot))
            {
                if (_rerunIfRequestedWhileRunning && slot.Running)
                {
                    slot.HasRerun = true;
                    slot.Rerun = job;
                }
                return false;
            }
            _inFlight[key] = new Slot();
            return true;
        }
    }

    private void Release(TKey key)
    {
        lock (_gate) _inFlight.Remove(key);
    }

    /// <summary>
    /// Queues <paramref name="job"/> only if there is room right now, and unless one with
    /// the same key is already queued or running. For a caller that must never wait on a
    /// full queue because the work is picked up again later anyway. True when enqueued.
    /// </summary>
    public bool TryEnqueue(TJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = _keySelector(job);
        if (!TryClaim(job, key)) return false;
        if (Writer.TryWrite(job)) return true;
        Release(key);
        return false;
    }

    /// <summary>
    /// Enqueues <paramref name="job"/> unless one with the same key is already queued or
    /// running. Returns <c>true</c> when enqueued, <c>false</c> when coalesced (or, on a
    /// re-running queue, held for after the running job).
    /// </summary>
    public new async ValueTask<bool> EnqueueAsync(TJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        var key = _keySelector(job);
        // Claim the in-flight slot first so a concurrent enqueue for the same key loses
        // the race and coalesces. Released by Complete() (worker) or here if the write
        // itself fails.
        if (!TryClaim(job, key)) return false;
        try
        {
            await base.EnqueueAsync(job, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            Release(key);
            throw;
        }
    }

    /// <summary>True while a job with this key is queued or running.</summary>
    public bool IsInFlight(TKey key)
    {
        lock (_gate) return _inFlight.ContainsKey(key);
    }

    /// <summary>
    /// Marks the key's job as running: the worker calls this as it picks the job up, so a
    /// re-running queue knows a later request must not be folded into it.
    /// </summary>
    public void Start(TKey key)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(key, out var slot)) slot.Running = true;
        }
    }

    /// <summary>
    /// Clears the in-flight flag once the worker has finished (success or failure), or,
    /// when a request arrived while the job ran, queues that request in its place.
    /// </summary>
    public void Complete(TKey key)
    {
        TJob rerun;
        lock (_gate)
        {
            if (!_inFlight.TryGetValue(key, out var slot)) return;
            if (!slot.HasRerun)
            {
                _inFlight.Remove(key);
                return;
            }
            rerun = slot.Rerun!;
            _inFlight[key] = new Slot();
        }
        // Never waits: a full queue drops the re-run as it would any request that found
        // no room, and the gate reopens for the next one.
        if (!Writer.TryWrite(rerun)) Release(key);
    }
}
