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
    /// </summary>
    internal static CappedRows<T> From(IReadOnlyList<T> rows, int cap, string noun, string order, string narrowHint)
    {
        if (rows.Count <= cap) return new(rows, false, null);
        return new(rows.Take(cap).ToList(), true,
            $"Showing the first {cap} {noun} ({order}); more were left out. {narrowHint}");
    }
}
