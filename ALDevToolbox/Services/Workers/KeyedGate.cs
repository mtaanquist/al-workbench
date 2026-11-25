using System.Collections.Concurrent;

namespace ALDevToolbox.Services.Workers;

/// <summary>
/// One-at-a-time per key, within this process: for work that background jobs running
/// side by side must not do at once for the same subject, such as two builds publishing
/// to one GitHub repository (#1137). The app runs as one instance, so a process-wide
/// gate is enough. One semaphore per key ever seen is kept; keys are repositories,
/// organisations and environments, so the set stays small.
/// </summary>
public sealed class KeyedGate<TKey> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Entry> _gates = new();

    /// <summary>Waits until no one else holds <paramref name="key"/>; disposing the result lets the next one in.</summary>
    public async Task<IDisposable> EnterAsync(TKey key, CancellationToken ct = default)
    {
        var entry = _gates.GetOrAdd(key, _ => new Entry());
        Interlocked.Increment(ref entry.Waiting);
        try
        {
            await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref entry.Waiting);
        }
        return new Held(entry.Gate);
    }

    /// <summary>
    /// How many callers are inside <see cref="EnterAsync"/> for <paramref name="key"/> and
    /// have not got in yet. For tests: a caller parked on a gate the test holds is the
    /// signal that it reached the gate, which a sleep only hopes for.
    /// </summary>
    internal int WaitingCount(TKey key) =>
        _gates.TryGetValue(key, out var entry) ? Volatile.Read(ref entry.Waiting) : 0;

    /// <summary>
    /// Enters <paramref name="key"/> only if no one holds it, without waiting: null when it is
    /// taken, for work that should step aside and come back later rather than queue (#1139).
    /// </summary>
    public IDisposable? TryEnter(TKey key)
    {
        var gate = _gates.GetOrAdd(key, _ => new Entry()).Gate;
        return gate.Wait(0) ? new Held(gate) : null;
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Waiting;
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
