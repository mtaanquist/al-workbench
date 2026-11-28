using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Archetype 1's structure, and the two rules the launcher had to keep in step by
/// hand while the signed-in and signed-out tiles were separate markup: a group with
/// nothing in it takes its heading with it, and a locked tile sends the visitor to
/// sign in and back rather than to a page that will bounce them.
/// </summary>
public sealed class LauncherPageTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public LauncherPageTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private IRenderedComponent<LauncherPage> Render(params LauncherGroup[] groups) =>
        _ctx.Render<LauncherPage>(p => p
            .Add(c => c.Title, "AL Workbench")
            .Add(c => c.Subtitle, "Pick a tool to get started.")
            .Add(c => c.Groups, groups));

    [Fact]
    public void Renders_the_hero_then_a_label_and_grid_per_group()
    {
        var cut = Render(
            new LauncherGroup("Build", new[] { new LauncherTile("Templates", "Browse templates.", "layers", "/templates", Meta: "3 templates") }),
            new LauncherGroup("Deliver", new[] { new LauncherTile("Releases", "Publish a build.", "send", "/releases") }));

        cut.Find("div.page").Children.Select(e => e.ClassName).Should()
            .Equal("page__hero", "section-label", "tool-grid", "section-label", "tool-grid");
        cut.Find("h1.page__hero-title").TextContent.Should().Be("AL Workbench");
        cut.FindAll(".section-label").Select(e => e.TextContent).Should().Equal("Build", "Deliver");

        var tile = cut.Find("a.tool-tile[href='/templates']");
        tile.Children.Select(e => e.ClassName).Should().Equal("tool-tile__icon", "tool-tile__body");
        tile.QuerySelector(".tool-tile__body")!.Children.Select(e => e.ClassName).Should()
            .Equal("tool-tile__title", "tool-tile__text", "tool-tile__meta");
        cut.Find("a.tool-tile[href='/releases']").QuerySelectorAll(".tool-tile__meta").Should().BeEmpty();
    }

    [Fact]
    public void A_group_with_no_tiles_leaves_no_heading_behind()
    {
        var cut = Render(
            new LauncherGroup("Build", Array.Empty<LauncherTile>()),
            new LauncherGroup("Deliver", new[] { new LauncherTile("Releases", "Publish a build.", "send", "/releases") }));

        cut.FindAll(".section-label").Select(e => e.TextContent).Should().Equal("Deliver");
        cut.FindAll(".tool-grid").Should().ContainSingle();
    }

    [Fact]
    public void A_locked_tile_links_to_sign_in_with_the_tool_as_the_return_url()
    {
        var cut = Render(new LauncherGroup("Build", new[]
        {
            new LauncherTile("Workspace", "Generate a workspace.", "folder-plus", "/templates/workspace", Locked: true),
        }));

        var tile = cut.Find("a.tool-tile");
        tile.ClassList.Should().Contain("tool-tile--locked");
        tile.GetAttribute("href").Should().Be("/login?return=%2Ftemplates%2Fworkspace");
        tile.Children.Select(e => e.ClassName).Should().Equal("tool-tile__icon", "tool-tile__lock", "tool-tile__body");
        tile.QuerySelector(".tool-tile__lock > span")!.TextContent.Should().Be("Sign in");
    }
}
