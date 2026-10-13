using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The find-references graph over the imported release chain: cross-release
/// reference search (by object and by symbol), interface-method implementation
/// resolution, and the Using / Used-by outline dependencies. These are the
/// queries that walk the recursive release-ancestry CTEs in
/// <see cref="ReleaseAncestrySql"/> and apply the same-AppId shadowing rule.
/// Split out of <see cref="ObjectExplorerService"/> so the headline
/// reference-resolution logic stands on its own. All reads are
/// <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>.
/// </summary>
public sealed class ReferenceQueryService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<ReferenceQueryService> _logger;

    /// <summary>
    /// Upper bound on the rows any single find-references query materialises.
    /// References to a popular base table (<c>Customer</c>, <c>Item</c>) run into
    /// tens of thousands of rows; without a cap the whole set is buffered,
    /// snippet-enriched (loading each source file), and held in the in-memory
    /// session cache — a memory + latency spike. The queries fetch one extra row
    /// so callers can tell the result was truncated. See issue #366.
    /// </summary>
    public const int MaxReferenceMatches = 5000;

    /// <summary>
    /// The SQL row limit for a caller's <paramref name="take"/>: one row past it,
    /// so the caller can tell the result was cut, and never past the global cap.
    /// A caller that shows fewer rows than the cap passes its own <c>take</c>, so
    /// only the rows it returns are fetched and snippet-enriched (#1247).
    /// </summary>
    private static int RowLimit(int take) => Math.Clamp(take, 1, MaxReferenceMatches) + 1;

    public ReferenceQueryService(
        AppDbContext db,
        ProjectAccess access,
        IOrganizationContext orgContext,
        ILogger<ReferenceQueryService> logger)
    {
        _db = db;
        _access = access;
        _orgContext = orgContext;
        _logger = logger;
    }

    // ── Outline dependencies (#148: Using / Used-by) ──────────────────

    /// <summary>
    /// Returns the file's outgoing dependencies (objects this file references)
    /// and incoming dependencies (objects elsewhere that reference this file).
    /// Self-references via <c>Rec</c> / <c>xRec</c> are filtered out on both
    /// sides; "no source" targets land with a null <c>TargetFileId</c> so the
    /// UI can render a non-clickable badge with the standard tooltip.
    ///
    /// Resolution walks the parent-release chain (ancestors only for Using,
    /// full visible chain for Used-by) via the same recursive CTE as
    /// <see cref="FindReferencesAsync"/>. Returns null when the file id
    /// doesn't exist.
    /// </summary>
    public async Task<FileDependencies?> GetFileDependenciesAsync(
        long fileId, CancellationToken ct = default)
    {
        var anchor = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                f.ModuleId,
                AppId = f.Module!.AppId,
                ReleaseId = f.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (anchor is null) return null;
        // Project-visibility fence: the file belongs to a release, and a release
        // produced under a Private project is only for that project's people.
        if (!await ReleaseVisibleAsync(anchor.ReleaseId, ct)) return null;

        // The file's "primary objects" — usually one, occasionally a
        // pageextension shipping multiple objects per .al file.
        var ownObjects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.ObjectId, o.Name })
            .ToListAsync(ct);

        if (ownObjects.Count == 0)
        {
            return new FileDependencies(Array.Empty<DependencyEntry>(), Array.Empty<DependencyEntry>());
        }

        var ownObjectIds = ownObjects.Select(o => o.Id).ToList();

        // Build the self-reference exclusion predicate components: any
        // (appId, kind, objectId|name) matching one of this file's objects.
        var selfKeys = ownObjects
            .Select(o => (Kind: o.Kind, ObjectId: o.ObjectId, Name: o.Name))
            .ToHashSet();

        bool IsSelfReference(string targetKind, int? targetId, string targetName) =>
            selfKeys.Contains((targetKind, targetId, targetName))
            || (targetId.HasValue && selfKeys.Any(k => k.Kind == targetKind && k.ObjectId == targetId))
            || selfKeys.Any(k => k.Kind == targetKind && k.Name == targetName);

        var usingList = await BuildUsingAsync(anchor.ReleaseId, ownObjectIds, IsSelfReference, ct);
        var usedByList = await BuildUsedByAsync(anchor.ReleaseId, ownObjects.Select(o => new ObjectSelfKey(o.Kind, o.ObjectId, o.Name)).ToList(), anchor.AppId, IsSelfReference, ct);

        _logger.LogInformation(
            "GetFileDependencies FileId={FileId} UsingCount={UsingCount} UsedByCount={UsedByCount}",
            fileId, usingList.Count, usedByList.Count);

        return new FileDependencies(usingList, usedByList);
    }

    private record ObjectSelfKey(string Kind, int? ObjectId, string Name);

    /// <summary>
    /// Outgoing dependencies. UNION of typed globals and explicit references,
    /// resolved through the parent-release ancestor chain to land in the
    /// "winning" target module.
    /// </summary>
    private async Task<List<DependencyEntry>> BuildUsingAsync(
        int releaseId,
        IReadOnlyList<long> ownObjectIds,
        Func<string, int?, string, bool> isSelfReference,
        CancellationToken ct)
    {
        if (ownObjectIds.Count == 0) return new();

        const string sql = ReleaseAncestrySql.WinningModulesWithName + "\n" + """
            ,
            outgoing AS (
                SELECT mv.target_app_id, mv.target_object_kind,
                       mv.target_object_id, mv.target_object_name,
                       'variable_type'::text AS reference_kind
                FROM oe_module_variables mv
                WHERE mv.object_id = ANY({1})
                  AND mv.target_app_id IS NOT NULL
                  AND mv.target_object_kind IS NOT NULL
                  AND mv.target_object_name IS NOT NULL
                UNION ALL
                SELECT mr.target_app_id, mr.target_object_kind,
                       mr.target_object_id, mr.target_object_name,
                       mr.reference_kind
                FROM oe_module_references mr
                WHERE mr.source_object_id = ANY({1})
            )
            SELECT DISTINCT ON (o.target_app_id, o.target_object_kind, o.target_object_name)
                o.target_app_id        AS "TargetAppId",
                COALESCE(w.name, '')   AS "TargetModuleName",
                o.target_object_kind   AS "TargetObjectKind",
                o.target_object_id     AS "TargetObjectId",
                o.target_object_name   AS "TargetObjectName",
                tgt.source_file_id     AS "TargetFileId",
                tgt.line_number        AS "TargetLineNumber",
                o.reference_kind       AS "ReferenceKind"
            FROM outgoing o
            LEFT JOIN winning w ON w.app_id = o.target_app_id
            LEFT JOIN oe_module_objects tgt
                ON tgt.module_id = w.id
                AND tgt.kind = o.target_object_kind
                AND (
                    (o.target_object_id IS NOT NULL AND tgt.object_id = o.target_object_id)
                    OR (o.target_object_id IS NULL AND tgt.name = o.target_object_name)
                )
            -- Dedupe key is (app, kind, name): AL guarantees a single
            -- object per (app, kind, name), so two source rows for the
            -- same target are always the same object. The ORDER BY
            -- breaks ties so the surviving row carries the most useful
            -- payload: prefer a non-null target_object_id (lets the
            -- LEFT JOIN against oe_module_objects hit the object_id
            -- branch instead of the name branch), then prefer
            -- reference_kind alphabetically for determinism. Without
            -- this an extends_target row (object_id=null) and a
            -- variable_type row (object_id=set) for the same base
            -- table both surfaced as separate Using entries — the
            -- user-reported "Gen. Journal Line shows twice in the
            -- Using list" bug.
            ORDER BY o.target_app_id, o.target_object_kind, o.target_object_name,
                     (o.target_object_id IS NULL), o.target_object_id,
                     o.reference_kind
            """;

        var rows = await _db.Database
            .SqlQueryRaw<DependencyEntry>(sql, releaseId, ownObjectIds.ToArray())
            .ToListAsync(ct);

        return rows
            .Where(r => !isSelfReference(r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName))
            .OrderBy(r => r.TargetModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Incoming dependencies. For each of the file's primary objects, find
    /// every <c>oe_module_references</c> row across the visible release chain
    /// whose target matches. Mirrors <see cref="FindReferencesAsync"/>'s
    /// recursive-CTE + shadowing logic but accepts an <c>IN</c> list of target
    /// objects so we can dispatch all the file's objects in one SQL round-trip.
    /// </summary>
    private async Task<List<DependencyEntry>> BuildUsedByAsync(
        int releaseId,
        IReadOnlyList<ObjectSelfKey> ownTargets,
        Guid ownAppId,
        Func<string, int?, string, bool> isSelfReference,
        CancellationToken ct)
    {
        if (ownTargets.Count == 0) return new();

        // The kind list narrows the cross-module reference fan-out cheaply.
        var kinds = ownTargets.Select(t => t.Kind).Distinct().ToArray();
        var ids = ownTargets.Where(t => t.ObjectId.HasValue).Select(t => t.ObjectId!.Value).Distinct().ToArray();
        var names = ownTargets.Select(t => t.Name).Distinct().ToArray();

        // Project the *caller* side as the dependency entry — Used-by surfaces
        // who points at this file, not the file's own objects. Walks the
        // parent-release ancestor chain only — same shape as
        // FindReferencesAsync. A child release sees its parent's callers; a
        // parent release doesn't see hits from un-attached children (matches
        // the existing find-references contract).
        // Match the references in a `matched` CTE scoped to the winning modules
        // (`module_id = ANY(...)`), then enrich. The id and name branches are a
        // UNION ALL, not an OR, so each uses its own module-scoped index
        // (ix_oe_module_references_module_target /
        // ix_oe_module_references_module_target_name) instead of one BitmapOr
        // dragged onto the app-keyed indexes, which match the target in *every*
        // imported Release (the GUID is Release-stable) and fan out to ~200k
        // rows. `target_app_id` stays as a filter because the module-scoped
        // indexes are keyed on (kind, id|name) only — a different app can ship
        // the same object id. The two branches are mutually exclusive
        // (id-branch needs target_object_id set, name-branch needs it null), so
        // UNION ALL can't duplicate. See .design/object-explorer.md.
        const string callerSql = ReleaseAncestrySql.WinningModulesWithName + "\n" + """
            ,
            matched AS (
                SELECT mr.module_id, mr.source_object_id, mr.reference_kind, mr.line_number
                FROM oe_module_references mr
                WHERE mr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND mr.target_app_id = {1}::uuid
                  AND mr.target_object_kind = ANY({2})
                  AND mr.target_object_id = ANY({3})
                UNION ALL
                SELECT mr.module_id, mr.source_object_id, mr.reference_kind, mr.line_number
                FROM oe_module_references mr
                WHERE mr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND mr.target_app_id = {1}::uuid
                  AND mr.target_object_kind = ANY({2})
                  AND mr.target_object_id IS NULL
                  AND mr.target_object_name = ANY({4})
            )
            SELECT DISTINCT ON (m.app_id, so.kind, COALESCE(so.object_id, -1), so.name)
                m.app_id              AS "TargetAppId",
                m.name                AS "TargetModuleName",
                so.kind               AS "TargetObjectKind",
                so.object_id          AS "TargetObjectId",
                so.name               AS "TargetObjectName",
                so.source_file_id     AS "TargetFileId",
                so.line_number        AS "TargetLineNumber",
                mr.reference_kind     AS "ReferenceKind"
            FROM matched mr
            JOIN oe_module_objects so ON so.id = mr.source_object_id
            JOIN oe_modules        m  ON m.id  = mr.module_id
            ORDER BY m.app_id, so.kind, COALESCE(so.object_id, -1), so.name, mr.line_number
            """;

        var callers = await _db.Database
            .SqlQueryRaw<DependencyEntry>(
                callerSql,
                releaseId,
                ownAppId,
                kinds,
                ids.Length == 0 ? new[] { -1 } : ids,
                names)
            .ToListAsync(ct);

        return callers
            .Where(r => !(r.TargetAppId == ownAppId
                && isSelfReference(r.TargetObjectKind, r.TargetObjectId, r.TargetObjectName)))
            .OrderBy(r => r.TargetModuleName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TargetObjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Find references ────────────────────────────────────────────────

    /// <summary>
    /// True when <paramref name="releaseId"/> is visible to the current caller,
    /// on both fences.
    ///
    /// <para><b>Tenant.</b> Hits <c>_db.OeReleases</c>, which carries the EF org
    /// query filter, so it returns false for another tenant's release — the
    /// hard fence the raw-SQL reference/dependency CTEs rely on, since those
    /// run unfiltered.</para>
    ///
    /// <para><b>Project visibility.</b> A release produced by, or imported under,
    /// a Private project is only for that project's people — see
    /// <see cref="ProjectAccess.IsReleaseVisibleAsync"/> and
    /// <c>.design/teams-and-visibility.md</c>.</para>
    /// </summary>
    private async Task<bool> ReleaseVisibleAsync(int releaseId, CancellationToken ct) =>
        await _db.OeReleases.AsNoTracking().AnyAsync(r => r.Id == releaseId, ct)
        && await _access.IsReleaseVisibleAsync(releaseId, ct);

    /// <summary>
    /// SQL predicate fragment matching the member-kind parameter (<c>{6}</c>)
    /// against a kind column, treating the field-like kinds
    /// (<c>field</c> / <c>table_field</c> / <c>page_field</c>) as
    /// interchangeable. The AL and C/AL importers disagree on the stored field
    /// kind (<c>table_field</c> vs <c>field</c>) and the MCP <c>find_references</c>
    /// tool asks for <c>field</c>, so an exact equality silently dropped every
    /// field-scoped query (an object had matching member rows but the kind
    /// filter excluded them). Field-like kinds collapse to one set; every other
    /// kind (<c>procedure</c>, <c>trigger</c>, …) keeps exact matching so a
    /// field query can't pick up a same-named procedure. Shared by the call-site
    /// filter (<see cref="FindReferencesAsync"/>) and the declaration bucket
    /// (<see cref="FindReferencesForSymbolAsync"/>) so the two can't drift.
    /// (cf. the field-kind helper in <c>AlProcedureWalker</c>.)
    /// </summary>
    private static string MemberKindMatch(string kindColumn) =>
        $"({{6}}::text IS NULL "
        + $"OR {kindColumn} = {{6}}::text "
        + $"OR (lower({{6}}::text) IN ('field','table_field','page_field') "
        + $"AND lower({kindColumn}) IN ('field','table_field','page_field')))";

    /// <summary>
    /// Finds every reference in the visible module-chain of <paramref name="releaseId"/>
    /// that targets the supplied object. "Visible module chain" =
    /// <list type="number">
    ///   <item>walk <c>parent_release_id</c> up from this Release to the root;</item>
    ///   <item>among same-<c>app_id</c> Modules at any depth, the one in the closest
    ///         Release to the current one wins (shadowing);</item>
    ///   <item>references on the winning Modules are the result set.</item>
    /// </list>
    /// Reference matching uses the target triplet: <c>app_id</c> + <c>kind</c> +
    /// (id if present else name). The result is ordered by source module name
    /// then source object name so the UI can render it without further sort.
    /// </summary>
    public async Task<List<ReferenceMatch>> FindReferencesAsync(
        int releaseId, FindReferencesQuery query, int take = MaxReferenceMatches, CancellationToken ct = default)
    {
        var limit = RowLimit(take);
        // Tenant fence: the recursive CTE below runs as raw SQL and so bypasses
        // the EF query filter. Confirm the seed release is visible in the
        // caller's org first, otherwise return empty rather than leaking
        // another tenant's reference graph. See ReleaseVisibleAsync.
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();

        // Use a parameterised raw SQL because LINQ to SQL can't express the
        // recursive CTE neatly. The SQL is bounded, documented, and lives
        // here in one place — see the class doc-comment for the resolution
        // algorithm it implements.
        // Same module-scoping + UNION-ALL split as BuildUsedByAsync (see the
        // comment there): match the references in a CTE bounded to the winning
        // modules so each id/name branch uses its module-scoped index instead
        // of the app-keyed indexes that fan out across every imported Release.
        // Here the target is a single object, so exactly one branch is ever
        // live — the `{3}::int IS [NOT] NULL` guards switch between them — but
        // both are spelled out so the id-branch (object_id set) and name-branch
        // (interfaces / id-less extensions) each get their own index path.
        string sql = ReleaseAncestrySql.WinningModules + "\n" + $$"""
            ,
            matched AS (
                SELECT mr.id, mr.module_id, mr.source_object_id, mr.source_symbol_id,
                       mr.reference_kind, mr.line_number,
                       mr.target_member_name, mr.target_member_kind
                FROM oe_module_references mr
                WHERE mr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND {3}::int IS NOT NULL
                  AND mr.target_app_id      = {1}::uuid
                  AND mr.target_object_kind = {2}
                  AND mr.target_object_id   = {3}::int
                  -- Member filter: when the caller scoped to a procedure /
                  -- field, only return rows already tagged with the matching
                  -- member name + kind. Object-level rows (member_name IS NULL)
                  -- drop out — surfaced separately via the owner-type query in
                  -- FindReferencesForSymbolAsync so the UI can group them. The
                  -- kind comparison collapses the field-like kinds (see
                  -- MemberKindMatch): the AL importer stamps `table_field`,
                  -- C/AL stamps `field`, and the MCP tool asks for `field`, so
                  -- an exact match silently dropped every field-scoped query.
                  AND (
                        {5}::text IS NULL
                     OR (mr.target_member_name = {5}::text
                         AND {{MemberKindMatch("mr.target_member_kind")}})
                  )
                UNION ALL
                SELECT mr.id, mr.module_id, mr.source_object_id, mr.source_symbol_id,
                       mr.reference_kind, mr.line_number,
                       mr.target_member_name, mr.target_member_kind
                FROM oe_module_references mr
                WHERE mr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND {3}::int IS NULL
                  AND mr.target_app_id      = {1}::uuid
                  AND mr.target_object_kind = {2}
                  AND mr.target_object_id   IS NULL
                  AND mr.target_object_name = {4}
                  AND (
                        {5}::text IS NULL
                     OR (mr.target_member_name = {5}::text
                         AND {{MemberKindMatch("mr.target_member_kind")}})
                  )
            )
            SELECT
                mr.id                    AS "Id",
                mr.module_id             AS "SourceModuleId",
                m.name                   AS "SourceModuleName",
                mr.source_object_id      AS "SourceObjectId",
                so.kind                  AS "SourceObjectKind",
                so.name                  AS "SourceObjectName",
                mr.reference_kind        AS "ReferenceKind",
                mr.line_number           AS "LineNumber",
                so.source_file_id        AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                CASE
                    WHEN mr.target_member_name IS NULL THEN 'object'
                    ELSE 'call'
                END                      AS "Category",
                mr.target_member_name    AS "MemberName",
                mr.target_member_kind    AS "MemberKind",
                NULL::text               AS "MemberSignature",
                -- Enclosing procedure / trigger the reference sits inside,
                -- resolved from source_symbol_id. Null for object-scope refs
                -- and rows imported before the symbol link existed.
                ss.name                  AS "SourceMemberName",
                ss.kind                  AS "SourceMemberKind",
                ss.signature             AS "SourceMemberSignature"
            FROM matched mr
            JOIN oe_module_objects so ON so.id = mr.source_object_id
            JOIN oe_modules        m  ON m.id  = mr.module_id
            LEFT JOIN oe_module_symbols ss ON ss.id = mr.source_symbol_id
            ORDER BY m.name, so.name, mr.id
            LIMIT {{limit}}
            """;

        var matches = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName,
                (object?)query.TargetMemberName ?? DBNull.Value,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);

        // Enrich each match with the actual code-line snippet and its
        // file path so the References panel can render VS-Code-style rows
        // (module · L42 · the code on that line). One extra query cuts the
        // lines out in the database rather than shipping whole file bodies
        // here — see EnrichReferencesWithSnippetsAsync.
        matches = await EnrichReferencesWithSnippetsAsync(matches, ct);

        _logger.LogInformation(
            "FindReferences ReleaseId={ReleaseId} TargetAppId={TargetAppId} Kind={Kind} Id={Id} Name={Name} Member={Member} Matches={Count}",
            releaseId, query.TargetAppId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName, query.TargetMemberName, matches.Count);

        return matches;
    }

    /// <summary>
    /// "Find System References": every call to a built-in / system method
    /// (<c>Insert</c>, <c>Modify</c>, <c>SetRange</c>, …) on the target object,
    /// read from the separate <c>oe_module_system_references</c> table. Same
    /// recursive-CTE shadowing and target-triplet matching as
    /// <see cref="FindReferencesAsync"/>; optionally narrowed to one method.
    /// Rows are tagged <c>Category = "system_call"</c> so the panel can label
    /// them, with the system method name in <see cref="ReferenceMatch.MemberName"/>.
    /// </summary>
    public async Task<List<ReferenceMatch>> FindSystemReferencesAsync(
        int releaseId, FindSystemReferencesQuery query, int take = MaxReferenceMatches, CancellationToken ct = default)
    {
        var limit = RowLimit(take);
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();

        // Module-scoping + UNION-ALL split, exactly as FindReferencesAsync /
        // BuildUsedByAsync (see those comments): bound the scan to the Release's
        // winning modules and give each id/name branch its own module-scoped
        // index (ix_oe_module_system_references_module_target /
        // ix_oe_module_system_references_module_target_name) instead of the
        // app-keyed indexes that match the receiver in every imported Release.
        string sql = ReleaseAncestrySql.WinningModules + "\n" + $$"""
            ,
            matched AS (
                SELECT sr.id, sr.module_id, sr.source_object_id, sr.source_symbol_id,
                       sr.reference_kind, sr.line_number, sr.system_method_name
                FROM oe_module_system_references sr
                WHERE sr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND {3}::int IS NOT NULL
                  AND sr.target_app_id      = {1}::uuid
                  AND sr.target_object_kind = {2}
                  AND sr.target_object_id   = {3}::int
                  AND ({5}::text IS NULL OR sr.system_method_name = {5}::text)
                UNION ALL
                SELECT sr.id, sr.module_id, sr.source_object_id, sr.source_symbol_id,
                       sr.reference_kind, sr.line_number, sr.system_method_name
                FROM oe_module_system_references sr
                WHERE sr.module_id = ANY(ARRAY(SELECT id FROM winning))
                  AND {3}::int IS NULL
                  AND sr.target_app_id      = {1}::uuid
                  AND sr.target_object_kind = {2}
                  AND sr.target_object_id   IS NULL
                  AND sr.target_object_name = {4}
                  AND ({5}::text IS NULL OR sr.system_method_name = {5}::text)
            )
            SELECT
                sr.id                    AS "Id",
                sr.module_id             AS "SourceModuleId",
                m.name                   AS "SourceModuleName",
                sr.source_object_id      AS "SourceObjectId",
                so.kind                  AS "SourceObjectKind",
                so.name                  AS "SourceObjectName",
                sr.reference_kind        AS "ReferenceKind",
                sr.line_number           AS "LineNumber",
                so.source_file_id        AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                'system_call'::text      AS "Category",
                sr.system_method_name    AS "MemberName",
                'system'::text           AS "MemberKind",
                NULL::text               AS "MemberSignature",
                ss.name                  AS "SourceMemberName",
                ss.kind                  AS "SourceMemberKind",
                ss.signature             AS "SourceMemberSignature"
            FROM matched sr
            JOIN oe_module_objects so ON so.id = sr.source_object_id
            JOIN oe_modules        m  ON m.id  = sr.module_id
            LEFT JOIN oe_module_symbols ss ON ss.id = sr.source_symbol_id
            ORDER BY m.name, so.name, sr.id
            LIMIT {{limit}}
            """;

        var matches = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName,
                (object?)query.SystemMethodName ?? DBNull.Value)
            .ToListAsync(ct);

        matches = await EnrichReferencesWithSnippetsAsync(matches, ct);

        _logger.LogInformation(
            "FindSystemReferences ReleaseId={ReleaseId} TargetAppId={TargetAppId} Kind={Kind} Id={Id} Name={Name} Method={Method} Matches={Count}",
            releaseId, query.TargetAppId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName, query.SystemMethodName, matches.Count);

        return matches;
    }

    /// <summary>
    /// Member-scoped find references: returns everywhere a specific
    /// procedure / field / trigger on a specific owner object is referenced,
    /// across the visible module chain. Composed of three result sets the UI
    /// renders together (each row's <see cref="ReferenceMatch.Category"/>
    /// signals which one it came from):
    /// <list type="number">
    ///   <item><c>declaration</c> — every <c>oe_module_symbols</c> row with
    ///         the same (kind, name) on a matching owner across the chain.
    ///         Surfaces overrides, internal overloads and same-name members
    ///         on extensions.</item>
    ///   <item><c>owner_type</c> — every <c>oe_module_references</c> row
    ///         targeting the owner object at the object level
    ///         (variable_type, parameter_type, return_type, extends_target,
    ///         table_no). Indirect callers — a variable typed to this object
    ///         is a place that could call this member.</item>
    ///   <item><c>call</c> — every <c>oe_module_references</c> row already
    ///         tagged with this member (target_member_name + target_member_kind).
    ///         Populated by call-site / field-access extraction (emitted in
    ///         <see cref="Services.Al.AlProcedureWalker"/>); see
    ///         <c>.design/al-reference-extractor-gaps.md</c> for the covered
    ///         surface and remaining gaps.</item>
    /// </list>
    /// The <c>call</c> bucket is the authoritative "who calls this member"
    /// answer; <c>declaration</c> and <c>owner_type</c> add sibling
    /// declarations and indirect callers (variables typed to the owner).
    /// </summary>
    public async Task<List<ReferenceMatch>> FindReferencesForSymbolAsync(
        int releaseId,
        FindReferencesQuery query,
        int take = MaxReferenceMatches,
        CancellationToken ct = default)
    {
        var limit = RowLimit(take);
        if (string.IsNullOrEmpty(query.TargetMemberName))
        {
            throw new ArgumentException(
                "Member-scoped find requires TargetMemberName.", nameof(query));
        }

        // Tenant fence for the raw-SQL CTEs below (and the private member /
        // interface-implementation queries this method fans out to, which
        // reuse releaseId): bail out for a release not visible in the caller's
        // org. See ReleaseVisibleAsync.
        if (!await ReleaseVisibleAsync(releaseId, ct)) return new();

        // (1) Sibling declarations of the matched member across the chain.
        // Same recursive-CTE + winning-module shadowing the object-level
        // query uses; we then join through to oe_module_symbols by owner +
        // member name.
        string declarationSql = ReleaseAncestrySql.WinningModules + "\n" + $$"""
            SELECT
                s.id                     AS "Id",
                o.module_id              AS "SourceModuleId",
                m.name                   AS "SourceModuleName",
                o.id                     AS "SourceObjectId",
                o.kind                   AS "SourceObjectKind",
                o.name                   AS "SourceObjectName",
                'declaration'::text      AS "ReferenceKind",
                s.line_number            AS "LineNumber",
                o.source_file_id         AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                'declaration'::text      AS "Category",
                s.name                   AS "MemberName",
                s.kind                   AS "MemberKind",
                s.signature              AS "MemberSignature",
                -- A declaration isn't a call site inside a member body.
                NULL::text               AS "SourceMemberName",
                NULL::text               AS "SourceMemberKind",
                NULL::text               AS "SourceMemberSignature"
            FROM oe_module_symbols s
            JOIN oe_module_objects o  ON o.id = s.object_id
            JOIN oe_modules        m  ON m.id = o.module_id
            JOIN winning           w  ON w.id = o.module_id
            WHERE m.app_id              = {1}::uuid
              AND o.kind                = {2}
              AND (
                    ({3}::int IS NOT NULL AND o.object_id = {3}::int)
                 OR ({3}::int IS NULL AND o.name = {4})
              )
              AND s.name                = {5}::text
              AND {{MemberKindMatch("s.kind")}}
            ORDER BY m.name, o.name, s.line_number
            LIMIT {{limit}}
            """;

        var declarations = await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                declarationSql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectKind,
                (object?)query.TargetObjectId ?? DBNull.Value,
                query.TargetObjectName,
                query.TargetMemberName,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);

        // (2) Call sites: rows already tagged with the matching member by
        // call-site / field-access extraction. Reusing FindReferencesAsync
        // keeps the call/object branch logic in one place.
        var memberRefs = await FindReferencesAsync(releaseId, query, take, ct);

        // (3) Owner-type references at the object level are deliberately
        // not collected: on large releases that bucket runs into thousands
        // of rows that aren't meaningful for most "find references" uses
        // and dominate the panel's render time.

        // (4) Interface implementations. When the target owner is an
        // interface, also pull procedures of matching (name, kind) on
        // codeunits whose header declares them as implementing this
        // interface. Source: the `implements_interface` reference rows
        // emitted at import time. Tagged with Category = "implementation"
        // so the UI can group them under their own header.
        List<ReferenceMatch> implementations = new();
        if (string.Equals(query.TargetObjectKind, "interface", StringComparison.OrdinalIgnoreCase))
        {
            implementations = await FindInterfaceMethodImplementationsAsync(releaseId, query, limit, ct);
        }

        // Concatenate. Declarations first (most direct), then concrete
        // member calls (phase-2 will fill this). The owner-type bucket
        // would have gone here.  EnrichReferencesWithSnippetsAsync
        // already ran inside FindReferencesAsync for the member-call
        // set; do it once for the declarations bucket so every row has
        // a snippet.
        declarations = await EnrichReferencesWithSnippetsAsync(declarations, ct);
        implementations = await EnrichReferencesWithSnippetsAsync(implementations, ct);

        var all = new List<ReferenceMatch>(declarations.Count + memberRefs.Count + implementations.Count);
        all.AddRange(declarations);
        all.AddRange(memberRefs);
        all.AddRange(implementations);

        _logger.LogInformation(
            "FindReferencesForSymbol ReleaseId={ReleaseId} Owner={Kind}/{Id}/{Name} Member={Member}/{MemberKind} Decl={DeclCount} Call={CallCount} Impl={ImplCount}",
            releaseId, query.TargetObjectKind, query.TargetObjectId, query.TargetObjectName,
            query.TargetMemberName, query.TargetMemberKind,
            declarations.Count, memberRefs.Count, implementations.Count);

        return all;
    }

    /// <summary>
    /// Returns implementing-codeunit procedure declarations for an
    /// interface method. Joins <c>oe_module_references</c> rows where
    /// <c>reference_kind = 'implements_interface'</c> targets the given
    /// interface (by name; interfaces have no numeric object id) to the
    /// referencing codeunit's <c>oe_module_symbols</c> rows of matching
    /// (name, kind). Uses the same recursive-CTE shadowing as the rest
    /// of the find-references family so results respect the visible
    /// module chain.
    /// </summary>
    private async Task<List<ReferenceMatch>> FindInterfaceMethodImplementationsAsync(
        int releaseId, FindReferencesQuery query, int limit, CancellationToken ct)
    {
        string sql = ReleaseAncestrySql.WinningModules + "\n" + $$"""
            SELECT
                s.id                     AS "Id",
                so.module_id             AS "SourceModuleId",
                sm.name                  AS "SourceModuleName",
                so.id                    AS "SourceObjectId",
                so.kind                  AS "SourceObjectKind",
                so.name                  AS "SourceObjectName",
                'implementation'::text   AS "ReferenceKind",
                s.line_number            AS "LineNumber",
                so.source_file_id        AS "SourceFileId",
                NULL::text               AS "SourceFilePath",
                NULL::text               AS "Snippet",
                'implementation'::text   AS "Category",
                s.name                   AS "MemberName",
                s.kind                   AS "MemberKind",
                s.signature              AS "MemberSignature",
                -- An implementing-procedure declaration, not a call site.
                NULL::text               AS "SourceMemberName",
                NULL::text               AS "SourceMemberKind",
                NULL::text               AS "SourceMemberSignature"
            FROM oe_module_references mr
            JOIN oe_module_objects    so ON so.id = mr.source_object_id
            JOIN oe_modules           sm ON sm.id = mr.module_id
            JOIN winning              w  ON w.id  = mr.module_id
            JOIN oe_module_symbols    s  ON s.object_id = so.id
            WHERE mr.reference_kind     = 'implements_interface'
              AND mr.target_app_id      = {1}::uuid
              AND mr.target_object_kind = 'interface'
              AND mr.target_object_name = {2}::text
              AND s.name                = {3}::text
              AND ({4}::text IS NULL OR s.kind = {4}::text)
            ORDER BY sm.name, so.name, s.line_number
            LIMIT {{limit}}
            """;

        return await _db.Database
            .SqlQueryRaw<ReferenceMatch>(
                sql,
                releaseId,
                query.TargetAppId,
                query.TargetObjectName,
                query.TargetMemberName!,
                (object?)query.TargetMemberKind ?? DBNull.Value)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Stamps each <see cref="ReferenceMatch"/> with the matching line's text
    /// plus the file's path. Rows pointing at modules that ship without source
    /// (SourceFileId == null) are returned unchanged. The snippet is trimmed
    /// and capped to keep the response payload bounded.
    ///
    /// <para>The line is cut out <em>in the database</em> (<c>split_part</c>)
    /// rather than by loading whole file bodies and splitting them in memory:
    /// a capped result set (<see cref="MaxReferenceMatches"/> + 1) spread over
    /// thousands of Base App files pulled tens of megabytes of source over the
    /// wire to keep at most 200 characters per row, all of it then held in a
    /// Blazor circuit and the reference-session cache. See issue #687.</para>
    ///
    /// <para><b>Tenant fence.</b> Raw SQL bypasses the EF query filter, so the
    /// join carries the same <c>organization_id</c> predicate explicitly. It is
    /// fed from <see cref="IOrganizationContext.OrganizationIdForFilter"/> — the
    /// value the EF filter itself uses, which is <c>0</c> (matching nothing)
    /// when no user is signed in, so an unauthenticated call enriches nothing
    /// exactly as the filtered query it replaces did.</para>
    /// </summary>
    private async Task<List<ReferenceMatch>> EnrichReferencesWithSnippetsAsync(
        List<ReferenceMatch> matches, CancellationToken ct)
    {
        // Distinct (file, line) pairs — many matches share a file, and popular
        // objects are referenced repeatedly on the same line.
        var pairs = matches
            .Where(m => m.SourceFileId.HasValue && m.LineNumber is >= 1)
            .Select(m => (FileId: m.SourceFileId!.Value, Line: m.LineNumber!.Value))
            .Distinct()
            .ToList();
        if (pairs.Count == 0) return matches;

        // split_part counts fields from 1 and returns '' past the end of the
        // string, which is exactly what we want for a line number beyond the
        // file. Splitting on '\n' alone leaves the '\r' of CRLF content on the
        // end of the line; the TrimEnd below takes it off along with trailing
        // indentation.
        const string sql = """
            SELECT m.file_id                             AS "FileId",
                   m.line                                AS "LineNumber",
                   f.path                                AS "Path",
                   split_part(fc.content, E'\n', m.line) AS "Text"
            FROM unnest({0}::bigint[], {1}::int[]) AS m(file_id, line)
            JOIN oe_module_files  f  ON f.id = m.file_id AND f.organization_id = {2}::int
            JOIN oe_file_contents fc ON fc.content_hash = f.content_hash
            """;

        var rows = await _db.Database
            .SqlQueryRaw<SnippetRow>(
                sql,
                pairs.Select(p => p.FileId).ToArray(),
                pairs.Select(p => p.Line).ToArray(),
                _orgContext.OrganizationIdForFilter)
            .ToListAsync(ct);

        var lookup = rows.ToDictionary(r => (r.FileId, r.LineNumber));

        var enriched = new List<ReferenceMatch>(matches.Count);
        foreach (var m in matches)
        {
            if (m.SourceFileId is { } fid && m.LineNumber is { } ln
                && lookup.TryGetValue((fid, ln), out var row))
            {
                var raw = (row.Text ?? string.Empty).TrimEnd();
                if (raw.Length > 200) raw = raw[..200] + "...";
                enriched.Add(m with { SourceFilePath = row.Path, Snippet = raw });
            }
            else
            {
                enriched.Add(m);
            }
        }
        return enriched;
    }

    /// <summary>
    /// One extracted source line, keyed back to the requested (file, line) pair
    /// — see <see cref="EnrichReferencesWithSnippetsAsync"/>. <c>Text</c> is
    /// empty when the line number runs past the end of the file.
    /// </summary>
    private sealed record SnippetRow(long FileId, int LineNumber, string Path, string? Text);

    /// <summary>
    /// Returns the codeunits (across the visible module chain seeded by
    /// the interface's defining module) that declare themselves as
    /// implementing the named interface. Backed by the
    /// <c>implements_interface</c> reference rows emitted at import. Feeds the
    /// "implemented by" enrichment on both the object outline
    /// (<see cref="ObjectExplorerService"/>) and the source-file outline
    /// (<see cref="SourceViewerService"/>).
    /// </summary>
    public async Task<List<InterfaceImplementerRow>> FindInterfaceImplementersAsync(
        long interfaceModuleId, string interfaceName, CancellationToken ct)
    {
        // Resolve the release the interface lives in so we can seed the
        // recursive CTE with it — same shadowing rule as the rest of the
        // family.
        var seed = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == interfaceModuleId)
            .Select(m => new { m.ReleaseId, m.AppId })
            .FirstOrDefaultAsync(ct);
        if (seed is null) return new();
        if (!await ReleaseVisibleAsync(seed.ReleaseId, ct)) return new();

        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT
                so.id                    AS "SourceObjectId",
                so.name                  AS "SourceObjectName",
                sm.name                  AS "SourceModuleName"
            FROM oe_module_references mr
            JOIN oe_module_objects    so ON so.id = mr.source_object_id
            JOIN oe_modules           sm ON sm.id = mr.module_id
            JOIN winning              w  ON w.id  = mr.module_id
            WHERE mr.reference_kind     = 'implements_interface'
              AND mr.target_app_id      = {1}::uuid
              AND mr.target_object_kind = 'interface'
              AND mr.target_object_name = {2}::text
            ORDER BY sm.name, so.name
            """;

        return await _db.Database
            .SqlQueryRaw<InterfaceImplementerRow>(sql, seed.ReleaseId, seed.AppId, interfaceName)
            .ToListAsync(ct);
    }
}

/// <summary>One codeunit that implements an interface — see
/// <see cref="ReferenceQueryService.FindInterfaceImplementersAsync"/>.</summary>
public sealed record InterfaceImplementerRow(
    long SourceObjectId,
    string SourceObjectName,
    string SourceModuleName);
