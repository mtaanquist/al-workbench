using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// Cross-module search within a BC release: object / procedure / content
/// search plus the kind and namespace filter-option lists. Ranking and the
/// "Tell Me"-style token parsing live in <see cref="ObjectSearchRanking"/>.
/// Split out of <see cref="ObjectExplorerService"/> so the search surface
/// stands on its own. All reads are <c>AsNoTracking</c> and respect the
/// tenant query filter on <see cref="AppDbContext"/>.
/// </summary>
public sealed class ObjectSearchService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public ObjectSearchService(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>
    /// Resolves the "winning" module ids for a Release's visible chain — the
    /// same recursive-CTE + app-id shadowing the find-references queries use
    /// (<see cref="ReleaseAncestrySql.WinningModules"/>): walk
    /// <c>parent_release_id</c> upward and keep, per app id, the module closest
    /// to the seed. Used to widen the single-Release search surfaces to include
    /// objects inherited from a parent (base) Release.
    ///
    /// <para><b>Tenant fence.</b> The CTE runs as raw SQL and bypasses the EF
    /// query filter, but the ids it returns are only ever fed back into an
    /// org-filtered <c>OeModuleObjects</c> / <c>OeModuleSymbols</c> query, so a
    /// module from another tenant's chain can't surface a foreign object row.
    /// A parent chain never crosses an org boundary (parents are picked from
    /// the same org at import time), and a dependency link is followed only to
    /// a release in the seed's own org. No <c>IgnoreQueryFilters</c>.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveWinningModuleIdsAsync(int releaseId, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT w.id AS "Value" FROM winning w
            """;
        return await _db.Database.SqlQueryRaw<long>(sql, releaseId).ToListAsync(ct);
    }

    /// <summary>
    /// The Release's own module ids (no parent chain). Resolved up front so the
    /// search predicate can be <c>module_id = ANY(&lt;ids&gt;)</c> rather than a
    /// join on <c>module.release_id</c>. The two are equivalent result-wise, but
    /// the direct id-set predicate lets Postgres bitmap-AND the per-module btree
    /// with the name trigram instead of scanning every symbol in the Release and
    /// filtering — the difference between procedure search taking ~1.9 s and
    /// ~440 ms on a full catalogue. Org-scoped (OeModules carries the EF query
    /// filter), so it only ever returns the caller's modules.
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveReleaseModuleIdsAsync(int releaseId, CancellationToken ct) =>
        await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => m.Id)
            .ToListAsync(ct);

    /// <summary>
    /// True when <paramref name="releaseId"/> is visible to the current caller,
    /// on both fences.
    ///
    /// <para><b>Tenant.</b> Hits <c>OeReleases</c>, which carries the EF org query
    /// filter, so it returns false for another tenant's Release — the tenant
    /// fence the raw-SQL content query below relies on (it runs unfiltered, but
    /// only ever touches one Release's files, all of which belong to that
    /// Release's org).</para>
    ///
    /// <para><b>Project visibility.</b> A Release produced by, or imported under,
    /// a Private project is only for that project's people — see
    /// <see cref="ProjectAccess.IsReleaseVisibleAsync"/> and
    /// <c>.design/teams-and-visibility.md</c>.</para>
    ///
    /// <para>Same pattern as <c>ReferenceQueryService.ReleaseVisibleAsync</c>.</para>
    /// </summary>
    private async Task<bool> ReleaseVisibleAsync(int releaseId, CancellationToken ct) =>
        await _db.OeReleases.AsNoTracking().AnyAsync(r => r.Id == releaseId, ct)
        && await _access.IsReleaseVisibleAsync(releaseId, ct);

    /// <summary>
    /// Searches every Module in a Release for objects matching the supplied
    /// kind + name/id filter, ordered by module then kind then name. Bounded
    /// by <paramref name="take"/> so a wide-open search ("just kind=table") on
    /// a 100-app DVD doesn't dump 5000 rows into the browser at once. The UI
    /// nudges the user to narrow the query when the cap is hit.
    /// <para>When <paramref name="includeInherited"/> is set, the search widens
    /// to the Release's whole visible chain (base objects inherited from a
    /// parent Release), not just the Release's own modules.</para>
    /// </summary>
    public async Task<List<ReleaseObjectMatch>> SearchObjectsInReleaseAsync(
        int releaseId, ObjectListFilter filter, int take = 200,
        bool includeInherited = false, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId: null, namespacePrefix: null, winning, out var tokens);
        return await ObjectSearchRanking.ExecuteAndRankAsync(q, tokens, take, ct);
    }

    /// <summary>
    /// Same as <see cref="SearchObjectsInReleaseAsync"/> but with the
    /// extension (= owning module) + namespace filters the legacy
    /// VersionBrowser exposed. <paramref name="moduleId"/> narrows to one
    /// module; <paramref name="namespacePrefix"/> requires
    /// <c>oe_module_objects.namespace</c> to start with the supplied prefix
    /// (no trailing dot — "Microsoft.Warehouse" matches both
    /// "Microsoft.Warehouse" and "Microsoft.Warehouse.ADCS").
    /// </summary>
    public async Task<List<ReleaseObjectMatch>> SearchObjectsInReleaseAsync(
        int releaseId,
        ObjectListFilter filter,
        long? moduleId,
        string? namespacePrefix,
        int take = 500,
        bool includeInherited = false,
        CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId, namespacePrefix, winning, out var tokens);
        return await ObjectSearchRanking.ExecuteAndRankAsync(q, tokens, take, ct);
    }

    /// <summary>
    /// A single page of objects for the release-detail grid, ordered by an
    /// explicit column instead of relevance, with offset paging so the UI can
    /// lazy-load the next batch as the user scrolls. Returns the window plus
    /// the total (filtered) count. Use this when the user has picked a sort
    /// column or is browsing without a search term; the relevance-ranked
    /// <see cref="SearchObjectsInReleaseAsync(int, ObjectListFilter, long?, string?, int, CancellationToken)"/>
    /// stays the path for "best match first" text search.
    /// </summary>
    public async Task<ObjectSearchPage> SearchObjectsPageInReleaseAsync(
        int releaseId,
        ObjectListFilter filter,
        long? moduleId,
        string? namespacePrefix,
        ObjectSortColumn sortColumn,
        bool descending,
        int skip,
        int take,
        bool includeInherited = false,
        CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new ObjectSearchPage(new List<ReleaseObjectMatch>(), 0);
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = BuildFilteredQuery(releaseId, filter, moduleId, namespacePrefix, winning, out _);
        var total = await q.CountAsync(ct);
        var rows = await ApplySort(q, sortColumn, descending)
            .Skip(skip)
            .Take(take)
            .Select(o => new ReleaseObjectMatch(
                o.Id, o.Kind, o.ObjectId, o.Name, o.Namespace,
                o.ModuleId, o.Module!.Name,
                o.SourceFileId, o.LineNumber,
                o.SourceFile != null ? o.SourceFile.LineCount : 0,
                o.VersionList))
            .ToListAsync(ct);
        return new ObjectSearchPage(rows, total);
    }

    /// <summary>
    /// Shared filter assembly for the object queries: release scope, kind(s),
    /// optional module + namespace-prefix narrows, and the "Tell Me" search
    /// tokens. Returns the query plus the positive token texts the ranker
    /// scores (empty when there's no search term).
    /// </summary>
    private IQueryable<OeModuleObject> BuildFilteredQuery(
        int releaseId, ObjectListFilter filter, long? moduleId, string? namespacePrefix,
        IReadOnlyList<long>? winningModuleIds,
        out IReadOnlyList<string> tokens)
    {
        // Release-scoped by default; chain-scoped (the Release's winning modules,
        // base objects included) when the caller resolved the inherited set.
        var q = winningModuleIds is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winningModuleIds.Contains(o.ModuleId));

        // A leading `kind:` prefix in the search box (e.g. `t:item`) scopes the
        // query to one kind; AND it with the Object-type dropdown selection.
        // Since an object has exactly one kind, a prefix outside the selected
        // set matches nothing. The remainder matches the object name.
        var (kindFromPrefix, searchRemainder) = ObjectSearchRanking.ExtractKindPrefix(filter.Search);
        var kinds = ObjectSearchRanking.NormalizeKinds(filter.Kinds);
        if (kindFromPrefix is not null)
        {
            if (kinds is not null && !kinds.Contains(kindFromPrefix))
            {
                // Prefix kind disjoint from the dropdown selection → empty AND.
                tokens = Array.Empty<string>();
                return q.Where(o => false);
            }
            kinds = new[] { kindFromPrefix };
        }
        if (kinds is { Count: > 0 })
        {
            q = q.Where(o => kinds.Contains(o.Kind));
        }
        if (moduleId is { } mid)
        {
            q = q.Where(o => o.ModuleId == mid);
        }
        if (!string.IsNullOrWhiteSpace(namespacePrefix))
        {
            var ns = namespacePrefix.Trim();
            q = q.Where(o => o.Namespace != null && o.Namespace.StartsWith(ns));
        }
        (q, tokens) = ObjectSearchRanking.ApplySearchTokens(q, searchRemainder);
        return q;
    }

    /// <summary>
    /// Orders the object query by the chosen grid column, always appending a
    /// stable <c>Id</c> tiebreaker so offset paging can't skip or duplicate
    /// rows that tie on the sort key.
    /// </summary>
    private static IOrderedQueryable<OeModuleObject> ApplySort(
        IQueryable<OeModuleObject> q, ObjectSortColumn column, bool descending)
    {
        IOrderedQueryable<OeModuleObject> ordered = (column, descending) switch
        {
            (ObjectSortColumn.Default, _)       => q.OrderBy(o => o.Kind).ThenBy(o => o.ObjectId).ThenBy(o => o.Module!.DependencyCount).ThenBy(o => o.Module!.Name),
            (ObjectSortColumn.Id, false)        => q.OrderBy(o => o.ObjectId),
            (ObjectSortColumn.Id, true)         => q.OrderByDescending(o => o.ObjectId),
            (ObjectSortColumn.Name, false)      => q.OrderBy(o => o.Name),
            (ObjectSortColumn.Name, true)       => q.OrderByDescending(o => o.Name),
            (ObjectSortColumn.Module, false)    => q.OrderBy(o => o.Module!.Name).ThenBy(o => o.Name),
            (ObjectSortColumn.Module, true)     => q.OrderByDescending(o => o.Module!.Name).ThenBy(o => o.Name),
            (ObjectSortColumn.Namespace, false) => q.OrderBy(o => o.Namespace).ThenBy(o => o.Name),
            (ObjectSortColumn.Namespace, true)  => q.OrderByDescending(o => o.Namespace).ThenBy(o => o.Name),
            (ObjectSortColumn.Lines, false)     => q.OrderBy(o => o.SourceFile != null ? o.SourceFile.LineCount : 0),
            (ObjectSortColumn.Lines, true)      => q.OrderByDescending(o => o.SourceFile != null ? o.SourceFile.LineCount : 0),
            (ObjectSortColumn.Type, true)       => q.OrderByDescending(o => o.Kind).ThenBy(o => o.Name),
            _                                   => q.OrderBy(o => o.Kind).ThenBy(o => o.Name),
        };
        return ordered.ThenBy(o => o.Id);
    }

    /// <summary>Distinct object kinds in a Release — feeds the "Object type" dropdown.</summary>
    public async Task<List<string>> ListObjectKindsInReleaseAsync(
        int releaseId, bool includeInherited = false, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = winning is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winning.Contains(o.ModuleId));
        return await q
            .Select(o => o.Kind)
            .Distinct()
            .OrderBy(k => k)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Distinct namespace prefixes in a Release for the "Namespace" filter.
    /// Returns each unique value of <c>oe_module_objects.namespace</c>,
    /// nulls dropped. The dropdown uses a typeahead so a long list (Base App
    /// has 100+ namespaces) is still navigable.
    /// </summary>
    public async Task<List<string>> ListNamespacesInReleaseAsync(
        int releaseId, bool includeInherited = false, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        var winning = includeInherited ? await ResolveWinningModuleIdsAsync(releaseId, ct) : null;
        var q = winning is null
            ? _db.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == releaseId && o.Namespace != null)
            : _db.OeModuleObjects.AsNoTracking().Where(o => winning.Contains(o.ModuleId) && o.Namespace != null);
        return await q
            .Select(o => o.Namespace!)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Procedure-name search across every module in the Release. Matches the
    /// supplied substring (case-insensitive) against <c>oe_module_symbols</c>
    /// where the symbol is procedure-like (<see cref="ProcedureKinds.All"/>:
    /// every procedure visibility, triggers, event publishers and subscribers); the
    /// owning object + module names join inline so the row can render
    /// "Module / Object / Procedure" in one query. Capped at <paramref name="take"/>.
    /// </summary>
    public async Task<List<ReleaseProcedureMatch>> SearchProceduresInReleaseAsync(
        int releaseId, string? search, long? moduleId, int take = 200,
        bool includeInherited = false, CancellationToken ct = default)
    {
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();
        // Scope by an explicit module-id set (own modules, or the whole visible
        // chain when including inherited) rather than a `module.release_id` join.
        // `kind` isn't selective here (most symbols are procedures), so the join
        // form made Postgres scan every procedure in the Release and filter the
        // `%term%` substring in memory (~1.9 s on a full catalogue); the id-set
        // predicate lets it bitmap-AND the per-module btree with the name
        // trigram instead (~440 ms). See ResolveReleaseModuleIdsAsync.
        var moduleIds = includeInherited
            ? await ResolveWinningModuleIdsAsync(releaseId, ct)
            : await ResolveReleaseModuleIdsAsync(releaseId, ct);
        var q = _db.OeModuleSymbols.AsNoTracking()
            .Where(s => moduleIds.Contains(s.ModuleId))
            .Where(s => ProcedureKinds.All.Contains(s.Kind));

        if (moduleId is { } mid)
        {
            q = q.Where(s => s.ModuleId == mid);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            // ILike instead of ToLower().Contains: the latter wraps the column in
            // a function (no index) and uses current-culture casing, which
            // disagrees with the OrdinalIgnoreCase confirmation on a tr-TR host.
            // Escape %/_ so a literal wildcard in the term doesn't match all. #385
            var pattern = "%" + EscapeLike(search.Trim()) + "%";
            q = q.Where(s => EF.Functions.ILike(s.Name, pattern, "\\"));
        }

        return await q.OrderBy(s => s.Module!.Name)
            .ThenBy(s => s.Object!.Name).ThenBy(s => s.Name)
            .Take(take)
            .Select(s => new ReleaseProcedureMatch(
                s.Id,
                s.ObjectId,
                s.Object!.Kind,
                s.Object.Name,
                s.Module!.Name,
                s.Kind,
                s.Name,
                s.Signature,
                s.ReturnType,
                s.Object.SourceFileId,
                // The procedure's own line, so View source opens at it (OEX2-4).
                s.LineNumber > 0 ? s.LineNumber : s.Object.LineNumber))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Minimum length of a content-search term. A one- or two-character term
    /// matches almost every file and floods the results with noise, so it's
    /// rejected up front; the UI and the MCP tool nudge the user to type more.
    /// (It also historically kept the term long enough for the pg_trgm GIN
    /// index — the search now scopes to the Release's own blobs first rather
    /// than the trigram, see <see cref="SearchContentInReleaseAsync"/>, so the
    /// bound is purely a usefulness guard.)
    /// </summary>
    public const int MinContentSearchLength = 3;

    /// <summary>
    /// Content (text) search within a Release, matching <c>content ILIKE
    /// '%term%'</c> against the Release's files (via the shared
    /// <c>oe_file_contents</c> store). The match is scoped to the Release's own
    /// blobs first (see the body) so a common term doesn't fan out across every
    /// imported Release — the difference between ~28 s and ~2 s on a full
    /// catalogue. Terms shorter than <see cref="MinContentSearchLength"/> return
    /// an empty list.
    ///
    /// For each matching file we materialise the line containing the first
    /// hit (or the first <paramref name="maxLinesPerFile"/> hits) so the
    /// result table can show a one-line preview with the right line number
    /// to deep-link to. Capped at <paramref name="take"/> file hits.
    /// </summary>
    public async Task<List<ReleaseContentMatch>> SearchContentInReleaseAsync(
        int releaseId, string search, long? moduleId, int take = 100, int maxLinesPerFile = 3, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(search);
        var needle = search.Trim();
        if (needle.Length < MinContentSearchLength)
        {
            // Too short to form a trigram; matching it would scan the whole
            // content store. The caller surfaces the "type more" hint.
            return new List<ReleaseContentMatch>();
        }
        var pattern = "%" + EscapeLike(needle) + "%";

        // Tenant fence: the query below runs as raw SQL (to pin the plan — see
        // the doc-comment), so it bypasses the EF org filter. Confirm the
        // Release is visible to the caller's org first; a Release's files all
        // belong to its org, so scoping to one visible Release is the fence.
        if (!await ReleaseVisibleAsync(releaseId, ct))
        {
            return new List<ReleaseContentMatch>();
        }

        // Scope the content match to the Release's *own* blobs up front (the
        // `rel` CTE), then ILIKE-filter those — rather than matching the term
        // against the whole deduplicated content store and joining back to the
        // Release afterward. The store is shared across every imported Release,
        // so a term like `%CalcFields%` matches ~20k blobs catalogue-wide that
        // fan out to ~200k files before the Release filter discards 99% (~28 s
        // on a full catalogue). Scoping first reads only the Release's ~15k
        // blobs (~2 s). The redundant `content_hash = ANY(ARRAY(SELECT … FROM
        // rel))` pins the plan to the Release-hash bitmap; without it the
        // planner flips to driving from the trigram (whose ILIKE selectivity it
        // badly under-estimates) and reads the whole store again. MATERIALIZED
        // keeps `rel` computed once. ILIKE uses the default `\` escape, matching
        // EscapeLike (see #385). Raw SQL because LINQ can't pin this plan shape.
        const string sql = """
            WITH rel AS MATERIALIZED (
                SELECT f.id, f.path, f.module_id, m.name AS module_name, f.content_hash
                FROM oe_module_files f
                JOIN oe_modules m ON m.id = f.module_id
                WHERE m.release_id = {0}
                  AND ({1}::bigint IS NULL OR f.module_id = {1}::bigint)
            )
            SELECT rel.id          AS "Id",
                   rel.path        AS "Path",
                   rel.module_id   AS "ModuleId",
                   rel.module_name AS "ModuleName",
                   fc.content      AS "Content"
            FROM rel
            JOIN oe_file_contents fc ON fc.content_hash = rel.content_hash
            WHERE fc.content_hash = ANY(ARRAY(SELECT DISTINCT content_hash FROM rel))
              AND fc.content ILIKE {2}
            ORDER BY rel.module_name, rel.path
            LIMIT {3}
            """;

        // Pull (Id, Path, ModuleId, ModuleName, Content) for the candidate
        // files, then walk each line client-side to pluck the matching line
        // numbers + snippets. Bounded by `take` * `maxLinesPerFile`, which
        // keeps the worst-case payload modest even on a Base App search.
        var candidates = await _db.Database
            .SqlQueryRaw<ContentCandidateRow>(
                sql, releaseId, (object?)moduleId ?? DBNull.Value, pattern, take)
            .ToListAsync(ct);

        var results = new List<ReleaseContentMatch>(candidates.Count);
        foreach (var c in candidates)
        {
            var added = 0;
            var lines = OeSourceText.SplitLines(c.Content);
            for (int i = 0; i < lines.Length && added < maxLinesPerFile; i++)
            {
                if (lines[i].Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    var snippet = lines[i].Trim();
                    if (snippet.Length > 200) snippet = snippet[..200] + "...";
                    results.Add(new ReleaseContentMatch(
                        FileId: c.Id,
                        FilePath: c.Path,
                        ModuleId: c.ModuleId,
                        ModuleName: c.ModuleName,
                        LineNumber: i + 1,
                        Snippet: snippet));
                    added++;
                }
            }
        }
        return results;
    }

    /// <summary>
    /// Row shape for the raw content-search query — the candidate file plus its
    /// full text, line-walked client-side into <see cref="ReleaseContentMatch"/>
    /// snippets. Column names match the quoted aliases in the SQL.
    /// </summary>
    private sealed record ContentCandidateRow(
        long Id, string Path, long ModuleId, string ModuleName, string Content);

    /// <summary>
    /// Escapes the SQL <c>LIKE</c>/<c>ILIKE</c> wildcards <c>%</c> and <c>_</c>
    /// (and the escape char itself) in a user search term, so a literal wildcard
    /// matches literally rather than everything. Paired with the <c>"\\"</c>
    /// escape-character argument on <see cref="EF.Functions"/>.<c>ILike</c>. #385
    /// </summary>
    internal static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
