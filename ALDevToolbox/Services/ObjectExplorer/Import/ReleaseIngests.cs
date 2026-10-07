using System.Collections.Concurrent;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// What this process is importing right now, and the one-at-a-time gate for the
/// heavy imports. Both live in memory only, which is enough because the app runs as
/// one instance (#1180).
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
public static class ReleaseIngests
{
    private static readonly ConcurrentDictionary<int, int> Running = new();
    private static readonly SemaphoreSlim HeavyGate = new(1, 1);

    /// <summary>
    /// Records that this process is importing <paramref name="releaseId"/> until the
    /// result is disposed. Nested calls for the same release are counted.
    /// </summary>
    public static IDisposable Track(int releaseId)
    {
        Running.AddOrUpdate(releaseId, 1, (_, n) => n + 1);
        return new Tracked(releaseId);
    }

    /// <summary>Whether something in this process is importing <paramref name="releaseId"/>.</summary>
    public static bool IsRunning(int releaseId) => Running.ContainsKey(releaseId);

    /// <summary>
    /// Waits until no other heavy import is running; disposing the result lets the
    /// next one in.
    /// </summary>
    public static async Task<IDisposable> EnterHeavyAsync(CancellationToken ct)
    {
        await HeavyGate.WaitAsync(ct).ConfigureAwait(false);
        return new Held();
    }

    /// <summary>Takes the heavy-import gate if nobody holds it, else returns null without waiting.</summary>
    public static IDisposable? TryEnterHeavy() => HeavyGate.Wait(0) ? new Held() : null;

    private sealed class Tracked(int releaseId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            while (true)
            {
                if (!Running.TryGetValue(releaseId, out var n)) return;
                if (n <= 1)
                {
                    if (Running.TryRemove(new KeyValuePair<int, int>(releaseId, n))) return;
                }
                else if (Running.TryUpdate(releaseId, n - 1, n))
                {
                    return;
                }
            }
        }
    }

    private sealed class Held : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) HeavyGate.Release();
        }
    }
}
