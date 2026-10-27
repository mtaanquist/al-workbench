using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Coverage for the content-addressed source-file store
/// (<c>oe_file_contents</c>): identical <c>.al</c> source is stored once and
/// shared within a tenant, across releases, and across tenants, while staying
/// invisible to each org. Also exercises the hard-purge orphan-reclaim path.
/// Drives the real services against the shared <see cref="TestDb"/> Postgres
/// fixture.
/// </summary>
public sealed class FileContentDeduplicationTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObjectExplorer");

    private ReleaseImportService NewService(Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx),
            new TranslationImportService(ctx, _db.OrgContext, new ALDevToolbox.Services.Translation.TranslationMemoryService(ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance), _db.NewQuotaGuard(ctx), NullLogger<TranslationImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext)),
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext));

    private async Task<int> ImportDkCoreAsync(int orgId, string label)
    {
        _db.OrgContext.CurrentOrganizationId = orgId;
        await using var ctx = _db.NewContext();
        var svc = NewService(ctx);
        await using var appStream = File.OpenRead(Path.Combine(FixtureRoot, "Microsoft_DK_Core.app"));
        var summary = await svc.ImportReleaseAsync(new ReleaseImportRequest(
            Label: label, Kind: "first_party",
            ParentReleaseId: null, ApplicationVersionId: null,
            Uploads: new[] { new AppFileUpload("dk.app", appStream, SourceZipStream: null) }));
        return summary.ReleaseId;
    }

    private async Task<int> CountContentRowsAsync()
    {
        await using var read = _db.NewContext();
        return await read.OeFileContents.AsNoTracking().CountAsync();
    }

    // ── Deduplication ───────────────────────────────────────────────────

    [Fact]
    public async Task Importing_identical_source_into_two_orgs_stores_each_blob_once()
    {
        await ImportDkCoreAsync(TestDb.DefaultOrgId, "BC DK org1");
        var afterOrg1 = await CountContentRowsAsync();
        afterOrg1.Should().BeGreaterThan(0);

        await ImportDkCoreAsync(TestDb.OtherOrgId, "BC DK org2");
        var afterOrg2 = await CountContentRowsAsync();

        afterOrg2.Should().Be(afterOrg1,
            "org2 imported byte-identical source — every blob already exists, so no new content rows");

        // Both orgs still carry their own file rows pointing at the shared blobs.
        await using var read = _db.NewContext();
        var org1Files = await read.OeModuleFiles.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(f => f.OrganizationId == TestDb.DefaultOrgId);
        var org2Files = await read.OeModuleFiles.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(f => f.OrganizationId == TestDb.OtherOrgId);
        org1Files.Should().Be(org2Files).And.BeGreaterThan(0);
        afterOrg2.Should().BeLessThanOrEqualTo(org1Files,
            "content rows are the distinct-hash count, never the per-org file-row count");
    }

    [Fact]
    public async Task Importing_identical_source_into_two_releases_in_one_tenant_shares_blobs()
    {
        await ImportDkCoreAsync(TestDb.DefaultOrgId, "release A");
        var afterFirst = await CountContentRowsAsync();

        await ImportDkCoreAsync(TestDb.DefaultOrgId, "release B");
        var afterSecond = await CountContentRowsAsync();

        afterSecond.Should().Be(afterFirst,
            "the second release in the same org reuses the existing blobs");
    }

    [Fact]
    public async Task Source_content_length_counts_every_file_row_including_duplicates()
    {
        var releaseId = await ImportDkCoreAsync(TestDb.DefaultOrgId, "lengths");

        await using var read = _db.NewContext();
        var release = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId);

        // The denormalised per-org logical size sums each file row's content
        // length via the shared store — unchanged by dedup.
        var groundTruth = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == releaseId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Length = g.Sum(f => (long)f.FileContent!.ContentLength) })
            .SingleAsync();

        release.SourceFileCount.Should().Be(groundTruth.Count);
        release.SourceContentLength.Should().Be(groundTruth.Length);
        release.SourceContentLength.Should().BeGreaterThan(0);
    }

    // ── Tenant isolation ────────────────────────────────────────────────

    [Fact]
    public async Task Content_search_cannot_reach_another_orgs_release_despite_shared_blobs()
    {
        var org1Release = await ImportDkCoreAsync(TestDb.DefaultOrgId, "org1 search");
        await ImportDkCoreAsync(TestDb.OtherOrgId, "org2 search");

        // As org2, searching org2's own release works…
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await using var read = _db.NewContext();
        var search = new ObjectSearchService(read, new ProjectAccess(read, _db.OrgContext));

        var org2ReleaseId = await read.OeReleases.AsNoTracking()
            .Where(r => r.OrganizationId == TestDb.OtherOrgId)
            .Select(r => r.Id).FirstAsync();
        var ownHits = await search.SearchContentInReleaseAsync(org2ReleaseId, "codeunit", null);
        ownHits.Should().NotBeEmpty();

        // …but org2 cannot search org1's release, even though the underlying
        // source blobs are physically shared. The content query runs as raw SQL
        // (to pin its plan), fenced by a ReleaseVisibleAsync check against the
        // org-filtered OeReleases — org1's release isn't visible to org2.
        var crossOrgHits = await search.SearchContentInReleaseAsync(org1Release, "codeunit", null);
        crossOrgHits.Should().BeEmpty("the release belongs to another org and is filtered out");
    }

    // ── Garbage collection on hard-purge ────────────────────────────────

    [Fact]
    public async Task Hard_deleting_a_release_reclaims_its_unreferenced_blobs()
    {
        var releaseId = await ImportDkCoreAsync(TestDb.DefaultOrgId, "to purge");
        (await CountContentRowsAsync()).Should().BeGreaterThan(0);

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            var mgmt = new ReleaseManagementService(ctx, _db.OrgContext, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseManagementService>.Instance);
            await mgmt.HardDeleteAsync(releaseId, "to purge");
        }

        (await CountContentRowsAsync()).Should().Be(0,
            "no file row references these blobs any more, so the purge reclaims them all");
    }

    [Fact]
    public async Task Hard_deleting_one_release_keeps_blobs_still_referenced_by_another()
    {
        var keep = await ImportDkCoreAsync(TestDb.DefaultOrgId, "keep");
        var purge = await ImportDkCoreAsync(TestDb.DefaultOrgId, "purge");
        var before = await CountContentRowsAsync();
        before.Should().BeGreaterThan(0);

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        await using (var ctx = _db.NewContext())
        {
            var mgmt = new ReleaseManagementService(ctx, _db.OrgContext, new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseManagementService>.Instance);
            await mgmt.HardDeleteAsync(purge, "purge");
        }

        (await CountContentRowsAsync()).Should().Be(before,
            "every blob is still referenced by the surviving release");

        // The surviving release's files still resolve their content.
        await using var read = _db.NewContext();
        var content = await read.OeModuleFiles.AsNoTracking()
            .Where(f => f.Module!.ReleaseId == keep)
            .Select(f => f.FileContent!.Content)
            .FirstAsync();
        content.Should().NotBeNullOrEmpty();
    }
}
