using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Services.Migration;

/// <summary>What a migration is moving into: the template, the customer, and the plan generated from them.</summary>
/// <param name="TemplateName">The template's name, as the page shows it.</param>
/// <param name="AddTemplateExtensions">Whether the template's own extensions and modules come along (off by default, #1698).</param>
/// <param name="Plan">The workspace plan the template is generated from, built and validated when the preview was asked for.</param>
public sealed record MigrationTarget(
    string TemplateKey,
    string TemplateName,
    string CustomerName,
    bool AddTemplateExtensions,
    ProjectPlan Plan);

/// <summary>Why a source file is not in the new workspace.</summary>
public enum MigrationLeftBehindReason
{
    /// <summary>It sits outside every extension folder, at a repository's or workspace's root.</summary>
    NotInExtension,

    /// <summary>As <see cref="NotInExtension"/>, and the template has its own file in that place.</summary>
    ReplacedByTemplate,

    /// <summary>The template's <c>.gitignore</c> ignores it.</summary>
    IgnoredByGitignore,
}

/// <summary>A file, or a folder of files, that a migration leaves behind.</summary>
/// <param name="Path">Inside the source repository; a folder ends in a slash.</param>
/// <param name="FileCount">How many files that is; 1 for a file.</param>
public sealed record MigrationLeftBehind(string RepositoryName, string Path, MigrationLeftBehindReason Reason, int FileCount);

/// <summary>The changes a migration makes to one extension's <c>app.json</c>.</summary>
public sealed record MigrationAppJsonReport(string Folder, string RepositoryName, IReadOnlyList<MigrationAppJsonChange> Changes);

/// <summary>The workspace a migration builds, as a preview reports it.</summary>
/// <param name="TemplateFolders">The template's own extension folders that were added, when the Admin asked for them.</param>
/// <param name="RootFiles">The files the template puts at the workspace's root.</param>
/// <param name="FileCount">How many files the new repository would hold.</param>
public sealed record MigrationWorkspace(
    string TemplateName,
    IReadOnlyList<string> TemplateFolders,
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<MigrationAppJsonReport> AppJson,
    IReadOnlyList<MigrationLeftBehind> LeftBehind,
    IReadOnlyList<MigrationFinding> Findings,
    int FileCount)
{
    /// <summary>True when nothing found while building stands in the way.</summary>
    public bool CanMigrate => Findings.All(f => f.Severity != MigrationFindingSeverity.Blocking);
}

/// <summary>
/// Builds the new workspace of a migration on disk (issue #1700): the template
/// generated exactly as New Workspace generates it, each source extension copied
/// into its own folder with its <c>app.json</c> rebuilt on the template's, the
/// <c>.code-workspace</c> file and <c>workspace.aldt.toml</c> pointed at those
/// folders, and everything the template's <c>.gitignore</c> ignores taken out.
/// The preview builds it to report what would change; the migration itself
/// (#1701) builds it again and pushes it.
/// </summary>
public sealed class MigrationWorkspaceBuilder
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    private static readonly EnumerationOptions NoLinks = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private readonly GenerationService _generation;
    private readonly WorkspaceConfigService _config;
    private readonly IProcessRunner _processes;
    private readonly ILogger<MigrationWorkspaceBuilder> _logger;

    public MigrationWorkspaceBuilder(
        GenerationService generation,
        WorkspaceConfigService config,
        IProcessRunner processes,
        ILogger<MigrationWorkspaceBuilder> logger)
    {
        _generation = generation;
        _config = config;
        _processes = processes;
        _logger = logger;
    }

    /// <summary>
    /// Writes the workspace for <paramref name="target"/> into the empty folder
    /// <paramref name="destination"/>, from the extensions <paramref name="analysis"/>
    /// found in <paramref name="sources"/>. <paramref name="commits"/> maps each
    /// repository's name to the commit that was read, recorded in
    /// <c>workspace.aldt.toml</c>.
    /// </summary>
    /// <exception cref="PlanValidationException">The template can no longer be generated (it was deleted, say).</exception>
    public async Task<MigrationWorkspace> BuildAsync(
        MigrationTarget target,
        IReadOnlyList<ClonedSource> sources,
        MigrationSourceAnalysis analysis,
        IReadOnlyDictionary<string, string> commits,
        string destination,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var template = await ReadTemplateAsync(target.Plan, ct).ConfigureAwait(false);
        var findings = new List<MigrationFinding>();
        var migratedFolders = analysis.Extensions.Select(e => e.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var templateFolders = template.Extensions.Select(e => e.Folder).ToList();
        var keptTemplateFolders = target.AddTemplateExtensions ? templateFolders : [];

        foreach (var clash in keptTemplateFolders.Where(migratedFolders.Contains))
        {
            findings.Add(new MigrationFinding(MigrationFindingSeverity.Blocking, null,
                $"The template's own extension and a moved extension would both go into the folder \"{clash}\". "
                + "Rename the folder in its repository, or leave the template's extensions out."));
        }

        if (findings.Count > 0)
        {
            // Two extensions in one folder cannot be built; say so and stop.
            return new MigrationWorkspace(target.TemplateName, keptTemplateFolders, [], [], [], findings, 0);
        }

        // The template's files, less the parts rebuilt below and its own extensions unless asked for.
        var dropped = templateFolders.Except(keptTemplateFolders, StringComparer.OrdinalIgnoreCase).ToList();
        var rootFiles = new List<string>();
        foreach (var dir in template.Directories)
        {
            if (!InFolders(dir, dropped)) Directory.CreateDirectory(Under(destination, dir));
        }
        foreach (var (path, content) in template.Files)
        {
            if (InFolders(path, dropped)) continue;
            if (path == template.CodeWorkspaceFile || path == WorkspaceConfigService.FileName)
            {
                rootFiles.Add(path); // rewritten below
                continue;
            }
            Write(Under(destination, path), content);
            if (!path.Contains('/')) rootFiles.Add(path);
        }

        var appJson = new List<MigrationAppJsonReport>();
        var leftBehind = new List<MigrationLeftBehind>();
        var identities = template.Extensions
            .Where(e => keptTemplateFolders.Contains(e.Folder, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var bySource = analysis.Extensions.ToLookup(e => e.RepositoryName, StringComparer.Ordinal);

        foreach (var source in sources)
        {
            var extensions = bySource[source.RepositoryName].ToList();
            foreach (var extension in extensions)
            {
                ct.ThrowIfCancellationRequested();
                var from = extension.SourcePath.Length == 0 ? source.Directory : Path.Combine(source.Directory, extension.SourcePath);
                var nested = extensions
                    .Where(e => e != extension && IsBelow(e.SourcePath, extension.SourcePath))
                    .Select(e => e.SourcePath)
                    .ToList();
                var to = Path.Combine(destination, extension.Folder);
                CopyExtension(from, to, extension.SourcePath, nested);

                var manifestPath = Path.Combine(to, AppJsonManifestParser.FileName);
                var sourceJson = MigrationAppJson.Parse(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false)) ?? new JsonObject();
                var (normalized, changes) = MigrationAppJson.Normalize(template.AppJson, sourceJson);
                await File.WriteAllTextAsync(manifestPath, WorkspaceZipBuilder.WriteJson(normalized) + "\n", ct).ConfigureAwait(false);
                appJson.Add(new MigrationAppJsonReport(extension.Folder, extension.RepositoryName, changes));
                identities.Add(Identity(extension, normalized, commits.GetValueOrDefault(extension.RepositoryName)));
            }

            leftBehind.AddRange(OutsideExtensions(source, extensions.Select(e => e.SourcePath).ToList(), template));
        }

        await WriteWorkspaceFilesAsync(destination, template, target, dropped, analysis.Extensions.Select(e => e.Folder).ToList(), identities, ct)
            .ConfigureAwait(false);
        leftBehind.AddRange(await RemoveIgnoredAsync(destination, analysis.Extensions, ct).ConfigureAwait(false));

        var fileCount = Directory.EnumerateFiles(destination, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Count(f => !Path.GetRelativePath(destination, f).Replace('\\', '/').StartsWith(".git/", StringComparison.Ordinal));
        _logger.LogInformation(
            "Built a migration workspace from template {Template} with {ExtensionCount} moved extensions: {FileCount} files, {LeftBehind} left behind.",
            target.TemplateKey, analysis.Extensions.Count, fileCount, leftBehind.Count);

        return new MigrationWorkspace(
            target.TemplateName,
            keptTemplateFolders,
            rootFiles.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            appJson.OrderBy(a => a.Folder, StringComparer.OrdinalIgnoreCase).ToList(),
            leftBehind
                .OrderBy(l => l.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Path, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            findings,
            fileCount);
    }

    // ===== The template =====

    private sealed record TemplateFiles(
        Dictionary<string, byte[]> Files,
        List<string> Directories,
        IReadOnlyList<WorkspaceExtensionIdentity> Extensions,
        ProjectPlan Plan,
        string? CodeWorkspaceFile,
        JsonObject AppJson);

    /// <summary>
    /// Generates the template for <paramref name="plan"/> and reads the result
    /// back: its files by path below the workspace folder, the extensions it
    /// recorded, and the first extension's <c>app.json</c> as the one every
    /// moved extension's is rebuilt on.
    /// </summary>
    private async Task<TemplateFiles> ReadTemplateAsync(ProjectPlan plan, CancellationToken ct)
    {
        var archive = await _generation.GenerateWorkspaceAsync(plan, ct).ConfigureAwait(false);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var directories = new List<string>();
        await using (archive.Stream.ConfigureAwait(false))
        {
            archive.Stream.Position = 0;
            using var zip = new ZipArchive(archive.Stream, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                // Every entry sits under one folder named after the customer.
                var slash = entry.FullName.IndexOf('/');
                if (slash < 0) continue;
                var path = entry.FullName[(slash + 1)..];
                if (path.Length == 0) continue;
                if (path.EndsWith('/'))
                {
                    directories.Add(path.TrimEnd('/'));
                    continue;
                }
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
                files[path] = buffer.ToArray();
            }
        }

        if (!files.TryGetValue(WorkspaceConfigService.FileName, out var toml))
        {
            throw new InvalidOperationException("The generated workspace has no workspace.aldt.toml.");
        }
        var config = await _config.ParseAsync(Encoding.UTF8.GetString(toml), ct).ConfigureAwait(false);
        var first = config.Extensions.FirstOrDefault();
        var appJson = first is not null && files.TryGetValue($"{first.Folder}/{AppJsonManifestParser.FileName}", out var bytes)
            ? MigrationAppJson.Parse(Encoding.UTF8.GetString(bytes)) ?? new JsonObject()
            : new JsonObject();
        return new TemplateFiles(files, directories, config.Extensions, config.Workspace ?? plan, config.CodeWorkspaceFileName, appJson);
    }

    private async Task WriteWorkspaceFilesAsync(
        string destination,
        TemplateFiles template,
        MigrationTarget target,
        IReadOnlyCollection<string> dropped,
        IReadOnlyList<string> migratedFolders,
        IReadOnlyList<WorkspaceExtensionIdentity> identities,
        CancellationToken ct)
    {
        if (template.CodeWorkspaceFile is { } codeWorkspace && template.Files.TryGetValue(codeWorkspace, out var bytes))
        {
            var json = Encoding.UTF8.GetString(bytes);
            var rewritten = WorkspaceZipBuilder.ReplaceExtensionFolders(json, dropped, migratedFolders.Order(StringComparer.OrdinalIgnoreCase).ToList());
            await File.WriteAllTextAsync(Path.Combine(destination, codeWorkspace), rewritten ?? json, ct).ConfigureAwait(false);
        }

        var plan = target.AddTemplateExtensions
            ? template.Plan
            : template.Plan with { SelectedExtensionPaths = [], SelectedModuleKeys = [] };
        await File.WriteAllTextAsync(
            Path.Combine(destination, WorkspaceConfigService.FileName),
            _config.BuildWorkspace(plan, identities, template.CodeWorkspaceFile),
            ct).ConfigureAwait(false);
    }

    private static WorkspaceExtensionIdentity Identity(MigrationExtension extension, JsonObject appJson, string? commit)
    {
        var (from, to) = FirstRange(appJson);
        return new WorkspaceExtensionIdentity(
            Kind: WorkspaceExtensionIdentity.MigratedKind,
            Key: null,
            Id: Guid.TryParse(extension.Id, out var id) ? id : Guid.Empty,
            Name: extension.Name,
            Folder: extension.Folder,
            Publisher: extension.Publisher,
            IdRangeFrom: from,
            IdRangeTo: to,
            SourceRepository: extension.SourcePath.Length == 0 ? extension.RepositoryName : $"{extension.RepositoryName}/{extension.SourcePath}",
            SourceCommit: commit);
    }

    private static (int From, int To) FirstRange(JsonObject appJson)
    {
        JsonNode? range = null;
        foreach (var (key, value) in appJson)
        {
            if (key.Equals("idRanges", StringComparison.OrdinalIgnoreCase) && value is JsonArray ranges && ranges.Count > 0) range = ranges[0];
            else if (key.Equals("idRange", StringComparison.OrdinalIgnoreCase) && value is JsonObject) range ??= value;
        }
        return range is JsonObject r ? (Int(r, "from"), Int(r, "to")) : (0, 0);

        static int Int(JsonObject o, string name) =>
            o[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;
    }

    // ===== The sources =====

    /// <summary>
    /// Copies an extension's folder, skipping links, <c>.git</c> and any
    /// <c>.alpackages</c>, and the folders of extensions nested inside it, which
    /// get folders of their own.
    /// </summary>
    private static void CopyExtension(string from, string to, string sourcePath, IReadOnlyList<string> nested)
    {
        var stack = new Stack<(string From, string To, string Relative)>();
        stack.Push((from, to, sourcePath));
        while (stack.Count > 0)
        {
            var (dir, target, relative) = stack.Pop();
            Directory.CreateDirectory(target);
            var top = relative.Length == 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", NoLinks))
            {
                if (top && IsRepositoryLevel(Path.GetFileName(file), isFolder: false)) continue;
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", NoLinks))
            {
                var name = Path.GetFileName(sub);
                if (IsTooling(name)) continue;
                if (top && IsRepositoryLevel(name, isFolder: true)) continue;
                var subRelative = Join(relative, name);
                if (nested.Contains(subRelative, StringComparer.Ordinal)) continue;
                stack.Push((sub, Path.Combine(target, name), subRelative));
            }
        }
    }

    /// <summary>
    /// What in <paramref name="source"/> is outside every extension folder,
    /// as whole folders where nothing inside them moves.
    /// </summary>
    private static IEnumerable<MigrationLeftBehind> OutsideExtensions(ClonedSource source, IReadOnlyList<string> extensionPaths, TemplateFiles template)
    {
        if (extensionPaths.Contains(string.Empty))
        {
            // The repository is the extension; only its repository-level files stay behind.
            foreach (var file in Directory.EnumerateFiles(source.Directory, "*", NoLinks))
            {
                var name = Path.GetFileName(file);
                if (IsRepositoryLevel(name, isFolder: false)) yield return new MigrationLeftBehind(source.RepositoryName, name, ReasonFor(name, template), 1);
            }
            foreach (var sub in Directory.EnumerateDirectories(source.Directory, "*", NoLinks))
            {
                var name = Path.GetFileName(sub);
                if (!IsRepositoryLevel(name, isFolder: true)) continue;
                var count = Directory.EnumerateFiles(sub, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Count();
                if (count > 0) yield return new MigrationLeftBehind(source.RepositoryName, name + "/", ReasonFor(name + "/", template), count);
            }
            yield break;
        }

        var stack = new Stack<(string Dir, string Relative)>();
        stack.Push((source.Directory, string.Empty));
        while (stack.Count > 0)
        {
            var (dir, relative) = stack.Pop();
            foreach (var file in Directory.EnumerateFiles(dir, "*", NoLinks))
            {
                var path = Join(relative, Path.GetFileName(file));
                yield return new MigrationLeftBehind(source.RepositoryName, path, ReasonFor(path, template), 1);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir, "*", NoLinks))
            {
                var name = Path.GetFileName(sub);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".alpackages", StringComparison.OrdinalIgnoreCase)) continue;
                var subRelative = Join(relative, name);
                if (extensionPaths.Contains(subRelative, StringComparer.Ordinal)) continue;
                if (extensionPaths.Any(p => IsBelow(p, subRelative)))
                {
                    stack.Push((sub, subRelative));
                    continue;
                }
                var count = Directory.EnumerateFiles(sub, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }).Count();
                if (count == 0) continue;
                yield return new MigrationLeftBehind(source.RepositoryName, subRelative + "/", ReasonFor(subRelative + "/", template), count);
            }
        }
    }

    private static MigrationLeftBehindReason ReasonFor(string path, TemplateFiles template)
    {
        var replaced = path.EndsWith('/')
            ? template.Files.Keys.Any(f => f.StartsWith(path, StringComparison.OrdinalIgnoreCase))
            : template.Files.ContainsKey(path)
              || template.Files.Keys.Any(f => f.Equals(path, StringComparison.OrdinalIgnoreCase))
              || (path.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase) && template.CodeWorkspaceFile is not null);
        return replaced ? MigrationLeftBehindReason.ReplacedByTemplate : MigrationLeftBehindReason.NotInExtension;
    }

    /// <summary>
    /// Removes what the template's <c>.gitignore</c> ignores inside the moved
    /// extensions, as git itself decides it, so the preview and the push agree.
    /// </summary>
    private async Task<IReadOnlyList<MigrationLeftBehind>> RemoveIgnoredAsync(
        string destination, IReadOnlyList<MigrationExtension> extensions, CancellationToken ct)
    {
        var git = RepositoryMigrationPreviewer.GitPath();
        var gitDir = Path.Combine(destination, ".git");
        try
        {
            var init = await _processes.RunAsync(new ProcessRunRequest(git, ["init", "--quiet", destination], destination, null, GitTimeout), ct).ConfigureAwait(false);
            if (!init.Succeeded) throw new InvalidOperationException($"git init failed with exit code {init.ExitCode}.");
            var listed = await _processes.RunAsync(new ProcessRunRequest(git,
                ["-C", destination, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "-z"], destination, null, GitTimeout), ct).ConfigureAwait(false);
            if (!listed.Succeeded) throw new InvalidOperationException($"git ls-files failed with exit code {listed.ExitCode}.");

            var byFolder = extensions.ToDictionary(e => e.Folder, StringComparer.OrdinalIgnoreCase);
            var result = new List<MigrationLeftBehind>();
            foreach (var path in listed.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var slash = path.IndexOf('/');
                if (slash <= 0 || !byFolder.TryGetValue(path[..slash], out var extension)) continue;
                var full = Under(destination, path.TrimEnd('/'));
                var count = 1;
                if (Directory.Exists(full))
                {
                    count = Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Count();
                    Directory.Delete(full, recursive: true);
                }
                else if (File.Exists(full))
                {
                    File.Delete(full);
                }
                else
                {
                    continue;
                }
                var inSource = Join(extension.SourcePath, path[(slash + 1)..]);
                result.Add(new MigrationLeftBehind(extension.RepositoryName, inSource, MigrationLeftBehindReason.IgnoredByGitignore, count));
            }
            return result;
        }
        finally
        {
            if (Directory.Exists(gitDir)) DeleteTree(gitDir);
        }
    }

    // ===== Helpers =====

    /// <summary>
    /// Files that belong to a repository rather than to the extension at its
    /// root: its build pipelines, its workspace file and its ignore list. The
    /// template brings its own, so a root extension leaves these behind.
    /// </summary>
    private static bool IsRepositoryLevel(string name, bool isFolder) => isFolder
        ? name.Equals(".azuredevops", StringComparison.OrdinalIgnoreCase)
          || name.Equals(".pipelines", StringComparison.OrdinalIgnoreCase)
          || name.Equals(".github", StringComparison.OrdinalIgnoreCase)
        : name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
          || name.EndsWith(".code-workspace", StringComparison.OrdinalIgnoreCase)
          || (name.StartsWith("azure-pipelines", StringComparison.OrdinalIgnoreCase)
              && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)));

    private static bool IsTooling(string name) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".alpackages", StringComparison.OrdinalIgnoreCase);

    private static bool IsBelow(string path, string folder) =>
        folder.Length == 0 ? path.Length > 0 : path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static bool InFolders(string path, IReadOnlyCollection<string> folders) =>
        folders.Any(f => path.Equals(f, StringComparison.OrdinalIgnoreCase) || path.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase));

    private static string Join(string relative, string name) => relative.Length == 0 ? name : $"{relative}/{name}";

    /// <summary>A path below <paramref name="root"/>, refusing one that would land outside it.</summary>
    private static string Under(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The path {relative} is outside the workspace.");
        }
        return full;
    }

    private static void Write(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    /// <summary>Deletes a folder git wrote, whose object files are read-only on some systems.</summary>
    private static void DeleteTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(dir, recursive: true);
    }
}
