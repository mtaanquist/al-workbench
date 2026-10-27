using System.Text;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Keeps the house style for user-facing copy mechanical instead of aspirational.
///
/// <para>CLAUDE.md asks visible strings to use ASCII punctuation — straight
/// quotes and <c>...</c> rather than the ellipsis character — with the em dash
/// and the arrow the two sanctioned exceptions, and to use CRONUS rather than
/// Acme for placeholder company names. Those rules used to be enforced by
/// remembering to grep before committing, which is how issue #703 found an en
/// dash in four validation messages and in three MCP tool descriptions that
/// agents parse.</para>
///
/// <para><b>What this scans, and what it deliberately cannot.</b> It reads the
/// app project's <c>.cs</c>, <c>.razor</c> and <c>.js</c> sources, strips comments the way
/// <see cref="IgnoreQueryFiltersBaselineTests"/> does, and flags the banned
/// characters in what is left — string literals and Razor markup, which is
/// where user-facing copy lives. The stripper is not a C# parser: a <c>//</c>
/// inside a string literal (a URL, say) ends the "line" early, so a violation
/// hiding after one is missed, and a <c>/*</c> inside a string or a JavaScript
/// regex opens a comment that blanks everything up to the next <c>*/</c>
/// (<c>code-editor.js</c> has one). That trade is deliberate — a whole-line scan
/// that under-reports is easier to trust than a parser that fires on code
/// nobody reads. Comments are out of scope on purpose: no user sees them, and
/// sweeping them would bury the real hits in churn.</para>
///
/// <para>The test project is not scanned. Its fixtures use "Acme" as arbitrary
/// test data, which is not copy anyone reads.</para>
/// </summary>
public sealed class HouseStyleCopyTests
{
    /// <summary>
    /// Characters that must not appear in copy, with the ASCII replacement the
    /// house style wants. The em dash (U+2014) and the arrow (U+2192) are the
    /// sanctioned non-ASCII characters and are deliberately absent.
    /// </summary>
    private static readonly (char Character, string Name, string Fix)[] Banned =
    [
        ('–', "en dash", "write ranges with an ASCII hyphen, e.g. 2-80"),
        ('…', "ellipsis character", "write three ASCII dots, e.g. Loading..."),
        ('‘', "left curly single quote", "use a straight quote (')"),
        ('’', "right curly single quote", "use a straight quote (')"),
        ('“', "left curly double quote", "use a straight quote (\")"),
        ('”', "right curly double quote", "use a straight quote (\")"),
    ];

    /// <summary>
    /// Placeholder company names use CRONUS, the standard Business Central demo
    /// company. The ban is on the placeholder name only — ACME as part of a
    /// protocol or identifier (the ACME certificate protocol, the
    /// <c>ACME_EMAIL</c> variable Caddy reads) is correct, and belongs in
    /// <see cref="Baseline"/> if it ever lands in a scanned source file.
    /// </summary>
    private static readonly Regex Acme = new("acme", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Findings per scanned file that are known and accepted, relative to the
    /// repository root with forward slashes. Empty today: every hit outside a
    /// comment was fixed in #703. An entry belongs here only when the scan is
    /// wrong about the line (a legitimate ACME protocol reference, or a literal
    /// the comment stripper mangles) — never to park a real violation.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Baseline =
        new Dictionary<string, int>(StringComparer.Ordinal);

    [Fact]
    public void User_facing_copy_uses_ascii_punctuation_and_the_house_placeholder_name()
    {
        var findings = ScanRepository();
        var counts = findings
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var group in findings.GroupBy(f => f.Path, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var allowed = Baseline.TryGetValue(group.Key, out var b) ? b : 0;
            if (group.Count() <= allowed) continue;
            foreach (var finding in group)
            {
                problems.Add($"{finding.Path}:{finding.Line}: {finding.Problem} — {finding.Fix}. Line: {finding.Excerpt}");
            }
        }

        var stale = Baseline.Keys
            .Where(p => !counts.ContainsKey(p) || counts[p] < Baseline[p])
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        foreach (var path in stale)
        {
            problems.Add(
                $"{path}: the baseline allows {Baseline[path]} finding(s) but the scan sees " +
                $"{(counts.TryGetValue(path, out var c) ? c : 0)}. Good news, but lower or drop the entry so the baseline stays honest.");
        }

        problems.Should().BeEmpty(
            "user-facing copy uses ASCII punctuation (the em dash and the arrow excepted) and CRONUS, not Acme — see CLAUDE.md");
    }

    [Fact]
    public void The_scan_sees_copy_and_skips_comments()
    {
        // Guards the guard: if the comment stripper ever swallowed everything,
        // the test above would pass on a tree full of violations.
        var lines = new[]
        {
            "// A comment with an en dash 2–80 and an ellipsis…",
            "/// <summary>Docs about a 1–3 sentence description.</summary>",
            "var caption = \"Full name must be 2–80 characters.\";",
            "var fine = \"Full name must be 2-80 characters — no more.\";",
        };

        var findings = Scan(lines, "Sample.cs").ToList();

        findings.Should().ContainSingle("only the string literal is copy a user reads");
        findings[0].Line.Should().Be(3);
        findings[0].Problem.Should().Contain("en dash");
    }

    private sealed record Finding(string Path, int Line, string Problem, string Fix, string Excerpt);

    private static List<Finding> ScanRepository()
    {
        var root = RepoRoot();
        var app = Path.Combine(root, "ALDevToolbox");
        var findings = new List<Finding>();
        var files = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
                        // The scripts set text on screen themselves (the command
                        // palette's "No search results", the import progress
                        // labels), and their comments strip the same way.
                        || p.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
            .Where(p => !IsExcluded(root, p))
            .OrderBy(p => p, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            findings.AddRange(Scan(File.ReadAllLines(file), relative));
        }
        return findings;
    }

    /// <summary>
    /// Generated code is out of scope: EF migrations are machine-written and
    /// build output is not source. So are the vendored bundles under
    /// <c>wwwroot/lib</c>, which are someone else's minified code.
    /// </summary>
    private static bool IsExcluded(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        var segments = relative.Split('/');
        return segments.Contains("obj") || segments.Contains("bin") || segments.Contains("Migrations")
            || relative.StartsWith("ALDevToolbox/wwwroot/lib/", StringComparison.Ordinal);
    }

    private static IEnumerable<Finding> Scan(IReadOnlyList<string> raw, string path)
    {
        var code = StripComments(raw);
        for (var i = 0; i < code.Count; i++)
        {
            var line = code[i];
            if (line.Length == 0) continue;

            foreach (var (character, name, fix) in Banned)
            {
                if (line.IndexOf(character) < 0) continue;
                yield return new Finding(path, i + 1, $"contains the {name} (U+{(int)character:X4})", fix, Excerpt(raw[i]));
            }

            if (Acme.IsMatch(line))
            {
                yield return new Finding(
                    path, i + 1, "uses Acme as a placeholder name",
                    "use CRONUS, the standard Business Central demo company", Excerpt(raw[i]));
            }
        }
    }

    private static string Excerpt(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 120 ? trimmed : trimmed[..120] + "...";
    }

    /// <summary>
    /// Blanks out <c>//</c> to end of line plus <c>/* */</c> and Razor's
    /// <c>@* *@</c> blocks, keeping line numbering intact. Same shape as
    /// <see cref="IgnoreQueryFiltersBaselineTests"/>, and the same caveat: it
    /// reads left to right without tracking string literals.
    /// </summary>
    private static List<string> StripComments(IReadOnlyList<string> raw)
    {
        var code = new List<string>(raw.Count);
        var inBlock = false;

        foreach (var line in raw)
        {
            var buffer = new StringBuilder(line.Length);
            var j = 0;
            while (j < line.Length)
            {
                if (inBlock)
                {
                    if (Starts(line, j, "*/") || Starts(line, j, "*@")) { inBlock = false; j += 2; }
                    else j++;
                }
                else if (Starts(line, j, "//"))
                {
                    break;
                }
                else if (Starts(line, j, "/*") || Starts(line, j, "@*"))
                {
                    inBlock = true;
                    j += 2;
                }
                else
                {
                    buffer.Append(line[j]);
                    j++;
                }
            }
            code.Add(buffer.ToString());
        }
        return code;
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;

    private static string RepoRoot()
    {
        var dir = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
