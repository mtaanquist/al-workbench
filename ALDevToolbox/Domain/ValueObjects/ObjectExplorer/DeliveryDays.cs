using System.Text.Json.Serialization;

namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// The days of the week an environment's delivery window opens on
/// (<c>OeProjectEnvironment.UpdateWindowDays</c>), as a bit set stored in one
/// <c>smallint</c>. A day names when the window <em>opens</em>: a Wednesday-only
/// 22:00-06:00 window runs into Thursday morning. <see cref="Every"/> is the daily
/// window every environment had before days could be picked. See
/// <c>.design/saas-delivery.md</c> ("Update window").
/// <para>Written by name in JSON ("Wednesday, Friday"), so an audit row reads as days, not a number.</para>
/// </summary>
[Flags]
[JsonConverter(typeof(JsonStringEnumConverter<DeliveryDays>))]
public enum DeliveryDays : short
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    Every = Weekdays | Weekend,
}

/// <summary>Helpers for <see cref="DeliveryDays"/>.</summary>
public static class DeliveryDaysExtensions
{
    /// <summary>The days in calendar order, Monday first, as the picker shows them.</summary>
    public static readonly IReadOnlyList<DayOfWeek> Week =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    /// <summary>The flag for one <see cref="DayOfWeek"/>.</summary>
    public static DeliveryDays ToDeliveryDay(this DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => DeliveryDays.Monday,
        DayOfWeek.Tuesday => DeliveryDays.Tuesday,
        DayOfWeek.Wednesday => DeliveryDays.Wednesday,
        DayOfWeek.Thursday => DeliveryDays.Thursday,
        DayOfWeek.Friday => DeliveryDays.Friday,
        DayOfWeek.Saturday => DeliveryDays.Saturday,
        _ => DeliveryDays.Sunday,
    };

    /// <summary>True when <paramref name="day"/> is one of <paramref name="days"/>.</summary>
    public static bool Includes(this DeliveryDays days, DayOfWeek day) => (days & day.ToDeliveryDay()) != 0;

    /// <summary>Only the seven day bits; anything else a stored value carries is dropped.</summary>
    public static DeliveryDays Normalize(this DeliveryDays days) => days & DeliveryDays.Every;

    /// <summary>
    /// How the days read on screen: "every day", "weekdays", "weekends", "Wednesdays",
    /// or a list ("Mon, Wed, Fri"). Empty for <see cref="DeliveryDays.None"/>.
    /// </summary>
    public static string Describe(this DeliveryDays days)
    {
        days = days.Normalize();
        switch (days)
        {
            case DeliveryDays.None: return string.Empty;
            case DeliveryDays.Every: return "every day";
            case DeliveryDays.Weekdays: return "weekdays";
            case DeliveryDays.Weekend: return "weekends";
        }

        var picked = Week.Where(d => days.Includes(d)).ToList();
        return picked.Count == 1
            ? picked[0] + "s"
            : string.Join(", ", picked.Select(d => d.ToString()[..3]));
    }
}
