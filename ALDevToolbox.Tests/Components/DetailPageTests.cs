using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <see cref="DetailPage"/> is the entity-detail frame from PageDetail.dc.html. These pin
/// the two things a hand-written copy kept getting wrong: where the crumbs sit (above
/// the head, not inside it) and that a missing entity still gives the page a heading.
/// </summary>
public sealed class DetailPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public DetailPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    private static string Name(AngleSharp.Dom.IElement e) => string.IsNullOrEmpty(e.Id) ? e.ClassName ?? "" : e.Id;

    [Fact]
    public void Found_renders_crumbs_above_the_head_then_the_facts_then_the_sections()
    {
        var cut = _ctx.Render<DetailPage>(p => p
            .Add(c => c.Title, "Build and test")
            .Add(c => c.TitleTooltip, "Pipeline #44")
            .Add(c => c.Subtitle, "Pipeline in CRONUS Sales Extension")
            .Add(c => c.Crumbs, Html("<a href=\"/pipelines\">Pipelines</a>"))
            .Add(c => c.TitleRow, Html("<span class=\"status-pill\">Running</span>"))
            .Add(c => c.Actions, Html("<button class=\"btn\">Edit</button>"))
            .Add(c => c.Meta, Html("<span class=\"meta-item\"></span>"))
            .Add(c => c.ChildContent, Html("<section id=\"runs\"></section>")));

        cut.Find("div.page").Children.Select(Name).Should()
            .Equal("page-head__crumbs", "detail-head", "meta-row", "runs");
        cut.Find("nav.page-head__crumbs").GetAttribute("aria-label").Should().Be("Breadcrumb");

        var row = cut.Find("header.detail-head > div > .detail-head__title-row");
        row.Children.Select(e => e.ClassName).Should().Equal("detail-head__title", "status-pill");
        cut.Find("h1.detail-head__title").GetAttribute("title").Should().Be("Pipeline #44");
        cut.Find("header.detail-head > div > p.page-head__sub").TextContent.Should().Be("Pipeline in CRONUS Sales Extension");
        cut.Find("header.detail-head > .page-head__actions button").TextContent.Should().Be("Edit");
    }

    [Fact]
    public void Optional_parts_are_left_out_rather_than_rendered_empty()
    {
        var cut = _ctx.Render<DetailPage>(p => p
            .Add(c => c.Title, "Production")
            .Add(c => c.Actions, Html("<button class=\"btn\">Open</button>"))
            .Add(c => c.ShowActions, false)
            .Add(c => c.Meta, Html("<span class=\"meta-item\"></span>"))
            .Add(c => c.ShowMeta, false));

        cut.Find("div.page").Children.Select(Name).Should().Equal("detail-head");
        cut.FindAll(".page-head__actions, .page-head__sub, .meta-row").Should().BeEmpty();
    }

    [Fact]
    public void A_loading_skeleton_replaces_the_plain_loading_block_and_still_draws_no_head()
    {
        var cut = _ctx.Render<DetailPage>(p => p
            .Add(c => c.IsLoading, true)
            .Add(c => c.Title, "28.5 in November 2026")
            .Add(c => c.LoadingTrail, [new Crumb("Upgrades", "/upgrades")])
            .Add(c => c.LoadingMeta, 3)
            .Add(c => c.Loading, Html("<div id=\"skeleton\"></div>")));

        // The frame draws the head's skeleton; the page's slot is only the body.
        cut.Find("div.page").Children.Select(Name).Should().Equal("page-head__crumbs", "detail-head", "meta-row", "skeleton");
        cut.Find(".page-head__crumbs a").GetAttribute("href").Should().Be("/upgrades");
        cut.FindAll(".meta-row .meta-item").Should().HaveCount(3);
        cut.FindAll(".loading-block, h1, .detail-head__title").Should().BeEmpty("there is no name to put in a heading yet");
    }

    [Fact]
    public void Loading_draws_no_head_because_there_is_no_name_to_put_in_it()
    {
        var cut = _ctx.Render<DetailPage>(p => p
            .Add(c => c.IsLoading, true)
            .Add(c => c.LoadingText, "Loading pipeline...")
            // A page whose "not found" flag is also true while loading must still load.
            .Add(c => c.IsNotFound, true)
            .Add(c => c.NotFound, Html("<div id=\"missing\"></div>")));

        cut.Find("div.page").Children.Select(Name).Should().Equal("loading-block");
        cut.Find(".loading-block").TextContent.Should().Contain("Loading pipeline...");
    }

    [Fact]
    public void Not_found_replaces_the_whole_page_and_its_empty_state_carries_the_heading()
    {
        var cut = _ctx.Render<DetailPage>(p => p
            .Add(c => c.IsNotFound, true)
            .Add(c => c.Title, null)
            .Add<EmptyState>(c => c.NotFound, e => e
                .Add(x => x.Title, "This pipeline doesn't exist")
                .Add(x => x.Heading, true)));

        cut.FindAll(".detail-head, .page-head__crumbs").Should().BeEmpty();
        cut.Find("h1.empty-state__title").TextContent.Should().Be("This pipeline doesn't exist");
    }
}
