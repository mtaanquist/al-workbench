using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the class names that are <b>built at runtime from a stem</b>, which
/// no whole-name search can find.
///
/// <c>source-viewer.js</c> writes <c>`cm-diff-${row.kind}`</c> and
/// <c>`oe-diff-overview__mark--${run.kind}`</c>; <c>code-editor.js</c> writes
/// <c>`cm-diff-gutter-${kind}`</c>. Grep for <c>cm-diff-inserted</c> across the
/// repo and it appears in exactly one place — the stylesheet — which is what a
/// dead class looks like. The PR 17a dead sweep would have deleted 15 of these
/// and taken the colour out of every diff on the branch; this test is the
/// reason a future sweep does not.
///
/// The vocabularies are read from the <b>producing</b> code rather than
/// restated here, so adding a fifth diff kind fails this
/// test instead of shipping an unstyled one.
/// </summary>
public sealed class ComposedClassNameTests
{
    private static readonly string[] Sheets =
    [
        "ALDevToolbox/wwwroot/code-editor.css",
        "ALDevToolbox/wwwroot/source-viewer.css",
        "ALDevToolbox/wwwroot/app.css",
        "ALDevToolbox/wwwroot/components.css",
        "ALDevToolbox/wwwroot/pages-power.css",
    ];

    [Fact]
    public void Every_diff_kind_the_serializer_emits_is_painted()
    {
        var kinds = DiffKinds();
        kinds.Should().BeEquivalentTo(["inserted", "deleted", "modified"],
            because: "MapKind names four, but SerializeSide drops Unchanged and Imaginary "
                     + "before they reach the client - an imaginary row arrives as a filler "
                     + "widget, not as a decorated line");

        foreach (var kind in kinds)
        {
            // The line tint, the gutter mark and the overview rail each build
            // their own name off the same kind, and each is a separate way for
            // one state to go unpainted.
            Styled($"cm-diff-{kind}").Should().BeTrue(
                because: $"source-viewer.js tints lines with `cm-diff-${{kind}}` and {kind} is a kind");
            Styled($"cm-diff-gutter-{kind}").Should().BeTrue(
                because: $"code-editor.js marks the gutter with `cm-diff-gutter-${{kind}}`");
            Styled($"oe-diff-overview__mark--{kind}").Should().BeTrue(
                because: $"source-viewer.js builds the overview rail with `oe-diff-overview__mark--${{kind}}`");
        }
    }

    /// <summary>
    /// CodeMirror puts these in the DOM itself, so they appear in no file we
    /// wrote and read as dead to any search over our own source. They are what
    /// makes the find-in-file panel look like the rest of the app.
    /// </summary>
    [Theory]
    [InlineData("cm-panels")]
    [InlineData("cm-panel")]
    [InlineData("cm-search")]
    [InlineData("cm-textfield")]
    [InlineData("cm-button")]
    public void Library_emitted_chrome_is_still_styled(string cls) =>
        Styled(cls).Should().BeTrue(
            because: $"CodeMirror renders .{cls}; nothing in our source names it");

    /// <summary>
    /// The kinds that actually reach the browser: everything <c>MapKind</c>
    /// names, minus the ones <c>SerializeSide</c>'s guard clause skips. Read
    /// from the guard rather than restated here, so widening it to emit
    /// Imaginary lines fails this test until something paints them.
    /// </summary>
    private static string[] DiffKinds()
    {
        var source = Read("ALDevToolbox/Services/Diff/SideBySideDiffSerializer.cs");
        var skipped = Regex.Match(source, @"if \(line\.Type is ([^)]+)\) continue;").Groups[1].Value;
        skipped.Should().Contain("Imaginary", because: "SerializeSide is where the kinds are filtered");
        return Regex.Matches(source, @"ChangeType\.(\w+)\s*=>\s*""(\w+)""")
            .Where(m => !skipped.Contains($"ChangeType.{m.Groups[1].Value}", StringComparison.Ordinal))
            .Select(m => m.Groups[2].Value)
            .Where(k => k != "unchanged")
            .Distinct()
            .ToArray();
    }

    /// <summary>True when some shared sheet has a rule naming this class.</summary>
    private static bool Styled(string cls) => Sheets.Any(sheet =>
    {
        var stripped = Regex.Replace(Read(sheet), @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Matches(stripped, @"([^{}]+)\{")
            .Any(m => Regex.IsMatch(m.Groups[1].Value, $@"\.{Regex.Escape(cls)}(?![\w-])"));
    });

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        dir.Should().NotBeNull(because: "the tests run from inside the repo");
        return dir!.FullName;
    }
}
