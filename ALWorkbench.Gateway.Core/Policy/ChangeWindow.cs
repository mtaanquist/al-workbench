using System.Globalization;
using System.Text.RegularExpressions;

namespace ALWorkbench.Gateway.Core.Policy;

/// <summary>
/// The local time window changes may run in (<c>change_hours = "20:00-06:00"</c>).
/// The start is inside the window and the end is not, so <c>20:00-06:00</c> allows
/// 20:00 up to 05:59:59. A start later than the end runs overnight.
///
/// <para>Judged on the wall clock of the gateway's computer, because that is the
/// clock customer IT has in mind when they write the window. The clock reading is
/// converted from UTC each time, so a daylight-saving change moves the window with
/// the wall clock: on the night the clocks go back, an hour that happens twice is
/// inside the window both times, and on the night they go forward the hour that
/// does not exist simply never comes.</para>
/// </summary>
public sealed partial record ChangeWindow(TimeOnly Start, TimeOnly End)
{
    [GeneratedRegex(@"^(?<sh>\d{2}):(?<sm>\d{2})-(?<eh>\d{2}):(?<em>\d{2})$")]
    private static partial Regex Pattern();

    /// <summary>True when <paramref name="localTime"/> is inside the window.</summary>
    public bool Contains(TimeOnly localTime) => Start < End
        ? localTime >= Start && localTime < End
        : localTime >= Start || localTime < End;

    /// <summary>True when the moment <paramref name="utcNow"/>, read on a wall clock in <paramref name="zone"/>, is inside the window.</summary>
    public bool Contains(DateTimeOffset utcNow, TimeZoneInfo zone) =>
        Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utcNow, zone).DateTime));

    /// <summary>Reads <c>HH:MM-HH:MM</c> in 24-hour time, or says what is wrong in a sentence.</summary>
    public static bool TryParse(string text, out ChangeWindow? window, out string? problem)
    {
        window = null;
        problem = null;
        var match = Pattern().Match(text);
        if (!match.Success)
        {
            problem = $"\"{text}\" is not a time window. Write it as start-end in 24-hour time, for example \"20:00-06:00\".";
            return false;
        }
        if (!TryTime(match.Groups["sh"].Value, match.Groups["sm"].Value, out var start)
            || !TryTime(match.Groups["eh"].Value, match.Groups["em"].Value, out var end))
        {
            problem = $"\"{text}\" has a time that does not exist. Hours go from 00 to 23 and minutes from 00 to 59.";
            return false;
        }
        if (start == end)
        {
            problem = $"\"{text}\" starts and ends at the same time. Leave change_hours out to allow changes at any time.";
            return false;
        }
        window = new ChangeWindow(start, end);
        return true;
    }

    private static bool TryTime(string hours, string minutes, out TimeOnly time)
    {
        var h = int.Parse(hours, CultureInfo.InvariantCulture);
        var m = int.Parse(minutes, CultureInfo.InvariantCulture);
        time = default;
        if (h > 23 || m > 59) return false;
        time = new TimeOnly(h, m);
        return true;
    }

    public override string ToString() =>
        Start.ToString("HH:mm", CultureInfo.InvariantCulture) + "-" + End.ToString("HH:mm", CultureInfo.InvariantCulture);
}
