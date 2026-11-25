using System.Text.RegularExpressions;
using ALDevToolbox.Services.Al;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// How a field or variable is used at one reference site: read, or set - and
/// if set, how. Worked out at query time from the reference's source line
/// rather than stored on <c>oe_module_references</c>, so every release that is
/// already imported answers "where is this set?" without being imported again.
/// See <c>.design/object-explorer.md</c>, "Where a field is set".
/// </summary>
public static class ReferenceUsage
{
    public const string Read = "read";

    /// <summary>Lines read on each side of a reference, for statements split over several lines.</summary>
    public const int ContextLines = 3;

    /// <summary><c>X := ...</c>, and the compound <c>+=</c> / <c>-=</c> / <c>*=</c> / <c>/=</c>.</summary>
    public const string Assign = "assign";

    /// <summary><c>Rec.Validate("Field", ...)</c> - the field is the first argument.</summary>
    public const string Validate = "validate";

    /// <summary><c>Rec.ModifyAll("Field", ...)</c>.</summary>
    public const string ModifyAll = "modify_all";

    /// <summary><c>Evaluate(X, Text)</c> - the variable or field is the first argument.</summary>
    public const string Evaluate = "evaluate";

    /// <summary><c>Clear(X)</c>.</summary>
    public const string Clear = "clear";

    /// <summary><c>X.Init()</c>, <c>X.TransferFields(...)</c> and <c>X.Copy(...)</c> on a record variable.</summary>
    public const string RecordWrite = "record_write";

    /// <summary>True for every usage that changes the value; false for reads and unknown sites.</summary>
    public static bool IsWrite(string? usage) => usage is not null && usage != Read;

    /// <summary>
    /// The reference kinds that point at something that holds a value: a field,
    /// or a global or local variable. Only these are classified; a call, a type
    /// use or a declaration is neither read nor set.
    /// </summary>
    public static bool AppliesTo(string referenceKind) =>
        referenceKind is "field_access" or "variable_use" or "local_variable_use";

    // The first argument of these is the thing being set. Matched without
    // regard to case so C/AL's VALIDATE / MODIFYALL / EVALUATE match too.
    private static readonly Dictionary<string, string> FirstArgSetters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Validate"] = Validate,
        ["ModifyAll"] = ModifyAll,
        ["Evaluate"] = Evaluate,
        ["Clear"] = Clear,
    };

    // Field-name methods whose later arguments are fields too.
    private static readonly HashSet<string> AllArgumentsAreFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "CalcFields", "CalcSums", "SetCurrentKey", "SetLoadFields", "AddLoadFields", "SetAutoCalcFields",
    };

    // Called on the variable itself: X.Init() / X.TransferFields(Y) / X.Copy(Y)
    // overwrite X's fields wholesale.
    private static readonly HashSet<string> RecordWriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Init", "TransferFields", "Copy",
    };

    /// <summary>
    /// Classifies the reference whose name starts at 1-based
    /// <paramref name="column"/> on <paramref name="line"/>, looking at that
    /// line alone. See <see cref="Classify(IReadOnlyList{string}, int, int?, string?)"/>.
    /// </summary>
    public static Site? Classify(string line, int? column, string? name) =>
        Classify([line], 0, column, name);

    /// <summary>
    /// Classifies the reference whose name starts at 1-based
    /// <paramref name="column"/> on <c>lines[target]</c>. The lines around it
    /// are there for statements split over several lines
    /// (<c>Rec."No." :=</c> on one, the value on the next, or the field on the
    /// line after <c>Rec.Validate(</c>): the scan reads across them, and stops
    /// at the <c>;</c> that ends the previous statement. When the column is
    /// missing or does not land on <paramref name="name"/> (rows imported
    /// before columns were stamped), the first whole-word occurrence of the
    /// name on the target line is used instead. Returns null when the name is
    /// not on that line at all, so an unknown site is never reported as a read.
    /// </summary>
    public static Site? Classify(IReadOnlyList<string> lines, int target, int? column, string? name)
    {
        if (target < 0 || target >= lines.Count) return null;
        var at = LocateName(lines[target], column, name);
        if (at < 0) return null;

        // One string with the lines joined, and line comments blanked out so a
        // parenthesis or := in a comment is not read as code. Blanking rather
        // than cutting keeps every column where it was.
        var offset = 0;
        var joined = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i == target) offset = joined.Length;
            joined.Append(BlankLineComment(lines[i])).Append('\n');
        }
        return ClassifyAt(joined.ToString(), offset + at);
    }

    private static Site? ClassifyAt(string line, int start)
    {
        var end = TokenEnd(line, start);
        if (end <= start) return null;

        // What follows the name: an assignment, or a whole-record write method.
        var p = SkipSpace(line, end);
        while (p < line.Length && line[p] == '[')
        {
            p = SkipBalancedForward(line, p, '[', ']');
            p = SkipSpace(line, p);
        }
        string? usage = null;
        if (p + 1 < line.Length && line[p + 1] == '=' && line[p] is ':' or '+' or '-' or '*' or '/')
        {
            usage = Assign;
        }
        else if (p < line.Length && line[p] == '.' && (p + 1 >= line.Length || line[p + 1] != '.'))
        {
            var methodStart = SkipSpace(line, p + 1);
            var methodEnd = TokenEnd(line, methodStart);
            var method = Unquote(line[methodStart..methodEnd]);
            // Parentheses are optional on a call with no arguments: X.INIT;
            if (RecordWriteMethods.Contains(method)
                && SkipSpace(line, methodEnd) is var afterMethod
                && (afterMethod >= line.Length || line[afterMethod] is '(' or ';' or '\n'
                    || IsIdentChar(line[afterMethod])))
            {
                usage = RecordWrite;
            }
        }

        // What precedes it: a receiver chain (TempLine."No."), and before
        // that, possibly the call it is an argument of.
        string? receiver = null;
        var exprStart = start;
        var q = SkipSpaceBack(line, start - 1);
        while (q >= 0 && line[q] == '.' && (q == 0 || line[q - 1] != '.'))
        {
            var segEnd = SkipSpaceBack(line, q - 1);
            var segStart = SegmentStart(line, segEnd);
            if (segStart < 0) break;
            receiver ??= Unquote(SegmentName(line, segStart, segEnd));
            exprStart = segStart;
            q = SkipSpaceBack(line, segStart - 1);
        }

        var call = EnclosingCall(line, exprStart);
        if (call is not null)
        {
            if (usage is null && call.FirstArgument
                && FirstArgSetters.TryGetValue(call.Method, out var setter))
            {
                usage = setter;
            }
            // A bare name inside Validate / SetRange / ... names a field on the
            // record the method is called on: TempLine.Validate("No."). Only the
            // first argument does, except for the methods every argument of
            // which is a field; in TempLine.Validate("No.", "No.") the second
            // is the calling object's own field.
            if (receiver is null && exprStart == start && IsFieldNameTaking(call.Method)
                && (call.FirstArgument || AllArgumentsAreFields.Contains(call.Method)))
            {
                receiver = call.Receiver;
            }
        }

        return new Site(usage ?? Read, receiver);
    }

    /// <summary>The outcome of <see cref="Classify"/>: the usage, and the record the name was reached through (null for the implicit <c>Rec</c>).</summary>
    public sealed record Site(string Usage, string? Receiver);

    /// <summary>
    /// True when the record the name is reached through is a temporary one:
    /// a variable or parameter declared <c>temporary</c> in the same file, or
    /// <c>Rec</c> (written or implied) on a page with a temporary source table
    /// or a table whose type is temporary.
    /// </summary>
    public static bool IsTemporary(string? receiver, bool recIsTemporary, IReadOnlySet<string> temporaryNames)
    {
        if (receiver is null
            || receiver.Equals("Rec", StringComparison.OrdinalIgnoreCase)
            || receiver.Equals("xRec", StringComparison.OrdinalIgnoreCase))
        {
            return recIsTemporary;
        }
        return temporaryNames.Contains(receiver);
    }

    /// <summary>
    /// Splits the name lists the declaration pattern in
    /// <see cref="ReferenceQueryService"/> captures (<c>TempA, "Temp B"</c>)
    /// into unquoted names.
    /// </summary>
    public static HashSet<string> ParseTemporaryNames(IEnumerable<string?> captured)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in captured)
        {
            if (string.IsNullOrEmpty(list)) continue;
            foreach (var part in list.Split(','))
            {
                var trimmed = Unquote(part.Trim());
                if (trimmed.Length > 0) names.Add(trimmed);
            }
        }
        return names;
    }

    /// <summary>Replaces a <c>//</c> comment (outside strings and quoted names) with spaces.</summary>
    private static string BlankLineComment(string line)
    {
        char? quote = null;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null) { if (c == quote) quote = null; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i] + new string(' ', line.Length - i);
            }
        }
        return line;
    }

    private static bool IsFieldNameTaking(string method) =>
        AlBuiltinMethods.FieldNameTakingMethods.Contains(method);

    private sealed record Call(string Method, string? Receiver, bool FirstArgument);

    /// <summary>
    /// Walks left from <paramref name="exprStart"/> to the unmatched <c>(</c>
    /// that opens the argument list the expression sits in, and names the
    /// method and its receiver. Null when the expression is not inside a call
    /// on this line.
    /// </summary>
    private static Call? EnclosingCall(string line, int exprStart)
    {
        var depth = 0;
        var firstArgument = true;
        for (var i = exprStart - 1; i >= 0; i--)
        {
            var c = line[i];
            if (c == '\'' || c == '"')
            {
                // Skip a string literal or quoted name whole, so a comma or
                // bracket inside it does not count.
                var open = line.LastIndexOf(c, Math.Max(i - 1, 0));
                if (open < 0 || open >= i) return null;
                i = open;
                continue;
            }
            if (c is ')' or ']') { depth++; continue; }
            if (c == '[') { if (depth == 0) return null; depth--; continue; }
            if (c == '(')
            {
                if (depth > 0) { depth--; continue; }
                var methodEnd = SkipSpaceBack(line, i - 1);
                var methodStart = SegmentStart(line, methodEnd);
                if (methodStart < 0) return null;
                var method = Unquote(SegmentName(line, methodStart, methodEnd));
                string? receiver = null;
                var dot = SkipSpaceBack(line, methodStart - 1);
                if (dot >= 0 && line[dot] == '.')
                {
                    var recvEnd = SkipSpaceBack(line, dot - 1);
                    var recvStart = SegmentStart(line, recvEnd);
                    if (recvStart >= 0) receiver = Unquote(SegmentName(line, recvStart, recvEnd));
                }
                return new Call(method, receiver, firstArgument);
            }
            if (depth == 0 && (c == ',' || c == ';')) firstArgument = false;
            if (depth == 0 && c == ';') return null;
        }
        return null;
    }

    private static int LocateName(string line, int? column, string? name)
    {
        if (column is { } col && col >= 1 && col <= line.Length)
        {
            var at = col - 1;
            if (string.IsNullOrEmpty(name) || NameAt(line, at, name)) return at;
        }
        if (string.IsNullOrEmpty(name)) return -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (NameAt(line, i, name)
                && (i == 0 || !IsIdentChar(line[i - 1]))) return i;
        }
        return -1;
    }

    private static bool NameAt(string line, int at, string name)
    {
        if (line[at] == '"')
        {
            var close = line.IndexOf('"', at + 1);
            return close > at && line.AsSpan(at + 1, close - at - 1).Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        var end = TokenEnd(line, at);
        return line.AsSpan(at, end - at).Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    private static int TokenEnd(string line, int start)
    {
        if (start >= line.Length) return start;
        if (line[start] == '"')
        {
            var close = line.IndexOf('"', start + 1);
            return close < 0 ? line.Length : close + 1;
        }
        var i = start;
        while (i < line.Length && IsIdentChar(line[i])) i++;
        return i;
    }

    /// <summary>Start of the name or call that ends at <paramref name="end"/>: <c>Name</c>, <c>"Quoted name"</c> or <c>Name(...)</c>.</summary>
    private static int SegmentStart(string line, int end)
    {
        if (end < 0) return -1;
        if (line[end] == '"')
        {
            var open = end > 0 ? line.LastIndexOf('"', end - 1) : -1;
            return open;
        }
        if (line[end] == ')')
        {
            var depth = 0;
            for (var i = end; i >= 0; i--)
            {
                if (line[i] == ')') depth++;
                else if (line[i] == '(' && --depth == 0) return SegmentStart(line, SkipSpaceBack(line, i - 1));
            }
            return -1;
        }
        if (!IsIdentChar(line[end])) return -1;
        var s = end;
        while (s > 0 && IsIdentChar(line[s - 1])) s--;
        return s;
    }

    private static string SegmentName(string line, int start, int end)
    {
        var tokenEnd = TokenEnd(line, start);
        return line[start..Math.Min(tokenEnd, end + 1)];
    }

    private static int SkipBalancedForward(string line, int at, char open, char close)
    {
        var depth = 0;
        for (var i = at; i < line.Length; i++)
        {
            if (line[i] == open) depth++;
            else if (line[i] == close && --depth == 0) return i + 1;
        }
        return line.Length;
    }

    private static int SkipSpace(string line, int i)
    {
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        return i;
    }

    private static int SkipSpaceBack(string line, int i)
    {
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        return i;
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Postgres (ARE) pattern capturing the names in a declaration of a
    /// temporary record: AL <c>TempA, "Temp B": Record "Sales Line" temporary</c>
    /// (variables and parameters alike, the type optionally namespace-qualified)
    /// and C/AL <c>TempLine@1000 : TEMPORARY Record 37</c>. Group 1 is the AL
    /// name list, which stays on one line, group 2 the C/AL name.
    /// </summary>
    internal const string TemporaryDeclarationPattern =
        """((?:(?:"[^"\n]+"|\w+)[ \t]*,[ \t]*)*(?:"[^"\n]+"|\w+))\s*:\s*Record\s+(?:[\w.]+\.)?(?:"[^"\n]+"|[\w.]+)\s+temporary\y"""
        + """|("[^"\n]+"|\w+)@\d+\s*:\s*TEMPORARY\s+Record\y""";

    /// <summary>
    /// Postgres (ARE) pattern for an object whose own <c>Rec</c> is temporary:
    /// a page's <c>SourceTableTemporary = true</c> (C/AL <c>=Yes</c>) or a
    /// table's <c>TableType = Temporary</c>.
    /// </summary>
    internal const string TemporaryRecPattern =
        """\ySourceTableTemporary\s*=\s*(true|yes)\y|\yTableType\s*=\s*Temporary\y""";

    /// <summary>The same two patterns for .NET, for callers that already hold the file's text.</summary>
    internal static readonly Regex TemporaryDeclarationRegex = new(
        TemporaryDeclarationPattern.Replace(@"\y", @"\b"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static readonly Regex TemporaryRecRegex = new(
        TemporaryRecPattern.Replace(@"\y", @"\b"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
