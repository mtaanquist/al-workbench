using System.Text.Json;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.Migration;

/// <summary>One source repository after it was cloned, ready to be read.</summary>
/// <param name="RepositoryName">The repository's name, used in messages and for an extension at its root.</param>
/// <param name="Directory">Where the clone's working tree is on disk.</param>
public sealed record ClonedSource(string RepositoryName, string Directory, string? WebUrl = null);

/// <summary>An extension found in a source repository, and where it would go.</summary>
/// <param name="RepositoryName">The repository it came from.</param>
/// <param name="SourcePath">Its folder inside that repository, with forward slashes; empty for the repository's root.</param>
/// <param name="Folder">The folder it gets in the new workspace.</param>
public sealed record MigrationExtension(
    string RepositoryName,
    string SourcePath,
    string Folder,
    string Id,
    string Name,
    string Publisher,
    string Version);

/// <summary>How much a finding matters to the run.</summary>
public enum MigrationFindingSeverity
{
    /// <summary>Worth knowing; the migration can go ahead.</summary>
    Warning,

    /// <summary>The migration cannot go ahead until this is fixed in the source.</summary>
    Blocking,
}

/// <summary>Something the preview wants the Admin to see.</summary>
/// <param name="RepositoryName">The repository it is about, or null when it spans several.</param>
public sealed record MigrationFinding(MigrationFindingSeverity Severity, string? RepositoryName, string Message);

/// <summary>What a preview found across every source repository.</summary>
public sealed record MigrationSourceAnalysis(
    IReadOnlyList<MigrationExtension> Extensions,
    IReadOnlyList<MigrationFinding> Findings)
{
    /// <summary>True when nothing stands in the way of migrating.</summary>
    public bool CanMigrate => Extensions.Count > 0 && Findings.All(f => f.Severity != MigrationFindingSeverity.Blocking);
}

/// <summary>
/// Reads cloned source repositories and works out which extensions they hold,
/// which folder each would get in the new workspace, and what stands in the way
/// (issue #1699). Pure over the file system, so it is tested on folders built by
/// hand rather than on clones.
///
/// <para>Every folder holding an <c>app.json</c> is an extension, test apps
/// included: a test app moves with the app it tests. Each keeps its own folder
/// name; one at a repository's root has no folder of its own, so it takes the
/// repository's name. Decided by Mads on #1698.</para>
/// </summary>
public static class MigrationSourceAnalyzer
{
    /// <summary>The largest file GitHub accepts in a push.</summary>
    public const long MaxFileBytes = 100L * 1024 * 1024;

    private static readonly EnumerationOptions NoLinks = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static readonly JsonDocumentOptions TolerantJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads every source in <paramref name="sources"/> and returns what a migration of all of them would move.</summary>
    public static MigrationSourceAnalysis Analyze(IReadOnlyList<ClonedSource> sources)
    {
        var extensions = new List<MigrationExtension>();
        var findings = new List<MigrationFinding>();
        // Dependencies are checked once every extension is known, so each entry keeps its manifest's.
        var dependencies = new List<(MigrationExtension Extension, IReadOnlyList<(string Id, string Name, string Publisher)> On)>();

        foreach (var source in sources)
        {
            var found = 0;
            foreach (var dir in FindExtensionFolders(source.Directory))
            {
                var relative = Path.GetRelativePath(source.Directory, dir).Replace('\\', '/');
                if (relative == ".") relative = string.Empty;
                var label = relative.Length == 0 ? "the root" : relative;
                var manifest = Read(Path.Combine(dir, AppJsonManifestParser.FileName));
                if (manifest is null)
                {
                    findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, source.RepositoryName,
                        $"The app.json in {label} can't be read. Fix it in the repository, then preview again."));
                    continue;
                }
                if (manifest.Value.Manifest.Id.Length == 0)
                {
                    findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, source.RepositoryName,
                        $"The app.json in {label} has no id. Fix it in the repository, then preview again."));
                    continue;
                }

                var m = manifest.Value.Manifest;
                var folder = relative.Length == 0 ? source.RepositoryName : Path.GetFileName(relative);
                var extension = new MigrationExtension(source.RepositoryName, relative, folder, m.Id, m.Name, m.Publisher, m.Version);
                extensions.Add(extension);
                dependencies.Add((extension, manifest.Value.Dependencies));
                found++;
            }

            if (found == 0 && !findings.Any(f => f.RepositoryName == source.RepositoryName))
            {
                findings.Add(new MigrationFinding(MigrationFindingSeverity.Warning, source.RepositoryName,
                    "No extension was found in this repository, so nothing from it would be moved."));
            }

            foreach (var big in FindLargeFiles(source.Directory))
            {
                findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, source.RepositoryName,
                    $"{big} is larger than 100 MB, which GitHub does not accept. Remove it from the repository, then preview again."));
            }
        }

        foreach (var clash in extensions.GroupBy(e => e.Folder, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, null,
                $"{Describe(clash)} would all go into the folder \"{clash.Key}\". Rename one of the folders in its "
                + "repository, then preview again."));
        }

        foreach (var duplicate in extensions.GroupBy(e => NormalizeId(e.Id)).Where(g => g.Count() > 1))
        {
            findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, null,
                $"{Describe(duplicate)} have the same app id. Each extension needs its own, so leave one of the "
                + "repositories out or fix the id in it."));
        }

        // An app that depends on another app of the same publisher which is not in
        // the run usually means a repository was left unticked. Apps from other
        // publishers (Microsoft, AppSource) are expected to come from elsewhere.
        var ids = extensions.Select(e => NormalizeId(e.Id)).ToHashSet(StringComparer.Ordinal);
        var publishers = extensions.Select(e => e.Publisher).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (extension, on) in dependencies)
        {
            foreach (var dependency in on)
            {
                if (ids.Contains(NormalizeId(dependency.Id))) continue;
                if (!publishers.Contains(dependency.Publisher)) continue;
                var name = dependency.Name.Length > 0 ? dependency.Name : dependency.Id;
                findings.Add(new MigrationFinding(MigrationFindingSeverity.Warning, extension.RepositoryName,
                    $"{extension.Name} depends on {name} by {dependency.Publisher}, which isn't in any of the chosen "
                    + "repositories. If it lives in another repository of this customer, add that one too."));
            }
        }

        return new MigrationSourceAnalysis(
            extensions
                .OrderBy(e => e.Folder, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            findings
                .OrderByDescending(f => f.Severity)
                .ToList());
    }

    /// <summary>Canonical form of an app id for comparison: no braces, lower case.</summary>
    internal static string NormalizeId(string id) => id.Trim().Trim('{', '}').ToLowerInvariant();

    private static string Describe(IEnumerable<MigrationExtension> group)
    {
        var names = group.Select(e => $"{e.Name} ({e.RepositoryName}{(e.SourcePath.Length > 0 ? "/" + e.SourcePath : string.Empty)})").ToList();
        return names.Count == 2 ? $"{names[0]} and {names[1]}" : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
    }

    /// <summary>
    /// Folders holding an <c>app.json</c>, never descending into tooling folders
    /// (<c>.alpackages</c>, <c>.git</c>, <c>.vscode</c> and the like) or following a link.
    /// Unlike a build, test folders are walked too: a test app moves with its app.
    /// </summary>
    internal static IEnumerable<string> FindExtensionFolders(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var results = new List<string>();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var manifest = new FileInfo(Path.Combine(dir, AppJsonManifestParser.FileName));
            if (manifest.Exists && manifest.LinkTarget is null) results.Add(dir);
            string[] subs;
            try { subs = Directory.GetDirectories(dir, "*", NoLinks); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                if (AppJsonManifestParser.IsExcludedSegment(Path.GetFileName(sub))) continue;
                stack.Push(sub);
            }
        }
        return results.OrderBy(d => d, StringComparer.Ordinal);
    }

    /// <summary>Files over <see cref="MaxFileBytes"/>, relative to <paramref name="root"/>, outside <c>.git</c> and <c>.alpackages</c>.</summary>
    internal static IEnumerable<string> FindLargeFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        var results = new List<string>();
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", NoLinks))
                {
                    if (new FileInfo(file).Length > MaxFileBytes)
                    {
                        results.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                    }
                }
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", NoLinks))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals(".git", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(".alpackages", StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(sub);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return results.OrderBy(f => f, StringComparer.Ordinal);
    }

    private static (AppJsonManifest Manifest, IReadOnlyList<(string Id, string Name, string Publisher)> Dependencies)? Read(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        var manifest = AppJsonManifestParser.Parse(json);
        if (manifest is null) return null;

        // The shared manifest leaves a dependency's publisher out; the
        // same-publisher check above needs it, so it is read here.
        var dependencies = new List<(string, string, string)>();
        using (var doc = JsonDocument.Parse(json, TolerantJson))
        {
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in deps.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var id = String(d, "id") ?? String(d, "appId") ?? string.Empty;
                    if (id.Length == 0) continue;
                    dependencies.Add((id, String(d, "name") ?? string.Empty, String(d, "publisher") ?? string.Empty));
                }
            }
        }
        return (manifest, dependencies);
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
