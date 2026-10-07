using System.Security.Cryptography;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Services.Cal;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Ingests one legacy C/AL TXT export (a classic NAV "export all objects" file,
/// Windows-1252 or codepage 850) into the <c>oe_*</c> schema, parallel to
/// <see cref="ReleaseImportService"/> for AL <c>.app</c> packages. Reuses that
/// service's Release lifecycle (<see cref="ReleaseImportService.BeginReleaseAsync"/>
/// creates the <c>ingesting</c> row; this service flips it to <c>ready</c> /
/// <c>failed</c>) and the shared blob store + chunking in
/// <see cref="OeIngestHelpers"/>.
///
/// <para>
/// One synthetic <see cref="OeModule"/> per file. Each object's raw C/AL text is
/// stored as its own source slice so the existing source viewer / outline / diff
/// work unchanged. Because a C/AL export is a full self-contained database, all
/// references resolve within the one module — a single id→name post-pass
/// replaces the AL Phase-2 resolution chain. See
/// <c>.design/object-explorer.md</c> and the C/AL parser under
/// <c>Services/Cal/</c>.
/// </para>
/// </summary>
public sealed class CalImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly ILogger<CalImportService> _logger;

    public CalImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        ILogger<CalImportService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; CalImportService called outside an authenticated request.");

    /// <summary>
    /// Parses the staged C/AL TXT at <paramref name="txtPath"/> (decoded with
    /// <paramref name="encodingName"/> — "850" or "1252") into the existing
    /// <c>ingesting</c> Release <paramref name="releaseId"/>, then flips it to
    /// <c>ready</c>. On any exception the release is stamped <c>failed</c> and
    /// the exception rethrown, matching <see cref="ReleaseImportService.ProcessReleaseAsync"/>.
    /// </summary>
    public async Task<ReleaseImportSummary> ProcessReleaseAsync(
        int releaseId, string txtPath, string encodingName, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);
        var release = await _db.OeReleases.FindAsync(new object?[] { releaseId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} not found for C/AL processing.");

        // A 150 MB+ export runs the chunked writes and the module-wide
        // resolution UPDATEs well past Npgsql's 30 s default command timeout on
        // a resource-constrained DB. This is a background job (the worker's
        // active-duration ceiling is 90 min), so give each command real room —
        // the index added in migration AddCalResolutionIndexes keeps the
        // resolution UPDATEs fast, and this is the safety margin on top.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        try
        {
            // Resolve the encoding *inside* the try: the codepage string comes
            // from the import form and an unknown value makes ResolveEncoding
            // throw. Outside the try that throw stranded the release in
            // `ingesting` and surfaced as a worker crash. See issue #362.
            var encoding = ResolveEncoding(encodingName);
            _logger.LogInformation(
                "Processing C/AL ingest: ReleaseId={ReleaseId} Encoding={Encoding}", release.Id, encoding.WebName);

            var appFileHash = await HashFileAsync(txtPath, ct).ConfigureAwait(false);

            var module = new OeModule
            {
                OrganizationId = orgId,
                ReleaseId = release.Id,
                AppId = DeterministicAppId(orgId, release.Label),
                Name = string.IsNullOrWhiteSpace(release.Label) ? "C/AL Objects" : release.Label,
                Publisher = release.Publisher ?? "Legacy C/AL",
                Version = "NAV",
                DependenciesJson = "[]",
                DependencyCount = 0,
                AppFileHash = appFileHash,
                CreatedAt = DateTime.UtcNow,
            };
            _db.OeModules.Add(module);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            long moduleId = module.Id;

            int objectsImported = 0, referencesImported = 0, filesImported = 0;
            string? versionList = null;
            var pendingContent = new Dictionary<string, (string Content, int Length, int LineCount)>(StringComparer.Ordinal);
            int pendingObjects = 0;

            using (var fs = File.OpenRead(txtPath))
            {
                foreach (var block in CalObjectSplitter.Split(fs, encoding,
                    w => _logger.LogWarning("C/AL splitter: {Warning}", w)))
                {
                    ct.ThrowIfCancellationRequested();
                    var parsed = CalObjectParser.Parse(block);
                    versionList ??= parsed.VersionList;

                    EmitObject(orgId, moduleId, module.AppId, block, parsed,
                        pendingContent, ref referencesImported);
                    objectsImported++;
                    filesImported++;
                    pendingObjects++;

                    if (pendingObjects >= OeIngestHelpers.ObjectChunkSize)
                    {
                        await FlushAsync(pendingContent, ct).ConfigureAwait(false);
                        pendingContent.Clear();
                        pendingObjects = 0;
                    }
                }
            }
            if (pendingObjects > 0)
                await FlushAsync(pendingContent, ct).ConfigureAwait(false);

            // Stamp the parsed Version List on the module (e.g. "NAVW114.49,NAVDK14.49").
            if (!string.IsNullOrWhiteSpace(versionList))
            {
                await _db.OeModules.Where(m => m.Id == moduleId)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.Version, versionList!.Trim()), ct)
                    .ConfigureAwait(false);
            }

            await ResolveTargetsByIdAsync(moduleId, ct).ConfigureAwait(false);

            _db.ChangeTracker.Clear();
            var ready = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Release {release.Id} disappeared mid-ingest.");
            ready.Status = "ready";
            ready.UpdatedAt = DateTime.UtcNow;

            var totalsRow = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => f.Module!.ReleaseId == release.Id)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.FileContent!.ContentLength) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            ready.SourceFileCount = totalsRow?.Count ?? 0;
            ready.SourceContentLength = totalsRow?.Length ?? 0;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Completed C/AL ingest: ReleaseId={ReleaseId} Objects={Objects} References={References} Files={Files}",
                release.Id, objectsImported, referencesImported, filesImported);

            return new ReleaseImportSummary(
                ReleaseId: release.Id,
                ModulesImported: 1,
                ModulesSkipped: 0,
                ObjectsImported: objectsImported,
                ReferencesImported: referencesImported,
                SourceFilesImported: filesImported,
                TranslationsImported: 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "C/AL ingest failed: ReleaseId={ReleaseId}", release.Id);
            _db.ChangeTracker.Clear();
            var failed = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false);
            if (failed is not null)
            {
                failed.Status = "failed";
                failed.StatusMessage = ex.Message;
                failed.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Backfill path (#291): repopulates <c>oe_module_system_references</c> for
    /// an already-imported C/AL release by re-parsing each object's stored
    /// source slice (no original TXT needed) and re-running the call-site walker
    /// in system-references-only mode. Idempotent — deletes the release's
    /// existing system-reference rows first — and leaves
    /// <c>oe_module_references</c> and the source untouched. Routed here (rather
    /// than the AL path) by the worker when the release's files are C/AL slices.
    /// </summary>
    public async Task BackfillSystemReferencesAsync(int releaseId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();

        var preview = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Status, r.DeletedAt })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (preview is null || preview.DeletedAt is not null
            || !string.Equals(preview.Status, "ready", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Release {releaseId} isn't ready to backfill (status = {preview?.Status ?? "missing"}).");
        }

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        await _db.Database.ExecuteSqlRawAsync(
            "DELETE FROM oe_module_system_references WHERE module_id IN (SELECT id FROM oe_modules WHERE release_id = {0});",
            new object[] { releaseId }, ct).ConfigureAwait(false);

        var moduleAppId = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .ToDictionaryAsync(m => m.Id, m => m.AppId, ct).ConfigureAwait(false);

        // Page the objects in chunks rather than one FirstOrDefaultAsync (with two
        // Include chains) + SaveChangesAsync per object — that was tens of
        // thousands of round-trips on a full DB. Match the main path's
        // ObjectChunkSize. See issue #384.
        var objectIds = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Module!.ReleaseId == releaseId && o.SourceFileId != null)
            .Select(o => o.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        int emitted = 0;
        foreach (var chunk in objectIds.Chunk(OeIngestHelpers.ObjectChunkSize))
        {
            ct.ThrowIfCancellationRequested();
            // Tracked (not AsNoTracking): EmitBodyReferences attaches new system-
            // reference rows that navigate to the loaded object, so the object's
            // include graph (SourceFile → shared FileContent) must be tracked
            // Unchanged or EF treats it as new and re-inserts the content blob.
            // The tracker holds one chunk's objects and is cleared per chunk.
            var objs = await _db.OeModuleObjects
                .Include(o => o.SourceFile!).ThenInclude(f => f.FileContent!)
                .Include(o => o.Symbols)
                .Where(o => chunk.Contains(o.Id))
                .ToListAsync(ct).ConfigureAwait(false);

            foreach (var obj in objs)
            {
                ct.ThrowIfCancellationRequested();

                if (obj?.SourceFile?.FileContent?.Content is not { Length: > 0 } content)
                {
                    continue;
                }
                var moduleId = obj.ModuleId;
                var appId = moduleAppId.TryGetValue(moduleId, out var ai) ? ai : Guid.Empty;

                // Re-derive the parsed object from the stored slice. UTF-8
                // round-trips the already-decoded string losslessly, so the
                // splitter/parser reproduce the original structure (and line
                // numbers, which we match back to the existing symbol rows).
                CalParsedObject parsed;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(content)))
                {
                    var block = CalObjectSplitter.Split(ms, Encoding.UTF8, _ => { }).FirstOrDefault();
                    if (block is null) continue;
                    parsed = CalObjectParser.Parse(block);
                }

                var symByLine = new Dictionary<int, OeModuleSymbol>();
                foreach (var s in obj.Symbols)
                {
                    if (s.Kind is "procedure" or "local_procedure" or "trigger")
                        symByLine[s.LineNumber] = s;
                }

                var ownerId = parsed.ObjectId;
                CalTypeRef? recRef = parsed.Kind == "table"
                    ? new CalTypeRef("table", parsed.ObjectId)
                    : parsed.Kind == "page" && int.TryParse(parsed.SourceTableId, out var stid)
                        ? new CalTypeRef("table", stid)
                        : null;
                var globalVars = BuildVarMap(parsed.Globals);

                foreach (var p in parsed.Procedures)
                {
                    if (!symByLine.TryGetValue(p.LineNumber, out var sym)) continue;
                    EmitBodyReferences(orgId, moduleId, appId, obj, sym, p.Body, p.BodyLine, p.BodyColumn,
                        globalVars, p.Parameters, p.Locals, parsed.Kind, ownerId, recRef, ref emitted,
                        systemReferencesOnly: true);
                }
                foreach (var t in parsed.Triggers)
                {
                    if (!symByLine.TryGetValue(t.LineNumber, out var sym)) continue;
                    EmitBodyReferences(orgId, moduleId, appId, obj, sym, t.Body, t.BodyLine, t.BodyColumn,
                        globalVars, Array.Empty<CalVariable>(), t.Locals, parsed.Kind, ownerId,
                        TriggerRec(t, recRef), ref emitted,
                        systemReferencesOnly: true);
                }
                foreach (var ex in parsed.Expressions)
                {
                    EmitBodyReferences(orgId, moduleId, appId, obj, null, ex.Text, ex.LineNumber, ex.Column,
                        globalVars, Array.Empty<CalVariable>(), Array.Empty<CalVariable>(), parsed.Kind, ownerId,
                        ex.RecTableId is int exId ? new CalTypeRef("table", exId) : recRef, ref emitted,
                        systemReferencesOnly: true);
                }
            }

            // One save per chunk rather than per object (#384).
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Backfilled system references (C/AL): ReleaseId={ReleaseId} Emitted={Count}", releaseId, emitted);
    }

    /// <summary>Builds the entity rows for one parsed object and queues its source slice for the blob store.</summary>
    private void EmitObject(
        int orgId, long moduleId, Guid appId,
        CalObjectBlock block, CalParsedObject parsed,
        Dictionary<string, (string Content, int Length, int LineCount)> pendingContent,
        ref int referencesImported)
    {
        var content = block.RawText;
        var hash = OeIngestHelpers.HashHex(content);
        var lineCount = OeIngestHelpers.CountLines(content);
        pendingContent[hash] = (content, content.Length, lineCount);

        var file = new OeModuleFile
        {
            OrganizationId = orgId,
            ModuleId = moduleId,
            Path = SlicePath(parsed.Kind, parsed.ObjectId, parsed.Name),
            ContentHash = hash,
            LineCount = lineCount,
        };
        _db.OeModuleFiles.Add(file);

        var obj = new OeModuleObject
        {
            OrganizationId = orgId,
            ModuleId = moduleId,
            Kind = parsed.Kind,
            ObjectId = parsed.ObjectId,
            Name = parsed.Name,
            // Per-object C/SIDE Version List (issue #271). Trimmed; null when blank.
            VersionList = string.IsNullOrWhiteSpace(parsed.VersionList) ? null : parsed.VersionList.Trim(),
            // Numeric source-table id is stored verbatim now and resolved to the
            // table name in ResolveTargetsByIdAsync (mirrors the AL path).
            SourceTableName = parsed.SourceTableId,
            SourceFile = file,
            LineNumber = 1,
        };
        _db.OeModuleObjects.Add(obj);

        foreach (var f in parsed.Fields)
        {
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                Object = obj,
                Kind = "table_field",
                Name = f.Name,
                Signature = f.DataType,
                FieldId = f.Id,
                LineNumber = f.LineNumber,
            });
        }

        foreach (var pf in parsed.PageFields)
        {
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                Object = obj,
                Kind = "page_field",
                Name = pf.Name,
                LineNumber = pf.LineNumber,
            });
        }

        // Shared call-site scope for this object: globals + Rec binding + the
        // owner itself (for bare self-calls). Built once; each body adds its own
        // params / locals on top.
        var ownerId = parsed.ObjectId;
        CalTypeRef? recRef = parsed.Kind == "table"
            ? new CalTypeRef("table", parsed.ObjectId)
            : parsed.Kind == "page" && int.TryParse(parsed.SourceTableId, out var stid)
                ? new CalTypeRef("table", stid)
                : null;
        var globalVars = BuildVarMap(parsed.Globals);

        foreach (var p in parsed.Procedures)
        {
            var procSym = new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                Object = obj,
                Kind = p.IsLocal ? "local_procedure" : "procedure",
                Name = p.Name,
                Signature = p.Signature,
                ReturnType = p.ReturnType,
                LineNumber = p.LineNumber,
                EndLine = p.EndLine,
            };
            _db.OeModuleSymbols.Add(procSym);
            EmitBodyReferences(orgId, moduleId, appId, obj, procSym, p.Body, p.BodyLine, p.BodyColumn,
                globalVars, p.Parameters, p.Locals, parsed.Kind, ownerId, recRef, ref referencesImported);
        }

        foreach (var t in parsed.Triggers)
        {
            var trigSym = new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                Object = obj,
                Kind = "trigger",
                Name = t.Name,
                LineNumber = t.LineNumber,
                EndLine = CalScanEndLine(t),
            };
            _db.OeModuleSymbols.Add(trigSym);
            EmitBodyReferences(orgId, moduleId, appId, obj, trigSym, t.Body, t.BodyLine, t.BodyColumn,
                globalVars, Array.Empty<CalVariable>(), t.Locals, parsed.Kind, ownerId,
                TriggerRec(t, recRef), ref referencesImported);
        }

        // A report data item's DataItemTable / an xmlport element's SourceTable
        // binds an object as declaratively as a `Record 18` global does, so it
        // emits the same variable_type reference (#713).
        foreach (var or in parsed.ObjectRefs)
        {
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                SourceObject = obj,
                TargetAppId = appId,
                TargetObjectKind = or.Kind,
                TargetObjectId = or.TargetId,
                TargetObjectName = string.Empty,   // resolved in the id post-pass
                ReferenceKind = "variable_type",
                LineNumber = or.LineNumber,
            });
            referencesImported++;
        }

        // A column's / xmlport field's SourceExpr is executable C/AL: walk it
        // with the owning data item's Rec so `"No."` and `MyCodeunit.Calc` land
        // as references. It hangs off no symbol, so the row is object-scoped.
        foreach (var ex in parsed.Expressions)
        {
            EmitBodyReferences(orgId, moduleId, appId, obj, null, ex.Text, ex.LineNumber, ex.Column,
                globalVars, Array.Empty<CalVariable>(), Array.Empty<CalVariable>(), parsed.Kind, ownerId,
                ex.RecTableId is int exId ? new CalTypeRef("table", exId) : recRef, ref referencesImported);
        }

        foreach (var g in parsed.Globals)
        {
            var targetKind = g.TypeKeyword is not null
                && CalObjectKinds.ObjectTypeKeywordToKind.TryGetValue(g.TypeKeyword, out var tk)
                ? tk : null;

            _db.OeModuleVariables.Add(new OeModuleVariable
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                Object = obj,
                Name = g.Name,
                TypeKeyword = g.TypeKeyword,
                TypeName = g.TypeName,
                TargetAppId = targetKind is not null ? appId : null,
                TargetObjectKind = targetKind,
                TargetObjectId = g.TargetObjectId,
                LineNumber = g.LineNumber,
                ColumnStart = g.ColumnStart,
                ColumnEnd = g.ColumnEnd,
            });

            // A global typed to another object is a declarative variable_type
            // reference, resolvable by id within this release.
            if (targetKind is not null && g.TargetObjectId is int targetId)
            {
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = moduleId,
                    SourceObject = obj,
                    TargetAppId = appId,
                    TargetObjectKind = targetKind,
                    TargetObjectId = targetId,
                    TargetObjectName = string.Empty,   // resolved in the id post-pass
                    ReferenceKind = "variable_type",
                    LineNumber = g.LineNumber,
                });
                referencesImported++;
            }
        }
    }

    /// <summary>
    /// Runs the C/AL call-site walker over one procedure / trigger body and
    /// emits its <c>method_call</c> / <c>field_access</c> references, stamped
    /// with the calling symbol so the forward-edge "what does this call?" query
    /// works. Target names are filled in by <see cref="ResolveTargetsByIdAsync"/>.
    /// </summary>
    private void EmitBodyReferences(
        int orgId, long moduleId, Guid appId,
        OeModuleObject obj, OeModuleSymbol? sourceSym, string body, int bodyLine, int bodyColumn,
        IReadOnlyDictionary<string, CalTypeRef> globals,
        IEnumerable<CalVariable> parameters, IEnumerable<CalVariable> locals,
        string ownerKind, int ownerId, CalTypeRef? recRef, ref int referencesImported,
        bool systemReferencesOnly = false)
    {
        if (string.IsNullOrEmpty(body)) return;

        var scope = new CalExtractScope { OwnerKind = ownerKind, OwnerId = ownerId, Rec = recRef };
        foreach (var kv in globals) scope.Variables[kv.Key] = kv.Value;
        foreach (var v in parameters) AddTypedVar(scope.Variables, v);
        foreach (var v in locals) AddTypedVar(scope.Variables, v);

        var result = CalReferenceExtractor.Extract(body, scope);
        // Normal call-site references. Skipped on the backfill path (#291),
        // which only repopulates oe_module_system_references.
        if (!systemReferencesOnly)
        foreach (var r in result.References)
        {
            // The walker numbers lines from 1 within the body text it was given;
            // shift to slice-relative so the line lands on the real call site
            // (bodyLine is the slice line of the body's BEGIN = walker line 1).
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                SourceObject = obj,
                SourceSymbol = sourceSym,
                TargetAppId = appId,
                TargetObjectKind = r.TargetKind,
                TargetObjectId = r.TargetId,
                // A static object receiver (CODEUNIT::"Sales-Post") names its
                // target but carries no id; everything else is the other way
                // round. The post-pass fills in whichever half is missing.
                TargetObjectName = r.TargetName ?? string.Empty,
                TargetMemberName = r.MemberName,
                TargetMemberKind = r.MemberKind,
                ReferenceKind = r.ReferenceKind,
                LineNumber = bodyLine + r.Line - 1,
                ColumnNumber = FileColumn(r.Line, r.Column, bodyColumn),
            });
            referencesImported++;
        }

        // System / built-in method calls (INSERT, MODIFY, SETRANGE, …) go to
        // the separate oe_module_system_references table — see #279. Within a
        // single C/AL export the receiver id is enough; the target name stays
        // empty (the find-system-references query matches by id).
        foreach (var sr in result.SystemReferences)
        {
            _db.OeModuleSystemReferences.Add(new OeModuleSystemReference
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                SourceObject = obj,
                SourceSymbol = sourceSym,
                TargetAppId = appId,
                TargetObjectKind = sr.TargetKind,
                TargetObjectId = sr.TargetId,
                TargetObjectName = string.Empty,
                SystemMethodName = sr.MemberName ?? string.Empty,
                ReferenceKind = sr.ReferenceKind,
                LineNumber = bodyLine + sr.Line - 1,
                ColumnNumber = FileColumn(sr.Line, sr.Column, bodyColumn),
            });
            referencesImported++;
        }
    }

    /// <summary>
    /// Shifts a walker column onto the file. The walker numbers columns from 1
    /// within the body text it was handed, and that text starts at the body's
    /// <c>BEGIN</c> (or at a SourceExpr's first character) — so only its first
    /// line is offset; every later line already starts at file column 1. Before
    /// #713 the offset was dropped, and code written on the <c>BEGIN</c> line
    /// pointed at the wrong place.
    /// </summary>
    private static int FileColumn(int walkerLine, int walkerColumn, int bodyColumn)
        => walkerLine == 1 ? bodyColumn + walkerColumn - 1 : walkerColumn;

    /// <summary>
    /// The <c>Rec</c> binding for one trigger: a report data item's or xmlport
    /// element's own table when it has one, otherwise the owner object's.
    /// </summary>
    private static CalTypeRef? TriggerRec(CalTrigger t, CalTypeRef? ownerRec)
        => t.RecTableId is int id ? new CalTypeRef("table", id) : ownerRec;

    private static Dictionary<string, CalTypeRef> BuildVarMap(IEnumerable<CalVariable> vars)
    {
        var d = new Dictionary<string, CalTypeRef>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vars) AddTypedVar(d, v);
        return d;
    }

    private static void AddTypedVar(Dictionary<string, CalTypeRef> d, CalVariable v)
    {
        if (v.TargetObjectId is int id && v.TypeKeyword is not null
            && CalObjectKinds.ObjectTypeKeywordToKind.TryGetValue(v.TypeKeyword, out var k))
            d[v.Name] = new CalTypeRef(k, id);
    }

    private async Task FlushAsync(
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> pendingContent,
        CancellationToken ct)
    {
        await OeIngestHelpers.SaveWithFileContentsAsync(_db, pendingContent, ct).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Resolves the numeric object ids that C/AL references carry into target
    /// names, within the single module. Set-based UPDATEs so a full-database
    /// export (tens of thousands of vars/refs) resolves without loading rows.
    /// </summary>
    private async Task ResolveTargetsByIdAsync(long moduleId, CancellationToken ct)
    {
        var p = new object[] { moduleId };

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_references r SET target_object_name = o.name " +
            "FROM oe_module_objects o " +
            "WHERE r.module_id = {0} AND o.module_id = {0} " +
            "AND r.target_object_id IS NOT NULL " +
            "AND o.object_id = r.target_object_id AND o.kind = r.target_object_kind " +
            "AND (r.target_object_name = '' OR r.target_object_name IS NULL)", p, ct).ConfigureAwait(false);

        // The mirror image for static object receivers (CODEUNIT::"Sales-Post"),
        // which name their target but carry no id — see #713.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_references r SET target_object_id = o.object_id " +
            "FROM oe_module_objects o " +
            "WHERE r.module_id = {0} AND o.module_id = {0} " +
            "AND r.target_object_id IS NULL AND r.target_object_name <> '' " +
            "AND o.name = r.target_object_name AND o.kind = r.target_object_kind", p, ct).ConfigureAwait(false);

        // Paren-less member access (`Cust.MyProc;`) reads exactly like a field
        // read in C/AL, so the walker emits field_access for both. Now that the
        // module's symbols are stored, a name that matches a procedure on the
        // target object is a call — reclassify those rows (issue #712).
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_references r " +
            "SET reference_kind = 'method_call', target_member_kind = 'procedure' " +
            "FROM oe_module_objects o JOIN oe_module_symbols s ON s.object_id = o.id " +
            "WHERE r.module_id = {0} AND o.module_id = {0} " +
            "AND r.reference_kind = 'field_access' AND r.target_member_name IS NOT NULL " +
            "AND r.target_object_id IS NOT NULL " +
            "AND o.object_id = r.target_object_id AND o.kind = r.target_object_kind " +
            "AND s.kind IN ('procedure', 'local_procedure') " +
            "AND lower(s.name) = lower(r.target_member_name)", p, ct).ConfigureAwait(false);

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_variables v SET target_object_name = o.name " +
            "FROM oe_module_objects o " +
            "WHERE v.module_id = {0} AND o.module_id = {0} " +
            "AND v.target_object_id IS NOT NULL " +
            "AND o.object_id = v.target_object_id AND o.kind = v.target_object_kind", p, ct).ConfigureAwait(false);

        // Pages store SourceTable as a bare numeric id; resolve to the table name
        // so Rec-field resolution and the outline read a name like the AL path.
        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_objects pg SET source_table_name = t.name " +
            "FROM oe_module_objects t " +
            "WHERE pg.module_id = {0} AND t.module_id = {0} AND t.kind = 'table' " +
            "AND pg.source_table_name ~ '^[0-9]+$' " +
            "AND t.object_id = CAST(pg.source_table_name AS int)", p, ct).ConfigureAwait(false);

        // Platform virtual tables (Field, Object, Date, AllObj, …) are
        // referenced by id but never shipped as objects in the export, so the
        // joins above can't name them. Resolve from the NAV-specific id→name map
        // (NOT the BC one — the id space was renumbered) so they stop showing as
        // unresolved. Set-based via unnest: one UPDATE per target table.
        var ids = CalVirtualTables.Ids;
        var names = CalVirtualTables.Names;
        var vt = new object[] { moduleId, ids, names };

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_references r SET target_object_name = v.name " +
            "FROM unnest({1}::int[], {2}::text[]) AS v(id, name) " +
            "WHERE r.module_id = {0} AND r.target_object_kind = 'table' AND r.target_object_id = v.id " +
            "AND (r.target_object_name = '' OR r.target_object_name IS NULL)", vt, ct).ConfigureAwait(false);

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_variables mv SET target_object_name = v.name " +
            "FROM unnest({1}::int[], {2}::text[]) AS v(id, name) " +
            "WHERE mv.module_id = {0} AND mv.target_object_kind = 'table' AND mv.target_object_id = v.id " +
            "AND (mv.target_object_name = '' OR mv.target_object_name IS NULL)", vt, ct).ConfigureAwait(false);

        await _db.Database.ExecuteSqlRawAsync(
            "UPDATE oe_module_objects o SET source_table_name = v.name " +
            "FROM unnest({1}::int[], {2}::text[]) AS v(id, name) " +
            "WHERE o.module_id = {0} AND o.source_table_name = v.id::text", vt, ct).ConfigureAwait(false);
    }

    private static int? CalScanEndLine(CalTrigger t)
        => t.Body.Length > 0 ? t.BodyLine + CountNewlines(t.Body) : null;

    private static int CountNewlines(string s)
    {
        int n = 0;
        foreach (var c in s) if (c == '\n') n++;
        return n;
    }

    /// <summary>A stable, idempotent synthetic AppId per (org, release label).</summary>
    private static Guid DeterministicAppId(int orgId, string label)
    {
        // MD5 is used purely to fold the (org, label) tuple into a stable
        // 16-byte GUID — it is NOT a security/integrity use, and the value
        // already-persisted AppIds depend on, so the algorithm can't change
        // without re-keying existing C/AL imports. Suppress CA5351 here only.
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"cal:{orgId}:{label}"));
#pragma warning restore CA5351
        return new Guid(bytes);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Resolves the C/AL export codepage to an <see cref="Encoding"/>, defaulting
    /// to CP850 when unset. The classic Windows client only ever exports OEM 850
    /// or Windows-1252, so those are the offered choices — but the value arrives
    /// as a raw form string, so an unknown codepage is translated to a clean
    /// <see cref="InvalidDataException"/> rather than letting
    /// <see cref="Encoding.GetEncoding(string)"/>'s framework exception escape.
    /// Callers run this inside their failure handler so a bad value stamps the
    /// release <c>failed</c> instead of stranding it. See issue #362.
    /// </summary>
    private static Encoding ResolveEncoding(string name)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (string.IsNullOrWhiteSpace(name)) return Encoding.GetEncoding(850);
        try
        {
            return int.TryParse(name, out var cp)
                ? Encoding.GetEncoding(cp)
                : Encoding.GetEncoding(name);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Unknown C/AL codepage '{name}'. Use 850 (OEM, the classic finsql default) or 1252 (Windows-1252).");
        }
    }

    private static string SlicePath(string kind, int id, string name)
    {
        var title = char.ToUpperInvariant(kind[0]) + kind[1..];
        var safeName = name;
        foreach (var bad in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(bad, '-');
        return $"CAL/{title}/{id} - {safeName}.txt";
    }
}
