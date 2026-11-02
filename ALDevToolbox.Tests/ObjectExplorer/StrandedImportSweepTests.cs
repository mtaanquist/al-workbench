using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="StartupTasks.WillResumeAnImport"/>: which job rows keep the startup
/// sweep from failing a release left "ingesting". A queued backfill is not one of
/// them (#1616); a queued import is.
/// </summary>
public sealed class StrandedImportSweepTests : IDisposable
{
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(TestDb.DefaultOrgId, null, false, false);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_queued_backfill_does_not_count_as_resuming_its_release()
    {
        var id = await SeedIngestingReleaseAsync("CRONUS 26.0 (DK)");
        await using (var ctx = _db.NewContext())
        {
            await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(id, Identity, new ReleaseImportSource.Backfill(), storeSymbolReference: false);
        }

        (await ResumingReleaseIdsAsync()).Should().NotContain(id, "the sweep must fail a release only a backfill is waiting on");
    }

    [Fact]
    public async Task A_queued_url_import_still_counts_as_resuming_its_release()
    {
        var id = await SeedIngestingReleaseAsync("CRONUS 26.1 (DK)");
        await using (var ctx = _db.NewContext())
        {
            await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(id, Identity, new ReleaseImportSource.Url("https://download.example/dvd.zip"), storeSymbolReference: false);
        }

        (await ResumingReleaseIdsAsync()).Should().Contain(id, "the resumed job will import it");
    }

    private async Task<List<int>> ResumingReleaseIdsAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeImportJobs.AsNoTracking()
            .Where(StartupTasks.WillResumeAnImport)
            .Select(j => j.ReleaseId)
            .ToListAsync();
    }

    private async Task<int> SeedIngestingReleaseAsync(string label)
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = label, Kind = "first_party", Status = "ingesting",
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }
}
