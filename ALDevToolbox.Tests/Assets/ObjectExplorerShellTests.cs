using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the Object Explorer's shell — the power-tool frame and the explorer
/// tree that PR 14b put around the code pane and inspector PR 14a ported.
///
/// Four things here go wrong quietly.
///
/// <b>The root class does double duty.</b> <c>.source-viewer</c> is both the
/// hook <c>source-viewer.js</c> mounts on and, in <c>tools.css</c>, a flex
/// column. The ported page is a <c>.pw</c> grid, and tools.css loads after
/// pages-power.css — so an unguarded <c>.source-viewer { display: flex }</c>
/// silently overwrites the frame's own layout.
///
/// <b>Two renderers, one row.</b> The server renders the branch leading to the
/// open file; <c>buildTreeRow</c> in JavaScript renders every folder opened
/// afterwards. A user cannot tell which is which, so the two have to agree on
/// class names, attributes and glyphs — and a mismatch is an unstyled or dead
/// row, never a build error.
///
/// <b>Two alphabets.</b> The tree's <c>.okind</c> badge spells the same short
/// form the search box accepts (<c>te:</c>, <c>c:</c>). If someone adds an
/// object kind to one and not the other, the tree either draws nothing or
/// teaches a prefix that does not work.
///
/// <b>Dead legacy rules.</b> The pre-port viewer's classes are retired (#562
/// took the second viewer and the CSS it stranded). The ported page must not
/// render any of them, and no rule may come back — see
/// <c>ObjectExplorerInspectorTests</c> for the sheet-side half of that.
/// </summary>
public sealed class ObjectExplorerShellTests
{
    private const string Viewer = "ALDevToolbox/Components/Pages/ObjectExplorer/SourceFileViewer.razor";
    private const string TreeRow = "ALDevToolbox/Components/Pages/ObjectExplorer/OeTreeRow.razor";
    private const string Glyphs = "ALDevToolbox/Components/Pages/ObjectExplorer/ObjectKindGlyph.cs";
    private const string Ranking = "ALDevToolbox/Services/ObjectExplorer/Explore/ObjectSearchRanking.cs";
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    private const string Tools = "ALDevToolbox/wwwroot/source-viewer.css";
    private const string PagesPower = "ALDevToolbox/wwwroot/pages-power.css";

    private static readonly string[] DesignSheets =
    [
        "ALDevToolbox/wwwroot/pages-power.css",
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/pages.css",
        "ALDevToolbox/wwwroot/tokens.css",
    ];

    // ── The frame ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("pw")]
    [InlineData("pw__head")]
    [InlineData("pw__title")]
    [InlineData("pw__name")]
    [InlineData("pw__file")]
    [InlineData("pw__bar")]
    [InlineData("pw__body")]
    [InlineData("pw__foot")]
    [InlineData("pw__sep")]
    [InlineData("pw__spacer")]
    [InlineData("pw-split")]
    [InlineData("oe")]
    [InlineData("oe__left")]
    [InlineData("oe__centre")]
    [InlineData("oe__right")]
    [InlineData("otree")]
    [InlineData("otree__row")]
    [InlineData("otree__row--app")]
    [InlineData("otree__caret")]
    [InlineData("otree__ico")]
    [InlineData("otree__name")]
    [InlineData("otree__id")]
    [InlineData("okind")]
    [InlineData("kbd")]
    [InlineData("kbd-hint")]
    [InlineData("kbd-hint__label")]
    [InlineData("u-compact")]
    public void Every_handoff_component_the_shell_uses_is_in_the_design_layer(string cls)
    {
        var markup = Read(Viewer) + Read(TreeRow) + Read(ViewerJs);
        markup.Should().MatchRegex($@"\b{Regex.Escape(cls)}\b",
            because: $"this test is only meaningful while the shell still renders .{cls}");

        // A rule of its own, or a rule that qualifies it — `.otree__row--app`
        // only ever appears as an ancestor of `.otree__ico`, and that still
        // means the design layer owns the name.
        DesignSheets.Any(f => Selectors(Read(f))
                .Any(sel => Regex.IsMatch(sel, $@"\.{Regex.Escape(cls)}(?![\w-])")))
            .Should().BeTrue(
                because: $".{cls} comes from the handoff, so its rule belongs in the design layer");
    }

    /// <summary>
    /// The collision that would be invisible: the ported page carries
    /// <c>.source-viewer</c> for the JS mount and <c>.pw</c> for its layout,
    /// and tools.css loads last. Any <c>.source-viewer</c> rule in tools.css
    /// that sets a layout property has to exclude <c>.pw</c>.
    /// </summary>
    [Fact]
    public void No_later_sheet_rule_lays_out_the_ported_root()
    {
        Read(Viewer).Should().Contain("class=\"pw u-compact object-explorer source-viewer\"",
            because: "the root carries both the frame class and the JS mount hook");

        var laidOut = new[] { "display", "flex-direction", "height", "grid-template" };
        var offenders = new List<string>();
        foreach (var (selector, body) in Rules(Read(Tools)))
        {
            // Only rules that can match the ported root itself. A descendant
            // selector (`.source-viewer__code`, `.source-viewer .foo`) styles
            // something inside it and is fine.
            if (!Regex.IsMatch(selector, @"(^|,\s*)[^,]*\.source-viewer(?![\w-])[^,]*$")) continue;
            var last = selector.Split(',').First(s => s.Contains(".source-viewer")).Trim();
            if (!Regex.IsMatch(last, @"\.source-viewer[\w.:()-]*$")) continue;
            if (last.Contains(":not(.pw)") || last.Contains("--compare")) continue;
            if (laidOut.Any(p => Regex.IsMatch(body, $@"(^|;|\s){Regex.Escape(p)}\s*:")))
            {
                offenders.Add(last);
            }
        }
        offenders.Should().BeEmpty(
            because: "source-viewer.css loads after pages-power.css, so these would beat .pw's own grid");
    }

    /// <summary>
    /// The pre-port viewer's layout classes. The ported page renders none of
    /// them; since #562 nothing else does either.
    /// </summary>
    [Theory]
    [InlineData("source-viewer__layout")]
    [InlineData("source-viewer__outline")]
    [InlineData("source-viewer__resizer")]
    [InlineData("source-viewer__outline-inner")]
    [InlineData("source-viewer__header-actions")]
    [InlineData("source-viewer__compare-picker")]
    public void The_ported_viewer_renders_none_of_the_pre_port_frame_classes(string cls)
    {
        // The trailing lookahead, not \b, so `source-viewer__outline` does not
        // also match the longer names in the family - they are listed here in
        // their own right.
        Read(Viewer).Should().NotMatchRegex($@"\b{Regex.Escape(cls)}(?![\w-])");
        Read(ViewerJs).Should().NotMatchRegex($@"""[^""]*\b{Regex.Escape(cls)}(?![\w-])");
    }

    /// <summary>
    /// A rule kept for markup nobody renders any more is a rule the next reader
    /// has to reason about. Four went with the port; the rest with #562.
    /// </summary>
    [Theory]
    [InlineData("source-viewer__resizer")]
    [InlineData("source-viewer__outline-inner")]
    [InlineData("source-viewer__header-actions")]
    [InlineData("source-viewer__compare-picker")]
    // #562 retired the second viewer, so its whole vocabulary joins them.
    [InlineData("source-viewer__layout")]
    [InlineData("source-viewer__outline")]
    [InlineData("source-viewer__outline-list")]
    [InlineData("source-viewer__find-list")]
    public void The_retired_frame_classes_have_no_rules_left(string cls)
    {
        Selectors(Read(Tools)).Should().NotContain(
            sel => Regex.IsMatch(sel, $@"\.{Regex.Escape(cls)}(?![\w-])"),
            because: $".{cls} is no longer rendered anywhere");
    }

    /// <summary>
    /// A class composed from data is invisible to <c>ComponentCollisionTests</c>,
    /// which reads stylesheets: nothing in any sheet said <c>.page</c> and
    /// <c>.otype</c> ever meet. They met in the markup —
    /// <c>class="otype @@r.Kind.ToLowerInvariant()"</c> — and `.page` in
    /// pages.css is the page-layout container, `display: grid` with
    /// `container-type: inline-size`. That produced two opposite-looking bugs
    /// months apart.
    ///
    /// PR 14c removed the last such call site: the objects grid now spells a
    /// kind with the same <c>.okind</c> badge the explorer tree uses, and
    /// <see cref="ObjectKindGlyph.TintClass"/> returns a whole, already-prefixed
    /// class rather than a word to be concatenated onto one. So the assertion
    /// is no longer "prefix the word" but the stronger "never build a class by
    /// interpolating a kind at all" — checked across every Object Explorer
    /// component, not just the one that had the bug.
    /// </summary>
    [Fact]
    public void No_class_attribute_is_built_by_interpolating_an_object_kind()
    {
        var dir = Path.Combine(Root(), "ALDevToolbox", "Components", "Pages", "ObjectExplorer");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.razor"))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"class=""(?<v>[^""]*)"""))
            {
                var value = m.Groups["v"].Value;
                // A kind reaching a class attribute raw: `@r.Kind`, `@o.Kind`,
                // `@node.ObjectKind`, `.ToLowerInvariant()` on one. A call to
                // ObjectKindGlyph.TintClass is fine - it returns the whole class.
                if (Regex.IsMatch(value, @"@[\w.]*\b(Kind|ObjectKind)\b")
                    && !value.Contains("ObjectKindGlyph."))
                {
                    offenders.Add($"{Path.GetFileName(file)}: class=\"{value}\"");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "a kind is an ordinary English word - `page`, `report`, `query` - "
                   + "and several are already classes in this app. Return a whole "
                   + "prefixed class from ObjectKindGlyph instead of concatenating one.");
    }

    /// <summary>
    /// The grid and the tree have to agree on what a kind looks like, or the
    /// same object reads as two different things one pane apart.
    /// </summary>
    [Fact]
    public void The_objects_grids_spell_a_kind_with_the_same_badge_as_the_tree()
    {
        var cell = Read("ALDevToolbox/Components/Pages/ObjectExplorer/OeKindCell.razor");

        cell.Should().Contain("ObjectKindGlyph.For(",
            because: "the grids take their letters from the same map the tree does");
        cell.Should().MatchRegex(@"class=""okind @ObjectKindGlyph\.TintClass",
            because: "and the same tint, from the design layer's .okind family");

        // The two grids list the same objects one page apart, and they HAD
        // drifted - a tinted pill on the release grid, a bare word on the
        // module grid. Both go through the one component now; neither may
        // grow its own type cell again.
        foreach (var grid in new[] { "OeObjectResults.razor", "OeModuleDetail.razor" })
        {
            Read($"ALDevToolbox/Components/Pages/ObjectExplorer/{grid}")
                .Should().Contain("<OeKindCell Kind=", because: $"{grid} must not spell a kind itself");
        }

        // The tints have to exist where the design layer says they do - if they
        // slid back into tools.css they would be a private copy again.
        Selectors(Read(PagesPower)).Any(sel => sel.Contains(".okind--")).Should().BeTrue();
    }

    /// <summary>
    /// The words a kind badge can be built from, checked against every bare
    /// class the design layer defines. A future kind that happens to match one
    /// would be the same bug again.
    /// </summary>
    [Fact]
    public void No_object_kind_shares_a_name_with_a_design_layer_class()
    {
        var kinds = Regex.Matches(Read(Ranking), @"\[""(?<kind>[a-z]{3,})""\]\s*=\s*""(?<same>[a-z]+)""")
            .Where(m => m.Groups["kind"].Value == m.Groups["same"].Value)
            .Select(m => m.Groups["kind"].Value)
            .Distinct()
            .ToList();
        kinds.Should().NotBeEmpty();

        var designClasses = DesignSheets
            .SelectMany(f => Selectors(Read(f)))
            .SelectMany(sel => Regex.Matches(sel, @"^\.(?<c>[a-z][\w-]*)$").Select(m => m.Groups["c"].Value))
            .ToHashSet();

        // Reported as information, not a failure: the collision only bites if
        // someone emits the kind as a bare class again, which the test above
        // forbids. This one names the words that would bite.
        var overlap = kinds.Where(designClasses.Contains).ToList();
        overlap.Should().NotBeEmpty(
            because: "at least `page` collides today - if this ever empties, "
                   + "the design layer changed and this test has stopped saying anything");
    }

    // ── The tree's two renderers ───────────────────────────────────────

    /// <summary>
    /// The classes a tree row is made of, and the attributes its toggle needs.
    /// Both renderers have to produce all of them or a row opened after page
    /// load behaves differently from one that came with the page.
    /// </summary>
    [Theory]
    [InlineData("otree__row")]
    [InlineData("otree__row--app")]
    [InlineData("otree__caret")]
    [InlineData("otree__ico")]
    [InlineData("otree__name")]
    [InlineData("otree__id")]
    [InlineData("okind")]
    [InlineData("sv-tree-overflow")]
    public void Both_tree_renderers_build_the_same_row(string cls)
    {
        Read(TreeRow).Should().MatchRegex($@"\b{Regex.Escape(cls)}\b",
            because: "OeTreeRow.razor renders the server-side half");
        Read(ViewerJs).Should().MatchRegex($@"\b{Regex.Escape(cls)}\b",
            because: "buildTreeRow renders the lazily-opened half");
    }

    /// <summary>
    /// The toggle contract, spelled two ways: <c>data-tree-module</c> in
    /// markup, <c>dataset.treeModule</c> in script. A row missing one of these
    /// is a caret that does nothing.
    /// </summary>
    [Theory]
    [InlineData("data-tree-toggle", "treeToggle")]
    [InlineData("data-tree-module", "treeModule")]
    [InlineData("data-tree-path", "treePath")]
    [InlineData("data-tree-depth", "treeDepth")]
    public void Both_tree_renderers_set_the_toggle_contract(string attribute, string dataset)
    {
        Read(TreeRow).Should().Contain(attribute);
        Read(ViewerJs).Should().Contain("dataset." + dataset);
    }

    /// <summary>
    /// Three states the two renderers have to agree on, each of which produced
    /// a wrong row when only one of them knew about it: a node with nothing to
    /// open draws no caret, the open file is marked active, and the row that
    /// names what a capped folder left out is not a destination.
    /// </summary>
    [Theory]
    [InlineData("HasChildren", "hasChildren")]
    [InlineData("IsActive", "isActive")]
    public void Both_tree_renderers_read_the_same_node_state(string csharp, string js)
    {
        Read(TreeRow).Should().Contain("Node." + csharp,
            because: "OeTreeRow.razor renders the server-side half");
        Read(ViewerJs).Should().Contain("node." + js,
            because: "buildTreeRow renders the lazily-opened half");
    }

    [Fact]
    public void Both_tree_renderers_draw_the_overflow_row_as_inert()
    {
        Read(TreeRow).Should().Contain("\"overflow\"");
        Read(ViewerJs).Should().Contain("\"overflow\"");
        // A <span>, never an <a> or a <button>: the row names what the tree is
        // not showing, and a hover state that promises navigation is a lie.
        Read(ViewerJs).Should().Contain("createElement(\"span\")");
    }

    [Fact]
    public void Both_tree_renderers_mark_a_folder_as_closed_by_default()
    {
        Read(TreeRow).Should().Contain("aria-expanded");
        Read(ViewerJs).Should().Contain("setAttribute(\"aria-expanded\", \"false\")");
    }

    /// <summary>
    /// The branch leading to the open file arrives with its children already in
    /// the page, so it has to be marked loaded at wire-up. Without that it looks
    /// unloaded to the toggle, and closing then re-opening one of those folders
    /// fetches a second copy of every child and inserts it beside the first —
    /// which is what it did, and which no amount of reading the diff showed.
    /// </summary>
    [Fact]
    public void The_server_rendered_branch_is_marked_loaded_before_the_toggle_can_refetch()
    {
        var js = Read(ViewerJs);
        var wire = js[js.IndexOf("function wireExplorerTree", StringComparison.Ordinal)..];
        var body = wire[..wire.IndexOf("\n}\n", StringComparison.Ordinal)];

        var marks = body.IndexOf("treeLoaded", StringComparison.Ordinal);
        // The click goes on through bindOnce rather than addEventListener
        // (#602) - the listener is bound once for the element's life, not
        // once per navigation.
        var listens = body.IndexOf("bindOnce(tree", StringComparison.Ordinal);
        marks.Should().BeGreaterThan(-1,
            because: "the open branch has to be marked loaded, or re-opening it refetches");
        listens.Should().BeGreaterThan(-1,
            because: "the tree's click handler is what the marking has to beat");
        marks.Should().BeLessThan(listens,
            because: "the marking has to happen before a click can be handled");
        body.Should().Contain("aria-expanded=\"true\"",
            because: "the rows to mark are exactly the ones the server rendered open");
    }

    /// <summary>
    /// Blazor's enhanced navigation re-uses the toolbar button across file
    /// hops, so a listener bound per navigation piles up on the same element:
    /// two handlers ran on one click, the second put back the class the first
    /// had just written, and the Explorer toggle looked dead until a reload
    /// (#602). The handlers live on <c>document</c>, installed once behind a
    /// module-level guard.
    /// </summary>
    [Fact]
    public void The_explorer_toggle_is_wired_once_for_the_session_not_once_per_navigation()
    {
        var js = Read(ViewerJs);
        var wire = js[js.IndexOf("function wireExplorerVisibility", StringComparison.Ordinal)..];
        var body = wire[..wire.IndexOf("\n}\n", StringComparison.Ordinal)];

        body.Should().NotContain("addEventListener",
            because: "per-navigation wiring is exactly what left two listeners on one button");
        js.Should().Contain("if (explorerDelegatesWired) return;",
            because: "the delegated handlers are installed once for the session");
    }

    /// <summary>
    /// Both halves of the explorer's remembered state - folded or not, and how
    /// wide - are rendered by the server from cookies. Held client-side they
    /// were applied after paint, so every navigation flashed the pane open at
    /// its default width before correcting itself (#603). An inline pre-paint
    /// script cannot stand in: hopping between files is an enhanced
    /// navigation, and scripts in a patched response are not executed.
    /// </summary>
    [Fact]
    public void The_explorer_renders_folded_and_at_its_stored_width_server_side()
    {
        var razor = Read(Viewer);
        razor.Should().Contain("aldt-oe-explorer-hidden");
        razor.Should().Contain("aldt-oe-explorer-width");
        razor.Should().Contain("is-explorer-hidden",
            because: "a folded pane has to be folded in the HTML, not a frame later");
        razor.Should().Contain("--oe-left",
            because: "the stored width has to be on the element the response paints");

        var js = Read(ViewerJs);
        js.Should().Contain("EXPLORER_HIDDEN_COOKIE = \"aldt-oe-explorer-hidden\"",
            because: "the writer and the reader have to name the same cookie");
        js.Should().Contain("cookie: \"aldt-oe-explorer-width\"",
            because: "the width the drag stores is the width the server reads back");
    }

    /// <summary>
    /// Every inline SVG the tree builds in JavaScript is a copy of a vendored
    /// Lucide file, because <c>&lt;Icon&gt;</c> is a Razor component the client
    /// renderers cannot reach. A drifted copy draws the wrong glyph next to the
    /// right name. Compared on path data alone — the wrappers differ by size.
    /// </summary>
    [Theory]
    [InlineData("PACKAGE_ICON_SVG", "package")]
    [InlineData("FOLDER_ICON_SVG", "folder")]
    [InlineData("FILE_CODE_ICON_SVG", "file-code")]
    [InlineData("CHEVRON_RIGHT_ICON_SVG", "chevron-right")]
    [InlineData("CHECK_ICON_SVG", "check")]
    [InlineData("SELECT_CARET_ICON_SVG", "chevron-down")]
    public void Inlined_tree_icons_match_the_vendored_svg(string constant, string icon)
    {
        var js = Read(ViewerJs);
        var start = js.IndexOf($"const {constant} =", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var literal = js[start..js.IndexOf(";\n", start, StringComparison.Ordinal)];

        var vendored = Read($"ALDevToolbox/Resources/Icons/{icon}.svg");
        foreach (var d in Regex.Matches(vendored, @"d=""([^""]+)""").Select(m => m.Groups[1].Value))
        {
            literal.Should().Contain(d, because: $"{constant} is a copy of {icon}.svg");
        }
        foreach (var pts in Regex.Matches(vendored, @"points=""([^""]+)""").Select(m => m.Groups[1].Value))
        {
            literal.Should().Contain(pts, because: $"{constant} is a copy of {icon}.svg");
        }
    }

    // ── One alphabet for kinds ─────────────────────────────────────────

    /// <summary>
    /// The badge is the search prefix, uppercased. Read out of both source
    /// files rather than called, so this also fails when someone teaches the
    /// search box a kind and forgets the tree.
    /// </summary>
    [Fact]
    public void Every_tree_badge_is_the_kinds_own_search_prefix()
    {
        var badges = GlyphMap();
        badges.Should().NotBeEmpty();

        // ["te"] = "tableextension"  →  tableextension: TE
        var prefixes = Regex.Matches(Read(Ranking), @"\[""(?<short>[a-z]{1,3})""\]\s*=\s*""(?<kind>[a-z]+)""")
            .Where(m => m.Groups["short"].Value != m.Groups["kind"].Value)
            .ToDictionary(m => m.Groups["kind"].Value, m => m.Groups["short"].Value.ToUpperInvariant());
        prefixes.Should().NotBeEmpty();

        foreach (var (kind, badge) in badges)
        {
            prefixes.Should().ContainKey(kind,
                because: $"the tree draws '{badge}' for {kind}, so the search box must accept it too");
            badge.Should().Be(prefixes[kind],
                because: $"the tree and the search box must spell {kind} the same way");
        }

        foreach (var (kind, prefix) in prefixes)
        {
            badges.Should().ContainKey(kind,
                because: $"the search box accepts '{prefix}:' for {kind}, so the tree should badge it");
        }
    }

    /// <summary>
    /// <c>ObjectKindGlyph</c> is the server's copy and <c>OKIND_GLYPHS</c> the
    /// client's. Same rows or the tree changes appearance as you expand it.
    /// </summary>
    [Fact]
    public void The_client_glyph_table_matches_the_servers()
    {
        JsMap("OKIND_GLYPHS").Should().BeEquivalentTo(GlyphMap());
        JsMap("OKIND_TINTS").Should().BeEquivalentTo(TintMap());
    }

    /// <summary>
    /// A tint is a claim that the kind belongs to one of the four families the
    /// handoff colours. Every tinted kind must have a badge to tint.
    /// </summary>
    [Fact]
    public void Only_a_kind_with_a_badge_carries_a_tint()
    {
        var badges = GlyphMap();
        foreach (var (kind, tint) in TintMap())
        {
            badges.Should().ContainKey(kind, because: $"{kind} is tinted {tint} but draws no badge");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────

    /// <summary>Reads a C# switch-expression arm table out of the source.</summary>
    private static Dictionary<string, string> CsMap(string methodName)
    {
        var src = Read(Glyphs);
        var start = src.IndexOf($"{methodName}(string? kind)", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var body = src[start..src.IndexOf("};", start, StringComparison.Ordinal)];
        var map = new Dictionary<string, string>();
        foreach (Match m in Regex.Matches(body, @"^\s*""(?<kinds>[^""]+(?:""\s*or\s*""[^""]+)*)""\s*=>\s*""(?<val>[^""]*)"",",
                     RegexOptions.Multiline))
        {
            var value = m.Groups["val"].Value;
            if (value.Length == 0) continue;
            foreach (var kind in Regex.Split(m.Groups["kinds"].Value, @"""\s*or\s*"""))
            {
                map[kind] = value;
            }
        }
        return map;
    }

    private static Dictionary<string, string> GlyphMap() => CsMap("For");

    private static Dictionary<string, string> TintMap() => CsMap("TintClass");

    /// <summary>Reads a JS object literal of string values out of the module.</summary>
    private static Dictionary<string, string> JsMap(string name)
    {
        var js = Read(ViewerJs);
        var start = js.IndexOf($"const {name} = {{", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1);
        var body = js[start..js.IndexOf("};", start, StringComparison.Ordinal)];
        return Regex.Matches(body, @"(?<kind>[a-zA-Z]+)\s*:\s*""(?<val>[^""]+)""")
            .ToDictionary(m => m.Groups["kind"].Value, m => m.Groups["val"].Value);
    }

    private static IEnumerable<(string Selector, string Body)> Rules(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            yield return (m.Groups["sel"].Value.Trim(), m.Groups["body"].Value);
        }
    }

    private static IEnumerable<string> Selectors(string css) => Rules(css).Select(r => r.Selector);

    private static string? Rule(string css, string selector) =>
        Rules(css).FirstOrDefault(r => r.Selector.Split(',')
            .Any(s => s.Trim() == selector)).Body;

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
