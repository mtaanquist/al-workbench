using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Al;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Ingests one Release worth of <c>.app</c> uploads into the
/// <c>oe_*</c> schema. Owns the Release lifecycle:
/// <c>ingesting → ready</c> on success, <c>ingesting → failed</c> on any
/// per-module exception. Each <c>.app</c> commits in its own SaveChanges
/// transaction so a 100-app DVD doesn't blow up the change tracker — but
/// the Release stays in <c>ingesting</c> until the final flip so partial
/// data is visibly partial.
///
/// See <c>.design/object-explorer.md</c> for the model and the resolution
/// strategy that reads back from the rows this service writes.
/// </summary>
public class ReleaseImportService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly StorageQuotaGuard _quotaGuard;
    private readonly TranslationImportService _translations;
    private readonly CallSiteReferenceEmitter _callSites;
    private readonly ILogger<ReleaseImportService> _logger;

    /// <summary>
    /// The dependency drift scan (issue #630), run once a first-party Release
    /// reaches <c>ready</c>. Optional so a test - or any caller that builds this
    /// service by hand - can ingest a Release without standing up the whole
    /// GitHub stack; in the app it is always injected.
    /// </summary>
    private readonly ALDevToolbox.Services.GitHub.DependencyDriftService? _drift;

    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "first_party",
        "third_party",
        // Pipeline builds only — stamped by ProjectBuildImporter, never posted
        // from the import forms (the endpoint rejects it there).
        "project",
        // Legacy C/AL TXT exports — forced by the endpoint whenever a CalTxtFile
        // is posted.
        "cal",
    };

    public ReleaseImportService(
        AppDbContext db,
        IOrganizationContext orgContext,
        StorageQuotaGuard quotaGuard,
        TranslationImportService translations,
        CallSiteReferenceEmitter callSites,
        ILogger<ReleaseImportService> logger,
        ALDevToolbox.Services.GitHub.DependencyDriftService? drift = null,
        ReleaseIngests? ingests = null)
    {
        _db = db;
        _orgContext = orgContext;
        _quotaGuard = quotaGuard;
        _translations = translations;
        _callSites = callSites;
        _logger = logger;
        _drift = drift;
        Ingests = ingests ?? new ReleaseIngests();
    }

    /// <summary>
    /// The process's record of running imports and its whole-release import gate
    /// (#1180), for this service and the callers that build on it. The app injects
    /// its one singleton; a service built by hand without one gets its own, so tests
    /// never share it by accident.
    /// </summary>
    public ReleaseIngests Ingests { get; }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; ReleaseImportService called outside an authenticated request.");

    /// <summary>
    /// Creates a Release and ingests every supplied <c>.app</c> upload. Per-app
    /// failures abort the run and flip the Release to <c>failed</c>; partial
    /// modules stay in the DB so an operator can inspect what got through.
    /// </summary>
    public async Task<ReleaseImportSummary> ImportReleaseAsync(
        ReleaseImportRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Uploads.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Uploads"] = "At least one .app file is required.",
            });
        }
        var releaseId = await BeginReleaseAsync(ReleaseImportMetadata.From(request), ct).ConfigureAwait(false);
        return await ProcessReleaseAsync(releaseId, request.Uploads, request.StoreSymbolReference, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates the metadata, reserves the label, and inserts the Release row
    /// in <c>ingesting</c> state — the synchronous half of a queued import, so
    /// the row shows in the admin list immediately and the user gets inline
    /// validation errors before any heavy work. The uploads are processed later
    /// by <see cref="ProcessReleaseAsync"/> (same request for the synchronous
    /// path, the background worker for the DVD-scale paths).
    /// </summary>
    public async Task<int> BeginReleaseAsync(ReleaseImportMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var orgId = RequireOrganizationId();
        ValidateMetadata(metadata);
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);
        await EnsureDedupKeyAvailableAsync(orgId, metadata.DedupKey, ct).ConfigureAwait(false);

        var release = new OeRelease
        {
            OrganizationId = orgId,
            Label = metadata.Label.Trim(),
            Kind = metadata.Kind,
            DedupKey = ReleaseSourceScanner.NullIfBlank(metadata.DedupKey),
            IsPrerelease = metadata.IsPrerelease,
            Publisher = ReleaseSourceScanner.NullIfBlank(metadata.Publisher),
            ProjectName = ReleaseSourceScanner.NullIfBlank(metadata.ProjectName),
            ParentReleaseId = metadata.ParentReleaseId,
            ApplicationVersionId = metadata.ApplicationVersionId,
            Status = "ingesting",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.OeReleases.Add(release);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Started Release ingest: ReleaseId={ReleaseId} Label={Label} Kind={Kind} ParentReleaseId={ParentReleaseId}",
            release.Id, release.Label, release.Kind, release.ParentReleaseId);
        return release.Id;
    }

    /// <summary>
    /// Ingests the uploads into an existing <c>ingesting</c> Release row
    /// (created by <see cref="BeginReleaseAsync"/>) and flips it to
    /// <c>ready</c>, or to <c>failed</c> (with the message) on any per-module
    /// exception. Partial modules stay in the DB so an operator can inspect
    /// what got through.
    /// </summary>
    public async Task<ReleaseImportSummary> ProcessReleaseAsync(
        int releaseId,
        IReadOnlyList<AppFileUpload> uploads,
        bool storeSymbolReference = false,
        CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        // A build waiting on this release tells a running import from an abandoned one by this (#1180).
        using var tracked = Ingests.Track(releaseId);
        var release = await _db.OeReleases.FindAsync(new object?[] { releaseId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} not found for processing.");

        // The long UPDATE…FROM resolution post-passes (numeric source tables,
        // variable targets, call-site emission) run well past Npgsql's 30 s
        // default on a busy DB; this is a background job, so give commands real
        // room — matching the backfill/re-extract paths. See issue #382.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        _logger.LogInformation(
            "Processing Release ingest: ReleaseId={ReleaseId} Uploads={UploadCount} StoreSymbolReference={StoreSymbolReference}",
            release.Id, uploads.Count, storeSymbolReference);

        var totals = new ImportTotals();
        try
        {
            foreach (var upload in uploads)
            {
                ct.ThrowIfCancellationRequested();
                await ImportOneAppAsync(orgId, release, upload, totals, storeSymbolReference, ct).ConfigureAwait(false);
            }

            // SourceTable propagation for pageextensions. The symbol
            // package only carries SourceTable on the base page; the
            // extension inherits it implicitly but ships no property of
            // its own. Copy it across now so the reference extractor's
            // page-Rec resolution (BuildGlobalScope) finds it for
            // pageextensions too.
            await PropagateSourceTableToPageExtensionsAsync(release.Id, ct).ConfigureAwait(false);

            // SourceTable property values in modern BC (28.x+) ship as
            // bare numeric ids ("36" for Sales Header) rather than the
            // legacy `#<appid>#<name>` hash-ref format. Resolve any
            // numeric values to the table's name so the reference
            // extractor can ResolveTypeByName on it. Runs AFTER the
            // pageextension propagation so any propagated numeric values
            // get normalised too.
            await ResolveNumericSourceTableNamesAsync(release.Id, ct).ConfigureAwait(false);

            // Dataitem-alias variables (and any same-module Record
            // globals whose symbol package omitted ModuleId) get their
            // TargetAppId / TargetObjectId filled in now that the full
            // catalog for this release exists. Object Explorer's variable
            // rows render click-through links via those fields.
            await ResolveVariableTargetsAsync(release.Id, ct).ConfigureAwait(false);

            // Phase 2 call-site extraction. Runs once per release, AFTER
            // every module's symbols + variables are in the DB so the
            // resolver can see types declared anywhere in this release.
            // Cross-release receivers (DK Core file references Customer
            // from a parent Base App release) currently drop — phase 2.1
            // can add the chain walk when needed.
            totals.ReferencesImported += await _callSites.EmitAsync(orgId, release.Id, ct).ConfigureAwait(false);

            // Pin the platform/application version from the canonical Base App
            // module when one came in this Release. Continue to leave null if
            // the upload was third-party-only. Re-find via the cleared tracker
            // because per-module SaveChanges has detached the entity already.
            _db.ChangeTracker.Clear();
            var ready = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Release {release.Id} disappeared mid-ingest.");
            ready.BcVersion = await InferBcVersionAsync(release.Id, ct).ConfigureAwait(false);
            ready.Status = "ready";
            ready.UpdatedAt = DateTime.UtcNow;

            // Stamp the denormalised file count + content-size totals so the
            // Releases picker doesn't recompute them via correlated subqueries
            // on every page load. The file set is immutable after a Release
            // goes ready, so a single snapshot here is enough.
            var totalsRow = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => f.Module!.ReleaseId == release.Id)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.FileContent!.ContentLength) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            ready.SourceFileCount = totalsRow?.Count ?? 0;
            ready.SourceContentLength = totalsRow?.Length ?? 0;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);


            // A new Business Central release is exactly when the customer
            // repositories that target the old one become worth knowing about,
            // so the drift scan runs here (issue #630). Best-effort: a GitHub
            // that will not answer must not fail an import that has already
            // landed every module it was given.
            await ScanForDependencyDriftAsync(ready, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Completed Release ingest: ReleaseId={ReleaseId} ModulesImported={ModulesImported} ModulesSkipped={ModulesSkipped} ObjectsImported={ObjectsImported} ReferencesImported={ReferencesImported} TranslationsImported={TranslationsImported}",
                release.Id, totals.ModulesImported, totals.ModulesSkipped, totals.ObjectsImported, totals.ReferencesImported, totals.TranslationsImported);

            return new ReleaseImportSummary(
                ReleaseId: release.Id,
                ModulesImported: totals.ModulesImported,
                ModulesSkipped: totals.ModulesSkipped,
                ObjectsImported: totals.ObjectsImported,
                ReferencesImported: totals.ReferencesImported,
                SourceFilesImported: totals.SourceFilesImported,
                TranslationsImported: totals.TranslationsImported);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller's own cancellation: the caller decides what the release
            // becomes. The import worker leaves it importing on shutdown so the
            // startup reconciler resumes the job (#483); a build that gives up on a
            // parent or vendor import it started marks that release failed itself (#1180).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Release ingest failed: ReleaseId={ReleaseId} ModulesImportedBeforeFailure={ModulesImported}",
                release.Id, totals.ModulesImported);
            // Stamp the release as failed so the UI can show the operator which
            // upload didn't make it.
            await StampFailedAsync(release.Id, ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Flips a release to <c>failed</c> on a fresh tracker, so the failed entity's
    /// state is not dragged into the status update. Never cancelled: a status write
    /// that does not land leaves the release <c>ingesting</c> with nobody importing
    /// it, and builds parenting onto it wait for it (#1180).
    /// </summary>
    private async Task StampFailedAsync(int releaseId, string message)
    {
        _db.ChangeTracker.Clear();
        var failed = await _db.OeReleases.FindAsync(new object?[] { releaseId }, CancellationToken.None).ConfigureAwait(false);
        if (failed is null) return;
        failed.Status = "failed";
        failed.StatusMessage = message;
        failed.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds more <c>.app</c> uploads to an existing <c>ready</c> Release.
    /// Used when partner / project modules trickle in after the first-party
    /// DVD has been ingested, and as the fallback for NEA-encrypted .apps
    /// that can't go through <see cref="ImportReleaseAsync"/> directly —
    /// admin compiles the publisher's .Source.zip locally with alc and
    /// drops the resulting unsigned .app onto the existing Release.
    /// Mirrors <see cref="TranslationImportService"/>'s on-demand additive
    /// pattern. See GitHub issue #216.
    ///
    /// Flips <c>ready → ingesting</c> for the duration of the amend so
    /// concurrent submits can be refused by the UI; back to <c>ready</c>
    /// on success or <c>failed</c> on any exception. Re-runs the same
    /// release-scoped post-passes <see cref="ImportReleaseAsync"/> uses,
    /// and reindexes extracted call-site references (option 1 from #216)
    /// so a late-landing module retroactively resolves earlier modules'
    /// unresolved call sites.
    /// </summary>
    public async Task<ReleaseImportSummary> AmendReleaseAsync(
        int releaseId,
        IReadOnlyList<AppFileUpload> uploads,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uploads);
        var orgId = RequireOrganizationId();
        await _quotaGuard.EnsureCanWriteAsync(ct).ConfigureAwait(false);

        // Same long resolution post-passes as ProcessReleaseAsync — raise the
        // command timeout off Npgsql's 30 s default. See issue #382.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        if (uploads.Count == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Uploads"] = "At least one .app file is required.",
            });
        }

        // Pre-check the Release's current state so the operator gets a
        // field-keyed error instead of an unexpected status flip if they
        // try to amend an ingesting / failed / soft-deleted Release.
        var preview = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Status, r.DeletedAt })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (preview is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} not found in this organisation.",
            });
        }
        if (preview.DeletedAt is not null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} is soft-deleted. Restore it from the admin page before amending.",
            });
        }
        if (!string.Equals(preview.Status, "ready", StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} isn't ready to amend (status = {preview.Status}). Wait for the current import to finish, or restore the release first.",
            });
        }

        var release = await _db.OeReleases.FindAsync(new object?[] { releaseId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} disappeared between the pre-check and the load.");

        using var tracked = Ingests.Track(release.Id);
        release.Status = "ingesting";
        release.StatusMessage = null;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Started Release amend: ReleaseId={ReleaseId} Label={Label} Uploads={UploadCount}",
            release.Id, release.Label, uploads.Count);

        var totals = new ImportTotals();
        try
        {
            foreach (var upload in uploads)
            {
                ct.ThrowIfCancellationRequested();
                // Amend (#216) doesn't expose the store-symbol-reference option.
                await ImportOneAppAsync(orgId, release, upload, totals, storeSymbolReference: false, ct).ConfigureAwait(false);
            }

            // Same post-passes as ImportReleaseAsync (lines ~130–147). The
            // first two are idempotent UPDATEs; the third (call-site
            // extraction) is not, so we wipe its previous output for this
            // release before re-running. See issue #216 reindex section.
            await PropagateSourceTableToPageExtensionsAsync(release.Id, ct).ConfigureAwait(false);
            await ResolveNumericSourceTableNamesAsync(release.Id, ct).ConfigureAwait(false);
            await ResolveVariableTargetsAsync(release.Id, ct).ConfigureAwait(false);
            await DeleteExtractedCallSiteReferencesAsync(release.Id, ct).ConfigureAwait(false);
            totals.ReferencesImported += await _callSites.EmitAsync(orgId, release.Id, ct).ConfigureAwait(false);

            _db.ChangeTracker.Clear();
            var ready = await _db.OeReleases.FindAsync(new object?[] { release.Id }, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Release {release.Id} disappeared mid-amend.");

            // Re-infer BcVersion so amending the Base App into a previously
            // third-party-only release lights it up. Don't *clear* an
            // already-set value if InferBcVersionAsync returns null — the
            // amend can only add modules, never remove them, so the
            // pre-existing inference still stands.
            var inferred = await InferBcVersionAsync(release.Id, ct).ConfigureAwait(false);
            if (inferred is not null)
            {
                ready.BcVersion = inferred;
            }

            ready.Status = "ready";
            ready.UpdatedAt = DateTime.UtcNow;

            // Re-stamp denormalised totals in the same SaveChanges as the
            // status flip back to ready, so the Releases picker never
            // reads stale counts.
            var totalsRow = await _db.OeModuleFiles.AsNoTracking()
                .Where(f => f.Module!.ReleaseId == release.Id)
                .GroupBy(_ => 1)
                .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.FileContent!.ContentLength) })
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            ready.SourceFileCount = totalsRow?.Count ?? 0;
            ready.SourceContentLength = totalsRow?.Length ?? 0;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);


            // A new Business Central release is exactly when the customer
            // repositories that target the old one become worth knowing about,
            // so the drift scan runs here (issue #630). Best-effort: a GitHub
            // that will not answer must not fail an import that has already
            // landed every module it was given.
            await ScanForDependencyDriftAsync(ready, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "Completed Release amend: ReleaseId={ReleaseId} ModulesImported={ModulesImported} ModulesSkipped={ModulesSkipped} ObjectsImported={ObjectsImported} ReferencesImported={ReferencesImported} TranslationsImported={TranslationsImported}",
                release.Id, totals.ModulesImported, totals.ModulesSkipped, totals.ObjectsImported, totals.ReferencesImported, totals.TranslationsImported);

            return new ReleaseImportSummary(
                ReleaseId: release.Id,
                ModulesImported: totals.ModulesImported,
                ModulesSkipped: totals.ModulesSkipped,
                ObjectsImported: totals.ObjectsImported,
                ReferencesImported: totals.ReferencesImported,
                SourceFilesImported: totals.SourceFilesImported,
                TranslationsImported: totals.TranslationsImported);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Release amend failed: ReleaseId={ReleaseId} ModulesImportedBeforeFailure={ModulesImported}",
                release.Id, totals.ModulesImported);
            // Cancelled or not: an amend has no job to resume it, so leaving it
            // importing would only strand it until the next restart (#1180).
            await StampFailedAsync(release.Id, ex.Message).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Wipes every reference row the source extractor produces, for every
    /// module in this Release. Run before
    /// <see cref="CallSiteReferenceEmitter.EmitAsync"/> on the amend and
    /// re-extract paths so the reindex doesn't produce duplicates.
    /// Declarative references (variable_type, extends_target,
    /// parameter_type, return_type, table_no) are written per-module
    /// during <see cref="ImportOneAppAsync"/> from the symbol package,
    /// NOT re-emitted by the extractor, so they must survive the sweep.
    ///
    /// The list below has to stay in step with the reference kinds
    /// <c>AlReferenceExtractor</c> / <c>CalReferenceExtractor</c> emit.
    /// It used to cover only method_call and field_access while the
    /// extractor also emitted property_object, event_publisher,
    /// implements_interface, label_use and variable_use — re-running
    /// extraction multiplied those rows (issue #712).
    /// </summary>
    private async Task DeleteExtractedCallSiteReferencesAsync(int releaseId, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM oe_module_references
            WHERE reference_kind IN (
                    'method_call', 'field_access', 'property_object',
                    'event_publisher', 'implements_interface',
                    'label_use', 'variable_use')
              AND module_id IN (SELECT id FROM oe_modules WHERE release_id = {0});
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-runs the Phase-2 extraction over already-stored source for an
    /// existing AL release, repopulating <c>oe_module_system_references</c>
    /// WITHOUT re-uploading the package — the maintenance backfill for releases
    /// imported before #279. Idempotent: deletes the release's existing
    /// system-reference rows first, then re-extracts in system-only mode so
    /// <c>oe_module_references</c> and the source files stay untouched. C/AL
    /// releases route to <see cref="CalImportService.BackfillSystemReferencesAsync"/>
    /// instead. See issue #291.
    /// </summary>
    public async Task BackfillSystemReferencesAsync(int releaseId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();

        var preview = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Status, r.DeletedAt })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (preview is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} not found in this organisation.",
            });
        }
        if (preview.DeletedAt is not null || !string.Equals(preview.Status, "ready", StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} isn't ready to backfill (status = {preview.Status}).",
            });
        }

        // Phase-2 over a large catalog can run long on a constrained DB; give
        // it the same headroom the C/AL import grants its module-wide passes.
        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        await DeleteSystemReferencesAsync(releaseId, ct).ConfigureAwait(false);

        var totals = new ImportTotals();
        totals.ReferencesImported += await _callSites
            .EmitSystemReferencesOnlyAsync(orgId, releaseId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Backfilled system references (AL): ReleaseId={ReleaseId} Emitted={Count}",
            releaseId, totals.ReferencesImported);
    }

    /// <summary>
    /// Re-runs the full Phase-2 extraction (call-site field / method references
    /// AND system references) over already-stored source for an existing AL
    /// release, WITHOUT re-uploading the package. Use this to repopulate
    /// references after a resolver change — notably the chain-aware catalog
    /// fix that lets a project / third-party release resolve its code
    /// references to base-table fields in the parent Release (those were
    /// silently dropped at the original import because the resolver only saw
    /// this release's own modules). Idempotent: clears the extracted call-site
    /// + system-reference rows first, then re-emits; declarative references
    /// (variable_type, extends_target, …) written during ingest stay put.
    /// Returns the number of references emitted.
    /// </summary>
    public async Task<int> ReextractReferencesAsync(int releaseId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();

        var preview = await _db.OeReleases.AsNoTracking()
            .Where(r => r.Id == releaseId)
            .Select(r => new { r.Status, r.DeletedAt })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (preview is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} not found in this organisation.",
            });
        }
        if (preview.DeletedAt is not null || !string.Equals(preview.Status, "ready", StringComparison.Ordinal))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["ReleaseId"] = $"Release {releaseId} isn't ready to re-extract (status = {preview.Status}).",
            });
        }

        _db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));

        await DeleteExtractedCallSiteReferencesAsync(releaseId, ct).ConfigureAwait(false);
        await DeleteSystemReferencesAsync(releaseId, ct).ConfigureAwait(false);

        var totals = new ImportTotals();
        totals.ReferencesImported += await _callSites.EmitAsync(orgId, releaseId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Re-extracted references (AL): ReleaseId={ReleaseId} Emitted={Count}",
            releaseId, totals.ReferencesImported);
        return totals.ReferencesImported;
    }

    /// <summary>
    /// Wipes <c>oe_module_system_references</c> for every module in the release
    /// so the backfill re-extraction can't produce duplicates. See #291.
    /// </summary>
    private async Task DeleteSystemReferencesAsync(int releaseId, CancellationToken ct)
    {
        const string sql = """
            DELETE FROM oe_module_system_references
            WHERE module_id IN (SELECT id FROM oe_modules WHERE release_id = {0});
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);
    }

    // ── Validation ──────────────────────────────────────────────────────

    private static void ValidateMetadata(ReleaseImportMetadata m)
    {
        var errors = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(m.Label))
        {
            errors["Label"] = "Label is required.";
        }
        if (!AllowedKinds.Contains(m.Kind))
        {
            errors["Kind"] = $"Kind must be one of: {string.Join(", ", AllowedKinds)}.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    /// <summary>
    /// Marks a queued Release as <c>failed</c> when the background worker hits
    /// an error <em>before</em> <see cref="ProcessReleaseAsync"/> takes over its
    /// own status handling — e.g. the URL download fails or the staged ZIP
    /// holds no application apps. Idempotent and tolerant of a missing row.
    /// </summary>
    public async Task MarkFailedAsync(int releaseId, string message, CancellationToken ct = default)
    {
        var release = await _db.OeReleases.FindAsync(new object?[] { releaseId }, ct).ConfigureAwait(false);
        if (release is null) return;
        release.Status = "failed";
        release.StatusMessage = message;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Flips a project build's Release to <c>ready</c> without indexing what it
    /// compiled. A preview check only has to say whether the code still compiles
    /// against the next Business Central version; its build results and
    /// diagnostics carry that, and the source it would index is a commit the
    /// current build indexes anyway (#1140). The release stays so the build keeps
    /// its results, but holds no files.
    /// </summary>
    public async Task MarkReadyWithoutIngestAsync(int releaseId, CancellationToken ct = default)
    {
        var release = await _db.OeReleases.FindAsync(new object?[] { releaseId }, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Release {releaseId} not found.");
        release.Status = "ready";
        release.StatusMessage = null;
        release.SourceFileCount = 0;
        release.SourceContentLength = 0;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Release {ReleaseId} marked ready without indexing its objects.", releaseId);
    }

    /// <summary>
    /// Reopens a <c>failed</c> Release for a fresh import attempt: flips it back
    /// to <c>ingesting</c> and clears the failure message so the row reads as
    /// in-progress while the re-queued job runs. The caller wipes any partial
    /// data from the previous attempt
    /// (<see cref="ReleaseManagementService.ClearIngestedDataAsync"/>) and
    /// re-enqueues the import job — this method only owns the Release-row state
    /// flip and its validation. Refuses anything that isn't a failed,
    /// non-deleted Release in this org with a field-keyed (<c>Retry</c>) error
    /// so the manage page renders it inline. See the <c>/retry</c> endpoint.
    /// </summary>
    public async Task ReopenForRetryAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw RetryError($"Release {releaseId} not found in this organisation.");

        if (release.DeletedAt is not null)
        {
            throw RetryError("This release is soft-deleted. Restore it before retrying the import.");
        }
        if (!string.Equals(release.Status, "failed", StringComparison.Ordinal))
        {
            throw RetryError($"Only a failed import can be retried (status = {release.Status}).");
        }

        release.Status = "ingesting";
        release.StatusMessage = null;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Reopened Release {ReleaseId} ({Label}) for retry.", release.Id, release.Label);
    }

    /// <summary>
    /// Reopens a project Release for a fresh build — like
    /// <see cref="ReopenForRetryAsync"/>, but also accepts a <c>ready</c> release.
    /// A partial project build lands <c>ready</c> (its successes are usable) yet
    /// still wants rebuilding once the operator supplies the missing dependency
    /// symbols, so the manual-symbols recovery path can't require the <c>failed</c>
    /// state. Flips <c>ready</c>/<c>failed</c> → <c>ingesting</c>; the caller wipes
    /// the previous attempt's data and re-enqueues the <c>ProjectBuild</c> job.
    /// Refuses anything else with a field-keyed (<c>Retry</c>) error. See the
    /// <c>/recover-symbols</c> endpoint and
    /// <c>.design/object-explorer-project-builds.md</c>.
    /// </summary>
    public async Task ReopenForRebuildAsync(int releaseId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var release = await _db.OeReleases
            .SingleOrDefaultAsync(r => r.Id == releaseId, ct).ConfigureAwait(false)
            ?? throw RetryError($"Release {releaseId} not found in this organisation.");

        if (release.DeletedAt is not null)
        {
            throw RetryError("This release is soft-deleted. Restore it before rebuilding.");
        }
        if (!string.Equals(release.Kind, "project", StringComparison.Ordinal))
        {
            throw RetryError("Only a project build can be rebuilt this way.");
        }
        if (release.Status is not ("ready" or "failed"))
        {
            throw RetryError($"This release isn't in a rebuildable state (status = {release.Status}). Wait for the current build to finish.");
        }

        release.Status = "ingesting";
        release.StatusMessage = null;
        release.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Reopened project Release {ReleaseId} ({Label}) for rebuild.", release.Id, release.Label);
    }

    private static PlanValidationException RetryError(string message) =>
        new(new Dictionary<string, string> { ["Retry"] = message });

    /// <summary>
    /// Refuses a dedup key that's already in use by another active Release in the
    /// same org. The DB also enforces this via
    /// <c>ix_oe_releases_org_dedup_key_active</c> (partial unique index on
    /// <c>(organization_id, dedup_key)</c> filtered by
    /// <c>deleted_at IS NULL AND dedup_key IS NOT NULL</c>) — the pre-check exists
    /// so callers get a clean field-keyed error instead of a raw Postgres 23505.
    /// Soft-deleted keys remain reusable since the partial index excludes them.
    ///
    /// <para>
    /// Releases without a dedup key (manual uploads, manual third-party imports,
    /// project builds) are never deduped — the <see cref="OeRelease.Label"/> is a
    /// pure display string, free to repeat. First-party artifact imports set a key
    /// (<c>bc-onprem:{Maj}.{Min}:{cc}</c>) as the daily sweep's idempotency
    /// guarantee, and so do the vendor symbols a pipeline build ingests
    /// (<c>symbols:{appId}:{version}</c>, #901) so every build shares one copy.
    /// See <c>.design/roadmap.md</c> ("Harden first-party dedup, then free the
    /// label").
    /// </para>
    /// </summary>
    private async Task EnsureDedupKeyAvailableAsync(int orgId, string? dedupKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dedupKey)) return;

        var taken = await _db.OeReleases.AsNoTracking()
            .AnyAsync(r => r.OrganizationId == orgId && r.DeletedAt == null && r.DedupKey == dedupKey, ct)
            .ConfigureAwait(false);
        if (taken)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["DedupKey"] = $"A Release with dedup key \"{dedupKey}\" already exists in this organisation.",
            });
        }
    }

    // ── Per-app ingest ──────────────────────────────────────────────────

    private async Task ImportOneAppAsync(
        int orgId, OeRelease release, AppFileUpload upload, ImportTotals totals,
        bool storeSymbolReference, CancellationToken ct)
    {
        // Pull the .app's Translations/*.xlf out only for first-party (Microsoft)
        // releases: those are the DVDs whose language packs seed the org-wide
        // translation memory. Partner/project imports skip the extra work — an
        // admin can still upload their XLIFFs on demand. See .design/translator/.
        // A preview build is skipped too: its captions are still changing and
        // would seed the memory with strings the shipped release may not carry.
        var captureTranslations = string.Equals(release.Kind, "first_party", StringComparison.Ordinal) && !release.IsPrerelease;

        AppPackage pkg;
        try
        {
            pkg = await AppPackageReader.ReadAsync(upload.AppStream, storeSymbolReference, captureTranslations, ct).ConfigureAwait(false);
        }
        catch (NeaEncryptedAppException ex)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [$"Uploads.{upload.FileName}"] = ex.Message,
            });
        }

        // Idempotency: if this Release already has a module with the same
        // (AppId, Version) AND the same byte hash, treat the upload as a
        // silent no-op. Same (AppId, Version) with a *different* hash is a
        // genuine surprise — the AL ecosystem doesn't rebuild .apps with
        // identical (AppId, Version) and different bytes — so we surface it
        // as an error.
        var existing = await _db.OeModules
            .AsNoTracking()
            .Where(m => m.ReleaseId == release.Id && m.AppId == pkg.Manifest.AppId && m.Version == pkg.Manifest.Version)
            .Select(m => new { m.Id, m.AppFileHash })
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);

        if (existing is not null)
        {
            if (string.Equals(existing.AppFileHash, pkg.AppFileHash, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Skipping byte-identical re-upload of {File} (AppId={AppId} Version={Version}) into ReleaseId={ReleaseId}",
                    upload.FileName, pkg.Manifest.AppId, pkg.Manifest.Version, release.Id);
                totals.ModulesSkipped++;
                return;
            }
            throw new PlanValidationException(new Dictionary<string, string>
            {
                [$"Uploads.{upload.FileName}"] =
                    $".app with AppId {pkg.Manifest.AppId} version {pkg.Manifest.Version} already exists in this Release with a different content hash. Start a new Release instead of overwriting.",
            });
        }

        // Source priority: a paired .Source.zip wins over the .app's
        // embedded source whenever one was uploaded alongside the .app.
        // Microsoft's BC 28+ first-party modules ship as Ready2Run wrappers
        // whose inner .app's embedded source is partial — the canonical
        // full source tree sits in the sibling <Name>.Source.zip on the
        // DVD. Falling back to pkg.SourceFiles only when no .Source.zip
        // was provided keeps single-file partner uploads (which never pair
        // a zip) working as before.
        //
        // Both branches dedupe by path with last-write-wins. Microsoft's
        // System.app (observed on BC 28.1) ships duplicate entries that
        // normalise to the same canonical path (e.g. two src/dotnet.al's);
        // failing the entire 110-module import on a content collision the
        // user can't fix isn't worth it, so we keep one and log a warning.
        IReadOnlyDictionary<string, string> sourceFiles;
        if (upload.SourceZipStream is not null)
        {
            var fromZip = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in ReleaseSourceScanner.ReadSourceZip(upload.SourceZipStream))
            {
                if (fromZip.ContainsKey(path))
                {
                    _logger.LogWarning(
                        "Duplicate source path in .Source.zip for {Module}: {Path} — keeping last occurrence",
                        pkg.Manifest.Name, path);
                }
                fromZip[path] = content;
            }
            sourceFiles = fromZip;
        }
        else
        {
            var fromEmbedded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in pkg.SourceFiles)
            {
                if (fromEmbedded.ContainsKey(file.Path))
                {
                    _logger.LogWarning(
                        "Duplicate embedded source path in {Module}: {Path} — keeping last occurrence",
                        pkg.Manifest.Name, file.Path);
                }
                fromEmbedded[file.Path] = file.Content;
            }
            sourceFiles = fromEmbedded;
        }

        await WriteModuleAsync(orgId, release, upload, pkg, sourceFiles, storeSymbolReference, totals, ct).ConfigureAwait(false);
    }

    private async Task WriteModuleAsync(
        int orgId, OeRelease release,
        AppFileUpload upload,
        AppPackage pkg,
        IReadOnlyDictionary<string, string> sourceFiles,
        bool storeSymbolReference,
        ImportTotals totals,
        CancellationToken ct)
    {
        // Optionally persist the raw SymbolReference.json for resolver
        // debugging. Upsert the content into the shared store FIRST so the
        // module's FK on symbol_reference_content_hash is satisfied when the
        // row is inserted below. Deduped by hash like source files, so a
        // re-import of the same module version doesn't duplicate the blob.
        string? symbolReferenceHash = null;
        if (storeSymbolReference && !string.IsNullOrEmpty(pkg.SymbolReferenceJson))
        {
            var json = pkg.SymbolReferenceJson;
            symbolReferenceHash = HashHex(json);
            await UpsertFileContentsAsync(
                new Dictionary<string, (string Content, int Length, int LineCount)>(StringComparer.Ordinal)
                {
                    [symbolReferenceHash] = (json, json.Length, CountLines(json)),
                },
                ct).ConfigureAwait(false);
        }

        var module = new OeModule
        {
            OrganizationId = orgId,
            ReleaseId = release.Id,
            AppId = pkg.Manifest.AppId,
            Name = pkg.Manifest.Name,
            Publisher = pkg.Manifest.Publisher,
            Version = pkg.Manifest.Version,
            Target = pkg.Manifest.Target,
            Runtime = pkg.Manifest.Runtime,
            SymbolReferenceContentHash = symbolReferenceHash,
            // Flags come from the upload-layer inference (folder names,
            // _Exclude_ marker, language-pack name pattern). The per-file
            // upload path leaves all three at false; the folder-ZIP path
            // sets them based on the DVD's folder conventions.
            IsTest = upload.IsTest,
            IsInternal = upload.IsInternal,
            IsLanguagePack = upload.IsLanguagePack,
            DependenciesJson = ReleaseSourceScanner.SerializeDeps(pkg.Manifest.Dependencies),
            DependencyCount = pkg.Manifest.Dependencies.Count,
            AppFileHash = pkg.AppFileHash,
            CreatedAt = DateTime.UtcNow,
        };
        _db.OeModules.Add(module);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Files first so we can resolve ModuleObject.SourceFileId on the way.
        // Symbol-package ReferenceSourceFileName is the full relative path
        // (e.g. "src/Codeunits/DKCoreEventSubscribers.Codeunit.al"), so we
        // key by full path. The parser already normalised both the embedded
        // src/ tree (.app) and the paired .Source.zip to the same "src/…" shape.
        //
        // SaveChanges in chunks of FileChunkSize: Base App can carry several
        // thousand .al files with multi-KB Content each, and EF's batch
        // builder allocates the whole batch text + parameter array in
        // memory. Bounded chunks keep the per-flush memory footprint flat.
        var filesByPath = new Dictionary<string, OeModuleFile>(StringComparer.OrdinalIgnoreCase);
        // Per-chunk dedup of source blobs, keyed by hash, upserted into the
        // shared oe_file_contents store BEFORE the file rows that reference them
        // (FK on content_hash). Keyed by hash so two identical files in the same
        // chunk produce one blob.
        var pendingContent = new Dictionary<string, (string Content, int Length, int LineCount)>(StringComparer.Ordinal);
        int filesPending = 0;
        foreach (var (path, content) in sourceFiles)
        {
            var hash = HashHex(content);
            var lineCount = CountLines(content);
            var file = new OeModuleFile
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Path = path,
                ContentHash = hash,
                LineCount = lineCount,
            };
            _db.OeModuleFiles.Add(file);
            filesByPath[path] = file;
            pendingContent[hash] = (content, content.Length, lineCount);
            totals.SourceFilesImported++;
            filesPending++;
            if (filesPending >= FileChunkSize)
            {
                await UpsertFileContentsAsync(pendingContent, ct).ConfigureAwait(false);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                pendingContent.Clear();
                filesPending = 0;
            }
        }
        if (filesPending > 0)
        {
            await UpsertFileContentsAsync(pendingContent, ct).ConfigureAwait(false);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        // Build the (Kind, Name) → (File, Line) index used to link
        // symbol-package objects to their .al file. We deliberately do
        // *not* use ReferenceSourceFileName: it's path-string-based and
        // the .Source.zip layouts Microsoft ships are inconsistent
        // enough within a single release that no canonicaliser can
        // bridge the gap (System Application Test Library uses
        // "Password/src/LibraryPassword.Codeunit.al", Business Foundation
        // Test Libraries uses "NoSeries/src/LibraryNoSeries.Codeunit.al",
        // first-party Base App uses bare "Codeunits/Foo.Codeunit.al" —
        // all in BC 28.1 DK). The AL declaration at the top of each .al
        // file is deterministic; AL enforces one object per file in
        // practice; matching by that header is the stable contract.
        var declarations = ReleaseSourceScanner.ScanFileDeclarations(filesByPath, sourceFiles);

        // Same pass, deeper: run the AL symbol extractor over each file so we
        // can stamp line/column on every sub-symbol (procedure / trigger /
        // event publisher/subscriber / field) and emit rows for ones the
        // symbol package doesn't ship (local procedures, triggers). Keyed by
        // file path so the per-object loop below can grab its file's symbols
        // in O(1). Files with no source — third-party modules built with
        // IncludeSourceInSymbolFile="false" and no paired .Source.zip — have
        // no entry here; sub-symbols for those objects stay at LineNumber=0.
        var extractedByPath = ReleaseSourceScanner.ExtractSubSymbolsByFile(sourceFiles);

        // Each chunk holds the object + every symbol/variable/reference row
        // that references it via navigation. Saving them together lets EF
        // resolve the dependent FKs from the principal's freshly-generated
        // Id; the tracker clear between chunks drops the per-chunk memory
        // pressure so a 5000-object Base App doesn't grow unbounded.
        int objectsPending = 0;
        int objectsExpectingSource = 0;
        int objectsLinked = 0;
        foreach (var symObj in pkg.Symbols.Objects)
        {
            ct.ThrowIfCancellationRequested();

            OeModuleFile? sourceFile = null;
            int line = 1;
            // ReferenceSourceFileName drives the "should this object
            // have linked?" diagnostic counter only — the actual link
            // is established by matching the AL header in the .al
            // file, which is layout-agnostic.
            if (!string.IsNullOrEmpty(symObj.ReferenceSourceFileName))
            {
                objectsExpectingSource++;
            }
            string? sourceExtends = null;
            string? sourceTableFromHeader = null;
            if (declarations.TryGetValue((symObj.Kind, symObj.Name), out var hit))
            {
                sourceFile = hit.File;
                line = hit.Line;
                sourceExtends = hit.ExtendsName;
                sourceTableFromHeader = hit.SourceTable;
                objectsLinked++;
            }

            // Interface inheritance fallback: BC's symbol package
            // doesn't surface the `extends` pointer for interfaces via
            // the usual TargetObject path, so a derived interface
            // (`interface "Cost Adjustment With Params" extends
            // "Inventory Adjustment"`) lands with ExtendsObjectName=null
            // and the resolver's interface-extends walk has nothing to
            // chase. Backfill from the source-side header scan, scoped
            // to the same module (interfaces extending across module
            // boundaries are vanishingly rare and we can't tell which
            // app the base belongs to without resolving downstream).
            var extendsName = symObj.ExtendsObjectName;
            var extendsAppId = symObj.ExtendsAppId;
            if (string.IsNullOrEmpty(extendsName)
                && string.Equals(symObj.Kind, "interface", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(sourceExtends))
            {
                extendsName = sourceExtends;
                extendsAppId = module.AppId;
            }

            var obj = new OeModuleObject
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Kind = symObj.Kind,
                ObjectId = symObj.ObjectId,
                Name = symObj.Name,
                Namespace = string.IsNullOrEmpty(symObj.Namespace) ? null : symObj.Namespace,
                ExtendsAppId = extendsAppId,
                ExtendsObjectName = extendsName,
                // SourceTable on pages — extracted from the symbol package's
                // property list. Pageextensions don't carry it directly; a
                // second pass below copies the value from their base page.
                // SourceTable resolution order depends on the object
                // kind. Pages / codeunits get a reliable SourceTable /
                // TableNo on the symbol package's top-level Properties
                // list — that path stays authoritative. Reports nest
                // the property inside `requestpage { ... }` and BC's
                // symbol package doesn't always surface it at the
                // report's own Properties list (Whse. Change Unit of
                // Measure / VAT Report Suggest Lines), so source-side
                // wins for reports and the package side serves as the
                // fallback when source isn't paired (third-party
                // modules with no .Source.zip).
                SourceTableName =
                    (string.Equals(symObj.Kind, "report", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(symObj.Kind, "reportextension", StringComparison.OrdinalIgnoreCase))
                        ? (sourceTableFromHeader ?? ReleaseSourceScanner.ExtractSourceTableName(symObj))
                        : (ReleaseSourceScanner.ExtractSourceTableName(symObj) ?? sourceTableFromHeader),
                // Use the FK directly rather than the navigation: after the
                // file-chunk save loop above, the file entity may have been
                // detached from the tracker on a previous flush. The Id is
                // intact on the captured reference; bind by Id to side-step
                // any tracker-state assumption.
                SourceFileId = sourceFile?.Id,
                LineNumber = line,
                ObsoleteState = ReleaseSourceScanner.NullIfBlank(symObj.Properties.FirstOrDefault(p =>
                    string.Equals(p.Name, "ObsoleteState", StringComparison.OrdinalIgnoreCase))?.Value),
            };
            _db.OeModuleObjects.Add(obj);
            totals.ObjectsImported++;

            // Only feed the extractor's symbols in when the file's primary
            // object_declaration matches this symObj — for multi-object .al
            // files the extractor only marks the first object, so anything
            // past that risks attributing the second object's procedures to
            // the first. Bound the over-attribution by being strict here.
            IReadOnlyList<AlSymbol> extractedForObject = Array.Empty<AlSymbol>();
            if (sourceFile is not null && extractedByPath.TryGetValue(sourceFile.Path, out var extracted))
            {
                var primaryDecl = extracted.FirstOrDefault(s => s.Kind == "object_declaration");
                if (primaryDecl is not null
                    && string.Equals(primaryDecl.Name, symObj.Name, StringComparison.OrdinalIgnoreCase))
                {
                    extractedForObject = extracted;
                }
            }

            EmitSymbols(orgId, module, obj, symObj, extractedForObject);
            EmitVariables(orgId, module, obj, symObj, extractedForObject);
            EmitReferences(orgId, module, obj, symObj, totals);

            objectsPending++;
            if (objectsPending >= ObjectChunkSize)
            {
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                // Drop tracker state once the chunk is committed. The next
                // iteration's object will only navigate via SourceFileId
                // (already a primitive FK), so detaching the file entities
                // here is fine.
                _db.ChangeTracker.Clear();
                objectsPending = 0;
            }
        }
        if (objectsPending > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        totals.ModulesImported++;

        // First-party (Microsoft) release ingest now extracts the module's
        // Translations/*.xlf and feeds the org-wide translation memory. Only
        // populated when captureTranslations was set (see ImportOneAppAsync);
        // the streaming AlXliffParser keeps this off the OOM path that made it
        // admin-upload-only. Best-effort — a bad XLIFF must not sink the ingest.
        if (pkg.Translations.Count > 0)
        {
            await ImportModuleTranslationsAsync(module, pkg, totals, ct).ConfigureAwait(false);
        }

        // Surface a warning when source files were loaded but no
        // symbol-package objects matched any .al header — that means
        // either the .Source.zip doesn't contain the .al files for
        // those objects, or the file headers don't agree with the
        // symbol package's (Kind, Name) pairs (a Microsoft-side
        // packaging change). The example expected object + example
        // declaration help diagnose which case it is.
        if (filesByPath.Count > 0 && objectsExpectingSource > 0 && objectsLinked == 0)
        {
            var expectedObj = pkg.Symbols.Objects.FirstOrDefault(o => !string.IsNullOrEmpty(o.ReferenceSourceFileName));
            var declaredExample = declarations.Keys.FirstOrDefault();
            _logger.LogWarning(
                "Module {Name} {Version}: {FileCount} source file(s) loaded with {DeclCount} header declaration(s); "
                + "0/{Expected} symbol-package objects matched any .al header. "
                + "Expected example: {ExpectedKind} \"{ExpectedName}\". Declared example: {DeclaredKind} \"{DeclaredName}\". "
                + "The .Source.zip may not contain .al files for these objects, or the headers no longer match.",
                pkg.Manifest.Name, pkg.Manifest.Version,
                filesByPath.Count, declarations.Count, objectsExpectingSource,
                expectedObj?.Kind ?? "(none)", expectedObj?.Name ?? "(none)",
                declaredExample.Kind ?? "(none)", declaredExample.Name ?? "(none)");
        }

        // Clear the tracker between modules so a release-wide import doesn't
        // turn into an O(n²) walk over an ever-growing change-tracker.
        _db.ChangeTracker.Clear();
    }

    /// <summary>
    /// Streams each <c>Translations/*.xlf</c> captured from a first-party
    /// module's <c>.app</c> through the (streaming) parser and into
    /// <see cref="TranslationImportService"/>, which clobbers + inserts
    /// <c>oe_module_translations</c> rows for the containing module and upserts
    /// the source→target pairs into the org-wide translation memory. We attach to
    /// the <em>containing</em> module rather than the XLIFF's
    /// <c>&lt;file original&gt;</c> app because the full release isn't in the DB
    /// yet during this per-module pass; the memory is keyed by text, not module,
    /// so the pairs land either way. Best-effort per file — a parse failure or a
    /// memory hiccup is logged and skipped so it can't sink the release ingest
    /// (the whole reason translations used to be admin-upload-only).
    /// </summary>
    private async Task ImportModuleTranslationsAsync(
        OeModule module, AppPackage pkg, ImportTotals totals, CancellationToken ct)
    {
        foreach (var xlf in pkg.Translations)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                XliffDocument parsed;
                using (var ms = new MemoryStream(xlf.Content, writable: false))
                {
                    parsed = AlXliffParser.Parse(ms);
                }

                // Prefer the XLIFF's own <file original> as the memory origin —
                // it names the translated app (e.g. "Base Application") — and
                // fall back to the containing module's name.
                var origin = string.IsNullOrEmpty(parsed.OriginalName) ? module.Name : parsed.OriginalName;
                var inserted = await _translations
                    .ImportForModuleAsync(module.Id, parsed, origin, ct)
                    .ConfigureAwait(false);
                totals.TranslationsImported += inserted;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Skipping translation {File} in module {Module} (id={ModuleId}); release ingest continues.",
                    xlf.FileName, module.Name, module.Id);
            }
        }
    }

    private const int FileChunkSize = OeIngestHelpers.FileChunkSize;
    private const int ObjectChunkSize = OeIngestHelpers.ObjectChunkSize;

    private void EmitSymbols(
        int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj,
        IReadOnlyList<AlSymbol> extractedSymbols)
    {
        // The symbol package only ships public + internal methods; locals,
        // triggers, and event subscribers exist only in source. Index the
        // extractor's findings so the symbol-package loop below can stamp
        // line/column on matches, and so we can emit additional rows for
        // anything the extractor saw that the package omitted. Procedures
        // are kept as a queue per name so each overload of a method picks
        // up a distinct line — symbol-package method ordering tracks the
        // source declaration order in practice.
        var procQueueByName = new Dictionary<string, Queue<AlSymbol>>(StringComparer.OrdinalIgnoreCase);
        var fieldByName = new Dictionary<string, AlSymbol>(StringComparer.OrdinalIgnoreCase);
        var fieldById = new Dictionary<int, AlSymbol>();
        foreach (var sym in extractedSymbols)
        {
            switch (sym.Kind)
            {
                case "procedure":
                case "local_procedure":
                case "internal_procedure":
                case "protected_procedure":
                case "trigger":
                case "event_publisher":
                case "event_subscriber":
                    if (!procQueueByName.TryGetValue(sym.Name, out var queue))
                    {
                        queue = new Queue<AlSymbol>();
                        procQueueByName[sym.Name] = queue;
                    }
                    queue.Enqueue(sym);
                    break;
                case "table_field":
                    // Only table-side fields ship in symObj.Fields, so only
                    // table_field needs to seed the dedup index. Page fields
                    // (page_field, emitted by the source extractor) never
                    // collide here — they fall through to the source-only
                    // re-emission loop below.
                    fieldByName.TryAdd(sym.Name, sym);
                    if (sym.FieldId is { } id) fieldById.TryAdd(id, sym);
                    break;
            }
        }

        var consumedExtracted = new HashSet<AlSymbol>();

        foreach (var method in symObj.Methods)
        {
            var kind = method.IsInternal ? "internal_procedure" : "procedure";
            int line = 0, colStart = 0, colEnd = 0;
            // Doc comments are dropped by the AL compiler, so the package
            // side of this merge never has one — the description only ever
            // arrives with the source-extracted match.
            string? doc = null;
            if (procQueueByName.TryGetValue(method.Name, out var queue) && queue.Count > 0)
            {
                var extracted = queue.Dequeue();
                consumedExtracted.Add(extracted);
                line = extracted.LineNumber;
                colStart = extracted.ColumnStart;
                colEnd = extracted.ColumnEnd;
                doc = extracted.Doc;
                // event_publisher / event_subscriber / local_procedure /
                // protected_procedure carry more specific intent than the
                // package's IsInternal bit — prefer the extractor's kind
                // when it's one of those, but never downgrade
                // internal_procedure to plain procedure.
                if (extracted.Kind is "event_publisher" or "event_subscriber" or "protected_procedure")
                {
                    kind = extracted.Kind;
                }
                else if (extracted.Kind == "internal_procedure")
                {
                    kind = "internal_procedure";
                }
            }
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = kind,
                Name = method.Name,
                Signature = ReleaseSourceScanner.RenderSignature(method),
                ReturnType = CallSiteReferenceEmitter.FormatReturnType(method.ReturnType),
                Doc = doc,
                LineNumber = line,
                ColumnStart = colStart,
                ColumnEnd = colEnd,
            });
        }

        foreach (var field in symObj.Fields)
        {
            int line = 0, colStart = 0, colEnd = 0;
            string? doc = null;
            if (fieldById.TryGetValue(field.Id, out var extracted)
                || fieldByName.TryGetValue(field.Name, out extracted))
            {
                line = extracted.LineNumber;
                colStart = extracted.ColumnStart;
                colEnd = extracted.ColumnEnd;
                doc = extracted.Doc;
                // Mark the extracted field row consumed so the
                // page-field pass below doesn't re-emit it. Table-side
                // fields ship in symObj.Fields; page-side ones don't, so
                // we only need the dedup on table flows.
                consumedExtracted.Add(extracted);
            }
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                // symObj.Fields only carries table-side fields — page
                // fields aren't in symbol packages — so the persisted
                // kind is always table_field here. See
                // .design/al-reference-extractor-refactor.md step 1.
                Kind = "table_field",
                Name = field.Name,
                Signature = field.Type.Name,
                Doc = doc,
                FieldId = field.Id,
                LineNumber = line,
                ColumnStart = colStart,
                ColumnEnd = colEnd,
            });
        }

        // Locals / triggers / event subscribers / event publishers / page
        // fields / actions that the symbol package omits — pick them up
        // from the source extractor so the outline shows them.
        // consumedExtracted holds every AlSymbol already mapped into a
        // symbol-package row above, which also correctly handles
        // overloads (the queue dequeue gave each package method a distinct
        // extractor row) and table-field/page-field disambiguation
        // (table fields enter symObj.Fields and consume their matching
        // extractor row; page fields don't, so they fall through here).
        foreach (var sym in extractedSymbols)
        {
            switch (sym.Kind)
            {
                case "procedure":
                case "local_procedure":
                case "internal_procedure":
                case "protected_procedure":
                case "trigger":
                case "event_publisher":
                case "event_subscriber":
                case "table_field":
                case "page_field":
                case "page_action":
                case "query_column":
                    break;
                default:
                    continue;
            }
            if (consumedExtracted.Contains(sym)) continue;
            _db.OeModuleSymbols.Add(new OeModuleSymbol
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Kind = sym.Kind,
                Name = sym.Name,
                Signature = sym.Signature,
                Doc = sym.Doc,
                FieldId = sym.FieldId,
                LineNumber = sym.LineNumber,
                ColumnStart = sym.ColumnStart,
                ColumnEnd = sym.ColumnEnd,
            });
        }
    }

    private void EmitVariables(
        int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj,
        IReadOnlyList<AlSymbol> extractedSymbols)
    {
        // Symbol packages carry variable name + type but not source
        // positions; the source extractor's var_declaration rows fill
        // that gap. First-declaration-wins on name collisions — in
        // practice, object-scope globals appear in the file before any
        // procedure-local var with the same name. See
        // .design/al-reference-extractor-refactor.md step 2.
        var positionsByName = new Dictionary<string, AlSymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in extractedSymbols)
        {
            if (sym.Kind != "var_declaration") continue;
            positionsByName.TryAdd(sym.Name, sym);
        }

        foreach (var variable in symObj.Variables)
        {
            var (targetKind, targetId, targetName, typeKeyword) = ReleaseSourceScanner.ResolveVariableTarget(variable.Type, module.AppId);
            positionsByName.TryGetValue(variable.Name, out var pos);
            _db.OeModuleVariables.Add(new OeModuleVariable
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Name = variable.Name,
                TypeKeyword = typeKeyword,
                TypeName = variable.Type.ObjectName ?? variable.Type.Name,
                TargetAppId = variable.Type.ModuleId,
                TargetObjectKind = targetKind,
                TargetObjectId = targetId,
                TargetObjectName = targetName,
                LineNumber = pos?.LineNumber ?? 0,
                ColumnStart = pos?.ColumnStart ?? 0,
                ColumnEnd = pos?.ColumnEnd ?? 0,
            });
        }

        // Dataitem / tableelement aliases — recorded from source
        // because SymbolReference.json doesn't surface them in the
        // Variables list. Stamped as Record-typed globals so the
        // reportextension / base-globals scope merge (PR #252)
        // automatically threads the base report's aliases through
        // to extension procedures. Without this, every reportextension
        // reference to a base-report alias (e.g. `FilterItem` in
        // AsmGetDemandToReserve.ReportExt against GetDemandToReserve)
        // fires head-not-a-variable.
        //
        // TargetAppId stays null at insert time — we don't have the
        // catalog built yet during the per-object loop. The
        // post-import pass <see cref="ResolveDataItemAliasTargetsAsync"/>
        // walks every Record-typed variable row with a null
        // TargetAppId in this release and stamps the source table's
        // identity from the catalog. Picking up regular same-module
        // Record vars whose ModuleId was omitted by the symbol
        // package is a beneficial side-effect.
        var packageVariableNames = new HashSet<string>(
            symObj.Variables.Select(v => v.Name),
            StringComparer.OrdinalIgnoreCase);
        var seenAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sym in extractedSymbols)
        {
            if (sym.Kind != "dataitem_alias") continue;
            if (string.IsNullOrEmpty(sym.Name) || string.IsNullOrEmpty(sym.Signature)) continue;
            // Skip when the package's own Variables list already
            // claims this name — the package's metadata wins because
            // it carries TypeKeyword / TypeName the binary compiler
            // resolved. Nested dataitems repeating their parent's
            // alias also get skipped against ourselves.
            if (packageVariableNames.Contains(sym.Name)) continue;
            if (!seenAliases.Add(sym.Name)) continue;
            _db.OeModuleVariables.Add(new OeModuleVariable
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                Object = obj,
                Name = sym.Name,
                TypeKeyword = "Record",
                TypeName = sym.Signature,
                TargetAppId = null,
                TargetObjectKind = "table",
                TargetObjectId = null,
                TargetObjectName = sym.Signature,
                LineNumber = sym.LineNumber,
                ColumnStart = sym.ColumnStart,
                ColumnEnd = sym.ColumnEnd,
            });
        }
    }

    private void EmitReferences(int orgId, OeModule module, OeModuleObject obj, SymbolObject symObj, ImportTotals totals)
    {
        // 1. extends_target — for *extension kinds, the base object lives in
        //    another module (or, for same-publisher cases, this one).
        if (symObj.ExtendsAppId is not null && !string.IsNullOrEmpty(symObj.ExtendsObjectName))
        {
            var extendedKind = symObj.Kind switch
            {
                "tableextension"          => "table",
                "pageextension"           => "page",
                "reportextension"         => "report",
                "enumextension"           => "enum",
                "permissionsetextension"  => "permissionset",
                _ => null,
            };
            if (extendedKind is not null)
            {
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = symObj.ExtendsAppId.Value,
                    TargetObjectKind = extendedKind,
                    TargetObjectName = symObj.ExtendsObjectName!,
                    ReferenceKind = "extends_target",
                });
                totals.ReferencesImported++;
            }
        }

        // 2. variable_type — one ref per AL-object-typed object-scoped variable.
        foreach (var variable in symObj.Variables)
        {
            var (kind, id, name, _) = ReleaseSourceScanner.ResolveVariableTarget(variable.Type, module.AppId);
            if (kind is null || name is null) continue;     // non-AL type or unresolved.
            var targetAppId = variable.Type.ModuleId ?? module.AppId;
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                SourceObject = obj,
                TargetAppId = targetAppId,
                TargetObjectKind = kind,
                TargetObjectId = id,
                TargetObjectName = name,
                ReferenceKind = "variable_type",
            });
            totals.ReferencesImported++;
        }

        // 3. return_type — one ref per AL-object-typed procedure return.
        foreach (var method in symObj.Methods)
        {
            if (method.ReturnType is null) continue;
            var (kind, id, name, _) = ReleaseSourceScanner.ResolveVariableTarget(method.ReturnType, module.AppId);
            if (kind is null || name is null) continue;
            var targetAppId = method.ReturnType.ModuleId ?? module.AppId;
            _db.OeModuleReferences.Add(new OeModuleReference
            {
                OrganizationId = orgId,
                ModuleId = module.Id,
                SourceObject = obj,
                TargetAppId = targetAppId,
                TargetObjectKind = kind,
                TargetObjectId = id,
                TargetObjectName = name,
                ReferenceKind = "return_type",
            });
            totals.ReferencesImported++;
        }

        // 4. parameter_type — one ref per AL-object-typed parameter.
        foreach (var method in symObj.Methods)
        {
            foreach (var param in method.Parameters)
            {
                var (kind, id, name, _) = ReleaseSourceScanner.ResolveVariableTarget(param.Type, module.AppId);
                if (kind is null || name is null) continue;
                var targetAppId = param.Type.ModuleId ?? module.AppId;
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = targetAppId,
                    TargetObjectKind = kind,
                    TargetObjectId = id,
                    TargetObjectName = name,
                    ReferenceKind = "parameter_type",
                });
                totals.ReferencesImported++;
            }
        }

        // 5. table_no — codeunit "TableNo" property. The value is a raw
        //    "#<32hex>#<name>" string just like ExtendsTarget.
        var tableNo = symObj.Properties.FirstOrDefault(p =>
            string.Equals(p.Name, "TableNo", StringComparison.OrdinalIgnoreCase));
        if (tableNo is not null)
        {
            var (appId, name) = ReleaseSourceScanner.ParseHashRef(tableNo.Value);
            if (appId is not null && name is not null)
            {
                _db.OeModuleReferences.Add(new OeModuleReference
                {
                    OrganizationId = orgId,
                    ModuleId = module.Id,
                    SourceObject = obj,
                    TargetAppId = appId.Value,
                    TargetObjectKind = "table",
                    TargetObjectName = name,
                    ReferenceKind = "table_no",
                });
                totals.ReferencesImported++;
            }
        }
    }

    // Persistence helpers (hashing, blob upsert, line counting) and the
    // per-flush chunk sizes now live in OeIngestHelpers, shared with the
    // C/AL ingest path. Thin instance wrappers keep the call sites below
    // unchanged.
    private static string HashHex(string content) => OeIngestHelpers.HashHex(content);
    private static int CountLines(string content) => OeIngestHelpers.CountLines(content);
    private Task UpsertFileContentsAsync(
        IReadOnlyDictionary<string, (string Content, int Length, int LineCount)> contents,
        CancellationToken ct)
        => OeIngestHelpers.UpsertFileContentsAsync(_db, contents, ct);

    // ── BC version inference ────────────────────────────────────────────

    /// <summary>
    /// Asks the drift scan which tracked GitHub repositories are now behind, for
    /// a first-party Release that has just gone <c>ready</c>. Anything else - a
    /// pipeline build, a partner upload, a C/AL export - is not a Business
    /// Central version anybody's <c>app.json</c> targets, so nothing is scanned.
    ///
    /// <para>Every failure is swallowed on purpose. The import is finished by the
    /// time this runs; a scan that could not reach GitHub is a warning in the log
    /// and a panel that is a day out of date, not a Release marked failed. See
    /// <c>.design/github-integration-phase2.md</c>, issue #630.</para>
    /// </summary>
    private async Task ScanForDependencyDriftAsync(OeRelease release, CancellationToken ct)
    {
        // A preview never drives the scan: it would tell every solution it is
        // behind a version customers can't run yet (DependencyDriftService
        // ignores previews when picking the newest release for the same reason).
        if (_drift is null || release.Kind != "first_party" || release.IsPrerelease) return;

        try
        {
            var found = await _drift.ScanForReleaseAsync(release.Id, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Dependency drift scan after Release {ReleaseId} recorded {FindingCount} findings.",
                release.Id, found);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not check the tracked repositories for dependency drift after Release {ReleaseId}; the import itself is unaffected.",
                release.Id);
        }
    }

    /// <summary>
    /// Picks a stable platform/application version label from the Modules just
    /// imported. Microsoft's Base Application carries the canonical version
    /// stamp; when it's present we use that. Otherwise leave null and let the
    /// admin set <see cref="ReleaseImportRequest.ApplicationVersionId"/> by
    /// hand on retry. Reads from the DB rather than tracker state because
    /// per-module SaveChanges has cleared the tracker by now.
    /// </summary>
    private async Task<string?> InferBcVersionAsync(int releaseId, CancellationToken ct)
    {
        var baseApp = await _db.OeModules.AsNoTracking()
            .Where(m => m.ReleaseId == releaseId
                && m.Publisher == "Microsoft"
                && (m.Name == "Base Application" || m.Name == "Application"))
            .Select(m => m.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return baseApp;
    }

    /// <summary>
    /// Second pass over the just-imported release: for each pageextension,
    /// look up the page it extends (by ExtendsAppId + ExtendsObjectName,
    /// considering modules visible to the extension's module) and copy the
    /// base page's <c>SourceTableName</c> onto the extension. Done as a
    /// single UPDATE … FROM SQL because EF Core can't express the
    /// self-join cleanly and the per-row navigate-update approach would
    /// be hundreds of tracker-loaded entities for a large release.
    /// </summary>
    private async Task PropagateSourceTableToPageExtensionsAsync(int releaseId, CancellationToken ct)
    {
        // Match by ExtendsObjectName against any base page in the same
        // release. Cross-release base-page lookups (a project release
        // extending a Base App page from a parent release) are deferred
        // alongside the broader cross-release-shadowing gap — pageextension
        // .al source in the layered case is rare and the extractor
        // gracefully falls back to "Rec is the page itself" (still wrong,
        // still won't underline, but no crash).
        // Postgres UPDATE … FROM doesn't let the target alias (`ext`)
        // appear in an inner JOIN's ON clause — only in WHERE. So we
        // gate the extension's own release membership via a subquery
        // instead of joining oe_modules a second time on `ext.module_id`.
        const string sql = """
            UPDATE oe_module_objects ext
            SET source_table_name = base.source_table_name
            FROM oe_module_objects base
            JOIN oe_modules base_mod ON base_mod.id = base.module_id
            WHERE ext.kind = 'pageextension'
              AND ext.source_table_name IS NULL
              AND ext.extends_object_name IS NOT NULL
              AND base.kind = 'page'
              AND base.name = ext.extends_object_name
              AND base.source_table_name IS NOT NULL
              AND base_mod.release_id = {0}
              AND ext.module_id IN (
                  SELECT id FROM oe_modules WHERE release_id = {0}
              );
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Modern BC (28.x and later) emits a page's <c>SourceTable</c>
    /// property in the symbol package as the bare numeric object id
    /// (<c>"36"</c> for Sales Header), not the legacy
    /// <c>#&lt;appid&gt;#&lt;name&gt;</c> hash-ref format
    /// <see cref="ExtractSourceTableName"/> was originally written for.
    /// We pass the raw value through and resolve it here: for every
    /// page / pageextension in this release whose
    /// <c>source_table_name</c> is digit-only, look up the table with
    /// that <c>object_id</c> in the same release and replace the
    /// numeric value with the table's <c>name</c>.
    ///
    /// Same-release scoping is intentional. Tables in parent releases
    /// (cross-release shadowing) aren't reachable yet — that's gap #3
    /// in <c>al-reference-extractor-gaps.md</c>; the current pageext
    /// dependency-aware-resolver work would need a similar lift.
    ///
    /// Done as a single UPDATE … FROM for the same reason
    /// <see cref="PropagateSourceTableToPageExtensionsAsync"/> uses one:
    /// EF Core can't express the self-join cleanly and the per-row path
    /// is a tracker load for each page in a busy release.
    /// </summary>
    private async Task ResolveNumericSourceTableNamesAsync(int releaseId, CancellationToken ct)
    {
        // Filter includes codeunit alongside page / pageextension —
        // codeunits get source_table_name from their TableNo property
        // (a codeunit with TableNo binds Rec to the named table when run).
        const string sql = """
            UPDATE oe_module_objects pg
            SET source_table_name = t.name
            FROM oe_module_objects t
            JOIN oe_modules tm ON tm.id = t.module_id
            WHERE pg.kind IN ('page', 'pageextension', 'codeunit')
              AND pg.source_table_name ~ '^[0-9]+$'
              AND t.kind = 'table'
              AND t.object_id = pg.source_table_name::int
              AND tm.release_id = {0}
              AND pg.module_id IN (
                  SELECT id FROM oe_modules WHERE release_id = {0}
              );
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);

        // Second pass: numeric SourceTable values that fall in the BC
        // platform-table id range (2000000001 – 2000000999) don't match
        // any real table in oe_module_objects (the platform tables live
        // in the AL runtime, not in any module's symbol package). Walk
        // the PlatformVirtualTables map and rewrite the source_table_name
        // for any matching numeric value. Without this, every page with
        // `SourceTable = 2000000200` (NAV App Installed App) leaves
        // source_table_name as the numeric string, Rec binding becomes
        // `Record 2000000200`, and the chain-walker logs head-var-type-
        // unresolved on every Rec.X access.
        // One unnest-join UPDATE over the whole PlatformVirtualTables map rather
        // than ~170 sequential round-trips (one ExecuteSqlRaw per entry). Same
        // pattern as OeIngestHelpers.UpsertFileContentsAsync. See issue #383.
        var platformIds = new string[ReleaseImportAllowLists.PlatformVirtualTables.Length];
        var platformNames = new string[ReleaseImportAllowLists.PlatformVirtualTables.Length];
        {
            int i = 0;
            foreach (var vt in ReleaseImportAllowLists.PlatformVirtualTables)
            {
                platformIds[i] = vt.Id.ToString();
                platformNames[i] = vt.Name;
                i++;
            }
        }
        const string platformSql = """
            UPDATE oe_module_objects o
            SET source_table_name = v.name
            FROM unnest({0}::text[], {1}::text[]) AS v(id, name)
            WHERE o.source_table_name = v.id
              AND o.module_id IN (SELECT id FROM oe_modules WHERE release_id = {2});
            """;
        await _db.Database.ExecuteSqlRawAsync(
            platformSql,
            new object[] { platformIds, platformNames, releaseId },
            ct).ConfigureAwait(false);
    }

    // ── Mutable tally ───────────────────────────────────────────────────

    private sealed class ImportTotals
    {
        public int ModulesImported;
        public int ModulesSkipped;
        public int ObjectsImported;
        public int ReferencesImported;
        public int SourceFilesImported;
        // Trans-unit rows auto-extracted from first-party modules'
        // Translations/*.xlf during ingest (0 for partner/project imports,
        // which don't capture translations). Admins can still add more via
        // TranslationImportService's explicit upload paths afterwards.
        public int TranslationsImported;
    }

    /// <summary>
    /// Fills <c>TargetAppId</c> + <c>TargetObjectId</c> on
    /// <c>oe_module_variables</c> rows whose target object identity
    /// wasn't known at insert time. Two sources contribute:
    /// <list type="bullet">
    ///   <item>Dataitem / tableelement aliases emitted from source
    ///         extraction in <see cref="EmitVariables"/>. The catalog
    ///         isn't built during the per-object loop, so those rows
    ///         go in with null target identity and pick it up here.</item>
    ///   <item>Package-derived Record / Codeunit / Page variables whose
    ///         <c>SymbolTypeRef.ModuleId</c> wasn't set by the symbol
    ///         package (same-module references that the compiler
    ///         emitted without an explicit AppId). Beneficial side-
    ///         effect of the same name-based lookup.</item>
    /// </list>
    /// Runs as a single <c>UPDATE … FROM</c> per object kind so it's
    /// O(1) DB round-trips regardless of release size. Each kind needs
    /// its own statement because the join target's <c>kind</c> column
    /// has to match — sharing the join would over-match cross-kind
    /// name collisions (a codeunit and a page can share a name).
    /// </summary>
    private async Task ResolveVariableTargetsAsync(int releaseId, CancellationToken ct)
    {
        // Each kind in oe_module_variables points to a corresponding
        // oe_module_objects.kind. Wrap one UPDATE per kind; the index
        // ix_oe_module_variables_target_name keys on (TargetAppId,
        // TargetObjectKind, TargetObjectName) so the partial-NULL
        // filter still hits the index for the read side. The chosen
        // target row is the first one in_objectId order — ties
        // (cross-app collisions) settle deterministically by import
        // order; cross-release shadowing isn't modelled yet (same gap
        // as PropagateSourceTableToPageExtensionsAsync).
        const string sql = """
            UPDATE oe_module_variables v
            SET target_app_id = t.app_id_resolved,
                target_object_id = t.object_id_resolved
            FROM (
                SELECT DISTINCT ON (om.release_id, o.kind, LOWER(o.name))
                       om.release_id    AS release_id,
                       o.kind           AS kind,
                       LOWER(o.name)    AS name_lower,
                       o.object_id      AS object_id_resolved,
                       om.app_id        AS app_id_resolved
                FROM oe_module_objects o
                JOIN oe_modules om ON om.id = o.module_id
                WHERE om.release_id = {0}
                ORDER BY om.release_id, o.kind, LOWER(o.name), o.id
            ) t,
                 oe_modules vm
            WHERE v.module_id = vm.id
              AND vm.release_id = {0}
              AND v.target_object_kind = t.kind
              AND LOWER(v.target_object_name) = t.name_lower
              AND v.target_app_id IS NULL
              AND v.target_object_id IS NULL
              AND v.target_object_name IS NOT NULL;
            """;
        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { releaseId }, ct).ConfigureAwait(false);
    }
}
