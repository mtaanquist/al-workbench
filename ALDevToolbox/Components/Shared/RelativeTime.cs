namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Relative timestamps for build and delivery histories ("4 minutes ago",
/// "2 weeks ago"). Pulled out when the Pipelines browser moved onto the design
/// system's list archetype and would have been the third byte-identical copy.
///
/// Note there is a *second*, shorter phrasing still living privately on
/// <c>PipelineEdit</c> and <c>ReleasePipelineDetail</c> ("4 min ago",
/// then "on 2026-07-01" past a week). That one is deliberately not merged in
/// here: unifying them would change visible copy on two pages as a side effect
/// of a refactor. Fold them in when those pages migrate and the wording is a
/// decision rather than an accident.
/// </summary>
public static class RelativeTime
{
    /// <summary>
    /// A compact relative time for a UTC instant. Clamps future timestamps to
    /// "just now" — clock skew between the app and Postgres shouldn't render as
    /// a build that finished in the future.
    /// </summary>
    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span switch
        {
            { TotalSeconds: < 60 } => "just now",
            { TotalMinutes: < 60 } => Plural((int)span.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural((int)span.TotalHours, "hour"),
            { TotalDays: < 7 } => Plural((int)span.TotalDays, "day"),
            { TotalDays: < 30 } => Plural((int)(span.TotalDays / 7), "week"),
            { TotalDays: < 365 } => Plural((int)(span.TotalDays / 30), "month"),
            _ => Plural((int)(span.TotalDays / 365), "year"),
        };

        static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
    }

    /// <summary>
    /// The same phrasing looking forward ("in 3 hours"), for something scheduled.
    /// A time already past reads "any moment now": a scheduled deployment that is due
    /// is about to be picked up, not late.
    /// </summary>
    public static string Until(DateTime utc)
    {
        var span = utc - DateTime.UtcNow;
        return span switch
        {
            { TotalSeconds: < 60 } => "any moment now",
            { TotalMinutes: < 60 } => Plural((int)span.TotalMinutes, "minute"),
            { TotalHours: < 24 } => Plural((int)span.TotalHours, "hour"),
            { TotalDays: < 7 } => Plural((int)span.TotalDays, "day"),
            { TotalDays: < 30 } => Plural((int)(span.TotalDays / 7), "week"),
            { TotalDays: < 365 } => Plural((int)(span.TotalDays / 30), "month"),
            _ => Plural((int)(span.TotalDays / 365), "year"),
        };

        static string Plural(int n, string unit) => $"in {n} {unit}{(n == 1 ? "" : "s")}";
    }

    /// <summary>
    /// The exact instant behind a relative time, for its hover title. UTC and
    /// labelled so: pages render on the server, which does not know the reader's zone.
    /// </summary>
    public static string ExactUtc(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A length of time in words: "under a minute", "6 minutes", "1 hour 5 minutes".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.FromMinutes(1)) return "under a minute";
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (minutes < 60) return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        var hours = minutes / 60;
        var rest = minutes % 60;
        var h = $"{hours} hour{(hours == 1 ? "" : "s")}";
        return rest == 0 ? h : $"{h} {rest} minute{(rest == 1 ? "" : "s")}";
    }
}
