using System.Text;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// The source reading the tenant-fence baselines share
/// (<see cref="IgnoreQueryFiltersBaselineTests"/>, <see cref="RawSqlBaselineTests"/>,
/// <see cref="FolderTreeOrgFilterCallerTests"/>): which files count as app source,
/// what is code and what is comment, and which member a line sits in.
///
/// <para>A reader, not a parser. It knows enough C# and Razor to keep string
/// and char literals from being read as comments (<c>"https://..."</c>, or
/// <c>$"{bare}/*"</c> opening a block comment that hid thirty lines of a
/// page), and enough about declarations to name the member a line sits in.
/// Shapes it does not try to tell apart, because none of them carries a fence
/// crossing today and a wrong key there is stable rather than silent: a local
/// function with a modifier (<c>static async Task Local()</c>) reads as its own
/// member; a lambda in a field initialiser takes the name of the declaration
/// above the field; an attribute on the same line as its member is fine but
/// hides nothing; and overloads share one key, so a bypass moved between two
/// overloads of one name does not show.</para>
/// </summary>
internal static class FenceSourceScan
{
    /// <summary>One source line with its comments split off. String literals stay in <see cref="Code"/>.</summary>
    internal sealed record Line(string Code, bool HasComment, string Comment)
    {
        /// <summary>A line holding only a comment, no code.</summary>
        public bool IsPureComment => HasComment && Code.Trim().Length == 0;
    }

    /// <summary>One app source file, relative to the repository root with forward slashes.</summary>
    internal sealed record SourceFile(string Relative, Line[] Lines);

    private static readonly Lazy<IReadOnlyList<SourceFile>> Sources = new(LoadSources);

    /// <summary>
    /// Every app source file, read once per test run and shared by the fence
    /// tests. Generated code is out of scope: EF migrations write their own
    /// queries, and build output is not source.
    /// </summary>
    public static IReadOnlyList<SourceFile> AppSources => Sources.Value;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.Ordinal) { "obj", "bin", "Migrations" };

    private static IReadOnlyList<SourceFile> LoadSources()
    {
        var root = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory.FullName;
        var files = new List<SourceFile>();
        var pending = new Stack<string>();
        pending.Push(Path.Combine(root, "ALDevToolbox"));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub))) pending.Push(sub);
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) continue;
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                files.Add(new SourceFile(relative, Read(File.ReadAllLines(file))));
            }
        }
        return files.OrderBy(f => f.Relative, StringComparer.Ordinal).ToList();
    }

    private enum Mode { Code, BlockComment, Verbatim, Raw }

    /// <summary>
    /// Splits comments off the way a reader does: <c>//</c> to end of line,
    /// plus <c>/* */</c> and Razor's <c>@* *@</c> blocks, none of which count
    /// inside a string (<c>"..."</c>, <c>@"..."</c>, <c>$"..."</c> with its
    /// holes, <c>"""</c> raw strings) or a char literal.
    /// </summary>
    public static Line[] Read(IReadOnlyList<string> raw)
    {
        var lines = new Line[raw.Count];
        var mode = Mode.Code;
        var blockEnd = "*/";
        var rawQuotes = 0;

        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            var code = new StringBuilder(line.Length);
            var comment = new StringBuilder();
            var hasComment = mode == Mode.BlockComment;
            var j = 0;
            while (j < line.Length)
            {
                var c = line[j];
                switch (mode)
                {
                    case Mode.BlockComment:
                        if (Starts(line, j, blockEnd)) { mode = Mode.Code; j += 2; }
                        else { comment.Append(c); j++; }
                        continue;
                    case Mode.Verbatim:
                        code.Append(c);
                        if (c == '"')
                        {
                            if (j + 1 < line.Length && line[j + 1] == '"') { code.Append('"'); j += 2; continue; }
                            mode = Mode.Code;
                        }
                        j++;
                        continue;
                    case Mode.Raw:
                        if (CountQuotes(line, j) >= rawQuotes)
                        {
                            code.Append('"', rawQuotes);
                            j += rawQuotes;
                            mode = Mode.Code;
                        }
                        else { code.Append(c); j++; }
                        continue;
                }

                if (Starts(line, j, "//"))
                {
                    hasComment = true;
                    comment.Append(line, j, line.Length - j);
                    break;
                }
                if (Starts(line, j, "/*") || Starts(line, j, "@*"))
                {
                    hasComment = true;
                    blockEnd = line[j] == '/' ? "*/" : "*@";
                    mode = Mode.BlockComment;
                    j += 2;
                    continue;
                }

                var quote = j;
                while (quote < line.Length && line[quote] is '$' or '@') quote++;
                if (quote < line.Length && line[quote] == '"' && quote - j <= 3)
                {
                    var prefix = line.Substring(j, quote - j);
                    var quotes = CountQuotes(line, quote);
                    code.Append(prefix);
                    if (quotes >= 3)
                    {
                        code.Append('"', quotes);
                        rawQuotes = quotes;
                        mode = Mode.Raw;
                        j = quote + quotes;
                    }
                    else if (prefix.Contains('@') && quotes == 1)
                    {
                        code.Append('"');
                        mode = Mode.Verbatim;
                        j = quote + 1;
                    }
                    else
                    {
                        j = ReadRegularString(line, quote, prefix.Contains('$'), code);
                    }
                    continue;
                }

                if (c == '\'' && CharLiteralLength(line, j) is var length and > 0)
                {
                    code.Append(line, j, length);
                    j += length;
                    continue;
                }

                code.Append(c);
                j++;
            }
            lines[i] = new Line(code.ToString(), hasComment, comment.ToString());
        }
        return lines;
    }

    /// <summary>
    /// Copies the one-line string whose quote is at <paramref name="start"/>,
    /// stepping over escapes and, in an interpolated string, over the holes
    /// and the strings inside them. Returns the index after the closing quote,
    /// or the line's end for an unterminated one.
    /// </summary>
    private static int ReadRegularString(string line, int start, bool interpolated, StringBuilder code)
    {
        code.Append('"');
        var i = start + 1;
        while (i < line.Length)
        {
            var c = line[i];
            if (c == '\\' && i + 1 < line.Length) { code.Append(line, i, 2); i += 2; continue; }
            if (c == '"') { code.Append(c); return i + 1; }
            if (interpolated && c == '{')
            {
                if (i + 1 < line.Length && line[i + 1] == '{') { code.Append("{{"); i += 2; continue; }
                var depth = 0;
                while (i < line.Length)
                {
                    var h = line[i];
                    if (h == '"') { i = ReadRegularString(line, i, false, code); continue; }
                    code.Append(h);
                    i++;
                    if (h == '{') depth++;
                    else if (h == '}' && --depth == 0) break;
                }
                continue;
            }
            code.Append(c);
            i++;
        }
        return i;
    }

    /// <summary>
    /// The length of the char literal at <paramref name="start"/> (<c>'a'</c>,
    /// <c>'\''</c>, <c>'A'</c>), or 0 when the quote is an apostrophe in
    /// markup text.
    /// </summary>
    private static int CharLiteralLength(string line, int start)
    {
        if (start + 2 < line.Length && line[start + 1] != '\\' && line[start + 2] == '\'') return 3;
        if (start + 3 < line.Length && line[start + 1] == '\\')
        {
            var close = line.IndexOf('\'', start + 3);
            if (close > 0 && close - start <= 9) return close - start + 1;
        }
        return 0;
    }

    private static int CountQuotes(string line, int start)
    {
        var n = 0;
        while (start + n < line.Length && line[start + n] == '"') n++;
        return n;
    }

    /// <summary>
    /// The comment that justifies the code on <paramref name="index"/>: one on
    /// that line, or the run of pure comment lines ending within the three lines
    /// above it (the shape CLAUDE.md asks for). <c>null</c> when there is none.
    /// A justification that wraps, or sits under a longer remark, reads as one
    /// comment.
    /// </summary>
    public static string? JustifyingComment(IReadOnlyList<Line> lines, int index)
    {
        var text = new StringBuilder(lines[index].HasComment ? lines[index].Comment : string.Empty);
        var documented = lines[index].HasComment;
        var nearest = -1;
        for (var k = index - 1; k >= Math.Max(0, index - 3); k--)
        {
            if (lines[k].IsPureComment) { nearest = k; break; }
        }
        if (nearest >= 0)
        {
            documented = true;
            for (var k = nearest; k >= 0 && lines[k].IsPureComment; k--)
            {
                text.Insert(0, lines[k].Comment + " ");
            }
        }
        return documented ? text.ToString() : null;
    }

    private const string Modifiers =
        @"(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|abstract|extern|partial|unsafe)\s+)+";

    /// <summary>
    /// A method, constructor or local function: at least one modifier, then
    /// the return type and the name, then the parameter list. Requiring a
    /// modifier keeps calls and object creations (<c>return Foo(</c>) out. The
    /// return type may be a tuple (<c>Task&lt;(User? User, bool Ok)&gt;</c>).
    /// </summary>
    private static readonly Regex MethodDeclaration =
        new(@"^\s*" + Modifiers + @"(?:\([^()]*\)|[^=;()""])*?\b([A-Za-z_]\w*)\s*(?:<[^()]*>)?\s*\(", RegexOptions.Compiled);

    /// <summary>A property: modifiers, a type and a name, then a body or an arrow.</summary>
    private static readonly Regex PropertyDeclaration =
        new(@"^\s*" + Modifiers + @"[\w<>\[\],.?\s]+?\s([A-Za-z_]\w*)\s*(?:=>|\{\s*(?:get|set|init)?|$)", RegexOptions.Compiled);

    /// <summary>
    /// A minimal-API route whose handler is a lambda: a literal path
    /// (<c>MapPost("/auth/login"</c>) or a constant (<c>MapGet(SetupCallbackPath,</c>).
    /// </summary>
    private static readonly Regex RouteMapping =
        new(@"\.Map(\w*)\s*\(\s*(?:""([^""]*)""|([A-Za-z_][\w.]*)\s*,)", RegexOptions.Compiled);

    /// <summary>True when <paramref name="code"/> declares a method (named <paramref name="name"/>, if given).</summary>
    public static bool IsMethodDeclaration(string code, string? name = null) =>
        MethodDeclaration.Match(code) is { Success: true } m && (name is null || m.Groups[1].Value == name);

    /// <summary>
    /// The member the code on <paramref name="index"/> sits in: the nearest
    /// declaration at or above it, or the nearest minimal-API route when that
    /// is closer, so two handlers in one <c>Map*Endpoints</c> method stay
    /// apart. Only has to be stable: a site moved to another member changes
    /// the key and the baseline diff shows it.
    /// </summary>
    public static string EnclosingMember(IReadOnlyList<Line> lines, int index)
    {
        for (var k = index; k >= 0; k--)
        {
            var code = lines[k].Code;
            var route = RouteMapping.Match(code);
            if (route.Success)
            {
                var path = route.Groups[2].Success ? route.Groups[2].Value : route.Groups[3].Value;
                return $"Map{route.Groups[1].Value} {path}";
            }
            var method = MethodDeclaration.Match(code);
            // A long return type pushes the name onto the next line.
            if (!method.Success && k < index && !code.Contains(';'))
            {
                method = MethodDeclaration.Match(code + " " + lines[k + 1].Code.Trim());
            }
            if (method.Success) return method.Groups[1].Value;
            var property = PropertyDeclaration.Match(code);
            if (property.Success) return property.Groups[1].Value;
        }
        return "(file scope)";
    }

    /// <summary>
    /// Compares sites found (file, member, the lines they sit on) with a
    /// reviewed baseline of counts, as the fence baselines do. Each problem
    /// names the lines, and a file whose total is unchanged but whose members
    /// moved is reported once, as a likely rename, with the entry to paste.
    /// </summary>
    public static List<string> CompareWithBaseline(
        IReadOnlyDictionary<string, Dictionary<string, List<int>>> found,
        Func<string, IReadOnlyDictionary<string, int>?> reviewedFor,
        IEnumerable<string> reviewedFiles,
        string grewAdvice,
        string shrankAdvice)
    {
        var problems = new List<string>();
        foreach (var path in found.Keys.Union(reviewedFiles).OrderBy(p => p, StringComparer.Ordinal))
        {
            var now = found.GetValueOrDefault(path) ?? new Dictionary<string, List<int>>();
            var reviewed = reviewedFor(path) ?? new Dictionary<string, int>();
            var changed = now.Keys.Union(reviewed.Keys)
                .Where(m => (now.GetValueOrDefault(m)?.Count ?? 0) != reviewed.GetValueOrDefault(m))
                .OrderBy(m => m, StringComparer.Ordinal)
                .ToList();
            if (changed.Count == 0) continue;

            string Describe(string m) =>
                $"{m}: baseline {reviewed.GetValueOrDefault(m)}, found {now.GetValueOrDefault(m)?.Count ?? 0}" +
                (now.TryGetValue(m, out var at) ? " at " + string.Join(", ", at.Select(l => $"{path}:{l}")) : string.Empty);

            var entry = $"[\"{path}\"] = new() {{ " +
                        string.Join(", ", now.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"[\"{e.Key}\"] = {e.Value.Count}")) +
                        " }";
            var totalNow = now.Values.Sum(v => v.Count);
            if (reviewed.Count > 0 && totalNow == reviewed.Values.Sum())
            {
                problems.Add(
                    $"{path}: still {totalNow} site(s), but under different members - member renamed? move the entry. " +
                    string.Join("; ", changed.Select(Describe)) + $". Regenerated entry: {entry}");
                continue;
            }
            foreach (var member in changed)
            {
                var grew = (now.GetValueOrDefault(member)?.Count ?? 0) > reviewed.GetValueOrDefault(member);
                problems.Add($"{path} in {Describe(member)}. {(grew ? grewAdvice : shrankAdvice)} Regenerated entry: {entry}");
            }
        }
        return problems;
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;
}
