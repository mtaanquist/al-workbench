namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// A list tool's answer when the rows can run past what an assistant can read in
/// one response: at most the tool's cap, with <see cref="Truncated"/> and
/// <see cref="Note"/> saying when more matched and how to narrow the question.
/// The same shape as <see cref="ReferenceSearchResult"/> and its siblings, which
/// predate it. A bare list cut at its cap read as complete (#1510).
/// </summary>
public sealed record CappedRows<T>(
    IReadOnlyList<T> Rows,
    bool Truncated,
    string? Note)
{
    /// <summary>
    /// Cuts <paramref name="rows"/>, fetched one past <paramref name="cap"/>, to the
    /// cap. <paramref name="noun"/> names the rows ("objects"), <paramref name="order"/>
    /// how they were ordered, and <paramref name="narrowHint"/> what to pass instead.
    /// It also stops early once the rows reach <see cref="McpAnswerBudget.Characters"/>,
    /// so a page of long rows still fits one answer (#1524). <paramref name="raiseHint"/>
    /// is added only when the row cap cut the list: when the budget did, a higher limit
    /// would come back cut in the same place.
    /// </summary>
    internal static CappedRows<T> From(
        IReadOnlyList<T> rows, int cap, string noun, string order, string narrowHint, string? raiseHint = null)
    {
        var fit = McpAnswerBudget.Fit(rows, cap);
        if (fit < Math.Min(cap, rows.Count))
            return new(rows.Take(fit).ToList(), true, $"{McpAnswerBudget.LongRowsNote(fit, noun, order)} {narrowHint}");
        if (rows.Count <= cap) return new(rows, false, null);
        return new(rows.Take(cap).ToList(), true,
            $"Showing the first {cap} {noun} ({order}); more were left out. {narrowHint}"
            + (raiseHint is null ? "" : " " + raiseHint));
    }

    /// <summary>The <c>raiseHint</c> of a list whose limit can go higher.</summary>
    internal const string RaiseLimitHint = "You can also raise limit.";
}
