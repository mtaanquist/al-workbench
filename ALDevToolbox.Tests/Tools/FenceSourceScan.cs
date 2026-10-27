using System.Text;
using System.Text.RegularExpressions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// The source reading the tenant-fence baselines share
/// (<see cref="IgnoreQueryFiltersBaselineTests"/>, <see cref="RawSqlBaselineTests"/>,
/// <see cref="FolderTreeOrgFilterCallerTests"/>): which files count as app source,
/// what is code and what is comment, and which member a line sits in.
/// </summary>
internal static class FenceSourceScan
{
    /// <summary>One source line with its comments split off.</summary>
    internal sealed record Line(string Code, bool HasComment, string Comment)
    {
        /// <summary>A line holding only a comment, no code.</summary>
        public bool IsPureComment => HasComment && Code.Trim().Length == 0;
    }

    /// <summary>
    /// App sources, relative to the repository root with forward slashes.
    /// Generated code is out of scope: EF migrations write their own queries,
    /// and build output is not source.
    /// </summary>
    public static IEnumerable<(string Relative, string FullPath)> AppSourceFiles()
    {
        var root = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory.FullName;
        var app = Path.Combine(root, "ALDevToolbox");
        return Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Select(p => (Relative: Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/'), FullPath: p))
            .Where(f =>
            {
                var segments = f.Relative.Split('/');
                return !segments.Contains("obj") && !segments.Contains("bin") && !segments.Contains("Migrations");
            })
            .OrderBy(f => f.Relative, StringComparer.Ordinal);
    }

    /// <summary>
    /// Splits comments off the way a reader does: <c>//</c> to end of line,
    /// plus <c>/* */</c> and Razor's <c>@* *@</c> blocks.
    /// </summary>
    public static Line[] Read(IReadOnlyList<string> raw)
    {
        var lines = new Line[raw.Count];
        var inBlock = false;

        for (var i = 0; i < raw.Count; i++)
        {
            var line = raw[i];
            var buffer = new StringBuilder(line.Length);
            var comment = new StringBuilder();
            var hasComment = false;
            var j = 0;
            while (j < line.Length)
            {
                if (inBlock)
                {
                    hasComment = true;
                    if (Starts(line, j, "*/") || Starts(line, j, "*@")) { inBlock = false; j += 2; }
                    else { comment.Append(line[j]); j++; }
                }
                else if (Starts(line, j, "//"))
                {
                    hasComment = true;
                    comment.Append(line, j, line.Length - j);
                    break;
                }
                else if (Starts(line, j, "/*") || Starts(line, j, "@*"))
                {
                    hasComment = true;
                    inBlock = true;
                    j += 2;
                }
                else
                {
                    buffer.Append(line[j]);
                    j++;
                }
            }
            lines[i] = new Line(buffer.ToString(), hasComment, comment.ToString());
        }
        return lines;
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

    /// <summary>A minimal-API route (<c>MapPost("/auth/login"</c>), whose handler is a lambda.</summary>
    private static readonly Regex RouteMapping =
        new(@"\.Map(Get|Post|Put|Delete|Patch|Methods)?\s*\(\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>
    /// The member the code on <paramref name="index"/> sits in: the nearest
    /// declaration at or above it, or the nearest minimal-API route when that
    /// is closer, so two handlers in one <c>Map*Endpoints</c> method stay
    /// apart. A heuristic, not a parser - it only has to be stable, so a site
    /// moved to another member changes the key and the baseline diff shows it.
    /// </summary>
    public static string EnclosingMember(IReadOnlyList<Line> lines, int index)
    {
        for (var k = index; k >= 0; k--)
        {
            var code = lines[k].Code;
            var route = RouteMapping.Match(code);
            if (route.Success) return $"Map{route.Groups[1].Value} {route.Groups[2].Value}";
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
    /// The statement starting on <paramref name="start"/>: lines up to and
    /// including the first one ending in a semicolon. Bounded so a malformed
    /// read can't swallow the rest of the file.
    /// </summary>
    public static string ReadStatement(IReadOnlyList<Line> lines, int start)
    {
        var parts = new List<string>();
        for (var i = start; i < lines.Count && i < start + 30; i++)
        {
            parts.Add(lines[i].Code);
            if (lines[i].Code.TrimEnd().EndsWith(';')) break;
        }
        return string.Join("\n", parts);
    }

    public static int CountOccurrences(string line, string token)
    {
        var count = 0;
        var index = line.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = line.IndexOf(token, index + token.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;
}
