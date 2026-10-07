using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The Business Central artifacts a build downloads are kept for the next build of the
/// same version (#1140), within a size, and never removed under a build using them.
/// </summary>
public sealed class BcArtifactCacheTests : IDisposable
{
    private const string UrlA = "https://bcartifacts.example/onprem/29.0.1.2/dk";
    private const string UrlB = "https://bcartifacts.example/onprem/29.0.1.2/de";
    private const string UrlC = "https://bcartifacts.example/onprem/29.1.3.4/dk";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "aldt-artifact-cache-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private int _downloadCount;

    public BcArtifactCacheTests()
    {
        _downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private BcArtifactCache NewCache(long maxBytes = 1_000_000) => new(
        new BcArtifactCacheOptions { Directory = Path.Combine(_root, "cache"), MaxBytes = maxBytes },
        NullLogger<BcArtifactCache>.Instance);

    // Stands in for BcArtifactService.DownloadArtifactSetAsync: two temp zips per call.
    private Func<CancellationToken, Task<BcArtifactDownload>> Download(int bytes = 100, bool withPlatform = true) => async _ =>
    {
        Interlocked.Increment(ref _downloadCount);
        await Task.Yield();
        var app = Path.Combine(_downloads, Guid.NewGuid().ToString("N") + ".zip");
        await File.WriteAllBytesAsync(app, new byte[bytes]);
        string? platform = null;
        if (withPlatform)
        {
            platform = Path.Combine(_downloads, Guid.NewGuid().ToString("N") + ".zip");
            await File.WriteAllBytesAsync(platform, new byte[bytes]);
        }
        return new BcArtifactDownload(app, platform);
    };

    [Fact]
    public async Task The_second_build_of_a_version_reads_the_first_ones_download()
    {
        var cache = NewCache();

        using (var first = await cache.GetAsync(UrlA, Download()))
        {
            File.Exists(first.Value.ApplicationZipPath).Should().BeTrue();
        }
        using var second = await cache.GetAsync(UrlA, Download());

        _downloadCount.Should().Be(1);
        File.Exists(second.Value.ApplicationZipPath).Should().BeTrue();
        File.Exists(second.Value.PlatformZipPath).Should().BeTrue();
        Directory.EnumerateFiles(_downloads).Should().BeEmpty("the downloaded files were moved into the cache");
    }

    [Fact]
    public async Task Two_builds_of_one_version_at_once_download_it_once()
    {
        var cache = NewCache();

        var leases = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => cache.GetAsync(UrlA, Download())));

        _downloadCount.Should().Be(1);
        leases.Select(l => l.Value.ApplicationZipPath).Distinct().Should().ContainSingle();
        foreach (var lease in leases) lease.Dispose();
    }

    private static void Age(InUseLease<BcArtifactDownload> lease, int days)
    {
        File.SetLastWriteTimeUtc(lease.Value.ApplicationZipPath, DateTime.UtcNow.AddDays(-days));
        File.SetLastWriteTimeUtc(lease.Value.PlatformZipPath!, DateTime.UtcNow.AddDays(-days));
    }

    [Fact]
    public async Task Past_its_size_the_cache_drops_the_set_used_longest_ago()
    {
        // Room for two sets of 2 x 100 bytes.
        var cache = NewCache(maxBytes: 450);
        var a = await cache.GetAsync(UrlA, Download());
        a.Dispose();
        var b = await cache.GetAsync(UrlB, Download());
        b.Dispose();
        Age(a, 2);
        Age(b, 1);
        // Reading A again makes it the most recently used.
        (await cache.GetAsync(UrlA, Download())).Dispose();

        using var c = await cache.GetAsync(UrlC, Download());

        File.Exists(b.Value.ApplicationZipPath).Should().BeFalse();
        File.Exists(a.Value.ApplicationZipPath).Should().BeTrue();
        File.Exists(c.Value.ApplicationZipPath).Should().BeTrue();
        _downloadCount.Should().Be(3);
    }

    [Fact]
    public async Task A_set_a_build_is_reading_is_never_dropped()
    {
        var cache = NewCache(maxBytes: 450);
        var held = await cache.GetAsync(UrlA, Download());
        var b = await cache.GetAsync(UrlB, Download());
        b.Dispose();
        Age(held, 2);
        Age(b, 1);

        using var c = await cache.GetAsync(UrlC, Download());

        File.Exists(held.Value.ApplicationZipPath).Should().BeTrue("a build is still reading it, though it is the oldest");
        File.Exists(b.Value.ApplicationZipPath).Should().BeFalse();
        held.Dispose();
    }

    [Fact]
    public async Task A_set_a_build_could_not_read_is_dropped_so_the_next_build_downloads_it_again()
    {
        var cache = NewCache();
        var lease = await cache.GetAsync(UrlA, Download());

        lease.Discard();

        File.Exists(lease.Value.ApplicationZipPath).Should().BeFalse();
        (await cache.GetAsync(UrlA, Download())).Dispose();
        _downloadCount.Should().Be(2);
    }

    [Fact]
    public async Task A_copy_a_crash_cut_short_is_tidied_away_once_it_is_old_and_never_read()
    {
        var cache = NewCache();
        (await cache.GetAsync(UrlA, Download())).Dispose();
        var dir = Path.Combine(_root, "cache");
        var abandoned = Path.Combine(dir, "abandoned.app.zip.partial");
        var copying = Path.Combine(dir, "copying.app.zip.partial");
        await File.WriteAllBytesAsync(abandoned, new byte[10]);
        await File.WriteAllBytesAsync(copying, new byte[10]);
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - BcArtifactCache.AbandonedPartialAge - TimeSpan.FromMinutes(1));

        cache.Evict();

        File.Exists(abandoned).Should().BeFalse();
        File.Exists(copying).Should().BeTrue("another build may still be copying it");
    }

    [Fact]
    public async Task A_set_without_its_platform_half_is_used_once_and_deleted()
    {
        var cache = NewCache();

        var lease = await cache.GetAsync(UrlA, Download(withPlatform: false));
        var path = lease.Value.ApplicationZipPath;
        lease.Dispose();

        File.Exists(path).Should().BeFalse();
        (await cache.GetAsync(UrlA, Download())).Dispose();
        _downloadCount.Should().Be(2);
    }

    // The platform half moves in first; when the application half then cannot be stored
    // (a full disk), the build still gets two files it can read (#1181).
    [Fact]
    public async Task A_set_that_cannot_be_stored_is_used_once_from_files_that_still_exist()
    {
        var cache = NewCache();
        var cacheDir = Path.Combine(_root, "cache");
        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(UrlA)));
        // Something in the way of the application half's copy makes its store fail.
        Directory.CreateDirectory(Path.Combine(cacheDir, key + ".app.zip.partial"));

        var lease = await cache.GetAsync(UrlA, Download());

        File.Exists(lease.Value.ApplicationZipPath).Should().BeTrue();
        File.Exists(lease.Value.PlatformZipPath).Should().BeTrue();
        File.Exists(Path.Combine(cacheDir, key + ".platform.zip")).Should().BeFalse("a set that could not be kept whole is not kept");
        lease.Dispose();
        File.Exists(lease.Value.ApplicationZipPath).Should().BeFalse();
        File.Exists(lease.Value.PlatformZipPath).Should().BeFalse();
    }

    [Fact]
    public async Task A_cache_turned_off_downloads_every_time_and_keeps_nothing()
    {
        var cache = NewCache(maxBytes: 0);

        var lease = await cache.GetAsync(UrlA, Download());
        lease.Dispose();
        (await cache.GetAsync(UrlA, Download())).Dispose();

        _downloadCount.Should().Be(2);
        File.Exists(lease.Value.ApplicationZipPath).Should().BeFalse();
        Directory.Exists(Path.Combine(_root, "cache")).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, BcArtifactCacheOptions.DefaultGigabytes)]
    [InlineData("", BcArtifactCacheOptions.DefaultGigabytes)]
    [InlineData("lots", BcArtifactCacheOptions.DefaultGigabytes)]
    [InlineData("0", 0)]
    [InlineData("-3", 0)]
    [InlineData("40", 40)]
    public void The_size_comes_from_the_setting(string? raw, long expected) =>
        BcArtifactCacheOptions.Gigabytes(raw).Should().Be(expected);
}
