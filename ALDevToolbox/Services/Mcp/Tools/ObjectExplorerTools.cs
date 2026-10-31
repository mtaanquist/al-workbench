using System.ComponentModel;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// MCP tools that wrap <see cref="ObjectExplorerService"/>. The tools
/// accept a release Label ("BC 28.1") or numeric id — natural-language
/// queries reach for the human form first, but agents that already have
/// an id can pass it through. Owner-by-name resolution for
/// <c>find_references</c> looks the target up in <c>oe_module_objects</c>
/// so the agent only has to supply the table/page/codeunit name.
///
/// <para>Every tool here funnels its release argument through
/// <see cref="ObjectExplorerService.ResolveReleaseAsync"/>, which is where the
/// project-visibility fence is applied: a release built under (or imported into) a Private project the
/// caller has no grant on answers "does not exist", the same as an id in
/// another org. The two tools that can skip that resolution by taking a
/// <c>symbolId</c> directly check the symbol's own release instead. See
/// <c>.design/teams-and-visibility.md</c>.</para>
/// </summary>
[McpServerToolType]
public sealed class ObjectExplorerTools
{
    private const int MaxResults = 200;

    // Per-tool row caps, sized so a full page of each tool's rows stays near
    // 25,000 characters: a response much past that is refused by assistant
    // clients (find_references returned 100,000 at 200 rows). The compare tools
    // keep MaxResults, as their rows are narrow (#1510). Each list also stops at
    // McpAnswerBudget's character budget, so the caps are upper bounds (#1524).
    private const int MaxObjectMatches = 100;
    private const int MaxProcedureMatches = 75;
    private const int MaxContentMatches = 60;
    private const int MaxReferenceRows = 50;
    private const int MaxProcedureCalls = 100;
    private const int MaxTranslationMatches = 50;
    private const int MaxModuleRows = 100;
    private const int MaxReleaseListRows = 100;
    private const int MaxOutlineSymbols = 150;

    private readonly ObjectExplorerService _explorer;
    private readonly ObjectSearchService _search;
    private readonly ReferenceQueryService _references;
    private readonly TranslationQueryService _translations;
    private readonly ReleaseComparisonService _comparison;

    public ObjectExplorerTools(ObjectExplorerService explorer, ObjectSearchService search, ReferenceQueryService references, TranslationQueryService translations, ReleaseComparisonService comparison)
    {
        _explorer = explorer;
        _search = search;
        _references = references;
        _translations = translations;
        _comparison = comparison;
    }

    /// <summary>Default row count for <c>list_releases</c>: enough for a version list, small enough to read in one response.</summary>
    private const int DefaultReleaseListLimit = 50;

    /// <summary>The <c>kind</c> values <c>list_releases</c> accepts; "solution" names the pipeline builds, stored as kind "project".</summary>
    private static readonly string[] ReleaseListKinds = ["first_party", "third_party", "cal", "solution"];

    [McpServerTool(Name = "list_releases", ReadOnly = true)]
    [Description("Lists the releases you can search with the other Object Explorer tools - the same list the Releases page shows. That is Microsoft's Business Central releases (kind 'first_party'), third-party apps ('third_party'), legacy C/AL TXT imports ('cal'), and the newest ready build of each solution's build pipeline ('solution'), which is where a customer's own extensions are: pass the customer or solution name as search to find them, and use the id, since builds of one solution on the same BC version share a label. Releases still importing or whose import failed are left out. Returns each release's id, label (e.g. 'BC 28.1'), kind, BC version, publisher, the release it was layered on, the solution and pipeline for a solution build, and IsPrerelease - true for a Microsoft preview build of an upcoming version imported from the insider channel, which is replaced once that version ships; prefer a non-preview release for anything that has to match what customers run. Ordered Microsoft and other imported releases first, newest BC version first, then the solution builds by solution name; returns the first 50 unless you raise limit, and Truncated and Note say what was left out, so pass search or kind rather than paging. For an older build of a solution, use list_solution_builds and pass its releaseId.")]
    public async Task<ReleaseListResult> ListReleasesAsync(
        [Description("Optional case-insensitive substring of the release label, publisher, BC version, solution name or pipeline name: a customer's name finds their solution's build, 'Continia' that vendor's apps, '28.5' one version. Null lists every release up to limit.")] string? search = null,
        [Description("Optional kind: 'first_party' (Microsoft), 'third_party', 'cal' or 'solution'. Null lists every kind.")] string? kind = null,
        [Description("How many to return. Default 50, at least 1, at most 100.")] int limit = DefaultReleaseListLimit,
        CancellationToken ct = default)
    {
        var wantedKind = kind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(wantedKind) && !ReleaseListKinds.Contains(wantedKind))
        {
            throw new McpException($"Unknown kind '{kind}'. Use one of: {string.Join(", ", ReleaseListKinds)}.");
        }
        var cap = Math.Clamp(limit, 1, MaxReleaseListRows);

        // Project builds are otherwise served through the Artifacts tools; the one
        // exception is the newest ready build per pipeline, which the Releases page
        // lists on its Solutions tab. Leaving it out here sent agents looking
        // for a customer's code on GitHub instead (see .design/artifacts.md).
        // browsableOnly: the Releases page's list. A failed or importing release
        // cannot be searched, and the empty symbol packages builds pull would grow
        // the list without bound (#1344).
        var releases = await _explorer.ListReleasesAsync(includeSoftDeleted: false, browsableOnly: true, ct: ct);
        var pipelineBuilds = await _explorer.ListLatestPipelineBuildReleasesAsync(ct);

        var matched = releases.Concat(pipelineBuilds)
            .Where(r => MatchesReleaseSearch(r, search))
            .Select(ReleaseSummary.From)
            .Where(r => string.IsNullOrEmpty(wantedKind) || r.Kind == wantedKind)
            .ToList();
        var shown = McpAnswerBudget.Fit(matched, cap);
        var truncated = matched.Count > shown;
        return new ReleaseListResult(
            matched.Take(shown).ToList(),
            truncated,
            truncated ? TruncationNote(matched, shown, budgetCut: shown < cap) : NoMatchNote(matched, search));
    }

    /// <summary>
    /// The next step when a search finds nothing. The name an agent was given is
    /// often a short name or a repository name, which list_solutions matches and
    /// this list does not.
    /// </summary>
    private static string? NoMatchNote(List<ReleaseSummary> matched, string? search) =>
        matched.Count == 0 && !string.IsNullOrWhiteSpace(search)
            ? $"No release matches '{search.Trim()}'. If that is a customer, call list_solutions with it as search "
              + "(it also matches short names and repository names), then list_solution_builds for the releaseId of its builds."
            : null;

    /// <summary>
    /// The Releases page's search: a substring of label, publisher, BC version,
    /// solution or pipeline name. Matched on the service row rather than the
    /// summary, because an older manual import still carries the customer name
    /// it was filed under, and the page finds it by that name too.
    /// </summary>
    private static bool MatchesReleaseSearch(ReleaseListItem r, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var q = search.Trim();
        return Has(r.Label, q) || Has(r.Publisher, q) || Has(r.BcVersion, q)
            || Has(r.ProjectName, q) || Has(r.PipelineName, q);

        static bool Has(string? value, string needle) =>
            !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names what a cut release list left out, by kind, so an agent can tell the solution builds were among them.</summary>
    private static string TruncationNote(List<ReleaseSummary> matched, int cap, bool budgetCut)
    {
        var leftOut = matched.Skip(cap)
            .GroupBy(r => r.Kind)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
        return $"Showing {cap} of {matched.Count} releases; left out: {string.Join(", ", leftOut)}. "
            + "Pass search (a customer, solution, publisher or version) or kind to narrow the list"
            // A higher limit would come back cut in the same place (#1524).
            + (budgetCut ? "." : ", or raise limit.");
    }

    [McpServerTool(Name = "compare_releases", ReadOnly = true)]
    [Description("Diffs two releases at the object level, matched by (kind, object id). Returns each object's Status — 'added' (in the second only), 'removed' (in the first only), 'modified' (source differs), or 'unchanged' — plus the LeftFileId / RightFileId for a side-by-side source diff; Truncated and Note say whether anything was left out. Capped at 200 rows ordered by kind, then id — two Business Central versions differ in far more objects than that, so pass kind and/or namePattern to narrow it. Built for the legacy C/AL Base-vs-Customer comparison: pass the bare-Microsoft 'Base' release first and the customer export second.")]
    public async Task<ObjectCompareResult> CompareReleasesAsync(
        [Description("First (left / Base) release Label or numeric id.")] string baseReleaseLabelOrId,
        [Description("Second (right / Customer) release Label or numeric id.")] string otherReleaseLabelOrId,
        [Description("When true (default), omit unchanged objects and return only added / removed / modified.")] bool changesOnly = true,
        [Description("Optional AL kind to compare only ('table', 'page', 'codeunit', ...). Null compares every kind.")] string? kind = null,
        [Description("Optional case-insensitive substring of the object name. Null compares every object.")] string? namePattern = null,
        CancellationToken ct = default)
    {
        var leftId = await _explorer.ResolveReleaseAsync(baseReleaseLabelOrId, ct);
        var rightId = await _explorer.ResolveReleaseAsync(otherReleaseLabelOrId, ct);
        var set = await _comparison.CompareReleaseObjectsAsync(
            leftId, rightId, MaxResults, includeUnchanged: !changesOnly, kind, namePattern, ct);
        return ObjectCompareResult.From(set, MaxResults);
    }

    [McpServerTool(Name = "compare_release_files", ReadOnly = true)]
    [Description("Diffs two releases at the FILE level, pairing files by path within each app. This is the companion to compare_releases: that one only sees changes inside AL objects, so use this one to catch everything outside them — permission sets, .xlf translation files, .json manifests, report layouts, and whole files added or removed. Rows carry one entry per changed file with the owning AppId + ModuleName, the file Path, a Status of 'added' (in the second release only), 'removed' (in the first only) or 'modified', and the LeftFileId / RightFileId for a side-by-side source diff; Truncated and Note say whether anything was left out. Unchanged files are never returned. Capped at 200 rows, and the diff itself scans at most 5000 changed files — a base-application comparison runs well past both, so pass pathPattern to narrow it (e.g. '.xlf', 'PermissionSet', '/Layouts/').")]
    public async Task<ReleaseFileCompareResult> CompareReleaseFilesAsync(
        [Description("First (left / older) release Label or numeric id.")] string baseReleaseLabelOrId,
        [Description("Second (right / newer) release Label or numeric id.")] string otherReleaseLabelOrId,
        [Description("Optional case-insensitive substring of the file path — narrows a large diff to one file kind or folder. Null returns every changed file up to the cap.")] string? pathPattern = null,
        CancellationToken ct = default)
    {
        var leftId = await _explorer.ResolveReleaseAsync(baseReleaseLabelOrId, ct);
        var rightId = await _explorer.ResolveReleaseAsync(otherReleaseLabelOrId, ct);

        // Explicit scan cap: the diff is materialised in memory, so an uncapped
        // DVD-to-DVD compare would buffer tens of thousands of rows to hand back
        // 200. The path filter is applied inside the scan, so it narrows what
        // the cap counts. The service returns one row past the cap when it cut
        // the set - see issues #685 and #1510.
        var scanCap = ReleaseComparisonService.MaxCompareFileRows;
        var rows = await _comparison.CompareReleaseFilesFlatAsync(leftId, rightId, scanCap, pathPattern, ct);
        var scanTruncated = rows.Count > scanCap;
        if (scanTruncated) rows = rows.Take(scanCap).ToList();

        var matched = rows;
        var page = matched.Take(MaxResults).ToList();
        var notes = new List<string>();
        var filtered = !string.IsNullOrWhiteSpace(pathPattern);
        if (matched.Count > MaxResults)
            notes.Add(scanTruncated
                ? $"Showing the first {MaxResults} of at least {scanCap} {(filtered ? "matching" : "changed")} files."
                : $"Showing the first {MaxResults} of {matched.Count} {(filtered ? "matching" : "changed")} files.");
        if (scanTruncated)
            notes.Add($"The diff stopped after the first {scanCap} {(filtered ? "matching" : "changed")} files (ordered by app, then path), so later files were never examined.");
        if (notes.Count > 0)
            notes.Add("Pass pathPattern, or compare two individual apps, to narrow the comparison.");

        return new ReleaseFileCompareResult(
            page,
            Truncated: notes.Count > 0,
            Note: notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    [McpServerTool(Name = "search_objects", ReadOnly = true)]
    [Description("Searches a BC release for AL objects (tables, pages, codeunits, reports, etc.) by name or id. Tokens in the pattern are split on whitespace; every token must appear as a case-insensitive substring of the object name (BC \"Tell Me\" style), so 'sal set' finds 'Sales & Receivables Setup'. Wrap a phrase in double quotes for a literal match (\"sales header\"), and prefix a token with '-' to exclude it (setup -temp). Returns the owning module name + source file pointer for each hit, with word-boundary matches ranked first. Capped at 100 rows; Truncated and Note say whether more were left out, so add a token or a kind to narrow it.")]
    public async Task<CappedRows<ReleaseObjectMatch>> SearchObjectsAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Whitespace-separated tokens; every token must appear (case-insensitive substring) in the object name. Use \"quotes\" for a literal phrase and a -prefix to exclude a token. A single bare numeric token still matches by object id. A leading kind prefix scopes the search to one kind: 't:item' (or 'table:item') finds tables named 'item'; bare 't:' returns every table. Shortcuts: t/p/c/r/q/x/e/i and te/pe/re/ee/ps/pse/ca/ms/pr, or the full kind name.")] string namePattern,
        [Description("Optional AL kind filter ('table', 'page', 'codeunit', 'report', 'query', 'enum', 'interface', 'tableextension', 'pageextension'). Legacy C/AL releases also carry 'menusuite' (and 'form'/'dataport' from pre-2013 exports). Pass several comma-separated to match any of them ('table,page'). Null returns every kind.")] string? kind = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var kinds = kind?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = await _search.SearchObjectsInReleaseAsync(
            releaseId,
            new ObjectListFilter(Kinds: kinds, Search: namePattern),
            MaxObjectMatches + 1,
            ct: ct);
        // Ranking only applies when there is something to rank by: a bare kind
        // prefix or an empty pattern comes back in kind, then id order.
        var ranked = !string.IsNullOrWhiteSpace(ObjectSearchRanking.ExtractKindPrefix(namePattern).Remainder);
        return CappedRows<ReleaseObjectMatch>.From(rows, MaxObjectMatches, "objects",
            ranked ? "best matches first" : "ordered by kind, then id",
            "Add a token to namePattern or pass kind to narrow the search.");
    }

    [McpServerTool(Name = "search_procedures", ReadOnly = true)]
    [Description("Searches a BC release for procedures (global, local, internal and protected methods, triggers, event publishers and event subscribers) by name. Returns the owning object + module for each hit. Capped at 75 rows; Truncated and Note say whether more were left out, so lengthen namePattern or pass moduleId to narrow it.")]
    public async Task<CappedRows<ReleaseProcedureMatch>> SearchProceduresAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Substring of the procedure name.")] string namePattern,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var rows = await _search.SearchProceduresInReleaseAsync(releaseId, namePattern, moduleId, MaxProcedureMatches + 1, ct: ct);
        return CappedRows<ReleaseProcedureMatch>.From(rows, MaxProcedureMatches, "procedures", "ordered by module",
            "Lengthen namePattern or pass moduleId to narrow the search.");
    }

    [McpServerTool(Name = "search_content", ReadOnly = true)]
    [Description("Searches AL source content across a BC release for a free-text query. Returns the line containing the hit, cut at 200 characters, at most 3 lines per file and 60 lines in all; Truncated and Note say whether more were left out, so make the query more specific or pass moduleId to narrow it. The query must be at least 3 characters (a shorter term can't be trigram-indexed and returns no results).")]
    public async Task<CappedRows<ReleaseContentMatch>> SearchContentAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Free-text search query, at least 3 characters. Matched literally (case-insensitive substring) against .al source bodies.")] string query,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        // The service caps files, not lines: one past the cap in files is enough to
        // know whether more lines matched than are returned.
        var rows = await _search.SearchContentInReleaseAsync(releaseId, query, moduleId, MaxContentMatches + 1, 3, ct);
        // The service already cuts each snippet at 200 characters.
        return CappedRows<ReleaseContentMatch>.From(rows, MaxContentMatches, "lines", "ordered by module, then file",
            "Make the query more specific or pass moduleId to narrow the search.");
    }

    [McpServerTool(Name = "find_references", ReadOnly = true)]
    [Description("Finds the places in a BC release that reference a specific table, page, or codeunit — optionally narrowed to a specific field or procedure on it. Resolves the target by name. For a field, each reference's Usage says whether it reads the field ('read') or sets it ('assign' for := and +=, 'validate', 'modify_all', 'evaluate', 'clear'), and IsTemporary whether it goes through a temporary record; pass 'Sales Line' + field 'No.' + usage 'set' to answer 'which procedures change the No. field on Sales Line?', and includeTemporary false to leave out temporary records. Capped at 50 rows ordered by module, then object; Truncated and Note say whether more were left out — a popular base table runs well past that, so narrow with targetField or targetProcedure.")]
    public async Task<ReferenceSearchResult> FindReferencesAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Name of the target object — table, page, codeunit, etc. Resolved case-insensitively against oe_module_objects.")] string targetObject,
        [Description("AL kind of the target object. Defaults to 'table'.")] string targetKind = "table",
        [Description("Optional field name on the target table — narrows the search to references that touch this field.")] string? targetField = null,
        [Description("Optional procedure name on the target object — narrows the search to references that call this procedure.")] string? targetProcedure = null,
        [Description("With targetField only: 'all' (default) returns every reference, 'set' only the places that change the field (:=, Validate, ModifyAll, Evaluate, Clear), 'read' only the places that read it.")] string usage = "all",
        [Description("With targetField only: false leaves out references through a temporary record (a variable declared temporary, or Rec on a page or table whose record is temporary). Default true.")] bool includeTemporary = true,
        CancellationToken ct = default)
    {
        var wantedUsage = usage.Trim().ToLowerInvariant();
        if (wantedUsage is not ("all" or "set" or "read"))
        {
            throw new McpException($"Unknown usage '{usage}'. Use 'all', 'set' or 'read'.");
        }
        var filtered = wantedUsage != "all" || !includeTemporary;
        if (filtered && string.IsNullOrWhiteSpace(targetField))
        {
            throw new McpException("usage and includeTemporary apply to a field: pass targetField as well.");
        }

        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);

        var kind = targetKind.Trim().ToLowerInvariant();
        // Resolve the target across the release chain (a customer Release's
        // parent BC base, etc.) so you can target a base object — Customer,
        // Sales Header — from a child Release where it isn't redefined.
        var owner = await _explorer.ResolveChainObjectAsync(
            releaseId, targetObject.Trim(), kind, objectId: null, ct);
        if (owner is null)
        {
            throw new McpException($"Could not find a {kind} named '{targetObject}' in release {releaseLabelOrId} or its parent releases. Try search_objects to discover the exact name.");
        }

        string? memberName = null;
        string? memberKind = null;
        if (!string.IsNullOrWhiteSpace(targetField))
        {
            memberName = targetField.Trim();
            memberKind = "field";
        }
        else if (!string.IsNullOrWhiteSpace(targetProcedure))
        {
            memberName = targetProcedure.Trim();
            memberKind = "procedure";
        }

        var query = new FindReferencesQuery(
            TargetAppId: owner.AppId,
            TargetObjectKind: owner.Kind,
            TargetObjectId: owner.ObjectId,
            TargetObjectName: owner.Name,
            TargetMemberName: memberName,
            TargetMemberKind: memberKind);

        // Member-scoped queries flow through the symbol-aware variant so
        // additional buckets — sibling declarations across the chain and
        // the "implementation" set for interface methods — surface in
        // the response. Object-only queries stay on the lighter path.
        // The cap goes into the query, so only the rows returned are fetched and
        // given a code snippet (#1247).
        // A usage filter is applied after the query (usage is read from the
        // source line), so it fetches the full reference set and filters that:
        // capping first would drop the writes that sit past the first 50 rows.
        var take = filtered ? ReferenceQueryService.MaxReferenceMatches : MaxReferenceRows;
        var matches = memberName is null
            ? await _references.FindReferencesAsync(releaseId, query, take, ct)
            : await _references.FindReferencesForSymbolAsync(releaseId, query, take, ct);
        if (filtered)
        {
            var checkedAll = UsageFilterCheckedAll(matches);
            matches = matches
                .Where(m => wantedUsage switch
                {
                    "set" => ReferenceUsage.IsWrite(m.Usage),
                    "read" => m.Usage == ReferenceUsage.Read,
                    _ => true,
                })
                .Where(m => includeTemporary || m.IsTemporary != true)
                .ToList();
            var result = ReferenceSearchResult.From(matches, MaxReferenceRows,
                includeTemporary ? "Pass includeTemporary false to leave out temporary records."
                : wantedUsage == "all" ? "Pass usage 'set' to keep only the places that change the field."
                : "There is nothing narrower to filter on; read the rows shown first.");
            return checkedAll
                ? result
                : result with
                {
                    Truncated = true,
                    Note = $"Only the first {ReferenceQueryService.MaxReferenceMatches} references to the field were checked; some that match may be missing. {result.Note}".TrimEnd(),
                };
        }
        return ReferenceSearchResult.From(matches, MaxReferenceRows,
            "Pass targetField or targetProcedure to narrow the search.");
    }

    /// <summary>
    /// Whether a usage-filtered find_references saw every reference. The
    /// reference query fetches one row past the cap, so more than the cap means
    /// it was cut. Only that bucket is capped: the declarations and interface
    /// implementations a member search adds before and after it are not, so
    /// they are left out of the count, or a complete answer reads as cut.
    /// </summary>
    internal static bool UsageFilterCheckedAll(IEnumerable<ReferenceMatch> matches) =>
        matches.Count(m => m.Category is not ("declaration" or "implementation"))
            <= ReferenceQueryService.MaxReferenceMatches;

    [McpServerTool(Name = "find_system_references", ReadOnly = true)]
    [Description("Finds the calls to a built-in / system method on a specific object in a BC release — the calls that find_references deliberately omits. On a table: Insert, Modify, ModifyAll, Delete, DeleteAll, Get, Find/FindFirst/FindLast/FindSet, SetRange, Validate and so on; on a page: Run, RunModal, SetRecord; on a report: Run, RunModal, SaveAsPdf, SaveAs; on a codeunit: Run. Both the instance form (CustomerList.RunModal()) and the static form (Page.RunModal(Page::\"Customer List\")) are found. Use this to answer 'where is this table inserted/modified/deleted?' or 'where is this page opened?'. Optionally narrow to some methods. Capped at 50 rows ordered by module, then object; Truncated and Note say whether more were left out.")]
    public async Task<ReferenceSearchResult> FindSystemReferencesAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Name of the target object — usually a table. Resolved case-insensitively against oe_module_objects.")] string targetObject,
        [Description("AL kind of the target object. Defaults to 'table'.")] string targetKind = "table",
        [Description("Optional method name to narrow to, or several separated by commas, e.g. 'Insert', 'Modify,ModifyAll', 'FindFirst,FindLast,FindSet', 'Run,RunModal'. Matched without regard to case.")] string? systemMethod = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);

        var kind = targetKind.Trim().ToLowerInvariant();
        // Chain-aware (same as find_references) so a base object can be the
        // target from a child Release.
        var owner = await _explorer.ResolveChainObjectAsync(
            releaseId, targetObject.Trim(), kind, objectId: null, ct);
        if (owner is null)
        {
            throw new McpException($"Could not find a {kind} named '{targetObject}' in release {releaseLabelOrId} or its parent releases. Try search_objects to discover the exact name.");
        }

        var query = new FindSystemReferencesQuery(
            TargetAppId: owner.AppId,
            TargetObjectKind: owner.Kind,
            TargetObjectId: owner.ObjectId,
            TargetObjectName: owner.Name,
            SystemMethodNames: string.IsNullOrWhiteSpace(systemMethod)
                ? null
                : systemMethod.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        var matches = await _references.FindSystemReferencesAsync(releaseId, query, MaxReferenceRows, ct);
        return ReferenceSearchResult.From(matches, MaxReferenceRows,
            "Pass systemMethod to narrow the search to some methods.");
    }

    [McpServerTool(Name = "get_object_outline", ReadOnly = true)]
    [Description("Returns the outline of one AL object (table, page, codeunit, etc.) in a BC release — its declared procedures, triggers, event publishers, and fields with their line numbers and symbol ids. Use this to discover what an object exposes before calling get_procedure_source or list_procedure_calls. The returned symbol ids disambiguate procedure name collisions (page actions and table fields each carry an OnAction / OnValidate trigger with the same name). Each row also carries 'doc' — the declaration's XML doc-comment summary — when the module was imported with source and the declaration was documented; it is null otherwise, which is the common case. The outline lists at most 150 members, in line order; Truncated and Note say whether more were left out, and totalSymbols how many members matched the filters, so pass memberKind or namePattern to narrow a large object such as a posting codeunit or Sales Line.")]
    public async Task<ObjectOutlineResult> GetObjectOutlineAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Name of the target object — resolved case-insensitively against oe_module_objects.")] string objectName,
        [Description("AL kind of the target object. Defaults to 'codeunit'.")] string objectKind = "codeunit",
        [Description("Optional comma-separated member kinds to keep, e.g. 'procedure', 'trigger', 'field' or 'procedure,trigger'. 'procedure' also keeps local and internal procedures. Null keeps every kind.")] string? memberKind = null,
        [Description("Optional case-insensitive substring of the member name, e.g. 'Post' or 'OnValidate'. Null keeps every member.")] string? namePattern = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var outline = await _explorer.GetObjectOutlineAsync(releaseId, objectKind, objectName, ct);
        if (outline is null)
        {
            throw new McpException($"Could not find a {objectKind.Trim().ToLowerInvariant()} named '{objectName}' in release {releaseLabelOrId}. Try search_objects to discover the exact name.");
        }
        return ObjectOutlineResult.From(outline, memberKind, namePattern, MaxOutlineSymbols);
    }

    [McpServerTool(Name = "get_procedure_source", ReadOnly = true)]
    [Description("Returns the AL source text of one procedure / trigger body on an object — declaration through matching end. Capped at 200 lines with a truncation marker when the body is longer (call list_procedure_calls or narrow the question in that case). Accept either (objectName, objectKind, procedureName) for the unambiguous case, or symbolId from a prior get_object_outline call when the name is ambiguous (page-action OnAction triggers, table-field OnValidate triggers).")]
    public async Task<ProcedureSource> GetProcedureSourceAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Symbol id from get_object_outline. Preferred when set — disambiguates OnAction / OnValidate collisions and avoids a name resolution round-trip.")] long? symbolId = null,
        [Description("Name of the owning object. Required when symbolId is null.")] string? objectName = null,
        [Description("AL kind of the owning object. Required when symbolId is null.")] string? objectKind = null,
        [Description("Name of the procedure / trigger. Required when symbolId is null.")] string? procedureName = null,
        CancellationToken ct = default)
    {
        var resolvedSymbolId = symbolId ?? await _explorer.ResolveProcedureSymbolIdAsync(releaseLabelOrId, objectName, objectKind, procedureName, ct);
        await _explorer.EnsureSymbolVisibleAsync(resolvedSymbolId, ct);
        var source = await _explorer.GetProcedureSourceAsync(resolvedSymbolId, MaxProcedureSourceLines, ct);
        if (source is null)
        {
            throw new McpException($"Symbol id {resolvedSymbolId} either doesn't exist or doesn't have a source file attached. Call get_object_outline to see the current ids for an object.");
        }
        return source;
    }

    [McpServerTool(Name = "list_procedure_calls", ReadOnly = true)]
    [Description("Returns the outgoing references (method calls, field accesses, type references) emitted from inside one procedure / trigger body. Use this to trace what a procedure does without reading the full source: each row carries the target object + member name + line so the agent can follow the call chain. Same disambiguation rules as get_procedure_source — pass symbolId from get_object_outline when the procedure name is shared (OnAction / OnValidate). Capped at 100 calls in line order; Truncated and Note say whether more were left out, and get_procedure_source reads the body itself.")]
    public async Task<CappedRows<ProcedureCall>> ListProcedureCallsAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Symbol id from get_object_outline. Preferred when set.")] long? symbolId = null,
        [Description("Name of the owning object. Required when symbolId is null.")] string? objectName = null,
        [Description("AL kind of the owning object. Required when symbolId is null.")] string? objectKind = null,
        [Description("Name of the procedure / trigger. Required when symbolId is null.")] string? procedureName = null,
        CancellationToken ct = default)
    {
        var resolvedSymbolId = symbolId ?? await _explorer.ResolveProcedureSymbolIdAsync(releaseLabelOrId, objectName, objectKind, procedureName, ct);
        await _explorer.EnsureSymbolVisibleAsync(resolvedSymbolId, ct);
        var calls = await _explorer.ListProcedureCallsAsync(resolvedSymbolId, MaxProcedureCalls + 1, ct);
        if (calls is null)
        {
            throw new McpException($"Symbol id {resolvedSymbolId} doesn't exist. Call get_object_outline to see the current ids for an object.");
        }
        return CappedRows<ProcedureCall>.From(calls, MaxProcedureCalls, "calls", "in line order",
            "Call get_procedure_source to read the rest of the body.");
    }

    [McpServerTool(Name = "list_translation_languages", ReadOnly = true)]
    [Description("Lists every target language with at least one translation row in a BC release, plus the trans-unit count per language. Cheap discovery before calling search_translations — e.g. an agent helping a Danish customer would call this first to confirm 'da-DK' is loaded for the release in question.")]
    public async Task<IReadOnlyList<TranslationLanguageSummary>> ListTranslationLanguagesAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        return await _translations.ListTranslationLanguagesAsync(releaseId, ct);
    }

    [McpServerTool(Name = "search_translations", ReadOnly = true)]
    [Description("Searches translated captions / labels / error messages in a BC release for a substring of the target text. Use this when a customer reports an issue in their native language and you need to find the AL field, caption, or label that produced the text. Defaults to caption + label hits (the user's stated priority — captions for fields, labels for error messages); pass kinds='any' to include tooltips and other property kinds. Each hit carries the owning module + object + sub-element + property names plus a SymbolId when the lookup hint resolved to a known symbol — clients can navigate straight to source for those rows. Capped at 50 rows; Truncated and Note say whether more were left out, so lengthen the query or pass language or moduleNamePattern to narrow it.")]
    public async Task<CappedRows<TranslationMatch>> SearchTranslationsAsync(
        [Description("Release Label ('BC 28.1') or numeric id.")] string releaseLabelOrId,
        [Description("Substring of the target (translated) text, case-insensitive. E.g. 'Aktivér montageordrer' for the Danish caption.")] string query,
        [Description("Optional BCP-47 language code (e.g. 'da-DK', 'de-DE'). Null searches every uploaded language. 'da' (no region) matches every variant starting with that prefix.")] string? language = null,
        [Description("Comma-separated kind filter. Default 'caption,label' surfaces field captions and error-message labels first. Pass 'any' to include tooltips / instructional text / other. Valid kinds: caption, tooltip, label, instructional, option, other.")] string kinds = "caption,label",
        [Description("Optional substring of the owning module name (e.g. 'Base' to scope to Base Application).")] string? moduleNamePattern = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpException("query is required: pass the substring of the translated text to search for.");
        }
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in kinds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(raw);
        }
        var rows = await _translations.SearchTranslationsInReleaseAsync(
            releaseId, query, language, set, moduleNamePattern, MaxTranslationMatches + 1, ct);
        return CappedRows<TranslationMatch>.From(rows, MaxTranslationMatches, "translations", "ordered by module, then object",
            "Lengthen the query or pass language or moduleNamePattern to narrow the search.");
    }

    private const int MaxProcedureSourceLines = 200;

    [McpServerTool(Name = "list_release_modules", ReadOnly = true)]
    [Description("Lists the modules (extensions) in a BC release with their identity, flags, and whether a SymbolReference.json was stored for each at import time. Use this to discover the modules you can pass to search_objects, find_references, get_object_outline, and download_symbol_reference. Sorted alphabetically by name and capped at 100 rows; Truncated and Note say whether more were left out — a full Business Central release has more modules than that, so pass namePattern (e.g. 'System Application') to find one late in the alphabet.")]
    public async Task<ReleaseModuleListResult> ListReleaseModulesAsync(
        [Description("Release Label ('BC 28.1') or numeric id.")] string releaseLabelOrId,
        [Description("Optional case-insensitive substring of the module name. Null lists every module.")] string? namePattern = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        // One past the cap, so a cut list says so instead of reading as complete (#1344).
        var rows = await _explorer.ListReleaseModulesForMcpAsync(releaseId, MaxModuleRows + 1, namePattern, ct);
        var truncated = rows.Count > MaxModuleRows;
        return new ReleaseModuleListResult(
            rows.Take(MaxModuleRows).Select(m => new ReleaseModuleSummary(
                m.Id, m.Name, m.Publisher, m.Version, m.AppId,
                m.IsTest, m.IsInternal, m.IsLanguagePack, m.DependencyCount, m.HasStoredSymbolReference)).ToList(),
            truncated,
            truncated
                ? $"Showing the first {MaxModuleRows} modules (ordered by name); more were left out. Pass namePattern to narrow the list."
                : null);
    }

    [McpServerTool(Name = "download_symbol_reference", ReadOnly = true)]
    [Description("Returns a download URL for one module's raw SymbolReference.json, for debugging resolver errors. Only available when the release was imported with the 'store symbol reference' option enabled. The JSON is NOT returned inline — a base-app symbol file runs to tens of MB and would blow up the MCP response serialiser; share the returned downloadPath with the user (appended to the app's base URL) so they can fetch it via the streaming download endpoint.")]
    public async Task<SymbolReferenceDownloadLink> DownloadSymbolReferenceAsync(
        [Description("Release Label ('BC 28.1') or numeric id.")] string releaseLabelOrId,
        [Description("Module / extension name, e.g. 'Base Application' (case-insensitive).")] string moduleName,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var match = await _explorer.GetModuleSymbolReferenceAsync(releaseId, moduleName, ct);

        if (match is null)
        {
            throw new McpException(
                $"Module '{moduleName}' was not found in release '{releaseLabelOrId}'. Check the name, or use search_objects to find the owning module.");
        }
        if (match.ContentHash is null)
        {
            // Help the caller: list which modules in this release DO have it stored.
            var stored = await _explorer.ListModulesWithStoredSymbolReferenceAsync(releaseId, ct);
            var available = stored.Count == 0
                ? "No modules in this release have a stored SymbolReference.json — re-import the release with the 'store symbol reference' option enabled."
                : "Modules with a stored SymbolReference.json: " + string.Join(", ", stored);
            throw new McpException(
                $"Module '{match.Name}' has no stored SymbolReference.json. {available}");
        }

        var downloadPath = $"/api/object-explorer/release/{releaseId}/modules/{match.Id}/symbol-reference";
        return new SymbolReferenceDownloadLink(
            ModuleName: match.Name,
            Version: match.Version,
            ContentHash: match.ContentHash,
            ContentLength: match.ContentLength ?? 0,
            DownloadPath: downloadPath);
    }

}

/// <summary>
/// The <c>compare_release_files</c> response: the page of changed-file rows plus
/// an honest statement of what was left out. Two separate cuts can apply — the
/// 200-row response cap and the diff's own scan cap — and an agent that reads a
/// silently short list draws the wrong conclusion ("nothing else changed"), so
/// <c>Note</c> spells out which one bit and how to narrow the question.
/// </summary>
public sealed record ReleaseFileCompareResult(
    IReadOnlyList<ReleaseCompareFileRow> Rows,
    bool Truncated,
    string? Note);

/// <summary>
/// What <c>find_references</c> and <c>find_system_references</c> return: at most
/// the tool's cap of rows, with <see cref="Truncated"/> and <see cref="Note"/>
/// saying when more matched, the same shape as <see cref="ReleaseFileCompareResult"/>.
/// These tools used to drop everything past the cap without a word (#1247).
/// </summary>
public sealed record ReferenceSearchResult(
    IReadOnlyList<ReferenceMatch> Rows,
    bool Truncated,
    string? Note)
{
    /// <summary>Cuts <paramref name="matches"/> (fetched one past the cap) to <paramref name="cap"/> and says so.</summary>
    internal static ReferenceSearchResult From(IReadOnlyList<ReferenceMatch> matches, int cap, string narrowHint)
    {
        var capped = CappedRows<ReferenceMatch>.From(matches, cap, "references", "ordered by module, then object", narrowHint);
        return new(capped.Rows, capped.Truncated, capped.Note);
    }
}

/// <summary>
/// The object-level diff the <c>compare_releases</c> and
/// <c>compare_solution_builds</c> MCP tools return: at most the tool's cap of
/// rows, with <see cref="Truncated"/> and <see cref="Note"/> saying when more
/// matched, the same shape as <see cref="ReleaseFileCompareResult"/> (#1228).
/// </summary>
public sealed record ObjectCompareResult(
    IReadOnlyList<ObjectCompareRow> Rows,
    bool Truncated,
    string? Note)
{
    internal static ObjectCompareResult From(ObjectCompareSet set, int cap) =>
        new(set.Rows,
            set.Truncated,
            set.Truncated
                ? $"Showing the first {cap} objects (ordered by kind, then id); more were left out. Pass kind or namePattern to narrow the comparison."
                : null);
}

/// <summary>
/// What <c>get_object_outline</c> returns: the outline with its members narrowed
/// by the caller's filters and cut at the tool's cap. A posting codeunit or Sales
/// Line carries several hundred members, which ran past what an assistant can
/// read in one response (#1510). <see cref="TotalSymbols"/> counts the members
/// that matched before the cut.
/// </summary>
public sealed record ObjectOutlineResult(
    ObjectOutline Outline,
    int TotalSymbols,
    bool Truncated,
    string? Note)
{
    internal static ObjectOutlineResult From(ObjectOutline outline, string? memberKind, string? namePattern, int cap)
    {
        // "local procedure" as an agent writes it, local_procedure as the outline stores it.
        var kinds = memberKind?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.Replace(' ', '_')).ToArray() ?? [];
        var name = namePattern?.Trim();
        var matched = outline.Symbols
            .Where(sym => kinds.Length == 0 || kinds.Any(k => sym.Kind.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Where(sym => string.IsNullOrEmpty(name) || sym.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var shown = McpAnswerBudget.Fit(matched, cap);
        var truncated = shown < matched.Count;
        return new ObjectOutlineResult(
            outline with { Symbols = matched.Take(shown).ToList() },
            matched.Count,
            truncated,
            truncated
                ? $"Showing the first {shown} of {matched.Count} members (in line order); more were left out. Pass memberKind or namePattern to narrow the outline."
                : null);
    }
}

/// <summary>
/// Download link + identity for one module's stored <c>SymbolReference.json</c>,
/// returned by the <c>download_symbol_reference</c> MCP tool. The agent reports
/// <c>DownloadPath</c> to the user, who appends it to the app's base URL and
/// fetches it via the streaming download endpoint — the bytes never travel
/// through the MCP response (a base-app symbol file would OOM the serialiser).
/// </summary>
public sealed record SymbolReferenceDownloadLink(
    string ModuleName,
    string Version,
    string ContentHash,
    int ContentLength,
    string DownloadPath);

/// <summary>
/// What <c>list_releases</c> returns: at most the requested number of releases,
/// with <see cref="Truncated"/> and <see cref="Note"/> saying what was left out.
/// The list used to come back whole, and at a few hundred Microsoft releases it
/// ran past what an agent can read in one response.
/// </summary>
public sealed record ReleaseListResult(
    IReadOnlyList<ReleaseSummary> Rows,
    bool Truncated,
    string? Note);

/// <summary>
/// One row of <c>list_releases</c>: the fields an agent picks a release by. The
/// web list's bookkeeping (status, file counts, deletion) stays out, since every
/// row listed is ready and live. A pipeline build reports kind "solution" and
/// names its solution and pipeline.
/// </summary>
public sealed record ReleaseSummary(
    int Id,
    string Label,
    string Kind,
    string? BcVersion,
    string? Publisher,
    string? ParentLabel,
    string? SolutionName,
    string? PipelineName,
    DateTime ImportedAt,
    bool IsPrerelease)
{
    internal static ReleaseSummary From(ReleaseListItem r) => new(
        r.Id, r.Label,
        r.Kind == "project" ? "solution" : r.Kind,
        r.BcVersion, r.Publisher, r.ParentLabel,
        SolutionName: r.Kind == "project" ? r.ProjectName : null,
        PipelineName: r.PipelineName,
        r.ImportedAt, r.IsPrerelease);
}

/// <summary>
/// What <c>list_release_modules</c> returns: at most the tool's cap of modules,
/// with <see cref="Truncated"/> and <see cref="Note"/> saying when more were left
/// out, the same shape as <see cref="ReferenceSearchResult"/>. The list used to
/// stop at the cap without a word, so modules late in the alphabet looked absent (#1344).
/// </summary>
public sealed record ReleaseModuleListResult(
    IReadOnlyList<ReleaseModuleSummary> Rows,
    bool Truncated,
    string? Note);

/// <summary>
/// One row returned by the <c>list_release_modules</c> MCP tool — the per-release
/// module surface agents need before calling the other Object Explorer tools.
/// <c>HasStoredSymbolReference</c> is true when the release was imported with
/// the "store symbol reference" option on, so the JSON is downloadable.
/// </summary>
public sealed record ReleaseModuleSummary(
    long Id,
    string Name,
    string Publisher,
    string Version,
    Guid AppId,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack,
    int DependencyCount,
    bool HasStoredSymbolReference);
