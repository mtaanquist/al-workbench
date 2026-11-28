using System.Security.Claims;
using ALDevToolbox.Components.Pages;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Smoke tests for the launcher at <c>/</c>.
///
/// The tile list is the pin that matters. This page silently omitted four of
/// the ten tools for as long as they have existed - they were in the sidebar
/// and never here - so a test that counts tiles is what stops the next tool
/// shipping half-linked. The sign-in destinations are pinned for the same
/// reason: two of them pointed at the wrong page and at a 404, and nothing
/// about the markup looked wrong.
/// </summary>
public sealed class HomeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly MutableSingleTenant _singleTenant = new();

    private sealed class MutableSingleTenant : ALDevToolbox.Services.SingleTenant.ISingleTenantMode
    {
        public bool IsEnabled { get; set; }
    }
    private readonly ToolAvailabilityState _tools;

    public HomeTests()
    {
        _db.McpAvailability.Set(true);
        _tools = new ToolAvailabilityState(_db.McpAvailability);

        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<AppDbContext>(opts => opts
            .UseNpgsql(_db.ConnectionString)
            .AddInterceptors(_db.CommandTracker)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));
        _ctx.Services.AddScoped<DashboardService>();
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>(_singleTenant);
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    /// <summary>
    /// Derived from <see cref="ToolCatalog.All"/> rather than a hand-written
    /// list, because a hand-written list is green the day someone ships tool
    /// eleven, adds it to the catalogue and the sidebar, and forgets the
    /// launcher — which is exactly how Projects, Pipelines, Releases and
    /// Translator were missing from this page for as long as they existed.
    /// </summary>
    [Fact]
    public void Every_tool_in_the_catalogue_has_a_tile_on_the_launcher()
    {
        Authorize();

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href")!).ToList();
            foreach (var tool in ToolCatalog.All)
            {
                hrefs.Should().Contain(
                    h => tool.RoutePrefixes.Any(p => h.StartsWith(p, StringComparison.Ordinal)),
                    $"{tool.Key} is in the tool catalogue and the sidebar, so it needs a launcher tile");
            }
        });
    }

    [Fact]
    public void Signed_out_tiles_lead_back_to_the_tool_they_advertise()
    {
        Anonymous();

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href")).ToList();
            // The generators are the two that were wrong: Workspace pointed at
            // the Projects tool's create page and Extension at a route that does
            // not exist, so signing in from either landed somewhere else.
            hrefs.Should().Contain("/login?return=%2Ftemplates%2Fworkspace");
            hrefs.Should().Contain("/login?return=%2Ftemplates%2Fextension");
            hrefs.Should().NotContain(h => h!.Contains("%2Fprojects%2Fnew"));
            hrefs.Should().NotContain(h => h!.Contains("%2Fprojects%2Fextension"));
            // Diff needs no account, so it is the one tile that links straight through.
            hrefs.Should().Contain("/diff");

            // Every tile either links straight to its tool or signs you in and
            // sends you there. One rule catches both original bugs: a tile that
            // is missing, and a tile whose return address points somewhere else.
            foreach (var tool in ToolCatalog.All)
            {
                hrefs.Should().Contain(
                    h => tool.RoutePrefixes.Any(p =>
                        h!.StartsWith(p, StringComparison.Ordinal)
                        || h.StartsWith("/login?return=" + Uri.EscapeDataString(p), StringComparison.Ordinal)),
                    $"{tool.Key}'s signed-out tile must lead back to {tool.Key}");
            }
        });
    }

    [Fact]
    public void A_group_whose_tools_are_all_switched_off_takes_its_heading_with_it()
    {
        Authorize();
        _tools.Set(new[] { ToolKey.Projects, ToolKey.Pipelines, ToolKey.Releases });

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            // A heading over nothing is worse than no heading: it reads as a
            // section that failed to load.
            // Equal, not BeEquivalentTo: the group order is the hand-off's, and
            // an order-insensitive check would not notice it changing.
            cut.FindAll(".section-label").Select(l => l.TextContent.Trim())
                .Should().Equal("Build", "Work with text", "Connect an assistant");
        });
    }

    /// <summary>
    /// Seeds *other* tools so the render proves the counts were actually
    /// fetched. Without that, this test passes against the `ToolCounts.Empty`
    /// the field is initialised to — delete the service call from the page and
    /// it stays green, which is what the first version of it did.
    /// </summary>
    [Fact]
    public void An_empty_tool_says_what_to_do_next_instead_of_showing_a_zero()
    {
        Authorize();
        using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("proves-the-query-ran"));
            seed.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var metas = cut.FindAll(".tool-tile__meta").Select(m => m.TextContent.Trim()).ToList();
            metas.Should().Contain("1 template", "the counts must come from the database, not the initialiser");
            // A count of nothing is a fact; the second half is the next step. No
            // empty meta may be a bare zero.
            metas.Should().NotContain(m => m.StartsWith("0 ", StringComparison.Ordinal));
            metas.Should().Contain("No recipes yet - suggest the first one");
            metas.Should().Contain("No BC releases yet - ask an admin to import one");
            metas.Should().Contain("No solutions yet - add your first");
        });
    }

    [Fact]
    public void A_signed_out_visitor_is_told_nothing_about_what_the_organisation_holds()
    {
        Anonymous();
        using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("private"));
            seed.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".tool-tile").Should().NotBeEmpty();
            cut.FindAll(".tool-tile__meta").Should().BeEmpty(
                "what a tool holds is the organisation's business, and a signed-out "
                + "visitor has no organisation for the tenant filter to scope to");
        });
    }

    /// <summary>
    /// The per-org opt-out rides on the `org_disabled_tools` claim, and it is
    /// the only path by which an org's switched-off tool disappears from this
    /// page. Nothing covered it: deleting the claim read from the page left
    /// every other test green while a tool the org had turned off stayed
    /// advertised on the front page.
    /// </summary>
    [Fact]
    public void A_tool_the_organisation_switched_off_is_not_advertised()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("user@cronus.example");
        auth.SetRoles("User");
        auth.SetClaims(new Claim("org_disabled_tools", "Translator,Projects,Pipelines,Releases"));

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var hrefs = cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href")!).ToList();
            hrefs.Should().NotContain(h => h.StartsWith("/translator", StringComparison.Ordinal));
            hrefs.Should().Contain("/piper", "only the named tools are switched off");
            // The whole Deliver group went with its tiles rather than leaving a
            // heading over nothing — the org-level case, not the site-level one.
            cut.FindAll(".section-label").Select(l => l.TextContent.Trim())
                .Should().NotContain("Deliver");
        });
    }

    [Fact]
    public void A_populated_organisation_counts_what_the_linked_page_shows()
    {
        Authorize();
        using (var seed = _db.NewContext())
        {
            seed.RuntimeTemplates.Add(TemplateBuilder.Default("pickable"));
            seed.Recipes.Add(RecipeBuilder.Default("One"));
            seed.Recipes.Add(RecipeBuilder.Default("Two"));
            var deprecated = RecipeBuilder.Default("Deprecated");
            deprecated.Deprecated = true;
            seed.Recipes.Add(deprecated);
            seed.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            var metas = cut.FindAll(".tool-tile__meta").Select(m => m.TextContent.Trim()).ToList();
            metas.Should().Contain("1 template");
            metas.Should().Contain("2 recipes", "the plural arm is a separate branch from the singular one");
        });
    }

    /// <summary>
    /// Single-tenant, a signed-out visitor got here from a Sealed, Piper or
    /// Diff link. They see the tools that need no account and nothing about
    /// the rest of the product.
    /// </summary>
    [Fact]
    public void Single_tenant_signed_out_shows_only_the_tools_that_need_no_account()
    {
        _singleTenant.IsEnabled = true;
        Anonymous();

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".tool-tile").Select(t => t.GetAttribute("href"))
                .Should().BeEquivalentTo("/diff", "/piper");
            cut.FindAll(".tool-tile--locked").Should().BeEmpty();
            cut.FindAll(".section-label").Should().BeEmpty();
            cut.FindAll(".page__hero-sub").Should().BeEmpty();
            cut.Markup.Should().NotContain("Object Explorer");
        });
    }

    [Fact]
    public void Single_tenant_signed_in_gets_the_full_launcher()
    {
        _singleTenant.IsEnabled = true;
        Authorize();

        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
            cut.FindAll(".tool-tile").Should().HaveCountGreaterThan(2));
    }

    /// <summary>Registers the auth services and leaves the visitor signed out.</summary>
    private void Anonymous() => _ctx.AddAuthorization().SetNotAuthorized();

    private void Authorize()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("user@cronus.example");
        auth.SetRoles("User");
    }
}
