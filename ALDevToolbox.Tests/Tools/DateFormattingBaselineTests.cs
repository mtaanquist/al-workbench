using System.Text.RegularExpressions;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Tools;

/// <summary>
/// Keeps every time on screen going through <c>&lt;Timestamp&gt;</c> (issue #942).
///
/// <para>Times are stored in UTC. The organisation picks a display zone, and
/// <c>Components/Shared/Timestamp.razor</c> is the one place that converts into it
/// and puts the UTC instant in the hover title. A page that formats a
/// <see cref="DateTime"/> itself shows UTC with no label, which is the bug the
/// issue is about.</para>
///
/// <para>When this test landed the tree still did that in dozens of places. The
/// sweep that replaces them goes area by area, so this is a per-file baseline in
/// the style of <see cref="IgnoreQueryFiltersBaselineTests"/>: no file may gain a
/// hit, no new file may have one, and the numbers only go down. The target is an
/// empty baseline.</para>
///
/// <para>Matching "a DateTime being formatted" exactly would need the compiler, so
/// the scan matches the shapes the issue counted: a <c>ToString("...")</c> whose
/// format starts like a date or time pattern, the same patterns after a colon
/// inside an interpolation hole, <c>ToLocalTime()</c> (which converts to nothing:
/// the container runs in UTC), and <c>RelativeTime.Ago(</c> used directly
/// (<c>&lt;Timestamp Relative="true"&gt;</c> adds the UTC title it lacks).</para>
/// </summary>
public sealed class DateFormattingBaselineTests
{
    private const string ComponentPath = "ALDevToolbox/Components/Shared/Timestamp.razor";

    private static readonly (string Name, Regex Pattern)[] Patterns =
    {
        ("date format in ToString", new Regex(@"\.ToString\(""(?:yyyy|yy|HH|hh|dd|d |d""|MMM|MM|O"")", RegexOptions.Compiled)),
        ("date format in an interpolation", new Regex(@"\{[^{}""]*?:(?:yyyy|yy|HH|hh|dd|d |MMM)[^{}]*\}", RegexOptions.Compiled)),
        ("ToLocalTime()", new Regex(@"\.ToLocalTime\(\)", RegexOptions.Compiled)),
        ("RelativeTime.Ago(", new Regex(@"RelativeTime\.Ago\(", RegexOptions.Compiled)),
    };

    /// <summary>
    /// Hits per file, relative to the repository root with forward slashes.
    /// Generated from the tree when the fence landed. Lower an entry (and the
    /// total below) when a sweep replaces a site; never raise one.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> Baseline = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        // Empty since both sweeps of issue #942 landed. A new hit fails the test.
    };

    /// <summary>
    /// Hits that are not an instant shown on screen, so they never go through
    /// <c>&lt;Timestamp&gt;</c> and are not converted into the organisation's zone: a
    /// time of day agreed in the customer's Business Central zone (a delivery window), a
    /// wall clock booked in the customer's zone and said back the way it was agreed, a
    /// calendar date somebody typed, or relative wording inside a hover title (the same
    /// in every zone, and a title cannot hold a Timestamp). The scan subtracts these
    /// before comparing a file with <see cref="Baseline"/>. Unlike the baseline this is
    /// not meant to reach zero; each entry says in one sentence why its hits are not a
    /// time to convert. Counts are exact: a file that drops one fails until its entry is
    /// lowered too.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Count, string Reason)> Permitted = new Dictionary<string, (int, string)>(StringComparer.Ordinal)
    {
        ["ALDevToolbox/Components/Pages/AccountSecurity/RecoveryCodes.razor"] = (1, "The date is part of the downloaded file's name, not a time shown on screen."),
        ["ALDevToolbox/Components/Pages/Admin/Administration/AdminAdministrationBusinessCentral.razor"] = (2, "The client secret's expiry is a calendar date the admin typed from Entra, not an instant."),
        ["ALDevToolbox/Components/Pages/Environments/EnvironmentDetail.razor"] = (5,
            "The delivery window and Business Central's update window are times of day in the customer's zone, and the scheduled update is repeated as a labelled wall clock in that zone beside them so the three can be compared."),
        ["ALDevToolbox/Components/Pages/Error.razor"] = (1, "The error time is copied for someone matching it against server logs, which are in UTC, and it is labelled UTC; the page also has to render when the database is down."),
        ["ALDevToolbox/Components/Pages/Pipelines/ReleasePipelineDetail.razor"] = (3,
            "The delivery window is a time of day agreed in the customer's zone, and the client secret's expiry is a calendar date the consultant typed."),
        ["ALDevToolbox/Components/Pages/Pipelines/ReleasePipelinesBrowser.razor"] = (1,
            "\"Prepared 3 hours ago\" sits inside a hover title, where relative wording reads the same in every zone and a Timestamp cannot go."),
        ["ALDevToolbox/Components/Pages/Projects/ProjectDetailBc.razor"] = (5,
            "The delivery and update windows are times of day in the customer's zone, and the client secret's expiry is a calendar date the consultant typed."),
        ["ALDevToolbox/Components/Pages/Projects/ProjectsBrowser.razor"] = (1,
            "\"(2 days ago)\" sits inside the last-shipped hover title, where relative wording reads the same in every zone and a Timestamp cannot go."),
        ["ALDevToolbox/Components/Pages/Upgrades/UpgradeActionRunner.razor"] = (8,
            "A booked update slot is a wall clock the person types and Business Central runs in each customer's own zone, so it is said back in that zone and labelled with it."),
        ["ALDevToolbox/Components/Pages/Upgrades/UpgradeDetail.razor"] = (2,
            "A line's booked update slot is a wall clock in the customer's own zone, said back in that zone and labelled with it, as the fleet table's booking marker says it."),
        ["ALDevToolbox/Components/Pages/Upgrades/UpgradesPage.razor"] = (4,
            "A booked update slot is a wall clock in the customer's own zone, said back on its row in that zone and labelled with it."),
        ["ALDevToolbox/Components/Shared/EnvironmentActivityFeed.razor"] = (2,
            "A booked update slot is a wall clock in the customer's own zone, said back in that zone and labelled with it."),
        ["ALDevToolbox/Components/Shared/ReleaseBuildDialog.razor"] = (5,
            "The delivery window and the picked deployment time are wall clocks in the customer's zone, as the field's hint says, and the client secret's expiry is a calendar date the consultant typed."),
        ["ALDevToolbox/Components/Shared/ReleasePipelineEditorDialog.razor"] = (2,
            "The delivery window is a time of day agreed in the customer's zone."),
    };

    /// <summary>
    /// The sum of <see cref="Baseline"/>, written out so that a sweep has to lower
    /// both: the per-file numbers and the headline number the issue tracks.
    /// </summary>
    private const int BaselineTotal = 0;

    [Fact]
    public void No_razor_file_formats_a_time_more_often_than_its_baseline()
    {
        var actual = Scan();

        var problems = new List<string>();
        foreach (var (path, hits) in actual.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            Baseline.TryGetValue(path, out var allowed);
            allowed += Permitted.TryGetValue(path, out var permitted) ? permitted.Count : 0;
            if (hits.Count <= allowed) continue;
            var where = string.Join("; ", hits.Select(h => $"line {h.Line}: {h.What}"));
            problems.Add(allowed == 0
                ? $"{path} formats a time itself ({hits.Count}x: {where}). Render it with <Timestamp Value=\"...\" /> instead, so it shows in the organisation's zone with the UTC instant on hover."
                : $"{path}: baseline {allowed}, found {hits.Count} ({where}). Render the new one with <Timestamp Value=\"...\" /> instead of formatting it in the page.");
        }
        foreach (var (path, (permittedCount, _)) in Permitted.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var found = actual.TryGetValue(path, out var hits) ? hits.Count : 0;
            if (found < permittedCount)
            {
                problems.Add($"{path}: permitted {permittedCount}, found {found}. Lower this file's {nameof(Permitted)} entry so it stays exact.");
            }
        }
        foreach (var (path, allowed) in Baseline.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var found = (actual.TryGetValue(path, out var hits) ? hits.Count : 0)
                - (Permitted.TryGetValue(path, out var permitted) ? permitted.Count : 0);
            if (found < allowed)
            {
                problems.Add($"{path}: baseline {allowed}, found {found}. Good news - lower this file's baseline (and {nameof(BaselineTotal)}) so it stays honest.");
            }
        }

        problems.Should().BeEmpty(
            "every time on screen goes through <Timestamp> (issue #942); the baseline only goes down. Current counts:\n"
            + string.Join("\n", actual.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"        [\"{kv.Key}\"] = {kv.Value.Count},")));
    }

    [Fact]
    public void Baseline_total_is_the_sum_of_the_per_file_baseline()
    {
        Baseline.Values.Sum().Should().Be(BaselineTotal,
            "a sweep lowers both the per-file entries and the total, so neither can drift from the other");
    }

    [Fact]
    public void Every_permitted_entry_says_why()
    {
        foreach (var (path, (count, reason)) in Permitted)
        {
            count.Should().BePositive($"{path} is only worth an entry if it has hits to permit");
            reason.Should().NotBeNullOrWhiteSpace($"{path} must say why its hits are not a time to convert");
        }
    }

    [Fact]
    public void The_scan_finds_each_shape_it_is_meant_to_catch()
    {
        // The fence is only as good as its patterns; pin one example of each so a
        // regex edit that stops matching fails here rather than going quiet.
        string[] samples =
        {
            "@row.CreatedAt.ToString(\"yyyy-MM-dd HH:mm\")",
            "@row.CreatedAt.ToString(\"d MMM yyyy\")",
            "<time datetime=\"@row.CreatedAt.ToString(\"O\")\">",
            "@($\"Last run {row.At:yyyy-MM-dd}\")",
            "@row.CreatedAt.ToLocalTime()",
            "@RelativeTime.Ago(row.CreatedAt)",
        };
        foreach (var sample in samples)
        {
            ScanLines(new[] { sample }).Should().NotBeEmpty($"'{sample}' formats a time");
        }

        string[] innocent =
        {
            "@count.ToString(\"N0\")",
            "@tenantId.ToString(\"D\")",
            "@* Updated.ToString(\"yyyy\") in a comment *@",
            "<Timestamp Value=\"@row.CreatedAt\" />",
        };
        foreach (var sample in innocent)
        {
            ScanLines(new[] { sample }).Should().BeEmpty($"'{sample}' does not format a time");
        }
    }

    private sealed record Hit(int Line, string What);

    private static Dictionary<string, List<Hit>> Scan()
    {
        var root = ALDevToolbox.Tests.Infrastructure.RepoRoot.Directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
        var components = Path.Combine(root, "ALDevToolbox", "Components");
        var result = new Dictionary<string, List<Hit>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(components, "*.razor", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative == ComponentPath) continue;
            var hits = ScanLines(File.ReadAllLines(file));
            if (hits.Count > 0) result[relative] = hits;
        }
        return result;
    }

    /// <summary>
    /// Strips <c>//</c>, <c>/* */</c> and Razor's <c>@* *@</c> comments, then
    /// reports every match of every pattern on what is left.
    /// </summary>
    private static List<Hit> ScanLines(IReadOnlyList<string> lines)
    {
        var hits = new List<Hit>();
        var inBlock = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var code = StripComments(lines[i], ref inBlock);
            foreach (var (name, pattern) in Patterns)
            {
                foreach (Match _ in pattern.Matches(code))
                {
                    hits.Add(new Hit(i + 1, name));
                }
            }
        }
        return hits;
    }

    private static string StripComments(string line, ref bool inBlock)
    {
        var buffer = new System.Text.StringBuilder(line.Length);
        var j = 0;
        while (j < line.Length)
        {
            if (inBlock)
            {
                if (Starts(line, j, "*/") || Starts(line, j, "*@")) { inBlock = false; j += 2; }
                else j++;
            }
            else if (Starts(line, j, "@*") || Starts(line, j, "/*"))
            {
                inBlock = true;
                j += 2;
            }
            else if (Starts(line, j, "//") && (j == 0 || line[j - 1] != ':'))
            {
                // Not "https://": a URL in markup is not a comment.
                break;
            }
            else
            {
                buffer.Append(line[j]);
                j++;
            }
        }
        return buffer.ToString();
    }

    private static bool Starts(string line, int index, string token) =>
        index + token.Length <= line.Length && string.CompareOrdinal(line, index, token, 0, token.Length) == 0;
}
