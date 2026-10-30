using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// In-memory cache for active "Find references" searches. A token minted
/// here lives in the URL bar via <c>?refSet=</c> and lets the source
/// viewer keep the result list visible while the user clicks through
/// references in different files. Sessions are scoped per user (no
/// cross-user reuse) and expire after a sliding 30 minutes — refreshed
/// each time the user lands on a page consuming the token.
///
/// State is process-local on purpose: per
/// <c>.design/architecture.md</c> we don't run Redis, and a restart
/// just means the user re-triggers Find references — no data loss.
/// </summary>
public sealed class ReferenceSessionService
{
    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(30);
    private readonly IMemoryCache _cache;
    private readonly ReferenceQueryService _references;
    private readonly Data.AppDbContext _db;
    private readonly ReferenceResolver _resolver;
    private readonly ProjectAccess _access;
    private readonly ILogger<ReferenceSessionService> _logger;

    public ReferenceSessionService(
        IMemoryCache cache,
        ReferenceQueryService references,
        Data.AppDbContext db,
        ReferenceResolver resolver,
        ProjectAccess access,
        ILogger<ReferenceSessionService> logger)
    {
        _cache = cache;
        _references = references;
        _db = db;
        _resolver = resolver;
        _access = access;
        _logger = logger;
    }

    /// <summary>
    /// The seed release's project-visibility check. <see cref="ReferenceQueryService"/>
    /// already refuses to run its CTEs for a hidden release, so the results would
    /// come back empty anyway — but a session minted from a hidden release still
    /// carries the object's <em>name</em> in its label, which is a fact about the
    /// project. Refusing here means the endpoint answers not-found instead.
    /// See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    private Task<bool> SeedVisibleAsync(int seedReleaseId, CancellationToken ct)
        => _access.IsReleaseVisibleAsync(seedReleaseId, ct);

    /// <summary>
    /// <see cref="SeedVisibleAsync"/> for a lookup by target id that may be seeded
    /// elsewhere (the <c>from</c> release). The label names the target, so the
    /// release holding it must be visible too: checking only the seed let anyone
    /// step through ids from a release they can see and read a hidden solution's
    /// object names and signatures (#1227).
    /// </summary>
    private async Task<bool> SeedAndTargetVisibleAsync(int seedReleaseId, int targetReleaseId, CancellationToken ct)
        => await SeedVisibleAsync(targetReleaseId, ct)
           && (seedReleaseId == targetReleaseId || await SeedVisibleAsync(seedReleaseId, ct));

    /// <summary>
    /// Mints a session keyed off a source-object id. Resolves the object's
    /// owning Module to get the <c>AppId</c> for the underlying
    /// <see cref="ReferenceQueryService.FindReferencesAsync"/> call. Returns
    /// null when the object id is unknown.
    /// <para><paramref name="viewReleaseId"/> is the release the user is
    /// browsing <em>from</em>. The chain walk only goes upward (child →
    /// parent), so to surface a project Release's own references to a base
    /// object the query must be seeded at the project Release, not the base
    /// object's home Release. When null we fall back to the object's home
    /// Release (the original behaviour). The seed flows into
    /// <see cref="ReferenceQueryService.FindReferencesAsync"/>, which org-gates
    /// it via <c>ReleaseVisibleAsync</c>, so an out-of-tenant id yields an
    /// empty set rather than a leak.</para>
    /// </summary>
    public async Task<ReferenceSession?> CreateFromSymbolAsync(
        long objectId, string ownerKey, int? viewReleaseId = null, CancellationToken ct = default)
    {
        var head = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => new
            {
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
                ReleaseId = o.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;

        var seedReleaseId = viewReleaseId ?? head.ReleaseId;
        var query = new FindReferencesQuery(
            TargetAppId: head.AppId,
            TargetObjectKind: head.Kind,
            TargetObjectId: head.ObjectId,
            TargetObjectName: head.Name);

        if (!await SeedAndTargetVisibleAsync(seedReleaseId, head.ReleaseId, ct)) return null;
        var results = await _references.FindReferencesAsync(seedReleaseId, query, ct: ct);
        var label = head.ObjectId is { } oid
            ? $"references to {head.Kind} {oid} {head.Name}"
            : $"references to {head.Kind} {head.Name}";

        return Store(label, seedReleaseId, results, ownerKey, head.Name);
    }

    /// <summary>
    /// "Find System References" on an object: every call to a built-in /
    /// system method (<c>Insert</c>, <c>Modify</c>, <c>SetRange</c>, …) on the
    /// object, from the separate system-reference table. Same object lookup as
    /// <see cref="CreateFromSymbolAsync"/>; renders through the same panel.
    /// </summary>
    public async Task<ReferenceSession?> CreateSystemReferencesFromObjectAsync(
        long objectId, string ownerKey, int? viewReleaseId = null, CancellationToken ct = default)
    {
        var head = await _db.OeModuleObjects.AsNoTracking()
            .Where(o => o.Id == objectId)
            .Select(o => new
            {
                o.Kind,
                o.ObjectId,
                o.Name,
                AppId = o.Module!.AppId,
                ReleaseId = o.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;

        var seedReleaseId = viewReleaseId ?? head.ReleaseId;
        if (!await SeedAndTargetVisibleAsync(seedReleaseId, head.ReleaseId, ct)) return null;
        return await StoreSystemReferencesAsync(
            seedReleaseId, head.AppId, head.Kind, head.ObjectId, head.Name, ownerKey, ct);
    }

    /// <summary>
    /// "Find built-in calls" from a right-click anywhere in the code: the name
    /// under the cursor is resolved the way Find references resolves it, and
    /// the calls are listed for the object it names - a table or page name
    /// directly, or a record or page variable through its declared type
    /// (<c>Customer</c> in <c>Customer.Insert()</c>). Null when the name is
    /// neither, so the viewer can say what to right-click instead.
    /// </summary>
    public async Task<ReferenceSession?> CreateSystemReferencesAtPositionAsync(
        long fileId, int line, int column, string ownerKey,
        int? viewReleaseId = null, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { Content = f.FileContent!.Content, ReleaseId = f.Module!.ReleaseId })
            .SingleOrDefaultAsync(ct);
        if (meta is null) return null;
        if (!await SeedVisibleAsync(meta.ReleaseId, ct)) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word)) return null;

        var resolution = await _resolver.ResolveAsync(new ResolverContext(
            FileId: fileId,
            ReleaseId: meta.ReleaseId,
            Line: line,
            Column: column,
            Word: click.Word,
            LeftQualifier: click.LeftContext.Qualifier,
            LeftOperator: click.LeftContext.Operator,
            FileContent: meta.Content), ct);

        switch (resolution?.Target)
        {
            case ResolutionTarget.CatalogObject catalog:
                return await CreateSystemReferencesFromObjectAsync(
                    catalog.ObjectId, ownerKey, viewReleaseId ?? meta.ReleaseId, ct);
            case ResolutionTarget.GlobalVariable variable:
                var type = await _db.OeModuleVariables.AsNoTracking()
                    .Where(v => v.Id == variable.VariableId)
                    .Select(v => new { v.TargetAppId, v.TargetObjectKind, v.TargetObjectId, v.TargetObjectName })
                    .SingleOrDefaultAsync(ct);
                if (type?.TargetAppId is not { } appId
                    || string.IsNullOrEmpty(type.TargetObjectKind)
                    || string.IsNullOrEmpty(type.TargetObjectName))
                {
                    return null;
                }
                if (viewReleaseId is { } view && view != meta.ReleaseId && !await SeedVisibleAsync(view, ct))
                {
                    return null;
                }
                return await StoreSystemReferencesAsync(
                    viewReleaseId ?? meta.ReleaseId, appId, type.TargetObjectKind,
                    type.TargetObjectId, type.TargetObjectName, ownerKey, ct);
            case ResolutionTarget.LocalVariable local:
                // A variable declared in this file, local or global: its
                // declared type names the object, looked up by kind and name
                // so a page cannot stand in for the table of the same name.
                if (Services.Al.AlGoToDefinitionLocator.ResolveVariableObjectType(meta.Content, local.Name)
                    is not { } declared)
                {
                    return null;
                }
                var typeObject = await ChainObjectResolution.ResolveObjectAsync(
                    _db, meta.ReleaseId, declared.Name, declared.Kind, objectId: null, ct);
                return typeObject is null
                    ? null
                    : await CreateSystemReferencesFromObjectAsync(
                        typeObject.Id, ownerKey, viewReleaseId ?? meta.ReleaseId, ct);
            default:
                return null;
        }
    }

    private async Task<ReferenceSession> StoreSystemReferencesAsync(
        int seedReleaseId, Guid appId, string kind, int? objectId, string name,
        string ownerKey, CancellationToken ct)
    {
        var query = new FindSystemReferencesQuery(
            TargetAppId: appId,
            TargetObjectKind: kind,
            TargetObjectId: objectId,
            TargetObjectName: name);
        var results = await _references.FindSystemReferencesAsync(seedReleaseId, query, ct: ct);
        var label = objectId is { } oid
            ? $"built-in calls on {kind} {oid} {name}"
            : $"built-in calls on {kind} {name}";
        return Store(label, seedReleaseId, results, ownerKey, name, kind: "built_in_calls");
    }

    /// <summary>
    /// Member-scoped variant: mints a session for a specific procedure /
    /// field / trigger on an owner object. Used by the outline right-click
    /// path (where the symbol id is known directly) and by the at-position
    /// path when the click resolves to a member.
    /// </summary>
    public async Task<ReferenceSession?> CreateFromMemberSymbolAsync(
        long symbolId, string ownerKey, int? viewReleaseId = null, CancellationToken ct = default)
    {
        // Flat projection so we don't depend on a child navigation that
        // wasn't included. Projecting `Owner = s.Object!` materialised
        // just the object row; reading head.Owner.Module then threw an
        // NRE because EF never loaded the Module navigation property.
        // Pulling every column scalar dodges the navigation entirely.
        var head = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.Id == symbolId)
            .Select(s => new
            {
                s.Kind,
                s.Name,
                s.Signature,
                OwnerKind = s.Object!.Kind,
                OwnerObjectId = s.Object!.ObjectId,
                OwnerName = s.Object!.Name,
                OwnerAppId = s.Object!.Module!.AppId,
                ReleaseId = s.Object!.Module!.ReleaseId,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;

        var seedReleaseId = viewReleaseId ?? head.ReleaseId;
        if (!await SeedAndTargetVisibleAsync(seedReleaseId, head.ReleaseId, ct)) return null;
        var query = new FindReferencesQuery(
            TargetAppId: head.OwnerAppId,
            TargetObjectKind: head.OwnerKind,
            TargetObjectId: head.OwnerObjectId,
            TargetObjectName: head.OwnerName,
            TargetMemberName: head.Name,
            TargetMemberKind: head.Kind);

        var results = await _references.FindReferencesForSymbolAsync(
            seedReleaseId, query, ct: ct);

        var sigPart = string.IsNullOrEmpty(head.Signature) ? "" : head.Signature;
        var label = head.OwnerObjectId is { } oid
            ? $"references to {head.Kind} {head.OwnerKind} {oid} {head.OwnerName}.{head.Name}{sigPart}"
            : $"references to {head.Kind} {head.OwnerKind} {head.OwnerName}.{head.Name}{sigPart}";

        return Store(label, seedReleaseId, results, ownerKey, head.Name);
    }

    /// <summary>
    /// Mints a session for an object-scope global variable click.
    /// Queries <c>oe_module_references</c> for <c>variable_use</c>
    /// rows that the import-side stamped with this variable's id
    /// (step 6). Misses turn into an empty session — fine, the
    /// click is still acknowledged. See
    /// <c>.design/al-reference-extractor-refactor.md</c> step 3b
    /// (resolver) + step 6 (emission).
    /// </summary>
    public async Task<ReferenceSession?> CreateFromGlobalVariableAsync(
        long variableId, string ownerKey, CancellationToken ct = default)
    {
        var head = await _db.OeModuleVariables.AsNoTracking()
            .Where(v => v.Id == variableId)
            .Select(v => new
            {
                v.Name,
                ReleaseId = v.Module!.ReleaseId,
                OwnerName = v.Object!.Name,
                OwnerKind = v.Object!.Kind,
            })
            .SingleOrDefaultAsync(ct);
        if (head is null) return null;
        if (!await SeedVisibleAsync(head.ReleaseId, ct)) return null;

        var rows = await _db.OeModuleReferences.AsNoTracking()
            .Where(r => r.TargetVariableId == variableId)
            .Where(r => r.Module!.ReleaseId == head.ReleaseId)
            .Select(r => new ReferenceMatch(
                r.Id,
                r.ModuleId,
                r.Module!.Name,
                r.SourceObjectId,
                r.SourceObject!.Kind,
                r.SourceObject!.Name,
                r.ReferenceKind,
                r.LineNumber,
                r.SourceObject!.SourceFileId,
                r.SourceObject!.SourceFile!.Path,
                null,
                "variable_use",
                r.TargetMemberName,
                null,
                null,
                // Enclosing procedure / trigger that uses the global (LEFT
                // JOIN via the optional SourceSymbol nav — null when the
                // reference wasn't stamped with a source symbol).
                r.SourceSymbol!.Name,
                r.SourceSymbol!.Kind,
                r.SourceSymbol!.Signature,
                r.ColumnNumber,
                null,
                null))
            .OrderBy(m => m.SourceObjectName)
            .ThenBy(m => m.LineNumber)
            // Cap consistently with the other find-references paths (+1 so Store
            // can detect truncation). See issue #366.
            .Take(ReferenceQueryService.MaxReferenceMatches + 1)
            .ToListAsync(ct);

        // The code line, and whether each use reads or sets the variable.
        rows = await _references.EnrichReferencesWithSnippetsAsync(rows, ct);

        var label = $"references to variable {head.OwnerKind} {head.OwnerName}.{head.Name}";
        return Store(label, head.ReleaseId, rows, ownerKey, head.Name);
    }

    /// <summary>
    /// Mints a session for a local-variable click. Local variables
    /// aren't catalogued (only object-scope globals live in
    /// <c>oe_module_variables</c>), so "find references" here means
    /// "every occurrence of the identifier in the same file" — same
    /// shape as the right-click "Find in this file" affordance, just
    /// surfaced through the References panel so the user gets one
    /// coherent UI for the gesture. Word-boundary aware so
    /// <c>Item</c> doesn't match inside <c>ItemNo</c>.
    /// <para>
    /// Returns an empty session (rather than null) when nothing is
    /// found — the click is acknowledged and the panel renders the
    /// "no references" empty state instead of falling through to the
    /// generic resolver-miss notice.
    /// </para>
    /// </summary>
    public async Task<ReferenceSession?> CreateFromLocalVariableAsync(
        long fileId, string varName, string ownerKey, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new
            {
                Content = f.FileContent!.Content,
                ReleaseId = f.Module!.ReleaseId,
                ModuleId = f.ModuleId,
                ModuleName = f.Module!.Name,
                f.Path,
                Owner = _db.OeModuleObjects.AsNoTracking()
                    .Where(o => o.SourceFileId == f.Id)
                    .OrderBy(o => o.Id)
                    .Select(o => new { o.Id, o.Kind, o.Name })
                    .FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (meta is null || meta.Owner is null || string.IsNullOrEmpty(meta.Content))
        {
            return null;
        }
        if (!await SeedVisibleAsync(meta.ReleaseId, ct)) return null;

        var rows = new List<ReferenceMatch>();
        var lines = OeSourceText.SplitLines(meta.Content);
        for (var i = 0; i < lines.Length; i++)
        {
            var lineText = lines[i];
            var idx = IndexOfWord(lineText, varName, 0);
            if (idx < 0) continue;
            var trimmed = lineText.TrimStart();
            var from = Math.Max(i - ReferenceUsage.ContextLines, 0);
            var to = Math.Min(i + ReferenceUsage.ContextLines, lines.Length - 1);
            var site = ReferenceUsage.Classify(lines[from..(to + 1)], i - from, idx + 1, varName);
            rows.Add(new ReferenceMatch(
                Id: rows.Count + 1,
                SourceModuleId: meta.ModuleId,
                SourceModuleName: meta.ModuleName,
                SourceObjectId: meta.Owner.Id,
                SourceObjectKind: meta.Owner.Kind,
                SourceObjectName: meta.Owner.Name,
                ReferenceKind: "local_variable_use",
                LineNumber: i + 1,
                SourceFileId: fileId,
                SourceFilePath: meta.Path,
                Snippet: trimmed.Length > 200 ? trimmed[..200] : trimmed,
                Category: "object",
                MemberName: null,
                MemberKind: null,
                MemberSignature: null,
                ColumnNumber: idx + 1,
                Usage: site?.Usage));
        }

        var label = $"uses of local variable {varName} in this file";
        return Store(label, meta.ReleaseId, rows, ownerKey, varName);
    }

    /// <summary>
    /// Word-boundary-aware <c>IndexOf</c>: returns the position of
    /// <paramref name="word"/> in <paramref name="haystack"/> only
    /// when the surrounding characters aren't AL identifier characters
    /// (letter, digit, underscore). Stops <c>Item</c> from matching
    /// inside <c>ItemNo</c>.
    /// </summary>
    internal static int IndexOfWord(string haystack, string word, int start)
    {
        var i = start;
        while (i <= haystack.Length - word.Length)
        {
            var idx = haystack.IndexOf(word, i, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return -1;
            var before = idx == 0 || !IsIdentChar(haystack[idx - 1]);
            var after = idx + word.Length == haystack.Length
                || !IsIdentChar(haystack[idx + word.Length]);
            if (before && after) return idx;
            i = idx + 1;
        }
        return -1;
    }

    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Mints a session by clicking position: looks up the word at
    /// (line, column) in the supplied file, then delegates the
    /// "what does this token point at?" question to
    /// <see cref="ReferenceResolver"/> — the single source of truth for
    /// click-time resolution shared with the extractor side (see
    /// <c>.design/al-reference-extractor-refactor.md</c> step 3).
    /// Returns null when nothing at the click resolves.
    /// </summary>
    public async Task<ReferenceSession?> CreateAtPositionAsync(
        long fileId, int line, int column, string ownerKey,
        int? viewReleaseId = null, CancellationToken ct = default)
    {
        var meta = await _db.OeModuleFiles.AsNoTracking()
            .Where(f => f.Id == fileId)
            .Select(f => new { Content = f.FileContent!.Content, ReleaseId = f.Module!.ReleaseId, f.Path })
            .SingleOrDefaultAsync(ct);
        if (meta is null)
        {
            _logger.LogInformation("FindRefsAtPosition FileId={FileId} not found.", fileId);
            return null;
        }
        if (!await SeedVisibleAsync(meta.ReleaseId, ct)) return null;

        var click = Services.Al.AlGoToDefinitionLocator.Inspect(meta.Content, line, column);
        if (click is null || string.IsNullOrEmpty(click.Word))
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Line={Line} Col={Col} no token at cursor.",
                fileId, line, column);
            return null;
        }

        var resolution = await _resolver.ResolveAsync(new ResolverContext(
            FileId: fileId,
            ReleaseId: meta.ReleaseId,
            Line: line,
            Column: column,
            Word: click.Word,
            LeftQualifier: click.LeftContext.Qualifier,
            LeftOperator: click.LeftContext.Operator,
            FileContent: meta.Content), ct);
        if (resolution is null)
        {
            _logger.LogInformation(
                "FindRefsAtPosition FileId={FileId} Path='{Path}' Word='{Word}' Qualifier='{Qual}' — no match.",
                fileId, meta.Path, click.Word, click.LeftContext.Qualifier);
            return null;
        }

        switch (resolution.Target)
        {
            case ResolutionTarget.CatalogObject catalog:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via={Reason} to ObjectId={ObjectId}.",
                    fileId, click.Word, resolution.Reason, catalog.ObjectId);
                // Seed at the view Release when the viewer carries one (a base
                // file opened from a project Release), else the clicked file's
                // own Release. The chain walk only goes upward, so the seed must
                // be the descendant to surface the project's own references.
                return await CreateFromSymbolAsync(catalog.ObjectId, ownerKey, viewReleaseId ?? meta.ReleaseId, ct);
            case ResolutionTarget.MemberSymbol member:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' Qualifier='{Qual}' resolved-via={Reason} to SymbolId={SymbolId}.",
                    fileId, click.Word, click.LeftContext.Qualifier, resolution.Reason, member.SymbolId);
                return await CreateFromMemberSymbolAsync(member.SymbolId, ownerKey, viewReleaseId ?? meta.ReleaseId, ct);
            case ResolutionTarget.GlobalVariable variable:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via={Reason} to VariableId={VariableId} on OwnerObjectId={OwnerObjectId}.",
                    fileId, click.Word, resolution.Reason, variable.VariableId, variable.OwnerObjectId);
                return await CreateFromGlobalVariableAsync(variable.VariableId, ownerKey, ct);
            case ResolutionTarget.LocalVariable local:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} Word='{Word}' resolved-via={Reason} to local var in same file.",
                    fileId, click.Word, resolution.Reason);
                return await CreateFromLocalVariableAsync(local.FileId, local.Name, ownerKey, ct);
            default:
                _logger.LogInformation(
                    "FindRefsAtPosition FileId={FileId} resolved to unknown target {Target}.",
                    fileId, resolution.Target);
                return null;
        }
    }

    /// <summary>
    /// Retrieves a previously minted session by token. Returns null when
    /// the token is unknown, expired, or owned by a different user — the
    /// caller should hide the panel in that case (token in URL stale).
    /// </summary>
    public ReferenceSession? Get(string token, string ownerKey)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_cache.TryGetValue(CacheKey(token), out CachedSession? cached) || cached is null) return null;
        if (!string.Equals(cached.OwnerKey, ownerKey, StringComparison.Ordinal)) return null;
        return cached.Session;
    }

    private ReferenceSession Store(
        string label, int releaseId, IReadOnlyList<ReferenceMatch> results, string ownerKey,
        string targetName, string kind = "references")
    {
        // The query methods fetch up to MaxReferenceMatches + 1 rows; if we got
        // more than the cap, trim to the cap and flag the session truncated so
        // the panel can say "showing the first N". See issue #366.
        var truncated = results.Count > ReferenceQueryService.MaxReferenceMatches;
        if (truncated)
        {
            results = results.Take(ReferenceQueryService.MaxReferenceMatches).ToList();
            _logger.LogInformation(
                "Reference session truncated to {Cap} rows: {Label}",
                ReferenceQueryService.MaxReferenceMatches, label);
        }

        var token = Guid.NewGuid().ToString("N");
        var session = new ReferenceSession(token, label, releaseId, results, truncated, targetName, kind);
        _cache.Set(
            CacheKey(token),
            new CachedSession(session, ownerKey),
            new MemoryCacheEntryOptions { SlidingExpiration = SessionTtl });
        return session;
    }

    private static string CacheKey(string token) => "oe-refs:" + token;

    private sealed record CachedSession(ReferenceSession Session, string OwnerKey);
}
