using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="StartupTasks.FailStrandedImportsAsync"/>: a release left "ingesting"
/// fails at startup unless a queued job will resume its import. A queued backfill
/// is not such a job (#1616).
/// </summary>
public sealed class StrandedImportSweepTests : IDisposable
{
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(TestDb.DefaultOrgId, null, false, false);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_queued_backfill_does_not_keep_an_ingesting_release_from_failing()
    {
        var id = await SeedIngestingReleaseAsync("CRONUS 26.0 (DK)");
        await using (var ctx = _db.NewContext())
        {
            await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(id, Identity, new ReleaseImportSource.Backfill(), storeSymbolReference: false);
        }

        await SweepAsync();

        (await StatusAsync(id)).Should().Be("failed");
    }

    [Fact]
    public async Task A_queued_url_import_still_keeps_its_release_importing()
    {
        var id = await SeedIngestingReleaseAsync("CRONUS 26.1 (DK)");
        await using (var ctx = _db.NewContext())
        {
            await new PersistedImportJobs(ctx, TimeProvider.System)
                .CreateAsync(id, Identity, new ReleaseImportSource.Url("https://download.example/dvd.zip"), storeSymbolReference: false);
        }

        await SweepAsync();

        (await StatusAsync(id)).Should().Be("ingesting", "the resumed job will import it");
    }

    private async Task SweepAsync()
    {
        await using var ctx = _db.NewContext();
        await StartupTasks.FailStrandedImportsAsync(ctx, NullLogger.Instance, CancellationToken.None);
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

    private async Task<string> StatusAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeReleases.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleAsync();
    }
}
