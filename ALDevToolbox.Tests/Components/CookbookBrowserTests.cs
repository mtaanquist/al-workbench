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
using ALDevToolbox.Services.Cookbook;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the state contract from CLAUDE.md §"Always have the end user in mind"
/// for the user-facing cookbook page — now the design system's list archetype,
/// so <b>four</b> states rather than three: loading, first-run empty,
/// filtered-empty, populated. Specifically guards the two empty branches: the
/// "no recipes in this org" copy points to <c>/cookbook/suggest</c> (the
/// recovery action), while the filtered branch name-checks the search term and
/// offers "Clear filters" instead — because "no recipes yet" is a lie when some
/// exist and a filter simply matched none.
/// </summary>
public sealed class CookbookBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public CookbookBrowserTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<RecipeService>();
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
    public void Empty_org_renders_the_recovery_link_to_the_suggest_page()
    {
        var cut = _ctx.Render<CookbookBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No recipes in this organisation yet");
            cut.Find("a[href='/cookbook/suggest']").Should().NotBeNull(
                "the empty-state copy must offer a path to the recovery action — "
                + "CLAUDE.md §\"three states\" rule");
        });
    }

    [Fact]
    public async Task Populated_org_renders_recipe_cards_with_links_to_the_detail_page()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Generic table proxy"));
            seed.Recipes.Add(RecipeBuilder.Default("Posting routine skeleton"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<CookbookBrowser>();

        cut.WaitForAssertion(() =>
        {
            var cards = cut.FindAll("a.browse-card");
            cards.Should().HaveCount(2);
            cards.Select(c => c.QuerySelector(".browse-card__title")!.TextContent.Trim())
                .Should().BeEquivalentTo(new[] { "Generic table proxy", "Posting routine skeleton" });
            cards.Select(c => c.GetAttribute("href"))
                .Should().AllSatisfy(h => h!.StartsWith("/cookbook/").Should().BeTrue());
        });
    }

    [Fact]
    public async Task A_search_matching_nothing_offers_to_clear_the_filters_rather_than_to_suggest()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Generic table proxy"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<CookbookBrowser>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Generic table proxy"));

        await cut.InvokeAsync(() => cut.Find("input[type=search]").Input("zzzz"));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("No recipes match");
            // The distinction that matters: a filtered-empty list must not claim
            // the organisation has none, nor push the suggest action.
            cut.Markup.Should().NotContain("No recipes in this organisation yet");
            cut.Find(".empty-state__action button").TextContent.Trim().Should().Be("Clear filters");
        });

        await cut.InvokeAsync(() => cut.Find(".empty-state__action button").Click());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Generic table proxy"));
    }

    /// <summary>
    /// A search can start while another call is still awaiting the page's database
    /// context (the debounced reload, the deprecated checkbox and Clear filters all start
    /// one), and a context takes one command at a time. The search runs on a context of
    /// its own, so it lands whatever the page's context is doing.
    /// </summary>
    [Fact]
    public async Task A_search_lands_while_the_pages_own_database_context_is_busy()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Generic table proxy"));
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<CookbookBrowser>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Generic table proxy"));

        var pageContext = _ctx.Services.GetRequiredService<ALDevToolbox.Data.AppDbContext>();
        var busy = pageContext.Database.ExecuteSqlRawAsync("SELECT pg_sleep(2)");
        try
        {
            await cut.InvokeAsync(() => cut.Find("input[type=search]").Input("zzzz"));
            cut.WaitForAssertion(() => cut.Markup.Should().Contain("No recipes match"));
        }
        finally
        {
            await busy;
        }
    }

    [Fact]
    public async Task A_card_shows_its_type_and_carries_no_edge_state_because_it_is_not_a_row()
    {
        await using (var seed = _db.NewContext())
        {
            var deprecated = RecipeBuilder.Default("Old pattern");
            deprecated.Deprecated = true;
            seed.Recipes.Add(deprecated);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<CookbookBrowser>();
        // Let the first load settle before touching a control — ticking the box
        // mid-OnInitializedAsync starts a second query on the same DbContext.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No recipes yet"));

        await cut.InvokeAsync(() => cut.Find("input[type=checkbox]").Change(true));

        cut.WaitForAssertion(() =>
        {
            // The handoff's split: table rows get the edge keyline and a glyph;
            // "a card has no shared edge to line up", so cards keep the pill.
            cut.Find("a.browse-card .status-pill").TextContent.Should().Contain("Deprecated");
            cut.FindAll("a.browse-card[class*='is-']").Should().BeEmpty(
                "row-state edge classes belong to .data-table rows, not cards");
        });
    }

    [Fact]
    public async Task Deprecated_recipes_are_hidden_until_the_include_deprecated_checkbox_is_ticked()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Recipes.Add(RecipeBuilder.Default("Active pattern"));
            var deprecated = RecipeBuilder.Default("Old pattern");
            deprecated.Deprecated = true;
            seed.Recipes.Add(deprecated);
            await seed.SaveChangesAsync();
        }

        var cut = _ctx.Render<CookbookBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("a.browse-card").Should().HaveCount(1,
                "deprecated rows are hidden by default — RecipeService.SearchAsync's "
                + "includeDeprecated parameter defaults to false");
            cut.Markup.Should().Contain("Active pattern");
            cut.Markup.Should().NotContain("Old pattern");
        });

        // Find-then-Change can race a re-render and invalidate the event
        // handler id between calls; wrap both in InvokeAsync so the renderer
        // sees them as a single synchronised operation. See the bUnit error
        // message for UnknownEventHandlerIdException, which spells this out.
        await cut.InvokeAsync(() => cut.Find("input[type=checkbox]").Change(true));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("a.browse-card").Should().HaveCount(2);
            cut.Markup.Should().Contain("Old pattern");
        });
    }
}
