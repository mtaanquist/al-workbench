using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The Object Explorer's left-hand tree: the branch that opens onto a file,
/// the lazy per-folder children, the by-kind and flat groupings of one app,
/// and the search box that crosses every app in a release. Split out of
/// <see cref="SourceViewerService"/>, which owns the file the tree points at.
/// Project visibility is answered by <see cref="SourceVisibility"/>. All reads
/// are <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>.
/// </summary>
public sealed class ExplorerTreeService
{
    private readonly AppDbContext _db;
    private readonly SourceVisibility _visibility;

    public ExplorerTreeService(AppDbContext db, SourceVisibility visibility)
    {
        _db = db;
        _visibility = visibility;
    }

    /// <summary>
    /// The left-hand explorer tree, opened just far enough to show
    /// <paramref name="fileId"/>: every module in the release at depth 0, and
    /// under the one holding this file, the folder chain down to it with each
    /// level's siblings.
    ///
    /// Deliberately *not* the whole tree. A Base Application module carries
    /// thousands of source files; the closed carets fetch their children from
    /// <see cref="GetTreeChildrenAsync"/> on first open instead.
    /// </summary>
    public async Task<List<OeTreeNode>> GetExplorerTreeAsync(
        long fileId, string grouping = TreeGrouping.Folder, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return [];
        var file = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { f.ModuleId, f.Path, f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (file is null) return [];

        // The same three exclusions ObjectExplorerService.ListModulesAsync
        // applies by default. Without them the tree padded a DVD release with
        // test apps, internal apps and every language pack, and its count
        // disagreed with the release page the reader had just come from. The
        // open file's own module is always kept, whatever it is — the tree
        // cannot omit the branch it exists to show.
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == file.ReleaseId
                     && (m.Id == file.ModuleId
                         || (!m.IsTest && !m.IsInternal && !m.IsLanguagePack)))
            .OrderBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.Version, HasFiles = m.Files.Any() })
            .ToListAsync(ct);

        // Only the folder grouping needs the apps around it - that view answers
        // "where does this live". The others answer "what is in here", which is
        // one app's files and nothing else; the search box and switching back
        // to folders are how you cross apps.
        if (grouping != TreeGrouping.Folder)
        {
            return await ListModuleTreeAsync(file.ModuleId, grouping, fileId, ct);
        }

        var nodes = new List<OeTreeNode>(modules.Count + 32);
        foreach (var m in modules)
        {
            nodes.Add(new OeTreeNode(
                Kind: "module",
                Name: m.Name,
                Path: string.Empty,
                ModuleId: m.Id,
                Depth: 0,
                // A module whose .app shipped without embedded source has no
                // files at all. Claiming a caret for it produced a node that
                // opened, showed nothing, and latched itself as loaded so it
                // could never be tried again.
                HasChildren: m.HasFiles,
                IsOpen: m.Id == file.ModuleId,
                IsActive: false,
                Badge: m.Version));

            if (m.Id != file.ModuleId) continue;
            await AppendOpenChainAsync(nodes, m.Id, file.Path, fileId, ct);
        }
        return nodes;
    }

    /// <summary>
    /// Walks the open file's folder chain, splicing each level's children in
    /// directly beneath the folder they belong to so the flat list reads as a
    /// pre-order tree. Stops as soon as a level doesn't contain the next
    /// chain segment — a path that no longer matches the file rows (a stale
    /// deep link, say) leaves the tree open as far as it was true.
    /// </summary>
    private async Task AppendOpenChainAsync(
        List<OeTreeNode> nodes, long moduleId, string path, long fileId, CancellationToken ct)
    {
        var segments = path.Split('/');
        var prefix = string.Empty;
        var insertAt = nodes.Count;

        for (var level = 0; level < segments.Length; level++)
        {
            var children = await GetTreeChildrenAsync(moduleId, prefix, ct);
            if (children.Count == 0) return;

            var isLeafLevel = level == segments.Length - 1;
            var block = children
                .Select(c => c with
                {
                    Depth = level + 1,
                    IsOpen = !isLeafLevel && c.Kind == "folder" && c.Name == segments[level],
                    IsActive = c.FileId == fileId,
                })
                .ToList();

            nodes.InsertRange(insertAt, block);
            if (isLeafLevel) return;

            var openIndex = block.FindIndex(c => c.IsOpen);
            if (openIndex < 0) return;

            insertAt += openIndex + 1;
            prefix += segments[level] + "/";
        }
    }

    /// <summary>
    /// The immediate children of one folder in one module: sub-folders first,
    /// then files, each alphabetical. <paramref name="prefix"/> is empty for
    /// the module root and otherwise ends in <c>/</c>.
    ///
    /// A file row reads as its object's name rather than its file name, which
    /// is what the tree is for — the file name stays on
    /// <see cref="OeTreeNode.FileName"/> for the row's tooltip. Files without
    /// an object (<c>app.json</c>, a permission XML) keep their file name.
    ///
    /// Both halves filter in SQL: the folder half projects each path's first
    /// remaining segment and takes the distinct set, so expanding <c>src/</c>
    /// on a 7,000-file module returns a dozen rows rather than 7,000 paths to
    /// group in memory.
    ///
    /// <c>StartsWith</c> is safe against a folder name holding a LIKE
    /// metacharacter: EF parameterises it as an already-escaped pattern
    /// (<c>src/Mobile\_WMS/%</c>), so <c>src/Mobile_WMS/</c> does not swallow
    /// <c>src/MobileXWMS/</c>. Pinned by
    /// <c>A_folder_name_holding_a_like_metacharacter_does_not_match_its_neighbour</c>
    /// — do not hand-roll the pattern here, which is what would break it.
    /// </summary>
    public async Task<List<OeTreeNode>> GetTreeChildrenAsync(
        long moduleId, string prefix, CancellationToken ct = default)
    {
        if (!await _visibility.ModuleVisibleAsync(moduleId, ct)) return [];
        prefix ??= string.Empty;
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        var skip = prefix.Length;

        var folders = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId && f.Path.StartsWith(prefix))
            .Select(f => f.Path.Substring(skip))
            .Where(tail => tail.Contains("/"))
            .Select(tail => tail.Substring(0, tail.IndexOf("/")))
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync(ct);

        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId
                     && f.Path.StartsWith(prefix)
                     && !f.Path.Substring(skip).Contains("/"))
            .Select(f => new { f.Id, Tail = f.Path.Substring(skip) })
            .ToListAsync(ct);

        // Second round-trip rather than a correlated projection - see
        // LoadPrimaryObjectsAsync for why inlining it is a trap.
        var objects = await LoadPrimaryObjectsAsync(files.Select(f => f.Id).ToList(), ct);

        var nodes = new List<OeTreeNode>(folders.Count + Math.Min(files.Count, MaxFilesPerFolder) + 1);
        // Re-sorted here rather than trusting the database's ORDER BY: the file
        // half sorts in memory with an ordinal comparer, and a folder listing
        // whose two halves disagree on where an underscore goes reads as a bug.
        folders.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (var name in folders)
        {
            nodes.Add(new OeTreeNode(
                Kind: "folder",
                Name: name,
                Path: prefix + name + "/",
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: true,
                IsOpen: false,
                IsActive: false));
        }

        // Sorted by the name the row actually shows. Keying on `Obj.Name`
        // directly sorted a file whose object row has a blank name under the
        // empty string, so it jumped to the top of the list displaying a file
        // name that belonged further down.
        var ordered = files
            .Select(f =>
            {
                var obj = objects.GetValueOrDefault(f.Id);
                return new { f.Id, f.Tail, Obj = obj, Display = DisplayName(obj?.Name, f.Tail) };
            })
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var f in ordered.Take(MaxFilesPerFolder))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: f.Display,
                Path: prefix + f.Tail,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: f.Id,
                ObjectKind: f.Obj?.Kind,
                FileName: f.Tail,
                Badge: f.Obj?.ObjectId?.ToString()));
        }

        // Say what was left out rather than truncating in silence. The legacy
        // C/AL ingest writes every table into one `CAL/Table/` folder, which
        // is thousands of rows in a 280px rail; the search box reaches them
        // and the tree does not have to.
        var hidden = ordered.Count - MaxFilesPerFolder;
        if (hidden > 0)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: $"{hidden:N0} more - search to find them",
                Path: prefix,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>
    /// How many files one folder draws before it says "and N more". Folders
    /// this large do not occur in an AL layout; they occur in the C/AL ingest,
    /// which slices every object of a kind into one folder.
    /// </summary>
    private const int MaxFilesPerFolder = 400;

    /// <summary>
    /// The AL object a file row reads as: the first object declared in the
    /// file, by line number. AL enforces one object per file in practice, so
    /// "first" is unambiguous for everything except a hand-bundled .al.
    /// </summary>
    private sealed record FilePrimaryObject(string Kind, string Name, int? ObjectId);

    /// <summary>
    /// The primary object for each of <paramref name="fileIds"/>, as a lookup.
    ///
    /// This exists as a separate round-trip rather than the obvious correlated
    /// projection inside the file query —
    /// <c>Obj = _db.OeModuleObjects.Where(o =&gt; o.SourceFileId == f.Id)
    /// .OrderBy(o =&gt; o.LineNumber).Select(...).FirstOrDefault()</c> — because
    /// EF cannot emit a correlated <c>LIMIT 1</c> subquery that projects more
    /// than one column. It rewrites that shape into a
    /// <c>ROW_NUMBER() OVER (PARTITION BY source_file_id ORDER BY line_number)</c>
    /// over the org's <em>entire</em> <c>oe_module_objects</c> table, then joins
    /// the windowed result to the handful of files the caller actually asked
    /// for. On a 230-release install that window sorted ~1.6M rows (~1s) to
    /// answer a 25-row folder listing, and it ran on every source-viewer page
    /// load, every caret open, and every keystroke in the explorer's search box
    /// (see the v9 slow-source-load report).
    ///
    /// Keying off an id list instead turns it into an index scan on
    /// <c>IX_oe_module_objects_source_file_id</c> (<c>source_file_id = ANY(…)</c>),
    /// which is bounded by the caller's page rather than by the catalogue.
    /// Keep it that way: re-inlining the subquery reintroduces the window.
    /// </summary>
    private async Task<Dictionary<long, FilePrimaryObject>> LoadPrimaryObjectsAsync(
        IReadOnlyList<long> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0) return [];

        return await MapPrimaryObjectsAsync(
            _db.OeModuleObjects.AsNoTracking()
                .Where(o => o.SourceFileId != null && fileIds.Contains(o.SourceFileId.Value)),
            ct);
    }

    /// <summary>
    /// The same lookup for every file in one module. Listing a whole module
    /// already knows the module, so it says so with one scalar parameter rather
    /// than round-tripping the several thousand file ids a Base Application
    /// would otherwise send back to name the same rows.
    /// </summary>
    private Task<Dictionary<long, FilePrimaryObject>> LoadPrimaryObjectsForModuleAsync(
        long moduleId, CancellationToken ct)
        => MapPrimaryObjectsAsync(
            _db.OeModuleObjects.AsNoTracking()
                .Where(o => o.ModuleId == moduleId && o.SourceFileId != null),
            ct);

    private static async Task<Dictionary<long, FilePrimaryObject>> MapPrimaryObjectsAsync(
        IQueryable<OeModuleObject> query, CancellationToken ct)
    {
        var rows = await query
            .OrderBy(o => o.SourceFileId).ThenBy(o => o.LineNumber)
            .Select(o => new { FileId = o.SourceFileId!.Value, o.Kind, o.Name, o.ObjectId })
            .ToListAsync(ct);

        // Ordered by (file, line), so the first row per file is the one the
        // correlated FirstOrDefault() would have picked.
        var map = new Dictionary<long, FilePrimaryObject>(rows.Count);
        foreach (var r in rows)
        {
            map.TryAdd(r.FileId, new FilePrimaryObject(r.Kind, r.Name, r.ObjectId));
        }
        return map;
    }

    private static string DisplayName(string? objectName, string fileName) =>
        string.IsNullOrWhiteSpace(objectName) ? fileName : objectName;

    /// <summary>
    /// How the explorer arranges one app's files. The folder view is the tree
    /// the handoff draws; the other two exist because a vendor's folder layout
    /// is somebody else's filing system, and the reader usually knows what
    /// kind of object they are after rather than which folder it was filed in.
    /// </summary>
    public static class TreeGrouping
    {
        /// <summary>Apps, then the module's own folders, then files.</summary>
        public const string Folder = "folder";

        /// <summary>One section per AL object kind, files inside.</summary>
        public const string Kind = "kind";

        /// <summary>One alphabetical list of the app's files.</summary>
        public const string None = "none";

        public static string Parse(string? raw) => raw?.ToLowerInvariant() switch
        {
            Kind => Kind,
            None => None,
            _ => Folder,
        };
    }

    /// <summary>
    /// One app's files arranged by <paramref name="grouping"/>, without the
    /// app rows around them. Feeds both the first paint (through
    /// <see cref="GetExplorerTreeAsync"/>) and a live change of grouping,
    /// which is why it returns depths rather than a single level.
    /// </summary>
    public async Task<List<OeTreeNode>> ListModuleTreeAsync(
        long moduleId, string grouping, long? activeFileId = null, CancellationToken ct = default)
    {
        if (!await _visibility.ModuleVisibleAsync(moduleId, ct)) return [];
        var files = await ListModuleFilesAsync(moduleId, ct);
        var mark = (OeTreeNode n) => n with { IsActive = n.FileId != null && n.FileId == activeFileId };

        if (TreeGrouping.Parse(grouping) != TreeGrouping.Kind)
        {
            return files.Select(mark).ToList();
        }

        // Sections in a fixed reading order rather than alphabetical: an AL
        // developer looks for tables and pages far more often than for a
        // permission set, and the overflow row (if the app was capped) belongs
        // at the end whatever happens.
        var nodes = new List<OeTreeNode>(files.Count + 12);
        var groups = files
            .Where(f => f.Kind == "file")
            .GroupBy(f => KindSectionTitle(f.ObjectKind))
            .OrderBy(g => KindSectionRank(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            nodes.Add(new OeTreeNode(
                Kind: "section",
                Name: group.Key,
                Path: group.Key,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: true,
                IsOpen: true,
                IsActive: false,
                Badge: group.Count().ToString("N0")));
            nodes.AddRange(group.Select(f => mark(f with { Depth = 1 })));
        }

        nodes.AddRange(files.Where(f => f.Kind == "overflow"));
        return nodes;
    }

    /// <summary>
    /// Plural section heading for an object kind. Files with no object at all
    /// (<c>app.json</c>, a permission XML) group under "Other files", which is
    /// what they are.
    /// </summary>
    private static string KindSectionTitle(string? kind) => (kind ?? string.Empty).ToLowerInvariant() switch
    {
        "table" => "Tables",
        "tableextension" => "Table extensions",
        "page" => "Pages",
        "pageextension" => "Page extensions",
        "codeunit" => "Codeunits",
        "report" => "Reports",
        "reportextension" => "Report extensions",
        "query" => "Queries",
        "xmlport" => "XMLports",
        "enum" => "Enums",
        "enumextension" => "Enum extensions",
        "interface" => "Interfaces",
        "permissionset" => "Permission sets",
        "permissionsetextension" => "Permission set extensions",
        "controladdin" => "Control add-ins",
        "profile" => "Profiles",
        "menusuite" => "Menu suites",
        "" => "Other files",
        var other => char.ToUpperInvariant(other[0]) + other[1..] + "s",
    };

    private static int KindSectionRank(string title) => title switch
    {
        "Tables" => 0,
        "Table extensions" => 1,
        "Pages" => 2,
        "Page extensions" => 3,
        "Codeunits" => 4,
        "Reports" => 5,
        "Report extensions" => 6,
        "Enums" => 7,
        "Enum extensions" => 8,
        "Queries" => 9,
        "XMLports" => 10,
        "Interfaces" => 11,
        "Other files" => 99,
        _ => 50,
    };

    /// <summary>
    /// Every file in one module as a flat list, for the explorer's flat mode.
    /// The folder tree is the right shape for a vendor layout that groups by
    /// domain; it is the wrong shape when you know the object's name and the
    /// folders are just noise between you and it.
    ///
    /// Capped like a folder listing, and for the same reason — a Base
    /// Application module is thousands of files, and this is the one view that
    /// asks for all of them at once.
    /// </summary>
    public async Task<List<OeTreeNode>> ListModuleFilesAsync(
        long moduleId, CancellationToken ct = default)
    {
        if (!await _visibility.ModuleVisibleAsync(moduleId, ct)) return [];
        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == moduleId)
            .Select(f => new { f.Id, f.Path })
            .ToListAsync(ct);

        var objects = await LoadPrimaryObjectsForModuleAsync(moduleId, ct);

        return BuildFileNodes(moduleId, files.Select(f =>
        {
            var obj = objects.GetValueOrDefault(f.Id);
            return (f.Id, Tail: FileNameOf(f.Path), f.Path, obj?.Kind, obj?.Name, obj?.ObjectId);
        }), ct: ct);
    }

    /// <summary>
    /// Files across the whole release whose object name or path matches
    /// <paramref name="query"/>. Feeds the explorer's search box, which
    /// replaces the tree with its results while it has content.
    ///
    /// Matches the object name first because that is what an AL developer
    /// types; the path is a fallback for the files that have no object
    /// (<c>app.json</c>, a permission XML).
    ///
    /// The two halves are searched separately and merged rather than filtered
    /// in one pass over the release's files. Asking "does this file's object
    /// name match" per file forced the object name to be computed for every
    /// file in the release before anything could be discarded — three times
    /// per row, once for the EXISTS, once for the ILIKE and once for the
    /// ORDER BY (see <see cref="LoadPrimaryObjectsAsync"/> for the shape and
    /// what it cost). Searching <c>oe_module_objects</c> directly lets the
    /// name half ride the <c>ix_oe_module_objects_name_trgm</c> index that
    /// exists for exactly this substring match.
    ///
    /// Each half is capped before the merge, so the listing is "up to
    /// <see cref="MaxSearchResults"/> matches, alphabetical" rather than
    /// strictly the alphabetically-first matches in the release. The overflow
    /// row already tells the reader the set was cut and to narrow the search.
    ///
    /// Searching the object table also widens the name half slightly: a file
    /// bundling several objects now matches on <em>any</em> of them, where the
    /// old shape only ever compared the first one declared. The row still reads
    /// as its first object, so a hit can show a name that doesn't contain the
    /// term. AL is one-object-per-file in practice, so this is close to
    /// theoretical — and "the file holding the object I searched for" is the
    /// answer the reader wanted either way.
    /// </summary>
    public async Task<List<OeTreeNode>> SearchTreeAsync(
        int releaseId, string query, CancellationToken ct = default)
    {
        if (!await _visibility.ReleaseVisibleAsync(releaseId, ct)) return [];
        var needle = (query ?? string.Empty).Trim();
        if (needle.Length < 2) return [];

        // Escaped so a search for "_" or "%" matches those characters
        // rather than everything (#1262), as the object search does.
        var pattern = "%" + ObjectSearchService.EscapeLike(needle) + "%";
        var cap = MaxSearchResults + 1;

        // Object-name half. Driven off oe_module_objects so the trigram index
        // backs the ILIKE.
        //
        // Group to one row per file *before* the cap. Capping the object rows
        // first spends the budget on duplicates: with two matching objects per
        // file it yielded half as many files as asked for, and — because the
        // overflow row keys off the merged count — the listing then claimed to
        // be complete while silently dropping the rest.
        //
        // Ordering by the file's alphabetically-first matching object decides
        // *which* files a truncated search keeps, so the same query always
        // returns the same page rather than whichever rows the planner reached
        // first.
        var nameHits = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId
                     && o.SourceFileId != null
                     && EF.Functions.ILike(o.Name, pattern, "\\"))
            .GroupBy(o => o.SourceFileId!.Value)
            .OrderBy(g => g.Min(o => o.Name))
            .Select(g => g.Key)
            .Take(cap)
            .ToListAsync(ct);

        // Path half - the fallback for files with no object at all.
        var pathHits = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId
                     && EF.Functions.ILike(f.Path, pattern, "\\"))
            .OrderBy(f => f.Path)
            .Select(f => f.Id)
            .Take(cap)
            .ToListAsync(ct);

        var fileIds = nameHits.Union(pathHits).ToList();
        if (fileIds.Count == 0) return [];

        var rows = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.Path, ModuleName = f.Module!.Name, f.ModuleId })
            .ToListAsync(ct);

        var objects = await LoadPrimaryObjectsAsync(fileIds, ct);

        var ordered = rows
            .Select(r =>
            {
                var obj = objects.GetValueOrDefault(r.Id);
                return new { r.Id, r.Path, r.ModuleName, r.ModuleId, Obj = obj,
                             Display = DisplayName(obj?.Name, FileNameOf(r.Path)) };
            })
            .OrderBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodes = new List<OeTreeNode>(Math.Min(ordered.Count, MaxSearchResults) + 1);
        foreach (var r in ordered.Take(MaxSearchResults))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: r.Display,
                Path: r.Path,
                ModuleId: r.ModuleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: r.Id,
                ObjectKind: r.Obj?.Kind,
                FileName: r.Path,
                // The app, not the object id: a search crosses apps, and which
                // one a hit came from is the thing you cannot tell from a name.
                Badge: r.ModuleName));
        }

        if (ordered.Count > MaxSearchResults)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: "More matches than fit - narrow the search",
                Path: string.Empty,
                ModuleId: 0,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>How many search hits the explorer lists before it says so.</summary>
    private const int MaxSearchResults = 200;

    private static string FileNameOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    /// <summary>
    /// Shared row-building for the flat views, so a file reads the same
    /// whichever list it turns up in.
    /// </summary>
    private static List<OeTreeNode> BuildFileNodes(
        long moduleId,
        IEnumerable<(long Id, string Tail, string Path, string? Kind, string? Name, int? ObjectId)> files,
        CancellationToken ct = default)
    {
        var ordered = files
            .Select(f => (f.Id, f.Tail, f.Path, f.Kind, f.ObjectId, Display: DisplayName(f.Name, f.Tail)))
            .OrderBy(f => f.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodes = new List<OeTreeNode>(Math.Min(ordered.Count, MaxFilesPerFolder) + 1);
        foreach (var f in ordered.Take(MaxFilesPerFolder))
        {
            nodes.Add(new OeTreeNode(
                Kind: "file",
                Name: f.Display,
                Path: f.Path,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false,
                FileId: f.Id,
                ObjectKind: f.Kind,
                FileName: f.Tail,
                Badge: f.ObjectId?.ToString()));
        }

        var hidden = ordered.Count - MaxFilesPerFolder;
        if (hidden > 0)
        {
            nodes.Add(new OeTreeNode(
                Kind: "overflow",
                Name: $"{hidden:N0} more - search to find them",
                Path: string.Empty,
                ModuleId: moduleId,
                Depth: 0,
                HasChildren: false,
                IsOpen: false,
                IsActive: false));
        }
        return nodes;
    }

    /// <summary>
    /// How many AL objects the module holds. Drives the count chip in the
    /// explorer pane's head and the module name in the status line.
    /// </summary>
    public Task<int> CountModuleObjectsAsync(long moduleId, CancellationToken ct = default)
        => _db.OeModuleObjects.AsNoTracking().CountAsync(o => o.ModuleId == moduleId, ct);
}
