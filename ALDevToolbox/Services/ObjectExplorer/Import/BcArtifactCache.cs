using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Keeps the Business Central artifacts project builds download, so the next build
/// against the same version reads them from disk instead of fetching about 2 GB again
/// (#1140). An artifact URL names one exact build of one country, and Microsoft never
/// changes what it serves there, so a cached copy never goes stale; the cache only has
/// to stay within its size.
///
/// <para>
/// Files live under <c>{AL_COMPILER_DIR}/artifact-cache</c>, on the <c>app-altool</c>
/// volume the compiler and the symbol cache already use, as <c>{hash}.app.zip</c> and
/// <c>{hash}.platform.zip</c> keyed by the application URL. Builds run side by side, so
/// one download per URL runs at a time (the second build waits and reads the first's
/// copy), and a set a build holds is never evicted under it. When the cache grows past
/// <see cref="BcArtifactCacheOptions.MaxBytes"/> the sets used longest ago go first.
/// </para>
/// </summary>
public sealed class BcArtifactCache
{
    private const string AppSuffix = ".app.zip";
    private const string PlatformSuffix = ".platform.zip";
    private const string PartialSuffix = ".partial";

    /// <summary>A partial file older than this is left over from a store that never finished.</summary>
    internal static readonly TimeSpan AbandonedPartialAge = TimeSpan.FromHours(6);

    private readonly BcArtifactCacheOptions _options;
    private readonly ILogger<BcArtifactCache> _logger;
    private readonly KeyedGate<string> _downloads = new();
    private readonly object _inUseLock = new();
    private readonly Dictionary<string, int> _inUse = [];

    public BcArtifactCache(BcArtifactCacheOptions options, ILogger<BcArtifactCache> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The artifact set for <paramref name="applicationUrl"/>, from the cache or through
    /// <paramref name="download"/>. Dispose the lease once the files have been read; a
    /// set that could not be cached (the cache is off, or the platform half is missing)
    /// is deleted then.
    /// </summary>
    public async Task<BcArtifactLease> GetAsync(
        string applicationUrl,
        Func<CancellationToken, Task<BcArtifactDownload>> download,
        CancellationToken ct = default)
    {
        if (_options.MaxBytes <= 0)
        {
            return Uncached(await download(ct).ConfigureAwait(false));
        }

        var key = Key(applicationUrl);
        using (await _downloads.EnterAsync(key, ct).ConfigureAwait(false))
        {
            if (TryLease(key) is { } hit)
            {
                _logger.LogInformation("Using the cached Business Central artifacts for {Url}.", applicationUrl);
                return hit;
            }

            var fresh = await download(ct).ConfigureAwait(false);
            // An artifact set without its platform half is not what a later build
            // would download, so it is used once and not kept.
            if (fresh.PlatformZipPath is null) return Uncached(fresh);
            // Held before the files land, so a concurrent eviction never takes a set
            // that is being stored.
            Hold(key);
            try
            {
                Directory.CreateDirectory(_options.Directory);
                // The download sits on another filesystem, so a move is a copy. Each
                // file is copied under a name eviction and reads ignore, then renamed
                // in place: a crash mid-copy leaves a partial file, never a set that
                // reads as complete with a cut-off zip in it.
                Store(fresh.PlatformZipPath, PathOf(key, PlatformSuffix));
                Store(fresh.ApplicationZipPath, PathOf(key, AppSuffix));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not keep the Business Central artifacts for {Url}; this build uses them once.", applicationUrl);
                Release(key);
                // A half that already moved in is no longer at its download path, so
                // it goes back there before the cache copies are tidied away (#1181).
                var uncached = new BcArtifactDownload(
                    Unstore(fresh.ApplicationZipPath, PathOf(key, AppSuffix)),
                    Unstore(fresh.PlatformZipPath, PathOf(key, PlatformSuffix)));
                string[] kept = [uncached.ApplicationZipPath, uncached.PlatformZipPath!];
                foreach (var suffix in new[] { AppSuffix, PlatformSuffix })
                {
                    var cached = PathOf(key, suffix);
                    if (!kept.Contains(cached)) TryDelete(cached);
                    if (!kept.Contains(cached + PartialSuffix)) TryDelete(cached + PartialSuffix);
                }
                return Uncached(uncached);
            }

            var lease = new BcArtifactLease(
                new BcArtifactDownload(PathOf(key, AppSuffix), PathOf(key, PlatformSuffix)), () => Release(key), () => Discard(key));
            Evict();
            return lease;
        }
    }

    // Caller holds the key's download gate, so nothing else adds or replaces this set.
    private BcArtifactLease? TryLease(string key)
    {
        var app = PathOf(key, AppSuffix);
        var platform = PathOf(key, PlatformSuffix);
        lock (_inUseLock)
        {
            if (!File.Exists(app) || !File.Exists(platform)) return null;
            _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
        }
        // Last use is the eviction order, so a set read every night stays.
        var now = DateTime.UtcNow;
        TryTouch(app, now);
        TryTouch(platform, now);
        return new BcArtifactLease(new BcArtifactDownload(app, platform), () => Release(key), () => Discard(key));
    }

    private void Hold(string key)
    {
        lock (_inUseLock) _inUse[key] = _inUse.GetValueOrDefault(key) + 1;
    }

    private static void Store(string source, string target)
    {
        var partial = target + PartialSuffix;
        File.Move(source, partial, overwrite: true);
        File.Move(partial, target, overwrite: true);
    }

    // Where a download stands after a store that failed part-way: still at its download
    // path, or moved back there from the cache (or its partial copy, when only the
    // rename failed). Should that move fail too, the build reads it where it is.
    private static string Unstore(string source, string target)
    {
        if (File.Exists(source)) return source;
        foreach (var stored in new[] { target + PartialSuffix, target })
        {
            if (!File.Exists(stored)) continue;
            try
            {
                File.Move(stored, source);
                return source;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return stored;
            }
        }
        return source;
    }

    // A set a build could not read (a corrupt download): released, then deleted unless
    // another build still holds it, so the next build downloads it again.
    private void Discard(string key)
    {
        lock (_inUseLock)
        {
            Release(key);
            if (_inUse.ContainsKey(key)) return;
            TryDelete(PathOf(key, AppSuffix));
            TryDelete(PathOf(key, PlatformSuffix));
        }
        _logger.LogWarning("Removed cached Business Central artifacts {Key}: a build could not read them.", key);
    }

    private void Release(string key)
    {
        lock (_inUseLock)
        {
            if (!_inUse.TryGetValue(key, out var count)) return;
            if (count <= 1) _inUse.Remove(key);
            else _inUse[key] = count - 1;
        }
    }

    /// <summary>
    /// Deletes the sets used longest ago until the cache fits, skipping any set a build
    /// holds. Leftovers from an interrupted store (a platform half on its own) go too.
    /// </summary>
    internal void Evict()
    {
        // Best-effort: a build that already has its files must not fail over the tidy-up.
        try
        {
            EvictCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not tidy the Business Central artifact cache; trying again after the next download.");
        }
    }

    private void EvictCore()
    {
        if (!Directory.Exists(_options.Directory)) return;
        foreach (var partial in Directory.EnumerateFiles(_options.Directory, "*" + PartialSuffix))
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(partial) > AbandonedPartialAge) TryDelete(partial);
        }
        lock (_inUseLock)
        {
            var sets = Directory.EnumerateFiles(_options.Directory, "*.zip")
                .Select(path => new FileInfo(path))
                .GroupBy(f => SetKey(f.Name))
                .Where(g => g.Key is not null)
                .Select(g => new
                {
                    Key = g.Key!,
                    Files = g.ToList(),
                    Complete = g.Any(f => f.Name.EndsWith(AppSuffix, StringComparison.Ordinal))
                               && g.Any(f => f.Name.EndsWith(PlatformSuffix, StringComparison.Ordinal)),
                    Bytes = g.Sum(f => f.Length),
                    LastUsed = g.Max(f => f.LastWriteTimeUtc),
                })
                .ToList();

            var total = sets.Sum(s => s.Bytes);
            foreach (var set in sets.OrderBy(s => s.Complete).ThenBy(s => s.LastUsed))
            {
                if (_inUse.ContainsKey(set.Key)) continue;
                if (set.Complete && total <= _options.MaxBytes) break;
                foreach (var file in set.Files) TryDelete(file.FullName);
                total -= set.Bytes;
                _logger.LogInformation("Removed cached Business Central artifacts {Key} ({Bytes} bytes) to stay within the cache size.", set.Key, set.Bytes);
            }
        }
    }

    private static BcArtifactLease Uncached(BcArtifactDownload download) => new(download, () =>
    {
        TryDelete(download.ApplicationZipPath);
        if (download.PlatformZipPath is not null) TryDelete(download.PlatformZipPath);
    });

    private string PathOf(string key, string suffix) => Path.Combine(_options.Directory, key + suffix);

    private static string Key(string applicationUrl) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(applicationUrl)));

    private static string? SetKey(string fileName) =>
        fileName.EndsWith(AppSuffix, StringComparison.Ordinal) ? fileName[..^AppSuffix.Length]
        : fileName.EndsWith(PlatformSuffix, StringComparison.Ordinal) ? fileName[..^PlatformSuffix.Length]
        : null;

    private static void TryTouch(string path, DateTime now)
    {
        try { File.SetLastWriteTimeUtc(path, now); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A build's hold on one artifact set: the paths stay readable, and the set stays in
/// the cache, until it is disposed.
/// </summary>
public sealed class BcArtifactLease : IDisposable
{
    private Action? _release;
    private readonly Action? _discard;

    public BcArtifactLease(BcArtifactDownload download, Action release, Action? discard = null)
    {
        Download = download;
        _release = release;
        _discard = discard;
    }

    public BcArtifactDownload Download { get; }

    /// <summary>
    /// Releases the set and drops it from the cache: the files could not be read, so
    /// the next build must download them again rather than fail on them too.
    /// </summary>
    public void Discard()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release is null) return;
        (_discard ?? release)();
    }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
