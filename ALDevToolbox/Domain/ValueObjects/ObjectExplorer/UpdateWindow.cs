namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// Pure helpers for a Business Central environment's recurring daily <em>update
/// window</em> (<c>ProjectEnvironment.UpdateWindowStart</c> / <c>UpdateWindowEnd</c>,
/// interpreted in the project's <c>BcTimeZone</c>). A window is a default, not a lock:
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
    /// the daily window <c>[start, end)</c>. Handles a window that wraps past midnight.
    /// With no window configured this returns <c>true</c> (any time is "inside").
    /// </summary>
    public static bool IsWithin(TimeOnly? start, TimeOnly? end, TimeZoneInfo tz, DateTime utc)
    {
        if (!IsConfigured(start, end)) return true;
        var s = start!.Value;
        var e = end!.Value;
        if (s == e) return true; // degenerate: treat equal bounds as always-open

        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(EnsureUtc(utc), tz));
        return s < e
            ? local >= s && local < e          // same-day window
            : local >= s || local < e;          // wraps past midnight
    }

    /// <summary>
    /// The next UTC instant the window is open at or after <paramref name="fromUtc"/>:
    /// <paramref name="fromUtc"/> itself when already inside the window (or no window is
    /// configured), otherwise the next occurrence of <c>start</c>.
    /// </summary>
    public static DateTime NextOpeningUtc(TimeOnly? start, TimeOnly? end, TimeZoneInfo tz, DateTime fromUtc)
    {
        var from = EnsureUtc(fromUtc);
        if (!IsConfigured(start, end) || IsWithin(start, end, tz, from)) return from;

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(from, tz);
        var candidate = fromLocal.Date + start!.Value.ToTimeSpan();
        if (candidate <= fromLocal) candidate = candidate.AddDays(1);

        // Guard against a DST-invalid local time (spring-forward gap): nudge forward an
        // hour until the local time is representable, so a prefill never throws.
        for (var i = 0; i < 4 && tz.IsInvalidTime(candidate); i++)
        {
            candidate = candidate.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
    }

    /// <summary>
    /// When the window that is open at <paramref name="fromUtc"/> closes: the next
    /// occurrence of <c>end</c> after it. Null when no window is configured, the bounds are
    /// equal (always open), or the window is not open at <paramref name="fromUtc"/>.
    /// </summary>
    public static DateTime? ClosingUtc(TimeOnly? start, TimeOnly? end, TimeZoneInfo tz, DateTime fromUtc)
    {
        var from = EnsureUtc(fromUtc);
        if (!IsConfigured(start, end) || start == end || !IsWithin(start, end, tz, from)) return null;

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(from, tz);
        var candidate = fromLocal.Date + end!.Value.ToTimeSpan();
        if (candidate <= fromLocal) candidate = candidate.AddDays(1);
        for (var i = 0; i < 4 && tz.IsInvalidTime(candidate); i++)
        {
            candidate = candidate.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(candidate, DateTimeKind.Unspecified), tz);
    }

    private static DateTime EnsureUtc(DateTime utc) =>
        utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
}
