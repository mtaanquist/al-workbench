using ALDevToolbox.Components.Pages;
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
/// Smoke test for the canonical list page — the first surface moved onto the
/// design system's list archetype. Pins the state contract from CLAUDE.md
/// §"Always have the end user in mind", now **four** states rather than three:
/// loading, first-run empty, filtered-empty, populated. The fourth is the one
/// the design handoff calls out specifically, because "no templates yet" is a
/// lie when some exist and the filter simply matched none — so it must offer
/// "Clear search" rather than the create action.
/// </summary>
public sealed class TemplatesBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public TemplatesBrowserTests()
    {
        // Authorize attribute: bUnit's BunitAuthorizationContext stubs the
        // authentication state. We render as a generic authenticated user;
        // the page doesn't branch on role, only on data.
        _auth = _ctx.AddAuthorization();
        _auth.SetAuthorized("tester@example.com");

        // Real services against the per-fixture Postgres. Resolving via DI
        // mirrors production wiring rather than mocking a single method
        // (CLAUDE.md: no interfaces just for tests). Bind the ambient context
        // to the IOrganizationContext interface — TemplateService asks for the
        // interface, not the concrete type.
        _ctx.Services.AddSingleton<ALDevToolbox.Services.IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<FolderTreeHydrator>();
        _ctx.Services.AddScoped<TemplateService>();
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        // The archetype's empty states carry a Lucide glyph, so the page now
        // renders <Icon>, which property-injects the catalogue.
        _ctx.Services.AddSingleton<IconCatalog>();
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Empty_template_set_tells_a_member_who_to_ask_rather_than_linking_to_admin()
    {
        // A User cannot open the importer (#1266, L35).
        var cut = _ctx.Render<TemplatesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No templates yet");
            cut.FindAll("a[href='/admin/templates']").Should().BeEmpty();
            cut.Markup.Should().Contain("Ask one of your administrators");
        });
    }

    [Fact]
    public void Empty_template_set_renders_a_useful_empty_state_with_a_link_to_the_admin_importer()
    {
        _auth.SetRoles("Editor");
        var cut = _ctx.Render<TemplatesBrowser>();

        // Tick the dispatcher so OnInitializedAsync's await on the DB
        // settles before we assert; bUnit's WaitForAssertion handles the
        // microtask hop.
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No templates yet");
            cut.Find("a[href='/admin/templates']").Should().NotBeNull(
                "the empty-state copy must offer a path to the recovery action — "
                + "CLAUDE.md §\"three states\" rule");
        });
    }

    [Fact]
    public async Task A_search_matching_nothing_offers_to_clear_it_rather_than_to_create()
    {
        await SeedTemplateAsync();
        var cut = _ctx.Render<TemplatesBrowser>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Test Runtime"));

        cut.Find("input[type=search]").Input("zzzz");

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No templates match");
            // The distinction that matters: a filtered-empty list must not
            // claim there is nothing to see, nor push the create action.
            cut.Markup.Should().NotContain("No templates yet");
            cut.Find(".empty-state__action button").TextContent.Trim().Should().Be("Clear search");
        });
    }

    [Fact]
    public async Task Clearing_the_search_brings_the_rows_back()
    {
        await SeedTemplateAsync();
        var cut = _ctx.Render<TemplatesBrowser>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Test Runtime"));

        cut.Find("input[type=search]").Input("zzzz");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No templates match"));

        cut.Find(".empty-state__action button").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Test Runtime"));
    }

    [Fact]
    public async Task A_row_carries_its_state_as_an_edge_class_and_a_glyph_never_a_pill()
    {
        await SeedTemplateAsync();
        var cut = _ctx.Render<TemplatesBrowser>();

        cut.WaitForAssertion(() =>
        {
            // The handoff's rule: table rows never use pills; status is the
            // edge keyline plus a leading glyph with an accessible name.
            cut.Find("table.data-table--edge").Should().NotBeNull();
            cut.Find("tr.is-published td.data-table__col-state").Should().NotBeNull();
            cut.Find(".data-table__state--icon").GetAttribute("aria-label").Should().Be("Active");
            cut.FindAll("table .status-pill").Should().BeEmpty(
                "a table row carries status as the edge bar and glyph, never a pill");
        });
    }

    private async Task SeedTemplateAsync()
    {
        await using var db = _db.NewContext();
        db.RuntimeTemplates.Add(TemplateBuilder.Default());
        await db.SaveChangesAsync();
    }
}
