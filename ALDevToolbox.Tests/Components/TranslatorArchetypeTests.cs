using System.Text.RegularExpressions;
using ALDevToolbox.Components.Shared;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Translator's port onto page archetype 9 (the translation grid).
///
/// The page cannot be rendered in bUnit past its first screen -- it only has
/// units once JS has read a file off disk and handed the parse back -- so these
/// read the sources instead. Each one guards a join that has no compiler and no
/// runtime error behind it: the CSS class the JS goes looking for, the state
/// name the keyline rule is written against, the sheet the archetype lives in.
/// </summary>
public sealed class TranslatorArchetypeTests
{
    private static string Razor() => Read("ALDevToolbox/Components/Pages/Translator.razor");
    private static string Js() => Read("ALDevToolbox/Components/Pages/Translator.razor.js");
    private static string ScopedCss() => Read("ALDevToolbox/Components/Pages/Translator.razor.css");
    /// <summary>
    /// The power sheet with runs of whitespace collapsed. Its selectors are
    /// column-aligned for reading ('.trow.is-fuzzy        .trow__edge'), and a
    /// test that cares about the shape of a selector should not also care about
    /// how it was laid out.
    /// </summary>
    private static string PowerSheet() =>
        Regex.Replace(Read("ALDevToolbox/wwwroot/pages-power.css"), @"[ \t]+", " ");

    /// <summary>
    /// The four XLIFF buckets, spelled the way the design system spells them.
    /// The page's own tokens keep the XLIFF spelling because those get written
    /// back into the file; these are what the CSS and the glyph agree on.
    /// </summary>
    private static readonly string[] States = ["untranslated", "fuzzy", "translated", "final"];

    /// <summary>
    /// The grid declares its columns three times -- the track list in
    /// <c>--tg-cols</c>, the header cells, and the cells of each row -- and
    /// nothing checks they agree. Get one wrong and every column after it
    /// shifts by one, silently.
    ///
    /// Only the head is counted against the track list. A row's cells cannot be
    /// counted from the source without parsing Razor: the target cell lives in
    /// an <c>@if</c>/<c>@else</c> pair, and the editing row nests a
    /// <c>trow__editmeta</c> inside one of its tracks, so both "count every
    /// trow__ span" and "count the shallowest ones" get a different wrong
    /// answer. What the rows are checked for instead is the thing that actually
    /// broke: a cell added to one variant and not the other.
    /// </summary>
    [Fact]
    public void The_head_declares_one_cell_per_grid_track()
    {
        var tracks = Regex.Match(ScopedCss(), @"--tg-cols:\s*([^;]+);").Groups[1].Value;
        tracks.Should().NotBeEmpty(because: "the page overrides the handoff's track list");

        // minmax(0, 1fr) is ONE track but contains a space, so collapse any
        // parenthesised function to a single token before splitting.
        var flat = Regex.Replace(tracks, @"\w+\([^()]*\)", "T");
        var trackCount = flat.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        var head = Regex.Match(Razor(), @"<div class=""tgrid__head"">(.*?)</div>",
            RegexOptions.Singleline).Groups[1].Value;
        // The sr-only label inside the actions header is content, not a cell.
        var headCells = Regex.Matches(head, @"(?m)^\s*<span(?: class=""tgrid__h"")?>").Count;

        headCells.Should().Be(trackCount,
            because: "one header cell per track, or every label sits over the wrong column");
    }

    /// <summary>
    /// The resting row and the editing row are separate blocks of markup laid
    /// out on the same track list, so a cell added to one and not the other
    /// shifts that row's columns on its own. PR 18e added the actions cell,
    /// which is exactly that shape of change.
    /// </summary>
    [Theory]
    [InlineData("trow__edge")]
    [InlineData("trow__key")]
    [InlineData("trow__src")]
    [InlineData("trow__st")]
    [InlineData("trow__kind")]
    [InlineData("trow__acts")]
    public void Both_row_variants_declare_the_same_cells(string cell)
    {
        var variants = RowVariants(Razor()).ToList();
        // Without this the theory passes by iterating nothing, which is how a
        // source-reading test rots into decoration.
        variants.Select(v => v.Name).Should().BeEquivalentTo(["resting", "editing"],
            because: "the page renders a .trow two ways and both are checked here");

        foreach (var (name, block) in variants)
        {
            block.Should().Contain($@"class=""{cell}""",
                because: $"the {name} row shares a track list with the other one");
        }
    }

    /// <summary>
    /// Clearing a target has to move the unit's state too (#560). An empty
    /// target IS untranslated, so leaving the state alone produces a row
    /// reading "Translated" with nothing in it -- and one the To-do filter
    /// hides, so the user could not even find it again.
    /// </summary>
    [Fact]
    public void Clearing_a_target_puts_the_unit_back_in_the_todo_bucket()
    {
        var body = Regex.Match(Razor(), @"private void ClearTarget\(UnitVm u\)\s*\{(.*?)\n    \}",
            RegexOptions.Singleline).Groups[1].Value;
        body.Should().NotBeEmpty(because: "the row action's handler is what the button calls");
        body.Should().Contain("u.Target = string.Empty", because: "that is the point of the action");
        body.Should().Contain("u.State = StTodo",
            because: "an empty target is untranslated; leaving the state hides the row behind the To-do filter");
        body.Should().Contain("u.Dirty = true", because: "only dirty units are written back on export");
        body.Should().Contain("RecomputeFiltered()", because: "the row's bucket changed, so the filtered view has to");
    }

    /// <summary>The markup of each .trow variant: the resting row and the editing row.</summary>
    private static IEnumerable<(string Name, string Block)> RowVariants(string razor)
    {
        foreach (Match m in Regex.Matches(razor,
                     @"<div class=""trow[ @""].*?(?=\n\s*</div>)", RegexOptions.Singleline))
        {
            yield return (m.Value.Contains("trow--editing") ? "editing" : "resting", m.Value);
        }
    }

    [Theory]
    [InlineData("untranslated")]
    [InlineData("fuzzy")]
    [InlineData("translated")]
    [InlineData("final")]
    public void Every_xliff_state_has_a_keyline_rule_in_the_power_sheet(string state)
    {
        // RowStateIcon owns the mapping from a state word to the row class. If
        // the sheet has no rule for the class it hands back, the row's leading
        // 3px keyline silently falls through to --bar-unchanged and every unit
        // looks the same.
        var rowClass = RowStateIcon.RowClass(state);
        rowClass.Should().Be("is-" + state);

        PowerSheet().Should().Contain($".trow.{rowClass} .trow__edge",
            "the keyline for {0} has no rule, so the row would render grey", state);
    }

    [Fact]
    public void The_status_glyph_is_tinted_for_every_state()
    {
        // components.css tints RowStateIcon from selectors keyed on `tr.is-*`.
        // A .trow is a grid, not a <tr>, so the power sheet carries its own
        // twin of those rules -- without them the glyph in the state column is
        // grey on every row while the keyline beside it is coloured.
        foreach (var state in States)
        {
            PowerSheet().Should().Contain($".trow.is-{state} .trow__st .data-table__state",
                "the {0} glyph would render grey next to a coloured keyline", state);
        }
    }

    [Fact]
    public void The_page_does_not_declare_its_own_state_colours()
    {
        // What the port fixed: the old scoped sheet declared --st-todo /
        // --st-review / --st-trans / --st-final on its root element, which
        // shadowed the design system's identically-named tokens for the whole
        // subtree. The states belong to tokens.css now.
        var css = ScopedCss();
        Regex.Match(css, @"--st-[a-z]+\s*:").Success.Should().BeFalse(
            "the four XLIFF ramps live in tokens.css; re-declaring one here " +
            "shadows it for everything inside the page");
    }

    [Fact]
    public void The_loaded_view_is_a_power_tool_and_the_first_run_screen_is_not()
    {
        var razor = Razor();

        razor.Should().Contain("""<div class="pw u-compact">""",
            "the loaded file is archetype 9: it fills the shell and scrolls inside its panes");

        // .app__content:has(.pw) strips the shell's padding for the whole content
        // column. Wearing .pw before there is a file to show would land the user
        // on a drop target floating in an unpadded viewport.
        var firstRun = razor[..razor.IndexOf("""<div class="pw u-compact">""", StringComparison.Ordinal)];
        firstRun.Should().Contain("""<div class="page">""");
        firstRun.Should().NotContain("\"pw ");
    }

    [Fact]
    public void The_grid_widens_the_key_column_it_inherits()
    {
        // The handoff sizes the key column at 84px, which fits a short key. A BC
        // XLIFF id is "Codeunit 1465371914 - NamedType 1138880009" and at 84px
        // every row read "Codeunit ...". --tg-cols is declared on .tgrid exactly
        // so a page can re-declare it.
        ScopedCss().Should().MatchRegex(@"\.tgrid\s*\{[^}]*--tg-cols:",
            "the inherited 84px key column shows nothing but the object type");

        // Widening alone was not enough, so the cell renders GridKey() rather
        // than the raw id, and keeps the raw id on the title for anyone who
        // needs to find the unit in the file.
        Razor().Should().Contain("""<span class="trow__key" title="@u.Short">@GridKey(u.Short)</span>""",
            "the grid shows the shortened key and keeps the full one on the title");
    }

    [Fact]
    public void The_filter_and_its_badge_are_derived_from_the_same_predicate()
    {
        // The fresh-eyes review's first finding: the "Needs translation" tab
        // showed a badge of 88 and filtered to 175 rows, because the badge came
        // from Counts() (which excluded units needing review) and the filter
        // came from IsNeeding (which included them). Two derivations of "which
        // bucket is this unit in", quietly disagreeing.
        //
        // There is one now -- DesignState -- and it is the same one that picks
        // the row's keyline class and its status glyph, so the badge, the rows,
        // the colour and the icon cannot come apart. This asserts the wiring
        // rather than the arithmetic: the arithmetic is verified by driving the
        // page, but nothing else would notice a future edit reintroducing a
        // second predicate.
        var razor = Razor();

        foreach (var arm in new[] { "todo", "review", "done" })
        {
            var line = razor.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith($@"""{arm}"" =>"));
            line.Should().NotBeNull("the {0} filter arm should still be there to check", arm);
            line!.Should().Contain("DesignState",
                "the {0} filter must bucket units the same way its badge counts them", arm);
        }

        Body(razor, "private CountSet Counts()").Should().Contain("DesignState",
            "the badges must count the same buckets the filters select");
    }

    /// <summary>
    /// The body of a member, from its signature to the blank line before the
    /// next one. Brace-counting by regex does not survive a nested switch, and
    /// the point here is only which helper the body calls.
    /// </summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "{0} should still be there to check", signature);

        var end = source.IndexOf("\n\n", start + signature.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    [Fact]
    public void The_state_picker_offers_all_four_states()
    {
        // It offered three. A unit could arrive needing review and be moved out
        // of that state but never into it, and -- worse -- the picker doubles as
        // the legend for the row glyphs, so the state on roughly half the rows
        // had no name anywhere the user could reach.
        var options = Regex.Match(Razor(), @"StatePickOptions =\s*\{(?<body>[^}]*)\}");
        options.Success.Should().BeTrue();

        foreach (var token in new[] { "StTodo", "StReview", "StTranslated", "StFinal" })
        {
            options.Groups["body"].Value.Should().Contain(token,
                "{0} has no button, so the picker cannot legend the glyph that uses it", token);
        }
    }

    [Fact]
    public void Every_selector_the_javascript_hunts_for_exists_in_the_markup()
    {
        // These cross the C#/JS line as string literals. Rename one side and the
        // file picker, the drop zone or the rail resizer stops working with no
        // error at all -- initPicker returns early on a null querySelector.
        var razor = Razor();
        var js = Js();

        foreach (var selector in Regex.Matches(razor, @"InvokeVoidAsync\(""(?:initDropZone|initPicker)"",\s*(""[^""]+""(?:,\s*""[^""]+"")?)")
                     .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""\.([\w-]+)""").Select(x => x.Groups[1].Value)))
        {
            razor.Should().MatchRegex($@"class=""[^""]*\b{Regex.Escape(selector)}\b",
                "JS looks for .{0} and no element carries it", selector);
        }

        // The resizer finds its handle by attribute, not by class.
        js.Should().Contain("[data-tr-split]");
        razor.Should().Contain("data-tr-split");

        // And it toggles this class on the drop panel.
        js.Should().Contain("""classList.add("is-over")""");
        ScopedCss().Should().Contain(".tdrop.is-over");
    }

    [Fact]
    public void Classes_that_land_on_a_child_component_are_styled_through_deep()
    {
        // Blazor stamps the scope attribute on the elements a component renders
        // ITSELF. A class handed to a child component -- <InputFile class="x">,
        // <Icon Css="x"> -- ends up on markup that child owns, so a plain `.x`
        // rule in this page's scoped sheet compiles to `.x[b-thispage]` and
        // matches nothing. No error, no warning: the rule is in the file, the
        // file is served, and the element is simply unstyled.
        //
        // Found by shooting the drop panel on a browser without the File System
        // Access API, where the file input's "Choose File / No file chosen"
        // chrome was sitting in the middle of the page. The rule meant to hide
        // it had been there, unmatched, since before this port.
        var razor = Razor();
        var css = ScopedCss();

        var onChildComponents = Regex.Matches(razor, @"<[A-Z]\w*\b[^>]*?\b(?:class|Css)=""([^""]+)""")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Distinct();

        foreach (var cls in onChildComponents)
        {
            foreach (var selector in Selectors(css).Where(sel => Regex.IsMatch(sel, $@"\.{Regex.Escape(cls)}\b")))
            {
                selector.Should().Contain("::deep",
                    ".{0} sits on markup a child component renders, so this rule " +
                    "carries the wrong scope attribute and styles nothing", cls);
            }
        }
    }

    /// <summary>
    /// The selector of every rule in a sheet. Comments come out first: they sit
    /// between the previous rule and this one, so leaving them in makes each
    /// selector carry the prose above it -- and the first version of the test
    /// above passed on a broken sheet because the comment explaining ::deep was
    /// being read as part of the selector that had lost it.
    /// </summary>
    private static IEnumerable<string> Selectors(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Matches(stripped, @"([^{}]+)\{")
            .Select(m => Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim())
            .Where(sel => sel.Length > 0);
    }

    [Fact]
    public void Saving_in_place_does_not_name_the_renamed_file()
    {
        // Two controls sat next to each other saying contradictory things: a
        // pencil offering to rename "the file you export", and a Save button
        // writing back to "the file you opened". saveInPlace writes to the
        // handle, whose name on disk the rename never touches, so a toast built
        // from _fileName named a file that does not exist.
        var body = Body(Razor(), "private async Task SaveInPlaceAsync(");

        body.Should().NotContain("Saved to {_fileName}",
            "the in-place path writes to the handle, not to the name the rename box holds");
    }

    /// <summary>
    /// #1268 (L44): saveInPlace used to write back the session record it read
    /// before the request, overwriting edits persisted while the save was in
    /// flight, and the page then dropped the unsaved guard for them too. The
    /// record is read again before the write, and the guard only drops when the
    /// overlay that was saved is still the current one.
    /// </summary>
    [Fact]
    public void An_in_place_save_does_not_overwrite_edits_made_while_it_ran()
    {
        var js = Js();
        var save = js[js.IndexOf("export async function saveInPlace(", StringComparison.Ordinal)..];
        save = save[..save.IndexOf("\n}\n", StringComparison.Ordinal)];
        var write = save.IndexOf("handle.createWritable()", StringComparison.Ordinal);
        var reread = save.IndexOf("await getRec()", write, StringComparison.Ordinal);
        reread.Should().BeGreaterThan(write, "the record is read again after the file is written");
        save.Should().NotContain("putRec(rec)", "the record read before the save is stale by the time it ends");

        Body(Razor(), "private async Task SaveInPlaceAsync(")
            .Should().Contain("SerializeEdits() == editsJson",
                "the unsaved guard drops only when nothing changed during the save");
    }

    [Fact]
    public void The_focus_helper_still_points_at_the_target_box()
    {
        // focusTarget() puts the caret in the rail's textarea after Alt+Down.
        // It is a querySelector, so a class rename makes keyboard navigation
        // land nowhere and say nothing.
        Js().Should().Contain("""querySelector(".tr-tgtarea")""");
        Razor().Should().MatchRegex(@"class=""[^""]*\btr-tgtarea\b");
    }

    private static string Read(string relative)
    {
        var dir = RepoRoot.Directory;

        dir.Should().NotBeNull("could not locate repo root");
        return File.ReadAllText(Path.Combine(dir!.FullName, relative.Replace('/', Path.DirectorySeparatorChar)));
    }
}
