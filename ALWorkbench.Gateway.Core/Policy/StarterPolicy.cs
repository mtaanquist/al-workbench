using System.Text;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// The first <c>gateway.toml</c> the setup program writes (#1778): every service tier
/// found on this computer, allowed only to be reported on (inventory). The gateway is
/// useful straight away (its tiers show up in AL Workbench and can be linked to a
/// Solution) while nothing can be published, restarted or changed until customer IT
/// adds it. It is written once, when there is no <c>gateway.toml</c>; an existing one
/// is never touched. See <c>.design/on-prem-gateway.md</c>, "Setup program".
/// </summary>
public static class StarterPolicy
{
    /// <summary>
    /// The file's text: plain ASCII with Windows line endings, since it is opened in
    /// Notepad. Instance names come from the computer's own services, quoted as TOML
    /// strings.
    /// </summary>
    public static string Create(string machine, IEnumerable<string> instances)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machine);
        var tiers = instances.Where(i => !string.IsNullOrWhiteSpace(i))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var text = new StringBuilder();
        void Line(string value = "") => text.Append(value).Append("\r\n");

        Line("# AL Workbench Gateway settings, written by setup.");
        Line("#");
        Line("# The gateway may only report on the service tiers listed here (\"inventory\"):");
        Line("# their state, versions, tenants, apps and licence. It cannot publish,");
        Line("# restart or change anything until you add that to \"allow\" yourself.");
        Line("# gateway.example.toml beside this file explains every setting.");
        Line("#");
        Line("# After editing this file, open a command prompt with \"Run as administrator\",");
        Line("# go to this folder and run:  ALWorkbench.Gateway check");
        Line("# The gateway reads the file again by itself; no restart is needed.");
        if (tiers.Count == 0)
        {
            Line("#");
            Line("# Setup found no Business Central service tiers on this computer. Add an");
            Line("# [[instance]] section for each tier, as gateway.example.toml shows.");
        }
        foreach (var tier in tiers)
        {
            Line();
            Line("[[instance]]");
            Line($"machine = {Quote(machine)}");
            Line($"name = {Quote(tier)}");
            Line("environment = \"production\"   # or \"test\" or \"development\"");
            Line("allow = [\"inventory\"]");
        }
        return text.ToString();
    }

    /// <summary>A TOML basic string. Anything outside printable ASCII is escaped, so the file stays ASCII.</summary>
    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                _ when c < 0x20 || c > 0x7E => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return quoted.Append('"').ToString();
    }
}
