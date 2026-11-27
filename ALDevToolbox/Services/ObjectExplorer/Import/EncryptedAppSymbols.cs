using System.IO.Compression;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Finds readable symbols for an encrypted vendor <c>.app</c> (#1803). Vendors such
/// as Continia ship their apps NEA-encrypted, so the importer cannot read their
/// objects, but ship the source as a zip beside them. The source zip's
/// <c>app.json</c> names the app and its exact version, and the AppSource symbol
/// feed the pipeline builds already use (#901) publishes a symbols-only package for
/// it: that package is unencrypted and carries the symbol file the importer reads,
/// so symbols plus source make a module as complete as an unencrypted app.
///
/// <para>Only the exact version is accepted. Symbols of another version describe
/// different code than the source beside them. Holds no database context, so it
/// is a singleton beside the resolver it wraps.</para>
/// </summary>
public sealed class EncryptedAppSymbols
{
    private readonly AlSymbolFeedResolver _feed;
    private readonly ILogger<EncryptedAppSymbols> _logger;

    public EncryptedAppSymbols(AlSymbolFeedResolver feed, ILogger<EncryptedAppSymbols> logger)
    {
        _feed = feed;
        _logger = logger;
    }

    /// <summary>
    /// The symbols package for the app whose source is <paramref name="sourceZip"/>,
    /// as bytes, or the reason there is none. <paramref name="sourceZip"/> must be
    /// seekable; it is read, not closed.
    /// </summary>
    public async Task<EncryptedAppSymbolsResult> FindAsync(Stream sourceZip, CancellationToken ct = default)
    {
        AppJsonManifest? manifest;
        try
        {
            manifest = ReadAppJson(sourceZip);
        }
        catch (InvalidDataException)
        {
            return EncryptedAppSymbolsResult.NotFound("encrypted, and its source zip could not be read");
        }
        if (manifest is null || !Guid.TryParse(manifest.Id, out _) || AlSymbolFeedResolver.ParseVersion(manifest.Version) is null)
        {
            return EncryptedAppSymbolsResult.NotFound("encrypted, and its source zip has no app.json naming the app and version");
        }

        var dir = Path.Combine(Path.GetTempPath(), TempPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var outcome = await _feed.ResolveAsync(new SymbolFeedRequest(
                [new SymbolDependency(manifest.Id, manifest.Name, manifest.Version)],
                dir,
                new HashSet<string>(),
                ApplicationVersion: null,
                Country: null,
                ExactVersion: true,
                IncludeDependencies: false), ct);

            if (outcome.Resolved.FirstOrDefault() is { } resolved)
            {
                _logger.LogInformation(
                    "Using the {Feed} symbols of {App} {Version} for its encrypted .app.",
                    resolved.Feed, resolved.Name, resolved.Version);
                return EncryptedAppSymbolsResult.Found(
                    await File.ReadAllBytesAsync(Path.Combine(dir, resolved.FileName), ct), resolved.Version);
            }

            var why = outcome.Unresolved.FirstOrDefault()?.Reason ?? $"it is not on the {AlSymbolFeedResolver.AppSourceFeedName}";
            return EncryptedAppSymbolsResult.NotFound($"encrypted, and its symbols for {manifest.Version} could not be found: {why}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* swallow: the startup sweep reclaims it */ }
        }
    }

    /// <summary>Temp-folder prefix for one lookup; swept at startup like the other import temp files.</summary>
    public const string TempPrefix = "oe-encrypted-";

    /// <summary>Upper bound on the app.json read out of a source zip.</summary>
    private const long MaxAppJsonBytes = 1024 * 1024;

    /// <summary>
    /// The <c>app.json</c> at the root of the source zip, or one wrapper folder
    /// down, shallowest first. Deeper ones belong to nested apps (a test app, a
    /// sample) and never name the app the zip was shipped beside.
    /// </summary>
    internal static AppJsonManifest? ReadAppJson(Stream sourceZip)
    {
        sourceZip.Position = 0;
        using var archive = new ZipArchive(sourceZip, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.Entries
            .Select(e => (Entry: e, Path: e.FullName.Replace('\\', '/').TrimStart('/')))
            .Where(e => string.Equals(Path.GetFileName(e.Path), "app.json", StringComparison.OrdinalIgnoreCase))
            .Select(e => (e.Entry, Depth: e.Path.Count(c => c == '/')))
            .Where(e => e.Depth <= 1)
            .OrderBy(e => e.Depth)
            .Select(e => e.Entry)
            .FirstOrDefault();
        if (entry is null) return null;
        using var stream = AppPackageReader.OpenCapped(entry, MaxAppJsonBytes);
        using var reader = new StreamReader(stream);
        try
        {
            return AppJsonManifestParser.Parse(reader.ReadToEnd());
        }
        finally
        {
            sourceZip.Position = 0;
        }
    }
}

/// <summary>What <see cref="EncryptedAppSymbols.FindAsync"/> found: the symbols package, or why not.</summary>
public sealed record EncryptedAppSymbolsResult(byte[]? SymbolsApp, string? Version, string? Reason)
{
    public static EncryptedAppSymbolsResult Found(byte[] app, string version) => new(app, version, null);
    public static EncryptedAppSymbolsResult NotFound(string reason) => new(null, null, reason);
}
