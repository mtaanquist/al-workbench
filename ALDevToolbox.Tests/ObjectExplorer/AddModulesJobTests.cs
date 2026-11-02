using System.IO.Compression;
using System.Threading.Channels;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Add modules runs on the import worker (#1549, #1602): the request flips the release
/// to importing and queues a job, the worker runs the amend inside the one-heavy-import
/// gate and hands the release back ready, and a restart that loses the upload hands it
/// back ready too rather than failing a release that held good data.
/// </summary>
public sealed class AddModulesJobTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
        _db.Dispose();
    }

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private ReleaseImportService NewImporter(Data.AppDbContext ctx, bool blobsOnOwnConnection = true) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext),
            blobContexts: blobsOnOwnConnection ? _db.NewContextFactory() : null);

    private async Task<int> SeedReadyReleaseAsync(string label)
    {
        await using var ctx = _db.NewContext();
        var importer = NewImporter(ctx);
        var releaseId = await importer.BeginReleaseAsync(new ReleaseImportMetadata(
            Label: label, Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null));
        await using var app = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        await importer.ProcessReleaseAsync(releaseId, [new AppFileUpload("Microsoft_DK_Core.app", app, null)]);
        return releaseId;
    }

    /// <summary>What the request leaves: the release importing and a queued job row.</summary>
    private async Task<ReleaseImportJob> QueueAsync(int releaseId, params string[] apps)
    {
        await using var ctx = _db.NewContext();
        await NewImporter(ctx).BeginAmendAsync(releaseId);
        var source = new ReleaseImportSource.AddModules(StageZip(apps));
        var rowId = await new PersistedImportJobs(ctx, TimeProvider.System).CreateAsync(releaseId, Identity, source, storeSymbolReference: false);
        return new ReleaseImportJob(releaseId, Identity, source, JobRowId: rowId);
    }

    private async Task<(string Status, int Modules, string? JobStatus, string? JobError)> StateAsync(int releaseId, long jobRowId)
    {
        await using var ctx = _db.NewContext();
        var status = await ctx.OeReleases.AsNoTracking().Where(r => r.Id == releaseId).Select(r => r.Status).SingleAsync();
        var modules = await ctx.OeModules.CountAsync(m => m.ReleaseId == releaseId);
        var job = await ctx.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == jobRowId);
        return (status, modules, job.Status, job.ErrorMessage);
    }

    [Fact]
    public async Task The_worker_adds_the_queued_modules_and_hands_the_release_back_ready()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add modules)");
        var job = await QueueAsync(releaseId, "Microsoft_OIOUBL.app");
        (await StateAsync(releaseId, job.JobRowId)).Status.Should().Be("ingesting", "the release shows as importing while the job waits");

        await new TestWorker(this).RunAsync(job);

        var state = await StateAsync(releaseId, job.JobRowId);
        state.Status.Should().Be("ready");
        state.Modules.Should().Be(2);
        state.JobStatus.Should().Be("completed");
        File.Exists(((ReleaseImportSource.AddModules)job.Source).TempPath).Should().BeFalse("the worker deletes the staged upload");
    }

    [Fact]
    public async Task A_refused_upload_fails_the_job_with_the_reason_and_leaves_the_release_as_it_was()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add refused)");
        await using (var tamper = _db.NewContext())
        {
            var module = await tamper.OeModules.SingleAsync(m => m.ReleaseId == releaseId);
            module.AppFileHash = "not-the-same-bytes";
            await tamper.SaveChangesAsync();
        }
        var job = await QueueAsync(releaseId, "Microsoft_OIOUBL.app", "Microsoft_DK_Core.app");

        await new TestWorker(this).RunAsync(job);

        var state = await StateAsync(releaseId, job.JobRowId);
        state.Status.Should().Be("ready");
        state.Modules.Should().Be(1, "the upload that went in before the refusal is rolled back with it");
        state.JobStatus.Should().Be("failed");
        state.JobError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task An_upload_with_no_apps_fails_the_job_and_hands_the_release_back_ready()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add nothing)");
        var job = await QueueAsync(releaseId);

        await new TestWorker(this).RunAsync(job);

        var state = await StateAsync(releaseId, job.JobRowId);
        state.Status.Should().Be("ready");
        state.Modules.Should().Be(1);
        state.JobStatus.Should().Be("failed");
        state.JobError.Should().Contain("No .app files");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_amends_source_blobs_commit_outside_its_transaction(bool blobsOnOwnConnection)
    {
        // OEI-6: the blobs the first upload inserted commit at once on their own
        // connection, so a rolled-back amend leaves them behind (unreferenced, for the
        // reclaim) and never held them locked against another import. Without the
        // second connection they roll back with the amend, as before.
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add blobs " + blobsOnOwnConnection + ")");
        await using (var tamper = _db.NewContext())
        {
            var module = await tamper.OeModules.SingleAsync(m => m.ReleaseId == releaseId);
            module.AppFileHash = "not-the-same-bytes";
            await tamper.SaveChangesAsync();
        }
        int before;
        await using (var read = _db.NewContext()) before = await read.OeFileContents.CountAsync();

        await using (var ctx = _db.NewContext())
        {
            await using var oioubl = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_OIOUBL.app"));
            await using var clash = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
            var act = () => NewImporter(ctx, blobsOnOwnConnection).AmendReleaseAsync(releaseId,
                [new AppFileUpload("Microsoft_OIOUBL.app", oioubl, null), new AppFileUpload("Microsoft_DK_Core.app", clash, null)]);
            await act.Should().ThrowAsync<PlanValidationException>();
        }

        await using var after = _db.NewContext();
        (await after.OeModules.CountAsync(m => m.ReleaseId == releaseId)).Should().Be(1);
        var blobs = await after.OeFileContents.CountAsync();
        if (blobsOnOwnConnection)
            blobs.Should().BeGreaterThan(before, "the first upload's blobs were committed on their own connection");
        else
            blobs.Should().Be(before);
    }

    [Fact]
    public async Task A_restart_that_loses_the_upload_hands_the_release_back_ready_and_does_not_resume_it()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add restart)");
        var job = await QueueAsync(releaseId, "Microsoft_OIOUBL.app");
        await using (var ctx = _db.NewContext())
        {
            await new PersistedImportJobs(ctx, TimeProvider.System).MarkRunningAsync(job.JobRowId);
        }

        IReadOnlyList<ReleaseImportJob> resumed;
        await using (var ctx = _db.NewContext())
        {
            resumed = await new PersistedImportJobs(ctx, TimeProvider.System).ReconcileOnStartupAsync();
        }

        resumed.Should().BeEmpty("the staged upload is gone with the temp folder");
        var state = await StateAsync(releaseId, job.JobRowId);
        state.Status.Should().Be("ready", "nothing of the amend landed, so the release keeps what it held");
        state.Modules.Should().Be(1);
        state.JobStatus.Should().Be("failed");
        state.JobError.Should().Be(PersistedImportJobs.AddModulesLostMessage);
    }

    [Fact]
    public async Task The_newest_upload_is_what_the_page_reads_and_never_a_retry_origin()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add origin)");
        await using var ctx = _db.NewContext();
        var jobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var urlRow = await jobs.CreateAsync(releaseId, Identity,
            new ReleaseImportSource.Url("https://download.microsoft.com/dk.zip"), storeSymbolReference: false);
        await jobs.MarkCompletedAsync(urlRow);
        var addRow = await jobs.CreateAsync(releaseId, Identity,
            new ReleaseImportSource.AddModules("/tmp/oe-amend-gone.zip"), storeSymbolReference: false);
        await jobs.MarkFailedAsync(addRow, "Microsoft_DK_Core.app clashes.");

        (await jobs.GetLatestForReleaseAsync(releaseId))!.Kind.Should().Be("url",
            "Add modules added to a release that was already there; Retry re-runs how it got its content");
        var last = (await jobs.GetLatestAddModulesAsync(releaseId))!;
        last.Status.Should().Be("failed");
        last.ErrorMessage.Should().Be("Microsoft_DK_Core.app clashes.");
    }

    [Fact]
    public async Task A_release_that_is_already_importing_cannot_take_a_second_amend()
    {
        var releaseId = await SeedReadyReleaseAsync("BC 25.18 DK (add twice)");
        await using var ctx = _db.NewContext();
        await NewImporter(ctx).BeginAmendAsync(releaseId);

        var act = () => NewImporter(ctx).BeginAmendAsync(releaseId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ReleaseId").WhoseValue.Should().Contain("ingesting");
    }

    private string StageZip(params string[] apps)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{ReleaseImportRequestService.AddModulesTempPrefix}test-{Guid.NewGuid():N}.zip");
        _tempFiles.Add(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < apps.Length; i++)
            zip.CreateEntryFromFile(Path.Combine(FixtureRoot, apps[i]), $"{i:D3}/{apps[i]}");
        return path;
    }

    /// <summary>Runs one job through the real worker code, with the services it resolves built by hand.</summary>
    private sealed class TestWorker : ReleaseImportWorker
    {
        public TestWorker(AddModulesJobTests test)
            : base(Channel.CreateUnbounded<ReleaseImportJob>().Reader, BuildServices(test),
                NullLogger<ReleaseImportWorker>.Instance, new WorkerHeartbeatRegistry(), nameof(TestWorker), maintenance: null)
        {
        }

        public Task RunAsync(ReleaseImportJob job) => RunJobAsync(job, CancellationToken.None);

        private static ServiceProvider BuildServices(AddModulesJobTests test)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => test._db.NewContext());
            services.AddScoped(sp => test.NewImporter(sp.GetRequiredService<Data.AppDbContext>()));
            services.AddScoped(sp => new PersistedImportJobs(sp.GetRequiredService<Data.AppDbContext>(), TimeProvider.System));
            return services.BuildServiceProvider();
        }
    }
}
