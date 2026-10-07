using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Provisions the AL compiler (<c>alc</c>) at runtime rather than baking it into
/// the image, so a new compiler version never requires an image rebuild. The
/// compiler ships in the <c>Microsoft.Dynamics.BusinessCentral.Development.Tools</c>
/// NuGet package as a framework-dependent <c>tools/&lt;tfm&gt;/any/alc.dll</c>
/// that the host's <c>dotnet</c> runs; this service downloads the <c>.nupkg</c>
/// (a zip), extracts that folder flat into a per-version folder on the
/// <c>app-altool</c> volume and records what it installed - no SDK, no <c>dotnet tool install</c> (which
/// rejects these packages). Volumes provisioned before #921 hold the
/// <c>.Linux</c> package's <c>lib/&lt;tfm&gt;/alc</c> apphost instead; that
/// layout is still recognised and run as it was, so an existing install never
/// re-downloads. See <c>.design/object-explorer-project-builds.md</c>.
///
/// <para>
/// Singleton: it owns a shared on-disk resource (the volume) guarded by a
/// semaphore. Provisioning is lazy (first build / admin action), so a NuGet
/// outage never blocks app startup — the feature just reports itself unavailable.
/// </para>
/// </summary>
public sealed class AlCompilerProvisioner
{
    /// <summary>
    /// The AL compiler NuGet package id (lower-cased for the flat-container API).
    /// Not the <c>.Linux</c> package: from 18.x that one carries only the code
    /// analyzers, and the compiler itself is the framework-dependent
    /// <c>alc.dll</c> in this package (#921).
    /// </summary>
    public const string PackageId = "microsoft.dynamics.businesscentral.development.tools";

    /// <summary>
    /// How many versions, newest first, a provisioning pass tries when no pin is
    /// set and a package turns out to carry no compiler. Microsoft has shipped
    /// such packages (#921); a bounded walk finds the newest real one without
    /// turning an odd feed into a forty-download loop.
    /// </summary>
    internal const int MaxCandidates = 3;

    private const string IndexUrl =
        "https://api.nuget.org/v3-flatcontainer/" + PackageId + "/index.json";

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AlCompilerProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Compilers a running build is using, by version, so a prune never deletes one
    // under it now that builds run side by side (#1137).
    private readonly InUseLeases<string> _inUse = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _installDir;
    private readonly string? _versionPin;
    private readonly string? _explicitAlcPath;

    public AlCompilerProvisioner(
        IHttpClientFactory httpFactory,
        ILogger<AlCompilerProvisioner> logger,
        // Optional so hand-built instances keep compiling; DI always supplies
        // the instance registered from configuration.
        AlCompilerOptions? options = null)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        var compilerOptions = options ?? new AlCompilerOptions();
        _installDir = compilerOptions.InstallDirectory;
        _versionPin = compilerOptions.VersionPin;
        _explicitAlcPath = compilerOptions.ExplicitAlcPath;
    }

    /// <summary>
    /// Each compiler lives in its own folder, <c>{AL_COMPILER_DIR}/{version}/</c>,
    /// holding a <c>bin/</c> and its own <c>installed.json</c>, so a build for the
    /// next major can run a beta beside the stable compiler every other build
    /// uses. Installs made before #993 sit at the root (<see cref="LegacyBinDir"/>,
    /// <see cref="LegacyMarkerPath"/>) and are moved under their version on first use.
    /// </summary>
    private string VersionDir(string version) => Path.Combine(_installDir, RequireSafeVersion(version));
    private string BinDir(string version) => Path.Combine(VersionDir(version), "bin");
    private string MarkerPath(string version) => Path.Combine(VersionDir(version), "installed.json");
    private string LegacyBinDir => Path.Combine(_installDir, "bin");
    private string LegacyMarkerPath => Path.Combine(_installDir, "installed.json");

    private volatile bool _migrated;

    /// <summary>
    /// Ensures a usable <c>alc</c> is present and returns how to invoke it, or
    /// <see langword="null"/> when the compiler can't be provisioned (offline with
    /// an empty volume). Safe to call before every build.
    ///
    /// <para>
    /// The stable line (<paramref name="prerelease"/> false, what every build but
    /// a next-major one asks for) behaves as it always has: the pinned version
    /// when <c>AL_COMPILER_VERSION</c> is set, otherwise whichever stable compiler
    /// is already installed, and only when there is none, the newest stable one
    /// NuGet lists. With <paramref name="prerelease"/> true the feed is read on
    /// every call, because betas move weekly, and the newest prerelease newer
    /// than the newest stable release is used (see <see cref="PickPrereleaseCandidates"/>);
    /// when the feed has none, or cannot be reached and no beta is installed, the
    /// stable line answers instead. The pin never applies to the prerelease line.
    /// </para>
    /// </summary>
    public async Task<AlCompilerInfo?> ResolveAsync(bool prerelease = false, CancellationToken ct = default)
    {
        if (_explicitAlcPath is not null)
        {
            return File.Exists(_explicitAlcPath)
                ? new AlCompilerInfo(_explicitAlcPath, NeedsRollForward(_explicitAlcPath), "(pinned path)")
                : null;
        }

        await EnsureMigratedAsync(ct).ConfigureAwait(false);

        if (prerelease && await ResolvePrereleaseAsync(ct).ConfigureAwait(false) is { } beta) return beta;
        return await ResolveStableAsync(ct).ConfigureAwait(false);
    }

    private async Task<AlCompilerInfo?> ResolveStableAsync(CancellationToken ct)
    {
        if (_versionPin is not null)
        {
            var pinnedFolder = InstalledVersions().FirstOrDefault(v => string.Equals(v, _versionPin, StringComparison.OrdinalIgnoreCase));
            if (pinnedFolder is not null && Installed(pinnedFolder) is { } pinned) return pinned;
        }
        else if (InstalledVersions().Where(v => !IsPrerelease(v)).OrderByDescending(ToSortable).FirstOrDefault() is { } current
                 && Installed(current) is { } installed)
        {
            return installed;
        }

        // Nothing installed yet (or not the pinned one) - provision the target version.
        IReadOnlyList<string> candidates;
        try
        {
            candidates = PickCandidates(await FetchVersionsAsync(ct), _versionPin);
        }
        catch (Exception ex)
        {
            // Offline: whatever the volume already holds beats no compiler at all,
            // which is what a single-install volume always did.
            if (AnyInstalled() is { } fallback)
            {
                _logger.LogWarning(ex, "NuGet is unreachable; building with the installed AL compiler {Version}.", fallback.Version);
                return fallback;
            }
            _logger.LogError(ex, "AL compiler is not installed and NuGet is unreachable; project builds are unavailable.");
            return null;
        }
        return await ProvisionFirstAsync(candidates, lenient: _versionPin is null, ct).ConfigureAwait(false)
            ?? AnyInstalled();
    }

    /// <summary>The newest installed stable compiler, else the newest installed of any kind.</summary>
    private AlCompilerInfo? AnyInstalled() => InstalledVersions()
        .OrderBy(IsPrerelease)
        .ThenByDescending(ToSortable)
        .Select(Installed)
        .FirstOrDefault(i => i is not null);

    /// <summary>
    /// <see cref="ResolveAsync"/> for a build: the compiler comes with a lease that
    /// keeps its folder on the volume until the lease is disposed. Null when there is
    /// no compiler.
    /// </summary>
    public async Task<InUseLease<AlCompilerInfo>?> UseAsync(bool prerelease = false, CancellationToken ct = default)
    {
        // A newer beta can replace the one just resolved before the lease is taken;
        // resolving again finds the newer one.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var compiler = await ResolveAsync(prerelease, ct).ConfigureAwait(false);
            if (compiler is null) return null;
            if (_inUse.TryHold(compiler.Version, () => File.Exists(compiler.AlcPath), compiler) is { } lease) return lease;
        }
        return null;
    }

    /// <summary>The newest prerelease compiler, provisioned when needed, or null when the stable line should answer instead.</summary>
    private async Task<AlCompilerInfo?> ResolvePrereleaseAsync(CancellationToken ct)
    {
        IReadOnlyList<string> candidates;
        try
        {
            candidates = PickPrereleaseCandidates(await FetchVersionsAsync(ct));
        }
        catch (Exception ex)
        {
            // Offline: a beta already on the volume is better than none.
            var newestInstalled = InstalledVersions().Where(IsPrerelease).OrderByDescending(ToSortable).FirstOrDefault();
            _logger.LogWarning(ex, "Could not read the AL compiler feed for a prerelease; using {Version}.",
                newestInstalled ?? "the stable compiler");
            return newestInstalled is null ? null : Installed(newestInstalled);
        }
        if (candidates.Count == 0) return null;
        if (Installed(candidates[0]) is { } installed) return installed;

        var provisioned = await ProvisionFirstAsync(candidates, lenient: true, ct).ConfigureAwait(false);
        if (provisioned is not null)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { PruneOtherPrereleases(provisioned.Version); }
            finally { _gate.Release(); }
        }
        return provisioned;
    }

    /// <summary>
    /// Installs the first of <paramref name="candidates"/> that turns out to be a
    /// compiler and returns it. <paramref name="lenient"/> lets a package that is
    /// not one be skipped for the next; a pinned version gets no such leniency.
    /// </summary>
    private async Task<AlCompilerInfo?> ProvisionFirstAsync(IReadOnlyList<string> candidates, bool lenient, CancellationToken ct)
    {
        foreach (var version in candidates)
        {
            try
            {
                await ProvisionVersionAsync(version, ct).ConfigureAwait(false);
                return Installed(version);
            }
            catch (AlCompilerPackageException ex) when (lenient)
            {
                // A package without a compiler, or one the feed lists but will not
                // serve, is a feed quirk, not a fault of ours: say so and try the next older one. A pinned version gets no such
                // leniency - the operator asked for exactly that one.
                _logger.LogWarning("AL compiler package {Version} is not installable ({Reason}); trying the next older version.",
                    version, ex.Message);
            }
        }
        return null;
    }

    /// <summary>What the volume holds for <paramref name="version"/>, in either package layout, or null when it isn't usably installed.</summary>
    private AlCompilerInfo? Installed(string version)
    {
        var marker = ReadMarker(MarkerPath(version));
        if (marker is null) return null;
        // Markers written before #921 name no entry: those installs are the
        // .Linux package's apphost.
        var entry = Path.Combine(BinDir(version), marker.Entry ?? ApphostEntry);
        return File.Exists(entry) ? new AlCompilerInfo(entry, marker.Tfm == "net8.0", marker.Version) : null;
    }

    /// <summary>The versions with a folder and a marker on the volume.</summary>
    private IEnumerable<string> InstalledVersions()
    {
        if (!Directory.Exists(_installDir)) return [];
        return Directory.EnumerateDirectories(_installDir)
            .Select(Path.GetFileName)
            .Where(name => name is not null && IsSafeVersion(name) && File.Exists(Path.Combine(_installDir, name, "installed.json")))
            .Select(name => name!)
            .ToList();
    }

    /// <summary>
    /// Drops every installed prerelease but <paramref name="keep"/> (and the pin,
    /// if an operator pinned a beta), so weekly betas do not pile up on the
    /// volume. Best-effort; runs under the gate after a new beta is in place.
    /// A beta a running build holds a lease on stays; a later prune removes it.
    /// </summary>
    private void PruneOtherPrereleases(string keep)
    {
        _inUse.Locked(isHeld =>
        {
            foreach (var version in InstalledVersions().Where(IsPrerelease))
            {
                if (string.Equals(version, keep, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(version, _versionPin, StringComparison.OrdinalIgnoreCase)
                    || isHeld(version)) continue;
                try
                {
                    Directory.Delete(VersionDir(version), recursive: true);
                    _logger.LogInformation("Removed AL compiler {Version}; {Keep} replaces it for next-major builds.", version, keep);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not remove the older AL compiler {Version}.", version);
                }
            }
        });
    }

    /// <summary>
    /// Moves an install made before compilers were kept side by side (<c>bin/</c>
    /// and <c>installed.json</c> at the root) under its own version folder, so an
    /// upgraded deployment keeps the compiler it has instead of downloading it
    /// again. Written so a crash part-way leaves something the next start finishes.
    /// </summary>
    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (_migrated) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_migrated) return;
            var marker = ReadMarker(LegacyMarkerPath);
            if (marker is not null && IsSafeVersion(marker.Version))
            {
                var dir = VersionDir(marker.Version);
                Directory.CreateDirectory(dir);
                if (Directory.Exists(LegacyBinDir))
                {
                    var bin = BinDir(marker.Version);
                    if (Directory.Exists(bin)) Directory.Delete(bin, recursive: true);
                    Directory.Move(LegacyBinDir, bin);
                }
                if (!File.Exists(MarkerPath(marker.Version))) WriteMarker(MarkerPath(marker.Version), marker);
                File.Delete(LegacyMarkerPath);
                _logger.LogInformation("Moved the installed AL compiler {Version} into its own folder.", marker.Version);
            }
            _migrated = true;
        }
        catch (Exception ex)
        {
            // Leave the flag down so the next build tries again; this one can
            // still provision a compiler of its own.
            _logger.LogWarning(ex, "Could not move the installed AL compiler into its version folder.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads and installs a specific compiler version into its own folder
    /// on the volume, beside any other versions already there. Called lazily by
    /// <see cref="ResolveAsync"/>. Serialised by the gate so two builds never
    /// provision at once.
    /// </summary>
    public async Task ProvisionVersionAsync(string version, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!IsSafeVersion(version))
            {
                // A feed entry that cannot name a folder is a feed quirk; the next one may install.
                throw new AlCompilerPackageException($"'{version}' is not an AL compiler version this server can install.");
            }
            // Re-check under the lock: another caller may have just installed it.
            if (Installed(version) is not null)
            {
                return;
            }

            // The marker goes first, so a crash part-way through the extract never
            // leaves it standing over a half-filled bin folder.
            var versionDir = VersionDir(version);
            Directory.CreateDirectory(versionDir);
            if (File.Exists(MarkerPath(version))) File.Delete(MarkerPath(version));
            try
            {
                await ProvisionIntoAsync(version, ct).ConfigureAwait(false);
            }
            catch
            {
                // Nothing usable was installed: drop the folder rather than leave an
                // unmarked one no cleanup would ever find.
                try { Directory.Delete(versionDir, recursive: true); } catch { /* best-effort */ }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads, verifies and extracts <paramref name="version"/> into its folder, marker last. Caller holds the gate.</summary>
    private async Task ProvisionIntoAsync(string version, CancellationToken ct)
    {
        {
            var lower = version.ToLowerInvariant();
            var url = $"https://api.nuget.org/v3-flatcontainer/{PackageId}/{lower}/{PackageId}.{lower}.nupkg";

            var http = _httpFactory.CreateClient();
            _logger.LogInformation("Provisioning AL compiler {Version} from NuGet.", version);
            MemoryStream buffer;
            try
            {
                await using var nupkg = await http.GetStreamAsync(url, ct).ConfigureAwait(false);
                buffer = await BufferAsync(nupkg, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Listed in the index, but the blob is gone: an unlisted or
                // half-published version. Not ours to install; the next older one may be.
                throw new AlCompilerPackageException($"AL compiler package {version} is listed but not downloadable (404).", ex);
            }
            using var _ = buffer;
            // alc runs with the app's privileges over attacker-influenced source,
            // so verify the download against NuGet's published SHA-512 before
            // extracting/executing it. AL_COMPILER_VERSION should be pinned in
            // production (the default picks the newest published version). See #429.
            await VerifyPackageHashAsync(http, url, buffer, version, ct).ConfigureAwait(false);
            buffer.Position = 0;
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Read);

            var layout = PickLayout(zip.Entries.Select(e => e.FullName))
                ?? throw new AlCompilerPackageException($"AL compiler package {version} has no tools/<tfm>/any/alc.dll and no lib/<tfm>/alc.");

            // Fresh bin dir, then extract the chosen folder flat into it.
            var binDir = BinDir(version);
            if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true);
            Directory.CreateDirectory(binDir);

            var prefix = layout.Prefix;
            var binRoot = Path.GetFullPath(binDir) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.Length <= prefix.Length || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
                    continue;
                var relative = entry.FullName[prefix.Length..];
                if (relative.EndsWith('/')) continue; // directory entry
                var dest = Path.Combine(binDir, relative);
                // Zip-slip guard: a package entry with `..` segments could escape
                // BinDir and overwrite process-writable files (the app-keys ring,
                // the backups volume). The package origin is HTTPS-pinned but
                // unverified (no hash/signature, and AL_COMPILER_VERSION can point
                // at any version), so don't trust the entry path. See issue #427.
                var full = Path.GetFullPath(dest);
                if (!full.StartsWith(binRoot, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"AL compiler package entry '{entry.FullName}' escapes the install directory.");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }

            // The apphost binaries are extracted without the execute bit.
            foreach (var name in new[] { "alc", "altool" })
            {
                var p = Path.Combine(binDir, name);
                if (File.Exists(p))
                    File.SetUnixFileMode(p, File.GetUnixFileMode(p)
                        | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            if (!File.Exists(Path.Combine(binDir, layout.Entry)))
                throw new AlCompilerPackageException($"AL compiler package {version} ({prefix}) did not contain '{layout.Entry}'.");

            WriteMarker(MarkerPath(version), new InstalledMarker(version, layout.Tfm, layout.Entry));
            _logger.LogInformation("Installed AL compiler {Version} ({Prefix}{Entry}).", version, prefix, layout.Entry);
        }
    }

    private async Task<IReadOnlyList<string>> FetchVersionsAsync(CancellationToken ct)
    {
        var http = _httpFactory.CreateClient();
        await using var stream = await http.GetStreamAsync(IndexUrl, ct).ConfigureAwait(false);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return doc.RootElement.GetProperty("versions").EnumerateArray()
            .Select(e => e.GetString()!).Where(v => v is not null).ToList();
    }

    /// <summary>
    /// Picks the version to install: the pin when set (and present), otherwise the
    /// newest stable release. The NuGet flat-container index lists versions in
    /// SemVer-ascending order, so the newest is the last entry.
    /// </summary>
    public static string? PickNewest(IReadOnlyList<string> versions, string? pin) =>
        PickCandidates(versions, pin).FirstOrDefault();

    /// <summary>
    /// The versions to try, in order: just the pin when one is set (and present),
    /// otherwise the newest <see cref="MaxCandidates"/> stable releases, newest
    /// first, so a package that carries no compiler is skipped for the one before
    /// it. A prerelease is never picked by default - a beta compiler is a choice
    /// an operator makes with <c>AL_COMPILER_VERSION</c> - unless the feed holds
    /// nothing else.
    /// </summary>
    public static IReadOnlyList<string> PickCandidates(IReadOnlyList<string> versions, string? pin)
    {
        if (versions.Count == 0) return [];
        if (!string.IsNullOrWhiteSpace(pin))
        {
            var match = versions.FirstOrDefault(v => string.Equals(v, pin, StringComparison.OrdinalIgnoreCase));
            return match is null ? [] : [match];
        }
        var stable = versions.Where(v => !v.Contains('-')).ToList();
        var pool = stable.Count > 0 ? stable : versions.ToList();
        pool.Reverse();
        return pool.Take(MaxCandidates).ToList();
    }

    /// <summary>
    /// The prerelease versions a next-major build tries, newest first: those
    /// numbered above the newest stable release, at most <see cref="MaxCandidates"/>.
    /// A beta below the newest stable one is a leftover of a line that has since
    /// shipped, not the next compiler, so it is never picked; the feed then holds
    /// no next-major compiler and the caller falls back to the stable one. The
    /// feed does not say which Business Central major a compiler serves, so
    /// "the newest beta ahead of stable" is the rule rather than a mapping.
    /// </summary>
    public static IReadOnlyList<string> PickPrereleaseCandidates(IReadOnlyList<string> versions)
    {
        var newestStable = versions.Where(v => !IsPrerelease(v)).Select(ToSortable).DefaultIfEmpty(new Version(0, 0)).Max()!;
        return versions
            .Where(IsPrerelease)
            .Where(v => IsSafeVersion(v) && ToSortable(v) > newestStable)
            .Reverse()
            .Take(MaxCandidates)
            .ToList();
    }

    /// <summary>The apphost the <c>.Linux</c> package shipped up to 17.x; still what older volumes hold.</summary>
    internal const string ApphostEntry = "alc";

    /// <summary>The framework-dependent compiler the main package ships; run through <c>dotnet</c>.</summary>
    internal const string FrameworkDependentEntry = "alc.dll";

    /// <summary>
    /// Finds the compiler in a package: the framework-dependent
    /// <c>tools/&lt;tfm&gt;/any/alc.dll</c> the main package ships, else the
    /// <c>lib/&lt;tfm&gt;/alc</c> apphost the <c>.Linux</c> package shipped up to
    /// 17.x. Within a layout prefers <c>net10.0</c> (runs natively on the runtime
    /// image), else the highest <c>netN.0</c> (runs with roll-forward). Null when
    /// the package carries no compiler at all - the 18.x <c>.Linux</c> packages
    /// are analyzers only (#921).
    /// </summary>
    public static CompilerLayout? PickLayout(IEnumerable<string> entryNames)
    {
        var names = entryNames.ToList();
        return Find(names, "tools/", 4, FrameworkDependentEntry, p => $"tools/{p}/any/")
            ?? Find(names, "lib/", 3, ApphostEntry, p => $"lib/{p}/");

        static CompilerLayout? Find(List<string> names, string root, int depth, string entry, Func<string, string> prefixOf)
        {
            var tfms = names
                .Where(n => n.StartsWith(root, StringComparison.Ordinal))
                .Select(n => n.Split('/'))
                .Where(p => p.Length == depth && p[1].StartsWith("net", StringComparison.Ordinal) && p[^1] == entry)
                .Select(p => p[1])
                .Distinct()
                .ToList();
            if (tfms.Count == 0) return null;
            // Prefer net10.0; otherwise the highest netN.0 (lexical is wrong for net8 vs net10, so order numerically).
            var tfm = tfms.Contains("net10.0") ? "net10.0" : tfms.OrderByDescending(ParseNetMajor).First();
            return new CompilerLayout(prefixOf(tfm), tfm, entry);
        }
    }

    /// <summary>Retained for callers that only need the framework folder; see <see cref="PickLayout"/>.</summary>
    public static string? PickTfm(IEnumerable<string> entryNames) => PickLayout(entryNames)?.Tfm;

    private static int ParseNetMajor(string tfm)
    {
        var digits = new string(tfm.Skip(3).TakeWhile(c => char.IsDigit(c)).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static bool NeedsRollForward(string alcPath)
    {
        // Best-effort for the explicit-path override: a net8 runtimeconfig needs
        // roll-forward on a net10 host. Default to enabling it (harmless on net10).
        return true;
    }

    private static InstalledMarker? ReadMarker(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<InstalledMarker>(File.ReadAllText(path))
                : null;
        }
        catch { return null; }
    }

    private static void WriteMarker(string path, InstalledMarker marker) =>
        File.WriteAllText(path, JsonSerializer.Serialize(marker));

    /// <summary>
    /// A NuGet version as it may name a folder: digits and dots, then an optional
    /// SemVer prerelease tag. The pin comes from configuration and the rest from
    /// the feed, so neither is trusted to be a plain folder name without this.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SafeVersion =
        new(@"^\d+(\.\d+){1,3}(-[0-9A-Za-z][0-9A-Za-z.\-]*)?\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static bool IsSafeVersion(string? version) => version is not null && SafeVersion.IsMatch(version);

    private static string RequireSafeVersion(string version) => IsSafeVersion(version)
        ? version
        : throw new InvalidOperationException($"'{version}' is not an AL compiler version this server can install.");

    private static bool IsPrerelease(string version) => version.Contains('-');

    /// <summary>The numeric part of a version for ordering; a malformed one sorts first.</summary>
    private static Version ToSortable(string version) =>
        Version.TryParse(version.Split('-', 2)[0], out var v) ? v : new Version(0, 0);

    private static async Task<MemoryStream> BufferAsync(Stream source, CancellationToken ct)
    {
        var ms = new MemoryStream();
        await source.CopyToAsync(ms, ct).ConfigureAwait(false);
        ms.Position = 0;
        return ms;
    }

    /// <summary>
    /// Verifies the downloaded <c>.nupkg</c> against the base64 SHA-512 NuGet
    /// publishes for it, before the package is extracted and <c>alc</c> is run.
    /// Refuses to install if the hash can't be fetched or doesn't match — without
    /// this a yanked-then-republished or tampered package would become code
    /// execution in the container. See #429.
    ///
    /// <para>The hash comes from the package's registration leaf
    /// (<c>registration5-gz-semver2/{id}/{version}.json</c> → <c>catalogEntry</c>
    /// → <c>packageHash</c>), the same place the NuGet client reads it. The
    /// flat-container <c>.nupkg.sha512</c> resource this used to read answers 404
    /// on nuget.org for every package now, which silently made every fresh
    /// provisioning refuse (#921).</para>
    /// </summary>
    private async Task VerifyPackageHashAsync(
        HttpClient http, string nupkgUrl, MemoryStream content, string version, CancellationToken ct)
    {
        string expected;
        try
        {
            expected = await FetchPublishedHashAsync(http, version, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Downloadable but not registered: a half-published version. Refusing
            // to install is the point; a skippable refusal lets the walk try the
            // version before it.
            throw new AlCompilerPackageException($"AL compiler {version} has no published integrity hash; refusing to install unverified.", ex);
        }
        catch (Exception ex) when (ex is not AlCompilerPackageException)
        {
            throw new InvalidOperationException(
                $"Could not fetch the integrity hash for AL compiler {version}; refusing to install unverified.", ex);
        }

        var actual = Convert.ToBase64String(
            SHA512.HashData(content.GetBuffer().AsSpan(0, (int)content.Length)));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"AL compiler {version} failed its SHA-512 integrity check; refusing to install.");
        }
    }

    private const string RegistrationBase = "https://api.nuget.org/v3/registration5-gz-semver2/" + PackageId + "/";

    /// <summary>The base64 SHA-512 from the version's catalog entry. Throws <see cref="AlCompilerPackageException"/> when the entry names no SHA-512.</summary>
    private static async Task<string> FetchPublishedHashAsync(HttpClient http, string version, CancellationToken ct)
    {
        using var leaf = await GetJsonAsync(http, RegistrationBase + version.ToLowerInvariant() + ".json", ct).ConfigureAwait(false);
        var catalogUrl = leaf.RootElement.GetProperty("catalogEntry").GetString()
            ?? throw new AlCompilerPackageException($"AL compiler {version} has no catalog entry in its registration.");
        using var entry = await GetJsonAsync(http, catalogUrl, ct).ConfigureAwait(false);
        var algorithm = entry.RootElement.TryGetProperty("packageHashAlgorithm", out var a) ? a.GetString() : null;
        var hash = entry.RootElement.TryGetProperty("packageHash", out var h) ? h.GetString() : null;
        if (!string.Equals(algorithm, "SHA512", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(hash))
        {
            throw new AlCompilerPackageException($"AL compiler {version} publishes no SHA-512 hash (algorithm '{algorithm}').");
        }
        return hash.Trim();
    }

    /// <summary>Reads a JSON document, inflating it when nuget.org serves it gzip-encoded (the registration resource always does).</summary>
    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (response.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            await using var inflated = new GZipStream(body, CompressionMode.Decompress);
            return await JsonDocument.ParseAsync(inflated, cancellationToken: ct).ConfigureAwait(false);
        }
        return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    /// <summary><paramref name="Entry"/> is null on markers written before #921, which installed the apphost.</summary>
    private sealed record InstalledMarker(string Version, string Tfm, string? Entry = null);
}

/// <summary>Where a package keeps its compiler: the folder to extract flat, its framework, and the file to run.</summary>
public sealed record CompilerLayout(string Prefix, string Tfm, string Entry);

/// <summary>A downloaded package that is not a compiler - the next older version may be.</summary>
public sealed class AlCompilerPackageException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// How to invoke the resolved compiler. <see cref="AlcPath"/> is either the
/// apphost (<c>.../alc</c>, run directly) or the framework-dependent
/// <c>.../alc.dll</c>, which the host's <c>dotnet</c> runs; <see cref="FileName"/>
/// and <see cref="LeadingArguments"/> hide that difference from the build.
/// </summary>
public sealed record AlCompilerInfo(string AlcPath, bool NeedsRollForward, string Version)
{
    public bool IsFrameworkDependent => AlcPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    /// <summary>The process to start: <c>dotnet</c> for a framework-dependent compiler, else the apphost itself.</summary>
    public string FileName => IsFrameworkDependent ? "dotnet" : AlcPath;

    /// <summary>What goes before the compiler's own arguments: the dll path when <c>dotnet</c> hosts it, else nothing.</summary>
    public IReadOnlyList<string> LeadingArguments => IsFrameworkDependent ? [AlcPath] : [];
}
