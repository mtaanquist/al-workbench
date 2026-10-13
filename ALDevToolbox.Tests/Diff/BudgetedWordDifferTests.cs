using ALDevToolbox.Services.Diff;
using AwesomeAssertions;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace ALDevToolbox.Tests.Diff;

/// <summary>
/// The word-diff budget behind the Diff tool endpoint: within budget it must
/// give exactly what DiffPlex gives, and past it a changed line must still read
/// as changed - only the within-line highlighting may get coarser.
/// </summary>
public sealed class BudgetedWordDifferTests
{
    private const string Old = "x\nhello world foo\ny";
    private const string New = "x\nhello there foo\ny";

    [Fact]
    public void Within_budget_the_result_matches_DiffPlex()
    {
        var expected = SideBySideDiffBuilder.Diff(Old, New, true);
        var differ = new BudgetedWordDiffer(long.MaxValue, long.MaxValue);

        var actual = differ.Diff(Old, New, true);

        differ.SkippedPairs.Should().Be(0);
        Describe(actual.NewText).Should().Equal(Describe(expected.NewText));
        Describe(actual.OldText).Should().Equal(Describe(expected.OldText));
    }

    /// <summary>
    /// Either budget running out has the same effect: the pair is reported as
    /// one whole-line change, a single highlighted piece rather than one per
    /// word, so the counts stay true and the response stays small.
    /// </summary>
    [Theory]
    [InlineData(0L, long.MaxValue)]
    [InlineData(long.MaxValue, 0L)]
    public void Past_either_budget_a_changed_line_is_still_reported_as_changed(long cost, long words)
    {
        var differ = new BudgetedWordDiffer(cost, words);

        var model = differ.Diff(Old, New, true);

        differ.SkippedPairs.Should().Be(1);
        var line = model.NewText.Lines[1];
        line.Type.Should().Be(ChangeType.Modified);
        line.SubPieces.Where(p => p.Type is not ChangeType.Unchanged and not ChangeType.Imaginary)
            .Should().ContainSingle().Which.Text.Should().Be("hello there foo");
        model.OldText.Lines[1].Type.Should().Be(ChangeType.Modified);
        SideBySideDiffSerializer.Summarize(model).Modified.Should().Be(1);
    }

    [Fact]
    public void The_budgets_are_spent_across_the_pairs_of_one_diff()
    {
        // Each pair here is "lineN" vs "LINEN": one word a side, so 2 words and
        // a cost of 4 apiece. A word budget of 5 covers two pairs, not three.
        var differ = new BudgetedWordDiffer(long.MaxValue, 5);

        var model = differ.Diff("line1\nline2\nline3", "LINE1\nLINE2\nLINE3", false);

        differ.SkippedPairs.Should().Be(1);
        model.NewText.Lines.Should().OnlyContain(l => l.Type == ChangeType.Modified);
    }

    [Fact]
    public void Identical_lines_spend_nothing_however_long()
    {
        var image = new string('Q', 100_000);
        var differ = new BudgetedWordDiffer(100, 100);

        var model = differ.Diff($"{image}\nhello world", $"{image}\nhello there", true);

        differ.SkippedPairs.Should().Be(0, "only the short changed pair is word-diffed, and it fits");
        model.NewText.Lines[0].Type.Should().Be(ChangeType.Unchanged);
        model.NewText.Lines[1].Type.Should().Be(ChangeType.Modified);
    }

    private static List<string> Describe(DiffPaneModel pane) =>
        pane.Lines.Select(l => $"{l.Type}|{l.Text}|{string.Join(",", l.SubPieces.Select(s => $"{s.Type}:{s.Text}"))}").ToList();
}
