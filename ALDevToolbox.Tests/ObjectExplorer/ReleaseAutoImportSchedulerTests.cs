using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Target-selection for the daily release auto-import sweep
/// (<see cref="ReleaseAutoImportScheduler.ResolveTargetsAsync"/>). Pins the
/// single-tenant fix from issue #518: the system org must be swept when it's
/// the one working org (single-tenant), but skipped otherwise.
/// </summary>
public sealed class ReleaseAutoImportSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task System_org_is_only_a_target_when_single_tenant_mode_includes_it()
    {
        // Org 1 is the migration-stamped system org; enable auto-import on it.
        await EnableAutoImportAsync(TestDb.DefaultOrgId, "dk");

        await using var ctx = _db.NewContext();

        // Multi-tenant (includeSystemOrg: false): the system org is skipped —
        // this is the bug that left single-tenant installs never importing.
        var multiTenant = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
            ctx, includeSystemOrg: false, default);
        multiTenant.Should().NotContain(t => t.OrganizationId == TestDb.DefaultOrgId,
            "the system org is only the template source in a multi-tenant deployment");

        // Single-tenant (includeSystemOrg: true): the system org is the one
        // working org, so it must be swept.
        var singleTenant = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
            ctx, includeSystemOrg: true, default);
        var target = singleTenant.Should().ContainSingle(t => t.OrganizationId == TestDb.DefaultOrgId).Which;
        target.Countries.Should().Be("dk");
        // Issue #694: the sweep stamps this on the ambient identity, so it must be the
        // org's real flag — otherwise a scheduled import is quota-checked where the
        // interactive one is exempt.
        target.IsSystem.Should().BeTrue();
    }

    [Fact]
    public async Task Regular_org_is_a_target_regardless_of_single_tenant_flag()
    {
        // Org 2 is a regular (non-system) org — always eligible when opted in.
        await EnableAutoImportAsync(TestDb.OtherOrgId, "w1,dk");

        await using var ctx = _db.NewContext();

        foreach (var includeSystemOrg in new[] { false, true })
        {
            var targets = await ReleaseAutoImportScheduler.ResolveTargetsAsync(
                ctx, includeSystemOrg, default);
            targets.Should().ContainSingle(t => t.OrganizationId == TestDb.OtherOrgId)
                .Which.Countries.Should().Be("w1,dk");
        }
    }

    [Fact]
    public async Task Preview_opt_in_travels_with_the_target()
    {
        await EnableAutoImportAsync(TestDb.OtherOrgId, "dk", includePreviews: true);

        await using var ctx = _db.NewContext();
        var targets = await ReleaseAutoImportScheduler.ResolveTargetsAsync(ctx, includeSystemOrg: false, default);

        targets.Should().ContainSingle(t => t.OrganizationId == TestDb.OtherOrgId)
            .Which.IncludePreviews.Should().BeTrue("the sweep decides per org whether to hit the insider channel");
    }

    private async Task EnableAutoImportAsync(int organizationId, string country, bool includePreviews = false)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
            AutoImportReleasesEnabled = true,
            AutoImportCountry = country,
            AutoImportPreviewsEnabled = includePreviews,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
