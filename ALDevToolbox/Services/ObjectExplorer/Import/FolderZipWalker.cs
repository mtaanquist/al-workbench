using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Walks a single ZIP that wraps a DVD-style <c>applications/</c> folder tree
/// and returns one <see cref="FolderZipEntry"/> per <c>.app</c> file, paired
/// with the sibling <c>.Source.zip</c> when one is present in the same
/// directory. Used by the admin bulk-upload endpoint so admins can drop the
/// whole 100+ app DVD as one upload rather than picking each pair by hand.
///
/// Flag inference follows the documented Microsoft conventions:
/// <list type="bullet">
///   <item><c>IsTest</c> — any ancestor directory name matches one of
///         <c>Test</c> / <c>Tests</c> / <c>Test Library</c> / <c>TestLibraries</c>
///         / <c>TestFramework</c> (case-insensitive).</item>
///   <item><c>IsInternal</c> — the <c>.app</c> filename contains the
///         <c>_Exclude_</c> marker Microsoft uses for platform-internal apps
///         that ship on the DVD but aren't part of the public product.</item>
///   <item><c>IsLanguagePack</c> — the <c>.app</c> filename matches the
///         <c>&lt;Language&gt; language (&lt;Region&gt;)</c> pattern
///         Microsoft's translation-only modules use.</item>
/// </list>
/// </summary>
public static class FolderZipWalker
{
    private static readonly HashSet<string> TestFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test",
        "Tests",
        "Test Library",
        "Test Libraries",
        "TestLibraries",
        "TestFramework",
    };

    /// <summary>
    /// Suffixes a segment can end with to still count as a test-extension
    /// folder, in addition to the exact <see cref="TestFolderNames"/> set.
    /// Catches modern BC DVD shapes like <c>Application Test Library/</c> where
    /// the test toolkit ships as its own top-level <em>extension</em> folder
    /// (rather than as a <c>Test/</c> subfolder under the product app). The
    /// leading space is the word-boundary safety net so we don't false-positive
    /// on names like <c>MyTestLibrary</c>.
    /// </summary>
    private static readonly string[] TestFolderSuffixes =
    {
        " Test Library",
        " Test Libraries",
        " Test Toolkit",
        " Tests",
    };

    // The DVD root folder that holds the product apps. Microsoft has used a few
    // names across BC versions (and the casing varies), so match a set,
    // case-insensitively. EXTENDING: if a future/older DVD nests its apps under
    // a different folder, the zero-match diagnostic in ReleaseImportWorker names
    // the folder it actually found — add that name here.
    private static readonly HashSet<string> DvdAppFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Applications",
        "Extensions",
    };

    // Matches "<X> language (<Y>)" at the *end* of an .app filename stem.
    // The Microsoft DVD uses this exact shape for every translation-only
    // app (e.g. "Microsoft_Danish language (Denmark).app"). The leading
    // publisher prefix is irrelevant — we anchor on the right side.
    private static readonly Regex LanguagePackPattern = new(
        @"\blanguage\s*\([^)]+\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Walks every <c>.app</c> in the archive (the uploaded-folder-ZIP case),
    /// or only those an optional <paramref name="includeApp"/> predicate
    /// accepts. The predicate receives each <c>.app</c> entry's full path; when
    /// <see langword="null"/> every <c>.app</c> is included.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> Walk(ZipArchive archive, Func<string, bool>? includeApp = null)
    {
        ArgumentNullException.ThrowIfNull(archive);

        // Index source zips by their containing directory + basename stem so
        // we can pair them up in one pass. Paths are normalised first because
        // some Microsoft DVD ZIPs (e.g. BC 26.x) store entries with backslash
        // separators, which would otherwise collapse every path to a single
        // segment and break folder matching + name extraction.
        var sourceZips = new Dictionary<(string Directory, string Stem), ZipArchiveEntry>(
            new DirectoryStemComparer());
        foreach (var e in archive.Entries)
        {
            var full = Normalize(e.FullName);
            var name = LeafName(full);
            if (string.IsNullOrEmpty(name)) continue;
            if (!full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = StripSourceSuffix(Path.GetFileNameWithoutExtension(name));
            sourceZips[(GetDirectory(full), stem)] = e;
        }

        var result = new List<FolderZipEntry>();
        foreach (var entry in archive.Entries)
        {
            var full = Normalize(entry.FullName);
            var name = LeafName(full);
            if (string.IsNullOrEmpty(name)) continue;
            if (!full.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (includeApp is not null && !includeApp(full)) continue;

            var dir = GetDirectory(full);
            var appStem = StripPublisherPrefix(Path.GetFileNameWithoutExtension(name));

            ZipArchiveEntry? pairedSource = null;
            // Try a few stem variants so common filename conventions on the
            // DVD all pair correctly:
            //   Microsoft_Base Application.app ↔ Base Application.Source.zip
            //   _Exclude_Foo.app               ↔ _Exclude_Foo.Source.zip
            foreach (var candidate in PairingCandidates(name, appStem))
            {
                if (sourceZips.TryGetValue((dir, candidate), out var src))
                {
                    pairedSource = src;
                    break;
                }
            }

            result.Add(new FolderZipEntry(
                FileName: name,
                AppEntry: entry,
                SourceZipEntry: pairedSource,
                IsTest: HasTestAncestor(full),
                IsInternal: name.Contains("_Exclude_", StringComparison.OrdinalIgnoreCase),
                IsLanguagePack: LanguagePackPattern.IsMatch(Path.GetFileNameWithoutExtension(name))));
        }
        return result;
    }

    /// <summary>Unifies backslash- and forward-slash-separated ZIP entry paths to '/'.</summary>
    private static string Normalize(string fullName) => fullName.Replace('\\', '/');

    /// <summary>Last path segment of an already-normalised entry path (the file name).</summary>
    private static string LeafName(string normalizedFullName)
    {
        var slash = normalizedFullName.LastIndexOf('/');
        return slash >= 0 ? normalizedFullName[(slash + 1)..] : normalizedFullName;
    }

    /// <summary>
    /// Walks a full BC DVD ZIP, keeping only the apps that matter for the
    /// Object Explorer: every <c>.app</c> under one of the recognised app
    /// folders (<see cref="DvdAppFolderNames"/> — <c>Applications/</c> on modern
    /// DVDs, <c>Extensions/</c> on others, matched case-insensitively) plus the
    /// platform symbols app named exactly <c>System.app</c> (it lives under
    /// <c>ModernDev/PFiles/Microsoft Dynamics NAV/&lt;ver&gt;/AL Development
    /// Environment/</c>). Test extensions and their source are dropped entirely
    /// — any <c>.app</c> under a <c>Test*</c> folder is skipped, unlike
    /// <see cref="Walk(ZipArchive, Func{string, bool})"/> which imports them
    /// flagged.
    ///
    /// The match deliberately anchors only on the app-folder name and the
    /// literal <c>System.app</c> filename, not on the version segment, so a BC
    /// version bump needs no code change. If Microsoft nests the apps under a
    /// folder we don't recognise, this returns an empty list and the caller
    /// surfaces a diagnostic naming the folder it actually found.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> WalkDvd(ZipArchive archive) =>
        Walk(archive, IsWantedDvdApp);

    private static bool IsWantedDvdApp(string fullName)
    {
        if (HasTestAncestor(fullName)) return false;

        var segments = fullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (DvdAppFolderNames.Contains(segment)) return true;
        }

        var name = segments.Length > 0 ? segments[^1] : fullName;
        return string.Equals(name, "System.app", StringComparison.OrdinalIgnoreCase);
    }

    // ── Microsoft artifact layout (BcContainerHelper Get-BCArtifactUrl) ──
    //
    // A downloaded BC OnPrem artifact is NOT shaped like the DVD. The country
    // (application) artifact holds the *localized* apps under
    // `Applications.<COUNTRY>/` (e.g. `Applications.DK/`) plus first-party
    // `Extensions/`, with VERSIONED `.app` filenames
    // (`Microsoft_Base Application_25.5.30849.48785.app`) whose paired
    // `.Source.zip` is named without the version (`Base Application.Source.zip`).
    // Its test apps are flat (named `Tests-*`, `* Test Library`, …) rather than
    // under a `Test/` folder. The separate platform artifact is the classic W1
    // DVD (`platform/Applications/`, unversioned names) plus the unique
    // `System.app` — and its W1 apps collide (same AppId+Version, different
    // bytes) with the localized ones, so we take ONLY `System.app` from it.
    // See `.design/object-explorer.md` "Importing from Microsoft artifacts".

    /// <summary>
    /// Walks the <em>application</em> (country) artifact: every <c>.app</c> under
    /// an <c>Applications</c> / <c>Applications.&lt;country&gt;</c> /
    /// <c>Extensions</c> / <c>Extensions.&lt;country&gt;</c> folder, minus test
    /// apps (by <c>Test*</c> folder ancestor or by the flat test-app naming the
    /// artifact uses). Version-aware <c>.Source.zip</c> pairing is handled by
    /// <see cref="PairingCandidates"/>.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> WalkBcArtifactApplication(ZipArchive archive) =>
        PreferCountryFolderCopy(Walk(archive, IsWantedArtifactApp));

    /// <summary>
    /// The insider (preview) Sandbox artifact ships the core apps twice under the
    /// same filename: once in <c>Applications.&lt;country&gt;/</c> beside their
    /// <c>.Source.zip</c>, and once in <c>Extensions/</c> as different bytes (the
    /// install copy). Same <c>(AppId, Version)</c>, different hash - exactly what
    /// the importer's collision guard refuses - so when a leaf name appears in
    /// both places only the country-folder copy is kept. An app that appears
    /// once, in either folder, is untouched.
    /// </summary>
    private static IReadOnlyList<FolderZipEntry> PreferCountryFolderCopy(IReadOnlyList<FolderZipEntry> entries) =>
        entries
            .GroupBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1
                ? g.First()
                : g.OrderBy(e => IsUnderApplicationsFolder(Normalize(e.AppEntry.FullName)) ? 0 : 1).First())
            .ToList();

    private static bool IsUnderApplicationsFolder(string normalizedFullName) =>
        normalizedFullName.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment =>
            string.Equals(segment, "Applications", StringComparison.OrdinalIgnoreCase)
            || segment.StartsWith("Applications.", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Walks the <em>platform</em> artifact for the single thing the application
    /// artifact lacks and the resolver wants: the platform symbols
    /// <c>System.app</c>. Everything else under the platform's W1
    /// <c>Applications/</c> duplicates the localized apps and would abort the
    /// import on an <c>(AppId, Version)</c> collision, so it's deliberately skipped.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> WalkBcArtifactPlatform(ZipArchive archive) =>
        Walk(archive, p => string.Equals(LeafName(p), "System.app", StringComparison.OrdinalIgnoreCase));

    // Microsoft's test-toolkit and dev-tooling apps ship flat inside
    // `Applications.<country>/` with ordinary names (no `Test/` folder, and some
    // without "test" in the name at all), so the folder rule and the suffix
    // heuristic below can't catch them. This is the exact-name drop-list for the
    // ones that aren't product apps — the standard AL-Go "test toolkit" set plus
    // the build/demo tooling. EXTENDING: add a name here (publisher prefix and
    // version already stripped) if a new release ships another non-product app
    // flat in the country folder.
    private static readonly HashSet<string> ArtifactNonProductAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Any",
        "Library Assert",
        "Library Variable Storage",
        "Performance Toolkit",
        "Performance Toolkit Samples",
        "AI Test Toolkit",
        "Permissions Mock",
        "Test Runner",
        "BuildTools",
        "DemoTool",
    };

    private static bool IsWantedArtifactApp(string fullName)
    {
        if (HasTestAncestor(fullName)) return false;
        if (IsArtifactNonProductApp(LeafName(fullName))) return false;

        foreach (var segment in fullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (DvdAppFolderNames.Contains(segment)) return true;
            // `Applications.DK`, `Extensions.W1`, … — the country-suffixed forms.
            foreach (var folder in DvdAppFolderNames)
            {
                if (segment.StartsWith(folder + ".", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Recognises the flat test / test-framework / dev-tooling apps the country
    /// artifact ships inside <c>Applications.&lt;country&gt;</c> (no <c>Test/</c>
    /// folder to key off), so the import keeps only the product apps. Catches the
    /// pattern cases (<c>Tests-*</c>, anything ending in <c>Test</c> /
    /// <c>Tests</c>, the <c>Test Library</c> / <c>Test Libraries</c> /
    /// <c>Test Toolkit</c> / <c>Test Runner</c> kits) plus the
    /// <see cref="ArtifactNonProductAppNames"/> exact-name list for the ones a
    /// name pattern can't safely catch (<c>Any</c>, <c>Library Assert</c>, …).
    /// </summary>
    private static bool IsArtifactNonProductApp(string fileName)
    {
        var stem = SplitAppNameAndVersion(
            StripPublisherPrefix(Path.GetFileNameWithoutExtension(fileName))).Stem;

        if (ArtifactNonProductAppNames.Contains(stem)) return true;

        if (stem.StartsWith("Tests-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("Tests ", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("TestRunner", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (stem.EndsWith(" Tests", StringComparison.OrdinalIgnoreCase)
            || stem.EndsWith(" Test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return stem.Contains("Test Library", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("Test Libraries", StringComparison.OrdinalIgnoreCase)
            || stem.Contains("Test Toolkit", StringComparison.OrdinalIgnoreCase);
    }

    // ── VS Code AL workspace layout ─────────────────────────────────────

    // Folders that hold dependency caches or editor/tooling state rather than
    // an app's own output. A `.app` under any of these is NOT the project's
    // build artefact — `.alpackages/` in particular is the resolved-dependency
    // cache, so importing those copies would duplicate sibling modules (and
    // pull whatever version happened to be cached). EXTENDING: add a segment
    // here if a new tool drops build-irrelevant folders next to app.json.
    private static readonly HashSet<string> WorkspaceIgnoredSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        ".alpackages",
        ".snapshots",
        ".vscode",
        ".git",
        "node_modules",
    };

    // "<Publisher>_<Name>_<Version>.app" → stem "<Publisher>_<Name>",
    // version "<Version>". Version needs at least major.minor so a bare
    // integer tail (rare, not a real BC .app shape) doesn't get mistaken for
    // one. Greedy stem absorbs the publisher/name underscores; the anchored
    // version tail is unambiguous because AL versions are always numeric dotted.
    private static readonly Regex AppNameVersionSuffix = new(
        @"^(?<stem>.*)_(?<ver>\d+(?:\.\d+){1,3})$",
        RegexOptions.Compiled);

    /// <summary>
    /// True when the archive looks like a VS Code AL workspace (one or more
    /// folders each holding an <c>app.json</c>) rather than a DVD
    /// <c>Applications/</c> tree. Drives the auto-switch in
    /// <see cref="ReleaseZipStaging.OpenStagedZip"/> so an admin can zip a
    /// multi-root AL workspace and upload it through the same box. Ignores
    /// <c>app.json</c> sitting under a dependency / tooling folder.
    /// </summary>
    public static bool LooksLikeWorkspace(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var e in archive.Entries)
        {
            var full = Normalize(e.FullName);
            if (IsUnderIgnoredWorkspaceDir(full)) continue;
            if (string.Equals(LeafName(full), "app.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Walks a zipped VS Code AL workspace: every directory that holds an
    /// <c>app.json</c> is one app project, and we import the compiled
    /// <c>.app</c>(s) that sit <em>directly</em> in that folder — the app's own
    /// build output. The key difference from <see cref="Walk(ZipArchive, Func{string, bool})"/>
    /// is what's deliberately skipped:
    /// <list type="bullet">
    ///   <item>the <c>.alpackages/</c> dependency cache (those <c>.app</c>s are
    ///         dependencies, not this app — they'd duplicate sibling modules);</item>
    ///   <item><c>.dep.app</c> sidecars (symbols-only dependency packages);</item>
    ///   <item>older versions when a folder holds several builds of the same app
    ///         (e.g. <c>…_1.1.29.310.app</c> through <c>…_1.1.29.336.app</c>) —
    ///         only the highest version per app name is kept.</item>
    /// </list>
    /// Source comes from each <c>.app</c>'s embedded <c>src/</c> (BC 14 apps
    /// signal it with <c>ShowMyCode</c>) or a sibling <c>.Source.zip</c> when
    /// present. A folder whose <c>app.json</c> has no compiled <c>.app</c>
    /// (not built yet) yields no entry; see <see cref="DescribeUncompiledAppRoots"/>
    /// for the diagnostic that names those.
    /// </summary>
    public static IReadOnlyList<FolderZipEntry> WalkWorkspace(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var appRoots = FindAppRootIdentities(archive);

        // Bucket each app folder's own-output .app (excluding .dep.app) by its
        // containing directory in one pass, so the per-root selection below is a
        // lookup rather than a re-scan of the whole archive. A folder's root can
        // legitimately hold a *stray* copy of a sibling app's .app (a dependency
        // dropped next to app.json rather than in .alpackages); those are
        // filtered out by the app.json identity check so they don't get claimed
        // by — and imported under — the wrong folder.
        var appsByDir = new Dictionary<string, List<ZipArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        var sourceZips = new Dictionary<(string Directory, string Stem), ZipArchiveEntry>(new DirectoryStemComparer());
        foreach (var e in archive.Entries)
        {
            var full = Normalize(e.FullName);
            var name = LeafName(full);
            if (string.IsNullOrEmpty(name)) continue;

            if (full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var srcStem = StripSourceSuffix(Path.GetFileNameWithoutExtension(name));
                sourceZips[(GetDirectory(full), srcStem)] = e;
                continue;
            }

            if (!full.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (full.EndsWith(".dep.app", StringComparison.OrdinalIgnoreCase)) continue;

            var dir = GetDirectory(full);
            if (!appRoots.TryGetValue(dir, out var identity)) continue; // only an app's own folder, never .alpackages
            if (!AppMatchesRootIdentity(Path.GetFileNameWithoutExtension(name), identity)) continue;
            if (!appsByDir.TryGetValue(dir, out var list))
            {
                list = new List<ZipArchiveEntry>();
                appsByDir[dir] = list;
            }
            list.Add(e);
        }

        var result = new List<FolderZipEntry>();
        foreach (var (dir, apps) in appsByDir)
        {
            // Group by app name (version stripped) and keep only the newest
            // build of each, so a folder full of historical versions collapses
            // to one module per distinct app.
            var byName = apps.GroupBy(
                e => SplitAppNameAndVersion(Path.GetFileNameWithoutExtension(LeafName(Normalize(e.FullName)))).Stem,
                StringComparer.OrdinalIgnoreCase);

            foreach (var group in byName)
            {
                var picked = group
                    .OrderByDescending(e => SplitAppNameAndVersion(Path.GetFileNameWithoutExtension(LeafName(Normalize(e.FullName)))).Version ?? EmptyVersion)
                    .ThenByDescending(e => LeafName(Normalize(e.FullName)), StringComparer.OrdinalIgnoreCase)
                    .First();

                var full = Normalize(picked.FullName);
                var name = LeafName(full);
                var appStem = StripPublisherPrefix(Path.GetFileNameWithoutExtension(name));
                // The .app filename carries a version (…_1.1.29.336.app) but a
                // sibling .Source.zip is named after the app without one, so try
                // the version-stripped stem (and its publisher-stripped form)
                // on top of the DVD-style candidates.
                var (nameStem, _) = SplitAppNameAndVersion(Path.GetFileNameWithoutExtension(name));

                ZipArchiveEntry? pairedSource = null;
                foreach (var candidate in PairingCandidates(name, appStem)
                    .Append(nameStem)
                    .Append(StripPublisherPrefix(nameStem)))
                {
                    if (sourceZips.TryGetValue((dir, candidate), out var src))
                    {
                        pairedSource = src;
                        break;
                    }
                }

                result.Add(new FolderZipEntry(
                    FileName: name,
                    AppEntry: picked,
                    SourceZipEntry: pairedSource,
                    IsTest: HasTestAncestor(full),
                    IsInternal: name.Contains("_Exclude_", StringComparison.OrdinalIgnoreCase),
                    IsLanguagePack: LanguagePackPattern.IsMatch(Path.GetFileNameWithoutExtension(name))));
            }
        }

        // Safety net: never emit the same app identity (publisher_name +
        // version) twice across folders. The app.json check above already keeps
        // a stray sibling copy from being claimed by the wrong folder, but two
        // folders sharing an app.json id would still collide — and a duplicate
        // (AppId, Version) aborts the *entire* release at import time, so it's
        // worth being certain here rather than failing the whole upload.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<FolderZipEntry>(result.Count);
        foreach (var entry in result)
        {
            var (stem, version) = SplitAppNameAndVersion(Path.GetFileNameWithoutExtension(entry.FileName));
            var key = NormalizeIdentity(stem) + "@" + (version?.ToString() ?? "?");
            if (seen.Add(key)) deduped.Add(entry);
        }
        return deduped;
    }

    /// <summary>
    /// Names the workspace folders that declare an app (have an
    /// <c>app.json</c>) but ship no compiled <c>.app</c> — the "not built yet"
    /// case. Returns the app-root directory paths so the import worker can warn
    /// the admin which apps were skipped for lack of a build, rather than
    /// leaving them silently absent. Empty when every app root has output.
    /// </summary>
    public static IReadOnlyList<string> DescribeUncompiledAppRoots(ZipArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        var appRoots = FindAppRootIdentities(archive);
        var builtRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            var full = Normalize(e.FullName);
            if (!full.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
            if (full.EndsWith(".dep.app", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = GetDirectory(full);
            // A folder counts as "built" only when it holds its *own* app — a
            // stray sibling .app in the root (see WalkWorkspace) doesn't make
            // the folder's own app compiled, so it stays reported as uncompiled.
            if (appRoots.TryGetValue(dir, out var identity)
                && AppMatchesRootIdentity(Path.GetFileNameWithoutExtension(LeafName(full)), identity))
            {
                builtRoots.Add(dir);
            }
        }

        return appRoots.Keys
            .Where(r => !builtRoots.Contains(r))
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .Select(r => r.Length == 0 ? "(workspace root)" : r)
            .ToList();
    }

    private static readonly Version EmptyVersion = new(0, 0);

    /// <summary>
    /// Maps each app-root directory (one that holds an <c>app.json</c>) to its
    /// app identity — the normalised <c>publisher_name</c> from that
    /// <c>app.json</c> — or <see langword="null"/> when the manifest is missing
    /// or unparseable. The identity is what ties a folder to its own compiled
    /// <c>.app</c> so a stray dependency copy sitting in the folder root isn't
    /// mistaken for the folder's output.
    /// </summary>
    private static Dictionary<string, string?> FindAppRootIdentities(ZipArchive archive)
    {
        var roots = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            var full = Normalize(e.FullName);
            if (IsUnderIgnoredWorkspaceDir(full)) continue;
            if (!string.Equals(LeafName(full), "app.json", StringComparison.OrdinalIgnoreCase)) continue;
            var dir = GetDirectory(full);
            if (roots.ContainsKey(dir)) continue;
            roots[dir] = ReadAppJsonIdentity(e);
        }
        return roots;
    }

    /// <summary>
    /// True when an <c>.app</c> filename belongs to a folder with the given
    /// identity. When the identity is unknown (<paramref name="identityNorm"/>
    /// is <see langword="null"/> — unreadable app.json), fall back to accepting
    /// any <c>.app</c> in the folder so a single-app project with an odd
    /// manifest still imports.
    /// </summary>
    private static bool AppMatchesRootIdentity(string appFileNameNoExt, string? identityNorm)
    {
        if (identityNorm is null) return true;
        var (stem, _) = SplitAppNameAndVersion(appFileNameNoExt);
        return string.Equals(NormalizeIdentity(stem), identityNorm, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads <c>publisher</c> + <c>name</c> from an <c>app.json</c> entry and
    /// returns them as a normalised identity. Returns <see langword="null"/> on
    /// any read/parse failure or missing field — callers fall back to
    /// filename-only behaviour rather than dropping the folder.
    /// </summary>
    private static string? ReadAppJsonIdentity(ZipArchiveEntry entry)
    {
        try
        {
            using var s = entry.Open();
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var name = GetStringProperty(doc.RootElement, "name");
            var publisher = GetStringProperty(doc.RootElement, "publisher");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)) return null;
            return NormalizeIdentity($"{publisher}_{name}");
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Case-insensitive string-property lookup on a JSON object.</summary>
    private static string? GetStringProperty(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase)
                && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }

    /// <summary>
    /// Collapses an app name/identity to its comparable core — letters and
    /// digits only, lower-cased — so a folder's <c>publisher_name</c> from
    /// <c>app.json</c> matches the <c>.app</c> filename stem regardless of the
    /// spaces, dots, dashes, ampersands, and underscores either side uses.
    /// </summary>
    private static string NormalizeIdentity(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsUnderIgnoredWorkspaceDir(string normalizedFull)
    {
        foreach (var seg in normalizedFull.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (WorkspaceIgnoredSegments.Contains(seg)) return true;
        }
        return false;
    }

    private static (string Stem, Version? Version) SplitAppNameAndVersion(string fileStem)
    {
        var m = AppNameVersionSuffix.Match(fileStem);
        if (m.Success && Version.TryParse(m.Groups["ver"].Value, out var version))
        {
            return (m.Groups["stem"].Value, version);
        }
        return (fileStem, null);
    }

    private static IEnumerable<string> PairingCandidates(string appName, string stripped)
    {
        var bareStem = Path.GetFileNameWithoutExtension(appName);
        // 1. Exact bare stem (handles "_Exclude_Foo.app" → "_Exclude_Foo.Source.zip").
        yield return bareStem;
        // 2. With publisher prefix stripped (handles "Microsoft_Base Application" → "Base Application").
        if (!string.Equals(stripped, bareStem, StringComparison.Ordinal))
        {
            yield return stripped;
        }
        // 3. Version-stripped variants. Microsoft artifact .app filenames carry a
        //    version ("Microsoft_Base Application_25.5.30849.48785.app") while the
        //    paired source is named without one ("Base Application.Source.zip").
        //    DVD names have no version, so SplitAppNameAndVersion is a no-op there.
        var bareNoVer = SplitAppNameAndVersion(bareStem).Stem;
        if (!string.Equals(bareNoVer, bareStem, StringComparison.Ordinal))
        {
            yield return bareNoVer;
        }
        var strippedNoVer = SplitAppNameAndVersion(stripped).Stem;
        if (!string.Equals(strippedNoVer, stripped, StringComparison.Ordinal)
            && !string.Equals(strippedNoVer, bareNoVer, StringComparison.Ordinal))
        {
            yield return strippedNoVer;
        }
    }

    private static string GetDirectory(string fullName)
    {
        var lastSlash = fullName.LastIndexOf('/');
        return lastSlash <= 0 ? string.Empty : fullName[..lastSlash];
    }

    private static string StripPublisherPrefix(string stem)
    {
        // Microsoft's DVD wraps each app filename with a "Microsoft_" prefix.
        // We strip the first <Publisher>_ segment when present so the stem
        // aligns with the paired source zip naming.
        var underscore = stem.IndexOf('_');
        if (underscore <= 0) return stem;
        return stem[(underscore + 1)..];
    }

    private static string StripSourceSuffix(string stem)
    {
        // "<Name>.Source" → "<Name>". GetFileNameWithoutExtension already
        // dropped the ".zip" extension.
        if (stem.EndsWith(".Source", StringComparison.OrdinalIgnoreCase))
        {
            return stem[..^".Source".Length];
        }
        return stem;
    }

    private static bool HasTestAncestor(string fullName)
    {
        // Walk every path segment; the .app file's own filename is the last
        // segment so it's harmless to scan.
        foreach (var segment in fullName.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TestFolderNames.Contains(segment)) return true;
            foreach (var suffix in TestFolderSuffixes)
            {
                if (segment.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private sealed class DirectoryStemComparer : IEqualityComparer<(string Directory, string Stem)>
    {
        public bool Equals((string Directory, string Stem) x, (string Directory, string Stem) y) =>
            string.Equals(x.Directory, y.Directory, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Stem, y.Stem, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Directory, string Stem) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Directory),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Stem));
    }
}

/// <summary>
/// One <c>.app</c> entry inside the folder-style upload ZIP, paired with its
/// sibling <c>.Source.zip</c> when found, plus the flag tuple inferred from
/// the surrounding folder + filename conventions.
/// </summary>
public sealed record FolderZipEntry(
    string FileName,
    ZipArchiveEntry AppEntry,
    ZipArchiveEntry? SourceZipEntry,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack);
