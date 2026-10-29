namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The choices an organisation has for how often <see cref="EnvironmentRefreshScheduler"/>
/// asks Business Central about its solutions' environments, stored in minutes on
/// <c>organization_settings.environment_refresh_interval_minutes</c>.
/// <para>
/// A closed list rather than any number, for two reasons. Every choice divides a day, so
/// whatever is picked, one run still lands in the quiet hour the morning jobs that read
/// the result are timed after. And fifteen minutes is the floor: the scheduler only
/// looks every five, and a sweep is several requests per environment against every
/// customer's tenant, so going lower would mostly produce throttling.
/// </para>
/// </summary>
public static class EnvironmentRefreshInterval
{
    /// <summary>Once a day, at night: what every organisation had before this was a setting.</summary>
    public const int DefaultMinutes = 1440;

    /// <summary>The allowed values in minutes, shortest first, each with the words the setting shows.</summary>
    public static readonly IReadOnlyList<(int Minutes, string Label)> Choices =
    [
        (15, "Every 15 minutes"),
        (30, "Every 30 minutes"),
        (60, "Every hour"),
        (120, "Every 2 hours"),
        (180, "Every 3 hours"),
        (240, "Every 4 hours"),
        (360, "Every 6 hours"),
        (480, "Every 8 hours"),
        (720, "Every 12 hours"),
        (DefaultMinutes, "Once a night (default)"),
    ];

    public static bool IsAllowed(int minutes) => Choices.Any(c => c.Minutes == minutes);

    /// <summary>
    /// The interval to run on for a stored value. Anything not on the list - a row from
    /// before the column, or one written by hand - is read as the default rather than
    /// trusted, so a stray zero can never turn into a sweep on every poll.
    /// </summary>
    public static TimeSpan Normalise(int? minutes) =>
        TimeSpan.FromMinutes(minutes is { } m && IsAllowed(m) ? m : DefaultMinutes);
}
