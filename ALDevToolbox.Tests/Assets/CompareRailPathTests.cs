using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards the change rail's path truncation (#588). CSS gives truncation for
/// free and cuts wherever the width lands, which on a rail of BC paths produced
/// rows reading <c>…US Discount Mgt.Codeunit.al</c> — the tail of CRONUS with
/// its head bitten off. A reader's eye tries to parse a fragment that is not a
/// word, and two neighbouring rows can differ only inside the part that was cut.
///
/// Cutting on a folder boundary needs the text measured, so this runs on the
/// client, and that puts three failure modes in play that a screenshot of one
/// rail at one width will not show.
///
/// <b>A fit is only right at the width it was computed for.</b> The rail is
/// drag-resizable, so a one-shot fit is wrong the moment anyone touches the
/// handle — and wrong in the silent direction, since a too-short string still
/// renders neatly.
///
/// <b>A hidden row measures zero.</b> The filter hides rows rather than
/// removing them, and fitting a path to no width would replace every hidden
/// row's text with a bare ellipsis it never recovers from.
///
/// <b>The stylesheet truncates with <c>direction: rtl</c>.</b> That is what
/// keeps the filename end when CSS does the cutting, and it puts a leading
/// ellipsis on the far side of the row when JS has already done it.
/// </summary>
public sealed class CompareRailPathTests
{
    private const string ViewerJs = "ALDevToolbox/wwwroot/source-viewer.js";
    private const string PagesPower = "ALDevToolbox/wwwroot/pages-power.css";

    [Fact]
    public void The_fit_is_recomputed_when_the_rail_changes_width()
    {
        var js = Code(Read(ViewerJs));

        js.Should().Contain("new ResizeObserver(schedule).observe(rail)",
            because: "the rail is drag-resizable and a fit computed at one width is wrong at every other");
        js.Should().Contain("requestAnimationFrame(fitRailPaths)",
            because: "a resize drag fires continuously; measuring text on every event is a lot of layout");
    }

    [Fact]
    public void A_row_the_filter_has_hidden_is_left_alone()
    {
        var fit = Between(Read(ViewerJs), "function fitRailPaths()", "\n}\n");

        Code(fit).Should().Contain("if (width === 0) continue;",
            because: "a hidden row measures zero and would be fitted down to a bare ellipsis");
        Code(Read(ViewerJs)).Should().Contain("__refitPaths?.()",
            because: "the rows the filter puts back have never been measured");
    }

    /// <summary>
    /// Opening a file compare froze the browser for seconds: the fit read each
    /// row's width straight after writing the previous row's text, so every row
    /// forced a synchronous layout of the whole page, both editors included.
    /// With the rail at its 500-row cap that was 500 layouts in one task. The
    /// widths have to be read in one pass before anything is written.
    /// </summary>
    [Fact]
    public void Every_width_is_read_before_any_row_is_rewritten()
    {
        var fit = Code(Between(Read(ViewerJs), "function fitRailPaths()", "\n}\n"));

        const string batchRead = "Array.from(names, el => el.clientWidth)";
        var read = fit.IndexOf(batchRead, StringComparison.Ordinal);
        read.Should().BeGreaterThan(-1, "the widths are measured in one batch");
        var write = fit.IndexOf("el.textContent = fitted;", StringComparison.Ordinal);
        write.Should().BeGreaterThan(read, "every read has to come before the first write");
        fit[(read + batchRead.Length)..].Should().NotContain("clientWidth",
            because: "a layout read between writes forces a layout per row");
    }

    /// <summary>
    /// The stylesheet's <c>direction: rtl</c> is the CSS truncation strategy.
    /// It has to stay for the case JS hands back — and has to be overridden for
    /// the case JS handles, or the ellipsis it just wrote lands on the wrong end.
    /// </summary>
    [Fact]
    public void Direction_is_handed_back_to_css_only_when_js_did_not_cut()
    {
        Read(PagesPower).Should().Contain("direction: rtl",
            because: "it is still the fallback when even a filename does not fit");

        Code(Read(ViewerJs)).Should()
            .Contain(@"el.style.direction = fitted === full ? """" : ""ltr"";");
    }

    /// <summary>
    /// The rule the issue is actually about: never leave a broken word at the
    /// front. A path cuts at a "/" so the reader sees a path with its head
    /// dropped; a filename that will not fit on its own cuts from the other
    /// end, because a BC filename carries what distinguishes it at the front
    /// and ends in the same <c>.Codeunit.al</c> as its neighbours.
    /// </summary>
    [Fact]
    public void A_path_cuts_on_a_separator_and_a_bare_filename_cuts_from_the_right()
    {
        var fit = Code(Between(Read(ViewerJs), "function fitPath(", "\n}\n"));

        fit.Should().Contain(@"if (path[i] !== ""/"") continue;",
            because: "the whole point is to cut where a segment starts");
        fit.Should().Contain("RAIL_ELLIPSIS + path.slice(i + 1)");
        fit.Should().Contain(@"path.slice(path.lastIndexOf(""/"") + 1)",
            because: "when no folder-cut fits, the filename is what is left to show");
        fit.Should().Contain("name.slice(0, end) + RAIL_ELLIPSIS",
            because: "cutting a filename's FRONT is what produced the reported bug");
    }

    /// <summary>
    /// The full path stays on the row's title. Every fitted string is a
    /// lossy rendering, and the row has to be able to answer "which file is
    /// this, exactly" without a navigation.
    /// </summary>
    [Fact]
    public void The_untruncated_path_is_still_reachable_on_the_row()
    {
        var page = Read("ALDevToolbox/Components/Pages/ObjectExplorer/OeCompareFile.razor");
        page.Should().Contain(@"title=""@($""{row.Path} - {row.Status}"")""");
        page.Should().Contain(@"data-row-path=""@row.Path""",
            because: "the fitter reads the full path from the row, not from the text it is replacing");
    }

    /// <summary>Drops comment-only lines, so a disabled call cannot satisfy a guard.</summary>
    private static string Code(string js) =>
        string.Join('\n', js.Split('\n').Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, $"'{start}' should exist");
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(from, $"'{start}' should be a complete function");
        return text[from..to];
    }

    private static string Read(string relative) =>
        File.ReadAllText(Path.Combine(Root(), relative));

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
