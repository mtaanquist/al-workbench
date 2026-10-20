namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// Pure helpers for a Business Central environment's recurring daily <em>update
/// window</em> (<c>ProjectEnvironment.UpdateWindowStart</c> / <c>UpdateWindowEnd</c>,
/// interpreted in the project's <c>BcTimeZone</c>), opening on the days in
/// <c>UpdateWindowDays</c> (#1410). A window is a default, not a lock:
/// scheduling prefills to the next opening, the user can override, and an override is
/// audited. The window may wrap past midnight (e.g. 22:00–06:00). Both bounds null =
/// "no window — any time". No DB / no clock — unit-testable. See
/// <c>.design/saas-delivery.md</c>.
/// </summary>
public static class UpdateWindow
{
    /// <summary>True when both bounds are set (a real window exists).</summary>
    public static bool IsConfigured(TimeOnly? start, TimeOnly? end) => start is not null && end is not null;

    /// <summary>
    /// A window bound as the 24-hour clock reading people agree it in ("22:00"). A time of
    /// day in the customer's zone, not an instant, so it never goes through the
    /// organisation's display zone the way a stored timestamp does.
    /// </summary>
    public static string Clock(TimeOnly time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Resolves an IANA time-zone id to a <see cref="TimeZoneInfo"/>, falling back to
    /// UTC when the id is null/blank/unknown — so a missing or stale tz never throws in
    /// a scheduling path; the window just runs in UTC.
    /// </summary>
    public static TimeZoneInfo ResolveTimeZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>
    /// True when <paramref name="utc"/>, viewed in <paramref name="tz"/>, falls within
    /// the window <c>[start, end)</c> on one of <paramref name="days"/>. Handles a window
    /// that wraps past midnight: the day it opens on is the one that counts, so a
    /// Wednesday 22:00-06:00 window is still open at 02:00 on Thursday. Equal bounds
    /// mean a full 24 hours from <c>start</c> on each chosen day. With no window configured this returns
    /// <c>true</c> (any time is "inside").
    /// </summary>
    public static bool IsWithin(TimeOnly? start, TimeOnly? end, TimeZoneInfo tz, DateTime utc,
        DeliveryDays days = DeliveryDays.Every)
    {
        if (!IsConfigured(start, end)) return true;
        var s = start!.Value;
        var e = end!.Value;
        days = Effective(days);

        var localDateTime = TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), tz);
        var local = TimeOnly.FromDateTime(localDateTime);
        var today = localDateTime.DayOfWeek;
        var yesterday = localDateTime.AddDays(-1).DayOfWeek;

        return s < e
            ? local >= s && local < e && days.Includes(today)                          // same-day window
            : (local >= s && days.Includes(today)) || (local < e && days.Includes(yesterday)); // wraps past midnight; equal bounds = 24 hours
    }

    /// <summary>
    /// The next UTC instant the window is open at or after <paramref name="fromUtc"/>:
    /// <paramref name="fromUtc"/> itself when already inside the window (or no window is
    /// configured), otherwise the next <c>start</c> that falls on one of
    /// <paramref name="days"/>.
    /// </summary>
    public static DateTime NextOpeningUtc(TimeOnly? start, TimeOnly? end, TimeZoneInfo tz, DateTime fromUtc,
        DeliveryDays days = DeliveryDays.Every)
    {
        var from = EnsureUtc(fromUtc);
        if (!IsConfigured(start, end) || IsWithin(start, end, tz, from, days)) return from;
        days = Effective(days);

        var opens = start!.Value;
        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(from, tz);
        var candidate = fromLocal.Date + opens.ToTimeSpan();
        for (var i = 0; i < 8 && (candidate <= fromLocal || !days.Includes(candidate.DayOfWeek)); i++)
        {
            candidate = candidate.AddDays(1);
        }

        // Guard against a DST-invalid local time (spring-forward gap): nudge forward an
        // hour until the local time is representable, so a prefill never throws.
        for (var i = 0; i < 4 && tz.IsInvalidTime(candidate); i++)
        {
            candidate = candidate.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// What to put after a window's hours to name its days: empty for a daily window, so
    /// every existing caption reads as before, else " on Wednesdays" / " on Mon, Wed, Fri".
    /// </summary>
    public static string DaysSuffix(DeliveryDays days)
    {
        days = Effective(days);
        return days == DeliveryDays.Every ? string.Empty : $" on {days.Describe()}";
    }

    /// <summary>
    /// The days a stored value means. No day at all can't be saved alongside a window, so
    /// one that somehow is reads as every day: a window that never opens would park
    /// every deployment that follows it.
    /// </summary>
    private static DeliveryDays Effective(DeliveryDays days)
    {
        var normalized = days.Normalize();
        return normalized == DeliveryDays.None ? DeliveryDays.Every : normalized;
    }

    private static DateTime EnsureUtc(DateTime utc) =>
        utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}
