using System.IO.Compression;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// One file an admin picked on an Object Explorer upload form, reduced to what
/// the import policy actually needs: its name, its size, and a way to open it.
/// Keeps <see cref="ReleaseImportRequestService"/> free of any dependency on
/// ASP.NET's form types, so the path-selection rules can be driven from a test
/// with a plain <see cref="MemoryStream"/>.
/// </summary>
/// <param name="OpenRead">
/// Opens a fresh read stream over the upload. Called at most once per file by
/// the service, which owns and disposes what it opens.
/// </param>
public sealed record UploadedFile(string FileName, long Length, Func<Stream> OpenRead);

/// <summary>
/// Everything the "import a release" form carries, already read off the request.
/// Which of these fields are populated is what picks the ingest path — see
/// <see cref="ReleaseImportRequestService.SubmitAsync"/>.
/// </summary>
public sealed record ReleaseImportSubmission(
    string Label,
    string Kind,
    int? ParentReleaseId,
    string Publisher,
    string ProjectName,
    bool StoreSymbolReference,
    string DvdUrl,
    string CalEncoding,
    UploadedFile? CalTxtFile,
    UploadedFile? FolderZip,
    IReadOnlyList<UploadedFile> AppFiles,
    IReadOnlyList<UploadedFile> SourceZips)
{
    /// <summary>
    /// True when this post came from the legacy C/AL tab. The kind is decided
    /// from the file, not from the form's Kind field — see
    /// <see cref="ReleaseImportRequestService.SubmitAsync"/>.
    /// </summary>
    public bool IsCalImport => CalTxtFile is { Length: > 0 };
}

/// <summary>
/// What the bulk system-reference backfill queued, and how many releases it left
/// alone because a backfill was already queued or running for them.
/// </summary>
public sealed record BackfillQueueResult(int Queued, int Skipped);

/// <summary>Everything the "retry a failed import" form carries.</summary>
public sealed record ReleaseRetrySubmission(
    string DvdUrl,
    string CalEncoding,
    bool StoreSymbolReference,
    UploadedFile? FolderZip,
    UploadedFile? CalTxtFile);

/// <summary>
/// How an import submission ended, in the terms the admin pages redirect on.
/// Validation refusals aren't modelled here — they stay
/// <see cref="PlanValidationException"/>s so the form can render them inline
/// against the field the admin typed in.
/// </summary>
public abstract record ReleaseImportOutcome
{
    /// <summary>The release row exists and its ingest is on the queue.</summary>
    public sealed record Queued(int ReleaseId) : ReleaseImportOutcome;

    /// <summary>
    /// The release row exists but the upload couldn't be staged to disk (out of
    /// scratch space), so the failure is recorded on the release rather than
    /// thrown at the admin as a 500. The caller sends them to the release list,
    /// where the failed row explains itself.
    /// </summary>
    public sealed record StagingFailed(int ReleaseId) : ReleaseImportOutcome;

    /// <summary>The small individual-file path ran to completion in-request.</summary>
    public sealed record Imported(ReleaseImportSummary Summary) : ReleaseImportOutcome;
}

/// <summary>
/// Turns an Object Explorer upload form into an ingest: picks the path (legacy
/// C/AL TXT, URL download, folder-ZIP upload, or individual files), stages what
/// needs staging to a temp file, and either queues the job for
/// <see cref="ReleaseImportWorker"/> or runs the small synchronous import.
///
/// <para>The rule, in one place, is: a C/AL TXT wins over everything (it forces
/// <c>cal</c> kind), then a pasted URL, then a folder ZIP, and individual
/// <c>.app</c> files are the fallback. The first three are DVD-scale and queue;
/// only the last is small enough to keep the admin on the page.</para>
///
/// <para>Mirrors <see cref="ProjectBuildImporter"/>, which does the same
/// create-the-release-then-enqueue dance for builds. The heavy lifting stays in
/// <see cref="ReleaseImportService"/> (ingest) and <see cref="ReleaseImportWorker"/>
/// (the queued paths); this type owns only the policy the endpoint used to hold.</para>
/// </summary>
public sealed class ReleaseImportRequestService
{
    /// <summary>Temp-file prefix for an uploaded folder ZIP staged for the worker.</summary>
    public const string FolderZipTempPrefix = "oe-folder-";

    /// <summary>Temp-file prefix for an uploaded C/AL TXT staged for the worker.</summary>
    public const string CalTxtTempPrefix = "oe-cal-";

    private readonly ReleaseImportService _importer;
    private readonly ReleaseManagementService _management;
    private readonly DvdDownloadService _dvdDownloader;
    private readonly ReleaseImportQueue _queue;
    private readonly PersistedImportJobs _persistedJobs;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectBuildImporter _projectBuilds;
    private readonly ProjectAccess _access;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ReleaseImportRequestService> _logger;

    public ReleaseImportRequestService(
        ReleaseImportService importer,
        ReleaseManagementService management,
        DvdDownloadService dvdDownloader,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        IOrganizationContext orgContext,
        ProjectBuildImporter projectBuilds,
        ProjectAccess access,
        IHostApplicationLifetime lifetime,
        ILogger<ReleaseImportRequestService> logger)
    {
        _importer = importer;
        _management = management;
        _dvdDownloader = dvdDownloader;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _orgContext = orgContext;
        _projectBuilds = projectBuilds;
        _access = access;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Runs one import submission: validates the metadata, picks the ingest
    /// path from which inputs the admin filled in, and either queues the job or
    /// performs the synchronous individual-file import. Throws
    /// <see cref="PlanValidationException"/> (field-keyed) for anything the
    /// admin can fix on the form — a missing kind, nothing picked, a URL off
    /// the allow-list, a label collision, a quota refusal.
    /// </summary>
    public async Task<ReleaseImportOutcome> SubmitAsync(
        ReleaseImportSubmission submission, CancellationToken ct = default)
    {
        var label = submission.Label;
        var kind = submission.Kind;
        var publisher = submission.Publisher;
        var projectName = submission.ProjectName;
        var parentReleaseId = submission.ParentReleaseId;
        var storeSymbolReference = submission.StoreSymbolReference;
        var isCalImport = submission.IsCalImport;

        // The kind is decided server-side, not trusted from the form: a C/AL
        // TXT post is always a 'cal' release with no parent/publisher/project
        // (those fields are hidden on that page and must stay empty even on a
        // stale-form or no-JS post), and 'project' is reserved for pipeline
        // builds stamped by ProjectBuildImporter — never a manual import.
        if (isCalImport)
        {
            kind = "cal";
            parentReleaseId = null;
            publisher = string.Empty;
            projectName = string.Empty;
        }
        else if (kind is not ("first_party" or "third_party"))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Kind"] = "Pick either First-party (Microsoft) or Third-party.",
            });
        }

        var metadata = new ReleaseImportMetadata(label, kind, parentReleaseId, null, publisher, projectName);
        // Legacy C/AL TXT codepage: classic finsql exports are OEM (850);
        // newer ones can be 1252. Admin-selectable, default 850.
        var calEncoding = submission.CalEncoding;

        var folderZip = submission.FolderZip is { Length: > 0 } fz ? fz : null;
        var appFiles = submission.AppFiles.Where(f => f.Length > 0).ToArray();
        var dvdUrl = submission.DvdUrl;

        if (dvdUrl.Length == 0 && submission.FolderZip is null && appFiles.Length == 0 && !isCalImport)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AppFiles"] = "Paste a download URL, pick a folder ZIP, pick at least one .app file, or pick a C/AL TXT export before submitting.",
            });
        }

        // ── Legacy C/AL TXT: stage to disk, queue, ingest in bg ────
        if (isCalImport)
        {
            var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
            var tempPath = await TryStageAsync(releaseId, submission.CalTxtFile!, CalTxtTempPrefix, ".txt", "C/AL file", ct).ConfigureAwait(false);
            if (tempPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
            var source = new ReleaseImportSource.CalTxt(tempPath, calEncoding);
            await EnqueueImportAsync(releaseId, source, storeSymbolReference: false, ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Queued(releaseId);
        }

        // ── URL download: queue, ingest in the background ──────────
        if (dvdUrl.Length > 0)
        {
            await _dvdDownloader.ValidateUrlForQueueAsync(dvdUrl, ct).ConfigureAwait(false);
            var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
            var source = new ReleaseImportSource.Url(dvdUrl);
            await EnqueueImportAsync(releaseId, source, storeSymbolReference, ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Queued(releaseId);
        }

        // ── Folder-ZIP upload: stage to disk, queue, ingest in bg ──
        if (folderZip is not null)
        {
            var releaseId = await _importer.BeginReleaseAsync(metadata, ct).ConfigureAwait(false);
            var tempPath = await TryStageAsync(releaseId, folderZip, FolderZipTempPrefix, ".zip", "ZIP", ct).ConfigureAwait(false);
            if (tempPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
            var source = new ReleaseImportSource.StagedZip(tempPath, IsDvd: false);
            await EnqueueImportAsync(releaseId, source, storeSymbolReference, ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Queued(releaseId);
        }

        // ── Individual files: small/fast, stays synchronous ────────
        var openedStreams = new List<Stream>();
        try
        {
            var uploads = BuildUploadsFromIndividualFiles(appFiles, submission.SourceZips, openedStreams);
            var request = new ReleaseImportRequest(
                Label: label,
                Kind: kind,
                ParentReleaseId: parentReleaseId,
                ApplicationVersionId: null,
                Uploads: uploads,
                Publisher: publisher,
                ProjectName: projectName,
                StoreSymbolReference: storeSymbolReference);

            var summary = await _importer.ImportReleaseAsync(request, ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Imported(summary);
        }
        finally
        {
            DisposeAll(openedStreams);
        }
    }

    /// <summary>
    /// Amends modules into an existing release (#216) from the same upload
    /// shapes the import form accepts: a folder ZIP, or individual <c>.app</c>
    /// files with their paired <c>.Source.zip</c>s. Synchronous — the streams
    /// are consumed in-request, and the staged ZIP is deleted before returning.
    /// Throws <see cref="PlanValidationException"/> when nothing was picked, or
    /// when the importer refuses the amend.
    /// </summary>
    public async Task<ReleaseImportSummary> AmendAsync(
        int releaseId,
        UploadedFile? folderZipFile,
        IReadOnlyList<UploadedFile> appFileUploads,
        IReadOnlyList<UploadedFile> sourceZips,
        CancellationToken ct = default)
    {
        var folderZip = folderZipFile is { Length: > 0 } fz ? fz : null;
        var appFiles = appFileUploads.Where(f => f.Length > 0).ToArray();

        if (folderZipFile is null && appFiles.Length == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["AppFiles"] = "Pick a folder ZIP or at least one .app file before submitting.",
            });
        }

        var openedStreams = new List<Stream>();
        ZipArchive? folderArchive = null;
        string? tempFolderZipPath = null;
        try
        {
            List<AppFileUpload> uploads;
            if (folderZip is not null)
            {
                (uploads, folderArchive, tempFolderZipPath) =
                    await BuildUploadsFromFolderZipAsync(folderZip, openedStreams, ct).ConfigureAwait(false);
            }
            else
            {
                uploads = BuildUploadsFromIndividualFiles(appFiles, sourceZips, openedStreams);
            }

            return await _importer.AmendReleaseAsync(releaseId, uploads, ct).ConfigureAwait(false);
        }
        finally
        {
            DisposeAll(openedStreams);
            folderArchive?.Dispose();
            if (tempFolderZipPath is not null && File.Exists(tempFolderZipPath))
            {
                try { File.Delete(tempFolderZipPath); } catch { /* swallow */ }
            }
        }
    }

    /// <summary>
    /// Re-runs a failed import in place — into the SAME release row (label /
    /// metadata preserved) instead of forcing a delete-and-reimport. A URL
    /// import re-runs from its original (or a freshly pasted) URL with no
    /// re-upload, and a Microsoft artifact import re-runs from the artifact it was
    /// queued with; a staged-ZIP / C-AL import needs the file re-uploaded because
    /// its temp file is gone; a project build re-runs from its project id
    /// alone. Either way the previous attempt's partial data is wiped so the
    /// re-run starts clean. See the manage page.
    /// </summary>
    public async Task<ReleaseImportOutcome> RetryAsync(
        int releaseId, ReleaseRetrySubmission submission, CancellationToken ct = default)
    {
        // A release the solution-visibility fence hides is answered exactly as an
        // unknown id: its saved source is read and then discarded, so there is
        // nothing to reuse, and the reopen reads the release and answers not
        // found. Asked first and always, so the two run the same queries.
        var visible = await _access.IsReleaseVisibleAsync(releaseId, ct).ConfigureAwait(false);
        var latest = await _persistedJobs.GetLatestForReleaseAsync(releaseId, ct).ConfigureAwait(false);
        var origin = visible ? latest : null;

        // Project builds re-run from the project id alone — no URL, no
        // re-upload. Reopen the release, wipe the previous attempt's modules,
        // and re-enqueue a ProjectBuild job (the build service replaces the
        // per-app report). Handled before the URL/upload validation below.
        if (origin is { Kind: "project_build", ProjectId: int retryProjectId })
        {
            // ReopenForRebuildAsync (not ReopenForRetryAsync) so a partial
            // build — which lands `ready`, not `failed` — can be plainly
            // re-run without a symbol upload when its failure was
            // transient. See issue #433.
            // Held to the same rules as pressing Build: the person must be able to
            // manage the solution, and no other build of its pipeline may be running.
            // The rerun builds the commits the first run did (#1110). The check and the
            // reopen happen under the pipeline's build lock (#1119).
            // The release reads as importing from the reopen on, before its job is
            // queued: a build polling it must not take it for abandoned (#1180).
            using var rebuilding = _importer.Ingests.Track(releaseId);
            await using (var rebuild = await _projectBuilds.BeginRebuildAsync(releaseId, retryProjectId, "Retry", ct).ConfigureAwait(false))
            {
                await _importer.ReopenForRebuildAsync(releaseId, ct).ConfigureAwait(false);
                await rebuild.CommitAsync(ct).ConfigureAwait(false);
            }
            // Wipes the previous attempt first, and fails the build if it cannot queue it.
            await _projectBuilds.QueueRebuildAsync(releaseId, retryProjectId, ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Queued(releaseId);
        }

        var dvdUrl = submission.DvdUrl;
        var folderZip = submission.FolderZip is { Length: > 0 } fz ? fz : null;
        var calTxt = submission.CalTxtFile is { Length: > 0 } cal ? cal : null;
        var hasFolderZip = folderZip is not null;
        var hasCalTxt = calTxt is not null;

        // Resolve the URL to use (pasted wins; else reuse the original
        // URL import) and validate it against the allow-list BEFORE we
        // touch the release, so a bad URL leaves the failed row untouched.
        string? urlToUse = null;
        string? artifactUrl = null;
        if (dvdUrl.Length > 0)
        {
            urlToUse = dvdUrl;
        }
        else if (!hasFolderZip && !hasCalTxt)
        {
            if (origin is { Kind: "url", DownloadUrl: { Length: > 0 } originalUrl })
            {
                urlToUse = originalUrl;
            }
            else if (origin is { Kind: "bc_artifact", DownloadUrl: { Length: > 0 } originalArtifactUrl })
            {
                // A Microsoft artifact import re-runs from the artifact address it
                // was queued with, the same one a restart resumes from (#1222).
                artifactUrl = originalArtifactUrl;
            }
            else if (origin is null || origin.Kind is not ("url" or "bc_artifact" or "staged_zip" or "cal_txt"))
            {
                // No saved source at all (#1338): the manage page offers only
                // the URL field here, so ask for exactly that.
                throw RetryValidation("Paste the DVD's download URL to retry.");
            }
            else
            {
                throw RetryValidation(
                    "There's nothing to re-run automatically — the original upload isn't on disk any more. "
                    + "Paste a download URL, or re-upload the ZIP / C-AL file, to retry.");
            }
        }
        if (urlToUse is not null)
        {
            await _dvdDownloader.ValidateUrlForQueueAsync(urlToUse, ct).ConfigureAwait(false);
        }

        // Flip failed → ingesting (validates state) and wipe the previous
        // attempt's partial modules so the re-run can't skip a
        // half-written module on the idempotency check. Wiping can take a while, and
        // the job is queued only after it: until then nothing else says the release
        // is being worked on, so a build polling it would take it for abandoned (#1180).
        using var reopened = _importer.Ingests.Track(releaseId);
        await _importer.ReopenForRetryAsync(releaseId, visible, ct).ConfigureAwait(false);

        // From the reopen on the release reads as importing, but nothing works on it
        // until its job is queued. Anything that stops the steps below (leaving the
        // page during the long wipe, which cancels the request; the wipe's command
        // timeout; a failed insert) fails it again rather than leave it stuck until a
        // restart (#1612). The module DELETE is one statement, so cut off it rolls back
        // and the earlier attempt's modules stay; a failure after it (the blob reclaim,
        // staging, the job row) leaves them already removed. Either way the next Retry
        // starts by wiping again.
        string? stagedPath = null;
        long? jobRowId = null;
        try
        {
            await _management.ClearIngestedDataAsync(releaseId, ct).ConfigureAwait(false);

            ReleaseImportSource source;
            if (artifactUrl is not null)
            {
                source = new ReleaseImportSource.BcArtifact(artifactUrl);
            }
            else if (urlToUse is not null)
            {
                source = new ReleaseImportSource.Url(urlToUse);
            }
            else if (hasFolderZip)
            {
                stagedPath = await TryStageAsync(releaseId, folderZip!, FolderZipTempPrefix, ".zip", "ZIP", ct).ConfigureAwait(false);
                if (stagedPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
                // A URL-origin DVD re-uploaded as a zip is still a DVD subset;
                // otherwise honour the original staged flag (defaults to the
                // whole-archive / workspace walk).
                var isDvd = origin?.Kind == "url" || (origin?.StagedIsDvd ?? false);
                source = new ReleaseImportSource.StagedZip(stagedPath, isDvd);
            }
            else
            {
                stagedPath = await TryStageAsync(releaseId, calTxt!, CalTxtTempPrefix, ".txt", "C/AL file", ct).ConfigureAwait(false);
                if (stagedPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
                source = new ReleaseImportSource.CalTxt(stagedPath, submission.CalEncoding);
            }

            // EnqueueImportAsync's two steps, inline so a row written but never queued
            // (the request ended while the queue was full) can be failed below.
            var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a release import");
            jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, submission.StoreSymbolReference, ct).ConfigureAwait(false);
            await _queue.EnqueueAsync(
                new ReleaseImportJob(releaseId, identity, source, submission.StoreSymbolReference, jobRowId.Value),
                ct).ConfigureAwait(false);
            return new ReleaseImportOutcome.Queued(releaseId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retry of release {ReleaseId} stopped before its import was queued; failing it again.", releaseId);
            // A queued row with no job behind it would be resumed at the next start,
            // wiping the release first, even after a later Retry has imported it.
            if (jobRowId is long rowId)
            {
                try
                {
                    await _persistedJobs.FailUnqueuedAsync(rowId, ReleaseImportService.RetryNotQueuedMessage).ConfigureAwait(false);
                }
                catch (Exception rowEx)
                {
                    _logger.LogError(rowEx, "Could not fail import job row {JobRowId} of release {ReleaseId}.", rowId, releaseId);
                }
            }
            // No job will open the staged upload, so it would sit in the temp folder until the next start.
            if (stagedPath is not null)
            {
                try { File.Delete(stagedPath); } catch { /* swallow */ }
            }
            await _importer.FailUnqueuedRetryAsync(releaseId).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The solution whose build produced <paramref name="releaseId"/>, for the
    /// manual-symbols recovery, or null when the release is not a solution build.
    /// A release the solution-visibility fence hides also answers null, after the
    /// same read, so the refusal does not confirm that the id exists.
    /// </summary>
    public async Task<int?> FindSymbolRecoveryProjectAsync(int releaseId, CancellationToken ct = default)
    {
        var visible = await _access.IsReleaseVisibleAsync(releaseId, ct).ConfigureAwait(false);
        var origin = await _persistedJobs.GetLatestForReleaseAsync(releaseId, ct).ConfigureAwait(false);
        return visible && origin is { Kind: "project_build", ProjectId: int projectId } ? projectId : null;
    }

    /// <summary>
    /// The shared two-step enqueue every import path performs: persist a
    /// <c>queued</c> job row, then push the matching in-memory
    /// <see cref="ReleaseImportJob"/> stamped with that row id onto the
    /// channel the background worker drains. Public because the maintenance
    /// endpoints (backfill, symbol recovery) queue their own sources without
    /// going through a submission; each caller keeps its own redirect because
    /// the success target differs (release page vs manage page vs none for the
    /// bulk loop).
    /// </summary>
    public async Task EnqueueImportAsync(
        int releaseId,
        ReleaseImportSource source,
        bool storeSymbolReference,
        CancellationToken ct = default)
    {
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a release import");
        var jobRowId = await _persistedJobs.CreateAsync(releaseId, identity, source, storeSymbolReference, ct).ConfigureAwait(false);
        await _queue.EnqueueAsync(
            new ReleaseImportJob(releaseId, identity, source, storeSymbolReference, jobRowId),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Queues a system-reference backfill for each of <paramref name="releaseIds"/>
    /// without waiting for room on the queue, and says how many it queued and how
    /// many it skipped because a backfill was already queued or running for them.
    ///
    /// <para>The import queue holds sixteen jobs and has one worker, and a backfill
    /// takes minutes on a Business Central release, so writing a whole catalogue to
    /// it from the request held the request for hours, and every other import waited
    /// behind it (#1542). Instead the durable <c>queued</c> rows are written here, in
    /// one transaction (<see cref="PersistedImportJobs.CreateBackfillsAsync"/>), and
    /// the in-memory jobs are handed to the same background feeder the startup resume
    /// uses. It feeds them paced (<see cref="ReleaseImportQueue.EnqueuePacedAsync"/>),
    /// one waiting at a time, so an import submitted during the run is not stuck
    /// behind a full queue of backfills. The feeder runs until shutdown, not until the
    /// request ends, so closing the tab loses nothing; rows it has not fed by then are
    /// queued again on the next start.</para>
    ///
    /// <para>A release that already has a backfill queued or running is skipped:
    /// that run re-extracts the same stored source, so a second click adds nothing
    /// but another hour on the queue.</para>
    /// </summary>
    public async Task<BackfillQueueResult> QueueBackfillAllAsync(IReadOnlyList<int> releaseIds, CancellationToken ct = default)
    {
        var identity = AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "queuing a system-reference backfill");
        // Before the rows are written, so a restore from here on drops the feed (#1538).
        var epoch = _queue.Epoch;
        var rows = await _persistedJobs.CreateBackfillsAsync(releaseIds, identity, ct).ConfigureAwait(false);
        var source = new ReleaseImportSource.Backfill();
        var jobs = rows
            .Select(r => new ReleaseImportJob(r.ReleaseId, identity, source, StoreSymbolReference: false, r.JobRowId))
            .ToList();
        if (jobs.Count > 0)
        {
            _ = ResumedImportJobs.EnqueueAllAsync(_queue, jobs, _logger, "system-reference backfill", _lifetime.ApplicationStopping, paced: true, epoch: epoch);
        }
        var skipped = releaseIds.Distinct().Count() - jobs.Count;
        _logger.LogInformation(
            "Queued {Count} system-reference backfill(s), skipping {Skipped} already queued.",
            jobs.Count, skipped);
        return new BackfillQueueResult(jobs.Count, skipped);
    }

    /// <summary>Field-keyed (<c>Retry</c>) validation error for the retry path's inline messages.</summary>
    private static PlanValidationException RetryValidation(string message) =>
        new(new Dictionary<string, string> { ["Retry"] = message });

    /// <summary>
    /// Stages an uploaded file to a temp path the background worker reopens,
    /// folding the out-of-scratch-disk failure handling every caller needs:
    /// the release row already exists, so an <see cref="IOException"/> records
    /// the failure on it rather than becoming a 500. Returns the temp path, or
    /// <c>null</c> after recording the failure — in which case the caller
    /// returns <see cref="ReleaseImportOutcome.StagingFailed"/>.
    /// </summary>
    private async Task<string?> TryStageAsync(
        int releaseId,
        UploadedFile file,
        string prefix,
        string extension,
        string failedNoun,
        CancellationToken ct)
    {
        try
        {
            return await StageUploadToTempAsync(file, prefix, extension, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            await _importer.MarkFailedAsync(releaseId, $"Could not stage the uploaded {failedNoun} to disk: " + ex.Message, ct).ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>
    /// Streams an uploaded file to a temp file the background worker reopens
    /// after the request ends (the worker deletes it when done). Used for both
    /// the folder ZIP and the raw C/AL TXT — neither fits through the Blazor
    /// circuit at 150 MB+.
    /// </summary>
    private static async Task<string> StageUploadToTempAsync(
        UploadedFile file, string prefix, string extension, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N") + extension);
        try
        {
            await using var fs = File.Create(tempPath);
            await using var src = file.OpenRead();
            await src.CopyToAsync(fs, ct).ConfigureAwait(false);
        }
        catch
        {
            // A half-written copy has no job to delete it, so it would sit in the
            // temp folder until the next start. The file is closed by now.
            try { File.Delete(tempPath); } catch { /* swallow */ }
            throw;
        }
        return tempPath;
    }

    /// <summary>
    /// Stages the uploaded folder ZIP to a temp file and walks every
    /// <c>.app</c> entry — the synchronous amend path (#216) consumes the entry
    /// streams in-request, so it gets the archive + temp path back to dispose.
    /// </summary>
    private static async Task<(List<AppFileUpload> Uploads, ZipArchive Archive, string TempPath)>
        BuildUploadsFromFolderZipAsync(
            UploadedFile folderZip,
            List<Stream> openedStreams,
            CancellationToken ct)
    {
        var tempPath = await StageUploadToTempAsync(folderZip, FolderZipTempPrefix, ".zip", ct).ConfigureAwait(false);
        var (uploads, archive) = ReleaseZipStaging.OpenStagedZip(tempPath, isDvd: false, openedStreams);
        return (uploads, archive, tempPath);
    }

    // ── Individual-file path (legacy / partner extensions) ─────────────

    private static List<AppFileUpload> BuildUploadsFromIndividualFiles(
        IReadOnlyList<UploadedFile> appFiles,
        IReadOnlyList<UploadedFile> sourceZipFiles,
        List<Stream> openedStreams)
    {
        var sourceZips = sourceZipFiles.Where(f => f.Length > 0).ToArray();
        var sourceByBasename = sourceZips.ToDictionary(
            f => Path.GetFileNameWithoutExtension(f.FileName).Replace(".Source", "", StringComparison.OrdinalIgnoreCase),
            f => f,
            StringComparer.OrdinalIgnoreCase);

        var uploads = new List<AppFileUpload>(appFiles.Count);
        foreach (var af in appFiles)
        {
            var appStream = af.OpenRead();
            openedStreams.Add(appStream);
            Stream? sourceStream = null;
            var stem = Path.GetFileNameWithoutExtension(af.FileName);
            foreach (var key in EnumeratePossibleSourceKeys(stem))
            {
                if (sourceByBasename.TryGetValue(key, out var match))
                {
                    sourceStream = match.OpenRead();
                    openedStreams.Add(sourceStream);
                    break;
                }
            }
            uploads.Add(new AppFileUpload(af.FileName, appStream, sourceStream));
        }
        return uploads;
    }

    /// <summary>
    /// Tries a few reasonable name shapes for "what .Source.zip pairs with
    /// this .app" on the individual-file path. The folder-ZIP path has its
    /// own pairing logic (<see cref="FolderZipWalker"/>) that takes
    /// containing-directory + stem into account; this fallback handles the
    /// flat-folder partner case.
    /// </summary>
    private static IEnumerable<string> EnumeratePossibleSourceKeys(string stem)
    {
        yield return stem;

        var underscore = stem.IndexOf('_');
        if (underscore > 0)
        {
            var trimmed = stem[(underscore + 1)..];
            yield return trimmed;
            yield return trimmed.Replace('_', ' ');
        }

        yield return stem.Replace('_', ' ');
    }

    private static void DisposeAll(List<Stream> streams)
    {
        foreach (var s in streams)
        {
            try { s.Dispose(); } catch { /* swallow */ }
        }
    }
}
