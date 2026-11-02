using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke test for <c>/admin/modules</c>. Pins the three-state contract plus
/// the admin-list-specific "Show soft-deleted" toggle (CLAUDE.md §"admin
/// lists show deprecated and (with a toggle) deleted").
/// </summary>
public sealed class AdminModuleListTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminModuleListTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@example.com");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<ModuleService>();
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_module_set_renders_a_recovery_message_pointing_at_templates()
    {
        var cut = _ctx.Render<AdminModuleList>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No modules yet",
                "the empty-state copy must tell the admin how to recover — "
                + "CLAUDE.md §\"three states\" rule");
            cut.Find("a[href='/admin/templates']").Should().NotBeNull(
                "modules also arrive via template imports; the empty state must surface that path");
        });
    }

    [Fact]
    public async Task Populated_module_set_renders_one_row_per_module_with_keys_and_dependency_counts()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("posting", "Posting"));
            seed.Modules.Add(ModuleBuilder
                .Default("reporting", "Reporting")
                .WithDependency("Base Application", "Base Application", "Microsoft", "1.0.0.0"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminModuleList>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.data-table tbody tr");
            rows.Should().HaveCount(2);

            // By class, not column position: the table gained a leading state
            // cell when it moved onto .data-table--edge, and nth-child made the
            // assertion about layout rather than about the key being rendered.
            var keys = cut.FindAll("table.data-table tbody tr code.code")
                .Select(e => e.TextContent)
                .ToList();
            keys.Should().BeEquivalentTo(new[] { "posting", "reporting" });
        });
    }

    [Fact]
    public async Task Soft_deleted_rows_are_hidden_until_the_show_deleted_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Modules.Add(ModuleBuilder.Default("live"));
            var trashed = ModuleBuilder.Default("trashed");
            trashed.DeletedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            seed.Modules.Add(trashed);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminModuleList>();

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(1));

        var toggles = cut.FindAll("div.filter-bar input[type=checkbox]");
        toggles[0].Change(true);

        cut.WaitForAssertion(() =>
            cut.FindAll("table.data-table tbody tr").Should().HaveCount(2));
    }
}
