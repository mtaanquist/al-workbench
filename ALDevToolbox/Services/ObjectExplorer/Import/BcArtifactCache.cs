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
            try
            {
                Directory.CreateDirectory(_options.Directory);
                // Platform first: a set is only found once its application half is
                // there, so a crash in between leaves an orphan, never half a set.
                File.Move(fresh.PlatformZipPath, PathOf(key, PlatformSuffix), overwrite: true);
                File.Move(fresh.ApplicationZipPath, PathOf(key, AppSuffix), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not keep the Business Central artifacts for {Url}; this build uses them once.", applicationUrl);
                TryDelete(PathOf(key, AppSuffix));
                TryDelete(PathOf(key, PlatformSuffix));
                return Uncached(fresh);
            }

            var lease = TryLease(key)
                ?? throw new InvalidOperationException($"The cached artifacts for {applicationUrl} disappeared as they were stored.");
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
        return new BcArtifactLease(new BcArtifactDownload(app, platform), () => Release(key));
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
        if (!Directory.Exists(_options.Directory)) return;
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

    public BcArtifactLease(BcArtifactDownload download, Action release)
    {
        Download = download;
        _release = release;
    }

    public BcArtifactDownload Download { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
