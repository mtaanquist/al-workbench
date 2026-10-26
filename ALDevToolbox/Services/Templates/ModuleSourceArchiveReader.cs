using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Turns the gzipped tarball GitHub serves for one commit into a module's folder
/// tree (#1415). Everything in the archive is untrusted: paths are checked segment
/// by segment, links are never followed, binary files and oversized files are left
/// out and listed, and the copy as a whole is held to <see cref="ModuleSourceLimits"/>.
/// Nothing touches the disk.
/// </summary>
public static partial class ModuleSourceArchiveReader
{
    /// <summary>
    /// Folder and file names that are never copied, wherever they sit. Editor and
    /// tool settings act on whoever opens the generated workspace (a
    /// <c>.vscode/tasks.json</c> runs commands), and the rest is repository
    /// plumbing or build output that a new extension must not inherit.
    /// </summary>
    private static readonly string[] LeftOutPrefixes = [".git", ".vscode", ".alpackages", ".snapshots", ".altestrunner"];

    // Same rule as the folder editor's (TemplateValidation): no slashes, no
    // leading or trailing whitespace.
    [GeneratedRegex(@"^[^/\\\s][^/\\]*[^/\\\s]$|^[^/\\\s]$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Reads <paramref name="gzippedTar"/> and returns the files under
    /// <paramref name="sourcePath"/> (null for the repository root) as a folder tree.
    /// </summary>
    /// <exception cref="ModuleSourceException">The folder is not in the repository, or the copy is too large.</exception>
    public static async Task<ModuleSourceSnapshot> ReadAsync(Stream gzippedTar, string? sourcePath, CancellationToken ct)
    {
        var prefix = sourcePath is null ? null : sourcePath.Split('/');
        var root = new FolderBuilder(string.Empty);
        var skipped = new List<ModuleSourceSkippedFile>();
        var dependencies = new List<ModuleDependencyInput>();
        int? idRangeSize = null;
        var folderFound = prefix is null;
        var fileCount = 0;
        long totalBytes = 0;

        try
        {
            await using var gzip = new GZipStream(gzippedTar, CompressionMode.Decompress, leaveOpen: true);
            await using var unpacked = new CappedReadStream(gzip, ModuleSourceLimits.MaxUnpackedBytes);
            await using var reader = new TarReader(unpacked, leaveOpen: true);

            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes) continue;

                // GitHub puts everything under one top folder named after the
                // repository and commit; the module's paths start below it.
                var segments = entry.Name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length <= 1) continue;
                var path = segments[1..];

                if (entry.Name.StartsWith('/') || path.Any(s => s is "." or ".."))
                {
                    skipped.Add(new(string.Join('/', path), "Its path points outside the repository."));
                    continue;
                }

                if (prefix is not null)
                {
                    if (!StartsWith(path, prefix)) continue;
                    folderFound = true;
                    path = path[prefix.Length..];
                    if (path.Length == 0) continue;
                }

                var display = string.Join('/', path);

                if (entry.EntryType == TarEntryType.Directory) continue;
                if (IsLeftOut(path)) continue;

                if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
                {
                    skipped.Add(new(display, "It is a link to another file, which is not copied."));
                    continue;
                }
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile))
                {
                    continue;
                }

                var isAppJson = path.Length == 1 && string.Equals(path[0], "app.json", StringComparison.OrdinalIgnoreCase);
                if (path[^1].EndsWith(".app", StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Length == 1 && !isAppJson)
                {
                    skipped.Add(new(display, "Files directly in the extension folder are not copied; put them in a folder."));
                    continue;
                }
                if (path.Any(s => !NamePattern().IsMatch(s)))
                {
                    skipped.Add(new(display, "Its name has spaces at the start or end, which a folder or file name cannot have here."));
                    continue;
                }
                if (entry.Length > ModuleSourceLimits.MaxFileBytes)
                {
                    skipped.Add(new(display, $"It is larger than {ModuleSourceLimits.MaxFileBytes / 1024} KB."));
                    continue;
                }

                var bytes = await ReadAllAsync(entry, ct);
                if (!TryDecodeText(bytes, out var text))
                {
                    skipped.Add(new(display, "It is not a text file."));
                    continue;
                }

                if (isAppJson)
                {
                    ReadAppJson(text, dependencies, ref idRangeSize);
                    continue;
                }

                FolderBuilder? folder = root;
                foreach (var segment in path[..^1]) folder = folder?.Child(segment);
                if (folder is null || !folder.TryAddFile(path[^1], text))
                {
                    skipped.Add(new(display, "Another file or folder beside it has the same name apart from upper and lower case."));
                    continue;
                }

                fileCount++;
                totalBytes += bytes.Length;
                if (fileCount > ModuleSourceLimits.MaxFiles)
                {
                    throw new ModuleSourceException(
                        $"The repository has more than {ModuleSourceLimits.MaxFiles} files to copy. Point the module at a smaller folder.");
                }
                if (totalBytes > ModuleSourceLimits.MaxTotalBytes)
                {
                    throw new ModuleSourceException(
                        $"The files to copy add up to more than {ModuleSourceLimits.MaxTotalBytes / (1024 * 1024)} MB. Point the module at a smaller folder.");
                }
            }
        }
        catch (ArchiveTooLargeException)
        {
            throw new ModuleSourceException(
                $"The repository is too large to copy (more than {ModuleSourceLimits.MaxUnpackedBytes / (1024 * 1024)} MB unpacked).");
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or EndOfStreamException)
        {
            throw new ModuleSourceException("GitHub sent a download that could not be read. Try again.", ex);
        }

        if (!folderFound)
        {
            throw new ModuleSourceException(
                $"The folder '{sourcePath}' is not in the repository at that branch, tag or commit.", isAboutFolder: true);
        }

        return new ModuleSourceSnapshot(root.BuildFolders(), skipped, dependencies, idRangeSize, fileCount, totalBytes);
    }

    private static bool StartsWith(string[] path, string[] prefix)
    {
        if (path.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            // GitHub paths are case-sensitive, and so is the folder field.
            if (!string.Equals(path[i], prefix[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsLeftOut(string[] path) =>
        path.Any(s => LeftOutPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    private static async Task<byte[]> ReadAllAsync(TarEntry entry, CancellationToken ct)
    {
        if (entry.DataStream is null) return [];
        using var buffer = new MemoryStream();
        await entry.DataStream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>
    /// Strict UTF-8 with no NUL bytes; a leading byte-order mark is dropped, as
    /// the editor and generator write files without one.
    /// </summary>
    private static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = string.Empty;
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, start, bytes.Length - start);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the dependencies and the id range out of the repository's own
    /// <c>app.json</c>. A malformed file contributes nothing rather than failing the
    /// copy: the generator writes a fresh <c>app.json</c> either way.
    /// </summary>
    private static void ReadAppJson(string text, List<ModuleDependencyInput> dependencies, ref int? idRangeSize)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var dep in deps.EnumerateArray())
                {
                    if (dep.ValueKind != JsonValueKind.Object) continue;
                    var id = Read(dep, "id") ?? Read(dep, "appId");
                    if (id is null || !Guid.TryParse(id, out _) || !seen.Add(id)) continue;
                    var name = Read(dep, "name");
                    var publisher = Read(dep, "publisher");
                    var version = Read(dep, "version");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(version)) continue;
                    dependencies.Add(new ModuleDependencyInput(id, name.Trim(), publisher.Trim(), version.Trim()));
                }
            }

            var ranges = new List<(int From, int To)>();
            if (root.TryGetProperty("idRanges", out var idRanges) && idRanges.ValueKind == JsonValueKind.Array)
            {
                foreach (var range in idRanges.EnumerateArray()) AddRange(range, ranges);
            }
            else if (root.TryGetProperty("idRange", out var idRange))
            {
                AddRange(idRange, ranges);
            }
            if (ranges.Count > 0)
            {
                var size = ranges.Max(r => r.To) - ranges.Min(r => r.From) + 1;
                if (size > 0) idRangeSize = size;
            }
        }
        catch (JsonException)
        {
            // Not valid JSON: nothing to read.
        }
    }

    private static void AddRange(JsonElement range, List<(int From, int To)> ranges)
    {
        if (range.ValueKind == JsonValueKind.Object
            && range.TryGetProperty("from", out var from) && from.TryGetInt32(out var f)
            && range.TryGetProperty("to", out var to) && to.TryGetInt32(out var t)
            && f > 0 && t >= f)
        {
            ranges.Add((f, t));
        }
    }

    private static string? Read(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Accumulates folders and files in archive order, then sorts them by name.</summary>
    private sealed class FolderBuilder
    {
        private readonly Dictionary<string, FolderBuilder> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public FolderBuilder(string name) => Name = name;

        public string Name { get; private set; }

        /// <summary>The sub-folder <paramref name="name"/>, or null when a file already has that name.</summary>
        public FolderBuilder? Child(string name)
        {
            if (_files.ContainsKey(name)) return null;
            if (!_folders.TryGetValue(name, out var child))
            {
                child = new FolderBuilder(name);
                _folders[name] = child;
            }
            return child;
        }

        public bool TryAddFile(string name, string content) =>
            !_folders.ContainsKey(name) && _files.TryAdd(name, content);

        public List<FolderAuthoring> BuildFolders() =>
            _folders.Values
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Build())
                .ToList();

        private FolderAuthoring Build() => new(
            Path: Name,
            Folders: BuildFolders(),
            Files: _files
                .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileAuthoring(f.Key, f.Value, IsExample: false))
                .ToList());
    }
}

/// <summary>
/// A copy from a repository was refused, with a message the Editor can act on.
/// Raised for problems with the repository itself; GitHub being unreachable is a
/// <see cref="GitHubApiException"/>.
/// </summary>
public sealed class ModuleSourceException : Exception
{
    public ModuleSourceException(string message, bool isAboutFolder = false) : base(message)
    {
        IsAboutFolder = isAboutFolder;
    }

    public ModuleSourceException(string message, Exception inner) : base(message, inner) { }

    /// <summary>True when the folder field is what to fix, so the message shows under it.</summary>
    public bool IsAboutFolder { get; }
}
