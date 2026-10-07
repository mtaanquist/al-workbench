namespace ALDevToolbox.Services.Configuration;

/// <summary>
/// Where project builds keep the Business Central artifacts they download, and how
/// much disk that may take. Read once at startup, in the <see cref="AlCompilerOptions"/>
/// style. See <see cref="ALDevToolbox.Services.ObjectExplorer.Import.BcArtifactCache"/>.
/// </summary>
public sealed record BcArtifactCacheOptions
{
    /// <summary>The cache size when <c>BC_ARTIFACT_CACHE_GB</c> is not set: room for several versions and countries.</summary>
    public const int DefaultGigabytes = 15;

    /// <summary>
    /// An <c>artifact-cache</c> folder inside the compiler's install root, so it rides
    /// on the <c>app-altool</c> volume (<c>AL_COMPILER_DIR</c>) beside the symbol cache.
    /// </summary>
    public string Directory { get; init; } = CacheUnder(new AlCompilerOptions().InstallDirectory);

    /// <summary>The most the cache holds before it drops the sets used longest ago. Zero turns it off. <c>BC_ARTIFACT_CACHE_GB</c>.</summary>
    public long MaxBytes { get; init; } = DefaultGigabytes * Gigabyte;

    private const long Gigabyte = 1024L * 1024 * 1024;

    public static BcArtifactCacheOptions FromConfiguration(IConfiguration configuration) => new()
    {
        Directory = CacheUnder(AlCompilerOptions.FromConfiguration(configuration).InstallDirectory),
        MaxBytes = Gigabytes(configuration["BC_ARTIFACT_CACHE_GB"]) * Gigabyte,
    };

    /// <summary>A whole number of gigabytes from the setting; blank or unreadable means the default, below zero means off.</summary>
    internal static long Gigabytes(string? raw) =>
        long.TryParse(raw, out var n) ? Math.Max(n, 0) : DefaultGigabytes;

    private static string CacheUnder(string compilerInstallDirectory) =>
        Path.Combine(compilerInstallDirectory, "artifact-cache");
}
