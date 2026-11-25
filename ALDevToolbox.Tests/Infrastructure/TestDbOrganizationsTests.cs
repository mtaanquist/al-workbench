using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Infrastructure;

/// <summary>
/// Pins that every identity <see cref="TestDb.OrgContext"/> can take agrees with
/// the organisation row it names (#1441). Production reads "is this the system
/// org" off the row at sign-in, so a fixture whose context and row disagree
/// tests a request that cannot happen: before this, the default identity was
/// the system org's row with the context saying "tenant", and most of the suite
/// generated inside the system org's own catalogue.
/// </summary>
public sealed class TestDbOrganizationsTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void The_default_identity_is_an_ordinary_tenant()
    {
        _db.OrgContext.CurrentOrganizationId.Should().Be(TestDb.DefaultOrgId);
        _db.OrgContext.IsSystemOrganization.Should().BeFalse();
    }

    [Theory]
    [InlineData(TestDb.SystemOrgId)]
    [InlineData(TestDb.OtherOrgId)]
    [InlineData(TestDb.DefaultOrgId)]
    public async Task Acting_as_an_organisation_carries_its_rows_own_system_flag(int organizationId)
    {
        await using var ctx = _db.NewContext();
        var isSystem = await ctx.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId).Select(o => o.IsSystem).SingleAsync();

        _db.OrgContext.CurrentOrganizationId = organizationId;

        _db.OrgContext.IsSystemOrganization.Should().Be(isSystem);
    }

    [Fact]
    public async Task Only_the_system_org_id_names_the_system_org()
    {
        await using var ctx = _db.NewContext();

        (await ctx.Organizations.AsNoTracking().Where(o => o.IsSystem).Select(o => o.Id).ToListAsync())
            .Should().Equal([TestDb.SystemOrgId]);
    }

    [Fact]
    public async Task The_default_tenant_starts_with_the_files_signup_gives_a_new_organisation()
    {
        await using var ctx = _db.NewContext();

        var paths = await ctx.OrganizationFiles.AsNoTracking().Select(f => f.Path).ToListAsync();

        paths.Should().BeEquivalentTo(
            ALDevToolbox.Services.Organizations.PlatformOrganizationFiles.All.Select(f => f.Path));
    }
}
