using ALDevToolbox.Services.ObjectExplorer.Projects;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>The fields the build pipeline reads from an <c>app.json</c>.</summary>
public sealed record AppJsonManifest(
    string Id,
    string Name,
    string Publisher,
    string Version,
    string? Application,
    string? Platform,
    string? Runtime,
    IReadOnlyList<AppJsonDependency> Dependencies);

/// <summary>
/// One inter-app dependency declared in <c>app.json</c>.
///
/// <para><c>Version</c> is the minimum version the manifest asks
/// for. The build ignores it - it compiles against whatever symbols it was
/// given - but dependency drift (issue #630) compares it with the catalogue's
/// default to decide whether a repository is a version behind, so the parser
/// keeps it. Null when the entry does not state one, which is a manifest the
/// drift scan leaves alone rather than guesses at.</para>
/// </summary>
public sealed record AppJsonDependency(string Id, string Name, string? Version = null);

/// <summary>
/// Reading an <c>app.json</c>, and deciding which folders holding one are a test
/// extension rather than a shipped one.
///
/// <para>Lifted out of <see cref="ProjectBuildService"/> because the build is no
/// longer the only reader: repository discovery (issue #629) asks the same
/// questions of a manifest it fetched over the GitHub API rather than cloned, and
/// a second copy of "which folder names mean tests" is exactly how the two
/// surfaces would come to disagree about what a repository contains. Behaviour is
/// unchanged; the build service forwards to the members here.</para>
///
/// <para>See <c>.design/github-integration-phase2.md</c>, "Shared plumbing".</para>
/// </summary>
public static class AppJsonManifestParser
{
    /// <summary>The manifest's file name, at a repository root or inside an extension folder.</summary>
    public const string FileName = "app.json";

    /// <summary>
    /// Folders a walk never descends into: tooling output and metadata that can
    /// contain an <c>app.json</c> without an extension living there.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".alpackages", ".vscode", ".git", ".github", "node_modules", ".snapshots", ".altestrunner",
    };

    // Mirrors FolderZipWalker's test-folder rules so every ingest path agrees
    // on what counts as a test extension.
    private static readonly HashSet<string> TestFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Test", "Tests", "Test Library", "Test Libraries", "TestLibraries", "TestFramework",
    };

    private static readonly string[] TestFolderSuffixes =
    {
        " Test Library", " Test Libraries", " Test Toolkit", " Tests",
    };

    /// <summary>True when <paramref name="segment"/> names a test folder rather than a shipped extension.</summary>
    public static bool IsTestSegment(string segment) =>
        TestFolderNames.Contains(segment)
        || TestFolderSuffixes.Any(suf => segment.EndsWith(suf, StringComparison.OrdinalIgnoreCase));

    // Microsoft's test framework apps, by id, so a test app is recognised whatever its
    // folder is called (#1130). Names stand in only for a dependency that leaves the id
    // out: another publisher's app may well be called "Any" or "Test Runner" (#1193).
    private static readonly HashSet<string> TestFrameworkAppIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "dd0be2ea-f733-4d65-bb34-a28f4624fb14", // Library Assert
        "23de40a6-dfe8-4f80-80db-d70f83ce8caf", // Test Runner
        "e7320ebb-08b3-4406-b1ec-b4927d3e280b", // Any
        "5095f467-0a01-4b99-99d1-9ff1237d286f", // Library Variable Storage
        "40860557-a18d-42ad-aecb-22b7dd80dc80", // Permissions Mock
        "9856ae4f-d1a7-46ef-89bb-6ef056398228", // System Application Test Library
        "5d86850b-0d76-4eca-bd7b-951ad998e997", // Tests-TestLibraries
        "2156302a-872f-4568-be0b-60968696f0d5", // AI Test Toolkit
    };

    private static readonly HashSet<string> TestFrameworkAppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Library Assert", "Test Runner", "Any", "Library Variable Storage", "Permissions Mock",
        "System Application Test Library", "Tests-TestLibraries", "AI Test Toolkit",
    };

    /// <summary>
    /// True when <paramref name="manifest"/> depends on one of Microsoft's test framework
    /// apps (Library Assert, Test Runner, Any and the like): a test app, whatever the
    /// folder holding it is called (#1130). A shipped extension never needs them.
    /// </summary>
    public static bool IsTestApp(AppJsonManifest manifest) =>
        manifest.Dependencies.Any(d =>
        {
            var id = d.Id.Trim('{', '}', ' ');
            return id.Length > 0
                ? TestFrameworkAppIds.Contains(id)
                : TestFrameworkAppNames.Contains(d.Name.Trim());
        });

    /// <summary>True when a walk should not descend into <paramref name="segment"/> at all.</summary>
    public static bool IsExcludedSegment(string segment) => ExcludedFolderNames.Contains(segment);

    /// <summary>Parses an <c>app.json</c> body into a manifest, tolerant of trailing commas / comments. Null when unreadable.</summary>
    public static AppJsonManifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string Str(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            string? StrOrNull(string prop) =>
                root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            var deps = new List<AppJsonDependency>();
            if (root.TryGetProperty("dependencies", out var depsEl) && depsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in depsEl.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    // Old app.json used "appId"; new uses "id".
                    var depId = (d.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString()
                              : d.TryGetProperty("appId", out var aidv) && aidv.ValueKind == JsonValueKind.String ? aidv.GetString() : null) ?? string.Empty;
                    var depName = d.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString()! : string.Empty;
                    var depVersion = d.TryGetProperty("version", out var vv) && vv.ValueKind == JsonValueKind.String ? vv.GetString() : null;
                    if (depId.Length > 0) deps.Add(new AppJsonDependency(depId, depName, depVersion));
                }
            }

            var id = Str("id");
            if (id.Length == 0) id = Str("appId");
            return new AppJsonManifest(id, Str("name"), Str("publisher"), Str("version"), StrOrNull("application"), StrOrNull("platform"), StrOrNull("runtime"), deps);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
