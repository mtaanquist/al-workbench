using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Catches the quiet half of a design-system port: markup that names a class no
/// stylesheet defines. Nothing errors, nothing logs, the build is green and the
/// element just renders with browser defaults — which on a caption or a hint
/// looks close enough to right that it survives a screenshot.
///
/// PR 17c produced two in one sitting. <c>.hint</c> was swapped in for
/// <c>.muted</c> because a grep for <c>\.hint\b</c> matched <c>.hint-details</c>
/// (a hyphen is a word boundary); <c>.file-row</c> was written as a hook and
/// then never given a rule. Both are invisible unless something walks the set.
///
/// The reverse direction — a rule with no caller — is the dead-CSS sweep's job
/// and is checked by measuring rather than asserted here, because a component
/// may legitimately ship a state the app has not reached yet.
/// </summary>
public sealed class UnstyledMarkupTests
{
    private static readonly string[] Sheets =
    [
        "tokens.css", "components.css", "shell.css", "pages.css", "pages-forms.css",
        "pages-power.css", "pages-content.css", "fonts.css",
        "app.css", "command-palette.css", "code-editor.css", "source-viewer.css", ];

    [Fact]
    public void Every_class_the_markup_names_is_defined_by_some_stylesheet()
    {
        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sheet in Sheets)
        {
            defined.UnionWith(SelectorClasses(File.ReadAllText(Path.Combine(Wwwroot(), sheet))));
        }
        // Scoped sheets count: a component may keep its own layout private.
        foreach (var scoped in Directory.EnumerateFiles(Components(), "*.razor.css", SearchOption.AllDirectories))
        {
            defined.UnionWith(SelectorClasses(File.ReadAllText(scoped)));
        }

        var offenders = new List<string>();
        foreach (var razor in Directory.EnumerateFiles(Components(), "*.razor", SearchOption.AllDirectories))
        {
            foreach (var cls in LiteralClasses(File.ReadAllText(razor)))
            {
                if (!defined.Contains(cls) && !Unstyled.Contains(cls))
                {
                    offenders.Add($"{Path.GetFileName(razor)}: .{cls}");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "a class with no rule renders as an unstyled element, which nothing reports");
    }

    /// <summary>
    /// <c>.select</c> sets <c>appearance: none</c>, so the browser's own arrow
    /// is gone and the only thing left to say "this opens a list" is the caret
    /// <c>SelectBox</c> draws beside it. A <c>.select</c> outside one is a text
    /// box that mysteriously refuses to be typed in.
    ///
    /// Nothing errors and the control still works, which is why this is a test:
    /// PR 17c shipped four of them past a screenshot before anyone noticed the
    /// arrows were missing. The wrapper is a component now (#842), so what is
    /// left to check is that every select is inside it, and that nobody writes
    /// the wrapper by hand again.
    /// </summary>
    [Fact]
    public void Every_styled_select_sits_inside_a_SelectBox()
    {
        var offenders = new List<string>();
        foreach (var razor in Directory.EnumerateFiles(Components(), "*.razor", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(razor);
            if (!razor.EndsWith(Path.Combine("Shared", "SelectBox.razor"), StringComparison.Ordinal)
                && Regex.IsMatch(markup, @"class=""[^""]*\bselect-wrap\b"))
            {
                offenders.Add($"{Path.GetFileName(razor)}: a hand-written .select-wrap; use <SelectBox>");
            }

            foreach (Match m in Regex.Matches(markup, @"<select\b[^>]*class=""[^""]*\bselect\b[^""]*"""))
            {
                var before = markup[..m.Index];
                var open = before.LastIndexOf("<SelectBox", StringComparison.Ordinal);
                var close = before.LastIndexOf("</SelectBox>", StringComparison.Ordinal);
                if (open < 0 || open < close)
                {
                    offenders.Add($"{Path.GetFileName(razor)}: a .select outside a <SelectBox>");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: ".select removes the native arrow, so the caret SelectBox draws is the only affordance left");
    }

    /// <summary>
    /// Names that appear inside a <c>class="…"</c> and are deliberately not CSS.
    /// Keep it short and say why for each group: the default is that a name in a
    /// class attribute is a name somebody meant to style.
    /// </summary>
    private static readonly HashSet<string> Unstyled = new(StringComparer.Ordinal)
    {
        // Behaviour hooks the client scripts query for. Each is on an element
        // that a sibling class already styles.
        "sv-row", "sv-filter", "sv-tree-search", "sv-section__toggle",
        "source-viewer__find-host", "source-viewer__tab", "source-viewer__tab-count",
        "source-viewer--inline", "object-explorer", "oe-hero", "oe-release",
        "cb-card", "field__value", "piper-page", "icon-missing", "diff__ln--",
        "fdrop--options", "build-pill--", "rp-app__state--", "run-row--",
        "status-pill--", "tok-", "folder-editor__row--depth-",
        // State words the markup toggles, always compounded onto a real class.
        "active", "collapsed", "gone", "is", "isChecked", "isCurrent", "isProd",
        "open", "over", "passed", "picked", "selected", "state", "status", "tab",
        "not", "null", "key", "node", "path", "scope", "r", "u", "cssClass",
        "defaultTab", "row", "rowState", "line", "env",
    };

    /// <summary>
    /// Class names written as literals in a <c>class</c> attribute. Razor
    /// expressions inside the attribute contribute their bare identifiers too,
    /// which is why the allow-list carries a handful of C# locals.
    /// </summary>
    private static IEnumerable<string> LiteralClasses(string razor)
    {
        foreach (Match m in Regex.Matches(razor, @"class=""([^""]*)"""))
        {
            foreach (var token in Regex.Split(m.Groups[1].Value, @"[\s@()?:""]+"))
            {
                if (token.Length > 0 && Regex.IsMatch(token, @"\A[A-Za-z][A-Za-z0-9_-]*\z")
                    // PascalCase is a C# member, never one of our class names.
                    && !char.IsUpper(token[0]))
                {
                    yield return token;
                }
            }
        }
    }

    private static HashSet<string> SelectorClasses(string css)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var selectors = string.Join(" ", Regex.Matches(stripped, @"([^{}]+)\{").Select(m => m.Groups[1].Value));
        return new HashSet<string>(
            Regex.Matches(selectors, @"\.([A-Za-z][A-Za-z0-9_-]*)").Select(m => m.Groups[1].Value),
            StringComparer.Ordinal);
    }

    private static string Wwwroot() => Path.Combine(Root(), "ALDevToolbox", "wwwroot");
    private static string Components() => Path.Combine(Root(), "ALDevToolbox", "Components");

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        dir.Should().NotBeNull(because: "the tests run from inside the repo");
        return dir!.FullName;
    }
}
