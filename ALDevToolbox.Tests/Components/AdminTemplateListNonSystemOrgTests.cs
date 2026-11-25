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
/// Companion to <see cref="AdminTemplateListTests"/> covering the
/// non-system-org branch — i.e. the "From the site catalogue" section
/// surfaced by <c>@if (!OrgContext.IsSystemOrganization)</c>. The acting
/// org renders empty for its own templates while the system org's
/// catalogue appears alongside as importable rows. The
/// <c>AlreadyImported</c> tag flips to a muted label once the acting org
/// has a row with the same key.
/// </summary>
public sealed class AdminTemplateListNonSystemOrgTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public AdminTemplateListNonSystemOrgTests()
    {
        // Acting as an ordinary tenant so the page renders its "non-system"
        // branch. Other rather than Default: the admin signs in from it below.
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;

        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("admin@other.example");
        auth.SetRoles("Admin");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddScoped<TemplateImportService>();
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
    public void Empty_system_org_renders_the_empty_catalogue_copy()
    {
        var cut = _ctx.Render<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("From the site catalogue",
                "the section header must render whenever the acting org is not the "
                + "system org — see NavMenu.razor's header comment");
            cut.Markup.Should().Contain("Nothing published yet",
                "the empty-state copy must distinguish \"no catalogue published\" "
                + "from \"no local templates yet\"");
        });
    }

    [Fact]
    public async Task System_org_templates_render_as_importable_rows_with_an_import_button()
    {
        // Seed a template into the system org. The acting org is Other, with
        // no overlap, so the row shows the Import button rather than the
        // Already imported label.
        await using (var seed = _db.NewContext())
        {
            var sys = TemplateBuilder.Default(key: "runtime-system", organizationId: TestDb.SystemOrgId);
            sys.Name = "System runtime";
            sys.Description = "Canonical template published by the site.";
            seed.RuntimeTemplates.Add(sys);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("System runtime");
            cut.FindAll("button").Select(b => b.TextContent.Trim())
                .Should().Contain(s => s.Contains("Import"),
                    "the row must surface the Import action; missing it means the "
                    + "fork pipeline has no entry point from this page");
        });
    }

    [Fact]
    public async Task Already_imported_template_renders_a_muted_label_instead_of_an_import_button()
    {
        // Same key in both orgs simulates the post-import state.
        await using (var seed = _db.NewContext())
        {
            var sys = TemplateBuilder.Default(key: "shared-key", organizationId: TestDb.SystemOrgId);
            sys.Name = "Shared";
            seed.RuntimeTemplates.Add(sys);

            var local = TemplateBuilder.Default(key: "shared-key", organizationId: TestDb.OtherOrgId);
            local.Name = "Local fork";
            seed.RuntimeTemplates.Add(local);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<AdminTemplateList>();

        cut.WaitForAssertion(() =>
        {
            // The "Already imported" label sits where the Import button would.
            // Render it via TemplateImportService's AlreadyImported flag — flipping
            // that flag is what the page reads on every catalogue row.
            cut.Markup.Should().Contain("Already imported",
                "TemplateImportService.ListSystemTemplatesAsync sets AlreadyImported "
                + "for keys the acting org already owns — the UI mirrors that flag");
        });
    }
}
