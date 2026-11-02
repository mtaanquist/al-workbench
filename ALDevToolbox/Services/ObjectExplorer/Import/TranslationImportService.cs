using System.IO.Compression;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Reads XLIFF translation files and persists them as
/// <see cref="OeModuleTranslation"/> rows. Three entry points (single-file
/// admin upload, per-release ZIP admin upload, automatic extraction during
/// <see cref="ReleaseImportService"/>) funnel into a shared
/// <c>(module, language)</c> clobber + insert + best-effort symbol-resolution
/// path. See GitHub issue #151 for the larger feature and
/// <c>CLAUDE.md</c> for the architecture fences this service stays inside.
/// </summary>
public class TranslationImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ALDevToolbox.Services.Translation.TranslationMemoryService _memory;
    private readonly ALDevToolbox.Services.Organizations.StorageQuotaGuard _quotaGuard;
    private readonly ILogger<TranslationImportService> _logger;

    /// <summary>
    /// The solution-visibility fence for the admin uploads (#1430): a Private
    /// solution's build release answers like an unknown id to someone outside its
    /// teams. Required, so nothing can build the service without the fence.
    /// </summary>
    private readonly ProjectAccess _access;

    /// <summary>
    /// Per-entry decompression ceiling for an uploaded translation ZIP. A single
    /// BC language XLIFF runs to tens of MB; 256 MB is generous headroom while
    /// still refusing a decompression bomb before it fills the shared heap.
    /// Enforced on the bytes actually inflated (via
    /// <see cref="AppPackageReader.OpenCapped"/>), not the entry's declared
    /// length. See issue #367.
    /// </summary>
    private const long MaxXliffEntryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Aggregate decompressed-size budget across all <c>.xlf</c> entries in one
    /// uploaded ZIP, summed from the entries' declared lengths as a cheap upfront
    /// guard so a ZIP of thousands of large XLIFFs is refused before the per-entry
    /// streaming loop grinds through them. See issue #367.
    /// </summary>
    private const long MaxTotalXliffBytes = 2L * 1024 * 1024 * 1024;

    public TranslationImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ALDevToolbox.Services.Translation.TranslationMemoryService memory,
        ALDevToolbox.Services.Organizations.StorageQuotaGuard quotaGuard,
        ILogger<TranslationImportService> logger,
        ProjectAccess access)
    {
        _db = db;
        _orgContext = orgContext;
        _memory = memory;
        _quotaGuard = quotaGuard;
        _logger = logger;
        _access = access;
    }

    /// <summary>
    /// False when the visibility fence hides <paramref name="releaseId"/>. Asked
    /// before the existence read so a hidden id and an unknown one run the same
    /// queries and get the same error.
    /// </summary>
    private Task<bool> IsReleaseVisibleAsync(int releaseId, CancellationToken ct) =>
        _access.IsReleaseVisibleAsync(releaseId, ct);

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; TranslationImportService called outside an authenticated request.");

    // ── Single .xlf admin upload ───────────────────────────────────────

    /// <summary>
    /// Admin uploads one <c>.xlf</c> against a known module. Re-uploading
    /// for the same target language clobbers the previous rows.
    /// </summary>
    public async Task<TranslationImportSummary> ImportSingleAsync(
        int releaseId,
        long moduleId,
        Stream xliff,
        string fileName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(xliff);
        var orgId = RequireOrganizationId();
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);

        var visible = await IsReleaseVisibleAsync(releaseId, ct).ConfigureAwait(false);
        var module = await _db.OeModules.AsNoTracking()
            .Where(m => m.Id == moduleId && m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.Name, m.Release!.Kind })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (module is null || !visible)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ModuleId"] = $"Module {moduleId} does not belong to release {releaseId}.",
            });
        }

        XliffDocument parsed;
        try
        {
            parsed = AlXliffParser.Parse(xliff);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["XliffFile"] = $"Could not parse \"{fileName}\" as XLIFF v1.2: {ex.Message}",
            });
        }

        // Light sanity check: the file's <file original> should match the
        // module name. Mismatch isn't fatal (admin may know better; we
        // chose the module explicitly) but it's worth logging.
        if (!string.IsNullOrEmpty(parsed.OriginalName)
            && !string.Equals(parsed.OriginalName, module.Name, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Translation upload {File} declares original=\"{Original}\" but admin attached it to module \"{Module}\" (id={ModuleId}).",
                fileName, parsed.OriginalName, module.Name, moduleId);
        }

        var inserted = await ReplaceForModuleAsync(
            orgId, moduleId, parsed, module.Name, LearnsIntoMemory(module.Kind), ct).ConfigureAwait(false);
        return new TranslationImportSummary(
            LanguageCode: parsed.TargetLanguage,
            ModuleName: module.Name,
            Inserted: inserted);
    }

    // ── Per-release ZIP admin upload ───────────────────────────────────

    /// <summary>
    /// Admin uploads a ZIP holding one or more <c>.xlf</c> files. Each
    /// entry is matched to a module in the release by the XLIFF's
    /// <c>&lt;file original="..."&gt;</c> attribute → <see cref="OeModule.Name"/>
    /// (case-insensitive). Unmatched entries are skipped — their names
    /// appear in <see cref="TranslationZipImportSummary.UnmatchedFiles"/>
    /// so the admin can see why a file didn't land.
    /// </summary>
    public async Task<TranslationZipImportSummary> ImportZipAsync(
        int releaseId,
        Stream zip,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(zip);
        var orgId = RequireOrganizationId();
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);

        var visible = await IsReleaseVisibleAsync(releaseId, ct).ConfigureAwait(false);
        var release = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Id, r.Kind })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (release is null || !visible)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} does not exist in this organisation.",
            });
        }

        // Build a case-insensitive Name → ModuleId map once per upload.
        var modules = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules)
        {
            // Duplicate module names in one release shouldn't happen, but
            // if they do, last-write-wins is fine for this lookup.
            byName[m.Name] = m.Id;
        }

        int matched = 0, totalInserted = 0;
        var unmatched = new List<string>();
        var matchedSummaries = new List<TranslationImportSummary>();

        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);

        // Upfront aggregate guard: refuse a ZIP whose .xlf entries declare more
        // than the total decompressed budget before streaming any of them (#367).
        var declaredXlfBytes = archive.Entries
            .Where(e => e.FullName.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase))
            .Sum(e => e.Length);
        if (declaredXlfBytes > MaxTotalXliffBytes)
        {
            // Field-keyed so the upload form shows it next to the ZIP field
            // rather than the request failing with a 500 (#1261).
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ZipFile"] = $"The translation files in this ZIP unpack to {declaredXlfBytes:N0} bytes, "
                    + $"over the {MaxTotalXliffBytes:N0}-byte limit. Split them across smaller ZIPs and upload each one.",
            });
        }

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (!entry.FullName.EndsWith(".xlf", StringComparison.OrdinalIgnoreCase)) continue;

            XliffDocument parsed;
            try
            {
                // Per-entry streaming cap on the bytes actually inflated, plus the
                // hardened XmlReader inside the parser (#367).
                using var stream = AppPackageReader.OpenCapped(entry, MaxXliffEntryBytes);
                parsed = AlXliffParser.Parse(stream);
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Skipping XLIFF zip entry {Entry}: parse failed.", entry.FullName);
                unmatched.Add(entry.FullName);
                continue;
            }

            if (string.IsNullOrEmpty(parsed.OriginalName)
                || !byName.TryGetValue(parsed.OriginalName, out var moduleId))
            {
                unmatched.Add(entry.FullName);
                continue;
            }

            var inserted = await ReplaceForModuleAsync(
                orgId, moduleId, parsed, parsed.OriginalName, LearnsIntoMemory(release.Kind), ct).ConfigureAwait(false);
            matched++;
            totalInserted += inserted;
            matchedSummaries.Add(new TranslationImportSummary(
                LanguageCode: parsed.TargetLanguage,
                ModuleName: parsed.OriginalName!,
                Inserted: inserted));
        }

        return new TranslationZipImportSummary(
            MatchedFiles: matched,
            UnmatchedFiles: unmatched,
            TotalInserted: totalInserted,
            PerFile: matchedSummaries);
    }

    // ── Automatic extraction during release ingest ────────────────────

    /// <summary>
    /// Imports one already-parsed XLIFF for a module during a first-party
    /// release ingest (<see cref="ReleaseImportService"/>). Same
    /// <c>(module, language)</c> clobber + <see cref="OeModuleTranslation"/>
    /// insert + translation-memory upsert as the admin uploads — the caller has
    /// already streamed the <c>.xlf</c> through <see cref="AlXliffParser"/>, so
    /// this skips the parse/validation the upload entry points do. Runs inside
    /// the import job's organization scope. Returns the number of rows inserted.
    /// </summary>
    public Task<int> ImportForModuleAsync(
        long moduleId, XliffDocument parsed, string? originName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var orgId = RequireOrganizationId();
        return ReplaceForModuleAsync(orgId, moduleId, parsed, originName, learnIntoMemory: true, ct);
    }

    /// <summary>
    /// Whether an upload onto a release of <paramref name="releaseKind"/> may feed
    /// the org-wide translation memory. A solution's build release may not (#1613):
    /// the memory keeps no link back to the release, so its pairs would reach
    /// everyone in the organisation, including people a Private solution hides the
    /// release from. A solution's own strings reach the memory through its
    /// repositories instead, attributed so the visibility fence still applies
    /// (<c>.design/github-integration-phase2.md</c>, #631). The upload's
    /// per-release rows are written either way.
    /// </summary>
    private static bool LearnsIntoMemory(string releaseKind) =>
        !string.Equals(releaseKind, OeRelease.ProjectBuildKind, StringComparison.Ordinal);

    // ── Shared parse → clobber → insert → resolve ──────────────────────

    /// <summary>
    /// Replaces every <see cref="OeModuleTranslation"/> row for the given
    /// <c>(moduleId, languageCode)</c> with rows derived from the parsed
    /// XLIFF, then runs a single name-keyed symbol resolution pass against
    /// <c>oe_module_symbols</c>. Returns the number of rows inserted.
    /// Clobber semantics are intentional — the user asked for "if one
    /// was already uploaded, it's fine for it to clobber existing".
    /// </summary>
    private async Task<int> ReplaceForModuleAsync(
        int orgId, long moduleId, XliffDocument parsed, string? originName, bool learnIntoMemory, CancellationToken ct)
    {
        // Skip XLIFFs whose source-language matches their target-language.
        // That's the shape the AL compiler emits as <Module>.g.xlf — the
        // generator template where every <target> mirrors the <source>
        // verbatim. Ingesting it would double every search hit with an
        // English no-op row and put a phantom en-US entry on the
        // per-release languages chip. Doing the check here means all
        // three intake paths (single-file admin, ZIP, .app auto-extract)
        // catch it without each having to know the filename convention.
        if (!string.IsNullOrEmpty(parsed.SourceLanguage)
            && string.Equals(parsed.SourceLanguage, parsed.TargetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Skipped XLIFF for module {ModuleId}: source-language and target-language are both {Lang} (generator template, not a real translation).",
                moduleId, parsed.TargetLanguage);
            return 0;
        }

        // Symbol map keyed by lower(name) → list of symbol ids. Loaded
        // once per upload so the per-row resolver doesn't run a query
        // for each of the thousands of trans-units. Field captions and
        // tooltips resolve to kind='table_field' / 'page_field'; labels
        // (sub_kind=namedtype) resolve to a same-name symbol — most
        // commonly a Label declaration emitted by the source extractor.
        var symbols = await _db.OeModuleSymbols.AsNoTracking()
            .Where(s => s.ModuleId == moduleId)
            .Select(s => new { s.Id, s.Name, s.Kind })
            .ToListAsync(ct).ConfigureAwait(false);
        var symbolByName = new Dictionary<string, List<(long Id, string Kind)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in symbols)
        {
            if (!symbolByName.TryGetValue(s.Name, out var list))
            {
                list = new List<(long, string)>();
                symbolByName[s.Name] = list;
            }
            list.Add((s.Id, s.Kind));
        }

        // The delete and the chunked insert share one transaction, so a
        // failed chunk or an abandoned request leaves the previous rows for
        // this (module, language) in place instead of a half-empty language
        // (#1261). The release amend already runs inside its own
        // transaction; this joins it rather than nesting.
        await using var tx = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        // Clobber existing rows for (module, language) so re-upload
        // replaces wholesale rather than merging. Executed as one DELETE
        // for tracker hygiene; the unique index on (module, language,
        // trans_unit_id) makes the insert side safe even when XLIFFs
        // accidentally double up a trans-unit.
        await _db.OeModuleTranslations
            .Where(t => t.ModuleId == moduleId && t.LanguageCode == parsed.TargetLanguage)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        int inserted = 0;
        int pending = 0;
        const int chunkSize = 500;
        var now = DateTime.UtcNow;
        // XLIFFs occasionally repeat the same id (BC compiler bug history;
        // hand-edited files); dedupe inside the upload so we don't violate
        // the unique index. Last-write-wins on collisions.
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var unit in parsed.Units)
        {
            if (!seenIds.Add(unit.Id))
            {
                continue;
            }
            var kind = AlXliffParser.BucketKind(unit.Hint);
            long? symbolId = ResolveSymbolId(symbolByName, unit.Hint, kind);

            _db.OeModuleTranslations.Add(new OeModuleTranslation
            {
                OrganizationId = orgId,
                ModuleId = moduleId,
                LanguageCode = parsed.TargetLanguage,
                TransUnitId = unit.Id,
                SourceText = unit.SourceText,
                TargetText = unit.TargetText,
                TargetState = unit.TargetState,
                Kind = kind,
                ObjectKind = unit.Hint?.ObjectKind,
                ObjectName = unit.Hint?.ObjectName,
                SubKind = unit.Hint?.SubKind,
                SubName = unit.Hint?.SubName,
                PropertyName = unit.Hint?.PropertyName,
                DeveloperNote = unit.DeveloperNote,
                SymbolId = symbolId,
                CreatedAt = now,
            });
            inserted++;
            pending++;
            if (pending >= chunkSize)
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

        if (tx is not null)
        {
            // Not cancellable: once every row is written, a request cancelled
            // at the last step should not throw the work away.
            await tx.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Imported XLIFF translations: Module={ModuleId} Lang={Lang} Inserted={Inserted}",
            moduleId, parsed.TargetLanguage, inserted);

        // Feed the org-wide translation memory so these pairs surface as
        // suggestions in the Translator tool (and later as LLM glossary hints).
        // Only meaningful when the file declares a source language; BC XLIFFs
        // carry source-language="en-US". Best-effort — a memory hiccup must not
        // fail the import the admin actually asked for. Skipped for a solution's
        // build release, whose pairs the memory could not keep inside the
        // solution (see LearnsIntoMemory).
        if (learnIntoMemory && !string.IsNullOrEmpty(parsed.SourceLanguage))
        {
            try
            {
                var origin = string.IsNullOrEmpty(originName) ? null : originName;
                var pairs = parsed.Units
                    .Where(u => !string.IsNullOrEmpty(u.TargetText))
                    .Select(u => new ALDevToolbox.Services.Translation.TranslationMemoryUpsert(
                        parsed.SourceLanguage!,
                        parsed.TargetLanguage,
                        u.SourceText,
                        u.TargetText,
                        AlXliffParser.BucketKind(u.Hint),
                        origin));
                await _memory.UpsertAsync(pairs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Translation memory population failed for module {ModuleId} ({Lang}); import itself succeeded.",
                    moduleId, parsed.TargetLanguage);
            }
        }

        return inserted;
    }

    /// <summary>
    /// Best-effort match from the parsed hint to a row in
    /// <c>oe_module_symbols</c>. Field captions / tooltips resolve to the
    /// field symbol; labels (NamedType sub-kind) to the label symbol.
    /// Returns null for hints we don't yet symbolise (page controls,
    /// page actions, enum values — issue #151 v2 closes those gaps).
    /// </summary>
    private static long? ResolveSymbolId(
        IReadOnlyDictionary<string, List<(long Id, string Kind)>> symbolByName,
        XliffLookupHint? hint,
        string bucketKind)
    {
        if (hint is null) return null;

        // Field caption / tooltip → match by field name (sub_name) against
        // table_field / page_field rows.
        if (string.Equals(hint.SubKind, "field", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var fieldCandidates))
        {
            var hit = fieldCandidates.FirstOrDefault(c =>
                string.Equals(c.Kind, "table_field", StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Kind, "page_field", StringComparison.OrdinalIgnoreCase));
            if (hit.Id != 0) return hit.Id;
        }

        // Label (NamedType) → match by sub_name against any symbol with
        // that name. The source extractor doesn't yet emit a distinct
        // 'label' kind, but Microsoft labels typically live in codeunits
        // as local variables; this falls through to symbol-id=null for
        // most v1 cases and is fine.
        if (bucketKind == "label" && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var labelCandidates))
        {
            return labelCandidates[0].Id;
        }

        // Procedure-typed XLIFF entries are uncommon but possible (Method
        // sub-kind). Resolve to the first procedure-shaped row by name.
        if (string.Equals(hint.SubKind, "method", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(hint.SubName)
            && symbolByName.TryGetValue(hint.SubName, out var procCandidates))
        {
            var hit = procCandidates.FirstOrDefault(c =>
                c.Kind is "procedure" or "local_procedure" or "internal_procedure" or "protected_procedure");
            if (hit.Id != 0) return hit.Id;
        }

        return null;
    }
}

/// <summary>
/// Result of importing one XLIFF file into one module.
/// </summary>
public sealed record TranslationImportSummary(
    string LanguageCode,
    string ModuleName,
    int Inserted);

/// <summary>
/// Result of importing a per-release ZIP. <see cref="UnmatchedFiles"/>
/// holds the entry names whose <c>&lt;file original&gt;</c> didn't match
/// any module in the release, plus any entries we couldn't parse.
/// </summary>
public sealed record TranslationZipImportSummary(
    int MatchedFiles,
    IReadOnlyList<string> UnmatchedFiles,
    int TotalInserted,
    IReadOnlyList<TranslationImportSummary> PerFile);
