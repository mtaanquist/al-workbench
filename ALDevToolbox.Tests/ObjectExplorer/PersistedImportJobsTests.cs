using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="PersistedImportJobs"/> end-to-end against the shared
/// <see cref="TestDb"/> Postgres fixture: the create / mark-* lifecycle plus
/// the startup reconciler's branching (URL → re-enqueue list; staged-zip →
/// failed in place, release flipped to failed too).
/// </summary>
public sealed class PersistedImportJobsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private PersistedImportJobs NewService(Data.AppDbContext ctx) =>
        new(ctx, TimeProvider.System);

    private async Task<OeRelease> SeedReleaseAsync(Data.AppDbContext ctx, string status = "ingesting")
    {
        var release = new OeRelease
        {
            OrganizationId = Identity.OrganizationId,
            Label = "Test Release " + Guid.NewGuid().ToString("N").Substring(0, 8),
            Kind = "first_party",
            Status = status,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release;
    }

    // ── Bulk backfill rows (#1542) ───────────────────────────────────────

    [Fact]
    public async Task CreateBackfillsAsync_writes_no_row_at_all_when_one_of_them_fails()
    {
        await using var ctx = _db.NewContext();
        var release = await SeedReleaseAsync(ctx, status: "ready");

        // The second id names no release, so its row breaks the foreign key on save.
        var act = () => NewService(ctx).CreateBackfillsAsync([release.Id, int.MaxValue - 7], Identity, DateTime.MinValue);

        await act.Should().ThrowAsync<DbUpdateException>();
        await using var read = _db.NewContext();
        (await read.OeImportJobs.AsNoTracking().CountAsync(j => j.Kind == "backfill_system_references"))
            .Should().Be(0, "a half-written bulk run would leave rows nothing ever feeds to the queue");
    }

    [Fact]
    public async Task CreateBackfillsAsync_writes_no_row_when_the_request_is_cancelled()
    {
        await using var ctx = _db.NewContext();
        var release = await SeedReleaseAsync(ctx, status: "ready");
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => NewService(ctx).CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue, cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await using var read = _db.NewContext();
        (await read.OeImportJobs.AsNoTracking().CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// #1578 OEI2-1: a second click during a bulk run leaves alone the releases the run
    /// has already finished, unless the release changed after its backfill.
    /// </summary>
    [Fact]
    public async Task CreateBackfillsAsync_skips_a_release_backfilled_since_it_last_changed()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var done = await SeedReleaseAsync(ctx, status: "ready");
        var changed = await SeedReleaseAsync(ctx, status: "ready");
        var first = await svc.CreateBackfillsAsync([done.Id, changed.Id], Identity, DateTime.MinValue);
        foreach (var (_, rowId) in first)
        {
            await svc.MarkRunningAsync(rowId);
            await svc.MarkCompletedAsync(rowId);
        }
        // Imported again after its backfill ran.
        await ctx.OeReleases.Where(r => r.Id == changed.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.UpdatedAt, DateTime.UtcNow.AddMinutes(5)));

        var second = await NewService(_db.NewContext()).CreateBackfillsAsync([done.Id, changed.Id], Identity, DateTime.MinValue);

        second.Select(r => r.ReleaseId).Should().Equal(changed.Id);
    }

    /// <summary>
    /// A backfill that failed after an earlier one completed decides: the release still
    /// needs one, so the bulk action queues it.
    /// </summary>
    [Fact]
    public async Task CreateBackfillsAsync_queues_a_release_whose_newest_backfill_failed_after_one_completed()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx, status: "ready");
        var completed = (await svc.CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue)).Single().JobRowId;
        await svc.MarkRunningAsync(completed);
        await svc.MarkCompletedAsync(completed);
        var failed = await svc.CreateAsync(release.Id, Identity, new ReleaseImportSource.Backfill(), storeSymbolReference: false);
        await svc.MarkRunningAsync(failed);
        await svc.MarkFailedAsync(failed, "Cut off twice.");
        await ctx.OeImportJobs.Where(j => j.Id == failed)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CompletedAt, DateTime.UtcNow.AddMinutes(1)));

        var again = await NewService(_db.NewContext()).CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue);

        again.Select(r => r.ReleaseId).Should().Equal(release.Id);
    }

    /// <summary>
    /// #1578 OEI2-3: a dump taken during a backfill holds its row as running. After the
    /// restore nothing runs it, so it is failed and replaced like a stale queued row.
    /// </summary>
    [Fact]
    public async Task CreateBackfillsAsync_replaces_a_running_row_from_before_the_restore()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx, status: "ready");
        var rowId = (await svc.CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue)).Single().JobRowId;
        await svc.MarkRunningAsync(rowId);
        var restoredAt = DateTime.UtcNow.AddSeconds(1);

        var again = await NewService(_db.NewContext()).CreateBackfillsAsync([release.Id], Identity, restoredAt);

        again.Select(r => r.ReleaseId).Should().Equal(release.Id);
        await using var read = _db.NewContext();
        var old = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == rowId);
        old.Status.Should().Be("failed");
        old.ErrorMessage.Should().Be(PersistedImportJobs.StaleBackfillMessage);
    }

    /// <summary>
    /// #1578 OEI2-3: a queued backfill row from before this process started or the last
    /// restore has nothing feeding it. It is failed and replaced rather than counted as
    /// waiting; one queued since is still left alone.
    /// </summary>
    [Fact]
    public async Task CreateBackfillsAsync_replaces_a_queued_row_nothing_feeds_and_keeps_one_that_is_fed()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var orphaned = await SeedReleaseAsync(ctx, status: "ready");
        var fed = await SeedReleaseAsync(ctx, status: "ready");
        var rows = await svc.CreateBackfillsAsync([orphaned.Id, fed.Id], Identity, DateTime.MinValue);
        var orphanedRow = rows.Single(r => r.ReleaseId == orphaned.Id).JobRowId;
        var restoredAt = DateTime.UtcNow;
        await ctx.OeImportJobs.Where(j => j.Id == orphanedRow)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAt, restoredAt.AddHours(-1)));
        await ctx.OeImportJobs.Where(j => j.ReleaseId == fed.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAt, restoredAt.AddMinutes(1)));

        var again = await NewService(_db.NewContext()).CreateBackfillsAsync([orphaned.Id, fed.Id], Identity, restoredAt);

        again.Select(r => r.ReleaseId).Should().Equal(orphaned.Id);
        await using var read = _db.NewContext();
        var old = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == orphanedRow);
        old.Status.Should().Be("failed");
        old.ErrorMessage.Should().Be(PersistedImportJobs.StaleBackfillMessage);
    }

    /// <summary>
    /// A backfill startup queues again is fed by this process, so it is stamped as
    /// queued now and the bulk action does not take it for one nothing feeds.
    /// </summary>
    [Fact]
    public async Task A_backfill_queued_again_at_startup_is_not_taken_for_a_stale_row()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx, status: "ready");
        var rowId = (await svc.CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue)).Single().JobRowId;
        await ctx.OeImportJobs.Where(j => j.Id == rowId)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.CreatedAt, DateTime.UtcNow.AddHours(-3)));
        var startedAt = DateTime.UtcNow.AddSeconds(-1);

        await NewService(_db.NewContext()).ReconcileOnStartupAsync();
        var again = await NewService(_db.NewContext()).CreateBackfillsAsync([release.Id], Identity, startedAt);

        again.Should().BeEmpty("the reconciled row is waiting in this process's queue");
        await using var read = _db.NewContext();
        (await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == rowId)).Status.Should().Be("queued");
    }

    /// <summary>#1578 OEI2-4: a backfill a restore stopped goes back to queued, not failed.</summary>
    [Fact]
    public async Task MarkQueuedAgainAsync_puts_a_running_backfill_back_to_queued()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx, status: "ready");
        var rowId = (await svc.CreateBackfillsAsync([release.Id], Identity, DateTime.MinValue)).Single().JobRowId;
        await svc.MarkRunningAsync(rowId);

        await svc.MarkQueuedAgainAsync(rowId);

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == rowId);
        row.Status.Should().Be("queued");
        row.StartedAt.Should().BeNull();
    }

    /// <summary>
    /// A deploy in the middle of a bulk run: the backfills that never started
    /// run again, and so does the one the restart cut off (OEI2-2). That one
    /// had deleted its release's system references and emitted only some, so
    /// failing it would leave the release quietly incomplete; a backfill
    /// deletes before it emits, so running it again is safe.
    /// </summary>
    [Fact]
    public async Task Reconcile_queues_both_a_backfill_that_never_started_and_one_cut_off_while_running()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var waiting = await SeedReleaseAsync(ctx, status: "ready");
        var interrupted = await SeedReleaseAsync(ctx, status: "ready");
        var rows = await svc.CreateBackfillsAsync([waiting.Id, interrupted.Id], Identity, DateTime.MinValue);
        var waitingRow = rows.Single(r => r.ReleaseId == waiting.Id).JobRowId;
        var interruptedRow = rows.Single(r => r.ReleaseId == interrupted.Id).JobRowId;
        await svc.MarkRunningAsync(interruptedRow);

        var resumable = await NewService(_db.NewContext()).ReconcileOnStartupAsync();

        resumable.Select(j => j.JobRowId).Should().BeEquivalentTo([waitingRow, interruptedRow]);
        resumable.Should().OnlyContain(j => j.Source is ReleaseImportSource.Backfill);
        resumable.Should().OnlyContain(j => !j.Resumed,
            "a resumed job wipes the release's ingested rows, and a backfill must never do that");
        await using var read = _db.NewContext();
        var after = await read.OeImportJobs.AsNoTracking()
            .Where(j => j.Id == waitingRow || j.Id == interruptedRow).ToListAsync();
        after.Should().OnlyContain(j => j.Status == "queued" && j.StartedAt == null);
        after.Single(j => j.Id == waitingRow).ErrorMessage.Should().BeNull();
        after.Single(j => j.Id == interruptedRow).ErrorMessage.Should().Be(PersistedImportJobs.CutOffBackfillNote);
    }

    /// <summary>
    /// The cut-off backfill goes after the ones that never started, so a run
    /// is not stuck behind it; and if it is cut off again - perhaps because
    /// it is what takes the process down - it is failed, not queued a third
    /// time, so the container does not restart in a loop.
    /// </summary>
    [Fact]
    public async Task A_backfill_cut_off_twice_is_failed_and_one_cut_off_once_runs_last()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var interrupted = await SeedReleaseAsync(ctx, status: "ready");
        var waiting = await SeedReleaseAsync(ctx, status: "ready");
        var rows = await svc.CreateBackfillsAsync([interrupted.Id, waiting.Id], Identity, DateTime.MinValue);
        var interruptedRow = rows.Single(r => r.ReleaseId == interrupted.Id).JobRowId;
        var waitingRow = rows.Single(r => r.ReleaseId == waiting.Id).JobRowId;
        await svc.MarkRunningAsync(interruptedRow);

        var first = await NewService(_db.NewContext()).ReconcileOnStartupAsync();

        first.Select(j => j.JobRowId).Should().Equal(waitingRow, interruptedRow);

        // The second start: the re-run was cut off as well.
        await NewService(_db.NewContext()).MarkRunningAsync(interruptedRow);
        var second = await NewService(_db.NewContext()).ReconcileOnStartupAsync();

        second.Select(j => j.JobRowId).Should().Equal(waitingRow);
        await using var read = _db.NewContext();
        var failed = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == interruptedRow);
        failed.Status.Should().Be("failed");
        failed.ErrorMessage.Should().Contain("cut off by a restart twice");
        failed.CompletedAt.Should().NotBeNull();
    }

    /// <summary>
    /// #1549: an import that had saved its release ready, but died before its row was
    /// closed, is closed on start rather than resumed, which would wipe the finished
    /// release and download it again.
    /// </summary>
    [Theory]
    [InlineData("url")]
    [InlineData("bc_artifact")]
    public async Task Reconcile_closes_a_url_or_artifact_job_whose_release_is_already_ready(string kind)
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx, status: "ready");
        ReleaseImportSource source = kind == "url"
            ? new ReleaseImportSource.Url("https://download.microsoft.com/x.zip")
            : new ReleaseImportSource.BcArtifact("https://bcartifacts.example/onprem/26.0.1/dk");
        var id = await svc.CreateAsync(release.Id, Identity, source, storeSymbolReference: false);
        await svc.MarkRunningAsync(id); // killed after the ingest, during the drift scan

        var resumable = await NewService(_db.NewContext()).ReconcileOnStartupAsync();

        resumable.Should().BeEmpty();
        await using var read = _db.NewContext();
        (await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id)).Status.Should().Be("completed");
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == release.Id)).Status.Should().Be("ready");
    }

    [Fact]
    public async Task CreateAsync_writes_a_queued_row_with_url_source_fields()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);

        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/x.zip"),
            storeSymbolReference: true);

        id.Should().BeGreaterThan(0);
        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.ReleaseId.Should().Be(release.Id);
        row.Kind.Should().Be("url");
        row.DownloadUrl.Should().Be("https://download.microsoft.com/x.zip");
        row.StagedZipPath.Should().BeNull();
        row.StoreSymbolReference.Should().BeTrue();
        row.Status.Should().Be("queued");
    }

    [Fact]
    public async Task CreateAsync_writes_staged_zip_source_fields()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);

        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.StagedZip("/tmp/oe-folder-abc.zip", IsDvd: true),
            storeSymbolReference: false);

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.Kind.Should().Be("staged_zip");
        row.StagedZipPath.Should().Be("/tmp/oe-folder-abc.zip");
        row.StagedIsDvd.Should().BeTrue();
        row.DownloadUrl.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestForReleaseAsync_returns_the_most_recent_origin()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);

        await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/old.zip"), storeSymbolReference: false);
        // A later job — e.g. a retry with a corrected URL — must win.
        await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/new.zip"), storeSymbolReference: true);

        var origin = await svc.GetLatestForReleaseAsync(release.Id);
        origin.Should().NotBeNull();
        origin!.Kind.Should().Be("url");
        origin.DownloadUrl.Should().Be("https://download.microsoft.com/new.zip");
        origin.StoreSymbolReference.Should().BeTrue();
    }

    /// <summary>
    /// #1338: a maintenance backfill carries no source. When it was the newest
    /// row it hid the import's own, and Retry had nothing to re-run.
    /// </summary>
    [Fact]
    public async Task GetLatestForReleaseAsync_skips_a_later_backfill_job()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);

        await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/dvd.zip"), storeSymbolReference: false);
        await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Backfill(), storeSymbolReference: false);

        var origin = await svc.GetLatestForReleaseAsync(release.Id);
        origin.Should().NotBeNull();
        origin!.Kind.Should().Be("url");
        origin.DownloadUrl.Should().Be("https://download.microsoft.com/dvd.zip");
    }

    [Fact]
    public async Task GetLatestForReleaseAsync_is_null_when_the_only_job_is_a_backfill()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Backfill(), storeSymbolReference: false);

        (await svc.GetLatestForReleaseAsync(release.Id)).Should().BeNull(
            because: "the manage page then offers the URL field, so the release can still be retried");
    }

    [Fact]
    public async Task GetLatestForReleaseAsync_returns_null_when_no_job_exists()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        (await svc.GetLatestForReleaseAsync(release.Id)).Should().BeNull();
    }

    [Fact]
    public async Task MarkRunning_then_completed_flows_through_the_status_field()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/x.zip"), storeSymbolReference: false);

        await svc.MarkRunningAsync(id);
        await svc.MarkCompletedAsync(id);

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.Status.Should().Be("completed");
        row.StartedAt.Should().NotBeNull();
        row.CompletedAt.Should().NotBeNull();
        row.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailed_records_the_error_message()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/x.zip"), storeSymbolReference: false);

        await svc.MarkFailedAsync(id, "download stalled");

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.Status.Should().Be("failed");
        row.ErrorMessage.Should().Be("download stalled");
        row.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconciler_returns_url_jobs_for_resume_and_resets_their_status()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/x.zip"), storeSymbolReference: true);
        await svc.MarkRunningAsync(id); // simulates a crash mid-download

        var resumable = await svc.ReconcileOnStartupAsync();

        resumable.Should().ContainSingle();
        resumable[0].ReleaseId.Should().Be(release.Id);
        resumable[0].StoreSymbolReference.Should().BeTrue();
        resumable[0].Source.Should().BeOfType<ReleaseImportSource.Url>()
            .Which.DownloadUrl.Should().Be("https://download.microsoft.com/x.zip");
        resumable[0].JobRowId.Should().Be(id);
        resumable[0].Resumed.Should().BeTrue("the interrupted run may have left part of the release behind (#1209)");

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.Status.Should().Be("queued");
        row.StartedAt.Should().BeNull();
    }

    [Fact]
    public async Task Reconciler_returns_surviving_jobs_oldest_first()
    {
        // Waiting push builds must resume in the order they were pushed (#1107).
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var release = await SeedReleaseAsync(ctx);
            ids.Add(await svc.CreateAsync(release.Id, Identity,
                new ReleaseImportSource.Url($"https://download.microsoft.com/{i}.zip"), storeSymbolReference: false));
        }
        // A running row is touched last, which must not move it in the order.
        await svc.MarkRunningAsync(ids[0]);

        var resumable = await svc.ReconcileOnStartupAsync();

        resumable.Select(j => j.JobRowId).Should().Equal(ids);
    }

    [Fact]
    public async Task Reconciler_fails_staged_zip_jobs_and_their_releases()
    {
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        var release = await SeedReleaseAsync(ctx);
        var id = await svc.CreateAsync(release.Id, Identity,
            new ReleaseImportSource.StagedZip("/tmp/lost-upload.zip", IsDvd: false),
            storeSymbolReference: false);

        var resumable = await svc.ReconcileOnStartupAsync();

        resumable.Should().BeEmpty();
        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == id);
        row.Status.Should().Be("failed");
        row.ErrorMessage.Should().Contain("uploaded ZIP was lost");
        var releaseRow = await read.OeReleases.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(r => r.Id == release.Id);
        releaseRow.Status.Should().Be("failed");
        releaseRow.StatusMessage.Should().Contain("uploaded ZIP was lost");
    }

    [Fact]
    public async Task SnapshotAsync_surfaces_release_label_and_failure_message()
    {
        long jobId;
        int releaseId;
        string label;
        await using (var ctx = _db.NewContext())
        {
            var svc = NewService(ctx);
            var release = await SeedReleaseAsync(ctx);
            releaseId = release.Id;
            label = release.Label;
            jobId = await svc.CreateAsync(release.Id, Identity,
                new ReleaseImportSource.CalTxt("/tmp/x.txt", "850"), storeSymbolReference: false);
            await svc.MarkFailedAsync(jobId, "Exception while reading from stream\n  ---> Timeout during reading attempt");
        }

        await using var read = _db.NewContext();
        var snapshot = await NewService(read).SnapshotAsync();
        var row = snapshot.Recent.Single(r => r.Id == jobId);
        row.ReleaseId.Should().Be(releaseId);
        row.ReleaseLabel.Should().Be(label);          // joined from oe_releases
        row.Status.Should().Be("failed");
        row.ErrorMessage.Should().Contain("Timeout during reading attempt");
    }
}
