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

    public ReleaseImportRequestService(
        ReleaseImportService importer,
        ReleaseManagementService management,
        DvdDownloadService dvdDownloader,
        ReleaseImportQueue queue,
        PersistedImportJobs persistedJobs,
        IOrganizationContext orgContext,
        ProjectBuildImporter projectBuilds)
    {
        _importer = importer;
        _management = management;
        _dvdDownloader = dvdDownloader;
        _queue = queue;
        _persistedJobs = persistedJobs;
        _orgContext = orgContext;
        _projectBuilds = projectBuilds;
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
    /// re-upload; a staged-ZIP / C-AL import needs the file re-uploaded because
    /// its temp file is gone; a project build re-runs from its project id
    /// alone. Either way the previous attempt's partial data is wiped so the
    /// re-run starts clean. See the manage page.
    /// </summary>
    public async Task<ReleaseImportOutcome> RetryAsync(
        int releaseId, ReleaseRetrySubmission submission, CancellationToken ct = default)
    {
        var origin = await _persistedJobs.GetLatestForReleaseAsync(releaseId, ct).ConfigureAwait(false);

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
            // The rerun builds the commits the first run did (#1110).
            await _projectBuilds.EnsureCanRebuildAsync(releaseId, retryProjectId, "Retry", ct).ConfigureAwait(false);
            await _importer.ReopenForRebuildAsync(releaseId, ct).ConfigureAwait(false);
            await _management.ClearIngestedDataAsync(releaseId, ct).ConfigureAwait(false);
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
        // half-written module on the idempotency check.
        await _importer.ReopenForRetryAsync(releaseId, ct).ConfigureAwait(false);
        await _management.ClearIngestedDataAsync(releaseId, ct).ConfigureAwait(false);

        ReleaseImportSource source;
        if (urlToUse is not null)
        {
            source = new ReleaseImportSource.Url(urlToUse);
        }
        else if (hasFolderZip)
        {
            var tempPath = await TryStageAsync(releaseId, folderZip!, FolderZipTempPrefix, ".zip", "ZIP", ct).ConfigureAwait(false);
            if (tempPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
            // A URL-origin DVD re-uploaded as a zip is still a DVD subset;
            // otherwise honour the original staged flag (defaults to the
            // whole-archive / workspace walk).
            var isDvd = origin?.Kind == "url" || (origin?.StagedIsDvd ?? false);
            source = new ReleaseImportSource.StagedZip(tempPath, isDvd);
        }
        else
        {
            var tempPath = await TryStageAsync(releaseId, calTxt!, CalTxtTempPrefix, ".txt", "C/AL file", ct).ConfigureAwait(false);
            if (tempPath is null) return new ReleaseImportOutcome.StagingFailed(releaseId);
            source = new ReleaseImportSource.CalTxt(tempPath, submission.CalEncoding);
        }

        await EnqueueImportAsync(releaseId, source, submission.StoreSymbolReference, ct).ConfigureAwait(false);
        return new ReleaseImportOutcome.Queued(releaseId);
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
        await using var fs = File.Create(tempPath);
        await using var src = file.OpenRead();
        await src.CopyToAsync(fs, ct).ConfigureAwait(false);
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
