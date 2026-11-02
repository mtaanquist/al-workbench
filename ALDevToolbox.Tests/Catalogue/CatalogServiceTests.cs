using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Catalogue;

/// <summary>
/// Field-keyed validation and cross-org isolation for the well-known dependency
/// catalogue. The admin editor renders inline errors keyed by
/// <c>Entries[i].Field</c>; renaming any of those keys breaks the form layer.
/// </summary>
public sealed class CatalogServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SaveAsync_with_missing_required_fields_keys_each_error()
    {
        var svc = NewService();
        var input = new CatalogEntryInput(Id: null,
            DepId: "", DepName: "", DepPublisher: "", DepVersionDefault: "", Category: null);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKeys(
            "Entries[0].DepId",
            "Entries[0].DepName",
            "Entries[0].DepPublisher",
            "Entries[0].DepVersionDefault");
    }

    [Fact]
    public async Task SaveAsync_with_non_guid_dep_id_keys_the_error()
    {
        var svc = NewService();
        var input = new CatalogEntryInput(null, "not-a-guid", "Base", "Microsoft", "1.0.0.0", null);

        var ex = (await svc.Invoking(s => s.SaveAsync(new[] { input }))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[0].DepId");
        ex.Errors["Entries[0].DepId"].Should().Contain("GUID");
    }

    [Fact]
    public async Task SaveAsync_with_duplicate_dep_id_keys_the_second_row()
    {
        var svc = NewService();
        var dup = "00000000-0000-0000-0000-000000000001";
        var inputs = new[]
        {
            new CatalogEntryInput(null, dup, "Base", "Microsoft", "1.0.0.0", null),
            new CatalogEntryInput(null, dup, "Base again", "Microsoft", "1.0.0.0", null),
        };

        var ex = (await svc.Invoking(s => s.SaveAsync(inputs))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[1].DepId");
    }

    [Fact]
    public async Task SaveAsync_with_the_same_id_typed_in_two_forms_keys_the_second_row()
    {
        var svc = NewService();
        var inputs = new[]
        {
            new CatalogEntryInput(null, "437DBF0E-84FF-417A-965D-ED2BB9650972", "Base", "Microsoft", "1.0.0.0", null),
            new CatalogEntryInput(null, "{437dbf0e-84ff-417a-965d-ed2bb9650972}", "Base again", "Microsoft", "1.0.0.0", null),
        };

        var ex = (await svc.Invoking(s => s.SaveAsync(inputs))
            .Should().ThrowAsync<PlanValidationException>()).Which;

        ex.Errors.Should().ContainKey("Entries[1].DepId");
    }

    /// <summary>
    /// #1615: an id typed with braces, in capitals or as 32 bare digits is
    /// stored in the plain form app.json uses, so the picker hands that form on.
    /// </summary>
    [Theory]
    [InlineData("{437dbf0e-84ff-417a-965d-ed2bb9650972}")]
    [InlineData("437DBF0E-84FF-417A-965D-ED2BB9650972")]
    [InlineData("437dbf0e84ff417a965ded2bb9650972")]
    [InlineData("  437dbf0e-84ff-417a-965d-ed2bb9650972  ")]
    public async Task SaveAsync_stores_the_id_in_its_plain_form(string typed)
    {
        await NewService().SaveAsync(new[]
        {
            new CatalogEntryInput(null, typed, "Base Application", "Microsoft", "1.0.0.0", null),
        });

        await using var ctx = _db.NewContext();
        var row = await ctx.WellKnownDependencies.AsNoTracking().SingleAsync();
        row.DepId.Should().Be("437dbf0e-84ff-417a-965d-ed2bb9650972");
    }

    [Fact]
    public async Task SaveAsync_inserts_then_removes_rows_missing_from_input()
    {
        var svc = NewService();
        await svc.SaveAsync(new[]
        {
            new CatalogEntryInput(null,
                "00000000-0000-0000-0000-000000000001", "Base", "Microsoft", "1.0.0.0", null),
            new CatalogEntryInput(null,
                "00000000-0000-0000-0000-000000000002", "App", "Microsoft", "1.0.0.0", null),
        });

        int firstId;
        await using (var ctx = _db.NewContext())
        {
            firstId = (await ctx.WellKnownDependencies.Where(w => w.DepName == "Base").SingleAsync()).Id;
        }

        // Re-save with only the first row — second should be removed.
        await svc.SaveAsync(new[]
        {
            new CatalogEntryInput(firstId,
                "00000000-0000-0000-0000-000000000001", "Base", "Microsoft", "1.0.0.0", null),
        });

        await using (var ctx = _db.NewContext())
        {
            var rows = await ctx.WellKnownDependencies.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(1);
            rows[0].Id.Should().Be(firstId, "existing row preserves its primary key across saves");
        }
    }

    [Fact]
    public async Task GetAllAsync_is_org_scoped()
    {
        await using (var seed = _db.NewContext())
        {
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000001", "Default", organizationId: TestDb.DefaultOrgId));
            seed.WellKnownDependencies.Add(WellKnownDependencyBuilder.ForNav(
                "00000000-0000-0000-0000-000000000002", "Other", organizationId: TestDb.OtherOrgId));
            await seed.SaveChangesAsync();
        }

        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        var rows = await NewService().GetAllAsync();
        rows.Select(r => r.DepName).Should().Equal("Default");
    }

    private CatalogService NewService() =>
        new(_db.NewContext(), NullLogger<CatalogService>.Instance, _db.OrgContext);
}
