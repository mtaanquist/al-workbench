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
        new(OrganizationId: 1, UserId: null, IsSiteAdmin: false, IsSystemOrganization: true);

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
