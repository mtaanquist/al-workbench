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

    [McpServerTool(Name = "list_releases", ReadOnly = true)]
    [Description("Lists every BC release (the version snapshots from which Object Explorer was populated), including Microsoft (kind 'first_party'), third-party ('third_party'), and legacy C/AL TXT imports ('cal'). Returns each release's id, Label (e.g. 'BC 28.1'), kind, BC version, status, and IsPrerelease — true for a Microsoft preview build of an upcoming version imported from the insider channel, which is replaced once that version ships; prefer a non-preview release for anything that has to match what customers run.")]
    public async Task<IReadOnlyList<ReleaseListItem>> ListReleasesAsync(CancellationToken ct = default) =>
        // Project builds are excluded — they're served through the Artifacts surface
        // (list_pipelines / list_pipeline_builds / get_solution_build), not the general
        // Object Explorer release list. See .design/artifacts.md.
        await _explorer.ListReleasesAsync(includeSoftDeleted: false, ct: ct);

    [McpServerTool(Name = "compare_releases", ReadOnly = true)]
    [Description("Diffs two releases at the object level, matched by (kind, object id). Returns each object's Status — 'added' (in the second only), 'removed' (in the first only), 'modified' (source differs), or 'unchanged' — plus the LeftFileId / RightFileId for a side-by-side source diff. Built for the legacy C/AL Base-vs-Customer comparison: pass the bare-Microsoft 'Base' release first and the customer export second.")]
    public async Task<IReadOnlyList<ObjectCompareRow>> CompareReleasesAsync(
        [Description("First (left / Base) release Label or numeric id.")] string baseReleaseLabelOrId,
        [Description("Second (right / Customer) release Label or numeric id.")] string otherReleaseLabelOrId,
        [Description("When true (default), omit unchanged objects and return only added / removed / modified.")] bool changesOnly = true,
        CancellationToken ct = default)
    {
        var leftId = await _explorer.ResolveReleaseAsync(baseReleaseLabelOrId, ct);
        var rightId = await _explorer.ResolveReleaseAsync(otherReleaseLabelOrId, ct);
        var rows = await _comparison.CompareReleaseObjectsAsync(leftId, rightId, ct);
        return changesOnly ? rows.Where(r => r.Status != "unchanged").ToList() : rows;
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

        // Explicit scan cap: the diff is materialised in memory before the path
        // filter runs, so an uncapped DVD-to-DVD compare would buffer tens of
        // thousands of rows to hand back 200. The service returns one row past
        // the cap when it cut the set — see issue #685.
        var scanCap = ReleaseComparisonService.MaxCompareFileRows;
        var rows = await _comparison.CompareReleaseFilesFlatAsync(leftId, rightId, scanCap, ct);
        var scanTruncated = rows.Count > scanCap;
        if (scanTruncated) rows = rows.Take(scanCap).ToList();

        IEnumerable<ReleaseCompareFileRow> filtered = rows;
        if (!string.IsNullOrWhiteSpace(pathPattern))
        {
            var needle = pathPattern.Trim();
            filtered = filtered.Where(r => r.Path.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var matched = filtered.ToList();
        var page = matched.Take(MaxResults).ToList();
        var notes = new List<string>();
        if (matched.Count > MaxResults)
            notes.Add($"Showing the first {MaxResults} of {matched.Count} matching files.");
        if (scanTruncated)
            notes.Add($"The diff stopped after the first {scanCap} changed files (ordered by app, then path), so later files were never examined.");
        if (notes.Count > 0)
            notes.Add("Pass pathPattern, or compare two individual apps, to narrow the comparison.");

        return new ReleaseFileCompareResult(
            page,
            Truncated: notes.Count > 0,
            Note: notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    [McpServerTool(Name = "search_objects", ReadOnly = true)]
    [Description("Searches a BC release for AL objects (tables, pages, codeunits, reports, etc.) by name or id. Tokens in the pattern are split on whitespace; every token must appear as a case-insensitive substring of the object name (BC \"Tell Me\" style), so 'sal set' finds 'Sales & Receivables Setup'. Wrap a phrase in double quotes for a literal match (\"sales header\"), and prefix a token with '-' to exclude it (setup -temp). Returns the owning module name + source file pointer for each hit, with word-boundary matches ranked first.")]
    public async Task<IReadOnlyList<ReleaseObjectMatch>> SearchObjectsAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Whitespace-separated tokens; every token must appear (case-insensitive substring) in the object name. Use \"quotes\" for a literal phrase and a -prefix to exclude a token. A single bare numeric token still matches by object id. A leading kind prefix scopes the search to one kind: 't:item' (or 'table:item') finds tables named 'item'; bare 't:' returns every table. Shortcuts: t/p/c/r/q/x/e/i and te/pe/re/ee/ps/pse/ca/ms/pr, or the full kind name.")] string namePattern,
        [Description("Optional AL kind filter ('table', 'page', 'codeunit', 'report', 'query', 'enum', 'interface', 'tableextension', 'pageextension'). Legacy C/AL releases also carry 'menusuite' (and 'form'/'dataport' from pre-2013 exports). Pass several comma-separated to match any of them ('table,page'). Null returns every kind.")] string? kind = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var kinds = kind?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await _search.SearchObjectsInReleaseAsync(
            releaseId,
            new ObjectListFilter(Kinds: kinds, Search: namePattern),
            MaxResults,
            ct: ct);
    }

    [McpServerTool(Name = "search_procedures", ReadOnly = true)]
    [Description("Searches a BC release for procedures (methods / triggers / event publishers) by name. Returns the owning object + module for each hit.")]
    public async Task<IReadOnlyList<ReleaseProcedureMatch>> SearchProceduresAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Substring of the procedure name.")] string namePattern,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        return await _search.SearchProceduresInReleaseAsync(releaseId, namePattern, moduleId, MaxResults, ct: ct);
    }

    [McpServerTool(Name = "search_content", ReadOnly = true)]
    [Description("Searches AL source content across a BC release for a free-text query. Returns the line containing the hit; truncated at ~500 characters per line. The query must be at least 3 characters (a shorter term can't be trigram-indexed and returns no results).")]
    public async Task<IReadOnlyList<ReleaseContentMatch>> SearchContentAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Free-text search query, at least 3 characters. Matched literally (case-insensitive substring) against .al source bodies.")] string query,
        [Description("Optional ModuleId to narrow to one module.")] long? moduleId = null,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var rows = await _search.SearchContentInReleaseAsync(releaseId, query, moduleId, MaxResults, 3, ct);
        // Snippet is whatever the line carries; cap to keep responses bounded.
        return rows.Select(r => r.Snippet.Length > 500
            ? r with { Snippet = r.Snippet[..500] + "..." }
            : r).Take(MaxResults).ToList();
    }

    [McpServerTool(Name = "find_references", ReadOnly = true)]
    [Description("Finds every place in a BC release that references a specific table, page, or codeunit — optionally narrowed to a specific field or procedure on it. Resolves the target by name; pass 'Sales Line' + field 'No.' to answer 'which procedures change the No. field on Sales Line?'-style questions.")]
    public async Task<IReadOnlyList<ReferenceMatch>> FindReferencesAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Name of the target object — table, page, codeunit, etc. Resolved case-insensitively against oe_module_objects.")] string targetObject,
        [Description("AL kind of the target object. Defaults to 'table'.")] string targetKind = "table",
        [Description("Optional field name on the target table — narrows the search to references that touch this field.")] string? targetField = null,
        [Description("Optional procedure name on the target object — narrows the search to references that call this procedure.")] string? targetProcedure = null,
        CancellationToken ct = default)
    {
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
        var matches = memberName is null
            ? await _references.FindReferencesAsync(releaseId, query, ct)
            : await _references.FindReferencesForSymbolAsync(releaseId, query, ct);
        return matches.Count > MaxResults ? matches.Take(MaxResults).ToList() : matches;
    }

    [McpServerTool(Name = "find_system_references", ReadOnly = true)]
    [Description("Finds every call to a built-in / system method (Insert, Modify, ModifyAll, Delete, DeleteAll, SetRange, Validate, etc.) on a specific object in a BC release — the calls that find_references deliberately omits. Use this to answer 'where is this table inserted/modified/deleted?'. Optionally narrow to one method.")]
    public async Task<IReadOnlyList<ReferenceMatch>> FindSystemReferencesAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Name of the target object — usually a table. Resolved case-insensitively against oe_module_objects.")] string targetObject,
        [Description("AL kind of the target object. Defaults to 'table'.")] string targetKind = "table",
        [Description("Optional system method name to narrow to, e.g. 'Insert', 'Modify', 'Delete', 'SetRange'.")] string? systemMethod = null,
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
            SystemMethodName: string.IsNullOrWhiteSpace(systemMethod) ? null : systemMethod.Trim());

        var matches = await _references.FindSystemReferencesAsync(releaseId, query, ct);
        return matches.Count > MaxResults ? matches.Take(MaxResults).ToList() : matches;
    }

    [McpServerTool(Name = "get_object_outline", ReadOnly = true)]
    [Description("Returns the outline of one AL object (table, page, codeunit, etc.) in a BC release — its declared procedures, triggers, event publishers, and fields with their line numbers and symbol ids. Use this to discover what an object exposes before calling get_procedure_source or list_procedure_calls. The returned symbol ids disambiguate procedure name collisions (page actions and table fields each carry an OnAction / OnValidate trigger with the same name). Each row also carries 'doc' — the declaration's XML doc-comment summary — when the module was imported with source and the declaration was documented; it is null otherwise, which is the common case.")]
    public async Task<ObjectOutline> GetObjectOutlineAsync(
        [Description("Release Label ('BC 28.1') or numeric id from list_releases.")] string releaseLabelOrId,
        [Description("Name of the target object — resolved case-insensitively against oe_module_objects.")] string objectName,
        [Description("AL kind of the target object. Defaults to 'codeunit'.")] string objectKind = "codeunit",
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var outline = await _explorer.GetObjectOutlineAsync(releaseId, objectKind, objectName, ct);
        if (outline is null)
        {
            throw new McpException($"Could not find a {objectKind.Trim().ToLowerInvariant()} named '{objectName}' in release {releaseLabelOrId}. Try search_objects to discover the exact name.");
        }
        return outline;
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
    [Description("Returns the outgoing references (method calls, field accesses, type references) emitted from inside one procedure / trigger body. Use this to trace what a procedure does without reading the full source: each row carries the target object + member name + line so the agent can follow the call chain. Same disambiguation rules as get_procedure_source — pass symbolId from get_object_outline when the procedure name is shared (OnAction / OnValidate).")]
    public async Task<IReadOnlyList<ProcedureCall>> ListProcedureCallsAsync(
        [Description("Release Label or numeric id.")] string releaseLabelOrId,
        [Description("Symbol id from get_object_outline. Preferred when set.")] long? symbolId = null,
        [Description("Name of the owning object. Required when symbolId is null.")] string? objectName = null,
        [Description("AL kind of the owning object. Required when symbolId is null.")] string? objectKind = null,
        [Description("Name of the procedure / trigger. Required when symbolId is null.")] string? procedureName = null,
        CancellationToken ct = default)
    {
        var resolvedSymbolId = symbolId ?? await _explorer.ResolveProcedureSymbolIdAsync(releaseLabelOrId, objectName, objectKind, procedureName, ct);
        await _explorer.EnsureSymbolVisibleAsync(resolvedSymbolId, ct);
        var calls = await _explorer.ListProcedureCallsAsync(resolvedSymbolId, MaxResults, ct);
        if (calls is null)
        {
            throw new McpException($"Symbol id {resolvedSymbolId} doesn't exist. Call get_object_outline to see the current ids for an object.");
        }
        return calls;
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
    [Description("Searches translated captions / labels / error messages in a BC release for a substring of the target text. Use this when a customer reports an issue in their native language and you need to find the AL field, caption, or label that produced the text. Defaults to caption + label hits (the user's stated priority — captions for fields, labels for error messages); pass kinds='any' to include tooltips and other property kinds. Each hit carries the owning module + object + sub-element + property names plus a SymbolId when the lookup hint resolved to a known symbol — clients can navigate straight to source for those rows.")]
    public async Task<IReadOnlyList<TranslationMatch>> SearchTranslationsAsync(
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
        return await _translations.SearchTranslationsInReleaseAsync(
            releaseId, query, language, set, moduleNamePattern, MaxResults, ct);
    }

    private const int MaxProcedureSourceLines = 200;

    [McpServerTool(Name = "list_release_modules", ReadOnly = true)]
    [Description("Lists every module (extension) in a BC release with its identity, flags, and whether a SymbolReference.json was stored for it at import time. Use this to discover the modules you can pass to search_objects, find_references, get_object_outline, and download_symbol_reference. Sorted alphabetically by name; capped at the standard result limit.")]
    public async Task<IReadOnlyList<ReleaseModuleSummary>> ListReleaseModulesAsync(
        [Description("Release Label ('BC 28.1') or numeric id.")] string releaseLabelOrId,
        CancellationToken ct = default)
    {
        var releaseId = await _explorer.ResolveReleaseAsync(releaseLabelOrId, ct);
        var rows = await _explorer.ListReleaseModulesForMcpAsync(releaseId, MaxResults, ct);
        return rows.Select(m => new ReleaseModuleSummary(
            m.Id, m.Name, m.Publisher, m.Version, m.AppId,
            m.IsTest, m.IsInternal, m.IsLanguagePack, m.DependencyCount, m.HasStoredSymbolReference)).ToList();
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
