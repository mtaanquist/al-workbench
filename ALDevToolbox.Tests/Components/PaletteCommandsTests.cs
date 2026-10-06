using System.Reflection;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Navigation;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The fence round the palette's Commands group. <b>No command writes to a
/// customer's tenant</b> (<c>.design/command-palette.md</c>, "What it never
/// does"), and these tests are what keeps that true as the list grows: a
/// navigate command may only open a page that exists - every write on it still
/// sits behind the page's own confirm - and an act command may only name one of
/// the actions the script knows, a list with no tenant write on it. A new
/// action has to be added to both lists on purpose, in a diff someone reads.
/// </summary>
public sealed class PaletteCommandsTests
{
    /// <summary>
    /// The whole list of things a command may do without navigating. Written
    /// out here rather than read from <see cref="PaletteCommands.Actions"/>, so
    /// widening the server's list alone turns this test red.
    /// </summary>
    private static readonly string[] SanctionedActions =
        ["theme:light", "theme:dark", "theme:system", "copy-link", "sign-out", "refresh"];

    [Fact]
    public void Every_command_either_navigates_or_acts_never_both()
    {
        var malformed = PaletteCommands.All
            .Where(c => (c.Href is null) == (c.Action is null))
            .Select(c => c.Label)
            .ToList();

        malformed.Should().BeEmpty("a command is a link or an action: {0}", string.Join(", ", malformed));
    }

    [Fact]
    public void Every_navigate_command_opens_a_real_page()
    {
        var routes = RoutableTemplates();
        routes.Should().HaveCountGreaterThan(50,
            "a silently empty route set would make this assertion pass vacuously");

        var broken = PaletteCommands.All
            .Where(c => c.Href is not null && !routes.Any(r => Matches(r, PathOf(c.Href))))
            .Select(c => $"{c.Label} -> {c.Href}")
            .ToList();

        broken.Should().BeEmpty(
            "a wrong href renders perfectly and 404s on click. Broken: {0}", string.Join(", ", broken));
    }

    [Fact]
    public void Every_act_command_is_on_the_sanctioned_list_which_writes_to_no_tenant()
    {
        PaletteCommands.Actions.Should().BeEquivalentTo(SanctionedActions,
            "the script runs exactly these, and none of them may write to a customer's tenant. "
            + "Adding one is a design decision: change the design doc's \"Commands\" section too");

        PaletteCommands.All
            .Where(c => c.Action is not null)
            .Select(c => c.Action!)
            .Should().OnlyContain(a => SanctionedActions.Contains(a));
    }

    /// <summary>
    /// The script keeps its own copy of the list, because it refuses any other
    /// action even when the markup asks for one. The two copies have to agree,
    /// or a command renders and then silently does nothing.
    /// </summary>
    [Fact]
    public void The_script_runs_the_same_actions_and_no_others()
    {
        var script = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "wwwroot", "command-palette.js"));
        var match = Regex.Match(script, @"const ACTIONS = \[(?<list>[^\]]*)\];");
        match.Success.Should().BeTrue("command-palette.js declares its allow-list as `const ACTIONS = [...]`");

        var inScript = Regex.Matches(match.Groups["list"].Value, "\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        inScript.Should().BeEquivalentTo(SanctionedActions);
    }

    [Fact]
    public void Every_command_has_a_vendored_icon_and_a_unique_label()
    {
        var icons = Directory
            .EnumerateFiles(RepoRoot.Combine("ALDevToolbox", "Resources", "Icons"), "*.svg")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        PaletteCommands.All.Where(c => !icons.Contains(c.Icon)).Select(c => c.Label)
            .Should().BeEmpty("an icon with no SVG renders as an invisible placeholder");
        PaletteCommands.All.Select(c => c.Label).Should().OnlyHaveUniqueItems();
    }

    private static string PathOf(string href)
    {
        var query = href.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? href : href[..query];
    }

    /// <summary>
    /// A route template matches a path segment by segment, a parameter segment matching
    /// any one segment: the account tabs are one route, <c>/account/{SectionName}</c>
    /// (#1088), and the palette links straight to them.
    /// </summary>
    private static bool Matches(string template, string path)
    {
        var want = template.Split('/');
        var have = path.Split('/');
        return want.Length == have.Length
            && want.Zip(have).All(p => p.First.StartsWith('{') || string.Equals(p.First, p.Second, StringComparison.Ordinal));
    }

    /// <summary>Every route the app assembly exposes.</summary>
    private static HashSet<string> RoutableTemplates() =>
        typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true))
            .Select(r => r.Template)
            .ToHashSet(StringComparer.Ordinal);
}
