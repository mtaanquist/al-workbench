using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The source-file viewer surface: file content + header, the per-file
/// outline, and the click-to-navigate helpers (declarations, go-to-definition,
/// resolvable spans, find-in-file). Split out of <see cref="ObjectExplorerService"/>
/// so the viewer/navigation logic stands on its own; the explorer tree around
/// it is <see cref="ExplorerTreeService"/>. The outline's
/// "implemented by" section is enriched via <see cref="ReferenceQueryService"/>
/// (the same query the object outline uses); cursor-resolution leans on the
/// static <see cref="Al.AlGoToDefinitionLocator"/>. Project visibility is
/// answered by <see cref="SourceVisibility"/>. All reads are
/// <c>AsNoTracking</c> and respect the tenant query filter on
/// <see cref="AppDbContext"/>.
/// </summary>
public sealed class SourceViewerService
{
    private readonly AppDbContext _db;
    private readonly ReferenceQueryService _references;
    private readonly SourceVisibility _visibility;

    public SourceViewerService(AppDbContext db, ReferenceQueryService references, SourceVisibility visibility)
    {
        _db = db;
        _visibility = visibility;
        _references = references;
    }

    // ── One source blob per scope ───────────────────────────────────────
    // Four of the methods below need the file's text, and the viewer page calls
    // three of them back-to-back for the file it is opening. Remembering the last
    // blob read turns that into one round-trip instead of three. Only the most
    // recent file is held: a Base Application source file is large, the scope is
    // the circuit, and one page only ever reads one file's text.

    private long? _contentFileId;
    private string? _content;

    private async Task<string?> GetContentAsync(long fileId, CancellationToken ct)
    {
        if (_contentFileId == fileId) return _content;

        var content = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => f.FileContent!.Content)
            .SingleOrDefaultAsync(ct);
        _contentFileId = fileId;
        return _content = content;
    }

    // ── Source file viewer ─────────────────────────────────────────────

    public async Task<SourceFileDetail?> GetFileAsync(long fileId, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return null;

        // When another call in this scope already pulled the blob (the viewer page
        // loads declarations and resolvables around this one), take the text from
        // there and ask the database only for the row's small columns.
        if (_contentFileId == fileId)
        {
            var meta = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => f.Id == fileId)
                .Select(f => new { f.Id, f.ModuleId, f.Path, f.LineCount })
                .SingleOrDefaultAsync(ct);
            return meta is null
                ? null
                : new SourceFileDetail(meta.Id, meta.ModuleId, meta.Path, _content ?? string.Empty, meta.LineCount);
        }

        var detail = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileDetail(f.Id, f.ModuleId, f.Path, f.FileContent!.Content, f.LineCount))
            .SingleOrDefaultAsync(ct);
        if (detail is not null)
        {
            _contentFileId = fileId;
            _content = detail.Content;
        }
        return detail;
    }

    /// <summary>
    /// Header projection for the source-file viewer's breadcrumb. Separate
    /// from <see cref="GetFileAsync"/> so the breadcrumb call doesn't have
    /// to drag the full Content blob through.
    /// </summary>
    public async Task<SourceFileHeader?> GetFileHeaderAsync(long fileId, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return null;
        return await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new SourceFileHeader(
                f.Id, f.ModuleId, f.Module!.Name,
                f.Module.ReleaseId, f.Module.Release!.Label,
                f.Path, f.LineCount,
                // AL enforces one object per file in practice so picking the
                // first attached object's namespace is unambiguous. ModuleFile
                // has no inverse collection nav onto ModuleObject (the FK
                // direction is one-way, with SetNull on delete), so this is
                // a correlated subquery rather than a navigation traversal.
                // Skips gracefully when the file isn't backing an object.
                _db.OeModuleObjects.AsNoTracking()
                    .Where(o => o.SourceFileId == f.Id && o.Namespace != null)
                    .Select(o => o.Namespace)
                    .FirstOrDefault(),
                f.Module.Runtime))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// Flattens objects + their symbols inside a single source file into one
    /// outline list ordered by line. Feeds the right-hand "outline" panel on
    /// the source viewer.
    /// </summary>
    public async Task<List<SourceFileOutlineItem>> GetFileOutlineAsync(long fileId, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return new();
        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber, o.ModuleId })
            .ToListAsync(ct);

        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId)
            .Where(s => s.LineNumber > 0)
            .Select(s => new { s.Id, s.ObjectId, s.Kind, s.Name, s.Signature, s.ReturnType, s.LineNumber, s.EndLine, s.Doc })
            .ToListAsync(ct);

        var items = new List<SourceFileOutlineItem>(objects.Count + symbols.Count);
        foreach (var o in objects)
        {
            items.Add(new SourceFileOutlineItem(o.Kind, o.Name, null, o.LineNumber, o.Id));
        }
        foreach (var s in symbols)
        {
            items.Add(new SourceFileOutlineItem(
                s.Kind, s.Name, s.Signature, s.LineNumber, null, s.Id, s.EndLine, s.ReturnType, s.Doc));
        }

        // For interface files, append synthetic "implemented_by" rows
        // for every codeunit in the visible module chain that declares
        // this interface in its `implements` clause. Synthetic items
        // carry LineNumber = int.MaxValue so they sort to the bottom of
        // the outline; the source-viewer's outline grouper buckets them
        // into a dedicated "IMPLEMENTED BY" section.
        var interfaceObj = objects.FirstOrDefault(o => string.Equals(o.Kind, "interface", StringComparison.OrdinalIgnoreCase));
        if (interfaceObj is not null)
        {
            var implementers = await _references.FindInterfaceImplementersAsync(
                interfaceObj.ModuleId, interfaceObj.Name, ct);
            foreach (var impl in implementers)
            {
                items.Add(new SourceFileOutlineItem(
                    Kind: "implemented_by",
                    Name: impl.SourceObjectName,
                    Signature: impl.SourceModuleName,
                    LineNumber: int.MaxValue,
                    ObjectId: impl.SourceObjectId));
            }
        }

        return items.OrderBy(i => i.LineNumber).ToList();
    }

    // ── Source-viewer navigation ──────────────────────────────────────

    /// <summary>
    /// Returns decoration ranges the source viewer can stamp onto each
    /// object-header token so it hovers, underlines, and surfaces the
    /// "Find references" right-click menu. The <c>SymbolId</c> on each row
    /// is the <c>oe_module_objects.id</c> — the page maps it back into a
    /// navigation to the object detail's Find-references panel.
    /// </summary>
    public async Task<List<CodeViewerDeclaration>> ListDeclarationsInFileAsync(
        long fileId, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return new();
        var content = await GetContentAsync(fileId, ct);
        if (string.IsNullOrEmpty(content)) return new();

        var objects = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.SourceFileId == fileId)
            .Select(o => new { o.Id, o.Kind, o.Name, o.LineNumber })
            .ToListAsync(ct);

        // Sub-symbol declarations (procedures, fields, triggers, event
        // subscribers). oe_module_symbols already stamps 1-based
        // line/column spans at import via AlSymbolExtractor, so we don't
        // need a re-scan here — symbol rows with LineNumber > 0 are
        // declared in source and can be made clickable directly.
        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Object!.SourceFileId == fileId
                && s.LineNumber > 0
                && s.ColumnEnd > s.ColumnStart)
            .Select(s => new
            {
                s.Id, s.Kind, s.Name, s.LineNumber, s.ColumnStart, s.ColumnEnd,
                OwnerKind = s.Object!.Kind,
            })
            .ToListAsync(ct);

        var lines = OeSourceText.SplitLines(content);
        var result = new List<CodeViewerDeclaration>(objects.Count + symbols.Count);
        foreach (var obj in objects)
        {
            if (obj.LineNumber < 1 || obj.LineNumber > lines.Length) continue;
            var lineText = lines[obj.LineNumber - 1];

            // BC declarations typically quote the name —
            // `codeunit 80 "Sales-Post"`. Bare-identifier names (test code,
            // some old code) are matched as a fallback.
            int colStart, colEnd;
            var quoted = "\"" + obj.Name + "\"";
            var idx = lineText.IndexOf(quoted, StringComparison.Ordinal);
            if (idx >= 0)
            {
                colStart = idx + 1;
                colEnd = idx + 1 + quoted.Length;
            }
            else
            {
                idx = lineText.IndexOf(obj.Name, StringComparison.Ordinal);
                if (idx < 0) continue;
                colStart = idx + 1;
                colEnd = idx + 1 + obj.Name.Length;
            }

            result.Add(new CodeViewerDeclaration(
                SymbolId: obj.Id,
                Line: obj.LineNumber,
                ColumnStart: colStart,
                ColumnEnd: colEnd,
                Kind: obj.Kind,
                Name: obj.Name));
        }

        foreach (var sym in symbols)
        {
            result.Add(new CodeViewerDeclaration(
                SymbolId: sym.Id,
                Line: sym.LineNumber,
                ColumnStart: sym.ColumnStart,
                ColumnEnd: sym.ColumnEnd,
                Kind: sym.Kind,
                Name: sym.Name,
                IsMemberSymbol: true,
                OwnerKind: sym.OwnerKind));
        }

        // Objects are appended before member symbols, so the raw list isn't
        // ordered by position — a file shipping several objects (an extension
        // bundling multiple objects in one .al) interleaves their headers and
        // members out of order. The source viewer feeds these straight into
        // CodeMirror's RangeSetBuilder, which requires ascending `from`, so
        // sort by (line, column) before handing them over.
        return result.OrderBy(d => d.Line).ThenBy(d => d.ColumnStart).ToList();
    }

    /// <summary>
    /// Resolves a Cmd/Ctrl-click in the source viewer to a navigation
    /// target. Two strategies in order:
    ///
    /// 1. <b>Member-access</b>: when the clicked token matches a
    ///    <c>method_call</c> / <c>field_access</c> reference row on the same
    ///    file + line, follow <c>TargetSymbolId</c> to the
    ///    <see cref="OeModuleSymbol"/> declaration and return its file + line.
    ///    This is the path that resolves <c>GLAcc."Account Type"</c> and
    ///    <c>ConfirmManagement.GetResponseOrDefault</c> — the dominant cases
    ///    that the legacy object-name fallback couldn't reach.
    /// 2. <b>Object-name</b>: same-Release lookup against
    ///    <c>oe_module_objects.Name</c>. Catches bare type literals like
    ///    <c>Customer</c> / <c>"Sales-Post"</c> that the extractor doesn't
    ///    emit member-rows for.
    ///
    /// Returns <c>null</c> when neither strategy matches — the page no-ops
    /// and shows the "No definition found" notice.
    /// </summary>
    public async Task<GoToDefinitionTarget?> GoToDefinitionAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return null;
        var releaseId = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => (int?)f.Module!.ReleaseId)
            .SingleOrDefaultAsync(ct);
        if (releaseId is null) return null;
        var content = await GetContentAsync(fileId, ct);
        if (content is null) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;
        var word = click.Word;

        // 1. Member-access strategy. Phase-2 extraction stamps
        //    method_call / field_access / event_publisher / label_use
        //    rows with (LineNumber, TargetMemberName, TargetSymbolId).
        //    Match the clicked word case-insensitively (AL identifiers
        //    are case-insensitive). Prefer rows with a resolved
        //    TargetSymbolId — those have a direct file + line via the
        //    symbol's owner object.
        var memberHit = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => (r.ReferenceKind == "method_call"
                    || r.ReferenceKind == "field_access"
                    || r.ReferenceKind == "event_publisher"
                    || r.ReferenceKind == "label_use")
                && r.SourceObject!.SourceFileId == fileId
                && r.LineNumber == line
                && r.TargetMemberName != null
                && r.TargetMemberName.ToLower() == word.ToLower())
            .Where(r => r.TargetSymbolId != null)
            .Select(r => new
            {
                SymbolLine = r.TargetSymbol!.LineNumber,
                SymbolFileId = r.TargetSymbol!.Object!.SourceFileId,
            })
            .Where(x => x.SymbolFileId != null)
            .FirstOrDefaultAsync(ct);
        if (memberHit is not null)
        {
            return new GoToDefinitionTarget(memberHit.SymbolFileId!.Value, memberHit.SymbolLine);
        }

        // 2. Local-variable-declaration strategy. The click landed on
        //    an identifier that has a `VarName: Kind "TypeName"`
        //    declaration somewhere in the file — almost always a
        //    local var like `PaymentMethod: Record "Payment Method"`.
        //    The user expects Go-to-definition to land on the
        //    DECLARATION LINE in this file, not on the underlying
        //    type's source: typing `PaymentMethod` everywhere refers
        //    to the variable, so navigating to "where this variable
        //    was declared" is the IDE-conventional behaviour. The
        //    matching click on the underlined type-name token
        //    (`"Payment Method"` itself) still resolves through the
        //    object-name lookup below.
        //
        //    Earlier shape of this step navigated to the type — that
        //    was a temporary workaround for the bug where a bare
        //    variable name was getting object-name-looked-up and
        //    landing on an unrelated tableextension. With Go-to-def
        //    now ending on the declaration line, the user sees the
        //    type-name token right there and can Cmd-click it to
        //    reach the type source if they want it.
        var declLine = Services.Al.AlGoToDefinitionLocator
            .ResolveVariableDeclarationLine(content, word);
        if (declLine is int targetLine)
        {
            return new GoToDefinitionTarget(fileId, targetLine);
        }

        // 3. Object-name lookup across the visible release chain. Walks
        //    parent_release_id (child shadows parent) so a base object
        //    referenced from a project Release lands on the ancestor Release
        //    that defines it — e.g. clicking `Customer` in a Dansani file
        //    navigates to the base table in the BC parent Release. See
        //    ChainObjectResolution.
        //    The click's own context picks between same-named candidates:
        //    `Database::User` means the table, not the codeunit. Without the
        //    hint the ranking fell back to kind order and could land on the
        //    wrong object (issue #712).
        var target = await ChainObjectResolution.ResolveObjectAsync(
            _db, releaseId.Value, word, kind: null, objectId: null, ct,
            kindHint: Services.Al.AlGoToDefinitionLocator.KindHint(click));
        if (target?.SourceFileId is null) return null;
        return new GoToDefinitionTarget(target.SourceFileId.Value, target.LineNumber);
    }

    /// <summary>
    /// Spans inside <paramref name="fileId"/> that the source viewer should
    /// underline as resolvable. Drives the IDE-style "what's clickable"
    /// affordance: every token underlined here will, on right-click or
    /// Cmd-click, resolve to a definition via <see cref="GoToDefinitionAsync"/>.
    ///
    /// Sources from phase-2 <c>method_call</c> / <c>field_access</c> reference
    /// rows: each row carries <c>LineNumber</c> + <c>TargetMemberName</c>; we
    /// re-scan the line to recover the 1-based column range. Same scanning
    /// strategy as <see cref="ListDeclarationsInFileAsync"/> — quoted first
    /// (<c>"Account Type"</c>), bare identifier fallback. Multiple references
    /// on the same line are handled by walking forward through the line text
    /// rather than always picking the first occurrence.
    ///
    /// Variable-declaration types (<c>variable_type</c>, <c>parameter_type</c>,
    /// <c>return_type</c>) aren't included — those reference rows don't carry
    /// a line number; symbol-package extraction doesn't yield source positions.
    /// </summary>
    public async Task<List<CodeViewerResolvable>>
        ListResolvablesInFileAsync(long fileId, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return new();
        var content = await GetContentAsync(fileId, ct);
        if (string.IsNullOrEmpty(content)) return new();

        // Pull every source-extracted reference on the file (LineNumber
        // set). Two row shapes contribute spans:
        //   - Member-scoped (method_call / field_access): the underlined
        //     token is the MEMBER name. Go-to-definition resolves via
        //     the row's TargetSymbolId when present, or falls back to
        //     object-name lookup.
        //   - Object-scoped (property_object from SourceTable,
        //     LookupPageID, …): the underlined token is the TARGET
        //     OBJECT name. Go-to-definition resolves via the object-name
        //     lookup. No member-symbol id needed.
        // The line-text scan below uses the per-row Name to find the
        // 1-based column span — same logic for both shapes.
        var rows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.LineNumber != null)
            .Select(r => new
            {
                Line = r.LineNumber!.Value,
                Column = r.ColumnNumber,
                Name = r.TargetMemberName ?? r.TargetObjectName,
                SymbolId = r.TargetSymbolId,
            })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);
        // NB: don't early-return when there are no member-access rows — the
        // `extends_target` second pass below still has work to do. An extension
        // object whose body has no resolved method_call / field_access rows
        // (e.g. a pageextension that only adds fields) would otherwise lose the
        // underline + go-to-definition on its `extends "Base"` target.

        var lines = OeSourceText.SplitLines(content);
        var result = new List<CodeViewerResolvable>(rows.Count);
        // Group by line so the text-search fallback below can walk forward
        // through multiple references on the same line without re-finding
        // the first occurrence each time. Rows with `Column` set bypass
        // the search entirely.
        foreach (var byLine in rows.GroupBy(r => r.Line))
        {
            if (byLine.Key < 1 || byLine.Key > lines.Length) continue;
            var lineText = lines[byLine.Key - 1];
            // Track per-name search cursors for the text-search path so
            // successive occurrences of the same identifier on one line
            // each get their own span.
            var cursors = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in byLine)
            {
                // Fast path: the extractor stamped the column at emission
                // time. Use it directly and skip the text-search — the
                // search lands on the leftmost occurrence which is wrong
                // when the same identifier appears twice on a line (e.g.
                // `field("No."; Rec."No.")` should underline the RHS
                // Rec."No.", not the LHS control name).
                if (row.Column is { } colStart && colStart >= 1
                    && colStart <= lineText.Length + 1)
                {
                    var col0 = colStart - 1;
                    var nameLen = row.Name!.Length;
                    // The stored column points at the FIRST char of the
                    // identifier. If the source has a quote there, the
                    // underline span needs to include the quotes too.
                    var matchLen = (col0 < lineText.Length && lineText[col0] == '"')
                        ? nameLen + 2
                        : nameLen;
                    result.Add(new CodeViewerResolvable(
                        Line: byLine.Key,
                        ColumnStart: colStart,
                        ColumnEnd: colStart + matchLen,
                        SymbolId: row.SymbolId));
                    continue;
                }

                // Fallback for legacy rows imported before column_number
                // existed: walk the line text forward to find the name.
                var quoted = "\"" + row.Name + "\"";
                var cursor = cursors.TryGetValue(row.Name!, out var c) ? c : 0;
                int idx;
                int fallbackLen;
                idx = lineText.IndexOf(quoted, cursor, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    fallbackLen = quoted.Length;
                }
                else
                {
                    idx = Services.Al.AlGoToDefinitionLocator.IndexOfWord(lineText, row.Name!, cursor);
                    if (idx < 0) continue;
                    fallbackLen = row.Name!.Length;
                }
                cursors[row.Name!] = idx + fallbackLen;
                result.Add(new CodeViewerResolvable(
                    Line: byLine.Key,
                    ColumnStart: idx + 1,
                    ColumnEnd: idx + 1 + fallbackLen,
                    SymbolId: row.SymbolId));
            }
        }

        // Second pass: `extends_target` rows. The importer doesn't stamp
        // a line/column on them (the extends target sits in the object
        // header, not somewhere in the body), so they fall outside the
        // LineNumber != null filter above. Recover the range by joining
        // each row to its source object's header line and scanning that
        // line for the extends keyword + target name. The user
        // reported the `tableextension … extends "Gen. Journal Line"`
        // base name showing no underline; this is what restores it.
        var extendsRows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.SourceObject!.SourceFileId == fileId
                && r.ReferenceKind == "extends_target"
                && r.SourceObject!.LineNumber > 0
                && r.TargetObjectName != null)
            .Select(r => new
            {
                Line = r.SourceObject!.LineNumber,
                Name = r.TargetObjectName!,
            })
            .ToListAsync(ct);
        foreach (var row in extendsRows)
        {
            if (row.Line < 1 || row.Line > lines.Length) continue;
            var span = Services.Al.AlGoToDefinitionLocator.FindExtendsTargetSpan(lines[row.Line - 1], row.Name);
            if (span is null) continue;
            result.Add(new CodeViewerResolvable(
                Line: row.Line,
                ColumnStart: span.Value.Start,
                ColumnEnd: span.Value.End));
        }

        return result;
    }

    /// <summary>
    /// Describes one symbol for the source viewer's hover card. Read-only and
    /// tenant-scoped through the usual EF query filter; returns null when the
    /// id doesn't resolve inside the caller's org.
    /// </summary>
    public async Task<SymbolCard?> DescribeSymbolAsync(long symbolId, CancellationToken ct = default)
    {
        var symbolRelease = await _db.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Id == symbolId)
            .Select(sym => (int?)sym.Module!.ReleaseId)
            .FirstOrDefaultAsync(ct);
        if (symbolRelease is not null && !await _visibility.ReleaseVisibleAsync(symbolRelease.Value, ct)) return null;

        return await _db.OeModuleSymbols.AsNoTracking()
            .Where(sym => sym.Id == symbolId)
            .Select(sym => new SymbolCard(
                sym.Id,
                sym.Name,
                sym.Kind,
                sym.Signature,
                sym.Object!.Kind,
                sym.Object!.Name,
                sym.Module!.Name,
                sym.Object!.SourceFile!.Path,
                sym.Object!.SourceFileId,
                sym.LineNumber,
                sym.ReturnType,
                sym.Doc))
            .SingleOrDefaultAsync(ct);
    }

    /// <summary>
    /// "Find in this file" — extracts the word at the supplied click position
    /// and returns every line of the same file that contains it as a whole
    /// word, ignoring case.
    /// </summary>
    public async Task<FileWordSearch?> FindInFileAsync(
        long fileId, int line, int column, CancellationToken ct = default)
    {
        if (!await _visibility.FileVisibleAsync(fileId, ct)) return null;
        var content = await GetContentAsync(fileId, ct);
        if (string.IsNullOrEmpty(content)) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;

        var word = click.Word;
        var occurrences = new List<FileWordOccurrence>();
        var lines = OeSourceText.SplitLines(content);
        for (var i = 0; i < lines.Length; i++)
        {
            // AL identifiers are case-insensitive, and a match inside a longer
            // identifier (Insert in InsertRecord) is not the same name (#1262).
            if (ReferenceSessionService.IndexOfWord(lines[i], word, 0) >= 0)
            {
                var trimmed = lines[i].TrimEnd();
                if (trimmed.Length > 200) trimmed = trimmed[..200] + "...";
                occurrences.Add(new FileWordOccurrence(i + 1, trimmed));
            }
        }
        return new FileWordSearch(word, occurrences);
    }
}
