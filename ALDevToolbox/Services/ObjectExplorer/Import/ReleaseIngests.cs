using System.Collections.Concurrent;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// What this process is importing right now, and the one-at-a-time gate for the
/// heavy imports. Registered as a singleton; both halves live in memory only, which is
/// enough because the app runs as one instance (#1180).
///
/// <para>
/// <see cref="Track"/> answers "is anyone still working on this <c>ingesting</c>
/// release?". A build that finds its parent release importing waits for it; without
/// this it could not tell a running import from one a cancelled build abandoned, and
/// waited the full limit for an import nobody was doing.
/// </para>
///
/// <para>
/// <see cref="EnterHeavyAsync"/> serialises the imports of whole Business Central
/// releases - a build's inline parent import and every job the release import worker
/// runs. Before builds ran side by side (#1137) all of them shared one worker; now
/// several could bulk-insert into the indexed-file tables at once on a small
/// database. A build's own extensions and a vendor's symbols are small and do not
/// take it, so a build is never held behind a 30-minute import for its own ingest.
/// </para>
/// </summary>
public sealed class ReleaseIngests
{
    private readonly ConcurrentDictionary<int, int> _running = new();
    private readonly SemaphoreSlim _heavyGate = new(1, 1);

    /// <summary>
    /// Records that this process is importing <paramref name="releaseId"/> until the
    /// result is disposed. Nested calls for the same release are counted.
    /// </summary>
    public IDisposable Track(int releaseId)
    {
        _running.AddOrUpdate(releaseId, 1, (_, n) => n + 1);
        return new Tracked(this, releaseId);
    }

    /// <summary>Whether something in this process is importing <paramref name="releaseId"/>.</summary>
    public bool IsRunning(int releaseId) => _running.ContainsKey(releaseId);

    /// <summary>
    /// Waits until no other heavy import is running; disposing the result lets the
    /// next one in.
    /// </summary>
    public async Task<IDisposable> EnterHeavyAsync(CancellationToken ct)
    {
        await _heavyGate.WaitAsync(ct).ConfigureAwait(false);
        return new Held(_heavyGate);
    }

    /// <summary>Takes the heavy-import gate if nobody holds it, else returns null without waiting.</summary>
    public IDisposable? TryEnterHeavy() => _heavyGate.Wait(0) ? new Held(_heavyGate) : null;

    private void Untrack(int releaseId)
    {
        while (true)
        {
            if (!_running.TryGetValue(releaseId, out var n)) return;
            if (n <= 1)
            {
                if (_running.TryRemove(new KeyValuePair<int, int>(releaseId, n))) return;
            }
            else if (_running.TryUpdate(releaseId, n - 1, n))
            {
                return;
            }
        }
    }

    private sealed class Tracked(ReleaseIngests owner, int releaseId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Untrack(releaseId);
        }
    }

    private sealed class Held(SemaphoreSlim gate) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Release();
        }
    }
}
