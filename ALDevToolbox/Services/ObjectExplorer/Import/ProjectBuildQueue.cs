using System.Threading.Channels;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// The waiting line for project and pull request builds, drained by
/// <see cref="ProjectBuildWorker"/>s running side by side (#1137). Release imports keep
/// their own single-worker <see cref="ReleaseImportQueue"/>, so a DVD import and a
/// build never wait on each other.
///
/// <para>
/// Unlike <see cref="ReleaseImportQueue"/> it is unbounded and never makes a writer
/// wait. What bounds it is the build rows behind it: one manual build per pipeline,
/// five waiting push builds, one preview check per target. A writer that waited could
/// be a request, the GitHub webhook worker or the startup reconcile, and none of them
/// may stall on a busy build machine (#1107, #1121).
/// </para>
///
/// <para>
/// A worker takes the waiting build with the best <see cref="ProjectBuildOrder.Priority"/>,
/// oldest first among equals, so a person waiting is never behind a night of preview
/// checks. Builds that share a <see cref="ProjectBuildOrder.SerialKey"/> run one at a
/// time, in the order they were queued: two builds of one pipeline finishing out of
/// order would publish and prepare deployments out of order. The key lives in memory
/// only, which is safe because the app runs as one instance.
/// </para>
///
/// <para>
/// <see cref="MaxConcurrency"/> workers always drain it; <see cref="Limit"/> is what
/// decides how many builds run at once. A site admin changes it without a restart
/// (#1164): raising it wakes the waiting workers, lowering it lets the running builds
/// finish and holds back new ones until fewer than the new limit are running.
/// </para>
/// </summary>
public sealed class ProjectBuildQueue
{
    /// <summary>Builds that run at once when neither the site setting nor <c>OE_BUILD_CONCURRENCY</c> is set.</summary>
    public const int DefaultConcurrency = 2;

    /// <summary>
    /// The most builds that may run at once, whatever the site setting or
    /// <c>OE_BUILD_CONCURRENCY</c> says. Also the number of workers registered, so
    /// any limit up to it can take effect without a restart.
    /// </summary>
    public const int MaxConcurrency = 16;

    /// <summary>The fewest builds that may run at once.</summary>
    public const int MinConcurrency = 1;

    private readonly object _lock = new();
    private readonly List<Waiting> _waiting = [];
    private readonly Dictionary<int, string?> _runningByRelease = [];
    private readonly HashSet<string> _runningKeys = [];
    private TaskCompletionSource _changed = NewSignal();
    private long _sequence;
    private int _limit;

    /// <summary>
    /// A queue whose only limit is <see cref="MaxConcurrency"/>, for tests and callers
    /// that never start workers. The app registers one with
    /// <see cref="ProjectBuildQueue(int)"/>.
    /// </summary>
    public ProjectBuildQueue() : this(MaxConcurrency) { }

    /// <param name="defaultLimit">
    /// The limit used until a site admin saves one, and again when they clear it:
    /// <see cref="Concurrency"/> of <c>OE_BUILD_CONCURRENCY</c>.
    /// </param>
    public ProjectBuildQueue(int defaultLimit)
    {
        DefaultLimit = Math.Clamp(defaultLimit, MinConcurrency, MaxConcurrency);
        _limit = DefaultLimit;
        Reader = new BuildReader(this);
    }

    /// <summary>
    /// The read half the workers drain. A read hands out the best build that may start
    /// now; <see cref="Complete"/> must follow once it has finished.
    /// </summary>
    public ChannelReader<ReleaseImportJob> Reader { get; }

    /// <summary>Builds waiting for a worker.</summary>
    public int WaitingCount
    {
        get { lock (_lock) return _waiting.Count; }
    }

    /// <summary>Builds a worker has taken and not yet finished.</summary>
    public int RunningCount
    {
        get { lock (_lock) return _runningByRelease.Count; }
    }

    /// <summary>The limit that applies when no site admin value is saved.</summary>
    public int DefaultLimit { get; }

    /// <summary>How many builds may run at once right now.</summary>
    public int Limit
    {
        get { lock (_lock) return _limit; }
    }

    /// <summary>
    /// The default limit: <c>OE_BUILD_CONCURRENCY</c>, else
    /// <see cref="DefaultConcurrency"/>, held between 1 and <see cref="MaxConcurrency"/>.
    /// </summary>
    public static int Concurrency(string? raw) =>
        int.TryParse(raw, out var n) ? Math.Clamp(n, MinConcurrency, MaxConcurrency) : DefaultConcurrency;

    /// <summary>
    /// The limit that applies: the site admin's saved value when there is one, else
    /// <paramref name="defaultLimit"/>, held between 1 and <see cref="MaxConcurrency"/>.
    /// </summary>
    public static int EffectiveLimit(int? saved, int defaultLimit) =>
        Math.Clamp(saved ?? defaultLimit, MinConcurrency, MaxConcurrency);

    /// <summary>
    /// Applies the site admin's saved value, or <see cref="DefaultLimit"/> when it is
    /// empty. Called at startup and after the setting is saved.
    /// </summary>
    public void ApplySetting(int? saved) => SetLimit(EffectiveLimit(saved, DefaultLimit));

    /// <summary>
    /// Changes how many builds may run at once. Running builds are never stopped: a
    /// lower limit only holds back new ones. A higher one wakes the waiting workers.
    /// </summary>
    public void SetLimit(int limit)
    {
        limit = Math.Clamp(limit, MinConcurrency, MaxConcurrency);
        lock (_lock)
        {
            if (limit == _limit) return;
            var raised = limit > _limit;
            _limit = limit;
            if (raised) Signal();
        }
    }

    /// <summary>Adds <paramref name="job"/> to the line. Never waits.</summary>
    public void Enqueue(ReleaseImportJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_lock)
        {
            _waiting.Add(new Waiting(job, job.BuildOrder ?? ProjectBuildOrder.Unordered, _sequence++));
            Signal();
        }
    }

    /// <summary>
    /// Releases the build's place, so the next build of the same pipeline may start.
    /// Called once per build a read handed out, whatever its outcome.
    /// </summary>
    public void Complete(ReleaseImportJob job)
    {
        lock (_lock)
        {
            if (!_runningByRelease.Remove(job.ReleaseId, out var key)) return;
            if (key is not null) _runningKeys.Remove(key);
            Signal();
        }
    }

    private bool TryTake(out ReleaseImportJob job)
    {
        lock (_lock)
        {
            var next = NextStartable();
            if (next is null)
            {
                job = null!;
                return false;
            }
            _waiting.Remove(next);
            _runningByRelease[next.Job.ReleaseId] = next.Order.SerialKey;
            if (next.Order.SerialKey is { } key) _runningKeys.Add(key);
            job = next.Job;
            return true;
        }
    }

    // Caller holds _lock. Nothing starts while the limit is reached. A build whose key
    // is running is skipped, and so is every later build with the same key, which
    // keeps the key's builds in queue order. A release already building is skipped
    // too: two Retry clicks on a build with no pipeline must not build into the same
    // release at once.
    private Waiting? NextStartable()
    {
        if (_runningByRelease.Count >= _limit) return null;
        Waiting? best = null;
        HashSet<string>? passed = null;
        foreach (var w in _waiting.OrderBy(w => w.Sequence))
        {
            if (_runningByRelease.ContainsKey(w.Job.ReleaseId)) continue;
            if (w.Order.SerialKey is { } key)
            {
                if (_runningKeys.Contains(key) || (passed?.Contains(key) ?? false)) continue;
                (passed ??= []).Add(key);
            }
            if (best is null || w.Order.Priority < best.Order.Priority) best = w;
        }
        return best;
    }

    // Caller holds _lock. Wakes every waiting reader; each one looks again.
    private void Signal()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }

    private async ValueTask<bool> WaitForStartableAsync(CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_lock)
            {
                if (NextStartable() is not null) return true;
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record Waiting(ReleaseImportJob Job, ProjectBuildOrder Order, long Sequence);

    private sealed class BuildReader(ProjectBuildQueue queue) : ChannelReader<ReleaseImportJob>
    {
        public override bool TryRead(out ReleaseImportJob item) => queue.TryTake(out item);

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            queue.WaitForStartableAsync(cancellationToken);
    }
}

/// <summary>
/// Where a build stands in <see cref="ProjectBuildQueue"/>: lower
/// <paramref name="Priority"/> starts first, and builds that share a
/// <paramref name="SerialKey"/> run one at a time.
/// </summary>
public sealed record ProjectBuildOrder(int Priority, string? SerialKey)
{
    /// <summary>For a job queued without an order: first in line, alongside nothing.</summary>
    public static readonly ProjectBuildOrder Unordered = new(0, null);

    /// <summary>
    /// A build somebody is waiting on (Build, Retry, a pull request check) goes first,
    /// then builds on push, then the nightly preview checks. A pipeline's builds against
    /// one Business Central target run in order; its preview checks may run beside its
    /// current build, since neither publishes over the other.
    /// </summary>
    public static ProjectBuildOrder For(int? pipelineId, string? bcTarget, string trigger) => new(
        trigger switch
        {
            ProjectBuildTrigger.Push => 1,
            ProjectBuildTrigger.PreviewCheck => 2,
            _ => 0,
        },
        pipelineId is { } id ? $"{id}:{bcTarget ?? ProjectBuildTarget.Current}" : null);
}
