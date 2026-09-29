using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Pages.Upgrades;

/// <summary>
/// The words and the order of the Upgrades page's Open and Archive views
/// (<c>.design/handoff/UpgradesListBody.dc.html</c>), kept out of the markup so the rules
/// the sheet's spec sets - which pill tone a status wears, what the breakdown counts, how
/// a slot is said, which upgrade comes first - are tested on their own.
/// </summary>
internal static class UpgradesListWording
{
    /// <summary>The status as the sheet says it. Done only ever appears in the Archive.</summary>
    public static string StatusWord(UpgradeStatus status) => status switch
    {
        UpgradeStatus.Planned => "Planned",
        UpgradeStatus.InProgress => "In progress",
        UpgradeStatus.Updated => "Updated",
        _ => "Done",
    };

    /// <summary>
    /// The sheet's tones: Planned is queued, In progress is running, and Updated is warn,
    /// because an updated upgrade still has checks outstanding before anyone calls it done.
    /// </summary>
    public static string StatusTone(UpgradeStatus status) => status switch
    {
        UpgradeStatus.Planned => "queued",
        UpgradeStatus.InProgress => "running",
        UpgradeStatus.Updated => "warn",
        _ => "success",
    };

    /// <summary>One line state as the Mark done confirm names it.</summary>
    public static string LineStateWord(UpgradeLineState state) => state switch
    {
        UpgradeLineState.Planned => "Planned",
        UpgradeLineState.DateMoved => "Date moved",
        UpgradeLineState.Booked => "Booked",
        UpgradeLineState.Running => "Running",
        UpgradeLineState.Updated => "Updated",
        UpgradeLineState.Failed => "Failed",
        _ => "Checked",
    };

    /// <summary>The line state's pill tone, the sheet's for the three it draws and the nearest for the rest.</summary>
    public static string LineStateTone(UpgradeLineState state) => state switch
    {
        UpgradeLineState.Failed => "failed",
        UpgradeLineState.Updated => "warn",
        UpgradeLineState.Running => "running",
        UpgradeLineState.Booked => "queued",
        UpgradeLineState.Checked => "success",
        _ => "muted",
    };

    /// <summary>One count in the Environments column's breakdown, with its tone when it needs action.</summary>
    public sealed record Part(string Text, string? Tone);

    /// <summary>
    /// "6 updated, 1 running, 1 failed": each state with lines, in the order a wave goes
    /// through them read backwards - what is done, what is under way, what went wrong, then
    /// what is still to come. Running and failed carry a tone, because those are the two
    /// that need somebody. Checked lines count as updated here: the Checked column shows
    /// them, so no line is counted twice.
    /// </summary>
    public static List<Part> Breakdown(EnvironmentUpgradeSummary u)
    {
        var parts = new List<Part>();
        void Add(int n, string word, string? tone = null)
        {
            if (n > 0) parts.Add(new Part($"{n} {word}", tone));
        }

        Add(u.Count(UpgradeLineState.Updated) + u.Count(UpgradeLineState.Checked), "updated");
        Add(u.Count(UpgradeLineState.Running), "running", "running");
        Add(u.Count(UpgradeLineState.Failed), "failed", "failed");
        Add(u.Count(UpgradeLineState.Booked), "booked");
        Add(u.Count(UpgradeLineState.DateMoved), "date moved");
        Add(u.Count(UpgradeLineState.Planned), "planned");
        return parts;
    }

    public enum SlotDay { Today, Yesterday, Tomorrow, Other }

    /// <summary>Which day a slot falls on, both times already in the display zone.</summary>
    public static SlotDay DayOf(DateTime slotLocal, DateTime nowLocal) =>
        (slotLocal.Date - nowLocal.Date).Days switch
        {
            0 => SlotDay.Today,
            -1 => SlotDay.Yesterday,
            1 => SlotDay.Tomorrow,
            _ => SlotDay.Other,
        };

    /// <summary>
    /// The word the team uses for a near slot: an evening one is "Tonight" or "Last
    /// night", because that is when the waves run; a daytime one is plainly "Today" or
    /// "Yesterday". Null for a slot further off, which is said as its date.
    /// </summary>
    public static string? DayWord(SlotDay day, DateTime slotLocal)
    {
        var evening = slotLocal.Hour >= 17;
        return day switch
        {
            SlotDay.Today => evening ? "Tonight" : "Today",
            SlotDay.Yesterday => evening ? "Last night" : "Yesterday",
            SlotDay.Tomorrow => "Tomorrow",
            _ => null,
        };
    }

    /// <summary>"in 3 days", "4 days ago", "in 7 weeks": whole days between the two local dates.</summary>
    public static string Distance(DateTime slotLocal, DateTime nowLocal)
    {
        var days = (slotLocal.Date - nowLocal.Date).Days;
        var n = Math.Abs(days);
        string span = n switch
        {
            0 => "today",
            1 => "1 day",
            < 14 => $"{n} days",
            < 63 => $"{(int)Math.Round(n / 7.0)} weeks",
            _ => $"{(int)Math.Round(n / 30.4)} months",
        };
        if (n == 0) return span;
        return days > 0 ? $"in {span}" : $"{span} ago";
    }

    /// <summary>
    /// The Open view's order, "nearest first" as the team means it the morning after:
    /// today's slot, then the ones that have run, most recent first - they are the ones
    /// still being checked - then the ones to come, soonest first, then any without a
    /// slot, newest first.
    /// </summary>
    public static List<EnvironmentUpgradeSummary> OpenOrder(
        IEnumerable<EnvironmentUpgradeSummary> upgrades, Func<DateTime, DateTime> toLocal, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        return upgrades
            .OrderBy(u => u.PlannedAt is null ? 2 : toLocal(u.PlannedAt.Value).Date <= today ? 0 : 1)
            .ThenByDescending(u => u.PlannedAt is { } at && toLocal(at).Date <= today ? at : DateTime.MinValue)
            .ThenBy(u => u.PlannedAt is { } at && toLocal(at).Date > today ? at : DateTime.MaxValue)
            .ThenByDescending(u => u.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// The head's subtitle: how many are open, and how many environments are booked for
    /// a slot still to come today - what somebody opening the page in the afternoon wants
    /// to know about the evening. "Nothing planned." with nothing open.
    /// </summary>
    public static string Subtitle(IReadOnlyList<EnvironmentUpgradeSummary> open, Func<DateTime, DateTime> toLocal, DateTime nowUtc)
    {
        if (open.Count == 0) return "Nothing planned.";
        var nowLocal = toLocal(nowUtc);
        var tonight = open
            .Where(u => u.PlannedAt is { } at && at >= nowUtc && toLocal(at).Date == nowLocal.Date)
            .Sum(u => u.Count(UpgradeLineState.Booked));
        var booked = tonight switch
        {
            0 => "nothing booked for tonight",
            1 => "1 environment booked for tonight",
            _ => $"{tonight} environments booked for tonight",
        };
        return $"{open.Count} open, {booked}.";
    }
}
