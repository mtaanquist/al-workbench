using ALDevToolbox.Services.ObjectExplorer.Explore;
namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// One row in the Releases picker. Carries denormalised file count and
/// content-size totals so admins can spot which Release is dragging on
/// storage without a per-row drill-down. The sizes are <em>character</em>
/// counts (Postgres <c>LENGTH(content)</c>) rather than byte counts — close
/// enough for ASCII-dominant AL source, off by 1–4× for the rare multi-byte
/// run, which is fine for a "how much room is this taking" overview.
/// <see cref="PipelineName"/> is filled only by
/// <see cref="ObjectExplorerService.ListLatestPipelineBuildReleasesAsync"/> —
/// the pipeline-build rows surfaced on the Third-party tab.
/// </summary>
public sealed record ReleaseListItem(
    int Id,
    string Label,
    string Kind,
    string Status,
    string? BcVersion,
    int? ParentReleaseId,
    string? ParentLabel,
    string? Publisher,
    string? ProjectName,
    DateTime ImportedAt,
    int SourceFileCount,
    long SourceContentLength,
    DateTime? DeletedAt,
    string? StatusMessage = null,
    string? PipelineName = null,
    bool IsPrerelease = false,
    bool FromSymbolFeed = false)
{
    /// <summary>
    /// A symbols package a pipeline build pulled from the symbol feeds that came
    /// with no source files (#1092). It stays in the database so builds can resolve
    /// references into it, but the releases page and the compare picker leave it
    /// out. A manually imported release without source is still listed: an admin
    /// chose to import it, and its objects can be browsed and compared.
    /// </summary>
    public bool IsEmptySymbolPackage => FromSymbolFeed && SourceFileCount == 0;
}

/// <summary>
/// Release detail surface for the header bar — adds module count and the
/// parent label so the breadcrumb can show "Project X on BC 25.18".
/// </summary>
public sealed record ReleaseDetail(
    int Id,
    string Label,
    string Kind,
    string Status,
    string? StatusMessage,
    string? BcVersion,
    int? ParentReleaseId,
    string? ParentLabel,
    string? Publisher,
    string? ProjectName,
    DateTime ImportedAt,
    DateTime? DeletedAt,
    int ModuleCount,
    bool IsPrerelease = false);

/// <summary>One per-app row of a project build's report — drives the manage page's build panel and seeds the future Artifacts surface. Carries the source provenance (repo + commit) when known.</summary>
public sealed record ProjectBuildResultRow(
    string AppName,
    string AppId,
    string Status,
    string? Message,
    string? RepoUrl,
    string? CommitSha,
    DateTime? CommitDate);

/// <summary>Filter for <c>ListModulesAsync</c> — applies a substring search and the test/internal toggles.</summary>
public sealed record ModuleListFilter(
    string? Search = null,
    bool IncludeTest = false,
    bool IncludeInternal = false,
    bool IncludeLanguagePack = false);

/// <summary>Header facts for one module: its identity and the Release it came from.</summary>
public sealed record ModuleHeader(
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    int ReleaseId,
    string ReleaseLabel);

/// <summary>One row in the Modules list — name, publisher, version, counts.</summary>
public sealed record ModuleListItem(
    long Id,
    Guid AppId,
    string Name,
    string Publisher,
    string Version,
    string? Target,
    bool IsTest,
    bool IsInternal,
    bool IsLanguagePack,
    int ObjectCount);

/// <summary>
/// Filter for the per-module Objects browser. <see cref="Kinds"/> narrows to
/// one or more AL kinds (codeunit/table/etc.) — empty/null means every kind;
/// <see cref="Search"/> matches against object name or (when numeric) object id.
/// </summary>
public sealed record ObjectListFilter(
    IReadOnlyList<string>? Kinds = null,
    string? Search = null);

/// <summary>Sortable columns on the release-detail objects grid.</summary>
public enum ObjectSortColumn
{
    /// <summary>
    /// The grid's default order when no header is clicked: kind, then object id,
    /// then fewest module dependencies (so System / Base Application float above
    /// partner / project extensions), then module name.
    /// </summary>
    Default,
    Type,
    Id,
    Name,
    Module,
    Namespace,
    Lines,
}

/// <summary>
/// One page of an object search — the requested window plus the total row
/// count of the (filtered) result set, so the UI can tell whether more pages
/// remain as the user scrolls.
/// </summary>
public sealed record ObjectSearchPage(IReadOnlyList<ReleaseObjectMatch> Rows, int TotalCount);

/// <summary>One row in the per-module Objects list.</summary>
public sealed record ObjectListItem(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    Guid? ExtendsAppId,
    string? ExtendsObjectName,
    long? SourceFileId,
    int LineNumber);

/// <summary>Page-shaped wrapper for the object browser.</summary>
public sealed record ObjectListPage(IReadOnlyList<ObjectListItem> Rows, int TotalCount);

/// <summary>
/// Detail surface for one ModuleObject. Joins related symbols and variables
/// so the inspector panel can render in a single round-trip.
/// </summary>
public sealed record ObjectDetail(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    long ModuleId,
    string ModuleName,
    Guid? ExtendsAppId,
    string? ExtendsObjectName,
    long? SourceFileId,
    string? SourceFilePath,
    int LineNumber,
    IReadOnlyList<ObjectSymbolRow> Symbols,
    IReadOnlyList<ObjectVariableRow> Variables);

public sealed record ObjectSymbolRow(
    long Id,
    string Kind,
    string Name,
    string? Signature,
    string? ReturnType,
    int? FieldId,
    int LineNumber,
    // The declaration's XML doc summary, when the module was imported with
    // source and the author wrote one. Defaulted so the many positional
    // constructions of this row don't all have to name it.
    string? Doc = null);

/// <summary>
/// Outline projection of a single AL object — the slim surface the
/// forward-edge MCP <c>get_object_outline</c> tool returns. Drops
/// <see cref="ObjectDetail.Variables"/> (not needed for trace
/// navigation) and the extends-* fields (kept on the heavier
/// <see cref="ObjectDetail"/> when the inspector panel needs them).
/// Symbol rows carry the same <see cref="ObjectSymbolRow.Id"/> the
/// per-procedure tools accept as a disambiguating <c>symbolId</c> when
/// the simple by-name form is ambiguous (page actions and table fields
/// both produce multiple <c>OnAction</c> / <c>OnValidate</c> triggers
/// per object).
/// </summary>
public sealed record ObjectOutline(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    long ModuleId,
    string ModuleName,
    long? SourceFileId,
    string? SourceFilePath,
    int LineNumber,
    IReadOnlyList<ObjectSymbolRow> Symbols,
    IReadOnlyList<InterfaceImplementer>? ImplementedBy = null);

/// <summary>
/// One codeunit that implements an interface (via the codeunit's
/// <c>implements "X"</c> header clause). Populated on
/// <see cref="ObjectOutline.ImplementedBy"/> when the outlined object is
/// an interface, derived from <c>implements_interface</c> reference
/// rows across the visible module chain. Empty for non-interface kinds.
/// </summary>
public sealed record InterfaceImplementer(
    long ObjectId,
    string ObjectName,
    string ModuleName);

/// <summary>
/// Slice of an AL procedure / trigger body returned by the
/// <c>get_procedure_source</c> MCP tool. <see cref="Source"/> is the
/// raw text from <c>oe_module_files.content</c> sliced inclusive of
/// the declaration line and the matching <c>end;</c> line, capped at
/// a maxLines budget so a pathological procedure can't blow the MCP
/// response. When <see cref="Truncated"/> is true, the source ends with
/// a comment marker noting the full length so the agent knows to ask
/// for more focused queries via <c>list_procedure_calls</c> instead.
/// </summary>
public sealed record ProcedureSource(
    long SymbolId,
    string ObjectName,
    string ObjectKind,
    string Kind,
    string Name,
    string? Signature,
    string? ReturnType,
    int StartLine,
    int EndLine,
    bool Truncated,
    string Source,
    // The declaration's XML doc summary, carried separately because the
    // slice starts at the declaration line — the doc comment sits above it
    // and would otherwise be the one part of the source an agent reading a
    // procedure never sees.
    string? Doc = null);

/// <summary>
/// One outgoing call (or field access) from a procedure body, returned
/// by the <c>list_procedure_calls</c> MCP tool. Lets an agent walking
/// a trace chain see "what does this procedure touch?" without having
/// to read the entire body. <see cref="TargetAppId"/> + the target
/// triplet identify the dependency module across release boundaries
/// (the same identity tuple <c>find_references</c> uses in reverse).
/// </summary>
public sealed record ProcedureCall(
    long Id,
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string? TargetMemberName,
    string? TargetMemberKind,
    string ReferenceKind,
    int? LineNumber,
    int? ColumnNumber);

public sealed record ObjectVariableRow(
    long Id,
    string Name,
    string? TypeKeyword,
    string TypeName,
    Guid? TargetAppId,
    string? TargetObjectKind,
    int? TargetObjectId,
    string? TargetObjectName);

/// <summary>
/// Query envelope for <c>FindReferencesAsync</c>. Either a (kind, id) pair or
/// a (kind, name) pair identifies the target object. ID is preferred when
/// present; name is the fallback for kinds the symbol package doesn't number
/// (interfaces, some extensions).
///
/// When <see cref="TargetMemberName"/> is non-null the query scopes to a
/// member (procedure / field / trigger) inside that owner: it returns
/// sibling declarations of the same name plus the <c>method_call</c> /
/// <c>field_access</c> rows emitted by call-site extraction.
/// </summary>
public sealed record FindReferencesQuery(
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string? TargetMemberName = null,
    string? TargetMemberKind = null);

/// <summary>
/// Query envelope for <c>FindSystemReferencesAsync</c> — the receiver object
/// triplet (same matching as <see cref="FindReferencesQuery"/>), optionally
/// narrowed to a single built-in method (<c>Insert</c>, <c>Modify</c>, …).
/// </summary>
public sealed record FindSystemReferencesQuery(
    Guid TargetAppId,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    string? SystemMethodName = null);

/// <summary>
/// One reference matched by <c>FindReferencesAsync</c>. Carries enough
/// joined context (source module, source object, reference kind, line)
/// for the UI to link back to the calling object's file viewer without a
/// follow-up query.
///
/// For member-scoped searches the row's <see cref="Category"/> indicates
/// which strategy produced it — "declaration" (the matched symbol itself
/// elsewhere in the chain), "owner_type" (a variable / parameter / return
/// referencing the owner object), "call" (a method-call / field-access row
/// emitted by call-site extraction), or "implementation" (an interface
/// method's implementing procedure). The UI groups results by source object.
///
/// <see cref="MemberName"/> / <see cref="MemberKind"/> / <see cref="MemberSignature"/>
/// describe the <em>target</em> member (the thing referenced). The
/// <c>SourceMember*</c> trio describes the <em>enclosing</em> procedure /
/// trigger the reference sits inside (resolved from the reference's
/// <c>source_symbol_id</c>) — null for declarations and object-scope
/// references that aren't inside a member body.
/// </summary>
public sealed record ReferenceMatch(
    long Id,
    long SourceModuleId,
    string SourceModuleName,
    long SourceObjectId,
    string SourceObjectKind,
    string SourceObjectName,
    string ReferenceKind,
    int? LineNumber,
    long? SourceFileId,
    string? SourceFilePath = null,
    string? Snippet = null,
    string Category = "object",
    string? MemberName = null,
    string? MemberKind = null,
    string? MemberSignature = null,
    string? SourceMemberName = null,
    string? SourceMemberKind = null,
    string? SourceMemberSignature = null);

/// <summary>
/// Read-only projection of an <c>oe_module_files</c> row for the source viewer.
/// Content is loaded lazily — only fetched via <c>GetFileAsync</c> so the
/// list-files page doesn't drag every body into memory.
/// </summary>
public sealed record SourceFileDetail(
    long Id,
    long ModuleId,
    string Path,
    string Content,
    int LineCount);

/// <summary>
/// One hit from a Release-wide object search. Carries the owning module so
/// the result table can render "Module / Object" without a follow-up query,
/// plus the source-file pointer + line number so the row can deep-link
/// straight into the source viewer at the right line.
/// </summary>
public sealed record ReleaseObjectMatch(
    long Id,
    string Kind,
    int? ObjectId,
    string Name,
    string? Namespace,
    long ModuleId,
    string ModuleName,
    long? SourceFileId,
    int LineNumber,
    int FileLineCount,
    string? VersionList = null);

/// <summary>
/// Header info for the source-file viewer's breadcrumb — module + release
/// names so the page renders the full path without two extra round trips.
/// <see cref="ObjectNamespace"/> is the AL namespace declared by the
/// primary object in the file (e.g. <c>Microsoft.Foundation.Reporting</c>);
/// null when the file isn't backing a single object or no namespace is
/// declared. The breadcrumb prefers it over the raw <see cref="Path"/>
/// because AL namespaces are the canonical "where does this live"
/// identity (folder layouts vary across vendors and BC versions).
/// </summary>
public sealed record SourceFileHeader(
    long Id,
    long ModuleId,
    string ModuleName,
    int ReleaseId,
    string ReleaseLabel,
    string Path,
    int LineCount,
    string? ObjectNamespace,
    /// <summary>
    /// The owning module's <c>App.Runtime</c> (e.g. "13.0"), for the viewer's
    /// status bar (#568). Null for a module whose manifest did not declare one.
    /// </summary>
    string? Runtime = null);

/// <summary>
/// One procedure-search hit on the Release search page. Carries the source
/// file pointer so the row can deep-link straight to the declaration line.
/// </summary>
public sealed record ReleaseProcedureMatch(
    long Id,
    long ObjectId,
    string ObjectKind,
    string ObjectName,
    string ModuleName,
    string ProcedureKind,
    string Name,
    string? Signature,
    string? ReturnType,
    long? SourceFileId,
    int ObjectLineNumber);

/// <summary>
/// One content-search hit on the Release search page. The snippet is the
/// matched line's text trimmed to a reasonable display length; the file
/// pointer + line number deep-link straight into the source viewer with the
/// hit row highlighted.
/// </summary>
public sealed record ReleaseContentMatch(
    long FileId,
    string FilePath,
    long ModuleId,
    string ModuleName,
    int LineNumber,
    string Snippet);

/// <summary>
/// One row in the outline pane on the source-file viewer. Objects and their
/// symbols are flattened so the panel can render them as a single ordered
/// list keyed by line number — kind tells the renderer which icon to use.
///
/// <see cref="ObjectId"/> is set for object header rows (codeunit, table,
/// page, …) and drives the cross-page anchor.
/// <see cref="SymbolId"/> is set for procedure / field / trigger / event
/// symbol rows and drives the outline's right-click "Find references"
/// menu — clicking a procedure row mints a member-scoped session
/// without having to first click the declaration in the editor.
/// <see cref="EndLine"/> mirrors <c>oe_module_symbols.end_line</c> for
/// procedure-like symbols imported with the post-#181 walker; null for
/// objects and for legacy imports that pre-date the column. Drives the
/// status-bar "current procedure" lookup in the source viewer.
/// </summary>
public sealed record SourceFileOutlineItem(
    string Kind,
    string Name,
    string? Signature,
    int LineNumber,
    long? ObjectId,
    long? SymbolId = null,
    int? EndLine = null,
    // The declared return type, when the importer captured one. Deliberately
    // separate from <see cref="Signature"/>: the source-text extractor's
    // DeclarationRegex captures the parameter list only, so a signature never
    // carries a return type and parsing one out of its tail always yields
    // nothing. Only the symbol-package path populates this.
    string? ReturnType = null,
    // The declaration's XML doc summary. Null for object header rows, for
    // the undocumented majority, and for symbols-only imports.
    string? Doc = null);

/// <summary>
/// Minimal Module summary used by the search-filter dropdown. Lighter than
/// <see cref="ModuleListItem"/> — none of the row counts or test/internal
/// flags matter for the filter widget.
/// </summary>
public sealed record ReleaseModuleSummary(
    long Id,
    string Name,
    string Publisher);

/// <summary>
/// What the source viewer's hover card shows for one symbol: the signature
/// line, where it lives, and enough to offer "go to definition". Assembled
/// from the symbol row plus its owning object / module / file — the same
/// facts the outline already has, but for a symbol declared in some *other*
/// file, which is exactly when hovering is worth anything.
///
/// <see cref="FileId"/> is null when the owning object has no imported
/// source (a symbol-package-only module); the card then renders without a
/// jump action rather than offering a link that goes nowhere.
/// </summary>
public sealed record SymbolCard(
    long SymbolId,
    string Name,
    string Kind,
    string? Signature,
    string OwnerKind,
    string OwnerName,
    string ModuleName,
    string? FilePath,
    long? FileId,
    int LineNumber,
    // Kept apart from <see cref="Signature"/>, which is the parameter list
    // and nothing else — the card joins the two back into a declaration.
    string? ReturnType,
    // The declaration's XML doc summary, rendered as the card's prose line.
    // Null far more often than not — the card drops the line rather than
    // reserving empty space for it.
    string? Doc = null);

/// <summary>
/// Resolved go-to-definition target — file + 1-based line. The source
/// viewer navigates there with <c>?line=N</c> so the CodeMirror scroll
/// lands on the declaration row.
/// </summary>
public sealed record GoToDefinitionTarget(long FileId, int LineNumber);

/// <summary>
/// One occurrence of a word inside a single file — returned by
/// <c>FindInFileAsync</c> so the page can show a navigable list of every
/// line that matches the clicked identifier.
/// </summary>
public sealed record FileWordOccurrence(int Line, string LineText);

/// <summary>
/// Result envelope for the "Find in this file" gesture. <see cref="Word"/>
/// is what was extracted from the click position; <see cref="Occurrences"/>
/// is every line in the same file that contains it.
/// </summary>
public sealed record FileWordSearch(string Word, IReadOnlyList<FileWordOccurrence> Occurrences);

/// <summary>
/// A cached "Find references" search the source viewer keeps visible while
/// the user navigates between result files. <see cref="TargetLabel"/> is the
/// human-readable description for the sidebar heading ("references to
/// <c>Customer</c>"); <see cref="Results"/> is the same list the legacy
/// Object Detail page already renders.
/// </summary>
public sealed record ReferenceSession(
    string Token,
    string TargetLabel,
    int ReleaseId,
    IReadOnlyList<ReferenceMatch> Results,
    // True when the underlying query hit the MaxReferenceMatches cap and
    // Results was trimmed — the UI shows a "showing first N" notice (#366).
    bool Truncated = false,
    // Just the name of the thing being searched for ("Sales Header",
    // "BlockCustomer"), where TargetLabel is the whole sentence fragment
    // ("references to table 36 Sales Header"). The panel heading wants the
    // short form; the log line and the close button's tooltip want the long one.
    string TargetName = "");

/// <summary>
/// One row in the source-viewer outline's "Using" or "Used by" sections (#148).
/// Targets without ingested source come through with <see cref="TargetFileId"/>
/// null and the UI renders a non-clickable badge with a "no source available"
/// tooltip.
/// </summary>
public sealed record DependencyEntry(
    Guid TargetAppId,
    string TargetModuleName,
    string TargetObjectKind,
    int? TargetObjectId,
    string TargetObjectName,
    long? TargetFileId,
    int? TargetLineNumber,
    string ReferenceKind);

/// <summary>
/// Aggregated dependency view for a single source file. Returned in one
/// round-trip by the lazy-loaded outline endpoint so the viewer wires both
/// sections from one fetch.
/// </summary>
public sealed record FileDependencies(
    IReadOnlyList<DependencyEntry> Using,
    IReadOnlyList<DependencyEntry> UsedBy);

/// <summary>
/// One language available on a release, with the total trans-unit count
/// across every module. Cheap discovery for "what can I search in?" —
/// drives the MCP <c>list_translation_languages</c> tool and the per-release
/// admin page header.
/// </summary>
public sealed record TranslationLanguageSummary(
    string LanguageCode,
    int TranslationCount);

/// <summary>
/// Per-module, per-language row count — drives the per-module language
/// chips on the admin translations page.
/// </summary>
public sealed record ModuleTranslationLanguageRow(
    long ModuleId,
    string LanguageCode,
    int TranslationCount);

/// <summary>
/// One translation hit from <c>SearchTranslationsInReleaseAsync</c>. The
/// MCP tool returns these so an agent can answer "the caption belongs to
/// field 'Activate Assembly On Service' on table 'AppSetup'", and
/// <see cref="SymbolId"/> (when non-null) lets the source viewer
/// navigate straight to the declaration.
/// </summary>
public sealed record TranslationMatch(
    long Id,
    string LanguageCode,
    string ModuleName,
    string? ObjectKind,
    string? ObjectName,
    string? SubKind,
    string? SubName,
    string? PropertyName,
    string Kind,
    string SourceText,
    string TargetText,
    long? SymbolId);

/// <summary>
/// One row in the Object Explorer's left-hand tree (the handoff's
/// <c>.otree</c>). The tree is three levels of thing rather than one:
/// <c>module</c> rows are the apps in the release, <c>folder</c> rows the
/// module-relative source folders, and <c>file</c> rows the leaves that
/// navigate.
///
/// Only the branch leading to the open file is server-rendered; every other
/// caret fetches its children from
/// <c>/api/object-explorer/modules/{id}/tree</c> on first open, because a
/// Base Application module runs to thousands of files.
/// </summary>
/// <param name="Kind"><c>module</c>, <c>folder</c> or <c>file</c>.</param>
/// <param name="Name">What the row reads. A file row prefers its object's
/// name over the file name — an AL developer navigates by object — and keeps
/// the file name on the row's <c>title</c>.</param>
/// <param name="Path">Module-relative path. Empty for a module row; a folder
/// row's path ends in <c>/</c> so it can be used as a prefix directly.</param>
/// <param name="HasChildren">Whether the row draws a caret. Folders always
/// do (we only emit folders that have descendants); modules always do.</param>
/// <param name="Badge">The right-aligned mono label: a module's version, or
/// an object's AL id.</param>
public sealed record OeTreeNode(
    string Kind,
    string Name,
    string Path,
    long ModuleId,
    int Depth,
    bool HasChildren,
    bool IsOpen,
    bool IsActive,
    long? FileId = null,
    string? ObjectKind = null,
    string? FileName = null,
    string? Badge = null);

/// <summary>
/// One declaration the viewer should make clickable. Coordinates are 1-based
/// and align with what <c>AlSymbolExtractor</c> captures at import time.
///
/// <see cref="SymbolId"/> identifies the underlying row — its meaning depends
/// on <see cref="IsMemberSymbol"/>:
/// <list type="bullet">
///   <item><c>false</c> (object header): <c>oe_module_objects.Id</c>. The
///         right-click "Find references" routes via
///         <c>/from-symbol/{id}</c> (object-scoped query).</item>
///   <item><c>true</c> (procedure / field / trigger / event symbol):
///         <c>oe_module_symbols.Id</c>. The right-click "Find references"
///         routes via <c>/from-member-symbol/{id}</c> (member-scoped
///         query: declarations + calls + owner-type buckets).</item>
/// </list>
/// Two ID spaces don't mix — keeping the flag explicit lets the JS host
/// pick the right endpoint without having to guess from the kind string.
/// </summary>
public sealed record CodeViewerDeclaration(
    long SymbolId,
    int Line,
    int ColumnStart,
    int ColumnEnd,
    string Kind,
    string Name,
    bool IsMemberSymbol = false,
    string? OwnerKind = null);

/// <summary>
/// One range the viewer should underline as resolvable. Same column convention
/// as <see cref="CodeViewerDeclaration"/>: 1-based, end-exclusive.
///
/// <paramref name="SymbolId"/> is the symbol the reference resolved to, when
/// the importer managed to resolve one. The viewer uses it to fetch the hover
/// card; navigation doesn't need it (go-to-definition re-resolves from the
/// click position), so it stays optional.
/// </summary>
public sealed record CodeViewerResolvable(
    int Line, int ColumnStart, int ColumnEnd, long? SymbolId = null);

/// <summary>One vendor Release a pipeline build linked for its dependency symbols - a row of the manage page's "Third-party dependencies" card. <see cref="AppName"/> / <see cref="AppVersion"/> come from the app it holds, null while it has none.</summary>
public sealed record ReleaseDependencyRow(
    int ReleaseId,
    string Label,
    string? Publisher,
    string Status,
    string? AppName = null,
    string? AppVersion = null);
