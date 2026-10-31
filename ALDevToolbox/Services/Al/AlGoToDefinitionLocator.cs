using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Al;

/// <summary>
/// Resolves a "Go to definition" click in the Object Explorer. Given a click
/// position inside a file, returns the token under the cursor plus minimal
/// context (was it qualified by a variable, was it inside an object-reference
/// keyword block, etc.) so the caller can look up the right declaration.
///
/// Not a full AL parser — recognises the common shapes that account for
/// almost all real-world definitions:
///
///   - <c>Codeunit "Sales-Post"</c>           → object reference, token is the name.
///   - <c>Codeunit::"Sales-Post"</c>          → object reference with double-colon.
///   - <c>"Sales-Post".Post(...)</c>          → object reference followed by member.
///   - <c>SalesPostCu.Post(...)</c>           → variable-qualified procedure call.
///   - <c>Post(...)</c>                        → bare procedure call.
/// </summary>
public static class AlGoToDefinitionLocator
{
    // Keep this in sync with AlResolvableTokenScanner.ObjectKeywords — both
    // lists drive "this token is an object reference" detection from the
    // same left-context check.
    private static readonly HashSet<string> ObjectKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "codeunit", "table", "page", "report", "query", "xmlport", "controladdin",
        "enum", "interface", "permissionset", "profile", "record",
        "requestpage", "testpage", "testpart", "testrequestpage",
        "pageextension", "tableextension", "reportextension", "enumextension",
        "permissionsetextension",
        "extends",
        "tabledata",
        // `Database::"Customer"` is a table literal. Kept here (and not in
        // AlResolvableTokenScanner's list) so the click's kind hint sees it
        // without changing which tokens the viewer underlines. See #712.
        "database",
    };

    /// <summary>
    /// Inspects <paramref name="source"/> at the supplied 1-based line and
    /// column and returns the click context. Returns <c>null</c> when the
    /// cursor lands on whitespace or a non-identifier character.
    /// </summary>
    public static GoToDefinitionClick? Inspect(string source, int line, int column)
    {
        if (string.IsNullOrEmpty(source) || line < 1 || column < 1) return null;

        var lineText = GetLine(source, line);
        if (lineText is null) return null;

        // CodeMirror passes a 1-based column for the click position; convert
        // to a 0-based index into the line.
        var idx = Math.Min(Math.Max(0, column - 1), lineText.Length - 1);
        if (idx < 0 || idx >= lineText.Length) return null;

        var (word, wordStart, wordEnd) = ExtractTokenAt(lineText, idx);
        if (string.IsNullOrEmpty(word)) return null;

        // Left context: what's immediately before the token (skipping
        // whitespace)? Captures `.`, `::`, or an identifier keyword like
        // <c>Codeunit</c>.
        var leftContext = ReadLeftContext(lineText, wordStart);

        return new GoToDefinitionClick(
            Word: word,
            LineText: lineText,
            LeftContext: leftContext);
    }

    /// <summary>
    /// The object kind the click's context asks for, or <c>null</c> when the
    /// context names none. Three shapes carry one: a typed literal
    /// (<c>Database::"Customer"</c>, <c>Codeunit::"Sales-Post"</c>), a
    /// declaration keyword (<c>Record "Customer"</c>, <c>Page "Customer
    /// List"</c>), and a page's <c>SourceTable = "Customer"</c> property.
    /// Callers pass it to the object lookup so a name shared by several
    /// kinds resolves to the one the source actually named — BC ships both a
    /// table and a page called <c>E-Document Service</c>, and both a table and
    /// a codeunit called <c>User</c>. See issue #712.
    /// </summary>
    public static string? KindHint(GoToDefinitionClick click)
    {
        if (click.LeftContext.Operator is "::" or "keyword"
            && click.LeftContext.Qualifier is { Length: > 0 } qualifier)
        {
            // `extends` names a base object but not which kind — the owning
            // object's own kind decides that, and this locator doesn't see it.
            if (qualifier.Equals("extends", StringComparison.OrdinalIgnoreCase)) return null;
            // Accept any qualifier that maps onto a real catalog kind. That
            // deliberately includes `Database`, which the underline scanner's
            // keyword list doesn't carry: `Database::User` is a table.
            var mapped = AlKindKeywords.MapKeywordToKind(qualifier);
            if (AlKindKeywords.KindRank(mapped) < AlKindKeywords.KindPriority.Length) return mapped;
        }

        if (SourceTablePropertyRegex.IsMatch(click.LineText)) return "table";
        return null;
    }

    private static readonly Regex SourceTablePropertyRegex =
        new(@"^\s*SourceTable\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the type name when <paramref name="fileContent"/> contains a
    /// var declaration like <c>VarName: Codeunit "Sales-Post"</c>. Used to
    /// resolve <c>SalesPostCu.Post(...)</c> to the right declaring object.
    /// Searches the whole file rather than tracking scope — AL devs rarely
    /// reuse a variable name across procedures, and even when they do, both
    /// vars usually share a type.
    /// </summary>
    public static string? ResolveVariableType(string fileContent, string variableName)
    {
        if (string.IsNullOrEmpty(fileContent) || string.IsNullOrEmpty(variableName)) return null;
        // Match `VarName: Codeunit "Some Name"` or `VarName: Codeunit Identifier`.
        // Record is by far the most common form for field-access targets and
        // was missing here — `MfgSetup."Dynamic Low-Level Code"` couldn't
        // resolve because the qualifier walked off `Record "Manufacturing Setup"`.
        var escaped = Regex.Escape(variableName);
        var pattern = $@"\b{escaped}\s*:\s*(?:Record|Codeunit|Page|Report|Query|XmlPort|Interface|Enum|RequestPage|TestPage|TestPart|TestRequestPage|ControlAddIn|PermissionSet|Profile)\s+(""(?<q>[^""]+)""|(?<u>[A-Za-z_][A-Za-z0-9_]*))";
        var match = Regex.Match(fileContent, pattern, RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups["q"].Success
            ? match.Groups["q"].Value
            : match.Groups["u"].Value;
    }

    private static readonly Regex VariableObjectTypeRegex = new(
        @"^\s*:\s*(?<kw>Record|Codeunit|Page|TestPage|Report|Query|XmlPort)\s+" +
        @"(?<n>(?:(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*)*(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The object a variable is declared as: its kind (<c>table</c> for a
    /// <c>Record</c>) and name, the last segment of a namespaced type. Unlike
    /// <see cref="ResolveVariableType"/> it keeps the kind, so a table and a
    /// page that share a name cannot be confused, and it never sends the type
    /// name back through a resolver that would find the variable again when
    /// the two are spelled alike (<c>Customer: Record Customer</c>).
    /// The name may be quoted (<c>"Temp Sales Line": Record ...</c>), and a
    /// declaration inside a comment or string is skipped, as in
    /// <see cref="ResolveVariableDeclarationLine"/> (#386).
    /// </summary>
    public static (string Kind, string Name)? ResolveVariableObjectType(string fileContent, string variableName)
    {
        variableName = variableName?.Trim('"') ?? string.Empty;
        if (string.IsNullOrEmpty(fileContent) || string.IsNullOrEmpty(variableName)) return null;
        var escaped = Regex.Escape(variableName);
        // Position-preserving, so indices into it are indices into the file.
        var masked = AlResolvableTokenScanner.MaskCommentsAndStrings(fileContent);
        foreach (Match decl in Regex.Matches(masked, $@"(?<![A-Za-z0-9_""])(?:""{escaped}""|{escaped}(?![A-Za-z0-9_""]))(?=\s*:)", RegexOptions.IgnoreCase))
        {
            var match = VariableObjectTypeRegex.Match(masked[(decl.Index + decl.Length)..Math.Min(masked.Length, decl.Index + decl.Length + 300)]);
            if (!match.Success) continue;
            var full = match.Groups["n"].Value;
            var last = Regex.Match(full, @"(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_]*)$").Value.Trim('"');
            var kind = match.Groups["kw"].Value.ToLowerInvariant() switch
            {
                "record" => "table",
                "testpage" => "page",
                var k => k,
            };
            return (kind, last);
        }
        return null;
    }

    // AL object keywords that can appear before a type name in a var
    // declaration. Captured so the reference classifier can distinguish a
    // var of type `Codeunit "HttpClient"` (real object reference) from a
    // var of type `HttpClient` (runtime / system type sharing the name).
    private static readonly Regex AllObjectVarDeclsRegex = new(
        // The variable may be quoted ("Temp Sales Line": Record ...).
        @"(?:""(?<var>[^""\r\n]+)""|\b(?<var>[A-Za-z_][A-Za-z0-9_]*))\s*:\s*" +
        @"(?:(?<kw>Record|Codeunit|Page|Report|Query|XmlPort|Interface|Enum|RequestPage|TestPage|TestPart|TestRequestPage|ControlAddIn|PermissionSet|Profile)\s+)?" +
        @"(""(?<q>[^""]+)""|(?<u>[A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns the 1-based line number where <paramref name="variableName"/>
    /// is declared in <paramref name="fileContent"/>, or null when no
    /// matching declaration appears. First-match wins — AL almost never
    /// reuses a name across procedures, and when it does the first
    /// declaration is the sensible target for "go to definition".
    /// <para>
    /// Used by the file viewer's Go-to-definition step that lands on a
    /// local-variable token (e.g. clicking <c>PaymentMethod</c> in
    /// <c>PaymentMethod.GET(...)</c>): the user wants the
    /// <c>PaymentMethod: Record "Payment Method"</c> line, not the
    /// <c>Payment Method</c> table source. The corresponding "click on
    /// the underlined type name" path is handled separately by the
    /// object-name resolver.
    /// </para>
    /// </summary>
    public static int? ResolveVariableDeclarationLine(string fileContent, string variableName)
    {
        if (string.IsNullOrEmpty(fileContent) || string.IsNullOrEmpty(variableName)) return null;
        // Mask comments/strings first (position-preserving) so a var name inside
        // a `// PaymentMethod: Record …` comment doesn't mis-resolve. The masked
        // text keeps identical indices, so newline-counting below is unaffected.
        // See issue #386.
        var masked = AlResolvableTokenScanner.MaskCommentsAndStrings(fileContent);
        variableName = variableName.Trim('"');
        foreach (Match m in AllObjectVarDeclsRegex.Matches(masked))
        {
            if (!string.Equals(m.Groups["var"].Value, variableName, StringComparison.OrdinalIgnoreCase)) continue;
            // Count newlines up to the match start. Both `\n` and
            // `\r\n` are handled because `\r\n` contains `\n` so a
            // single count of `\n` characters yields the right line
            // index either way.
            var prefix = fileContent.AsSpan(0, m.Index);
            int line = 1;
            foreach (var ch in prefix)
            {
                if (ch == '\n') line++;
            }
            return line;
        }
        return null;
    }

    private static string? GetLine(string source, int line)
    {
        var i = 0;
        var n = source.Length;
        var current = 1;
        var lineStart = 0;
        while (i < n)
        {
            if (source[i] == '\n')
            {
                if (current == line)
                {
                    var lineEnd = i;
                    if (lineEnd > 0 && source[lineEnd - 1] == '\r') lineEnd--;
                    return source.Substring(lineStart, lineEnd - lineStart);
                }
                current++;
                lineStart = i + 1;
            }
            i++;
        }
        if (current == line)
        {
            return source.Substring(lineStart);
        }
        return null;
    }

    /// <summary>
    /// Extracts the token straddling <paramref name="idx"/> on
    /// <paramref name="line"/>. Handles double-quoted identifiers
    /// (AL's <c>"Sales-Post"</c> form, spaces and hyphens allowed inside)
    /// and bare identifiers. Returns empty string when the index lands on
    /// punctuation or whitespace.
    /// </summary>
    private static (string Word, int Start, int End) ExtractTokenAt(string line, int idx)
    {
        // Quoted identifier: scan for a containing pair of quotes on this line.
        var quoteIdx = FindContainingQuotes(line, idx);
        if (quoteIdx is { } pair)
        {
            return (line.Substring(pair.openIdx + 1, pair.closeIdx - pair.openIdx - 1),
                pair.openIdx, pair.closeIdx + 1);
        }

        // Bare identifier: walk left and right while characters are identifier-like.
        if (!IsIdentifierChar(line[idx])) return (string.Empty, idx, idx);
        var start = idx;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        var end = idx;
        while (end < line.Length - 1 && IsIdentifierChar(line[end + 1])) end++;
        return (line.Substring(start, end - start + 1), start, end + 1);
    }

    private static (int openIdx, int closeIdx)? FindContainingQuotes(string line, int idx)
    {
        // Walk back to a '"'; if we cross a newline (we don't; per-line) or
        // another '"' first, no containment.
        var open = -1;
        for (var j = idx; j >= 0; j--)
        {
            if (line[j] == '"') { open = j; break; }
        }
        if (open < 0) return null;
        // The quote we found could be the closing quote of a string that
        // ends before idx. Check whether there's content between it and the
        // index; if idx == open we're sitting on a quote — still treat the
        // following identifier as the token.
        var close = -1;
        for (var j = Math.Max(open + 1, idx); j < line.Length; j++)
        {
            if (line[j] == '"') { close = j; break; }
        }
        if (close < 0) return null;
        if (idx > close) return null; // idx is past the closing quote
        return (open, close);
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Finds the column span of <paramref name="targetName"/> within an
    /// object-header line, but only when it appears after the <c>extends</c>
    /// keyword. The name may be quoted (the common case for AL names with
    /// spaces, dots, or other special characters) or bare; both forms are
    /// returned with their 1-based ColumnStart and exclusive-end column.
    /// Returns null when the line doesn't actually contain an <c>extends</c>
    /// followed by this target — defensive in case the header was reformatted
    /// between import and source storage.
    /// </summary>
    internal static (int Start, int End)? FindExtendsTargetSpan(string lineText, string targetName)
    {
        const string keyword = "extends";
        var kwIdx = lineText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (kwIdx < 0) return null;
        var after = kwIdx + keyword.Length;
        // `extends` must be followed by whitespace, then the name. If
        // the keyword appears as part of another identifier (very
        // unlikely on an object-header line, but cheap to guard
        // against) bail out.
        if (after < lineText.Length && IsIdentifierChar(lineText[after])) return null;
        while (after < lineText.Length && char.IsWhiteSpace(lineText[after])) after++;
        if (after >= lineText.Length) return null;

        var quotedTarget = "\"" + targetName + "\"";
        var quotedIdx = lineText.IndexOf(quotedTarget, after, StringComparison.Ordinal);
        if (quotedIdx >= 0)
        {
            return (quotedIdx + 1, quotedIdx + 1 + quotedTarget.Length);
        }
        var bareIdx = IndexOfWord(lineText, targetName, after);
        if (bareIdx >= 0)
        {
            return (bareIdx + 1, bareIdx + 1 + targetName.Length);
        }
        return null;
    }

    /// <summary>
    /// Word-boundary aware IndexOf — finds <paramref name="word"/> in
    /// <paramref name="haystack"/> starting at <paramref name="start"/> only
    /// when the surrounding characters aren't AL identifier characters
    /// (letter, digit, underscore). Stops <c>Insert</c> from matching inside
    /// <c>InsertRecord</c>.
    /// </summary>
    internal static int IndexOfWord(string haystack, string word, int start)
    {
        var i = start;
        while (i <= haystack.Length - word.Length)
        {
            var idx = haystack.IndexOf(word, i, StringComparison.Ordinal);
            if (idx < 0) return -1;
            var before = idx == 0 || !IsIdentifierChar(haystack[idx - 1]);
            var after = idx + word.Length == haystack.Length
                || !IsIdentifierChar(haystack[idx + word.Length]);
            if (before && after) return idx;
            i = idx + 1;
        }
        return -1;
    }

    /// <summary>
    /// Reads the meaningful characters immediately before <paramref name="wordStart"/>
    /// on the line. Used to decide whether the click landed on
    /// <c>X.Word</c> (qualified call), <c>Codeunit::"Word"</c> (typed
    /// reference), or <c>Codeunit "Word"</c> (object declaration / var).
    /// </summary>
    private static GoToDefinitionLeftContext ReadLeftContext(string line, int wordStart)
    {
        var i = wordStart - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        if (i < 0) return new GoToDefinitionLeftContext(null, null);

        // `.` operator — member access. Capture the qualifier identifier.
        if (line[i] == '.')
        {
            var qend = i - 1;
            while (qend >= 0 && char.IsWhiteSpace(line[qend])) qend--;
            if (qend < 0) return new GoToDefinitionLeftContext(".", null);
            // Quoted qualifier?
            if (line[qend] == '"')
            {
                var qopen = qend - 1;
                while (qopen >= 0 && line[qopen] != '"') qopen--;
                if (qopen < 0) return new GoToDefinitionLeftContext(".", null);
                var qualifier = line.Substring(qopen + 1, qend - qopen - 1);
                return new GoToDefinitionLeftContext(".", qualifier);
            }
            // Bare identifier qualifier.
            var qstart = qend;
            while (qstart > 0 && IsIdentifierChar(line[qstart - 1])) qstart--;
            return new GoToDefinitionLeftContext(".", line.Substring(qstart, qend - qstart + 1));
        }

        // `::` — typed reference like `Codeunit::"Sales-Post"`.
        if (i >= 1 && line[i] == ':' && line[i - 1] == ':')
        {
            var qend = i - 2;
            while (qend >= 0 && char.IsWhiteSpace(line[qend])) qend--;
            if (qend < 0) return new GoToDefinitionLeftContext("::", null);
            var qstart = qend;
            while (qstart > 0 && IsIdentifierChar(line[qstart - 1])) qstart--;
            var keyword = line.Substring(qstart, qend - qstart + 1);
            return new GoToDefinitionLeftContext("::",
                ObjectKeywords.Contains(keyword) ? keyword : null);
        }

        // Trailing identifier — possibly an object-reference keyword like
        // <c>Codeunit "Sales-Post"</c> (declaration / var typing).
        var kend = i;
        while (kend >= 0 && IsIdentifierChar(line[kend])) kend--;
        var kstart = kend + 1;
        var kendInclusive = i;
        var keywordCandidate = line.Substring(kstart, kendInclusive - kstart + 1);
        if (ObjectKeywords.Contains(keywordCandidate))
        {
            return new GoToDefinitionLeftContext("keyword", keywordCandidate);
        }

        return new GoToDefinitionLeftContext(null, null);
    }
}

/// <summary>
/// What <see cref="AlGoToDefinitionLocator.Inspect"/> found at the click
/// position. The caller combines <see cref="Word"/> and
/// <see cref="LeftContext"/> to pick the right symbol-table query.
/// </summary>
public sealed record GoToDefinitionClick(
    string Word,
    string LineText,
    GoToDefinitionLeftContext LeftContext);

/// <summary>
/// Context immediately to the left of the clicked token. <see cref="Operator"/>
/// is one of <c>.</c>, <c>::</c>, <c>keyword</c>, or <c>null</c> (nothing
/// meaningful). <see cref="Qualifier"/> is the identifier on the other side
/// of the operator (when applicable).
/// </summary>
public sealed record GoToDefinitionLeftContext(string? Operator, string? Qualifier);

/// <summary>
/// Resolved type of a variable declared in an AL file. <see cref="Keyword"/>
/// is one of the AL object keywords (<c>Codeunit</c>, <c>Record</c>,
/// <c>Page</c>, …) when the declaration spelled one out, or <c>null</c>
/// when the type was a bare identifier like <c>HttpClient</c> or
/// <c>JsonObject</c>. <see cref="TypeName"/> is the type's name itself.
/// </summary>
public sealed record ResolvedVariableType(string? Keyword, string TypeName);
