using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Fetches the dependency symbols a project build was not handed from Microsoft's
/// public symbol feeds: AppSource apps from the <c>AppSourceSymbols</c> feed, and
/// Microsoft apps the resolved artifact does not carry from <c>MSSymbols</c>. Each
/// package holds one symbols-only <c>.app</c> at its root and declares, in its
/// nuspec, the other apps it needs by app id - so resolution walks that graph
/// until everything the compiler will ask for is in the build's package cache.
/// See <c>.design/object-explorer-project-builds.md</c>, "Resolve symbols", and
/// issue #901 for the feed behaviour this relies on.
///
/// <para>
/// Shaped like <see cref="AlCompilerProvisioner"/> (a singleton that owns a folder
/// on the <c>app-altool</c> volume, a flat-container fetch, a zip-slip guard, and
/// a failure that degrades instead of throwing) with three deliberate differences
/// the feeds force: the <c>.nupkg</c> answers with a 303 to a blob URL, so
/// redirects are followed here by hand; the version index is not in a promised
/// order, so versions are sorted rather than read off one end; and the feed
/// publishes no package hash, so there is nothing to verify the download against
/// beyond HTTPS to a Microsoft-hosted feed. The last is a considered choice, not
/// a gap: an <c>.app</c> here is compiler input, never a binary this process
/// runs, and verifying the NuGet signature would make every build depend on the
/// signing certificate chain. The design doc records the decision.
/// </para>
///
/// <para>
/// Never throws for a feed problem. An unreachable feed, an unknown app id or a
/// package with no version that fits comes back as an <see cref="UnresolvedSymbol"/>
/// naming the app and the feeds tried, and the extensions that needed it fail at
/// compile time exactly as a missing dependency always has; everything else in the
/// build carries on.
/// </para>
/// </summary>
public sealed class AlSymbolFeedResolver
{
    /// <summary>The named HTTP client: auto-redirect off, because redirects are followed (and checked) here.</summary>
    public const string HttpClientName = "AlSymbolFeed";

    /// <summary>How the build log names the AppSource feed.</summary>
    public const string AppSourceFeedName = "AppSource symbol feed";

    /// <summary>How the build log names the Microsoft feed.</summary>
    public const string MicrosoftFeedName = "Microsoft symbol feed";

    private const int MaxRedirects = 5;

    /// <summary>A ceiling on packages fetched per build, so a malformed dependency graph can't turn into an unbounded crawl.</summary>
    private const int MaxPackagesPerBuild = 200;

    private const string CachedNuspecName = "package.nuspec";
    private const string CachedSourceName = "source.json";

    private static readonly Regex SymbolsPackageSuffix = new(
        @"\.symbols\.(?<id>[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MicrosoftBasePackage = new(
        @"^Microsoft\.(Application|Platform)(\.[A-Za-z]{2})?\.symbols$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlSymbolFeedResolver> _logger;
    private readonly AlSymbolFeedOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Concurrent: the version listing reads it without the build gate, which a build holds for its whole walk.
    private readonly ConcurrentDictionary<string, FeedEndpoints> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public AlSymbolFeedResolver(
        IHttpClientFactory httpFactory,
        ILogger<AlSymbolFeedResolver> logger,
        // Optional so hand-built instances keep compiling; DI supplies the one read from configuration.
        AlSymbolFeedOptions? options = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _options = options ?? new AlSymbolFeedOptions();
    }

    /// <summary>
    /// Brings every dependency in <paramref name="request"/> that is not already
    /// satisfied into <see cref="SymbolFeedRequest.TargetDirectory"/>, together
    /// with whatever those packages depend on in turn. An app counts as satisfied
    /// when an <c>.app</c> in the directory carries its id at or above the
    /// requested version, or when its id is in
    /// <see cref="SymbolFeedRequest.ProvidedAppIds"/> (a sibling the build compiles
    /// itself, or a stored upload that will be written afterwards and must win).
    /// Returns what was resolved and what was not; never throws except on
    /// cancellation.
    /// </summary>
    public async Task<SymbolFeedOutcome> ResolveAsync(SymbolFeedRequest request, CancellationToken ct = default)
    {
        var resolved = new List<ResolvedSymbolPackage>();
        var unresolved = new List<UnresolvedSymbol>();

        var wanted = request.Dependencies
            .Where(d => NormalizeId(d.AppId).Length > 0 && !request.ProvidedAppIds.Contains(NormalizeId(d.AppId)))
            .ToList();
        if (wanted.Count == 0) return new SymbolFeedOutcome(resolved, unresolved);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var present = ScanPresent(request.TargetDirectory);
            var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<Pending>(wanted.Select(d => new Pending(NormalizeId(d.AppId), d.Name, ParseVersion(d.MinVersion), null)));
            var fetched = 0;

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var next = queue.Dequeue();
                if (request.ProvidedAppIds.Contains(next.AppId) || failed.Contains(next.AppId)) continue;
                if (present.TryGetValue(next.AppId, out var have) && Satisfies(have, next.MinVersion)) continue;

                if (++fetched > MaxPackagesPerBuild)
                {
                    unresolved.Add(Unresolved(next, "the dependency graph was larger than a build is allowed to fetch"));
                    failed.Add(next.AppId);
                    continue;
                }

                var reasons = new List<string>();
                CachedPackage? package;
                try
                {
                    package = await ResolveOneAsync(next, request, reasons, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Symbol feed resolution failed for app {AppId}.", next.AppId);
                    reasons.Add($"it could not be fetched ({ex.Message})");
                    package = null;
                }

                if (package is null)
                {
                    unresolved.Add(Unresolved(next, reasons.Count > 0
                        ? string.Join("; ", reasons.Distinct())
                        : $"it is not on the {AppSourceFeedName} or the {MicrosoftFeedName}"));
                    failed.Add(next.AppId);
                    continue;
                }

                var dest = Path.Combine(request.TargetDirectory, package.FileName);
                if (!File.Exists(dest)) File.Copy(package.AppPath, dest);
                present[next.AppId] = ParseVersion(package.Version);
                resolved.Add(new ResolvedSymbolPackage(next.AppId, package.Name, package.Version, package.Feed, package.FileName, package.FromCache));
                _logger.LogInformation("Resolved symbols for {App} {Version} from the {Feed}{Cached}.",
                    package.Name, package.Version, package.Feed, package.FromCache ? " (cached)" : string.Empty);

                foreach (var dep in package.Dependencies)
                {
                    // The package id is the only name a transitive dependency has
                    // until it is fetched; its publisher.name part reads well enough
                    // in a failure line ("ContiniaSoftware.ContiniaSystemApplication").
                    var label = SymbolsPackageSuffix.Replace(dep.PackageId, string.Empty);
                    queue.Enqueue(new Pending(dep.AppId, label, dep.MinVersion, dep.PackageId));
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return new SymbolFeedOutcome(resolved, unresolved);
    }

    /// <summary>The Microsoft feed's package for the Application app, whose versions are the shipped Business Central versions.</summary>
    internal const string MicrosoftApplicationPackageId = "Microsoft.Application.symbols";

    /// <summary>
    /// Every version of Business Central the Microsoft symbol feed has published
    /// symbols for, read from the version index of its Application package
    /// (e.g. <c>29.0.54011.55644</c>). Only shipped builds reach that feed, so the
    /// list carries no previews. Unlike <see cref="ResolveAsync"/> this throws when
    /// the feed cannot be read: the caller is a sweep that logs and tries again.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListMicrosoftApplicationVersionsAsync(CancellationToken ct = default)
    {
        // Deliberately not behind the build gate: a build holds it for its whole
        // dependency walk, and this read needs nothing a build writes.
        var http = _httpFactory.CreateClient(HttpClientName);
        var endpoints = await GetEndpointsAsync(http, _options.MicrosoftFeedUrl, ct).ConfigureAwait(false);

        var lowerId = MicrosoftApplicationPackageId.ToLowerInvariant();
        using var indexDoc = await GetJsonAsync(http, $"{endpoints.PackageBase}{lowerId}/index.json", ct).ConfigureAwait(false);
        return indexDoc.RootElement.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList()
            : [];
    }

    // ── One app ─────────────────────────────────────────────────────────

    private async Task<CachedPackage?> ResolveOneAsync(Pending want, SymbolFeedRequest request, List<string> reasons, CancellationToken ct)
    {
        var cached = FindInCache(want.AppId, want.MinVersion, request.ApplicationVersion);
        if (cached is not null) return cached;

        foreach (var (feedName, indexUrl) in new[]
        {
            (AppSourceFeedName, _options.AppSourceFeedUrl),
            (MicrosoftFeedName, _options.MicrosoftFeedUrl),
        })
        {
            try
            {
                var found = await ResolveFromFeedAsync(feedName, indexUrl, want, request, reasons, ct).ConfigureAwait(false);
                if (found is not null) return found;
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "The {Feed} served an unusable package for app {AppId}.", feedName, want.AppId);
                reasons.Add($"the {feedName} package could not be used ({ex.Message})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // One feed down is not the end: the other may still have it.
                _logger.LogWarning(ex, "The {Feed} could not be reached while resolving app {AppId}.", feedName, want.AppId);
                reasons.Add($"the {feedName} could not be reached ({Describe(ex)})");
            }
        }
        return null;
    }

    private async Task<CachedPackage?> ResolveFromFeedAsync(
        string feedName, string indexUrl, Pending want, SymbolFeedRequest request, List<string> reasons, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(HttpClientName);
        var endpoints = await GetEndpointsAsync(http, indexUrl, ct).ConfigureAwait(false);

        // Search by the app id and read `data`: the Azure DevOps search reports
        // totalHits 0 even when it returns results. The package id is never built
        // from publisher and name - publisher normalisation is inconsistent.
        var searchUrl = $"{endpoints.Search}?q={want.AppId}&prerelease=false&take=100";
        using var searchDoc = await GetJsonAsync(http, searchUrl, ct).ConfigureAwait(false);
        var hits = searchDoc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Select(e => e.TryGetProperty("id", out var id) ? id.GetString() : null)
                .Where(id => id is not null).Select(id => id!).ToList()
            : new List<string>();
        var packageId = PickSearchHit(hits, want.AppId, want.PackageIdHint, request.Country);
        if (packageId is null) return null;

        var lowerId = packageId.ToLowerInvariant();
        using var indexDoc = await GetJsonAsync(http, $"{endpoints.PackageBase}{lowerId}/index.json", ct).ConfigureAwait(false);
        var versions = indexDoc.RootElement.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).Select(s => s!).ToList()
            : new List<string>();
        var candidates = OrderCandidates(versions, want.MinVersion);
        if (candidates.Count == 0)
        {
            reasons.Add(want.MinVersion is null
                ? $"the {feedName} lists no versions of it"
                : $"the {feedName} has no version at or above {want.MinVersion}");
            return null;
        }

        var nuspecs = new Dictionary<string, NuspecInfo>(StringComparer.OrdinalIgnoreCase);
        async Task<NuspecInfo> Nuspec(string version)
        {
            if (nuspecs.TryGetValue(version, out var known)) return known;
            var lowerVersion = version.ToLowerInvariant();
            var body = await GetStringFollowingRedirectsAsync(http,
                $"{endpoints.PackageBase}{lowerId}/{lowerVersion}/{lowerId}.nuspec", ct).ConfigureAwait(false);
            return nuspecs[version] = ParseNuspec(body);
        }

        var chosen = await PickVersionAsync(candidates, request.ApplicationVersion, Nuspec).ConfigureAwait(false);
        if (chosen is null)
        {
            reasons.Add($"no version on the {feedName} fits Business Central {request.ApplicationVersion}");
            return null;
        }

        var (version, nuspec) = chosen.Value;
        var lowerVer = version.ToLowerInvariant();
        var nupkg = await GetBytesFollowingRedirectsAsync(http,
            $"{endpoints.PackageBase}{lowerId}/{lowerVer}/{lowerId}.{lowerVer}.nupkg", ct).ConfigureAwait(false);
        return WriteToCache(want.AppId, version, feedName, packageId, nupkg, nuspec);
    }

    // ── Version choice ──────────────────────────────────────────────────

    /// <summary>
    /// The versions at or above <paramref name="floor"/>, oldest first. The feed's
    /// index happens to list newest first (unlike nuget.org), but nothing here
    /// depends on either order.
    /// </summary>
    internal static IReadOnlyList<string> OrderCandidates(IEnumerable<string> versions, Version? floor) =>
        versions
            .Where(s => !s.Contains('-', StringComparison.Ordinal))
            .Select(s => (Raw: s, Parsed: ParseVersion(s)))
            .Where(x => x.Parsed is not null && (floor is null || x.Parsed >= floor))
            .OrderBy(x => x.Parsed)
            .Select(x => x.Raw)
            .ToList();

    /// <summary>
    /// The newest candidate whose nuspec says it builds against Business Central
    /// <paramref name="applicationVersion"/> or older. Newer vendor versions need
    /// newer base symbols, so compatibility runs from the oldest candidates up to
    /// some point and stops; the newest is tried first (the usual answer), then a
    /// binary search finds the boundary without reading every nuspec - Microsoft
    /// packages list close to a thousand versions.
    /// </summary>
    internal static async Task<(string Version, NuspecInfo Nuspec)?> PickVersionAsync(
        IReadOnlyList<string> ascending, string? applicationVersion, Func<string, Task<NuspecInfo>> nuspec)
    {
        if (ascending.Count == 0) return null;
        var target = ParseVersion(applicationVersion);
        var newest = ascending[^1];
        var newestSpec = await nuspec(newest).ConfigureAwait(false);
        if (target is null || FitsApplication(newestSpec, target)) return (newest, newestSpec);

        (string, NuspecInfo)? best = null;
        int lo = 0, hi = ascending.Count - 2;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var spec = await nuspec(ascending[mid]).ConfigureAwait(false);
            if (FitsApplication(spec, target))
            {
                best = (ascending[mid], spec);
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best;
    }

    /// <summary>True when every Microsoft base-app dependency in the nuspec is at or below the target's Major.Minor.</summary>
    internal static bool FitsApplication(NuspecInfo spec, Version target)
    {
        foreach (var floor in spec.BaseFloors)
        {
            if (floor.Major > target.Major || (floor.Major == target.Major && floor.Minor > target.Minor)) return false;
        }
        return true;
    }

    /// <summary>
    /// Chooses the package for <paramref name="appId"/> among search hits. One hit
    /// is the AppSource case. The Microsoft feed publishes one package per country
    /// plus a worldwide one for the same app id; take the build's country when it
    /// has one, else the worldwide package (the id with no country segment).
    /// </summary>
    internal static string? PickSearchHit(IEnumerable<string> hitIds, string appId, string? preferredId, string? country)
    {
        var suffix = ".symbols." + appId;
        var matches = hitIds
            .Where(id => id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (matches.Count == 0) return null;
        if (matches.Count == 1) return matches[0];

        if (preferredId is not null)
        {
            var exact = matches.FirstOrDefault(m => string.Equals(m, preferredId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }

        string Prefix(string id) => id[..^suffix.Length];
        if (!string.IsNullOrWhiteSpace(country) && !string.Equals(country, "w1", StringComparison.OrdinalIgnoreCase))
        {
            var local = matches.FirstOrDefault(m =>
                Prefix(m).EndsWith("." + country.Trim(), StringComparison.OrdinalIgnoreCase));
            if (local is not null) return local;
        }
        return matches
            .OrderBy(m => Prefix(m).Count(c => c == '.'))
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    // ── Nuspec ──────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a symbols package's nuspec: its title (the app name), the other apps
    /// it depends on by app id, and the Microsoft application/platform floors it
    /// was built against. Namespaces vary between packages, so elements are matched
    /// by local name.
    /// </summary>
    internal static NuspecInfo ParseNuspec(string xml)
    {
        var doc = XDocument.Parse(xml.TrimStart('﻿'));
        var title = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim();
        var id = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "id")?.Value.Trim();
        var deps = new List<NuspecDependency>();
        var baseFloors = new List<Version>();
        foreach (var dep in doc.Descendants().Where(e => e.Name.LocalName == "dependency"))
        {
            var depId = dep.Attribute("id")?.Value.Trim();
            if (string.IsNullOrEmpty(depId)) continue;
            var min = ParseRangeFloor(dep.Attribute("version")?.Value);
            var match = SymbolsPackageSuffix.Match(depId);
            if (match.Success)
            {
                deps.Add(new NuspecDependency(NormalizeId(match.Groups["id"].Value), min, depId));
            }
            else if (MicrosoftBasePackage.IsMatch(depId) && min is not null)
            {
                baseFloors.Add(min);
            }
        }
        return new NuspecInfo(string.IsNullOrEmpty(title) ? id ?? string.Empty : title, deps, baseFloors);
    }

    /// <summary>The lower bound of a NuGet version range: <c>29.0.0</c>, <c>[27.0.0.0,27.1.0.0)</c>. Null when open below.</summary>
    internal static Version? ParseRangeFloor(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return null;
        var text = range.Trim().TrimStart('[', '(');
        var comma = text.IndexOf(',');
        if (comma >= 0) text = text[..comma];
        return ParseVersion(text.TrimEnd(']', ')'));
    }

    // ── Cache ───────────────────────────────────────────────────────────

    /// <summary>
    /// The newest cached version of <paramref name="appId"/> that meets the floor
    /// and fits the target, or null. Checked before any feed call, so a rebuild
    /// against the same dependencies makes no network call at all.
    /// </summary>
    private CachedPackage? FindInCache(string appId, Version? floor, string? applicationVersion)
    {
        var appDir = Path.Combine(_options.CacheDirectory, appId);
        if (!Directory.Exists(appDir)) return null;
        var target = ParseVersion(applicationVersion);
        foreach (var dir in Directory.EnumerateDirectories(appDir)
            .Where(d => !Path.GetFileName(d).StartsWith('.'))
            .Select(d => (Dir: d, Version: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.Version is not null && (floor is null || x.Version >= floor))
            .OrderByDescending(x => x.Version))
        {
            var package = TryLoadCached(dir.Dir, appId);
            if (package is null) continue;
            if (target is not null && !FitsApplication(package.Nuspec, target)) continue;
            return package;
        }
        return null;
    }

    private CachedPackage? TryLoadCached(string dir, string appId)
    {
        try
        {
            var app = Directory.EnumerateFiles(dir, "*.app").FirstOrDefault();
            var nuspecPath = Path.Combine(dir, CachedNuspecName);
            var sourcePath = Path.Combine(dir, CachedSourceName);
            if (app is null || !File.Exists(nuspecPath) || !File.Exists(sourcePath)) return null;
            var nuspec = ParseNuspec(File.ReadAllText(nuspecPath));
            var source = JsonSerializer.Deserialize<CacheSource>(File.ReadAllText(sourcePath));
            return new CachedPackage(appId, nuspec.Name, Path.GetFileName(dir), source?.Feed ?? AppSourceFeedName,
                app, Path.GetFileName(app), nuspec, FromCache: true);
        }
        catch (Exception ex) when (ex is IOException or JsonException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Ignoring an unreadable cached symbol package at {Dir}.", dir);
            return null;
        }
    }

    /// <summary>
    /// Pulls the root <c>.app</c> out of <paramref name="nupkg"/> into the cache
    /// under <c>{appId}/{version}/</c>, next to the nuspec. Written to a scratch
    /// folder and moved into place, so a crash mid-write never leaves a half
    /// package that a later build would trust.
    /// </summary>
    private CachedPackage WriteToCache(string appId, string version, string feedName, string packageId, byte[] nupkg, NuspecInfo nuspec)
    {
        var appDir = Path.Combine(_options.CacheDirectory, appId);
        var finalDir = Path.Combine(appDir, version);
        var scratch = Path.Combine(appDir, ".partial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string fileName;
            using (var zip = new ZipArchive(new MemoryStream(nupkg, writable: false), ZipArchiveMode.Read))
            {
                var entry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.IndexOf('/') < 0 && e.FullName.IndexOf('\\') < 0
                    && e.FullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"Package {packageId} {version} has no .app at its root.");
                fileName = entry.FullName;
                var dest = GuardedPath(scratch, fileName);
                using (var source = AppPackageReader.OpenCapped(entry))
                using (var file = File.Create(dest))
                {
                    source.CopyTo(file);
                }

                // The search matched on the id in the package name; the manifest is
                // the authority. A package that turns out to be a different app is
                // refused rather than handed to the compiler under the wrong name.
                var manifest = AppPackageReader.TryReadManifest(dest)
                    ?? throw new InvalidDataException($"Package {packageId} {version} does not contain a readable .app.");
                if (!string.Equals(NormalizeId(manifest.AppId.ToString()), appId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Package {packageId} {version} contains app {manifest.AppId}, not {appId}.");
                }

                var nuspecEntry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.IndexOf('/') < 0 && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                using var nuspecOut = File.Create(Path.Combine(scratch, CachedNuspecName));
                if (nuspecEntry is not null)
                {
                    using var s = AppPackageReader.OpenCapped(nuspecEntry, 1024 * 1024);
                    s.CopyTo(nuspecOut);
                }
                else
                {
                    // Every package carries one; this only keeps the cache readable if one ever doesn't.
                    using var w = new StreamWriter(nuspecOut);
                    w.Write($"<package><metadata><id>{WebUtility.HtmlEncode(packageId)}</id><title>{WebUtility.HtmlEncode(nuspec.Name)}</title></metadata></package>");
                }
            }
            File.WriteAllText(Path.Combine(scratch, CachedSourceName),
                JsonSerializer.Serialize(new CacheSource(feedName, packageId)));

            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            Directory.Move(scratch, finalDir);
            _logger.LogInformation("Cached symbol package {PackageId} {Version} from the {Feed}.", packageId, version, feedName);
            return new CachedPackage(appId, nuspec.Name, version, feedName,
                Path.Combine(finalDir, fileName), fileName, nuspec, FromCache: false);
        }
        finally
        {
            try { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Zip-slip guard: the entry name becomes a file name inside
    /// <paramref name="dir"/> and nowhere else. The package comes over HTTPS from
    /// a Microsoft-hosted feed but is not hash-verified, so its entry names are
    /// not trusted - the same stance as <see cref="AlCompilerProvisioner"/> (#427).
    /// </summary>
    internal static string GuardedPath(string dir, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)
            || Path.GetFileName(entryName) != entryName
            || entryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || entryName is "." or "..")
        {
            throw new InvalidDataException($"Package entry '{entryName}' is not a plain file name.");
        }
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, entryName));
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Package entry '{entryName}' escapes the cache directory.");
        }
        return full;
    }

    // ── What the build already has ──────────────────────────────────────

    /// <summary>The highest version of each app id already in <paramref name="dir"/>.</summary>
    internal static Dictionary<string, Version?> ScanPresent(string dir)
    {
        var present = new Dictionary<string, Version?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dir)) return present;
        foreach (var path in Directory.EnumerateFiles(dir, "*.app", SearchOption.TopDirectoryOnly))
        {
            var manifest = AppPackageReader.TryReadManifest(path);
            if (manifest is null) continue;
            var id = NormalizeId(manifest.AppId.ToString());
            var version = ParseVersion(manifest.Version);
            if (!present.TryGetValue(id, out var have) || (version is not null && (have is null || version > have)))
            {
                present[id] = version;
            }
        }
        return present;
    }

    private static bool Satisfies(Version? have, Version? floor) =>
        floor is null || (have is not null && have >= floor);

    // ── HTTP ────────────────────────────────────────────────────────────

    private async Task<FeedEndpoints> GetEndpointsAsync(HttpClient http, string indexUrl, CancellationToken ct)
    {
        if (_endpoints.TryGetValue(indexUrl, out var known)) return known;
        using var doc = await GetJsonAsync(http, indexUrl, ct).ConfigureAwait(false);
        string? search = null, packageBase = null;
        foreach (var resource in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = resource.TryGetProperty("@type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var id = resource.TryGetProperty("@id", out var i) ? i.GetString() : null;
            if (id is null) continue;
            if (search is null && type.StartsWith("SearchQueryService", StringComparison.Ordinal)) search = id;
            if (packageBase is null && type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal)) packageBase = id;
        }
        if (search is null || packageBase is null)
        {
            throw new InvalidDataException("the feed's service index lists no search or package download endpoint");
        }
        var endpoints = new FeedEndpoints(search, packageBase.EndsWith('/') ? packageBase : packageBase + "/");
        _endpoints[indexUrl] = endpoints;
        return endpoints;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        var bytes = await GetBytesFollowingRedirectsAsync(http, url, ct).ConfigureAwait(false);
        return JsonDocument.Parse(bytes);
    }

    private static async Task<string> GetStringFollowingRedirectsAsync(HttpClient http, string url, CancellationToken ct) =>
        System.Text.Encoding.UTF8.GetString(await GetBytesFollowingRedirectsAsync(http, url, ct).ConfigureAwait(false));

    /// <summary>
    /// GETs <paramref name="url"/>, following up to <see cref="MaxRedirects"/>
    /// redirects by hand. The feed answers a <c>.nupkg</c> request with a 303 to a
    /// blob URL; following it here rather than in the handler keeps the rule
    /// visible and testable: every hop must stay on HTTPS.
    /// </summary>
    internal static async Task<byte[]> GetBytesFollowingRedirectsAsync(HttpClient http, string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (var hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException($"refused a non-HTTPS address ({uri.Scheme})");
            }
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                var location = response.Headers.Location
                    ?? throw new HttpRequestException($"a {status} redirect named no location");
                if (hop >= MaxRedirects) throw new HttpRequestException("too many redirects");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"HTTP {status}", null, response.StatusCode);
            }
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }

    // ── Small helpers ───────────────────────────────────────────────────

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException h when h.StatusCode is { } code => $"HTTP {(int)code}",
        TaskCanceledException => "timed out",
        _ => ex.Message,
    };

    private static UnresolvedSymbol Unresolved(Pending p, string reason) =>
        new(p.AppId, p.Name, p.MinVersion?.ToString(), reason);

    internal static string NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? string.Empty : id.Trim().Trim('{', '}').ToLowerInvariant();

    internal static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0) text = text[..cut];
        if (!text.Contains('.')) text += ".0";
        return Version.TryParse(text, out var v) ? v : null;
    }

    private sealed record Pending(string AppId, string? Name, Version? MinVersion, string? PackageIdHint);

    private sealed record FeedEndpoints(string Search, string PackageBase);

    private sealed record CacheSource(string Feed, string PackageId);

    private sealed record CachedPackage(
        string AppId, string Name, string Version, string Feed, string AppPath, string FileName, NuspecInfo Nuspec, bool FromCache)
    {
        public IReadOnlyList<NuspecDependency> Dependencies => Nuspec.Dependencies;
    }
}

/// <summary>What a build asks the feeds for. See <see cref="AlSymbolFeedResolver.ResolveAsync"/>.</summary>
/// <param name="Dependencies">The apps the build's extensions declare, each with the lowest version they accept.</param>
/// <param name="TargetDirectory">The build's shared package cache; resolved <c>.app</c>s are copied in here.</param>
/// <param name="ProvidedAppIds">Lower-cased app ids something else supplies (siblings the build compiles, stored uploads); never fetched.</param>
/// <param name="ApplicationVersion">The Business Central Major.Minor the build compiles against; a package built for a newer one is passed over.</param>
/// <param name="Country">The build's country, to pick a localised Microsoft package over the worldwide one.</param>
public sealed record SymbolFeedRequest(
    IReadOnlyList<SymbolDependency> Dependencies,
    string TargetDirectory,
    IReadOnlySet<string> ProvidedAppIds,
    string? ApplicationVersion,
    string? Country);

/// <summary>One app a build needs, by id, with the lowest version it accepts.</summary>
public sealed record SymbolDependency(string AppId, string? Name, string? MinVersion);

/// <summary>A package copied into the build's package cache, and where it came from.</summary>
public sealed record ResolvedSymbolPackage(string AppId, string Name, string Version, string Feed, string FileName, bool FromCache);

/// <summary>An app no feed could supply, and why - the reason names the feeds tried.</summary>
public sealed record UnresolvedSymbol(string AppId, string? Name, string? MinVersion, string Reason);

/// <summary>The result of one <see cref="AlSymbolFeedResolver.ResolveAsync"/> call.</summary>
public sealed record SymbolFeedOutcome(IReadOnlyList<ResolvedSymbolPackage> Resolved, IReadOnlyList<UnresolvedSymbol> Unresolved);

/// <summary>What a symbols package's nuspec says: the app's name, the apps it needs, and the base-app floors it was built on.</summary>
public sealed record NuspecInfo(string Name, IReadOnlyList<NuspecDependency> Dependencies, IReadOnlyList<Version> BaseFloors);

/// <summary>One app-id dependency from a nuspec, with the exact package id the publisher named.</summary>
public sealed record NuspecDependency(string AppId, Version? MinVersion, string PackageId);
