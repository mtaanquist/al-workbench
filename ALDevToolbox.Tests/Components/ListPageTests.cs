using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <see cref="ListPage"/> exists so a list cannot be in two states at once or in none:
/// each test here is one of the four bodies and what the frame does around it. The
/// frame's own markup belongs to PageHead and FilterBar and is pinned with them.
/// </summary>
public sealed class ListPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ListPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    private IRenderedComponent<ListPage> Render(bool loading = false, bool empty = false, bool noMatches = false) =>
        _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Templates")
            .Add(c => c.Actions, Html("<button class=\"btn\">New template</button>"))
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />"))
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsEmpty, empty)
            .Add(c => c.HasNoMatches, noMatches)
            .Add(c => c.Empty, Html("<div id=\"empty\"></div>"))
            .Add(c => c.NoMatches, Html("<div id=\"none\"></div>"))
            .Add(c => c.ChildContent, Html("<table id=\"rows\"></table>"))
            .Add(c => c.Footer, Html("<div class=\"pager\"></div>")));

    // An element without an id reports "", not null, so ?? would never reach the class.
    private static string Name(AngleSharp.Dom.IElement e) => string.IsNullOrEmpty(e.Id) ? e.ClassName ?? "" : e.Id;

    private static List<string> Bodies(IRenderedComponent<ListPage> cut) =>
        cut.FindAll("#empty, #none, #rows, .loading-block, .pager").Select(Name).ToList();

    [Fact]
    public void Populated_renders_head_filters_rows_and_footer_in_that_order()
    {
        var cut = Render();

        cut.Find("div.page").Children.Select(Name).Should()
            .Equal("page-head", "filter-bar", "rows", "pager");
        cut.FindAll(".page-head__actions").Should().ContainSingle();
    }

    [Fact]
    public void Loading_shows_only_the_loading_block_and_holds_back_filters_and_actions()
    {
        var cut = Render(loading: true);

        Bodies(cut).Should().Equal("loading-block");
        cut.FindAll(".filter-bar").Should().BeEmpty();
        // Until the load returns the page cannot know whether the next step is the
        // head's button or the empty state's, so it shows neither.
        cut.FindAll(".page-head__actions").Should().BeEmpty();
    }

    [Fact]
    public void Loading_wins_over_a_stale_empty_flag()
    {
        Bodies(Render(loading: true, empty: true)).Should().Equal("loading-block");
    }

    [Fact]
    public void Empty_drops_the_filter_row_and_leaves_the_next_step_to_the_empty_state()
    {
        var cut = Render(empty: true);

        Bodies(cut).Should().Equal("empty");
        cut.FindAll(".filter-bar").Should().BeEmpty();
        cut.FindAll(".page-head__actions").Should().BeEmpty();
    }

    [Fact]
    public void No_matches_keeps_the_filter_row_because_it_is_the_way_back()
    {
        var cut = Render(noMatches: true);

        Bodies(cut).Should().Equal("none");
        cut.FindAll(".filter-bar").Should().ContainSingle();
        cut.FindAll(".page-head__actions").Should().ContainSingle();
    }

    [Fact]
    public void A_page_whose_filters_can_cause_the_empty_state_keeps_them_over_it()
    {
        var cut = _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Cookbook")
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />"))
            .Add(c => c.IsEmpty, true)
            .Add(c => c.FiltersWhenEmpty, true)
            .Add(c => c.Empty, Html("<div id=\"empty\"></div>")));

        cut.FindAll(".filter-bar").Should().ContainSingle();
        cut.FindAll("#empty").Should().ContainSingle();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Sections_after_the_list_show_in_every_state(bool loading, bool empty)
    {
        var cut = _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Cookbook")
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsEmpty, empty)
            .Add(c => c.After, Html("<section id=\"rules\"></section>")));

        cut.Find("div.page").Children.Last().Id.Should().Be("rules");
    }

    [Fact]
    public void A_page_can_supply_its_own_loading_body_and_keep_address_driven_filters_up()
    {
        var cut = _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Upgrades")
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />"))
            .Add(c => c.IsLoading, true)
            .Add(c => c.FiltersWhileLoading, true)
            .Add(c => c.Loading, Html("<table id=\"skeleton\"></table>")));

        cut.FindAll("#skeleton").Should().ContainSingle();
        cut.FindAll(".loading-block").Should().BeEmpty();
        cut.FindAll(".filter-bar").Should().ContainSingle();
    }
    /// <summary>
    /// The power list's <c>.cmdbar</c> is not a FilterBar, but it answers to the same
    /// rule: nothing to command on a first-run page, or before the rows are known.
    /// </summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_toolbar_shows_exactly_when_a_filter_row_would(bool loading, bool empty, bool shown)
    {
        var cut = _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Upgrades")
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsEmpty, empty)
            .Add(c => c.Toolbar, Html("<div class=\"cmdbar\"></div>"))
            .Add(c => c.ChildContent, Html("<table id=\"rows\"></table>")));

        cut.FindAll(".cmdbar").Should().HaveCount(shown ? 1 : 0);
        cut.FindAll(".filter-bar").Should().BeEmpty(
            because: "a page with a bar of its own and no filters must not get an empty filter row too");
    }

    /// <summary>
    /// A list with views (Upgrades: Open, Archive, Fleet) has its switch on a row of its
    /// own straight under the head - before the notices and the filter row - and in every
    /// state, because the view that is loading or empty is the one somebody may want to
    /// switch away from.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_view_switch_sits_under_the_head_in_every_state(bool loading, bool empty)
    {
        var cut = _ctx.Render<ListPage>(p => p
            .Add(c => c.Title, "Upgrades")
            .Add(c => c.IsLoading, loading)
            .Add(c => c.IsEmpty, empty)
            .Add(c => c.Views, Html("<nav id=\"views\" class=\"pill-tabs\"></nav>"))
            .Add(c => c.Notices, Html("<p id=\"notice\"></p>"))
            .Add(c => c.Search, Html("<input class=\"input\" type=\"search\" />"))
            .Add(c => c.Empty, Html("<div id=\"empty\"></div>"))
            .Add(c => c.ChildContent, Html("<table id=\"rows\"></table>")));

        var order = cut.Find("div.page").Children.Select(Name).ToList();
        order.Take(3).Should().Equal("page-head", "views", "notice");
    }

    [Fact]
    public void Sticky_is_passed_to_the_head()
    {
        var cut = _ctx.Render<ListPage>(p => p.Add(c => c.Title, "Environments").Add(c => c.Sticky, true));

        cut.Find(".page-head").ClassList.Should().Contain("page-head--sticky");
    }

}
