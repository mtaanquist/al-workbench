using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>What changed about an environment's next Business Central update, as its followers hear it.</summary>
public enum BcUpdateScheduleChangeKind
{
    /// <summary>A new update was scheduled: a version not seen before, or a date where there was none.</summary>
    Scheduled,

    /// <summary>The same update now runs on another date.</summary>
    Moved,

    /// <summary>The latest date the update can be postponed to is close.</summary>
    LatestDateClose,
}

/// <summary>
/// One change to an environment's next update, found when a refresh read it from
/// Business Central (issue #1049). Dates are UTC.
/// </summary>
public sealed record BcUpdateScheduleChange(
    int ProjectId,
    int EnvironmentId,
    BcUpdateScheduleChangeKind Kind,
    string Version,
    DateTime Date,
    DateTime? LatestDate,
    DateTime? PreviousDate);

/// <summary>An environment's next update as it was before a refresh wrote over it.</summary>
public sealed record BcUpdateScheduleSnapshot(string? Version, DateTime? Date, DateTime? LatestDate, DateTime? FetchedAt)
{
    public static BcUpdateScheduleSnapshot Of(OeProjectEnvironment row) =>
        new(row.BcNextUpdateVersion, row.BcNextUpdateDate, row.BcNextUpdateLatestDate, row.BcNextUpdateFetchedAt);

    /// <summary>How close the latest date has to come before followers hear about it.</summary>
    public static readonly TimeSpan LatestDateWarning = TimeSpan.FromDays(7);

    /// <summary>
    /// What, if anything, followers should hear about the move from this snapshot to
    /// <paramref name="after"/>. Nothing on the first ever read, which has nothing to
    /// compare with, and nothing when no date is set. At most one change: a new update
    /// outranks a moved date, which outranks the latest date coming close.
    ///
    /// <para>The latest date is reported once, on the read where it first comes within
    /// <see cref="LatestDateWarning"/>: the snapshot says whether the read before had
    /// already seen it that close, so nothing needs storing to avoid saying it twice.</para>
    /// </summary>
    public BcUpdateScheduleChange? CompareWith(OeProjectEnvironment after, DateTime now)
    {
        if (FetchedAt is null) return null;
        if (after.BcNextUpdateVersion is not { Length: > 0 } version || after.BcNextUpdateDate is not { } date) return null;
        // An update under way, or one whose date has come, is not news about a date:
        // Business Central may restamp or clear it while it runs.
        if (ProjectConnectionService.IsUpdateUnderWay(after.BcNextUpdateStatus) || date <= now) return null;

        var sameVersion = Version is not null && ProjectConnectionService.CompareVersions(Version, version) == 0;
        if (!sameVersion || Date is null)
        {
            return Change(BcUpdateScheduleChangeKind.Scheduled, null);
        }
        // A move is a different day where the customer is: the notice names days, and a
        // time shifted within the same day (the update window edited) is not worth one.
        if (Day(date, after.BcUpdateWindowTimeZoneIana) != Day(Date.Value, after.BcUpdateWindowTimeZoneIana))
        {
            return Change(BcUpdateScheduleChangeKind.Moved, Date);
        }
        if (after.BcNextUpdateLatestDate is { } latest && latest > now && latest - now <= LatestDateWarning)
        {
            var alreadyClose = LatestDate is { } before && (latest - before).Duration() < TimeSpan.FromMinutes(1)
                               && latest - FetchedAt.Value <= LatestDateWarning;
            if (!alreadyClose) return Change(BcUpdateScheduleChangeKind.LatestDateClose, null);
        }
        return null;

        BcUpdateScheduleChange Change(BcUpdateScheduleChangeKind kind, DateTime? previous) =>
            new(after.ProjectId, after.Id, kind, version, date, after.BcNextUpdateLatestDate, previous);
    }

    /// <summary>The calendar day of <paramref name="utc"/> in <paramref name="ianaZone"/>, or in UTC when it is unknown.</summary>
    internal static DateOnly Day(DateTime utc, string? ianaZone)
    {
        var when = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (ianaZone is { Length: > 0 })
        {
            try
            {
                return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(when, TimeZoneInfo.FindSystemTimeZoneById(ianaZone)));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Falls through to UTC.
            }
        }
        return DateOnly.FromDateTime(when);
    }
}
