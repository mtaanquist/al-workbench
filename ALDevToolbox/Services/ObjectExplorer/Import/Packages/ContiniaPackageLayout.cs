using System.IO.Compression;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.ObjectExplorer.Import.Packages;

/// <summary>
/// Continia's release package (#1802):
/// <code>
/// &lt;Package&gt;/Apps/BC 29.0 (BC 2026 Wave 2)/&lt;Product&gt;/&lt;App&gt; 29.1.1.257932 - 29.0.app
/// &lt;Package&gt;/Apps/BC 29.1 (BC 2026 Wave 2 CU1)/&lt;Product&gt;/&lt;App&gt; 29.1.1.257932 - 29.1.app
/// &lt;Package&gt;/Apps/Source/&lt;App&gt; 29.1.1.257932 - 29.0-Source.zip
/// &lt;Package&gt;/Apps/Scripts/...
/// </code>
/// Some packages (Document Capture, Expense Management) name the folder
/// <c>App</c> rather than <c>Apps</c> and carry installers, documentation and
/// sample files beside it; both names are read and everything else is ignored.
/// Each <c>BC x.y</c> folder holds the same app versions rebuilt for that
/// Business Central version, so each becomes its own release: imported together
/// they would collide on app id and version. The source zips are shared by all of
/// them and sit in <c>Source/</c>, named with the app name and version plus a
/// platform suffix (and ending <c>-Source.zip</c>, or <c> Source.zip</c> for
/// some products), so they are paired by name and version, preferring the one
/// built for the same Business Central version. Scripts and installers are
/// ignored.
/// </summary>
public sealed partial class ContiniaPackageLayout : IThirdPartyPackageLayout
{
    public string Name => "Continia";

    // "BC 29.0 (BC 2026 Wave 2)", "BC 29.1", "BC 29" or "BC29.0". The digits are
    // bounded so a hostile folder name can't overflow the version parse.
    [GeneratedRegex(@"^BC\s*(?<major>\d{1,4})(?:\.(?<minor>\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex BcFolder();

    // "Continia Banking - CSV Import 29.1.1.257932 - 29.0", "Core 29.1.0.244450 - 29.1 (BC 2026 Wave 2 CU1)",
    // "CFI 29.0.0.214343 - 29 (BC 2026 Wave 2)": the app's name, its four-part
    // version, and the Business Central version it was built for.
    [GeneratedRegex(@"^(?<name>.+?)\s+(?<ver>\d+\.\d+\.\d+\.\d+)\s+-\s+(?<plat>\d+(?:\.\d+)?)(?:\s*\(.*\))?$")]
    private static partial Regex VersionedStem();

    // The source-zip marker after the stem: "-Source", " Source" or ".Source".
    [GeneratedRegex(@"[-\s.]Source$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceSuffix();

    public bool Matches(ZipArchive archive) =>
        archive.Entries.Any(e => LocateApp(Normalize(e.FullName)) is { } app
            && VersionedStem().IsMatch(Path.GetFileNameWithoutExtension(app.LeafName)));

    public PackagePlan Plan(ZipArchive archive)
    {
        var sources = new Dictionary<(string Name, string Version), List<(Version Platform, ZipArchiveEntry Entry)>>(
            new NameVersionComparer());
        var apps = new List<(string BcVersion, string? Product, string Name, string Version, Version? Platform, ZipArchiveEntry Entry)>();

        foreach (var entry in archive.Entries)
        {
            var full = Normalize(entry.FullName);
            if (full.EndsWith('/')) continue;

            if (full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && IsInSourceFolder(full))
            {
                var stem = SourceSuffix().Replace(Path.GetFileNameWithoutExtension(LeafName(full)), string.Empty);
                if (Parse(stem) is { } src && src.Platform is { } platform)
                {
                    if (!sources.TryGetValue((src.Name, src.Version), out var list))
                    {
                        sources[(src.Name, src.Version)] = list = new();
                    }
                    list.Add((platform, entry));
                }
                continue;
            }

            if (LocateApp(full) is { } located)
            {
                var parsed = Parse(Path.GetFileNameWithoutExtension(located.LeafName));
                apps.Add((located.BcVersion, located.Product,
                    parsed?.Name ?? Path.GetFileNameWithoutExtension(located.LeafName),
                    parsed?.Version ?? string.Empty, parsed?.Platform, entry));
            }
        }

        var groups = apps
            .GroupBy(a => a.BcVersion)
            .OrderBy(g => System.Version.Parse(g.Key))
            .Select(g => new PackageReleaseGroup(
                g.Key,
                g.OrderBy(a => a.Entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .Select(a => new PackageApp(a.Entry, PickSource(sources, a.Name, a.Version, a.Platform ?? System.Version.Parse(g.Key)), a.Product))
                    .ToList()))
            .ToList();
        return new PackagePlan.Split(groups);
    }

    /// <summary>
    /// The source zip for one app: the one built for the same Business Central
    /// version when there is one, else the newest other build of the same app
    /// version. The source is the same code either way; only the compile target
    /// differs.
    /// </summary>
    private static ZipArchiveEntry? PickSource(
        Dictionary<(string Name, string Version), List<(Version Platform, ZipArchiveEntry Entry)>> sources,
        string name, string version, Version platform)
    {
        if (version.Length == 0 || !sources.TryGetValue((name, version), out var candidates)) return null;
        return candidates.FirstOrDefault(c => c.Platform == platform).Entry
            ?? candidates.OrderByDescending(c => c.Platform).First().Entry;
    }

    private static (string Name, string Version, Version? Platform)? Parse(string stem)
    {
        var m = VersionedStem().Match(stem.Trim());
        if (!m.Success) return null;
        return (m.Groups["name"].Value.Trim(), m.Groups["ver"].Value, ParsePlatform(m.Groups["plat"].Value));
    }

    // "29" and "29.0" are the same Business Central version.
    private static Version? ParsePlatform(string value) =>
        System.Version.TryParse(value.Contains('.') ? value : value + ".0", out var v) ? v : null;

    /// <summary>
    /// Where an <c>.app</c> sits when it is under <c>Apps/BC x.y/</c> (or <c>App/BC x.y/</c>): the
    /// Business Central version from the folder name, and the product folder
    /// below it (Banking, Base, ...) when there is one. Null for anything else.
    /// </summary>
    private static (string BcVersion, string? Product, string LeafName)? LocateApp(string normalizedFullName)
    {
        if (!normalizedFullName.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = normalizedFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // The last segment is the file; a BC folder needs an "Apps" or "App" segment before it.
        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (!string.Equals(segments[i], "Apps", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(segments[i], "App", StringComparison.OrdinalIgnoreCase)) continue;
            var bc = BcFolder().Match(segments[i + 1]);
            if (!bc.Success) continue;
            var product = i + 2 < segments.Length - 1 ? segments[i + 2] : null;
            var minor = bc.Groups["minor"].Success ? bc.Groups["minor"].Value : "0";
            return ($"{int.Parse(bc.Groups["major"].Value)}.{int.Parse(minor)}", product, segments[^1]);
        }
        return null;
    }

    // Directly in Source/ or in a folder below it (Source/<Product>/...).
    private static bool IsInSourceFolder(string normalizedFullName)
    {
        var segments = normalizedFullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Take(segments.Length - 1).Any(s => string.Equals(s, "Source", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string fullName) => fullName.Replace('\\', '/');

    private static string LeafName(string normalizedFullName)
    {
        var slash = normalizedFullName.LastIndexOf('/');
        return slash >= 0 ? normalizedFullName[(slash + 1)..] : normalizedFullName;
    }

    private sealed class NameVersionComparer : IEqualityComparer<(string Name, string Version)>
    {
        public bool Equals((string Name, string Version) x, (string Name, string Version) y) =>
            string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Version, y.Version, StringComparison.Ordinal);

        public int GetHashCode((string Name, string Version) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name), obj.Version);
    }
}
