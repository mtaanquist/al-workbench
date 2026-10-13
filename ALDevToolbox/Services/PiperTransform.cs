using System.Globalization;
using System.Text;

namespace ALDevToolbox.Services;

/// <summary>
/// Output preset for <see cref="PiperTransform.Run"/>. The presets bake in
/// the per-item prefix / suffix / join so the common cases ("BC OR" and
/// "BC AND" filter strings, SQL <c>IN</c>-style lists) don't need a trip
/// through <see cref="PiperOutputFormat.Custom"/>.
/// </summary>
public enum PiperOutputFormat
{
    /// <summary>BC filter OR: items joined with <c>|</c>.</summary>
    BcOr,
    /// <summary>BC filter AND with exclusion: each item prefixed with <c>&lt;&gt;</c>, joined with <c>&amp;</c>.</summary>
    BcAnd,
    /// <summary>SQL <c>IN</c>-clause body: each item quoted with <c>'</c>, joined with <c>,</c>. Embedded <c>'</c> is doubled.</summary>
    Sql,
    /// <summary>User-supplied per-item prefix, per-item suffix, and join string.</summary>
    Custom,
}

public enum PiperSortOrder { None, Ascending, Descending }

/// <summary>
/// Options bag for <see cref="PiperTransform.Run"/>. All fields have safe
/// defaults so callers can construct <c>new PiperOptions()</c> and only set
/// what they need.
/// </summary>
public sealed record PiperOptions
{
    public PiperOutputFormat Format { get; init; } = PiperOutputFormat.BcOr;
    public string CustomPrefix { get; init; } = "";
    public string CustomSuffix { get; init; } = "";
    public string CustomJoin { get; init; } = ",";
    public string ResultPrefix { get; init; } = "";
    public string ResultSuffix { get; init; } = "";

    /// <summary>
    /// When non-empty, splits on this exact string (with <c>\t</c>, <c>\n</c>,
    /// <c>\r</c>, and <c>\\</c> recognised as escapes). When empty, the
    /// transform auto-detects the most prolific delimiter from a fixed set.
    /// </summary>
    public string SeparatorOverride { get; init; } = "";

    public PiperSortOrder Sort { get; init; } = PiperSortOrder.None;

    /// <summary>Restricts auto-detect to <c>\r\n</c>, <c>\n</c>, <c>\r</c>.</summary>
    public bool SplitOnNewlinesOnly { get; init; }

    public bool TrimItems { get; init; }

    /// <summary>
    /// Drops items that are empty after trimming. Defaults to <c>true</c>
    /// because <c>a,,b</c> is almost always a typo, not three intentional
    /// items. Set <c>false</c> to keep the original behaviour.
    /// </summary>
    public bool SkipEmpty { get; init; } = true;

    /// <summary>
    /// Removes duplicate items, preserving first-occurrence order.
    /// Defaults to <c>true</c> because the most common use of Piper is
    /// building a BC OR or SQL <c>IN</c> list where duplicates are noise.
    /// </summary>
    public bool RemoveDuplicates { get; init; } = true;
}

/// <summary>
/// Result of one transform pass. <see cref="DetectedSeparatorDisplay"/> is
/// non-null only when auto-detect actually found a delimiter — callers use
/// it to hint the user what's being used (e.g. as a placeholder).
/// </summary>
public sealed record PiperResult(
    string Output,
    int ItemCount,
    string DelimiterDescription,
    string? DetectedSeparatorDisplay);

/// <summary>
/// Parsed clipboard table — what <see cref="PiperTransform.ParseTable"/>
/// returns when the input looks like a tab-separated grid copied from a
/// Business Central list page. <see cref="Headers"/> is the first row;
/// <see cref="Rows"/> is everything after. Each row is padded to the header
/// width so callers can index by column without bounds checks.
/// </summary>
public sealed record PiperTable(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> Rows);

/// <summary>
/// Pure string transform that powers the <c>/piper</c> page. Lives in
/// <c>Services/</c> rather than under a <c>PiperService</c> DI registration
/// because there is no DB access, no async, and no per-request state — it's
/// a static helper, called per-keystroke from the page.
/// </summary>
public static class PiperTransform
{
    // Auto-detect candidate order matters: \r\n leads so Windows line-endings
    // aren't double-counted as separate \r and \n. "|" is in the set so a BC
    // OR result round-trips when the user feeds it back via Swap.
    private static readonly string[] FullCandidates = { "\r\n", "\n", "\r", "\t", ",", ";", "|" };
    private static readonly string[] NewlineOnlyCandidates = { "\r\n", "\n", "\r" };

    /// <summary>
    /// Upper bound on the input the transform will process in one call. Run
    /// fires per-keystroke on a Blazor Server circuit (server-side CPU + memory
    /// per user), and the pipeline makes several full passes plus dedup/sort
    /// allocations, so an unbounded multi-MB paste is a cheap way to spike a
    /// server thread. 50 MB is far beyond any realistic ID/filter list but
    /// leaves room for very large pastes on an internal deployment. The
    /// Diff tool uses the same cap per side.
    /// </summary>
    public const int MaxInputLength = 50 * 1024 * 1024;

    public static PiperResult Run(string input, PiperOptions options)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new PiperResult("", 0, "Awaiting input...", null);
        }
        if (input.Length > MaxInputLength)
        {
            return new PiperResult(
                "", 0,
                $"Input is too large ({input.Length:N0} characters). Trim it under {MaxInputLength:N0} and try again.",
                null);
        }

        var (items, delimiterDesc, detectedDisplay) = Split(input, options);
        return RunPipeline(items, options, delimiterDesc, detectedDisplay);
    }

    /// <summary>
    /// Runs the format pipeline (trim → skip empty → dedup → sort → format
    /// → wrap) over an already-split item list. Used by Table mode on the
    /// <c>/piper</c> page, where the items are projected from a parsed
    /// table column rather than split out of a single delimited string.
    /// <paramref name="delimiterDescription"/> is echoed back on the result
    /// so the page can show something meaningful in the meta line
    /// (e.g. <c>"table column: \"Nummer\""</c>).
    /// </summary>
    public static PiperResult RunOnItems(IReadOnlyList<string> items, PiperOptions options, string delimiterDescription)
    {
        if (items.Count == 0)
        {
            return new PiperResult("", 0, delimiterDescription, null);
        }

        // Copy into a mutable array so the shared pipeline can rewrite slots
        // (Trim) without mutating the caller's list.
        var copy = new string[items.Count];
        for (var i = 0; i < items.Count; i++) copy[i] = items[i];
        return RunPipeline(copy, options, delimiterDescription, null);
    }

    private static PiperResult RunPipeline(string[] items, PiperOptions options, string delimiterDescription, string? detectedDisplay)
    {
        if (options.TrimItems)
        {
            for (var i = 0; i < items.Length; i++) items[i] = items[i].Trim();
        }

        if (options.SkipEmpty)
        {
            items = items.Where(i => i.Length > 0).ToArray();
        }

        if (options.RemoveDuplicates)
        {
            items = items.Distinct().ToArray();
        }

        if (options.Sort != PiperSortOrder.None && items.Length > 1)
        {
            items = SortItems(items, options.Sort);
        }

        var formatted = options.Format switch
        {
            PiperOutputFormat.BcAnd => string.Join("&", items.Select(i => "<>" + QuoteBcFilterValue(i))),
            PiperOutputFormat.Sql => string.Join(",", items.Select(i => $"'{i.Replace("'", "''")}'")),
            PiperOutputFormat.Custom => string.Join(options.CustomJoin, items.Select(i => options.CustomPrefix + i + options.CustomSuffix)),
            _ => string.Join("|", items.Select(QuoteBcFilterValue)),
        };

        if (options.ResultPrefix.Length > 0 || options.ResultSuffix.Length > 0)
        {
            formatted = options.ResultPrefix + formatted + options.ResultSuffix;
        }

        return new PiperResult(formatted, items.Length, delimiterDescription, detectedDisplay);
    }

    /// <summary>
    /// Characters Business Central reads as filter operators rather than as
    /// part of a value: <c>&amp;</c> and <c>|</c> join, <c>&lt; &gt; =</c>
    /// compare, <c>..</c> is a range, <c>( )</c> group, <c>* ? @</c> are
    /// wildcards and case folding, and <c>'</c> quotes.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> BcFilterOperators =
        System.Buffers.SearchValues.Create("&|<>=.()*?@'");

    /// <summary>
    /// One value as a Business Central filter reads it literally. A value with
    /// an operator character in it is single-quoted, with any quote inside it
    /// doubled - so <c>Smith &amp; Sons</c> is one customer, not two filters
    /// joined by "and". Plain values are left as they are, which is what a
    /// person would type.
    /// </summary>
    internal static string QuoteBcFilterValue(string value)
    {
        var needsQuotes = value.AsSpan().IndexOfAny(BcFilterOperators) >= 0
            || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return needsQuotes ? "'" + value.Replace("'", "''") + "'" : value;
    }

    /// <summary>
    /// Parses a delimited table (tab-separated by default, configurable
    /// for CSVs and similar) into headers + rows. Returns <c>null</c> when
    /// the input doesn't look like a table — no occurrence of the chosen
    /// separator, or not enough rows for the requested header layout.
    /// </summary>
    /// <param name="input">The raw paste from the clipboard.</param>
    /// <param name="separator">
    /// The cell separator. Defaults to <c>"\t"</c> so a Business Central
    /// list paste works out of the box.
    /// </param>
    /// <param name="hasHeaders">
    /// When <c>true</c> (the default), the first row is the header row and
    /// remaining rows are data. When <c>false</c>, every row is data and
    /// headers are synthesised as <c>"Column 1"</c>, <c>"Column 2"</c>, …
    /// up to the widest row's cell count.
    /// </param>
    /// <remarks>
    /// A single trailing blank row is trimmed (clipboards usually have
    /// one); interior blank rows are preserved because the source data
    /// might genuinely contain a blank row. Short rows are padded to the
    /// table width with empty strings, and over-wide rows are truncated —
    /// both happen silently because BC sometimes elides trailing empty
    /// cells. Duplicate header names are disambiguated with
    /// <c>" (2)"</c>, <c>" (3)"</c>, … so a UI <c>&lt;select&gt;</c> built
    /// from these can rely on unique option values.
    /// Quoted CSV fields (<c>"value, with comma"</c>) are out of scope —
    /// callers that need RFC 4180 quoting should pre-process the input.
    /// </remarks>
    public static PiperTable? ParseTable(string input, string separator = "\t", bool hasHeaders = true)
    {
        if (string.IsNullOrEmpty(input)) return null;
        if (string.IsNullOrEmpty(separator)) return null;

        // Normalise line endings (\r\n and lone \r → \n) so a single split
        // suffices and we don't have to juggle three separators downstream.
        var normalised = input.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawRows = normalised.Split('\n');

        // Trim a single trailing empty row (the common clipboard pattern).
        var rowCount = rawRows.Length;
        if (rowCount > 0 && rawRows[rowCount - 1].Length == 0)
        {
            rowCount--;
        }

        var minRows = hasHeaders ? 2 : 1;
        if (rowCount < minRows) return null;
        if (input.IndexOf(separator, StringComparison.Ordinal) < 0) return null;

        var splitRows = new string[rowCount][];
        var maxWidth = 0;
        for (var r = 0; r < rowCount; r++)
        {
            splitRows[r] = rawRows[r].Split(new[] { separator }, StringSplitOptions.None);
            if (splitRows[r].Length > maxWidth) maxWidth = splitRows[r].Length;
        }

        IReadOnlyList<string> headers;
        int dataStart;
        int width;
        if (hasHeaders)
        {
            headers = DisambiguateHeaders(splitRows[0]);
            dataStart = 1;
            width = headers.Count;
        }
        else
        {
            // No header row — label each column by its 1-based position so
            // the UI <select> has something to display. Use the widest row
            // as the canonical column count.
            var names = new string[maxWidth];
            for (var i = 0; i < maxWidth; i++) names[i] = $"Column {i + 1}";
            headers = names;
            dataStart = 0;
            width = maxWidth;
        }

        var rows = new List<IReadOnlyList<string>>(rowCount - dataStart);
        for (var r = dataStart; r < rowCount; r++)
        {
            var cells = splitRows[r];
            if (cells.Length == width)
            {
                rows.Add(cells);
                continue;
            }

            // Pad short rows; truncate over-wide rows. Both are silent —
            // BC elides trailing empty cells and we don't want to refuse
            // an otherwise-valid paste over a cosmetic mismatch.
            var padded = new string[width];
            var copyLen = Math.Min(cells.Length, width);
            for (var c = 0; c < copyLen; c++) padded[c] = cells[c];
            for (var c = copyLen; c < width; c++) padded[c] = "";
            rows.Add(padded);
        }

        return new PiperTable(headers, rows);
    }

    private static IReadOnlyList<string> DisambiguateHeaders(string[] raw)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new string[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            var name = raw[i];
            if (seen.TryGetValue(name, out var count))
            {
                count++;
                seen[name] = count;
                result[i] = $"{name} ({count})";
            }
            else
            {
                seen[name] = 1;
                result[i] = name;
            }
        }
        return result;
    }

    private static (string[] Items, string DelimiterDescription, string? DetectedDisplay) Split(string input, PiperOptions options)
    {
        var overrideLiteral = UnescapeSeparator(options.SeparatorOverride);

        if (!string.IsNullOrEmpty(overrideLiteral))
        {
            var items = input.Split(new[] { overrideLiteral }, StringSplitOptions.None);
            return (items, $"custom: {FriendlyDelim(overrideLiteral)}", null);
        }

        var candidates = options.SplitOnNewlinesOnly ? NewlineOnlyCandidates : FullCandidates;
        string bestDelim = "";
        int bestCount = 0;
        foreach (var c in candidates)
        {
            var count = CountOccurrences(input, c);
            if (count > bestCount)
            {
                bestCount = count;
                bestDelim = c;
            }
        }

        if (bestCount == 0)
        {
            return (new[] { input }, "no delimiter found", null);
        }

        var split = input.Split(new[] { bestDelim }, StringSplitOptions.None);
        return (split, $"auto-detected: {FriendlyDelim(bestDelim)}", EscapeForDisplay(bestDelim));
    }

    // Sorts numerically when every item parses as a decimal (typical for AL
    // ID lists where lexical order would put "999" after "1001"); otherwise
    // case-insensitive ordinal. Parses once into a (original, parsed) tuple
    // and sorts on the parsed value — the previous shape reparsed on every
    // comparison via OrderBy's key selector (#81).
    private static string[] SortItems(string[] items, PiperSortOrder order)
    {
        var parsed = new (string Original, decimal Value, bool Ok)[items.Length];
        var allNumeric = true;
        for (var i = 0; i < items.Length; i++)
        {
            var ok = decimal.TryParse(items[i], NumberStyles.Number, CultureInfo.InvariantCulture, out var value);
            parsed[i] = (items[i], value, ok);
            if (!ok) allNumeric = false;
        }

        IEnumerable<string> sorted = allNumeric
            ? (order == PiperSortOrder.Ascending
                ? parsed.OrderBy(t => t.Value).Select(t => t.Original)
                : parsed.OrderByDescending(t => t.Value).Select(t => t.Original))
            : (order == PiperSortOrder.Ascending
                ? items.OrderBy(i => i, StringComparer.OrdinalIgnoreCase)
                : items.OrderByDescending(i => i, StringComparer.OrdinalIgnoreCase));

        return sorted.ToArray();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) != -1)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    // Lets the user type literal "\t" / "\n" / "\r" / "\\" in the separator
    // override input instead of needing to paste actual whitespace.
    internal static string UnescapeSeparator(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                switch (s[i + 1])
                {
                    case 't': sb.Append('\t'); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static string FriendlyDelim(string s) => $"\"{EscapeForDisplay(s)}\"";

    internal static string EscapeForDisplay(string s) => s switch
    {
        "\t" => "\\t",
        "\n" => "\\n",
        "\r" => "\\r",
        "\r\n" => "\\r\\n",
        _ => s,
    };
}
