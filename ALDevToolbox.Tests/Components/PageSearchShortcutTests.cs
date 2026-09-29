using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// F3 puts the cursor in the page's search box (#902). The key is handled once, in
/// <c>wwwroot/search-shortcut.js</c>, and it finds the box by a marker in the markup:
/// <c>data-page-search</c> on the input, or <c>data-page-search-host</c> on
/// <c>FilterBar</c>'s wrapper. A box without either is a box F3 silently skips - the
/// same failure the release page's own F3 handler had when its selector stopped
/// matching, and nothing noticed. These tests are what notices now.
/// </summary>
public sealed class PageSearchShortcutTests
{
    /// <summary>
    /// An <c>input</c> start tag, quotes respected so a <c>&gt;</c> inside an attribute
    /// value does not end it.
    /// </summary>
    private static readonly Regex InputTag = new(
        """<input\b(?:[^>"']|"[^"]*"|'[^']*')*>""", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SearchSlot = new(
        @"<Search>.*?</Search>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Search boxes F3 deliberately does not reach, by file and a fragment of the tag
    /// that identifies the box. Each says why. Do not add to it without the same.
    /// </summary>
    private static readonly IReadOnlyDictionary<(string File, string Fragment), string> NotThePageSearch =
        new Dictionary<(string, string), string>
        {
            [("ALDevToolbox/Components/Pages/ObjectExplorer/OeReleaseDetail.razor", "aria-label=\"Filter by namespace\"")] =
                "a field inside the Options panel, not the page's search; the page's box is #oe-release-search",
            [("ALDevToolbox/Components/Pages/Upgrades/UpgradePicker.razor", "aria-label=\"Search the environments to add\"")] =
                "the search inside the Add environments dialog, not the page's; the upgrade page's own box carries data-page-search",
        };

    [Fact]
    public void Every_search_box_is_reachable_with_F3()
    {
        var offenders = SearchBoxes()
            .Where(b => !b.Hosted && !b.Tag.Contains("data-page-search", StringComparison.Ordinal))
            .Where(b => !NotThePageSearch.Keys.Any(k => k.File == b.File && b.Tag.Contains(k.Fragment, StringComparison.Ordinal)))
            .Select(b => b.File)
            .ToList();

        offenders.Should().BeEmpty(
            "F3 finds a page's search box by a marker: put the box in a ListPage <Search> slot, or give the " +
            "input data-page-search. If it is not the page's search, list it in NotThePageSearch with the reason");
    }

    [Fact]
    public void Every_box_F3_reaches_says_so()
    {
        var offenders = SearchBoxes()
            .Where(b => b.Hosted || b.Tag.Contains("data-page-search", StringComparison.Ordinal))
            .Where(b => !Regex.IsMatch(b.Tag, """aria-keyshortcuts="(?:[^"]*\s)?F3(?:\s|")"""))
            .Select(b => b.File)
            .ToList();

        offenders.Should().BeEmpty(
            "a box F3 reaches carries aria-keyshortcuts=\"F3\" (and \"(F3)\" in its title), so the shortcut is " +
            "announced and discoverable - FilterBar cannot add it to the page's own input, so the page does");
    }

    [Fact]
    public void Every_exception_still_names_a_box()
    {
        var boxes = SearchBoxes();
        var stale = NotThePageSearch.Keys
            .Where(k => !boxes.Any(b => b.File == k.File && b.Tag.Contains(k.Fragment, StringComparison.Ordinal)))
            .Select(k => k.File)
            .ToList();

        stale.Should().BeEmpty("the exception list stays honest: drop entries whose box has gone");
    }

    [Fact]
    public void FilterBar_marks_its_search_for_F3()
    {
        var filterBar = File.ReadAllText(Path.Combine(Root(), "ALDevToolbox", "Components", "Shared", "Archetypes", "FilterBar.razor"));
        var script = File.ReadAllText(Path.Combine(Root(), "ALDevToolbox", "wwwroot", "search-shortcut.js"));

        Regex.Matches(filterBar, @"class=""search filter-bar__search"" data-page-search-host").Count
            .Should().Be(2, "both of FilterBar's search wrappers (with and without the GET form) are F3's host");
        script.Should().Contain("[data-page-search]").And.Contain("[data-page-search-host] input");
    }

    /// <summary>
    /// Every search box in the app's markup: <c>type="search"</c>, plus any input already
    /// marked (the Translator's and the translation memory's boxes are plain text inputs).
    /// </summary>
    private static List<(string File, string Tag, bool Hosted)> SearchBoxes()
    {
        var root = Root();
        var boxes = new List<(string, string, bool)>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "ALDevToolbox", "Components"), "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("ALDevToolbox/Components/Shared/Archetypes/", StringComparison.Ordinal)) continue;

            var markup = Regex.Replace(File.ReadAllText(path), @"@\*.*?\*@", "", RegexOptions.Singleline);
            var slots = SearchSlot.Matches(markup).Select(m => (m.Index, End: m.Index + m.Length)).ToList();
            foreach (Match tag in InputTag.Matches(markup))
            {
                var isSearch = tag.Value.Contains("type=\"search\"", StringComparison.Ordinal)
                               || tag.Value.Contains("data-page-search", StringComparison.Ordinal);
                if (!isSearch) continue;
                var hosted = slots.Any(s => tag.Index > s.Index && tag.Index < s.End);
                boxes.Add((relative, tag.Value, hosted));
            }
        }
        return boxes;
    }

    private static string Root() =>
        ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory?.FullName
        ?? throw new InvalidOperationException("Could not locate the repository root.");
}
