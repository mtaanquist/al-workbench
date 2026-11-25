using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Generation;

/// <summary>
/// The storage-quota guard refuses tenant writes once billable usage reaches
/// the effective quota — except in single-tenant deployments, where quotas
/// are hidden and must never block a write the operator can't see.
/// See <c>Services/SingleTenant/ISingleTenantMode.cs</c>.
/// </summary>
public sealed class StorageQuotaGuardTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A default quota of 0 MB puts every org at or over quota (billable ≥ 0),
    /// so this is the cheapest way to force the refusal path without staging
    /// real bytes.
    /// </summary>
    private async Task SetZeroQuotaAsync()
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (row is null)
        {
            row = new ALDevToolbox.Domain.Entities.SystemSettings { Id = 1 };
            ctx.SystemSettings.Add(row);
        }
        row.DefaultStorageQuotaMb = 0;
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task Refuses_write_when_over_quota()
    {
        await SetZeroQuotaAsync();
        await using var ctx = _db.NewContext();
        var guard = _db.NewQuotaGuard(ctx);

        var act = () => guard.EnsureCanWriteAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("storage");
    }

    /// <summary>
    /// Issue #694: a scheduled import runs under an ambient organisation identity rather
    /// than a request's claims. When the sweep stamps the org's real <c>IsSystem</c>, the
    /// guard exempts the system org exactly as the interactive path does — and still
    /// refuses a regular org, so the exemption is the flag and not the ambient path.
    /// </summary>
    [Fact]
    public async Task Scheduled_write_for_the_system_org_skips_the_quota_like_a_request_does()
    {
        await SetZeroQuotaAsync();
        await using var ctx = _db.NewContext();
        // No HttpContext: the context falls back to AmbientOrganizationScope, which is
        // what a background worker sees.
        var guard = _db.NewQuotaGuard(
            ctx, orgContext: new HttpOrganizationContext(new HttpContextAccessor()));

        using (AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(TestDb.SystemOrgId, isSystem: true)))
        {
            var act = () => guard.EnsureCanWriteAsync(CancellationToken.None);
            await act.Should().NotThrowAsync();
        }

        using (AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(TestDb.DefaultOrgId, isSystem: false)))
        {
            var act = () => guard.EnsureCanWriteAsync(CancellationToken.None);
            (await act.Should().ThrowAsync<PlanValidationException>())
                .Which.Errors.Should().ContainKey("storage");
        }
    }

    [Fact]
    public async Task Single_tenant_mode_never_blocks_even_over_quota()
    {
        await SetZeroQuotaAsync();
        await using var ctx = _db.NewContext();
        var guard = _db.NewQuotaGuard(ctx, singleTenant: true);

        var act = () => guard.EnsureCanWriteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
