using System.IO.Compression;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;
using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Drains <see cref="ReleaseImportQueue"/> and runs each DVD-scale import
/// (folder-ZIP upload, URL download) off the request thread, so the admin is
/// returned to the releases list immediately and watches the row flip from
/// <c>ingesting</c> to <c>ready</c> / <c>failed</c>.
///
/// <para>
/// One job at a time (the channel is single-reader): a full DVD import is
/// memory-heavy, and serialising keeps two of them from running at once. Each
/// job runs in its own DI scope under the submitting user's
/// <see cref="AmbientOrganizationScope"/> identity so EF query filters and the
/// importer's org guard behave exactly as they would in the original request.
/// </para>
/// </summary>
public sealed class ReleaseImportWorker : QueueDrainWorker<ReleaseImportJob>
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReleaseImportWorker> _logger;

    public ReleaseImportWorker(
        ReleaseImportQueue queue,
        IServiceProvider services,
        ILogger<ReleaseImportWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
        // The active-duration ceiling is the longest legitimate single import — a fresh
        // BC base-app ingest can run 30+ minutes; 90 leaves margin while still catching
        // the hung-on-I/O case that prompted this in the first place.
        : base(queue.Reader, logger, heartbeats, nameof(ReleaseImportWorker), TimeSpan.FromMinutes(90))
    {
        _services = services;
        _logger = logger;
    }

    protected override string Describe(ReleaseImportJob job) => $"ReleaseId={job.ReleaseId}";

    /// <summary>
    /// Publishes the finished build as a GitHub Release when its pipeline names a
    /// repository to publish to. Runs inside the job's own DI scope and organisation
    /// identity, and swallows everything: the <c>.app</c> files are already built and
    /// downloadable, so nothing GitHub says is allowed to turn a successful build into
    /// a failed one. The reason lands on the build row and in its log instead. See
    /// <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    private async Task PublishReleaseAsync(IServiceProvider services, int releaseId, CancellationToken ct)
    {
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            var buildId = await db.OeProjectBuilds.AsNoTracking()
                .Where(b => b.ReleaseId == releaseId)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (buildId is not { } id) return;

            var releases = services.GetRequiredService<ALDevToolbox.Services.GitHub.GitHubReleaseService>();
            await releases.PublishBuildAsync(id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown, not a publish failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing release {ReleaseId}'s build to GitHub failed.", releaseId);
        }
    }

    /// <summary>
    /// Emails the people behind a pipeline when this build changed whether it
    /// works (#1035). The notifier never throws, so the build's outcome stands.
    /// </summary>
    private static Task NotifyBuildFinishedAsync(IServiceProvider services, int releaseId, CancellationToken ct) =>
        services.GetRequiredService<Notifications.BuildNotifier>().BuildFinishedAsync(releaseId, ct);

    /// <summary>
    /// Prepares a release of the finished build through every release pipeline that has
    /// "Prepare a release when a new build succeeds" on (#934). Nothing is sent: the
    /// release waits for a person to approve it. Swallows everything for the same reason
    /// <see cref="PublishReleaseAsync"/> does - the build has already succeeded, and
    /// nothing about preparing a release may turn it into a failed one.
    /// </summary>
    private async Task PrepareReleasesAsync(IServiceProvider services, int releaseId, CancellationToken ct)
    {
        try
        {
            var db = services.GetRequiredService<AppDbContext>();
            var buildId = await db.OeProjectBuilds.AsNoTracking()
                .Where(b => b.ReleaseId == releaseId)
                .Select(b => (int?)b.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (buildId is not { } id) return;

            var deliveries = services.GetRequiredService<Delivery.DeliveryService>();
            await deliveries.ProposeReleasesForBuildAsync(id, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Shutdown, not a failure.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Preparing releases of release {ReleaseId}'s build failed.", releaseId);
        }
    }

    /// <summary>
    /// Runs one pull-request build end to end and completes its check run.
    ///
    /// <para>The build's own failures are captured on the build row exactly as a
    /// manual build's are - <see cref="GitHubCheckRunService.CompleteAsync"/> then
    /// reads that row and says <c>failure</c> or <c>neutral</c> accordingly - so
    /// this method swallows them rather than letting them reach the base class's
    /// last-resort net, which would leave the check run stuck <c>in_progress</c>
    /// forever.</para>
    /// </summary>
    private async Task RunPullRequestBuildAsync(
        AsyncServiceScope scope,
        ReleaseImportService importer,
        ReleaseImportJob job,
        ReleaseImportSource.PullRequestBuild source,
        List<Stream> openedStreams,
        CancellationToken ct)
    {
        var buildService = scope.ServiceProvider.GetRequiredService<ProjectBuildService>();
        var checks = scope.ServiceProvider.GetRequiredService<GitHub.GitHubCheckRunService>();
        var github = scope.ServiceProvider.GetRequiredService<GitHub.GitHubAppClient>();
        var webhookQueue = scope.ServiceProvider.GetRequiredService<GitHub.GitHubWebhookQueue>();

        var key = new GitHub.GitHubPullRequestJob(
            source.InstallationId, source.RepositoryFullName, string.Empty,
            source.PullRequestNumber, source.HeadSha, string.Empty, string.Empty, string.Empty).Key;

        // Linked, not replacing: shutdown still cancels the build, and so now does
        // a newer head for the same pull request.
        using var superseded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        webhookQueue.BeginBuild(key, superseded);
        var buildCt = superseded.Token;
        try
        {
            // A newer head can be announced between the job being queued and this
            // registration being made, and its cancellation would then have found
            // nothing to cancel. So the question is asked once more now that the
            // token is registered: from here on Announce reaches us.
            if (!webhookQueue.IsLatest(key, source.HeadSha))
            {
                const string Message = "Superseded by a newer commit on the same pull request.";
                _logger.LogInformation(
                    "Pull-request build for release {ReleaseId} was superseded before it started.", job.ReleaseId);
                await importer.MarkFailedAsync(job.ReleaseId, Message, CancellationToken.None).ConfigureAwait(false);
                await buildService.MarkBuildFailedAsync(job.ReleaseId, Message, CancellationToken.None).ConfigureAwait(false);
                // Nothing is said on GitHub: the newer job owns its own check run,
                // and this stale head's run is completed by whichever build the
                // reviewer is actually waiting on.
                return;
            }

            string? installationToken = null;
            try
            {
                installationToken = await github.GetInstallationTokenAsync(source.InstallationId, buildCt).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is GitHub.GitHubApiException or GitHub.GitHubAppNotConfiguredException or HttpRequestException)
            {
                // Without a token nothing can be cloned, and saying so on the build
                // is more useful than a clone failure per repository.
                const string Message = "The workbench could not get permission from GitHub to read the repository.";
                _logger.LogWarning(ex, "Pull-request build for release {ReleaseId}: no installation token.", job.ReleaseId);
                await importer.MarkFailedAsync(job.ReleaseId, Message, ct).ConfigureAwait(false);
                await buildService.MarkBuildFailedAsync(job.ReleaseId, Message, ct).ConfigureAwait(false);
                // Still complete the run: a check left in_progress forever is worse
                // than one that says the build could not be started. It will most
                // likely fail too - the same credential writes both - but the
                // attempt costs nothing and its refusal is logged, not thrown.
                await checks.CompleteAsync(
                    source.InstallationId, source.RepositoryFullName, job.ReleaseId, source.ForkAuthor, ct)
                    .ConfigureAwait(false);
                return;
            }

            var options = new ProjectBuildOptions(
                RepositoryId: source.RepositoryId,
                HeadSha: source.HeadSha,
                InstallationToken: installationToken);
            try
            {
                var outcome = await buildService.BuildAsync(source.ProjectId, job.ReleaseId, options, buildCt).ConfigureAwait(false);
                foreach (var upload in outcome.Uploads) openedStreams.Add(upload.AppStream);
                await buildService.PersistResultsAsync(job.ReleaseId, outcome.Results, ct).ConfigureAwait(false);

                if (outcome.Uploads.Count == 0)
                {
                    const string Message = "No extensions compiled successfully. See the build report on the release.";
                    await importer.MarkFailedAsync(job.ReleaseId, Message, ct).ConfigureAwait(false);
                    await buildService.MarkBuildFailedAsync(job.ReleaseId, Message, ct).ConfigureAwait(false);
                }
                else
                {
                    await importer.ProcessReleaseAsync(job.ReleaseId, outcome.Uploads, job.StoreSymbolReference, ct).ConfigureAwait(false);
                    await buildService.MarkCompiledResultsIngestedAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    await buildService.MarkBuildReadyAsync(job.ReleaseId, outcome.BcVersion, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (superseded.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // A newer commit landed on the same pull request. The build we were
                // running is about a commit nobody is reviewing now, and the newer
                // job carries its own check run, so this one is recorded as
                // superseded rather than as a failure.
                const string Message = "Superseded by a newer commit on the same pull request.";
                _logger.LogInformation("Pull-request build for release {ReleaseId} was superseded.", job.ReleaseId);
                await importer.MarkFailedAsync(job.ReleaseId, Message, CancellationToken.None).ConfigureAwait(false);
                await buildService.MarkBuildFailedAsync(job.ReleaseId, Message, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Shutdown, not a failure — see #483.
            }
            catch (Exception ex)
            {
                var message = FriendlyMessage(ex);
                _logger.LogError(ex, "Release {ReleaseId} pull-request build failed.", job.ReleaseId);
                await importer.MarkFailedAsync(job.ReleaseId, message, ct).ConfigureAwait(false);
                await buildService.MarkBuildFailedAsync(job.ReleaseId, message, ct).ConfigureAwait(false);
            }

            await checks.CompleteAsync(
                    source.InstallationId, source.RepositoryFullName, job.ReleaseId, source.ForkAuthor, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            webhookQueue.EndBuild(key, superseded, source.HeadSha);
        }
    }

    protected override async Task RunJobAsync(ReleaseImportJob job, CancellationToken ct)
    {
        using var orgScope = AmbientOrganizationScope.Enter(job.Identity);
        await using var scope = _services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<ReleaseImportService>();
        var persistedJobs = scope.ServiceProvider.GetRequiredService<PersistedImportJobs>();

        // Stamp the durable row as running so the admin "Background workers"
        // page reflects current state and the startup reconciler skips this
        // row's re-enqueue on a restart that lands mid-job (the reconciler
        // resets it to queued before re-enqueuing, idempotent). JobRowId of 0
        // means a legacy in-flight job (no DB row) — skip the update.
        if (job.JobRowId != 0)
        {
            try { await persistedJobs.MarkRunningAsync(job.JobRowId, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to mark job row {JobRowId} as running.", job.JobRowId); }
        }

        var openedStreams = new List<Stream>();
        ZipArchive? archive = null;
        string? tempToDelete = null;
        // BC artifact imports stage two zips (application + platform); the
        // second slots carry the platform archive/temp file for cleanup.
        ZipArchive? archive2 = null;
        string? tempToDelete2 = null;
        var jobSucceeded = false;
        string? jobFailureMessage = null;
        try
        {
            // Legacy C/AL TXT has no .app uploads — parse the staged text file
            // directly through CalImportService and finalise via the shared
            // finally block below.
            if (job.Source is ReleaseImportSource.CalTxt calTxt)
            {
                tempToDelete = calTxt.TempPath;
                var calImporter = scope.ServiceProvider.GetRequiredService<CalImportService>();
                try
                {
                    await calImporter.ProcessReleaseAsync(job.ReleaseId, calTxt.TempPath, calTxt.EncodingName, ct).ConfigureAwait(false);
                    jobSucceeded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Graceful shutdown mid-job, not a real failure — don't stamp
                    // the release/job row "failed". Rethrow so the finally leaves
                    // the job row running for the startup reconciler. See #483.
                    throw;
                }
                catch (Exception ex)
                {
                    // ProcessReleaseAsync already flipped the row to failed.
                    jobFailureMessage = FriendlyMessage(ex);
                    _logger.LogError(ex, "Release {ReleaseId} C/AL import failed during processing.", job.ReleaseId);
                }
                return;
            }

            // Maintenance backfill: re-extract system references over
            // already-stored source, no upload (#291). Route to the AL or C/AL
            // path by the source flavour — C/AL stores its slices under CAL/.
            if (job.Source is ReleaseImportSource.Backfill)
            {
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var isCal = await db.OeModuleFiles.AsNoTracking()
                        .Where(f => f.Module!.ReleaseId == job.ReleaseId)
                        .AnyAsync(f => f.Path.StartsWith("CAL/"), ct).ConfigureAwait(false);
                    if (isCal)
                    {
                        var calImporter = scope.ServiceProvider.GetRequiredService<CalImportService>();
                        await calImporter.BackfillSystemReferencesAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await importer.BackfillSystemReferencesAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    }
                    jobSucceeded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Shutdown, not a failure — see #483 (leave for reconciler).
                }
                catch (Exception ex)
                {
                    jobFailureMessage = FriendlyMessage(ex);
                    _logger.LogError(ex, "Release {ReleaseId} system-reference backfill failed.", job.ReleaseId);
                }
                return;
            }

            // Project build: clone → compile → ingest. Unlike the upload paths
            // there's no archive to open — ProjectBuildService produces the
            // uploads in memory and the per-app build report. Partial success
            // (≥1 app compiled) still flips the release to ready; a build that
            // compiled nothing is a failure with the report explaining why.
            // A pull-request build (#627) is the same clone/compile/ingest with three
            // differences, all of which come from there being no user behind it: the
            // repository under review is checked out at the head commit GitHub named,
            // every clone authenticates as the app's installation, and the outcome is
            // reported back as a check run. It also honours supersession - a newer
            // push to the same pull request cancels this build through a linked token,
            // because compiling a commit nobody is looking at any more is work spent
            // on the wrong answer.
            if (job.Source is ReleaseImportSource.PullRequestBuild pullRequest)
            {
                await RunPullRequestBuildAsync(scope, importer, job, pullRequest, openedStreams, ct).ConfigureAwait(false);
                jobSucceeded = true;
                return;
            }

            if (job.Source is ReleaseImportSource.ProjectBuild projectBuild)
            {
                var buildService = scope.ServiceProvider.GetRequiredService<ProjectBuildService>();
                try
                {
                    var outcome = await buildService.BuildAsync(projectBuild.ProjectId, job.ReleaseId, ct: ct).ConfigureAwait(false);
                    foreach (var upload in outcome.Uploads) openedStreams.Add(upload.AppStream);
                    await buildService.PersistResultsAsync(job.ReleaseId, outcome.Results, ct).ConfigureAwait(false);

                    if (outcome.Uploads.Count == 0)
                    {
                        jobFailureMessage = "No extensions compiled successfully. See the build report on the release.";
                        await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                        await buildService.MarkBuildFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                        await NotifyBuildFinishedAsync(scope.ServiceProvider, job.ReleaseId, ct).ConfigureAwait(false);
                        return;
                    }

                    await importer.ProcessReleaseAsync(job.ReleaseId, outcome.Uploads, job.StoreSymbolReference, ct).ConfigureAwait(false);
                    await buildService.MarkCompiledResultsIngestedAsync(job.ReleaseId, ct).ConfigureAwait(false);
                    // Flip the first-class build row ready alongside the Release.
                    await buildService.MarkBuildReadyAsync(job.ReleaseId, outcome.BcVersion, ct).ConfigureAwait(false);
                    await NotifyBuildFinishedAsync(scope.ServiceProvider, job.ReleaseId, ct).ConfigureAwait(false);
                    // The build is final at this point, so publishing it to GitHub can
                    // only ever add to it: a pipeline that names a repository gets a
                    // Release, and every refusal is recorded on the build rather than
                    // failing it (issue #632).
                    await PublishReleaseAsync(scope.ServiceProvider, job.ReleaseId, ct).ConfigureAwait(false);
                    // Release pipelines that ask for it get a release prepared from this
                    // build, waiting for a person to approve it (#934).
                    await PrepareReleasesAsync(scope.ServiceProvider, job.ReleaseId, ct).ConfigureAwait(false);
                    jobSucceeded = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // Shutdown, not a failure — see #483 (leave for reconciler).
                }
                catch (Exception ex)
                {
                    jobFailureMessage = FriendlyMessage(ex);
                    _logger.LogError(ex, "Release {ReleaseId} project build failed.", job.ReleaseId);
                    await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                    await buildService.MarkBuildFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                    await NotifyBuildFinishedAsync(scope.ServiceProvider, job.ReleaseId, ct).ConfigureAwait(false);
                }
                return;
            }

            List<AppFileUpload> uploads;
            try
            {
                switch (job.Source)
                {
                    case ReleaseImportSource.Url url:
                        var downloader = scope.ServiceProvider.GetRequiredService<DvdDownloadService>();
                        tempToDelete = await downloader.DownloadToTempAsync(url.DownloadUrl, ct).ConfigureAwait(false);
                        (uploads, archive) = ReleaseZipStaging.OpenStagedZip(tempToDelete, isDvd: true, openedStreams);
                        break;
                    case ReleaseImportSource.StagedZip staged:
                        tempToDelete = staged.TempPath;
                        (uploads, archive) = ReleaseZipStaging.OpenStagedZip(staged.TempPath, staged.IsDvd, openedStreams);
                        break;
                    case ReleaseImportSource.BcArtifact artifact:
                        var artifacts = scope.ServiceProvider.GetRequiredService<BcArtifactService>();
                        var download = await artifacts.DownloadArtifactSetAsync(artifact.ApplicationUrl, ct).ConfigureAwait(false);
                        tempToDelete = download.ApplicationZipPath;
                        tempToDelete2 = download.PlatformZipPath;
                        // The application (country) artifact carries the localized
                        // apps (Applications.<country>/ + Extensions/); the platform
                        // artifact contributes ONLY System.app — its W1 apps would
                        // collide (same AppId+Version, different bytes) with the
                        // localized ones. See FolderZipWalker's artifact notes.
                        (uploads, archive) = ReleaseZipStaging.OpenBcArtifactZip(download.ApplicationZipPath, isPlatform: false, openedStreams);
                        if (download.PlatformZipPath is not null)
                        {
                            var (platformUploads, platformArchive) =
                                ReleaseZipStaging.OpenBcArtifactZip(download.PlatformZipPath, isPlatform: true, openedStreams);
                            archive2 = platformArchive;
                            uploads = uploads.Concat(platformUploads).ToList();
                        }
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown import source {job.Source.GetType().Name}.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown interrupted the download/open — this is the URL /
                // BcArtifact path the reconciler is designed to resume, so don't
                // mark the release failed. Rethrow and let the finally leave the
                // job row running for re-enqueue. See #483.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Release {ReleaseId} import failed while fetching/opening the archive.", job.ReleaseId);
                jobFailureMessage = FriendlyMessage(ex);
                await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                return;
            }

            if (uploads.Count == 0)
            {
                var diagnostic = archive is not null
                    ? " (" + ReleaseZipStaging.DescribeAppLocations(archive) + ")"
                    : string.Empty;
                jobFailureMessage = "No application .app files were found in the archive. For a DVD we keep everything under "
                    + "its Applications (or Extensions) folder plus System.app — check the URL points to a "
                    + "Business Central DVD." + diagnostic;
                await importer.MarkFailedAsync(job.ReleaseId, jobFailureMessage, ct).ConfigureAwait(false);
                return;
            }

            // Workspace uploads: surface any app folder that declares an
            // app.json but shipped no compiled .app (not built yet). Those are
            // absent from the release by design — the importer only ingests
            // build output — but the admin should know which apps were skipped
            // so they can build and amend them in. Log-only for now.
            if (archive is not null
                && job.Source is ReleaseImportSource.StagedZip { IsDvd: false }
                && FolderZipWalker.LooksLikeWorkspace(archive))
            {
                var uncompiled = FolderZipWalker.DescribeUncompiledAppRoots(archive);
                if (uncompiled.Count > 0)
                {
                    _logger.LogWarning(
                        "Release {ReleaseId}: workspace import skipped {Count} app folder(s) with no compiled .app: {Folders}. Build them and amend the release to include them.",
                        job.ReleaseId, uncompiled.Count, string.Join(", ", uncompiled));
                }
            }

            try
            {
                await importer.ProcessReleaseAsync(job.ReleaseId, uploads, job.StoreSymbolReference, ct).ConfigureAwait(false);
                jobSucceeded = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Shutdown, not a failure — see #483 (leave for reconciler).
            }
            catch (Exception ex)
            {
                // ProcessReleaseAsync already flips the row to failed with the
                // message; nothing to add here but a log line + the failure
                // message for the job-row update below.
                jobFailureMessage = FriendlyMessage(ex);
                _logger.LogError(ex, "Release {ReleaseId} import failed during processing.", job.ReleaseId);
            }
        }
        finally
        {
            foreach (var s in openedStreams)
            {
                try { s.Dispose(); } catch { /* swallow */ }
            }
            archive?.Dispose();
            archive2?.Dispose();
            if (tempToDelete is not null && File.Exists(tempToDelete))
            {
                try { File.Delete(tempToDelete); } catch { /* swallow */ }
            }
            if (tempToDelete2 is not null && File.Exists(tempToDelete2))
            {
                try { File.Delete(tempToDelete2); } catch { /* swallow */ }
            }
            if (job.JobRowId != 0 && ShouldFinaliseJobRow(jobSucceeded, ct.IsCancellationRequested))
            {
                try
                {
                    // Finalise with CancellationToken.None: on shutdown the
                    // ambient ct is already cancelled, and persisting with it
                    // would throw before the write lands — stranding the job
                    // row in "running" forever. The status write must complete
                    // regardless of why we're unwinding.
                    if (jobSucceeded)
                        await persistedJobs.MarkCompletedAsync(job.JobRowId, CancellationToken.None).ConfigureAwait(false);
                    else
                        await persistedJobs.MarkFailedAsync(job.JobRowId, jobFailureMessage ?? "Import failed.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to finalise job row {JobRowId}.", job.JobRowId);
                }
            }
        }
    }

    /// <summary>
    /// Whether the durable <c>oe_import_jobs</c> row should be finalised
    /// (stamped completed/failed) as <see cref="RunJobAsync"/> unwinds.
    ///
    /// <para>
    /// A job interrupted by graceful shutdown — the stopping token cancelled and
    /// the job hadn't already succeeded — is deliberately left <c>running</c> so
    /// <see cref="PersistedImportJobs.ReconcileOnStartupAsync"/> resumes it
    /// (URL / BcArtifact / project-build) or fails it with a source-specific
    /// message (a lost staged-zip), instead of a bogus "operation canceled"
    /// failure that falls outside the reconciler's queued/running sweep and would
    /// never be re-enqueued. A job that genuinely succeeded still persists its
    /// success even mid-shutdown. See #483.
    /// </para>
    /// </summary>
    internal static bool ShouldFinaliseJobRow(bool jobSucceeded, bool cancellationRequested) =>
        jobSucceeded || !cancellationRequested;

    private static string FriendlyMessage(Exception ex) =>
        ex is PlanValidationException pve && pve.Errors.Count > 0
            ? pve.Errors.First().Value
            : ex.Message;
}
