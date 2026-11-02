using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// CRUD over the durable <c>oe_import_jobs</c> rows that mirror every
/// <see cref="ReleaseImportQueue"/> enqueue. Created so the in-memory channel
/// can survive a process restart: the row carries everything the worker needs
/// to resume a URL download (the only source kind we can pick back up — staged
/// uploads live in container-local <c>/tmp</c> and are gone after restart).
///
/// <para>
/// Scoped lifetime: takes <see cref="AppDbContext"/>, no shared mutable state.
/// The endpoint creates a row, the worker updates its status, the startup
/// reconciler scans for survivors. The reconciler runs cross-org by design,
/// matching the same blessed startup-maintenance bucket the migration / seed
/// / interrupted-release sweep already occupies in <c>StartupTasks</c>.
/// </para>
/// </summary>
public sealed class PersistedImportJobs
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public PersistedImportJobs(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Writes a new <c>queued</c> row for a release the endpoint just created.
    /// Returns the generated row id so the caller can stamp it on the
    /// <see cref="ReleaseImportJob"/> it enqueues to the in-memory channel.
    /// </summary>
    public async Task<long> CreateAsync(
        int releaseId,
        AmbientOrganizationScope.OrganizationIdentity identity,
        ReleaseImportSource source,
        bool storeSymbolReference,
        CancellationToken ct = default)
    {
        var row = NewRow(releaseId, identity, source, storeSymbolReference);
        _db.OeImportJobs.Add(row);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.Id;
    }

    /// <summary>
    /// Writes a <c>queued</c> backfill row for each of <paramref name="releaseIds"/> that
    /// has no backfill queued or running yet, all in one transaction, and returns the
    /// rows it wrote. All or nothing: a cancelled request or a failed write leaves no
    /// row behind that nothing will ever feed to the queue, and that would block the
    /// next attempt at the same release (#1542). Two submits that race can still both
    /// pass the already-queued check; the cost is a repeated backfill, which is
    /// harmless, so it is not worth a lock.
    /// </summary>
    public async Task<IReadOnlyList<(int ReleaseId, long JobRowId)>> CreateBackfillsAsync(
        IReadOnlyList<int> releaseIds,
        AmbientOrganizationScope.OrganizationIdentity identity,
        CancellationToken ct = default)
    {
        await using var tx = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var active = await ListReleasesWithActiveBackfillAsync(ct).ConfigureAwait(false);
        var source = new ReleaseImportSource.Backfill();
        var rows = releaseIds.Distinct()
            .Where(id => !active.Contains(id))
            .Select(id => NewRow(id, identity, source, storeSymbolReference: false))
            .ToList();
        _db.OeImportJobs.AddRange(rows);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (tx is not null) await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows.Select(r => (r.ReleaseId, r.Id)).ToList();
    }

    private OeImportJob NewRow(
        int releaseId,
        AmbientOrganizationScope.OrganizationIdentity identity,
        ReleaseImportSource source,
        bool storeSymbolReference)
    {
        var row = new OeImportJob
        {
            OrganizationId = identity.OrganizationId,
            ReleaseId = releaseId,
            UserId = identity.UserId,
            IsSiteAdmin = identity.IsSiteAdmin,
            IsSystemOrganization = identity.IsSystemOrganization,
            StoreSymbolReference = storeSymbolReference,
            Status = "queued",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        switch (source)
        {
            case ReleaseImportSource.Url url:
                row.Kind = "url";
                row.DownloadUrl = url.DownloadUrl;
                break;
            case ReleaseImportSource.BcArtifact artifact:
                // Resumable like a URL import: the application-artifact URL was
                // resolved from Microsoft's index before enqueue, so a restart
                // re-downloads it idempotently into a fresh temp file.
                row.Kind = "bc_artifact";
                row.DownloadUrl = artifact.ApplicationUrl;
                break;
            case ReleaseImportSource.ProjectBuild build:
                // Resumable: the project id re-clones and rebuilds from scratch
                // into fresh temp dirs, at the commits the build already recorded
                // if it got that far (#1110), so a restart picks it back up
                // idempotently like a URL/artifact import.
                row.Kind = "project_build";
                row.ProjectId = build.ProjectId;
                break;
            case ReleaseImportSource.StagedZip staged:
                row.Kind = "staged_zip";
                row.StagedZipPath = staged.TempPath;
                row.StagedIsDvd = staged.IsDvd;
                break;
            case ReleaseImportSource.CalTxt cal:
                // Like a staged zip, the temp file is container-local and not
                // resumed across restarts; the encoding isn't persisted because
                // the row is only ever marked failed on restart, never re-run.
                row.Kind = "cal_txt";
                row.StagedZipPath = cal.TempPath;
                break;
            case ReleaseImportSource.Backfill:
                // No payload — re-extracts from already-stored source (#291).
                row.Kind = "backfill_system_references";
                break;
            default:
                throw new InvalidOperationException($"Unknown import source {source.GetType().Name}.");
        }
        return row;
    }

    public async Task MarkRunningAsync(long jobRowId, CancellationToken ct = default)
    {
        // Fence category 3 (background worker, no request org): pinned to j.Id == jobRowId.
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "running";
        row.StartedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkCompletedAsync(long jobRowId, CancellationToken ct = default)
    {
        // Fence category 3 (background worker, no request org): pinned to j.Id == jobRowId.
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "completed";
        row.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fails a <c>queued</c> row a request wrote but could not put on the in-memory
    /// queue (the request ended while the queue was full). Left queued, the startup
    /// reconciler would resume it, and a resumed job wipes its release first, by then
    /// perhaps one a later Retry has imported (#1612). Unlike
    /// <see cref="MarkFailedAsync"/> this keeps the organisation filter on: the row
    /// was written by the calling request, for its own organisation. Never cancelled,
    /// since the request's token may be why the enqueue failed.
    /// </summary>
    public Task FailUnqueuedAsync(long jobRowId, string errorMessage)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var message = Truncate(errorMessage, 4000);
        return _db.OeImportJobs
            .Where(j => j.Id == jobRowId && j.Status == "queued")
            .ExecuteUpdateAsync(u => u
                .SetProperty(j => j.Status, "failed")
                .SetProperty(j => j.ErrorMessage, message)
                .SetProperty(j => j.CompletedAt, now), CancellationToken.None);
    }

    public async Task MarkFailedAsync(long jobRowId, string errorMessage, CancellationToken ct = default)
    {
        // Fence category 3 (background worker, no request org): pinned to j.Id == jobRowId.
        var row = await _db.OeImportJobs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(j => j.Id == jobRowId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = "failed";
        row.ErrorMessage = Truncate(errorMessage, 4000);
        row.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Startup reconciliation. Returns the resumable jobs to re-enqueue (URL,
    /// artifact and build imports, backfills that never started, and a backfill
    /// a restart cut off, once - see <see cref="CutOffBackfillNote"/>); the
    /// caller (StartupTasks) hands them to <see cref="ResumedImportJobs"/>,
    /// oldest first, with the cut-off backfills last.
    /// Staged-zip jobs are flipped to <c>failed</c> in-place (their temp file
    /// is gone) along with their owning <c>oe_releases</c> row so the admin sees
    /// the failure reason on the list and can re-submit.
    /// </summary>
    public async Task<IReadOnlyList<ReleaseImportJob>> ReconcileOnStartupAsync(CancellationToken ct = default)
    {
        // Cross-org sweep — same blessed startup-maintenance bucket the
        // migration / seed / interrupted-release reconciliation already
        // sits in (see StartupTasks). No user in scope at startup; reading
        // every org's job rows here is the design.
        // A build's row comes along so it resumes in its place in the build queue (#1137).
        // The filter is off for that read too; it is pinned to b.ReleaseId == j.ReleaseId.
        // Fence category 3 (startup, no request org): each job carries its own org id.
        var survivors = await _db.OeImportJobs.IgnoreQueryFilters()
            .Where(j => j.Status == "queued" || j.Status == "running")
            // Oldest first, so waiting push builds resume in the order they were pushed.
            .OrderBy(j => j.Id)
            .Select(j => new
            {
                Job = j,
                Build = _db.OeProjectBuilds
                    .Where(b => b.ReleaseId == j.ReleaseId)
                    .Select(b => new { b.PipelineId, b.BcTarget, b.Trigger, b.Status })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (survivors.Count == 0) return Array.Empty<ReleaseImportJob>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var toResume = new List<ReleaseImportJob>();
        // Backfills a restart cut off go after everything else (see below).
        var cutOffBackfills = new List<ReleaseImportJob>();
        // releaseId → human noun for the lost upload, so the failure copy names
        // the right thing (a ZIP vs a C/AL file).
        var lostReleases = new Dictionary<int, string>();

        foreach (var survivor in survivors)
        {
            var row = survivor.Job;
            switch (row.Kind)
            {
                case "url" when !string.IsNullOrEmpty(row.DownloadUrl):
                    // Reset to queued so the worker re-runs from scratch. The
                    // download is idempotent: it writes to a fresh temp file.
                    // The import is not: it saves each module, its files and
                    // objects, and the release's references in separate chunks,
                    // so the interrupted run can have left part of the release
                    // behind. Resumed tells the worker to wipe it first (#1209).
                    row.Status = "queued";
                    row.StartedAt = null;
                    toResume.Add(new ReleaseImportJob(
                        ReleaseId: row.ReleaseId,
                        Identity: new AmbientOrganizationScope.OrganizationIdentity(
                            row.OrganizationId, row.UserId, row.IsSiteAdmin, row.IsSystemOrganization),
                        Source: new ReleaseImportSource.Url(row.DownloadUrl),
                        StoreSymbolReference: row.StoreSymbolReference,
                        JobRowId: row.Id,
                        Resumed: true));
                    break;
                case "bc_artifact" when !string.IsNullOrEmpty(row.DownloadUrl):
                    // Re-resolve happened at enqueue; the stored application URL
                    // is enough to re-download the whole artifact set on resume.
                    // Partly written like a URL import, so wiped first (#1209).
                    row.Status = "queued";
                    row.StartedAt = null;
                    toResume.Add(new ReleaseImportJob(
                        ReleaseId: row.ReleaseId,
                        Identity: new AmbientOrganizationScope.OrganizationIdentity(
                            row.OrganizationId, row.UserId, row.IsSiteAdmin, row.IsSystemOrganization),
                        Source: new ReleaseImportSource.BcArtifact(row.DownloadUrl),
                        StoreSymbolReference: row.StoreSymbolReference,
                        JobRowId: row.Id,
                        Resumed: true));
                    break;
                case "project_build" when survivor.Build?.Status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed:
                    // The build had already finished: the restart landed while it was
                    // being announced, published or prepared for release. Running it
                    // again would rebuild and re-index a finished build and could
                    // publish or prepare it twice, so the row is closed instead (#1181).
                    row.Status = survivor.Build.Status == ProjectBuildStatus.Ready ? "completed" : "failed";
                    if (row.Status == "failed") row.ErrorMessage ??= "The build had failed before the restart.";
                    row.CompletedAt = now;
                    break;
                case "project_build" when row.ProjectId is int projectId:
                    // Re-clone and rebuild from scratch; nothing on disk survives
                    // a restart, but the build row holds the commits its first
                    // clone recorded, so the rebuild is of the same code (#1110).
                    // Its ingest may have been cut off part way, so wiped first (#1209).
                    row.Status = "queued";
                    row.StartedAt = null;
                    toResume.Add(new ReleaseImportJob(
                        ReleaseId: row.ReleaseId,
                        Identity: new AmbientOrganizationScope.OrganizationIdentity(
                            row.OrganizationId, row.UserId, row.IsSiteAdmin, row.IsSystemOrganization),
                        Source: new ReleaseImportSource.ProjectBuild(projectId),
                        StoreSymbolReference: row.StoreSymbolReference,
                        JobRowId: row.Id,
                        BuildOrder: survivor.Build is { } build
                            ? ProjectBuildOrder.For(build.PipelineId, build.BcTarget, build.Trigger)
                            : ProjectBuildOrder.Unordered,
                        Resumed: true));
                    break;
                case "backfill_system_references" when row.Status == "running" && row.ErrorMessage == CutOffBackfillNote:
                    // Cut off a second time. It may be the backfill itself that
                    // takes the process down (out of memory, say); running it
                    // again on every start would loop the container and the
                    // rest of the run would never finish. So it fails, and an
                    // admin decides.
                    row.Status = "failed";
                    row.ErrorMessage = "The system-reference backfill was cut off by a restart twice, so it was not "
                        + "started again. Its system references may be incomplete. Re-trigger it from the release.";
                    row.CompletedAt = now;
                    break;
                case "backfill_system_references":
                {
                    // A bulk run queues every release and works through them for
                    // hours, so a deploy in the middle must not fail the rest
                    // (#1542). One that never started has changed nothing; one
                    // the restart cut off had deleted the release's system
                    // references and emitted only some of them again, and left
                    // failed it would leave Find System References quietly
                    // incomplete for that release (OEI2-2). Both run again: a
                    // backfill deletes before it emits, so a second run is safe.
                    // Not Resumed, which would wipe the release's rows. A cut-off
                    // one is marked, so a second cut-off fails it (above), and
                    // goes last, so the rest of the run is not stuck behind it.
                    var cutOff = row.Status == "running";
                    row.Status = "queued";
                    row.StartedAt = null;
                    if (cutOff) row.ErrorMessage = CutOffBackfillNote;
                    (cutOff ? cutOffBackfills : toResume).Add(new ReleaseImportJob(
                        ReleaseId: row.ReleaseId,
                        Identity: new AmbientOrganizationScope.OrganizationIdentity(
                            row.OrganizationId, row.UserId, row.IsSiteAdmin, row.IsSystemOrganization),
                        Source: new ReleaseImportSource.Backfill(),
                        StoreSymbolReference: false,
                        JobRowId: row.Id));
                    break;
                }
                default:
                    // Staged-zip / cal-txt can't be resumed — the temp file is
                    // gone. The existing OeRelease row reconciler in StartupTasks
                    // already flips ingesting releases to failed, but its message
                    // is generic; record a more specific failure on the job row.
                    var noun = row.Kind == "cal_txt" ? "C/AL file" : "ZIP";
                    row.Status = "failed";
                    row.ErrorMessage = $"The uploaded {noun} was lost when the container restarted. Re-submit to try again.";
                    row.CompletedAt = now;
                    lostReleases[row.ReleaseId] = noun;
                    break;
            }
        }

        // For lost staged-zip / cal-txt jobs, flip the matching OeRelease row to
        // failed with the same message so the list-page surfaces the cause.
        if (lostReleases.Count > 0)
        {
            var ids = lostReleases.Keys.ToList();
            // Fence category 3 (startup reconciliation, no request org): pinned to the release
            // ids collected from the lost jobs above.
            var releases = await _db.OeReleases.IgnoreQueryFilters()
                .Where(r => ids.Contains(r.Id) && r.Status == "ingesting")
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var r in releases)
            {
                r.Status = "failed";
                r.StatusMessage = $"The uploaded {lostReleases.GetValueOrDefault(r.Id, "file")} was lost when the container restarted. Re-submit to try again.";
                r.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        toResume.AddRange(cutOffBackfills);
        return toResume;
    }

    /// <summary>
    /// Left on a backfill row that a restart cut off and that startup queued
    /// again. Found on a row that is running at the next start, it means the
    /// second run was cut off too, and the row is failed rather than queued a
    /// third time. Kept in the existing message column so no schema change is
    /// needed; it stays on the row once the backfill completes, as a note.
    /// </summary>
    internal const string CutOffBackfillNote = "Started again after a restart cut it off.";

    /// <summary>
    /// The releases of the current organisation (EF query filter) that already have a
    /// system-reference backfill queued or running, so the bulk backfill does not
    /// queue the same release twice (#1542).
    /// </summary>
    public async Task<HashSet<int>> ListReleasesWithActiveBackfillAsync(CancellationToken ct = default)
    {
        var ids = await _db.OeImportJobs.AsNoTracking()
            .Where(j => j.Kind == "backfill_system_references" && (j.Status == "queued" || j.Status == "running"))
            .Select(j => j.ReleaseId)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Returns the most recent import job for a Release (org-scoped via the EF
    /// query filter), or <see langword="null"/> when none exists. The retry path
    /// uses it to reuse the original source: a <c>url</c> job's
    /// <see cref="OeImportJob.DownloadUrl"/> lets a retry re-run without the admin
    /// re-pasting the link, and the kind / DVD flag tell the UI whether a
    /// re-upload is required (the staged-zip / cal-txt temp file is gone).
    /// Backfill jobs are skipped: they never carry a source (#1338).
    /// </summary>
    public async Task<ImportJobOrigin?> GetLatestForReleaseAsync(int releaseId, CancellationToken ct = default)
    {
        return await _db.OeImportJobs.AsNoTracking()
            // A maintenance backfill re-scans stored source; it is never how the
            // release got its content, so it can't be the source a retry reuses.
            .Where(j => j.ReleaseId == releaseId && j.Kind != "backfill_system_references")
            // Newest first; tie-break on the serial id so two jobs created within
            // the clock's resolution still resolve to the genuinely-latest row.
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Select(j => new ImportJobOrigin(j.Kind, j.DownloadUrl, j.StagedIsDvd, j.StoreSymbolReference, j.ProjectId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Snapshot for the admin "Background workers" page — depth + recent rows.</summary>
    public async Task<ImportQueueSnapshot> SnapshotAsync(int recentLimit = 10, CancellationToken ct = default)
    {
        // Fence category 2 (SiteAdmin cross-org console): the snapshot's only caller is
        // /site-admin/workers, gated by [Authorize(Roles = SiteAdminRole)].
        var pending = await _db.OeImportJobs.IgnoreQueryFilters()
            .CountAsync(j => j.Status == "queued" || j.Status == "running", ct).ConfigureAwait(false);
        // Same category 2 read, for the same SiteAdmin-only page.
        var jobs = await _db.OeImportJobs.IgnoreQueryFilters()
            .OrderByDescending(j => j.CreatedAt)
            .Take(recentLimit)
            .ToListAsync(ct).ConfigureAwait(false);

        // Resolve release labels cross-org (this is the SiteAdmin console) so
        // the workers page can name each import rather than show a bare id.
        var releaseIds = jobs.Select(j => j.ReleaseId).Distinct().ToList();
        // Fence category 2 (SiteAdmin cross-org console): pinned to the job rows' release ids.
        var labels = await _db.OeReleases.IgnoreQueryFilters()
            .Where(r => releaseIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Label })
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct).ConfigureAwait(false);

        var recent = jobs
            .Select(j => new ImportQueueRow(
                j.Id, j.ReleaseId, labels.GetValueOrDefault(j.ReleaseId),
                j.Kind, j.Status, j.CreatedAt, j.StartedAt, j.CompletedAt, j.ErrorMessage))
            .ToList();
        return new ImportQueueSnapshot(pending, recent);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max));
}

/// <summary>
/// The reusable origin of a Release's most recent import — its source
/// <see cref="OeImportJob.Kind"/> (<c>url</c> / <c>staged_zip</c> / <c>cal_txt</c>
/// / <c>backfill_system_references</c>), the download URL when it was a URL
/// import, the DVD-subset flag for staged zips, and the store-symbol-reference
/// choice. Drives the retry endpoint's source reuse and the manage page's
/// prefill / re-upload prompt.
/// </summary>
public sealed record ImportJobOrigin(string Kind, string? DownloadUrl, bool? StagedIsDvd, bool StoreSymbolReference, int? ProjectId = null);

public sealed record ImportQueueSnapshot(int Pending, IReadOnlyList<ImportQueueRow> Recent);

public sealed record ImportQueueRow(
    long Id,
    int ReleaseId,
    string? ReleaseLabel,
    string Kind,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage);
