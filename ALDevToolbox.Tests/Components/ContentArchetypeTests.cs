using ALDevToolbox.Components.Shared;
using ALDevToolbox.Components.Shared.Archetypes;
using ALDevToolbox.Services;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Structure of the content archetypes (12 docs, 13 setup steps, 14 error page) and
/// the copy button they share. As with the primitives, the markup is the contract:
/// the sheets are byte-locked, so a drifted component cannot be patched in CSS.
/// </summary>
public sealed class ContentArchetypeTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ContentArchetypeTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    private static RenderFragment Html(string markup) => b => b.AddMarkupContent(0, markup);

    [Fact]
    public void CopyButton_carries_the_contract_the_shell_script_reads()
    {
        var cut = _ctx.Render<CopyButton>(p => p.Add(c => c.Target, "#mcp-url"));

        var button = cut.Find("button.copy-btn");
        button.ClassList.Should().Contain("btn--sm");
        button.GetAttribute("type").Should().Be("button");
        button.GetAttribute("data-copy-target").Should().Be("#mcp-url");
        cut.Find("button > span[data-copy-label]").TextContent.Should().Be("Copy");
    }

    [Fact]
    public void CopyButton_among_page_actions_is_full_size_with_its_own_label()
    {
        var cut = _ctx.Render<CopyButton>(p => p
            .Add(c => c.Target, "#err-ref").Add(c => c.Label, "Copy reference").Add(c => c.Small, false));

        cut.Find("button.copy-btn").ClassList.Should().NotContain("btn--sm");
        cut.Find("span[data-copy-label]").TextContent.Should().Be("Copy reference");
    }

    [Fact]
    public void DocsPage_renders_head_prose_column_and_a_toc_built_from_data()
    {
        var cut = _ctx.Render<DocsPage>(p => p
            .Add(c => c.Title, "What's next")
            .Add(c => c.Subtitle, "Opening what you downloaded.")
            .Add(c => c.Toc, new[] { new DocsTocEntry("Open the folder", "wn-open"), new DocsTocEntry("A sub point", "wn-sub", Sub: true) })
            .Add(c => c.ChildContent, Html("<h2 id=\"wn-open\">Open the folder</h2>")));

        cut.Find("div.page").Children.Select(e => e.ClassName).Should().Equal("page-head", "docs");
        cut.Find("div.docs").Children.Select(e => e.ClassName).Should().Equal("docs__main", "docs__toc");
        cut.Find(".docs__main > article.prose > h2").Id.Should().Be("wn-open");

        var links = cut.FindAll(".docs__toc-list > a.toc-link");
        links.Select(a => a.GetAttribute("href")).Should().Equal("#wn-open", "#wn-sub");
        links[0].ClassList.Should().NotContain("toc-link--sub");
        links[1].ClassList.Should().Contain("toc-link--sub");
    }

    [Fact]
    public void DocsPage_without_prose_leaves_the_articles_to_the_page()
    {
        var cut = _ctx.Render<DocsPage>(p => p
            .Add(c => c.Title, "Connect")
            .Add(c => c.Toc, Array.Empty<DocsTocEntry>())
            .Add(c => c.Prose, false)
            .Add(c => c.ChildContent, Html("<article class=\"prose\"></article><div class=\"picker\"></div>")));

        cut.Find(".docs__main").Children.Select(e => e.ClassName).Should().Equal("prose", "picker");
    }

    [Fact]
    public void SetupStep_shows_its_number_until_done_then_a_tick()
    {
        var todo = _ctx.Render<SetupStep>(p => p
            .Add(c => c.Number, 2).Add(c => c.Title, "Paste the settings")
            .Add(c => c.Text, Html("Do this.")).Add(c => c.ChildContent, Html("<pre>x</pre>")));

        var step = todo.Find("div.step");
        step.ClassList.Should().NotContain("is-done").And.NotContain("step--current");
        step.Children.Select(e => e.ClassName).Should().Equal("step__n", "step__head", "step__text", "step__body");
        todo.Find(".step__n").TextContent.Trim().Should().Be("2");
        todo.Find(".step__head > .step__title").TextContent.Should().Be("Paste the settings");

        var done = _ctx.Render<SetupStep>(p => p
            .Add(c => c.Number, 1).Add(c => c.Title, "Choose").Add(c => c.State, SetupStepState.Done)
            .Add(c => c.HeadExtra, Html("<span class=\"badge\">Token</span>")));

        done.Find("div.step").ClassList.Should().Contain("is-done");
        done.Find(".step__n").QuerySelectorAll("svg").Should().ContainSingle();
        done.Find(".step__n").TextContent.Trim().Should().BeEmpty();
        done.Find(".step__head").Children.Select(e => e.ClassName).Should().Equal("step__title", "badge");
        done.FindAll(".step__text, .step__body").Should().BeEmpty();

        _ctx.Render<SetupStep>(p => p.Add(c => c.Number, 1).Add(c => c.Title, "Now").Add(c => c.State, SetupStepState.Current))
            .Find("div.step").ClassList.Should().Contain("step--current");
    }

    [Fact]
    public void SetupSteps_wraps_its_steps()
    {
        var cut = _ctx.Render<SetupSteps>(p => p.Add(c => c.ChildContent, Html("<div class=\"step\"></div>")));
        cut.Find("div.steps").ClassName.Should().Be("steps");
        cut.Find("div.steps > div.step").Should().NotBeNull();
    }

    [Fact]
    public void ErrorPage_keeps_the_handoff_slot_order()
    {
        var cut = _ctx.Render<ErrorPage>(p => p
            .Add(c => c.Icon, "server-crash").Add(c => c.Danger, true)
            .Add(c => c.Code, "Error 500 - server error").Add(c => c.Title, "Something went wrong")
            .Add(c => c.Text, Html("Sorry."))
            .Add(c => c.Detail, Html("<span class=\"errpage__path\">/x</span>"))
            .Add(c => c.Actions, Html("<a class=\"btn btn--primary\" href=\"/\">Go to Home</a>"))
            .Add(c => c.ChildContent, Html("<span class=\"errpage__links\"></span>")));

        cut.Find("div.errpage > div.errpage__inner").Children.Select(e => e.ClassName).Should().Equal(
            "errpage__glyph errpage__glyph--danger", "errpage__code", "errpage__title",
            "errpage__text", "errpage__path", "errpage__acts", "errpage__links");
        cut.Find("h1.errpage__title").TextContent.Should().Be("Something went wrong");
        cut.FindAll(".errpage__acts .btn--primary").Should().ContainSingle();
    }
}
