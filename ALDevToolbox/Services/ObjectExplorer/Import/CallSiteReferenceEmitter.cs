using ALDevToolbox.Data;
using ALDevToolbox.Services.Al;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Phase 2 of the Object Explorer ingest: runs the AL reference
/// extractor over an already-ingested release's source files and writes
/// the resolved call-site references (and the system-reference rows)
/// back. <see cref="ReleaseImportService"/> owns the release lifecycle
/// and delegates this pass here — it is entered from four paths
/// (process, amend, backfill, re-extract) and is by far the heaviest
/// single step of the import.
///
/// See <c>.design/object-explorer.md</c>.
/// </summary>
public class CallSiteReferenceEmitter
{
    private readonly AppDbContext _db;
    private readonly ILogger<CallSiteReferenceEmitter> _logger;

    public CallSiteReferenceEmitter(
        AppDbContext db,
        ILogger<CallSiteReferenceEmitter> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ── Phase-2 call-site extraction ───────────────────────────────────

    /// <summary>
    /// Runs <see cref="ALDevToolbox.Services.Al.AlReferenceExtractor"/>
    /// over every source file in the freshly-imported release and emits
    /// one <c>method_call</c> or <c>field_access</c> reference per
    /// resolved member access. Single-pass over the release's files; the
    /// type + member catalogs are built once up-front from data the
    /// per-module loop already wrote.
    ///
    /// Cross-release shadowing isn't handled here — a DK Core file
    /// calling <c>Customer.Insert()</c> against a Customer table that
    /// lives in a parent Base App release would drop the reference.
    /// In practice users tend to import every module they care about
    /// into one release (the BC DVD convention); cross-release call
    /// sites can be added later by reusing the recursive-CTE chain walk.
    /// </summary>
    /// <summary>
    /// Full Phase-2 pass: emits both the resolved <c>method_call</c> /
    /// <c>field_access</c> references and the system-reference rows.
    /// Returns the number of reference rows written so the caller can
    /// fold it into its import tally.
    /// </summary>
    public Task<int> EmitAsync(int orgId, int releaseId, CancellationToken ct)
        => EmitCoreAsync(orgId, releaseId, ct, systemReferencesOnly: false);

    /// <summary>
    /// Same pass, but only the system-reference half is persisted — the
    /// backfill path re-runs the extraction over an already-ingested
    /// release whose ordinary references are still in place.
    /// </summary>
    public Task<int> EmitSystemReferencesOnlyAsync(int orgId, int releaseId, CancellationToken ct)
        => EmitCoreAsync(orgId, releaseId, ct, systemReferencesOnly: true);

    private async Task<int> EmitCoreAsync(
        int orgId, int releaseId, CancellationToken ct,
        bool systemReferencesOnly)
    {
        _db.ChangeTracker.Clear();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // The resolver must see the whole *visible release chain*, not just
        // the release being imported. A project / third-party release sits
        // on top of a parent (the BC base): its code references base objects
        // and base table fields (`Rec.Priority` on a Prod. Order Line
        // tableextension) that physically live in the parent Release. Scoping
        // the catalogs to this release alone left those member lookups
        // unresolved, so the references were silently dropped at import and
        // never showed up in find-references. Resolve the chain's winning
        // modules once (same recursive-CTE + app-id shadowing the query side
        // uses, see ReleaseAncestrySql) and build every catalog from that set.
        var chainModuleIds = await GetVisibleChainModuleIdsAsync(releaseId, ct);

        // (1) Build the type catalog: every object in the visible chain keyed
        // by name (case-insensitive). Multiple objects can share a name
        // across kinds and modules — e.g. Microsoft's Subscription
        // Billing app declares a tableextension named "Sales Header"
        // alongside the actual Sales Header table in Base Application.
        // We store every candidate per name; the resolver chooses by
        // visibility + non-extension preference at lookup time.
        //
        // Identity-keyed object-id lookup lets the post-extraction
        // TargetSymbolId stamp (and the resolver's member lookup) find
        // the right oe_module_objects row even when multiple objects
        // share a name. The composite key (AppId, Kind, Name) is the
        // catalog's canonical identity.
        var typeRows = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => chainModuleIds.Contains(o.ModuleId))
            .Select(o => new
            {
                o.Id,
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
                o.SourceTableName,
                o.ObsoleteState,
                o.ExtendsObjectName,
                o.ExtendsAppId,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var typesByName = new Dictionary<string, List<ALDevToolbox.Services.Al.AlTypeRef>>(StringComparer.OrdinalIgnoreCase);
        var typesByObjectId = new Dictionary<long, ALDevToolbox.Services.Al.AlTypeRef>();
        // Catalog lookup by AL object id (the numeric id developers write
        // in `Record 380` / `Codeunit 1060`), per kind. Multiple modules
        // can share a (kind, id) pair across releases / app boundaries
        // — keep them all and let the resolver tiebreak by visibility,
        // same as the by-name path. See <see cref="CatalogResolver.ResolveTypeByObjectId"/>.
        var typesByAlObjectId = new Dictionary<(string Kind, int ObjectId), List<ALDevToolbox.Services.Al.AlTypeRef>>();
        var objectIdByIdentity = new Dictionary<(Guid AppId, string Kind, string Name), long>(
            new ObjectIdentityComparer());
        // Per-object source-table lookup so AlPageStructure can
        // resolve cross-page SubPageLink / RunPageLink field
        // references in step 5 — the LHS field name belongs to the
        // TARGET page's source table, not the current page's Rec.
        // Only populated for objects that have a SourceTable
        // (page / pageextension / requestpage / report-dataitem in
        // practice); other kinds skip the dictionary entry.
        var sourceTablesByObjectId = new Dictionary<long, string>();
        // Interface inheritance: interface "B" extends "A" means a
        // member lookup on B walks up to A's members too. Keyed by
        // owner DB id (the same key shape `_members` uses) so the
        // resolver can chain TryGetValue calls. Only interfaces are
        // populated — tableextensions / pageextensions / etc. use the
        // separate `extensionsByBaseName` path with reversed semantics
        // (extension's members get attached to the base, not inherited
        // by the extension).
        var interfaceExtendsByOwnerId = new Dictionary<long, (Guid AppId, string Name)>();
        // Side-map for obsolete-state lookup on candidate ranking.
        // Keyed by the catalog's identity triple so the resolver can
        // ask "is this exact (AppId, Kind, Name) marked Pending /
        // Removed?" without bloating AlTypeRef. Only populated for
        // objects with the property set; lookups returning empty
        // string mean "non-obsolete" (the default `No` state).
        var obsoleteStateByIdentity = new Dictionary<(Guid AppId, string Kind, string Name), string>(
            new ObjectIdentityComparer());
        foreach (var t in typeRows)
        {
            var typeRef = new ALDevToolbox.Services.Al.AlTypeRef(t.AppId, t.Kind, t.ObjectId, t.Name);
            typesByObjectId[t.Id] = typeRef;
            if (!typesByName.TryGetValue(t.Name, out var list))
            {
                list = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                typesByName[t.Name] = list;
            }
            list.Add(typeRef);
            if (t.ObjectId is int alId && alId > 0)
            {
                var idKey = (t.Kind, alId);
                if (!typesByAlObjectId.TryGetValue(idKey, out var idList))
                {
                    idList = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                    typesByAlObjectId[idKey] = idList;
                }
                idList.Add(typeRef);
            }
            objectIdByIdentity[(t.AppId, t.Kind, t.Name)] = t.Id;
            if (!string.IsNullOrEmpty(t.SourceTableName))
            {
                sourceTablesByObjectId[t.Id] = t.SourceTableName;
            }
            if (!string.IsNullOrEmpty(t.ObsoleteState))
            {
                obsoleteStateByIdentity[(t.AppId, t.Kind, t.Name)] = t.ObsoleteState;
            }
            if (string.Equals(t.Kind, "interface", StringComparison.OrdinalIgnoreCase)
                && t.ExtendsAppId is Guid baseAppId
                && !string.IsNullOrEmpty(t.ExtendsObjectName))
            {
                interfaceExtendsByOwnerId[t.Id] = (baseAppId, t.ExtendsObjectName!);
            }
        }

        // (1b) Synthesise catalog entries for BC platform virtual tables.
        // These live in the AL runtime (ids 2000000001 – 2000000999 reserved),
        // not in any module's symbol package, but extensions reference them
        // freely: `TempFieldSet: Record Field;`, `User.SetRange("User Name", X);`.
        // Without synthesis the type lookup fails and the chain is logged
        // as an unresolved variable type — even though the reference is
        // legitimate runtime API. Stamped with PlatformAppId (Guid.Empty)
        // so visibility-aware resolvers can recognise them; the resolver
        // already treats PlatformAppId as visible to everyone via the
        // FoundationalAppNames-style implicit-visibility rule (see below).
        foreach (var vt in ReleaseImportAllowLists.PlatformVirtualTables)
        {
            var typeRef = new ALDevToolbox.Services.Al.AlTypeRef(ReleaseImportAllowLists.PlatformAppId, "table", vt.Id, vt.Name);
            if (!typesByName.TryGetValue(vt.Name, out var list))
            {
                list = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                typesByName[vt.Name] = list;
            }
            list.Add(typeRef);
            var vtIdKey = ("table", vt.Id);
            if (!typesByAlObjectId.TryGetValue(vtIdKey, out var vtIdList))
            {
                vtIdList = new List<ALDevToolbox.Services.Al.AlTypeRef>();
                typesByAlObjectId[vtIdKey] = vtIdList;
            }
            vtIdList.Add(typeRef);
            // No oe_module_objects.Id for synthetic entries — typesByObjectId
            // and objectIdByIdentity stay unaugmented (they're keyed off
            // the DB row id, which doesn't exist here). The chain walker
            // only needs typesByName + RecordMethods to resolve calls
            // like `TempFieldSet.GET(...)` against these tables.
        }

        // (2) Member catalog: for each owner Id, list its symbols.
        // Keyed by Id because owner names aren't unique across kinds.
        var memberRows = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => chainModuleIds.Contains(s.ModuleId))
            .Select(s => new
            {
                OwnerId = s.Object!.Id,
                SymbolId = s.Id,
                s.Name,
                s.Kind,
                s.ReturnType,
                s.LineNumber,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var membersByOwner = new Dictionary<long, List<MemberEntry>>();
        // (Owner, LineNumber) → SymbolId. Used by the reference loop to
        // resolve source_symbol_id (the calling procedure / trigger) and
        // by the scope-tracking pass to attach end_line / end_column onto
        // the right symbol row without a name+kind ambiguity dance —
        // line is unique within an object. See issues #180 / #181.
        var symbolIdByOwnerAndLine = new Dictionary<(long OwnerId, int LineNumber), long>();
        foreach (var m in memberRows)
        {
            if (!membersByOwner.TryGetValue(m.OwnerId, out var list))
            {
                list = new List<MemberEntry>();
                membersByOwner[m.OwnerId] = list;
            }
            // ReturnType is the raw "Record Customer" / "Code[20]" string
            // from the symbol package. Pull the AL type name out of it
            // so chained access can resolve through return types.
            var (retKw, retName) = ParseReturnType(m.ReturnType);
            list.Add(new MemberEntry(m.SymbolId, m.Name, m.Kind, retKw, retName));
            if (m.LineNumber > 0)
            {
                symbolIdByOwnerAndLine[(m.OwnerId, m.LineNumber)] = m.SymbolId;
            }
        }

        // (3) Per-object globals from oe_module_variables. Keyed by
        // (objectId, lowered name). Built once; the per-file loop
        // grabs its file's owner-object id and filters.
        var varRows = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => chainModuleIds.Contains(v.Object!.ModuleId))
            .Select(v => new
            {
                OwnerId = v.Object!.Id,
                v.Id,
                v.Name,
                v.TypeKeyword,
                v.TypeName,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var globalsByOwner = new Dictionary<long, Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>>();
        // Per-(owner, lowered name) → variable id lookup so the
        // reference-emit loop can stamp TargetVariableId on
        // variable_use rows (step 6). Built alongside globalsByOwner
        // to share the single varRows scan.
        var variableIdByOwnerAndName = new Dictionary<(long OwnerId, string Name), long>();
        foreach (var v in varRows)
        {
            variableIdByOwnerAndName[(v.OwnerId, v.Name.ToLowerInvariant())] = v.Id;
            if (string.IsNullOrEmpty(v.TypeName)) continue;
            if (!globalsByOwner.TryGetValue(v.OwnerId, out var dict))
            {
                dict = new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase);
                globalsByOwner[v.OwnerId] = dict;
            }
            dict[v.Name] = new ALDevToolbox.Services.Al.ResolvedVariableType(v.TypeKeyword, v.TypeName);
        }

        // (4) Extensions-by-base index: for each tableextension /
        // pageextension / etc., record the base object name it targets
        // plus the extension's own AppId + ObjectId. The resolver
        // consults this map when a member lookup misses on the base —
        // a procedure added via CustomerExt should be findable as a
        // method on Customer-typed receivers, subject to visibility.
        var extRows = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => chainModuleIds.Contains(o.ModuleId))
            .Where(o => o.Kind == "tableextension"
                     || o.Kind == "pageextension"
                     || o.Kind == "reportextension"
                     || o.Kind == "enumextension"
                     || o.Kind == "permissionsetextension")
            .Where(o => o.ExtendsObjectName != null)
            .Select(o => new
            {
                o.Id,
                o.Kind,
                ExtensionAppId = o.Module!.AppId,
                BaseName = o.ExtendsObjectName!,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var extensionsByBaseName = new Dictionary<string, List<ExtensionEntry>>(StringComparer.OrdinalIgnoreCase);
        // Extension owner id → base object owner id. Used by the per-
        // file context builder to merge the base object's global var
        // map into the extension's, so a `DimMgt.GetCombined…` chain
        // inside a tableextension procedure resolves through the base
        // table's DimMgt global rather than firing head-not-a-variable.
        // AL effectively merges extension and base scopes at compile
        // time; without this merge the diagnostic surfaces every base-
        // declared global accessed from an extension procedure
        // (DimMgt, TempPlanningErrorLog, PlanningLineMgt, FilterItem,
        // and others) as a spurious unresolved sample.
        var baseOwnerIdByExtensionOwnerId = new Dictionary<long, long>();
        // (kind, name) → first matching object id, built once from typeRows so
        // the per-extension base lookup below is O(1) instead of re-scanning all
        // objects per extension (quadratic on a full BC release). Keyed on a
        // space-separated kind+name (object kinds never contain a space) with
        // OrdinalIgnoreCase to match the previous per-component case-insensitive
        // comparison; "first wins" preserves the
        // old "first candidate in typeRows order" behaviour. See issue #365.
        static string KindNameKey(string kind, string name) => kind + "\0" + name;
        var objectIdByKindName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in typeRows)
        {
            var key = KindNameKey(t.Kind, t.Name);
            if (!objectIdByKindName.ContainsKey(key)) objectIdByKindName[key] = t.Id;
        }
        foreach (var e in extRows)
        {
            if (!extensionsByBaseName.TryGetValue(e.BaseName, out var list))
            {
                list = new List<ExtensionEntry>();
                extensionsByBaseName[e.BaseName] = list;
            }
            list.Add(new ExtensionEntry(e.ExtensionAppId, e.Id));

            // Map to the base object's owner id. Object kind maps
            // 1:1 (tableextension → table, pageextension → page,
            // reportextension → report); we only bother with the
            // three kinds that have variable scopes — enum and
            // permissionset extensions never own globals.
            var baseKind = e.Kind switch
            {
                "tableextension" => "table",
                "pageextension" => "page",
                "reportextension" => "report",
                _ => null,
            };
            if (baseKind is null) continue;
            // The base may live in a different app than the extension
            // (a Base App tableextension on top of a System App table,
            // a third-party extension on top of Base App). The first
            // candidate with the matching kind + name is the base object —
            // same-app collisions don't happen for `tableextension extends X`
            // because AL forbids extending an object you also declare. Looked
            // up via the prebuilt (kind, name) index rather than re-scanning
            // every object per extension (#365).
            if (objectIdByKindName.TryGetValue(KindNameKey(baseKind, e.BaseName), out var baseId))
            {
                baseOwnerIdByExtensionOwnerId[e.Id] = baseId;
            }
        }

        // (5) Per-module visibility: which AppIds is each module
        // allowed to reach via app.json dependencies (transitively).
        // Object resolution and extension-member lookup both filter
        // through this so a Base App file can't reach into DK Core,
        // a third-party extension can't reach into an unrelated
        // third-party extension, etc.
        var moduleVisibility = await BuildModuleVisibilityAsync(chainModuleIds, ct);

        // Per-module AppId lookup so the resolver can apply same-app
        // preference when multiple candidates match a name. Same query
        // shape BuildModuleVisibilityAsync uses but smaller projection;
        // the cost is one extra trip, kept here so the visibility
        // method's contract stays narrow.
        var moduleAppIdsById = await _db.OeModules.AsNoTracking()
            .Where(m => chainModuleIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.AppId, ct).ConfigureAwait(false);

        // Foundational app ids — the platform / system / system-app /
        // application modules whose tables (Company, User, File, …)
        // Base App code references without an explicit dependency.
        // Matched by Microsoft + name, same way the visibility builder
        // does it; capturing the set here lets the resolver prefer
        // platform candidates over random third-party same-named tables.
        var foundationalAppIds = new HashSet<Guid>(
            await _db.OeModules.AsNoTracking()
                .Where(m => chainModuleIds.Contains(m.Id)
                    && m.Publisher == "Microsoft"
                    && ReleaseImportAllowLists.FoundationalAppNames.Contains(m.Name))
                .Select(m => m.AppId)
                .ToListAsync(ct).ConfigureAwait(false));

        // (6) Per-module resolver cache. All files in the same module
        // share the same visibility set, so build the resolver once
        // and reuse across files.
        var resolversByModule = new Dictionary<long, ALDevToolbox.Services.Al.IAlTypeResolver>();
        ALDevToolbox.Services.Al.IAlTypeResolver ResolverFor(long moduleId)
        {
            if (resolversByModule.TryGetValue(moduleId, out var cached)) return cached;
            moduleVisibility.TryGetValue(moduleId, out var visible);
            moduleAppIdsById.TryGetValue(moduleId, out var ownerAppId);
            var r = new CatalogResolver(
                typesByName, typesByObjectId, typesByAlObjectId, objectIdByIdentity,
                membersByOwner, extensionsByBaseName, interfaceExtendsByOwnerId,
                sourceTablesByObjectId, visible,
                ownerAppId == Guid.Empty ? null : ownerAppId,
                foundationalAppIds, obsoleteStateByIdentity);
            resolversByModule[moduleId] = r;
            return r;
        }

        // (7) Walk every source file. For each, find its owner object
        // (the row in oe_module_objects whose source_file_id matches),
        // build the extract context, run the extractor, and emit
        // ModuleReference rows. Saved in chunks to keep tracker
        // pressure bounded.
        // File metadata WITHOUT the (potentially huge) source blob. Base App
        // alone is thousands of multi-KB .al files; buffering every file's
        // Content in one list held the entire release's source resident for the
        // extraction loop and risked OOM on large releases. We load just the
        // light columns here and pull Content in bounded batches inside the loop
        // (a streaming AsAsyncEnumerable can't be used: the loop SaveChanges'es
        // on this same context, and Npgsql can't write while a reader is open).
        // See issue #364.
        var fileMetas = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .Select(f => new
            {
                f.Id,
                f.Path,
                ModuleId = f.ModuleId,
                ModuleName = f.Module!.Name,
                Owner = _db.OeModuleObjects
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.Id)
                    .Select(o => new
                    {
                        o.Id,
                        o.Kind,
                        o.Name,
                        o.ObjectId,
                        AppId = o.Module!.AppId,
                        o.SourceTableName,
                        o.ExtendsObjectName,
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        int totalEmitted = 0;
        int totalUnresolved = 0;
        int pending = 0;
        // Diagnostic bucket: first N unresolved references seen across all
        // files in this phase. Capped low so a pathological release doesn't
        // bloat the log; the per-file extractor also caps internally so
        // late files still get a chance to contribute samples even when
        // earlier files were noisy. Bumped from 50 → 100 so a single
        // import surfaces enough variety to triage the long tail without
        // requiring re-runs against subsets of the release.
        const int unresolvedLogCap = 100;
        var unresolvedSamples = new List<(string Module, string Path, string Owner, ALDevToolbox.Services.Al.UnresolvedSample Sample)>(unresolvedLogCap);
        // Source content is pulled in bounded batches so only this many files'
        // blobs are resident at once, instead of the whole release (#364).
        const int SourceContentBatchSize = 200;
        var contentById = new Dictionary<long, string>();
        int fileIndex = 0;
        foreach (var file in fileMetas)
        {
            ct.ThrowIfCancellationRequested();

            // Refill the content cache at each batch boundary. AsNoTracking read
            // of just (Id, Content) for the next slice; the dict is replaced so
            // the prior slice's blobs become collectable.
            if (fileIndex % SourceContentBatchSize == 0)
            {
                var batchIds = fileMetas.Skip(fileIndex).Take(SourceContentBatchSize)
                    .Select(m => m.Id).ToList();
                contentById = await _db.OeModuleFiles.AsNoTracking()
                    .Where(f => batchIds.Contains(f.Id))
                    .Select(f => new { f.Id, Content = f.FileContent!.Content })
                    .ToDictionaryAsync(x => x.Id, x => x.Content, ct)
                    .ConfigureAwait(false);
            }
            fileIndex++;

            contentById.TryGetValue(file.Id, out var content);
            if (file.Owner is null || string.IsNullOrEmpty(content)) continue;

            globalsByOwner.TryGetValue(file.Owner.Id, out var globals);

            // Extension owners (tableextension / pageextension /
            // reportextension) reach the base object's global vars
            // through AL's merged-scope semantics — a base table's
            // `DimMgt: Codeunit DimensionManagement;` is callable from
            // any tableextension on top of it. Merge the base's
            // globals UNDER the extension's so extension-side names
            // shadow on collision (AL same-name rules), and leave the
            // map alone for non-extension owners.
            if (baseOwnerIdByExtensionOwnerId.TryGetValue(file.Owner.Id, out var baseOwnerId)
                && globalsByOwner.TryGetValue(baseOwnerId, out var baseGlobals))
            {
                if (globals is null)
                {
                    globals = new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(
                        baseGlobals, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    var merged = new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(
                        baseGlobals, StringComparer.OrdinalIgnoreCase);
                    foreach (var (name, type) in globals) merged[name] = type;
                    globals = merged;
                }
            }
            // For tableextensions, Rec is semantically the BASE TABLE
            // (the extension's columns are merged into the base at
            // runtime). The base table's name lives in ExtendsObjectName
            // — we feed it through OwnerSourceTableName so BuildGlobalScope
            // wires Rec to (Record, base table). ResolveMember on the
            // base table then walks base → visible extensions, which
            // covers all three cases (base-declared, this-extension-
            // declared, sibling-extension-declared).
            var sourceTable = file.Owner.SourceTableName;
            if (string.IsNullOrEmpty(sourceTable) && file.Owner.Kind == "tableextension")
            {
                sourceTable = file.Owner.ExtendsObjectName;
            }
            // Pageextension fallback: PropagateSourceTableToPageExtensionsAsync
            // copies the base page's source_table_name into the
            // extension at import end, but the join is same-release-only
            // and requires the base page's source_table_name to be set.
            // When either misses (cross-release base page, or the base
            // page's source table wasn't extracted), Rec doesn't get
            // wired in BuildGlobalScope and every `Rec.X` chain in the
            // body fires head-not-a-variable. Catch the miss here by
            // looking the base page up via the resolver — which catalogs
            // every page in the current release — and asking for its
            // source-table name through the IAlTypeResolver hook added
            // in step 5.
            if (string.IsNullOrEmpty(sourceTable)
                && file.Owner.Kind == "pageextension"
                && !string.IsNullOrEmpty(file.Owner.ExtendsObjectName))
            {
                var pextResolver = ResolverFor(file.ModuleId);
                var basePage = pextResolver.ResolveTypeByName(file.Owner.ExtendsObjectName!, "Page");
                if (basePage is not null)
                {
                    sourceTable = pextResolver.ResolveSourceTableName(basePage);
                }
            }
            var ctx = new ALDevToolbox.Services.Al.AlExtractContext(
                OwnerKind: file.Owner.Kind,
                OwnerName: file.Owner.Name,
                OwnerObjectId: file.Owner.ObjectId,
                OwnerAppId: file.Owner.AppId,
                GlobalVars: globals ?? new Dictionary<string, ALDevToolbox.Services.Al.ResolvedVariableType>(StringComparer.OrdinalIgnoreCase),
                Resolver: ResolverFor(file.ModuleId),
                OwnerSourceTableName: sourceTable,
                // ExtendsObjectName lets the bare-self-call resolver fall back
                // to procedures declared on the BASE object (the base page for
                // a pageextension, the base report for a reportextension) —
                // distinct from Rec which carries the source TABLE. Without
                // this, a bare `NoOfRecords(TableID)` in `Navigate Ext.` that
                // resolves to `Navigate.NoOfRecords` on the base page surfaces
                // as bare-call unresolved.
                OwnerExtendsName: file.Owner.ExtendsObjectName);

            var result = ALDevToolbox.Services.Al.AlReferenceExtractor.Extract(content, ctx);
            totalUnresolved += result.Stats.UnresolvedReceivers;

            // Diagnostic sampling: capture the first N unresolved
            // tokens across the whole phase so operators can spot
            // systematic gaps (a common token shape, an uningested
            // dependency, …) without re-running with verbose logging.
            // Cap at perFileSampleCap per file so one noisy file
            // doesn't consume the whole bucket — we'd rather see
            // patterns across many files than 50 lines from 3 files.
            const int perFileSampleCap = 3;
            if (unresolvedSamples.Count < unresolvedLogCap
                && result.Stats.UnresolvedSamples.Count > 0)
            {
                int fromThisFile = 0;
                foreach (var s in result.Stats.UnresolvedSamples)
                {
                    if (unresolvedSamples.Count >= unresolvedLogCap) break;
                    if (fromThisFile >= perFileSampleCap) break;
                    unresolvedSamples.Add((
                        file.ModuleName ?? string.Empty,
                        file.Path ?? string.Empty,
                        file.Owner.Kind + ":" + file.Owner.Name,
                        s));
                    fromThisFile++;
                }
            }

            // Stamp end_line / end_column onto the body-bearing symbols
            // the walker just finished tracing through. The extractor
            // emits one ExtractedSymbolScope per (procedure / trigger /
            // event publisher / event subscriber) on body close; we
            // resolve back to the symbol row by (owner, start line)
            // since line is unique within an object. Attach + mark
            // modified so EF emits a targeted UPDATE for these two
            // columns only — no full-row reload. See issue #181.
            foreach (var scope in result.SymbolScopes)
            {
                if (!symbolIdByOwnerAndLine.TryGetValue(
                        (file.Owner.Id, scope.StartLine),
                        out var scopeSymbolId))
                {
                    continue;
                }
                var stub = new OeModuleSymbol
                {
                    Id = scopeSymbolId,
                    EndLine = scope.EndLine,
                    EndColumn = scope.EndColumn,
                };
                _db.OeModuleSymbols.Attach(stub);
                _db.Entry(stub).Property(s => s.EndLine).IsModified = true;
                _db.Entry(stub).Property(s => s.EndColumn).IsModified = true;
                pending++;
            }

            // Normal call-site references (method_call / field_access). Skipped
            // in system-references-only mode — the backfill path (#291)
            // repopulates only oe_module_system_references and leaves the
            // already-present normal references untouched.
            if (!systemReferencesOnly)
            foreach (var r in result.References)
            {
                long? targetSymbolId = null;
                long? targetVariableId = null;
                // Resolve the owning procedure / trigger that emitted this
                // reference — the (Owner, StartLine) tuple uniquely
                // identifies the symbol row. Null for object-scope refs
                // and for legacy / pre-#181 ingests where the extractor
                // didn't stamp scope onto ExtractedReference.
                long? sourceSymbolId = null;
                if (r.SourceMemberLine is int sourceLine
                    && symbolIdByOwnerAndLine.TryGetValue(
                        (file.Owner.Id, sourceLine),
                        out var resolvedSourceSymbolId))
                {
                    sourceSymbolId = resolvedSourceSymbolId;
                }
                // Identity-keyed lookup so a tableextension named the
                // same as the table it extends doesn't claim the table's
                // symbols at TargetSymbolId stamp time. The reference row
                // carries TargetAppId + TargetObjectKind + TargetObjectName
                // — that's exactly the catalog's canonical identity.
                if (objectIdByIdentity.TryGetValue(
                        (r.TargetAppId, r.TargetObjectKind, r.TargetObjectName),
                        out var ownerId)
                    && membersByOwner.TryGetValue(ownerId, out var memberList))
                {
                    targetSymbolId = memberList.FirstOrDefault(m =>
                        string.Equals(m.Name, r.TargetMemberName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Kind, r.TargetMemberKind, StringComparison.OrdinalIgnoreCase))?.SymbolId;
                }

                // Stamp variable_use rows with the resolved
                // oe_module_variables FK so right-click "Find
                // references" on a global lands on the filtered
                // index (ix_oe_module_references_target_variable).
                // The extractor targets the file's owner with
                // TargetMemberName = variable name; we look up the
                // DB id by (owner, name). See step 6.
                if (string.Equals(r.ReferenceKind, "variable_use", StringComparison.Ordinal)
                    && r.TargetMemberName is not null
                    && variableIdByOwnerAndName.TryGetValue(
                        (file.Owner.Id, r.TargetMemberName.ToLowerInvariant()),
                        out var variableId))
                {
                    targetVariableId = variableId;
                }

                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = file.ModuleId,
                    SourceObjectId = file.Owner.Id,
                    TargetAppId = r.TargetAppId,
                    TargetObjectKind = r.TargetObjectKind,
                    TargetObjectId = r.TargetObjectId,
                    TargetObjectName = r.TargetObjectName,
                    ReferenceKind = r.ReferenceKind,
                    LineNumber = r.Line,
                    ColumnNumber = r.Column,
                    TargetMemberName = r.TargetMemberName,
                    TargetMemberKind = r.TargetMemberKind,
                    TargetSymbolId = targetSymbolId,
                    TargetVariableId = targetVariableId,
                    SourceSymbolId = sourceSymbolId,
                });
                totalEmitted++;
                pending++;
            }

            // System / built-in method calls (Insert, Modify, SetRange, …) go
            // to the separate oe_module_system_references table — see #279. The
            // receiver was already resolved at extraction time, so there's no
            // target-id post-pass; only the enclosing symbol is resolved here,
            // the same way as the normal references above.
            foreach (var sr in result.SystemReferences)
            {
                long? sourceSymbolId = null;
                if (sr.SourceMemberLine is int sysSourceLine
                    && symbolIdByOwnerAndLine.TryGetValue(
                        (file.Owner.Id, sysSourceLine),
                        out var resolvedSysSourceSymbolId))
                {
                    sourceSymbolId = resolvedSysSourceSymbolId;
                }

                _db.OeModuleSystemReferences.Add(new OeModuleSystemReference
                {
                    OrganizationId = orgId,
                    ModuleId = file.ModuleId,
                    SourceObjectId = file.Owner.Id,
                    TargetAppId = sr.TargetAppId,
                    TargetObjectKind = sr.TargetObjectKind,
                    TargetObjectId = sr.TargetObjectId,
                    TargetObjectName = sr.TargetObjectName,
                    SystemMethodName = sr.SystemMethodName,
                    ReferenceKind = sr.ReferenceKind,
                    LineNumber = sr.Line,
                    ColumnNumber = sr.Column,
                    SourceSymbolId = sourceSymbolId,
                });
                totalEmitted++;
                pending++;
            }

            if (pending >= 500)
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                _db.ChangeTracker.Clear();
                pending = 0;
            }
        }
        if (pending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        _logger.LogInformation(
            "Phase-2 call-site references: ReleaseId={ReleaseId} Files={Files} Emitted={Emitted} Unresolved={Unresolved} Elapsed={Elapsed}ms",
            releaseId, fileMetas.Count, totalEmitted, totalUnresolved, sw.ElapsedMilliseconds);

        if (unresolvedSamples.Count > 0)
        {
            // One log line per sample so existing grep/structured-log
            // tooling can slice by Reason without parsing a multi-line
            // entry. Includes the file's module+path+owner so the dev
            // can open the source viewer to inspect the token in
            // context. Capped at unresolvedLogCap (see above).
            foreach (var (module, path, owner, sample) in unresolvedSamples)
            {
                _logger.LogInformation(
                    "Phase-2 unresolved sample: ReleaseId={ReleaseId} Reason={Reason} Token='{Token}' Line={Line} Col={Col} Owner={Owner} ReceiverKind={ReceiverKind} ReceiverName='{ReceiverName}' ReceiverAppId={ReceiverAppId} Module={Module} Path={Path}",
                    releaseId,
                    sample.Reason,
                    sample.Token,
                    sample.Line,
                    sample.Column,
                    owner,
                    sample.ReceiverKind ?? "(n/a)",
                    sample.ReceiverName ?? string.Empty,
                    sample.ReceiverAppId?.ToString() ?? "(n/a)",
                    module,
                    path);
            }
        }

        return totalEmitted;
    }

    /// <summary>
    /// Pulls the AL type out of a symbol-package return-type string
    /// like <c>"Record Customer"</c>, <c>"Code[20]"</c>,
    /// <c>"Codeunit \"Sales-Post\""</c>. Returns (null, null) for scalar
    /// types so the extractor's chained-access loop terminates on the
    /// next step.
    /// </summary>
    /// <summary>
    /// Renders a symbol-package return type into the
    /// <c>Keyword "ObjectName"</c> string shape <see cref="ParseReturnType"/>
    /// round-trips. Storing just <c>type.Name</c> dropped the
    /// <c>ObjectName</c> half — so <c>procedure Get(...): Codeunit
    /// "Edit in Excel Fld Filter Impl."</c> persisted as the bare
    /// keyword <c>"Codeunit"</c>, the chain walker had no concrete
    /// type to advance to after the call, and
    /// <c>Get(fieldName).AddFilterValueV2(...)</c> stranded as bare-
    /// call against the owner. AL object kinds (Record/Codeunit/…) get
    /// the quoted-name form; everything else (DotNet types, scalars
    /// with no ObjectName, generics like Codeunit alone) round-trips
    /// the existing single-token shape.
    /// </summary>
    internal static string? FormatReturnType(SymbolTypeRef? type)
    {
        if (type is null) return null;
        if (string.IsNullOrEmpty(type.ObjectName)) return type.Name;
        return $"{type.Name} \"{type.ObjectName}\"";
    }

    private static (string? Keyword, string? TypeName) ParseReturnType(string? returnType)
    {
        if (string.IsNullOrEmpty(returnType)) return (null, null);
        var trimmed = returnType.Trim();
        foreach (var kw in ReturnTypeKeywords)
        {
            if (trimmed.StartsWith(kw, StringComparison.OrdinalIgnoreCase)
                && trimmed.Length > kw.Length
                && char.IsWhiteSpace(trimmed[kw.Length]))
            {
                var rest = trimmed.Substring(kw.Length).TrimStart();
                if (rest.StartsWith('"') && rest.EndsWith('"') && rest.Length >= 2)
                {
                    return (kw, rest.Substring(1, rest.Length - 2));
                }
                return (kw, rest);
            }
        }
        return (null, null);
    }

    private static readonly string[] ReturnTypeKeywords = new[]
    {
        "Record", "Codeunit", "Page", "Report", "Query", "XmlPort",
        "Interface", "Enum", "RequestPage", "TestPage", "TestPart",
        "ControlAddIn", "PermissionSet", "Profile",
    };

    /// <summary>
    /// For each module in the release, compute the transitive set of
    /// AppIds it can legally reach via <c>app.json</c> dependencies
    /// (plus the module's own AppId, plus the well-known foundational
    /// Microsoft apps every extension implicitly resolves). The
    /// reference-extractor's resolver consults this set so a file in
    /// DK Core can resolve types from Base App (an implicit dep) but
    /// not from OIOUBL (an unrelated extension).
    ///
    /// <para><b>Implicit foundational apps.</b> AL extensions never
    /// declare dependencies on System Application, Base Application,
    /// Application, or Business Foundation — the AL compiler always
    /// makes their symbols available, and Microsoft confirmed this
    /// matches the developer-tool experience. AMC Banking 365
    /// Fundamentals (and ~every BC extension) ships with empty
    /// <c>&lt;Dependencies/&gt;</c> in <c>NavxManifest.xml</c> yet
    /// freely references <c>Codeunit "Temp Blob"</c> (System App),
    /// <c>Record "Sales Header"</c> (Base App), etc. The visibility
    /// set must mirror that or every such reference looks
    /// "type-unresolved" from the resolver's perspective.
    ///
    /// Modules whose <c>DependenciesJson</c> references AppIds outside
    /// this release land in the visibility set anyway — cross-release
    /// receivers don't resolve in this pass but the set correctly
    /// captures intent.
    /// </summary>
    /// <summary>
    /// Resolves the set of "winning" module ids visible from
    /// <paramref name="releaseId"/> across its parent-release chain and its
    /// dependency links (the vendor releases a pipeline build resolved) — the same
    /// recursive ancestry + app-id shadowing (closest depth wins) the
    /// find-references queries use (<see cref="ReleaseAncestrySql.WinningModules"/>).
    /// The Phase-2 reference resolver builds its type / member / visibility
    /// catalogs from this set so a child Release's code resolves against the
    /// base objects and fields it sits on, not just its own modules.
    /// Runs raw SQL (bypasses the EF query filter); the caller seeds it with a
    /// release id already obtained through an org-filtered read, and a parent
    /// chain never crosses an org boundary — same fence as
    /// <see cref="ChainObjectResolution"/>.
    /// </summary>
    private async Task<HashSet<long>> GetVisibleChainModuleIdsAsync(int releaseId, CancellationToken ct)
    {
        const string sql = ReleaseAncestrySql.WinningModules + "\n" + """
            SELECT w.id AS "Id" FROM winning w
            """;
        var rows = await _db.Database
            .SqlQueryRaw<ChainIdRow>(sql, releaseId)
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(r => r.Id).ToHashSet();
    }

    private async Task<Dictionary<long, HashSet<Guid>>> BuildModuleVisibilityAsync(
        IReadOnlyCollection<long> chainModuleIds, CancellationToken ct)
    {
        // Span the visible release chain, not just the imported release: AL
        // extensions never declare a dependency on the platform umbrella apps
        // (System Application, Base Application, Application, Business
        // Foundation) — those are implicit. For a project / third-party
        // release those apps live in the *parent* Release, so building the
        // foundational set from this release alone would leave it empty and
        // every base object would be filtered out as "not visible" even once
        // it's in the catalog. Loading the chain's modules here lets a child
        // module's implicit-foundational + Microsoft-sibling visibility reach
        // the parent's base apps.
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => chainModuleIds.Contains(m.Id))
            .Select(m => new { m.Id, m.AppId, m.Name, m.Publisher, m.DependenciesJson })
            .ToListAsync(ct).ConfigureAwait(false);

        // Each module's direct deps as parsed from DependenciesJson.
        var directDepsByAppId = new Dictionary<Guid, HashSet<Guid>>();
        foreach (var m in modules)
        {
            directDepsByAppId[m.AppId] = ReleaseImportAllowLists.ParseDependencyAppIds(m.DependenciesJson);
        }

        // The implicit foundational set: Microsoft-published modules in
        // this release whose name matches one of the always-available
        // platform apps. Matched by name so the GUID can drift across
        // BC versions (Microsoft has historically restamped these).
        // Publisher filter keeps a hypothetical third-party app called
        // "Base Application" from sneaking into everyone's visibility.
        var implicitFoundational = new HashSet<Guid>(
            modules
                .Where(m => string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase)
                            && ReleaseImportAllowLists.FoundationalAppNames.Contains(m.Name))
                .Select(m => m.AppId));

        // All Microsoft-published modules in the release. Microsoft apps
        // (first-party) have unrestricted access to each other's symbols
        // in BC's compiler — the AL dev tools surface every Microsoft
        // codeunit / table / page without requiring an app.json dep.
        // Examples surfaced in the diagnostic samples:
        //   - `_Exclude_APIV1_` / `_Exclude_APIV2_` reference
        //     `Codeunit "O365 Setup Email"` which lives in another
        //     Microsoft app neither lists as a dep.
        //   - `Application Test Library` references `Library - Utility`
        //     and friends across sibling Microsoft test-library apps.
        //   - `Bank Account Reconciliation With AI Tests` references
        //     `Library - ERM`, `Library - Random`, `Assert`.
        // Third-party apps still respect declared deps + the foundational
        // set; this expansion only applies when the importing module is
        // Microsoft-published.
        var allMicrosoftAppIds = new HashSet<Guid>(
            modules
                .Where(m => string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.AppId));

        // Transitive closure per module, including the module itself,
        // the implicit foundational AppIds, and the PlatformAppId sentinel
        // for the synthetic virtual-table entries. Microsoft-published
        // modules additionally see every other Microsoft module in the
        // release (see comment above on allMicrosoftAppIds).
        var result = new Dictionary<long, HashSet<Guid>>(modules.Count);
        foreach (var m in modules)
        {
            var visible = new HashSet<Guid>(implicitFoundational) { m.AppId, ReleaseImportAllowLists.PlatformAppId };
            if (string.Equals(m.Publisher, "Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                visible.UnionWith(allMicrosoftAppIds);
            }
            ReleaseImportAllowLists.WalkDeps(m.AppId, visible, directDepsByAppId);
            result[m.Id] = visible;
        }
        return result;
    }
}
