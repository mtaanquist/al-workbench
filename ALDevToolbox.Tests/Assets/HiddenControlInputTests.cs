using System.Text.RegularExpressions;
using AwesomeAssertions;
using ALDevToolbox.Tests.Infrastructure;

namespace ALDevToolbox.Tests.Assets;

/// <summary>
/// Guards where the hidden real input of a <c>.check</c> or <c>.switch</c> lives.
///
/// components.css collapses that input to a 1px <c>position: absolute</c> box. An
/// absolute box belongs to its nearest positioned ancestor, not to the scroller it
/// sits in, so inside a dialog (where <c>.confirm-dialog</c> is the positioned one)
/// the inputs of a scrolling list stayed where their rows first laid out. Ticking a
/// row far down the upgrade's Add environments picker focused an input below the
/// dialog, and the browser scrolled the whole dialog to show it: the head went off
/// the top and a blank band opened under the footer. Each control is now the
/// containing block of its own input, which holds in any scroller.
/// </summary>
public sealed class HiddenControlInputTests
{
    [Theory]
    [InlineData(".check")]
    [InlineData(".switch")]
    public void The_control_contains_its_own_hidden_input(string control)
    {
        var components = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "wwwroot", "components.css"));
        Rule(components, $"{control} input[type=\"checkbox\"]").Should().Contain("position: absolute",
            because: "this test only matters while the hidden input is absolutely positioned");

        var app = File.ReadAllText(RepoRoot.Combine("ALDevToolbox", "wwwroot", "app.css"));
        Rule(app, $":where(.check, .switch)").Should().NotBeNull()
            .And.Contain("position: relative",
                because: $"without it the input of a {control} in a list that scrolls inside a dialog "
                       + "does not scroll with its row, and focusing it scrolls the whole dialog");
    }

    private static string? Rule(string css, string selector)
    {
        var stripped = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        foreach (Match m in Regex.Matches(stripped, @"(?<sel>[^{}@]+)\{(?<body>[^{}]*)\}"))
        {
            var selectors = Regex.Replace(m.Groups["sel"].Value, @"\s+", " ").Trim();
            if (selectors == selector || selectors.Split(',').Any(s => s.Trim() == selector))
            {
                return m.Groups["body"].Value;
            }
        }
        return null;
    }
}
