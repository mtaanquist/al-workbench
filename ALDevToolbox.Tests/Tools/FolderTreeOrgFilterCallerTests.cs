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
            "passing ignoreOrgFilter to FolderTreeHydrator reads every organisation's folder tree; only the " +
            "system-org fork in TemplateImportService may do it (CLAUDE.md, fence category 5)");
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

    private static List<Call> Calls()
    {
        var positions = FlagPositions();
        var calls = new List<Call>();
        foreach (var (relative, full) in FenceSourceScan.AppSourceFiles())
        {
            var lines = FenceSourceScan.Read(File.ReadAllLines(full));
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (name, position) in positions)
                {
                    var call = Regex.Match(lines[i].Code, @"\b" + name + @"\s*\(");
                    // The declaration itself is not a call.
                    if (!call.Success || Regex.IsMatch(lines[i].Code, @"\b(?:Task|void)\s+" + name + @"\s*\(")) continue;
                    var arguments = ReadArguments(lines, i, call.Index + call.Length);
                    calls.Add(new Call(relative, i + 1, FenceSourceScan.EnclosingMember(lines, i), SetsFlag(arguments, position)));
                }
            }
        }
        calls.Should().NotBeEmpty("the hydrator has callers; finding none means the scan is broken");
        return calls;
    }

    /// <summary>
    /// The top-level arguments of the call whose parenthesis opens just before
    /// <paramref name="start"/> on line <paramref name="line"/>, read across
    /// lines until it closes.
    /// </summary>
    private static List<string> ReadArguments(IReadOnlyList<FenceSourceScan.Line> lines, int line, int start)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inString = false;
        for (var i = line; i < lines.Count && i < line + 20; i++)
        {
            var code = lines[i].Code;
            for (var j = i == line ? start : 0; j < code.Length; j++)
            {
                var c = code[j];
                if (inString)
                {
                    if (c == '"') inString = false;
                    current.Append(c);
                    continue;
                }
                switch (c)
                {
                    case '"':
                        inString = true;
                        break;
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
