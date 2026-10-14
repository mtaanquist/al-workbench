using System.IO.Compression;
using System.Threading.Channels;
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
/// An import the startup reconciler resumes after a restart (#1209). The interrupted
/// run saved part of the release: a module row with its hash but only some of its
/// objects, and the release's references. Run again as-is, the importer skipped that
/// module as an identical re-upload and stored every reference a second time. The
/// resumed job now starts from a clean release, so it ends exactly where an
/// uninterrupted import would.
/// </summary>
public sealed class ResumedReleaseImportTests : IDisposable
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

    private static readonly string[] Apps = ["Microsoft_DK_Core.app", "Microsoft_OIOUBL.app"];

    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: true);

    private ReleaseImportService NewImporter(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);

    private sealed record Counts(int Modules, int Objects, int References, int SystemReferences);

    private async Task<Counts> CountAsync(int releaseId)
    {
        await using var ctx = _db.NewContext();
        return new Counts(
            await ctx.OeModules.CountAsync(m => m.ReleaseId == releaseId),
            await ctx.OeModuleObjects.CountAsync(o => o.Module!.ReleaseId == releaseId),
            await ctx.OeModuleReferences.CountAsync(r => r.Module!.ReleaseId == releaseId),
            await ctx.OeModuleSystemReferences.CountAsync(r => r.Module!.ReleaseId == releaseId));
    }

    [Fact]
    public async Task A_resumed_import_replaces_what_the_interrupted_run_left_behind()
    {
        // The interrupted run: the whole release went in, references included...
        int releaseId;
        await using (var ctx = _db.NewContext())
        {
            var importer = NewImporter(ctx);
            releaseId = await importer.BeginReleaseAsync(new ReleaseImportMetadata(
                Label: "BC 25.18 DK (resumed)", Kind: "first_party", ParentReleaseId: null, ApplicationVersionId: null));
            var streams = Apps.Select(a => File.OpenRead(Path.Combine(FixtureRoot, a))).ToList();
            try
            {
                await importer.ProcessReleaseAsync(releaseId,
                    Apps.Select((a, i) => new AppFileUpload(a, streams[i], SourceZipStream: null)).ToList());
            }
            finally
            {
                foreach (var s in streams) await s.DisposeAsync();
            }
        }
        var complete = await CountAsync(releaseId);
        complete.References.Should().BePositive("the duplicate-reference half of the bug needs references to duplicate");

        // ...but the restart cut the largest module off after its first chunk of objects,
        // and the release never reached ready.
        await using (var ctx = _db.NewContext())
        {
            var largest = await ctx.OeModules
                .Where(m => m.ReleaseId == releaseId)
                .OrderByDescending(m => m.Objects.Count)
                .Select(m => m.Id)
                .FirstAsync();
            var lost = await ctx.OeModuleObjects
                .Where(o => o.ModuleId == largest)
                .OrderBy(o => o.Id)
                .Skip(1)
                .Select(o => o.Id)
                .ToListAsync();
            lost.Should().NotBeEmpty();
            await ctx.OeModuleObjects.Where(o => lost.Contains(o.Id)).ExecuteDeleteAsync();
            await ctx.OeReleases.Where(r => r.Id == releaseId)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "ingesting"));
        }
        var interrupted = await CountAsync(releaseId);
        interrupted.Objects.Should().BeLessThan(complete.Objects);

        // The restart: the job row was running, so the reconciler hands it back.
        long jobRowId;
        ReleaseImportJob resumed;
        await using (var ctx = _db.NewContext())
        {
            var jobs = new PersistedImportJobs(ctx, TimeProvider.System);
            jobRowId = await jobs.CreateAsync(releaseId, Identity,
                new ReleaseImportSource.Url("https://download.microsoft.com/dk.zip"), storeSymbolReference: false);
            await jobs.MarkRunningAsync(jobRowId);
            resumed = (await jobs.ReconcileOnStartupAsync()).Should().ContainSingle().Subject;
        }
        resumed.Resumed.Should().BeTrue();

        // The test can't download, so the same apps come from a staged ZIP instead;
        // the worker treats every resumed job the same whatever its source.
        await new TestWorker(this).RunAsync(resumed with { Source = new ReleaseImportSource.StagedZip(StageZip(), IsDvd: false) });

        (await CountAsync(releaseId)).Should().Be(complete,
            "the resumed run re-imports the cut-off module in full and emits each reference once");
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId)).Status.Should().Be("ready");
        (await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == jobRowId)).Status.Should().Be("completed");
    }

    private string StageZip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aldt-resume-test-{Guid.NewGuid():N}.zip");
        _tempFiles.Add(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var app in Apps)
            zip.CreateEntryFromFile(Path.Combine(FixtureRoot, app), app);
        return path;
    }

    /// <summary>Runs one job through the real worker code, with the services it resolves built by hand.</summary>
    private sealed class TestWorker : ReleaseImportWorker
    {
        public TestWorker(ResumedReleaseImportTests test)
            : base(Channel.CreateUnbounded<ReleaseImportJob>().Reader, BuildServices(test),
                NullLogger<ReleaseImportWorker>.Instance, new WorkerHeartbeatRegistry(), nameof(TestWorker))
        {
        }

        public Task RunAsync(ReleaseImportJob job) => RunJobAsync(job, CancellationToken.None);

        private static ServiceProvider BuildServices(ResumedReleaseImportTests test)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => test._db.NewContext());
            services.AddScoped(sp => test.NewImporter(sp.GetRequiredService<Data.AppDbContext>()));
            services.AddScoped(sp => new PersistedImportJobs(sp.GetRequiredService<Data.AppDbContext>(), TimeProvider.System));
            services.AddScoped(sp => new ReleaseManagementService(
                sp.GetRequiredService<Data.AppDbContext>(), test._db.OrgContext, NullLogger<ReleaseManagementService>.Instance));
            return services.BuildServiceProvider();
        }
    }
}
