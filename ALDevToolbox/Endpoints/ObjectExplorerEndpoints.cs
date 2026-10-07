using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// HTTP endpoints for the Object Explorer surface (Releases / Modules /
/// Find references). The bulk-upload endpoint lives here rather than on a
/// Blazor InteractiveServer page so a 1 GB DVD body can stream through
/// Kestrel instead of buffering through the SignalR circuit.
///
/// The form accepts either of two upload shapes:
/// <list type="bullet">
///   <item><c>FolderZip</c> — a single ZIP wrapping the DVD's
///         <c>applications/</c> folder tree. Walked server-side; each
///         <c>.app</c> entry is paired with its sibling <c>.Source.zip</c>
///         in the same directory and flag inference (test / internal /
///         language-pack) follows the DVD's folder conventions.</item>
///   <item><c>AppFiles</c> + <c>SourceZips</c> — individual file pickers,
///         useful for partner extensions you've built locally without the
///         DVD layout.</item>
/// </list>
///
/// Which of those (plus the URL and legacy C/AL TXT paths) an upload takes,
/// and everything that follows from the choice — temp-file staging, queueing,
/// the synchronous small import — is <see cref="ReleaseImportRequestService"/>'s
/// job. These handlers read the form, call it, and turn its outcome into the
/// redirect the admin page reads.
/// </summary>
internal static class ObjectExplorerEndpoints
{
    /// <summary>
    /// 1 GB cap on the multipart upload. BC 28.1's <c>applications/</c>
    /// folder zipped lands around 700 MB, so this gives a comfortable
    /// margin for the largest first-party DVD.
    /// </summary>
    public const long MaxUploadBytes = 1024L * 1024 * 1024;

    public static IEndpointRouteBuilder MapObjectExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/object-explorer/import", async (
            HttpContext ctx,
            ReleaseImportRequestService imports,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            int? parentReleaseId = null;
            if (int.TryParse(form["ParentReleaseId"].ToString(), out var pr) && pr > 0)
            {
                parentReleaseId = pr;
            }

            var submission = new ReleaseImportSubmission(
                Label: form["Label"].ToString().Trim(),
                Kind: form["Kind"].ToString().Trim(),
                ParentReleaseId: parentReleaseId,
                Publisher: form["Publisher"].ToString(),
                ProjectName: form["ProjectName"].ToString(),
                StoreSymbolReference: form["StoreSymbolReference"].ToString() is "true" or "on",
                DvdUrl: form["DvdUrl"].ToString().Trim(),
                CalEncoding: form["CalEncoding"].ToString() is { Length: > 0 } ce ? ce : "850",
                CalTxtFile: ToUpload(form.Files.GetFile("CalTxtFile")),
                FolderZip: ToUpload(form.Files.GetFile("FolderZip")),
                AppFiles: ToUploads(form, "AppFiles"),
                SourceZips: ToUploads(form, "SourceZips"));

            try
            {
                switch (await imports.SubmitAsync(submission, ct))
                {
                    case ReleaseImportOutcome.Imported imported:
                        var summary = imported.Summary;
                        var query = $"/object-explorer/release/{summary.ReleaseId}"
                            + $"?ok=imported"
                            + $"&modules={summary.ModulesImported}"
                            + $"&skipped={summary.ModulesSkipped}"
                            + $"&refs={summary.ReferencesImported}"
                            + $"&translations={summary.TranslationsImported}";
                        ctx.Response.Redirect(query);
                        break;

                    // A queued import and one whose staging ran out of disk both
                    // land on the release list: the row is there either way, and
                    // a failed row explains itself.
                    case ReleaseImportOutcome.Queued queued:
                        RedirectQueued(ctx, queued.ReleaseId);
                        break;
                    case ReleaseImportOutcome.StagingFailed staged:
                        RedirectQueued(ctx, staged.ReleaseId);
                        break;
                }
            }
            catch (PlanValidationException ex)
            {
                // URL/allow-list validation, label collisions, quota — all
                // field-keyed so the form renders them inline. Keep a C/AL
                // submission on the C/AL tab (its label/parent errors would
                // otherwise surface on Upload, which no longer has the field).
                var first = ex.Errors.First();
                var page = submission.IsCalImport ? "/admin/object-explorer/new/cal" : null;
                Redirect(ctx, first.Key, first.Value, page);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // ── Amend modules into an existing release (#216) ───────────────
        // Mirrors /admin/object-explorer/import but binds to an existing
        // release id; reuses the same FolderZip / AppFiles + SourceZips
        // form shape so the upload-building helpers come for free.
        // Same 1 GB cap — a late-landing partner DVD is rare but possible.
        app.MapPost("/admin/object-explorer/release/{releaseId:int}/modules", async (
            int releaseId,
            HttpContext ctx,
            ReleaseImportRequestService imports,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);

            try
            {
                var summary = await imports.AmendAsync(
                    releaseId,
                    ToUpload(form.Files.GetFile("FolderZip")),
                    ToUploads(form, "AppFiles"),
                    ToUploads(form, "SourceZips"),
                    ct);

                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/modules"
                    + $"?ok=amended"
                    + $"&modules={summary.ModulesImported}"
                    + $"&skipped={summary.ModulesSkipped}"
                    + $"&refs={summary.ReferencesImported}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectAmend(ctx, releaseId, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // The lightweight read-side /api/object-explorer/* GET endpoints the
        // static source viewer hits (go-to-definition, find-in-file, download,
        // SymbolReference stream, outline dependencies, references sessions)
        // live in a sibling file but register here, so Program.cs's single
        // MapObjectExplorerEndpoints() call still wires everything up. They're
        // RequireAuthorization() (any signed-in org user reads), unlike the
        // Admin,Editor authoring POSTs below.
        app.MapObjectExplorerViewerEndpoints();

        // ── Translation uploads (#151) ─────────────────────────────────
        // Two admin POSTs: single .xlf against one module, or per-release
        // ZIP holding many .xlf files matched to modules by the XLIFF's
        // <file original> attribute. Both clobber existing rows for the
        // affected (module, language) pairs so re-upload is the recovery
        // story when a translation needs updating. 64 MB cap — a single
        // .xlf is well under 5 MB and a 12-language ZIP comfortably fits.
        app.MapPost("/admin/object-explorer/release/{releaseId:int}/modules/{moduleId:long}/translations", async (
            int releaseId,
            long moduleId,
            HttpContext ctx,
            TranslationImportService translations,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("XliffFile");
            if (file is null || file.Length == 0)
            {
                RedirectTranslations(ctx, releaseId, "XliffFile", "Pick an .xlf file before submitting.");
                return;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var summary = await translations.ImportSingleAsync(releaseId, moduleId, stream, file.FileName, ct);
                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/translations"
                    + $"?ok=imported&lang={Uri.EscapeDataString(summary.LanguageCode)}"
                    + $"&module={Uri.EscapeDataString(summary.ModuleName)}"
                    + $"&count={summary.Inserted}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectTranslations(ctx, releaseId, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));

        app.MapPost("/admin/object-explorer/release/{releaseId:int}/translations-zip", async (
            int releaseId,
            HttpContext ctx,
            TranslationImportService translations,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("ZipFile");
            if (file is null || file.Length == 0)
            {
                RedirectTranslations(ctx, releaseId, "ZipFile", "Pick a ZIP file holding one or more .xlf files before submitting.");
                return;
            }
            try
            {
                await using var stream = file.OpenReadStream();
                var summary = await translations.ImportZipAsync(releaseId, stream, ct);
                ctx.Response.Redirect(
                    $"/admin/object-explorer/release/{releaseId}/translations"
                    + $"?ok=zip-imported&matched={summary.MatchedFiles}"
                    + $"&skipped={summary.UnmatchedFiles.Count}"
                    + $"&inserted={summary.TotalInserted}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectTranslations(ctx, releaseId, first.Key, first.Value);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(64L * 1024 * 1024));

        app.MapPost("/admin/object-explorer/{id:int}/soft-delete", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await management.SoftDeleteAsync(id, ct);
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=soft-deleted");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        // Retry a failed import in place — the rule for what a retry re-runs
        // from (the original URL, a freshly pasted one, a re-uploaded ZIP /
        // C-AL file, or a project's own source) lives on
        // ReleaseImportRequestService.RetryAsync. See the manage page.
        app.MapPost("/admin/object-explorer/{id:int}/retry", async (
            int id,
            HttpContext ctx,
            ReleaseImportRequestService imports,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var submission = new ReleaseRetrySubmission(
                DvdUrl: form["DvdUrl"].ToString().Trim(),
                CalEncoding: form["CalEncoding"].ToString() is { Length: > 0 } ce ? ce : "850",
                StoreSymbolReference: form["StoreSymbolReference"].ToString() is "true" or "on",
                FolderZip: ToUpload(form.Files.GetFile("FolderZip")),
                CalTxtFile: ToUpload(form.Files.GetFile("CalTxtFile")));

            try
            {
                switch (await imports.RetryAsync(id, submission, ct))
                {
                    case ReleaseImportOutcome.Queued:
                        ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=retry-queued");
                        break;
                    // Out of scratch disk: the failure is recorded on the release
                    // row, so the release list is where it explains itself.
                    case ReleaseImportOutcome.StagingFailed staged:
                        RedirectQueued(ctx, staged.ReleaseId);
                        break;
                }
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
            catch (ProjectAccessDeniedException)
            {
                RedirectManage(ctx, id, "Retry", CannotRebuild);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // Manual-symbols recovery: upload the dependency .app(s) a project build
        // couldn't resolve, store them against the project, and rebuild this same
        // release. Works on a partial (ready) build as well as a fully failed one —
        // the typical case is one extension that failed for a missing third-party
        // symbol while its siblings ingested. See
        // .design/object-explorer-project-builds.md ("Manual-symbols recovery").
        app.MapPost("/admin/object-explorer/{id:int}/recover-symbols", async (
            int id,
            HttpContext ctx,
            ProjectService projects,
            ReleaseImportService importer,
            ReleaseManagementService management,
            ReleaseImportRequestService imports,
            PersistedImportJobs persistedJobs,
            ProjectBuildImporter projectBuilds,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var origin = await persistedJobs.GetLatestForReleaseAsync(id, ct);
            if (origin is not { Kind: "project_build", ProjectId: int projectId })
            {
                RedirectManage(ctx, id, "Symbols", "This release isn't a project build, so there's nothing to recover.");
                return;
            }

            try
            {
                var form = await ctx.Request.ReadFormAsync(ct);
                var files = form.Files.GetFiles("Symbols").Where(f => f.Length > 0).ToList();
                if (files.Count == 0)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["Symbols"] = "Choose at least one .app symbol package to upload.",
                    });
                }

                var uploads = new List<SupplementalSymbolUpload>(files.Count);
                foreach (var file in files)
                {
                    using var buffer = new MemoryStream();
                    await using (var stream = file.OpenReadStream())
                    {
                        await stream.CopyToAsync(buffer, ct);
                    }
                    uploads.Add(new SupplementalSymbolUpload(SanitiseFileName(file.FileName), buffer.ToArray()));
                }

                // Store the symbols, so every later build of this project benefits,
                // then rebuild this release in place. The checks, the stored symbols
                // and the reopen commit together under the pipeline's build lock
                // (#1119), so a refused rebuild stores nothing.
                await using (var rebuild = await projectBuilds.BeginRebuildAsync(id, projectId, "Symbols", ct))
                {
                    await projects.AddSupplementalSymbolsAsync(projectId, uploads, ct);
                    await importer.ReopenForRebuildAsync(id, ct);
                    await rebuild.CommitAsync(ct);
                }
                await management.ClearIngestedDataAsync(id, ct);
                var source = new ReleaseImportSource.ProjectBuild(projectId);
                await imports.EnqueueImportAsync(id, source, storeSymbolReference: false, ct);

                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=recover-queued");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
            catch (ProjectAccessDeniedException)
            {
                RedirectManage(ctx, id, "Symbols", CannotRebuild);
            }
        })
        .RequireObjectExplorerAuthoring()
        .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes))
        .WithMetadata(new RequestFormLimitsAttribute
        {
            MultipartBodyLengthLimit = MaxUploadBytes,
            MultipartHeadersLengthLimit = 32 * 1024,
        });

        // Maintenance: re-extract system references over already-stored source
        // for one release (no re-upload) — backfills oe_module_system_references
        // for releases imported before #279. Queued like an import; processed by
        // ReleaseImportWorker, which routes AL vs C/AL. See #291.
        app.MapPost("/admin/object-explorer/{id:int}/backfill-system-references", async (
            int id,
            HttpContext ctx,
            ReleaseImportRequestService imports,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var source = new ReleaseImportSource.Backfill();
            await imports.EnqueueImportAsync(id, source, storeSymbolReference: false, ct);
            ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=backfill-queued");
        }).RequireObjectExplorerAuthoring();

        // Bulk variant: enqueue a backfill for every ready, non-deleted release
        // in the org — the "I don't want to reimport my whole catalogue" path.
        app.MapPost("/admin/object-explorer/backfill-system-references-all", async (
            HttpContext ctx,
            AppDbContext db,
            ReleaseImportRequestService imports,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            // Query-filtered to the caller's org; no cross-tenant enqueue.
            var releaseIds = await db.OeReleases.AsNoTracking()
                .Where(r => r.Status == "ready" && r.DeletedAt == null)
                .Select(r => r.Id)
                .ToListAsync(ct);
            foreach (var rid in releaseIds)
            {
                var source = new ReleaseImportSource.Backfill();
                await imports.EnqueueImportAsync(rid, source, storeSymbolReference: false, ct);
            }
            ctx.Response.Redirect($"/admin/object-explorer?ok=backfill-all-queued&id={releaseIds.Count}");
        }).RequireObjectExplorerAuthoring();

        app.MapPost("/admin/object-explorer/{id:int}/restore", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            try
            {
                await management.RestoreAsync(id, ct);
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=restored");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        app.MapPost("/admin/object-explorer/{id:int}/metadata", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var publisher = form["Publisher"].ToString();
            var projectName = form["ProjectName"].ToString();

            try
            {
                await management.UpdateMetadataAsync(id, publisher, projectName, ct);
                ctx.Response.Redirect($"/admin/object-explorer/release/{id}/manage?ok=updated");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        app.MapPost("/admin/object-explorer/{id:int}/hard-delete", async (
            int id,
            HttpContext ctx,
            ReleaseManagementService management,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var confirm = form["ConfirmLabel"].ToString();

            try
            {
                await management.HardDeleteAsync(id, confirm, ct);
                // The release is gone, so there's no manage page to return to.
                ctx.Response.Redirect("/admin/object-explorer?ok=hard-deleted&id=" + id);
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                RedirectManage(ctx, id, first.Key, first.Value);
            }
        }).RequireObjectExplorerAuthoring();

        return app;
    }

    /// <summary>
    /// Authorisation shared by every mutating Object Explorer admin endpoint:
    /// the same <c>Admin,Editor</c> set the OE admin pages declare
    /// (<c>[Authorize(Roles = "Admin,Editor")]</c>). Object Explorer is a
    /// content-authoring surface, so Editors operate it fully — see CLAUDE.md's
    /// role model. Centralised here so the endpoint policy can't silently drift
    /// from the page policy again: when they disagreed, an Editor's POST 403'd
    /// and the cookie handler's AccessDeniedPath bounced them to /login, which
    /// looked like being logged out mid-upload.
    /// </summary>
    private static RouteHandlerBuilder RequireObjectExplorerAuthoring(this RouteHandlerBuilder builder) =>
        builder.RequireAuthorization(policy => policy.RequireRole(
            HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole));

    /// <summary>
    /// Adapts one posted file to the <see cref="UploadedFile"/> the import
    /// service takes, so nothing below the endpoint layer knows about
    /// <see cref="IFormFile"/>. Zero-length picks are kept as-is: the service
    /// decides what an empty file means for each path.
    /// </summary>
    private static UploadedFile? ToUpload(IFormFile? file) =>
        file is null ? null : new UploadedFile(file.FileName, file.Length, file.OpenReadStream);

    private static IReadOnlyList<UploadedFile> ToUploads(IFormCollection form, string name) =>
        form.Files.GetFiles(name)
            .Select(f => new UploadedFile(f.FileName, f.Length, f.OpenReadStream))
            .ToList();

    private const string CannotRebuild =
        "Only someone who can manage this solution can build it again.";

    private static void RedirectManage(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/manage?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    private static void RedirectTranslations(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/translations?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    private static void RedirectAmend(HttpContext ctx, int releaseId, string errKey, string message)
    {
        ctx.Response.Redirect(
            $"/admin/object-explorer/release/{releaseId}/modules?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }

    private static void RedirectQueued(HttpContext ctx, int releaseId) =>
        ctx.Response.Redirect($"/admin/object-explorer?ok=queued&id={releaseId}");

    private static void Redirect(HttpContext ctx, string errKey, string message, string? page = null)
    {
        // An explicit page wins (the caller knows which tab the post came from).
        // Otherwise DVD-URL errors come from the DVD tab; everything else is the
        // Upload tab's folder-ZIP picker.
        page ??= errKey == "DvdUrl" ? "/admin/object-explorer/new/dvd" : "/admin/object-explorer/new";
        ctx.Response.Redirect(
            page + "?err=" + Uri.EscapeDataString(errKey)
            + "&msg=" + Uri.EscapeDataString(message));
    }
}
