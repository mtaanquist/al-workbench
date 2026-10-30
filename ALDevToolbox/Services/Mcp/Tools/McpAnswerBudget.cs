using System.Text.Json;
using ModelContextProtocol;

namespace ALDevToolbox.Services.Mcp.Tools;

/// <summary>
/// How much of a list fits in one tool answer (#1524). The per-tool row caps guess how
/// long a row is, and a guess made on short rows fails on long ones: 150 event
/// publishers with their signatures, or 50 deployments carrying Business Central's
/// error messages, run past what an assistant client accepts in one answer. So a
/// capped list also stops once its rows reach <see cref="Characters"/>, counted as
/// the MCP server serialises them, and the row cap becomes an upper bound.
/// </summary>
internal static class McpAnswerBudget
{
    /// <summary>
    /// The characters a list's rows may take. Below the 25,000 an answer aims for, to
    /// leave room for the note and the fields around the rows.
    /// </summary>
    public const int Characters = 22_000;

    /// <summary>
    /// How many of the first <paramref name="cap"/> rows fit in <paramref name="budget"/>.
    /// Always at least one, so an answer never comes back empty when something matched.
    /// </summary>
    public static int Fit<T>(IReadOnlyList<T> rows, int cap, int budget = Characters)
    {
        var count = Math.Min(cap, rows.Count);
        var used = 0;
        for (var i = 0; i < count; i++)
        {
            // +1 for the comma between array elements.
            used += JsonSerializer.Serialize(rows[i], McpJsonUtilities.DefaultOptions).Length + 1;
            if (used > budget && i > 0) return i;
        }
        return count;
    }

    /// <summary>The sentence a note uses when the budget, not the row cap, cut the list.</summary>
    public static string LongRowsNote(int shown, string noun, string order) =>
        $"Showing the first {shown} {noun} ({order}); the rest would make the answer too long to read in one go.";
}
