using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Storage;

/// <summary>
/// Covers the persisted-snapshot path on <c>DatabaseUsageService</c> that
/// backs the sidebar <c>StorageBar</c> and the SiteAdmin storage page.
/// Computing usage live ran a <c>COUNT(*)</c> over every tenanted table on
/// each navigation; <c>UsageSnapshotScheduler</c> now recomputes on a timer
/// and the display surfaces read the cached row. These tests pin the
/// recompute/read contract — that recompute writes one row per org, that the
/// per-org read returns null until the first recompute, and that the
/// cross-org list LEFT JOINs so an org with no snapshot still appears.
///
/// Fresh database per test (not a shared fixture) so the "before recompute"
/// assertions see a clean snapshot table.
/// </summary>
public sealed class DatabaseUsageSnapshotTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task RecomputeSnapshotsAsync_writes_one_row_per_organisation()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        var snapshots = await read.OrganizationUsageSnapshots.AsNoTracking().ToListAsync();
        snapshots.Select(s => s.OrganizationId)
            .Should().BeEquivalentTo(new[] { TestDb.SystemOrgId, TestDb.OtherOrgId, TestDb.DefaultOrgId });
        snapshots.Should().OnlyContain(s => s.ComputedAt != default);
    }

    [Fact]
    public async Task RecomputeSnapshotsAsync_is_idempotent_and_updates_in_place()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);
        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        var count = await read.OrganizationUsageSnapshots.CountAsync();
        // UPSERT, not insert: a second pass refreshes the same rows, one per org.
        count.Should().Be(3);
    }

    [Fact]
    public async Task GetSnapshotForCurrentOrgAsync_returns_null_before_first_recompute()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        var row = await usage.GetSnapshotForCurrentOrgAsync(CancellationToken.None);

        row.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotForCurrentOrgAsync_reads_back_the_acting_org_after_recompute()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);
        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        var row = await usage.GetSnapshotForCurrentOrgAsync(CancellationToken.None);

        row.Should().NotBeNull();
        row!.OrganizationId.Should().Be(TestDb.DefaultOrgId);
        row.ComputedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ListFromSnapshotsAsync_includes_every_org_even_without_a_snapshot()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);
        _db.OrgContext.IsSiteAdmin = true;

        // No recompute yet: the LEFT JOIN still surfaces every org, with a
        // null ComputedAt (so the page can say "not computed yet").
        var before = await usage.ListFromSnapshotsAsync(CancellationToken.None);
        before.Select(r => r.OrganizationId)
            .Should().BeEquivalentTo(new[] { TestDb.SystemOrgId, TestDb.OtherOrgId, TestDb.DefaultOrgId });
        before.Should().OnlyContain(r => r.ComputedAt == null);

        await usage.RecomputeSnapshotsAsync(CancellationToken.None);

        var after = await usage.ListFromSnapshotsAsync(CancellationToken.None);
        after.Should().OnlyContain(r => r.ComputedAt != null);
    }

    /// <summary>
    /// The two cross-organisation members run raw SQL or write any
    /// organisation's row by id, so they check for SiteAdmin themselves
    /// rather than trusting the page and endpoint roles alone (#1426).
    /// </summary>
    [Fact]
    public async Task The_cross_org_listing_and_quota_override_refuse_a_non_site_admin()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);
        _db.OrgContext.IsSiteAdmin = false;

        var list = () => usage.ListFromSnapshotsAsync(CancellationToken.None);
        var quota = () => usage.SetOrgQuotaAsync(TestDb.OtherOrgId, 100, CancellationToken.None);

        await list.Should().ThrowAsync<InvalidOperationException>().WithMessage("SiteAdmin context is required*");
        await quota.Should().ThrowAsync<InvalidOperationException>().WithMessage("SiteAdmin context is required*");
        await using var read = _db.NewContext();
        (await read.Organizations.AsNoTracking().SingleAsync(o => o.Id == TestDb.OtherOrgId))
            .StorageQuotaMb.Should().BeNull();
    }

    [Fact]
    public async Task A_site_admin_sets_another_organisations_quota()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);
        _db.OrgContext.IsSiteAdmin = true;

        await usage.SetOrgQuotaAsync(TestDb.OtherOrgId, 100, CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.Organizations.AsNoTracking().SingleAsync(o => o.Id == TestDb.OtherOrgId))
            .StorageQuotaMb.Should().Be(100);
    }

    /// <summary>
    /// The Object Explorer fact tables are the biggest in the schema, so their
    /// per-org share is estimated from the org's share of <c>oe_modules</c>
    /// instead of counted (#684). This test makes the estimate visible: every
    /// object row belongs to the *other* org's module, yet the bytes are split
    /// 3:1 in the default org's favour because that is how the modules are
    /// split. A count-based attribution would have given the default org none
    /// of them.
    /// </summary>
    [Fact]
    public async Task Oe_fact_tables_are_attributed_by_module_share_not_by_row_count()
    {
        await using var ctx = _db.NewContext();
        var usage = _db.NewDatabaseUsageService(ctx);

        // 3 modules for the default org, 1 for the other org.
        await SeedModulesAsync(ctx, TestDb.DefaultOrgId, 3);
        var otherModuleId = (await SeedModulesAsync(ctx, TestDb.OtherOrgId, 1))[0];

        var before = await usage.ListAsync(CancellationToken.None);

        // Every object row hangs off the other org's single module.
        var insert = "INSERT INTO oe_module_objects (organization_id, module_id, kind, name, line_number) "
                     + "SELECT " + TestDb.OtherOrgId + ", " + otherModuleId
                     + ", 'codeunit', 'Object ' || g, 1 FROM generate_series(1, 5000) g";
        await ctx.Database.ExecuteSqlRawAsync(insert);
        // reltuples is the planner's estimate and stays at -1 until a table is
        // analysed, so the sizing read would otherwise see an empty table.
        await ctx.Database.ExecuteSqlRawAsync("ANALYZE oe_module_objects");

        var after = await usage.ListAsync(CancellationToken.None);

        var defaultDelta = Delta(TestDb.DefaultOrgId);
        var otherDelta = Delta(TestDb.OtherOrgId);

        otherDelta.Should().BeGreaterThan(0);
        // 3 of the 4 modules are the default org's, so it carries 3/4 of the
        // fact table even though it owns none of the rows.
        ((double)defaultDelta / otherDelta).Should().BeApproximately(3d, 0.2d);

        long Delta(int orgId) =>
            after.Single(r => r.OrganizationId == orgId).TotalBytes
            - before.Single(r => r.OrganizationId == orgId).TotalBytes;
    }

    /// <summary>Seeds <paramref name="count"/> modules (under one release) for an org; returns their ids.</summary>
    private static async Task<List<long>> SeedModulesAsync(AppDbContext ctx, int orgId, int count)
    {
        var release = new OeRelease
        {
            OrganizationId = orgId,
            Label = $"Release {orgId}",
            Kind = "cal",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var modules = new List<OeModule>();
        for (var i = 0; i < count; i++)
        {
            modules.Add(new OeModule
            {
                OrganizationId = orgId,
                ReleaseId = release.Id,
                AppId = Guid.NewGuid(),
                Name = $"Module {orgId}-{i}",
                Publisher = "CRONUS",
                Version = "1.0.0.0",
                CreatedAt = DateTime.UtcNow,
                DependencyCount = 0,
            });
        }
        ctx.OeModules.AddRange(modules);
        await ctx.SaveChangesAsync();
        return modules.Select(m => m.Id).ToList();
    }
}
