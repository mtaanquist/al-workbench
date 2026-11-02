using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// #1578 OEI2-4: a restore stops a running system-reference backfill rather than wait
/// for a bulk run that keeps the import worker busy for hours. The backfill is safe to
/// run again, so its row goes back to queued and the job back on the queue.
/// </summary>
public sealed class BackfillStoppedForRestoreTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    [Fact]
    public void Entering_maintenance_cancels_the_token_taken_before_it_and_not_the_next_one()
    {
        var maintenance = new MaintenanceModeState();
        var before = maintenance.EnteringToken;

        maintenance.Enter("Restoring backup");
        var after = maintenance.EnteringToken;

        before.IsCancellationRequested.Should().BeTrue();
        after.IsCancellationRequested.Should().BeFalse("a job that starts after the restore runs normally");
    }

    [Fact]
    public async Task A_restore_stops_a_running_backfill_and_puts_it_back_on_the_queue()
    {
        int releaseId;
        long rowId;
        await using (var ctx = _db.NewContext())
        {
            var now = DateTime.UtcNow;
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "BC 25.18 DK (restore)", Kind = "first_party",
                Status = "ready", ImportedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            ctx.OeReleases.Add(release);
            await ctx.SaveChangesAsync();
            releaseId = release.Id;
            rowId = await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(releaseId, Identity, new ReleaseImportSource.Backfill(), storeSymbolReference: false);
        }
        var maintenance = new MaintenanceModeState();
        var queue = new ReleaseImportQueue(maintenance);
        var ingests = new ReleaseIngests();
        var worker = new TestWorker(this, queue, maintenance, ingests);
        var job = new ReleaseImportJob(releaseId, Identity, new ReleaseImportSource.Backfill(), JobRowId: rowId);

        // A DVD import holds the heavy gate, so the backfill is running but waiting on it
        // when the restore begins.
        Task run;
        using (await ingests.EnterHeavyAsync(CancellationToken.None))
        {
            run = worker.RunAsync(job);
            await WaitUntilAsync(async () => await RowStatusAsync(rowId) == "running");
            maintenance.Enter("Restoring backup");
            await run.WaitAsync(TimeSpan.FromMinutes(1));
        }

        await using var read = _db.NewContext();
        var row = await read.OeImportJobs.AsNoTracking().SingleAsync(j => j.Id == rowId);
        row.Status.Should().Be("queued", "the backfill is safe to run again, so a restore must not fail it");
        row.StartedAt.Should().BeNull();
        var requeued = await queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromMinutes(1));
        requeued.JobRowId.Should().Be(rowId);
    }

    private async Task<string?> RowStatusAsync(long rowId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeImportJobs.AsNoTracking().Where(j => j.Id == rowId).Select(j => j.Status).SingleAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var giveUp = DateTime.UtcNow.AddMinutes(1);
        while (!await condition())
        {
            if (DateTime.UtcNow > giveUp) throw new TimeoutException("The condition never held.");
            await Task.Delay(20);
        }
    }

    private ReleaseImportService NewImporter(Data.AppDbContext ctx, ReleaseIngests ingests) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext),
            ingests: ingests);

    /// <summary>Runs one job through the real worker code, with the services it resolves built by hand.</summary>
    private sealed class TestWorker(BackfillStoppedForRestoreTests test, ReleaseImportQueue queue, MaintenanceModeState maintenance, ReleaseIngests ingests)
        : ReleaseImportWorker(queue, BuildServices(test, ingests), NullLogger<ReleaseImportWorker>.Instance, new WorkerHeartbeatRegistry(), maintenance)
    {
        public Task RunAsync(ReleaseImportJob job) => RunJobAsync(job, CancellationToken.None);

        private static ServiceProvider BuildServices(BackfillStoppedForRestoreTests test, ReleaseIngests ingests)
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => test._db.NewContext());
            services.AddScoped(sp => test.NewImporter(sp.GetRequiredService<Data.AppDbContext>(), ingests));
            services.AddScoped(sp => new PersistedImportJobs(sp.GetRequiredService<Data.AppDbContext>(), TimeProvider.System));
            return services.BuildServiceProvider();
        }
    }
}
