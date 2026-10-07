using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the detail-page head, which PR 15c moved off the private
/// <c>.det-*</c> / <c>.art-*</c> / <c>.rel-empty*</c> dialect onto the design
/// system's <c>.page-head</c> and <c>.empty-state</c>.
///
/// The dialect was shared by three pages at once — <c>PipelineBuilds</c>,
/// <c>ProjectDetail</c> and <c>ReleasePipelineDetail</c> — which is why they had
/// to move together: porting one would have left the rules alive for the other
/// two and the migration would have looked done while nothing was deleted.
///
/// Two ways this breaks quietly:
///
/// <b>A returning rule out-specifies the design layer.</b> <c>tools.css</c>
/// loads after <c>components.css</c>, so re-adding any of these names does not
/// error — it silently wins, and the page drifts back one property at a time.
///
/// <b>A call site keeps a name whose rules are gone.</b> The class still parses,
/// the element still renders, and the head loses its layout without anything
/// failing. That is what a stale <c>.det-sub</c> would do: an unstyled div where
/// a meta line used to be.
///
/// The <c>.empty-state__icon</c> check is a shape rule, not a spelling one. It
/// is a 42px grid box that centres a glyph, so the class belongs on a wrapping
/// element. The dialect it replaced worked the other way round — <c>.rel-empty-ico</c>
/// went on the <c>&lt;Icon&gt;</c> itself via <c>Css=</c> — so the mechanical
/// translation of that markup produces a sized grid container with nothing to
/// centre and a glyph stretched to 42px.
/// </summary>
public sealed class DetailHeadTests
{
    private static readonly string[] Sheets =
    [
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/app.css",
        "ALDevToolbox/wwwroot/code-editor.css",
        "ALDevToolbox/wwwroot/source-viewer.css",
        "ALDevToolbox/wwwroot/shell.css",
        "ALDevToolbox/wwwroot/pages.css",
        "ALDevToolbox/wwwroot/pages-forms.css",
        "ALDevToolbox/wwwroot/pages-content.css",
        "ALDevToolbox/wwwroot/pages-power.css",
    ];

    /// <summary>
    /// The head dialect PR 15c deleted. <c>.det-grid</c> / <c>.det-col</c> /
    /// <c>.det-card</c> are deliberately absent: they are the body layout, and
    /// they retire with PRs 15d and 15e.
    /// </summary>
    private static readonly string[] Retired =
    [
        "det-bc", "det-head", "det-id", "det-pico", "det-title", "det-sub", "det-actions",
        "art-page", "art-detail", "art-fail",
        "rel-empty", "rel-empty-ico", "rel-empty-h", "rel-empty-p",
        "dotsep",
    ];

    /// <summary>The three pages that shared the dialect and had to move together.</summary>
    public static TheoryData<string> DetailPages => new()
    {
        "ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor",
        "ALDevToolbox/Components/Pages/Projects/ProjectDetail.razor",
        "ALDevToolbox/Components/Pages/Pipelines/ReleasePipelineDetail.razor",
    };

    [Fact]
    public void The_legacy_detail_head_dialect_is_gone_from_every_sheet()
    {
        foreach (var cls in Retired)
        {
            foreach (var sheet in Sheets)
            {
                Selectors(Read(sheet)).Should().NotContain(sel => Regex.IsMatch(sel, $@"\.{cls}\b"),
                    because: $".{cls} was the private head dialect PR 15c retired; the later sheets load "
                           + "after the design layer, so a returning rule would out-specify "
                           + ".page-head rather than conflict with it");
            }
        }
    }

    [Fact]
    public void No_component_still_renders_the_legacy_detail_head()
    {
        foreach (var file in Razors())
        {
            var classes = RenderedClasses(StripComments(File.ReadAllText(file))).ToHashSet();
            classes.Overlaps(Retired).Should().BeFalse(
                because: $"{Relative(file)} names a class with no rules left — the element still "
                       + "renders, so the head simply loses its layout with nothing failing");
        }
    }

    private const string DetailPageComponent = "ALDevToolbox/Components/Shared/Archetypes/DetailPage.razor";

    /// <summary>
    /// The head these three shared is one component now, so the page-level rule is
    /// that they use it and hand it crumbs; its shape is pinned once, below and in
    /// <c>DetailPageTests</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DetailPages))]
    public void Each_detail_page_composes_the_detail_frame_and_gives_it_crumbs(string page)
    {
        var markup = StripComments(Read(page));

        markup.Should().Contain("<DetailPage",
            because: "PageDetail.dc.html is the archetype for these three, and DetailPage is "
                   + "its one implementation - a hand-written head is how the three drifted apart");
        markup.Should().Contain("<Crumbs>",
            because: "a detail page is reached from a list, and the crumb row is the way back");
        RenderedClasses(markup).Should().NotContain("detail-head",
            because: "a page that also writes its own head has two");
    }

    [Fact]
    public void The_detail_frame_heads_with_a_title_row_and_not_the_list_head()
    {
        var classes = RenderedClasses(StripComments(Read(DetailPageComponent))).ToHashSet();

        classes.Should().Contain("page");
        classes.Should().Contain("detail-head",
            because: "the detail head carries a title ROW so a state pill can sit beside the "
                   + "title, which .page-head has nowhere to put");
        classes.Should().Contain("detail-head__title-row");
        classes.Should().Contain("detail-head__title");
        // The crumb row is CrumbNav's now; the frame's part is composing it.
        StripComments(Read(DetailPageComponent)).Should().Contain("<CrumbNav ");
    }

    [Fact]
    public void The_crumb_row_sits_outside_the_detail_head()
    {
        var markup = StripComments(Read(DetailPageComponent));

        var crumbs = markup.IndexOf("<CrumbNav ", StringComparison.Ordinal);
        var head = markup.IndexOf("class=\"detail-head\"", StringComparison.Ordinal);

        crumbs.Should().BeGreaterThan(-1);
        head.Should().BeGreaterThan(-1);
        crumbs.Should().BeLessThan(head,
            because: "the two archetypes differ and it is easy to copy the wrong one: "
                   + "PageList.dc.html nests the crumbs INSIDE .page-head, PageDetail.dc.html "
                   + "puts them above .detail-head as a sibling. Nesting them here pulls the "
                   + "crumbs into the flex row that holds the title and the actions");
    }

    /// <summary>
    /// The two run monitors' heads hold three or four buttons, which at phone width run off
    /// the right edge unless the head and its actions wrap (#929, #978). DetailPage renders
    /// the head, so the rules live in app.css keyed on the page's own class.
    /// </summary>
    [Theory]
    [InlineData("ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor", "pb-page")]
    [InlineData("ALDevToolbox/Components/Pages/Pipelines/ReleasePipelineDetail.razor", "rp-page")]
    public void A_run_monitor_head_wraps_its_actions_at_phone_width(string page, string pageClass)
    {
        StripComments(Read(page)).Should().Contain($"PageClass=\"{pageClass}\"");

        var rules = RulesFor(Read("ALDevToolbox/wwwroot/app.css"));
        rules.Should().ContainKey($".{pageClass} .detail-head")
            .WhoseValue.Should().Contain("flex-wrap: wrap");
        rules.Should().ContainKey($".{pageClass} .page-head__actions")
            .WhoseValue.Should().Contain("flex-wrap: wrap").And.Contain("flex: 0 1 auto",
                because: "components.css gives the actions `flex: none`, and a box that cannot "
                       + "shrink below one line of buttons never wraps them");
    }

    /// <summary>Each single selector to its declaration block, a selector list split apart.</summary>
    private static Dictionary<string, string> RulesFor(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        var rules = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            foreach (var sel in m.Groups["sel"].Value.Split(','))
            {
                var key = Regex.Replace(sel.Trim(), @"\s+", " ");
                rules[key] = rules.TryGetValue(key, out var prior) ? prior + ";" + m.Groups["body"].Value : m.Groups["body"].Value;
            }
        }
        return rules;
    }

    [Fact]
    public void No_detail_page_carries_two_pills_for_one_state()
    {
        var markup = StripComments(Read("ALDevToolbox/Components/Pages/Pipelines/PipelineBuilds.razor"));

        // Pills that say the build's state. The "Preview build" pill beside it (#994)
        // says a different fact - what the build was compiled against - so it does not count.
        Regex.Matches(markup, @"<StatusPill\s+Tone=""@RowStateIcon\.PillTone\(").Count.Should().Be(1,
            because: "the build's state belongs beside the page title, where the archetype "
                   + "puts it. The Latest-build card had a second pill saying the same word, "
                   + "which reads as two different facts until you look twice");
        // The "Disabled" pill (#1131) says something about the pipeline, not the build.
        Regex.Matches(markup, @"<StatusPill\s").Count.Should().Be(3,
            because: "the only other pills are the Preview build one beside the state and the pipeline's Disabled one");
    }

    [Fact]
    public void An_empty_state_glyph_is_wrapped_rather_than_worn_by_the_icon()
    {
        foreach (var file in Razors())
        {
            var markup = StripComments(File.ReadAllText(file));

            Regex.IsMatch(markup, @"<Icon[^>]*Css=""[^""]*\bempty-state__icon\b").Should().BeFalse(
                because: $"{Relative(file)} would put the 42px tinted grid box on the <svg> itself, "
                       + "stretching the glyph instead of centring it in a tile — the shape the "
                       + "retired .rel-empty-ico had, which is exactly what a mechanical port "
                       + "of that markup reproduces");
        }
    }

    // ── Helpers (mirroring RowActionsMenuTests) ────────────────────────

    private static IEnumerable<string> Razors() =>
        Directory.EnumerateFiles(Path.Combine(Root(), "ALDevToolbox/Components"), "*.razor",
            SearchOption.AllDirectories);

    private static string Relative(string full) =>
        Path.GetRelativePath(Root(), full).Replace('\\', '/');

    private static string StripComments(string razor) =>
        Regex.Replace(razor, @"@\*.*?\*@", "", RegexOptions.Singleline);

    private static IEnumerable<string> RenderedClasses(string markup) =>
        Regex.Matches(markup, @"class=""(?<v>[^""]*)""")
            .SelectMany(m => Regex.Replace(m.Groups["v"].Value, @"@\([^)]*\)", " ")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(c => c.Trim())
            .Where(c => c.Length > 0 && !c.StartsWith('@'));

    private static IEnumerable<string> Selectors(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{[^{}]*\}"))
        {
            yield return m.Groups["sel"].Value.Trim();
        }
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
