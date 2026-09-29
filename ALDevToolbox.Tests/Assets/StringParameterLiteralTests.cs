using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// A <c>string</c> component parameter given a bare attribute value takes it as
/// a LITERAL, not as an expression. <c>&lt;Child Search="_search" /&gt;</c>
/// compiles, renders, and hands the child the seven characters
/// <c>_search</c>; only <c>Search="@_search"</c> passes the field.
///
/// Every other parameter type is safe — Razor treats a non-string attribute
/// value as C#, so <c>Results="_contentResults"</c> works and looks identical.
/// That is what makes this silent: the working line and the broken line differ
/// by nothing you can see, and which one you wrote depends on a type declared
/// in another file.
///
/// It bit the Object Explorer's release page for two releases. <c>CompareBy</c>
/// was permanently the string "_compareBy", so <c>CompareBy == "objects"</c>
/// was never true: choosing "Compare by objects" ran the object query and then
/// drew the (empty) file table. <c>CompareRight</c> was permanently non-blank,
/// so the "pick a release" state was unreachable. <c>Search</c> was permanently
/// "_search", so file-content search never showed its first-run state and its
/// no-results line quoted the field name back at the user. Found in PR 14c by
/// looking at the rendered page, not the markup — nothing in the markup is
/// wrong to read.
/// </summary>
public sealed class StringParameterLiteralTests
{
    [Fact]
    public void No_string_component_parameter_is_handed_a_bare_field_name()
    {
        var components = Path.Combine(Root(), "ALDevToolbox", "Components");
        var files = Directory.EnumerateFiles(components, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".razor") || f.EndsWith(".cs"))
            .ToList();

        // Which parameters each component declares as string / string?.
        var stringParams = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.EndsWith(".razor")) name = Path.GetFileNameWithoutExtension(name);
            foreach (Match m in Regex.Matches(File.ReadAllText(file),
                         @"\[Parameter[^\]]*\]\s*public\s+string\??\s+(?<p>\w+)\s*\{"))
            {
                if (!stringParams.TryGetValue(name, out var set))
                {
                    stringParams[name] = set = new HashSet<string>(StringComparer.Ordinal);
                }
                set.Add(m.Groups["p"].Value);
            }
        }

        var offenders = new List<string>();
        foreach (var file in files.Where(f => f.EndsWith(".razor")))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in Regex.Matches(text, @"<(?<c>[A-Z]\w+)\b(?<a>(?:[^>""]|""[^""]*"")*?)/?>", RegexOptions.Singleline))
            {
                if (!stringParams.TryGetValue(tag.Groups["c"].Value, out var declared)) continue;
                // Not the tail of a directive attribute: in @bind-Value="_field" the bare
                // field is exactly right - @bind- takes an expression, not a string.
                foreach (Match attr in Regex.Matches(tag.Groups["a"].Value, @"(?<![\w@:-])(?<n>\w+)\s*=\s*""(?<v>[^""]*)"""))
                {
                    var value = attr.Groups["v"].Value.Trim();
                    if (!declared.Contains(attr.Groups["n"].Value)) continue;
                    if (value.Contains('@')) continue;
                    // Our own conventions: a private field starts with `_`, and
                    // anything with a dot is a member access. Neither is ever a
                    // literal anyone means to write.
                    if (!Regex.IsMatch(value, @"^(_\w+|\w+(\.\w+)+)$")) continue;

                    var line = text[..tag.Index].Count(c => c == '\n') + 1;
                    offenders.Add($"{Path.GetRelativePath(Root(), file)}:{line}  "
                                + $"<{tag.Groups["c"].Value} {attr.Groups["n"].Value}=\"{value}\">");
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "a string parameter needs the @ - without it the child is handed the "
                   + "field's NAME. Write Param=\"@_field\".");
    }

    private static string Root()
    {
        var dir = RepoRoot.Directory;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
