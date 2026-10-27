using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Services.Templates;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// <see cref="FolderTreeHydrator"/> calls <c>IgnoreQueryFilters()</c> whenever
/// its caller passes <c>ignoreOrgFilter</c>, so every caller that sets the flag
/// crosses the tenant fence without a bypass of its own for
/// <see cref="IgnoreQueryFiltersBaselineTests"/> to count (#1426). This keeps
/// those callers a closed list: the system-org fork in
/// <see cref="TemplateImportService"/> (fence category 5), and nothing else.
/// </summary>
public sealed class FolderTreeOrgFilterCallerTests
{
    private const string FlagName = "ignoreOrgFilter";

    /// <summary>
    /// The members allowed to pass the flag, by file. Add one only with the
    /// maintainer's sign-off, as for a new <c>IgnoreQueryFilters()</c> call.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedCallers =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["ALDevToolbox/Services/Templates/TemplateImportService.cs"] = ["ImportTemplateAsync"],
        };

    [Fact]
    public void Only_the_system_org_fork_passes_ignoreOrgFilter()
    {
        var offenders = Calls()
            .Where(c => c.SetsFlag)
            .Where(c => !(AllowedCallers.TryGetValue(c.Path, out var members) && members.Contains(c.Member)))
            .Select(c => $"{c.Path}:{c.Line} in {c.Member}")
            .ToList();

        offenders.Should().BeEmpty(
            "passing ignoreOrgFilter to FolderTreeHydrator reads every organisation's folder tree. Route a " +
            "system-org read through TemplateImportService (CLAUDE.md, fence category 5); anything else needs " +
            "the maintainer's sign-off and an entry in AllowedCallers");
    }

    [Fact]
    public void Every_allowed_caller_still_passes_the_flag()
    {
        var setters = Calls().Where(c => c.SetsFlag).Select(c => (c.Path, c.Member)).ToHashSet();

        AllowedCallers
            .SelectMany(e => e.Value.Select(m => (Path: e.Key, Member: m)))
            .Where(a => !setters.Contains(a))
            .Should().BeEmpty("the allowed list has to stay honest - drop an entry that no longer passes the flag");
    }

    /// <summary>
    /// The scan has to recognise both ways of setting the flag, or a caller
    /// could pass it positionally and never show up.
    /// </summary>
    [Fact]
    public void A_positional_or_named_flag_counts_as_setting_it()
    {
        var position = FlagPositions()["HydrateExtensionFolderTreeAsync"];

        SetsFlag(["templates", "ct", "true"], position).Should().BeTrue();
        SetsFlag(["templates", "ct", "ignoreOrgFilter: true"], position).Should().BeTrue();
        SetsFlag(["templates", "ct", "pathsOnly: true"], position).Should().BeFalse();
        SetsFlag(["templates", "pathsOnly: true"], position).Should().BeFalse();
    }

    /// <summary>
    /// A declaration with any return type is not a call, and a char or string
    /// argument holding a comma or parenthesis does not shift the positions.
    /// </summary>
    [Fact]
    public void Declarations_are_skipped_and_literal_arguments_do_not_shift_positions()
    {
        string[] source =
        [
            "public sealed class Hydrator",
            "{",
            "    public async ValueTask<int> HydrateExtensionFolderTreeAsync(",
            "        IEnumerable<RuntimeTemplate> templates, CancellationToken ct = default, bool ignoreOrgFilter = false)",
            "    {",
            "        return 0;",
            "    }",
            "",
            "    private async Task SafeAsync(CancellationToken ct)",
            "    {",
            "        await _folderTree.HydrateExtensionFolderTreeAsync(Split(','), ct, pathsOnly: Has(\"),\"));",
            "    }",
            "",
            "    private async Task UnsafeAsync(CancellationToken ct)",
            "    {",
            "        await _folderTree.HydrateExtensionFolderTreeAsync(Split(')'), ct, true);",
            "    }",
            "}",
        ];

        var calls = CallsIn("Hydrator.cs", FenceSourceScan.Read(source), FlagPositions()).ToList();

        calls.Select(c => (c.Line, c.Member, c.SetsFlag)).Should().Equal(
            (11, "SafeAsync", false),
            (16, "UnsafeAsync", true));
    }

    private sealed record Call(string Path, int Line, string Member, bool SetsFlag);

    /// <summary>The hydrator's methods that take the flag, with its parameter index.</summary>
    private static Dictionary<string, int> FlagPositions()
    {
        var positions = typeof(FolderTreeHydrator)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => (m.Name, Parameter: m.GetParameters().FirstOrDefault(p => p.Name == FlagName)))
            .Where(x => x.Parameter is not null)
            .ToDictionary(x => x.Name, x => x.Parameter!.Position, StringComparer.Ordinal);
        positions.Should().NotBeEmpty("FolderTreeHydrator must still take the flag for this test to mean anything");
        return positions;
    }

    private static bool SetsFlag(IReadOnlyList<string> arguments, int position)
    {
        var positional = arguments.TakeWhile(a => !NamedArgument.IsMatch(a)).Count();
        return positional > position
            || arguments.Any(a => NamedArgument.Match(a) is { Success: true } m && m.Groups[1].Value == FlagName);
    }

    private static readonly Regex NamedArgument = new(@"^\s*([A-Za-z_]\w*)\s*:(?!:)");

    private static readonly Lazy<List<Call>> Scanned = new(() =>
    {
        var positions = FlagPositions();
        return FenceSourceScan.AppSources.SelectMany(f => CallsIn(f.Relative, f.Lines, positions)).ToList();
    });

    private static List<Call> Calls()
    {
        var calls = Scanned.Value;
        calls.Should().NotBeEmpty("the hydrator has callers; finding none means the scan is broken");
        return calls;
    }

    private static IEnumerable<Call> CallsIn(string path, FenceSourceScan.Line[] lines, Dictionary<string, int> positions)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (name, position) in positions)
            {
                var call = Regex.Match(lines[i].Code, @"\b" + name + @"\s*\(");
                // The declaration itself is not a call.
                if (!call.Success || FenceSourceScan.IsMethodDeclaration(lines[i].Code, name)) continue;
                var arguments = ReadArguments(lines, i, call.Index + call.Length);
                yield return new Call(path, i + 1, FenceSourceScan.EnclosingMember(lines, i), SetsFlag(arguments, position));
            }
        }
    }

    /// <summary>
    /// The top-level arguments of the call whose parenthesis opens just before
    /// <paramref name="start"/> on line <paramref name="line"/>, read across
    /// lines until it closes. String and char literals are copied whole, so a
    /// comma or bracket inside one does not split an argument.
    /// </summary>
    private static List<string> ReadArguments(IReadOnlyList<FenceSourceScan.Line> lines, int line, int start)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = line; i < lines.Count && i < line + 20; i++)
        {
            var code = lines[i].Code;
            for (var j = i == line ? start : 0; j < code.Length; j++)
            {
                var c = code[j];
                if (c is '"' or '\'')
                {
                    var end = j + 1;
                    while (end < code.Length && code[end] != c)
                    {
                        if (code[end] == '\\') end++;
                        end++;
                    }
                    end = Math.Min(end, code.Length - 1);
                    current.Append(code, j, end - j + 1);
                    j = end;
                    continue;
                }
                switch (c)
                {
                    case '(' or '[' or '{':
                        depth++;
                        break;
                    case ')' or ']' or '}' when depth == 0:
                        if (current.ToString().Trim().Length > 0) arguments.Add(current.ToString().Trim());
                        return arguments;
                    case ')' or ']' or '}':
                        depth--;
                        break;
                    case ',' when depth == 0:
                        arguments.Add(current.ToString().Trim());
                        current.Clear();
                        continue;
                }
                current.Append(c);
            }
            current.Append(' ');
        }
        return arguments;
    }
}
