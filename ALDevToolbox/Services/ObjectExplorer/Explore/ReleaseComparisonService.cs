using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Module- and file-level comparison between two BC releases, plus the
/// compare-target discovery that powers the "compare this file against…"
/// picker. Split out of <see cref="ObjectExplorerService"/> so the diff
/// surface stands on its own. All reads are <c>AsNoTracking</c> and respect
/// the tenant query filter on <see cref="AppDbContext"/>.
/// </summary>
public sealed class ReleaseComparisonService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ILogger<ReleaseComparisonService> _logger;

    public ReleaseComparisonService(AppDbContext db, ProjectAccess access, ILogger<ReleaseComparisonService> logger)
    {
        _db = db;
        _access = access;
        _logger = logger;
    }

    // ── Project-visibility fence ────────────────────────────────────────
    // A comparison reads two releases, so *both* sides are checked — a caller
    // who can see one side must not learn the other's contents through the
    // diff. A denied side reads as "release missing", the same answer an id
    // from another org gets. See .design/teams-and-visibility.md.

    private async Task<bool> BothSidesVisibleAsync(int leftReleaseId, int rightReleaseId, CancellationToken ct)
        => await _access.IsReleaseVisibleAsync(leftReleaseId, ct)
           && await _access.IsReleaseVisibleAsync(rightReleaseId, ct);

    private async Task<bool> ModuleVisibleAsync(long moduleId, CancellationToken ct)
    {
        var releaseId = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId)
            .Select(m => (int?)m.ReleaseId)
            .FirstOrDefaultAsync(ct);
        return releaseId is null || await _access.IsReleaseVisibleAsync(releaseId.Value, ct);
    }

    /// <summary>
    /// Module-and-file-level diff between two Releases, keyed by
    /// <c>AppId</c> for modules and canonical <c>Path</c> for files inside the
    /// Changed bucket. Read-only — see <c>.design/object-explorer.md</c> for
    /// why <c>ModuleFile.Path</c> is canonicalised at ingest, which is what
    /// makes the path-based file join trustworthy across releases.
    ///
    /// Returns null when either release id doesn't exist (or is soft-deleted).
    /// </summary>
    public async Task<ReleaseCompareSummary?> CompareReleasesAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct = default)
        => (await CompareReleasesCoreAsync(leftReleaseId, rightReleaseId, ct))?.Summary;

    /// <summary>
    /// The comparison plus the per-module file rows it was computed from, so a
    /// caller that needs the file-level detail (<see cref="CompareReleaseFilesFlatAsync"/>)
    /// can walk them in memory instead of re-reading the same rows one module at
    /// a time — the two batched file queries here already hold everything the
    /// flat view renders.
    /// </summary>
    private async Task<CompareContext?> CompareReleasesCoreAsync(
        int leftReleaseId, int rightReleaseId, CancellationToken ct)
    {
        if (!await BothSidesVisibleAsync(leftReleaseId, rightReleaseId, ct)) return null;
        var releases = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == leftReleaseId || r.Id == rightReleaseId)
            .Where(r => r.DeletedAt == null)
            .Select(r => new { r.Id, r.Label })
            .ToListAsync(ct);

        var left = releases.FirstOrDefault(r => r.Id == leftReleaseId);
        var right = releases.FirstOrDefault(r => r.Id == rightReleaseId);
        if (left is null || right is null) return null;

        var leftModules = await LoadModuleCompareRowsAsync(leftReleaseId, ct);
        var rightModules = await LoadModuleCompareRowsAsync(rightReleaseId, ct);

        var leftByApp = OneModulePerApp(leftModules);
        var rightByApp = OneModulePerApp(rightModules);

        var added = new List<ModuleCompareEntry>();
        var removed = new List<ModuleCompareEntry>();
        var changed = new List<ModuleCompareEntry>();

        foreach (var appId in rightByApp.Keys.Except(leftByApp.Keys))
        {
            var m = rightByApp[appId];
            added.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: null, LeftVersion: null,
                RightModuleId: m.ModuleId, RightVersion: m.Version,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }
        foreach (var appId in leftByApp.Keys.Except(rightByApp.Keys))
        {
            var m = leftByApp[appId];
            removed.Add(new ModuleCompareEntry(
                appId, m.Name, m.Publisher,
                LeftModuleId: m.ModuleId, LeftVersion: m.Version,
                RightModuleId: null, RightVersion: null,
                AddedFileCount: 0, RemovedFileCount: 0, ChangedFileCount: 0));
        }

        var intersection = leftByApp.Keys.Intersect(rightByApp.Keys).ToList();

        var leftByModule = new Dictionary<long, Dictionary<string, FileCompareRow>>();
        var rightByModule = new Dictionary<long, Dictionary<string, FileCompareRow>>();

        // For the Changed bucket compute per-module file diff counts in one
        // pass — load the file rows for both sides of every intersection
        // module, key into a dictionary by (ModuleId, Path), walk per AppId.
        if (intersection.Count > 0)
        {
            var leftModIds = intersection.Select(a => leftByApp[a].ModuleId).ToList();
            var rightModIds = intersection.Select(a => rightByApp[a].ModuleId).ToList();

            leftByModule = await LoadFilesByModuleAsync(leftModIds, ct);
            rightByModule = await LoadFilesByModuleAsync(rightModIds, ct);

            foreach (var appId in intersection)
            {
                var lm = leftByApp[appId];
                var rm = rightByApp[appId];
                var lf = leftByModule.GetValueOrDefault(lm.ModuleId, EmptyFiles);
                var rf = rightByModule.GetValueOrDefault(rm.ModuleId, EmptyFiles);

                var addedCount = rf.Keys.Count(p => !lf.ContainsKey(p));
                var removedCount = lf.Keys.Count(p => !rf.ContainsKey(p));
                var changedCount = lf.Count(kv =>
                    rf.TryGetValue(kv.Key, out var r)
                    && !string.Equals(r.ContentHash, kv.Value.ContentHash, StringComparison.Ordinal));

                if (addedCount == 0 && removedCount == 0 && changedCount == 0)
                {
                    continue; // module unchanged — drop from Changed bucket
                }
                changed.Add(new ModuleCompareEntry(
                    appId, lm.Name, lm.Publisher,
                    LeftModuleId: lm.ModuleId, LeftVersion: lm.Version,
                    RightModuleId: rm.ModuleId, RightVersion: rm.Version,
                    AddedFileCount: addedCount,
                    RemovedFileCount: removedCount,
                    ChangedFileCount: changedCount));
            }
        }

        added = added.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        removed = removed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        changed = changed.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();

        _logger.LogInformation(
            "CompareReleases Left={Left} Right={Right} Added={Added} Removed={Removed} Changed={Changed}",
            leftReleaseId, rightReleaseId, added.Count, removed.Count, changed.Count);

        return new CompareContext(
            new ReleaseCompareSummary(
                left.Id, left.Label, right.Id, right.Label, added, removed, changed),
            leftByModule,
            rightByModule);
    }

    private record ModuleCompareRow(long ModuleId, Guid AppId, string Name, string Publisher, string Version);

    /// <summary>One file row as both the summary and the flat view need it.</summary>
    private sealed record FileCompareRow(long Id, string Path, int LineCount, string ContentHash);

    private sealed record CompareContext(
        ReleaseCompareSummary Summary,
        Dictionary<long, Dictionary<string, FileCompareRow>> LeftFilesByModule,
        Dictionary<long, Dictionary<string, FileCompareRow>> RightFilesByModule);

    private static readonly Dictionary<string, FileCompareRow> EmptyFiles = new();

    private async Task<Dictionary<long, Dictionary<string, FileCompareRow>>> LoadFilesByModuleAsync(
        IReadOnlyList<long> moduleIds, CancellationToken ct)
    {
        var files = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => moduleIds.Contains(f.ModuleId))
            .Select(f => new { f.ModuleId, f.Id, f.Path, f.LineCount, f.ContentHash })
            .ToListAsync(ct);
        return files.GroupBy(f => f.ModuleId)
            .ToDictionary(
                g => g.Key,
                g => g.ToDictionary(
                    x => x.Path,
                    x => new FileCompareRow(x.Id, x.Path, x.LineCount, x.ContentHash)));
    }

    /// <summary>
    /// Pairs two modules' files by canonical path into the three buckets, in the
    /// order every caller renders them: added and removed by path, changed in
    /// left-path order. Pure — the rows are already in memory.
    /// </summary>
    private static (List<FileCompareEntry> Added, List<FileCompareEntry> Removed, List<FileCompareEntry> Changed)
        DiffFiles(Dictionary<string, FileCompareRow> leftByPath, Dictionary<string, FileCompareRow> rightByPath)
    {
        var added = new List<FileCompareEntry>();
        var removed = new List<FileCompareEntry>();
        var changed = new List<FileCompareEntry>();

        foreach (var path in rightByPath.Keys.Except(leftByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var r = rightByPath[path];
            added.Add(new FileCompareEntry(path, null, r.Id, 0, r.LineCount));
        }
        foreach (var path in leftByPath.Keys.Except(rightByPath.Keys).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var l = leftByPath[path];
            removed.Add(new FileCompareEntry(path, l.Id, null, l.LineCount, 0));
        }
        foreach (var kv in leftByPath.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!rightByPath.TryGetValue(kv.Key, out var r)) continue;
            if (string.Equals(kv.Value.ContentHash, r.ContentHash, StringComparison.Ordinal)) continue;
            changed.Add(new FileCompareEntry(kv.Key, kv.Value.Id, r.Id, kv.Value.LineCount, r.LineCount));
        }

        return (added, removed, changed);
    }

    /// <summary>
    /// Keys a release's modules by app. A release can hold two versions of one app
    /// (an amend accepts a newer one beside the old), so the higher version wins,
    /// then the newer module: the same pick the chain queries make
    /// (<see cref="ReleaseAncestrySql.WinningModules"/>), and a compare that used
    /// to throw on the duplicate key (#1224).
    /// </summary>
    private static Dictionary<Guid, ModuleCompareRow> OneModulePerApp(List<ModuleCompareRow> modules)
        => modules
            .GroupBy(m => m.AppId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => Version.TryParse(m.Version, out var v) ? v : null)
                      .ThenByDescending(m => m.ModuleId)
                      .First());

    private Task<List<ModuleCompareRow>> LoadModuleCompareRowsAsync(int releaseId, CancellationToken ct)
        => _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new ModuleCompareRow(m.Id, m.AppId, m.Name, m.Publisher, m.Version))
            .ToListAsync(ct);

    /// <summary>
    /// The ids of a release's modules that <see cref="OneModulePerApp"/> picks, so
    /// the object loads can be restricted to them in the query (#1332). Without it
    /// the object compare and "Compare with..." read whichever of two versions of
    /// one app the database returned first, usually the older one. A release holds
    /// tens to a few hundred modules, so picking in memory costs one small read.
    /// </summary>
    private async Task<List<long>> LoadWinningModuleIdsAsync(int releaseId, CancellationToken ct)
        => OneModulePerApp(await LoadModuleCompareRowsAsync(releaseId, ct).ConfigureAwait(false))
            .Values
            .Select(m => m.ModuleId)
            .ToList();

    /// <summary>
    /// File-pair diff for one Changed module. Files are joined on canonical
    /// <c>Path</c>. Returns null when either module id is missing.
    /// </summary>
    public async Task<ModuleFileCompareResult?> CompareModuleFilesAsync(
        long leftModuleId, long rightModuleId, CancellationToken ct = default)
    {
        if (!await ModuleVisibleAsync(leftModuleId, ct) || !await ModuleVisibleAsync(rightModuleId, ct)) return null;
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == leftModuleId || m.Id == rightModuleId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct);

        if (modules.Count < 2) return null;

        var leftFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == leftModuleId)
            .Select(f => new FileCompareRow(f.Id, f.Path, f.LineCount, f.ContentHash))
            .ToListAsync(ct);
        var rightFiles = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.ModuleId == rightModuleId)
            .Select(f => new FileCompareRow(f.Id, f.Path, f.LineCount, f.ContentHash))
            .ToListAsync(ct);

        var (added, removed, changed) = DiffFiles(
            leftFiles.ToDictionary(f => f.Path),
            rightFiles.ToDictionary(f => f.Path));

        var moduleName = modules.FirstOrDefault(m => m.Id == leftModuleId)?.Name
                         ?? modules.First().Name;

        return new ModuleFileCompareResult(
            leftModuleId, rightModuleId, moduleName, added, removed, changed);
    }

    /// <summary>
    /// Default cap on the flat file-diff row count. A DVD-to-DVD compare where a
    /// handful of apps come or go emits one row per file in each of them — Base
    /// Application alone ships thousands — so an uncapped result is tens of
    /// thousands of rows held per circuit and serialised down SignalR. 5,000 is
    /// the same ceiling find-references uses
    /// (<see cref="ReferenceQueryService.MaxReferenceMatches"/>). See issue #685.
    /// </summary>
    public const int MaxCompareFileRows = 5000;

    /// <summary>
    /// Flat per-file rows for every Added / Removed / Modified pair across all
    /// modules in the two releases — the shape the Release-page Compare scope
    /// renders directly into its result table. Empty list when either release
    /// is missing.
    ///
    /// Follows the truncation convention of the reference queries: at most
    /// <paramref name="take"/> + 1 rows come back, so a caller that gets more
    /// than <paramref name="take"/> knows the result was cut and can say
    /// "showing the first N". Pass a null <paramref name="take"/> only when the
    /// caller genuinely wants every row.
    /// </summary>
    public async Task<List<ReleaseCompareFileRow>> CompareReleaseFilesFlatAsync(
        int leftReleaseId, int rightReleaseId, int? take = MaxCompareFileRows,
        string? pathPattern = null, CancellationToken ct = default)
    {
        var context = await CompareReleasesCoreAsync(leftReleaseId, rightReleaseId, ct);
        if (context is null) return new();
        var summary = context.Summary;

        var rows = new List<ReleaseCompareFileRow>();
        // One row past the cap is what tells the caller it was truncated. The
        // per-bucket database reads take the same number in the final sort
        // order, which is safe: rows for one module are contiguous in a
        // (ModuleName, Path) sort, so any row inside the global first N is also
        // inside its own bucket's first N. That only holds while the database
        // and the in-memory sort agree, so both order by the lower-cased value
        // compared byte by byte: the column's own collation (en_US) ranks
        // punctuation and digits differently, and left gaps in the cut (#1262).
        var fetch = take.HasValue ? take.Value + 1 : int.MaxValue;

        // The path filter runs before the cap, in the database where it can, so
        // narrowing a large diff reaches files past the first few thousand rather
        // than filtering only those (#1510).
        var needle = string.IsNullOrWhiteSpace(pathPattern) ? null : pathPattern.Trim();
        var likePattern = needle is null ? null : "%" + ObjectSearchService.EscapeLike(needle) + "%";
        bool PathMatches(string path) => needle is null || path.Contains(needle, StringComparison.OrdinalIgnoreCase);

        // Added / Removed modules: every file in that module is added/removed.
        var addedRightModuleIds = summary.Added.Where(m => m.RightModuleId.HasValue)
            .Select(m => m.RightModuleId!.Value).ToList();
        var removedLeftModuleIds = summary.Removed.Where(m => m.LeftModuleId.HasValue)
            .Select(m => m.LeftModuleId!.Value).ToList();

        if (addedRightModuleIds.Count > 0)
        {
            var addedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => addedRightModuleIds.Contains(f.ModuleId))
                .Where(f => likePattern == null || EF.Functions.ILike(f.Path, likePattern, "\\"))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .OrderBy(f => EF.Functions.Collate(f.ModuleName.ToLower(), "C"))
                .ThenBy(f => EF.Functions.Collate(f.Path.ToLower(), "C"))
                .ThenBy(f => EF.Functions.Collate(f.Path, "C"))
                .Take(fetch)
                .ToListAsync(ct);
            rows.AddRange(addedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "added",
                LeftFileId: null, RightFileId: f.Id)));
        }
        if (removedLeftModuleIds.Count > 0)
        {
            var removedFiles = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => removedLeftModuleIds.Contains(f.ModuleId))
                .Where(f => likePattern == null || EF.Functions.ILike(f.Path, likePattern, "\\"))
                .Select(f => new { f.Id, f.Path, f.ModuleId, ModuleAppId = f.Module!.AppId, ModuleName = f.Module!.Name })
                .OrderBy(f => EF.Functions.Collate(f.ModuleName.ToLower(), "C"))
                .ThenBy(f => EF.Functions.Collate(f.Path.ToLower(), "C"))
                .ThenBy(f => EF.Functions.Collate(f.Path, "C"))
                .Take(fetch)
                .ToListAsync(ct);
            rows.AddRange(removedFiles.Select(f => new ReleaseCompareFileRow(
                f.ModuleAppId, f.ModuleName, f.Path, "removed",
                LeftFileId: f.Id, RightFileId: null)));
        }

        // Changed modules: pair files by path. The rows are the ones the summary
        // above already batch-loaded, so this loop costs no queries at all —
        // both modules belong to the two releases whose visibility was checked
        // once on the way in, so there is nothing left to re-check per module.
        foreach (var m in summary.Changed)
        {
            if (m.LeftModuleId is not { } lm || m.RightModuleId is not { } rm) continue;
            var (pairsAdded, pairsRemoved, pairsChanged) = DiffFiles(
                context.LeftFilesByModule.GetValueOrDefault(lm, EmptyFiles),
                context.RightFilesByModule.GetValueOrDefault(rm, EmptyFiles));

            foreach (var f in pairsAdded.Where(f => PathMatches(f.Path)))
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "added",
                    LeftFileId: null, RightFileId: f.RightFileId));
            }
            foreach (var f in pairsRemoved.Where(f => PathMatches(f.Path)))
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "removed",
                    LeftFileId: f.LeftFileId, RightFileId: null));
            }
            foreach (var f in pairsChanged.Where(f => PathMatches(f.Path)))
            {
                rows.Add(new ReleaseCompareFileRow(m.AppId, m.Name, f.Path, "modified",
                    LeftFileId: f.LeftFileId, RightFileId: f.RightFileId));
            }
        }

        var ordered = rows
            .OrderBy(r => r.ModuleName.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(r => r.Path.ToLowerInvariant(), StringComparer.Ordinal)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .Take(fetch)
            .ToList();

        if (take.HasValue && ordered.Count > take.Value)
        {
            _logger.LogInformation(
                "Flat file compare of releases {Left} and {Right} truncated at {Cap} rows",
                leftReleaseId, rightReleaseId, take.Value);
        }

        return ordered;
    }

    /// <summary>
    /// Object-level diff between two releases, matched by <c>(Kind, ObjectId)</c>
    /// (or <c>(Kind, Name)</c> when an object has no id). The <c>content_hash</c>
    /// of the source file holding each object decides modified / unchanged, and
    /// for objects with no source on either side the owning app stands in (see
    /// <see cref="SameObject"/>). This is the matcher the legacy C/AL Base-vs-Customer compare
    /// uses — the module/AppId-keyed <see cref="CompareReleasesAsync"/> can't
    /// line two independent releases up because each carries a distinct AppId.
    /// <para>Bounded like the file diff (#1228): unchanged objects, the bulk of
    /// any DVD-to-DVD compare, are left out unless
    /// <paramref name="includeUnchanged"/> asks for them, the optional
    /// <paramref name="kind"/> and <paramref name="nameContains"/> narrow the set
    /// before the cap, and at most <paramref name="take"/> rows come back, with
    /// <see cref="ObjectCompareSet.Truncated"/> saying whether more matched.</para>
    /// </summary>
    public async Task<ObjectCompareSet> CompareReleaseObjectsAsync(
        int leftReleaseId, int rightReleaseId,
        int? take = MaxCompareObjectRows,
        bool includeUnchanged = false,
        string? kind = null,
        string? nameContains = null,
        CancellationToken ct = default)
    {
        if (!await BothSidesVisibleAsync(leftReleaseId, rightReleaseId, ct)) return new([], 0, false);
        var left = await LoadCompareObjectsAsync(leftReleaseId, ct).ConfigureAwait(false);
        var right = await LoadCompareObjectsAsync(rightReleaseId, ct).ConfigureAwait(false);

        var leftByKey = ToKeyedMap(left.Where(o => MatchesFilter(o, kind, nameContains)));
        var rightByKey = ToKeyedMap(right.Where(o => MatchesFilter(o, kind, nameContains)));

        var rows = new List<ObjectCompareRow>();

        foreach (var (key, l) in leftByKey)
        {
            if (rightByKey.TryGetValue(key, out var r))
            {
                var status = SameObject(l, r) ? "unchanged" : "modified";
                rows.Add(new ObjectCompareRow(l.Kind, l.ObjectId, l.Name, status, l.FileId, r.FileId));
            }
            else
            {
                rows.Add(new ObjectCompareRow(l.Kind, l.ObjectId, l.Name, "removed", l.FileId, null));
            }
        }
        foreach (var (key, r) in rightByKey)
        {
            if (!leftByKey.ContainsKey(key))
                rows.Add(new ObjectCompareRow(r.Kind, r.ObjectId, r.Name, "added", null, r.FileId));
        }

        var objectCount = rows.Count;
        var ordered = rows
            .Where(r => includeUnchanged || r.Status != "unchanged")
            .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.ObjectId ?? int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var truncated = take.HasValue && ordered.Count > take.Value;
        if (truncated)
        {
            _logger.LogInformation(
                "Object compare of releases {Left} and {Right} truncated at {Cap} rows",
                leftReleaseId, rightReleaseId, take!.Value);
            ordered = ordered.Take(take!.Value).ToList();
        }
        return new ObjectCompareSet(ordered, objectCount, truncated);
    }

    /// <summary>
    /// Default cap on object-level diff rows: the same ceiling as the file diff
    /// (<see cref="MaxCompareFileRows"/>) and find-references, for the same
    /// reason (#1228).
    /// </summary>
    public const int MaxCompareObjectRows = MaxCompareFileRows;

    private static bool MatchesFilter(CompareObject o, string? kind, string? nameContains)
        => (string.IsNullOrWhiteSpace(kind) || string.Equals(o.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrWhiteSpace(nameContains) || o.Name.Contains(nameContains.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The source file of the counterpart object in another Release, matched by
    /// object identity — <c>(Kind, ObjectId)</c>, falling back to
    /// <c>(Kind, Name)</c> for id-less objects like AL interfaces, the same key
    /// <see cref="CompareReleaseObjectsAsync"/> uses. Powers a results row's
    /// "Compare with..." action jumping straight into the side-by-side file
    /// diff. Identity matching (not <c>(AppId, Path)</c>) is deliberate: two
    /// independently imported C/AL releases are distinct synthetic modules, so
    /// a path join would never line them up. Null when the picked Release has
    /// no such object (or its file wasn't ingested).
    /// </summary>
    public async Task<long?> FindObjectFileInReleaseAsync(
        int releaseId, string kind, int? objectId, string name, CancellationToken ct = default)
    {
        if (!await _access.IsReleaseVisibleAsync(releaseId, ct)) return null;
        var moduleIds = await LoadWinningModuleIdsAsync(releaseId, ct).ConfigureAwait(false);
        var query = _db.OeModuleObjects.AsNoTracking()
            .Where(o => moduleIds.Contains(o.ModuleId)
                        && o.Kind == kind
                        && o.SourceFileId != null);
        query = objectId is int id
            ? query.Where(o => o.ObjectId == id)
            : query.Where(o => o.ObjectId == null && o.Name.ToLower() == name.ToLower());
        return await query
            .Select(o => o.SourceFileId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A release's objects, from the one module per app the module compare picks
    /// (<see cref="LoadWinningModuleIdsAsync"/>), so a release amended with a newer
    /// version of an app compares that version's objects (#1332).
    /// </summary>
    private async Task<List<CompareObject>> LoadCompareObjectsAsync(int releaseId, CancellationToken ct)
    {
        var moduleIds = await LoadWinningModuleIdsAsync(releaseId, ct).ConfigureAwait(false);
        return await _db.OeModuleObjects.AsNoTracking()
            .Where(o => moduleIds.Contains(o.ModuleId))
            .Select(o => new CompareObject(
                o.Kind, o.ObjectId, o.Name, o.SourceFileId,
                o.SourceFile != null ? o.SourceFile.ContentHash : null,
                o.Module!.AppId, o.Module.Version, o.Module.AppFileHash))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether two matched objects are the same. The hash is the source file's,
    /// so with source on both sides it decides. An object from a symbols-only
    /// app has no source file, and used to read "modified" on every compare
    /// (#1262); with no source on either side, the same app package (by file
    /// hash, else by app id and version) is the best evidence there is.
    /// </summary>
    private static bool SameObject(CompareObject l, CompareObject r)
    {
        if (l.Hash is not null || r.Hash is not null) return l.Hash == r.Hash;
        if (l.AppFileHash is not null && r.AppFileHash is not null) return l.AppFileHash == r.AppFileHash;
        return l.AppId == r.AppId && string.Equals(l.Version, r.Version, StringComparison.Ordinal);
    }

    private static Dictionary<string, CompareObject> ToKeyedMap(IEnumerable<CompareObject> objects)
    {
        var map = new Dictionary<string, CompareObject>(StringComparer.Ordinal);
        foreach (var o in objects)
        {
            // The objects come from one module per app (LoadCompareObjectsAsync), and
            // (kind, object id) is unique across those; a release with two versions
            // of an app repeats it, which is why the loads keep only the winner.
            // Fall back to name for id-less objects (AL interfaces). First wins on
            // the rare collision between two different apps.
            var key = o.ObjectId is int id
                ? $"{o.Kind}#{id}"
                : $"{o.Kind}#name:{o.Name.ToLowerInvariant()}";
            map.TryAdd(key, o);
        }
        return map;
    }

    private sealed record CompareObject(
        string Kind, int? ObjectId, string Name, long? FileId, string? Hash,
        Guid AppId, string Version, string? AppFileHash);

    /// <summary>
    /// Releases other than the file's own that contain a file at the same
    /// <c>(AppId, Path)</c> — populates the "Compare with release" picker on
    /// the source-file viewer. Only ready Releases that actually carry a
    /// matching file are returned, keeping the dropdown dead-link-free.
    /// <para>
    /// A file in a pipeline build is offered the other builds of its own
    /// Solution, as the build's release page is (#1075): same order (own
    /// pipeline first, newest first), same "Build #N of {pipeline}" labels and
    /// the same cap, from <see cref="BuildCompareCandidates"/>. Every build
    /// shares one release label, so the plain list showed hundreds of
    /// identical entries (#1570). Any other file is offered every other
    /// release with the file except builds, which the global pickers never list.
    /// </para>
    /// </summary>
    public async Task<List<CompareTargetOption>> GetCompareTargetsAsync(
        long fileId, CancellationToken ct = default)
    {
        var anchor = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.Path,
                AppId = f.Module!.AppId,
                ReleaseId = f.Module!.ReleaseId,
                ReleaseKind = f.Module!.Release!.Kind,
                BcVersion = f.Module!.Release!.BcVersion,
                ImportedAt = f.Module!.Release!.ImportedAt,
            })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return new();
        if (!await _access.IsReleaseVisibleAsync(anchor.ReleaseId, ct)) return new();

        var snapshot = await _access.GetSnapshotAsync(ct);
        var visibleRelease = _access.VisibleReleasePredicate(snapshot);

        var files = _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Path == anchor.Path
                && f.Module!.AppId == anchor.AppId
                && f.Module!.ReleaseId != anchor.ReleaseId
                && f.Module!.Release!.Status == "ready"
                && f.Module!.Release!.DeletedAt == null)
            .Where(f => _db.OeReleases.Where(visibleRelease).Any(r => r.Id == f.Module!.ReleaseId));

        // Build releases: the picker's order and labels come from the build list.
        List<ReleaseListItem>? builds = null;
        if (anchor.ReleaseKind == OeRelease.ProjectBuildKind)
        {
            builds = await BuildCompareCandidates.ListAsync(_db, _access, anchor.ReleaseId, ct,
                withFile: (anchor.AppId, anchor.Path));
            if (builds.Count == 0) return new();
            var buildReleaseIds = builds.Select(b => b.Id).ToList();
            files = files.Where(f => buildReleaseIds.Contains(f.Module!.ReleaseId));
        }
        else
        {
            files = files.Where(f => f.Module!.Release!.Kind != OeRelease.ProjectBuildKind);
        }

        var candidates = await files
            .OrderBy(f => f.Module!.Release!.Label)
            .Select(f => new
            {
                ReleaseId = f.Module!.ReleaseId,
                Label = f.Module!.Release!.Label,
                FileId = f.Id,
                f.ModuleId,
                BcVersion = f.Module!.Release!.BcVersion,
                ImportedAt = f.Module!.Release!.ImportedAt,
            })
            .ToListAsync(ct);
        if (candidates.Count == 0) return new();

        // A release holding two versions of this app (an amend) would be listed
        // once per version, and one entry would open the old file. Keep only the
        // file of the version that release resolves to, the module compare's pick;
        // if that version dropped the file, the release is not offered (#1332).
        var releaseIds = candidates.Select(c => c.ReleaseId).Distinct().ToList();
        var appModules = await _db.OeModules.AsNoTracking()
            .Where(m => m.AppId == anchor.AppId && releaseIds.Contains(m.ReleaseId))
            .Select(m => new { m.ReleaseId, m.Id, m.AppId, m.Name, m.Publisher, m.Version })
            .ToListAsync(ct);
        var winningModuleIds = appModules
            .GroupBy(m => m.ReleaseId)
            .Select(g => OneModulePerApp(g.Select(m => new ModuleCompareRow(m.Id, m.AppId, m.Name, m.Publisher, m.Version)).ToList())[anchor.AppId].ModuleId)
            .ToHashSet();

        var offered = candidates.Where(c => winningModuleIds.Contains(c.ModuleId)).ToList();
        if (builds is not null)
        {
            var byRelease = offered.GroupBy(c => c.ReleaseId).ToDictionary(g => g.Key, g => g.First());
            return builds
                .Where(b => byRelease.ContainsKey(b.Id))
                .Select(b => ToOption(b.Id, b.Label, byRelease[b.Id].FileId, b.BcVersion, byRelease[b.Id].ImportedAt))
                .ToList();
        }
        return offered
            .Select(c => ToOption(c.ReleaseId, c.Label, c.FileId, c.BcVersion, c.ImportedAt))
            .ToList();

        CompareTargetOption ToOption(int releaseId, string label, long fileId, string? bcVersion, DateTime importedAt)
            => new(releaseId, label, fileId,
                TargetIsOlder: IsOlderRelease(bcVersion, importedAt, anchor.BcVersion, anchor.ImportedAt));
    }

    /// <summary>
    /// Whether release A predates release B, for putting the older side on the
    /// LEFT of a diff (so green always reads "new in the newer version"). BC
    /// version wins when both parse and differ; import time breaks ties and
    /// covers releases without a version (C/AL exports).
    /// </summary>
    internal static bool IsOlderRelease(
        string? versionA, DateTime importedA, string? versionB, DateTime importedB)
    {
        if (Version.TryParse(versionA, out var a) && Version.TryParse(versionB, out var b) && a != b)
        {
            return a < b;
        }
        return importedA < importedB;
    }
}
