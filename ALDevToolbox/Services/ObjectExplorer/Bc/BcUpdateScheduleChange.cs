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

        var sameVersion = Version is not null && ProjectConnectionService.CompareVersions(Version, version) == 0;
        if (!sameVersion || Date is null)
        {
            return Change(BcUpdateScheduleChangeKind.Scheduled, null);
        }
        if ((date - Date.Value).Duration() >= TimeSpan.FromMinutes(1))
        {
            return Change(BcUpdateScheduleChangeKind.Moved, Date);
        }
        if (after.BcNextUpdateLatestDate is { } latest && latest > now && latest - now <= LatestDateWarning)
        {
            var alreadyClose = LatestDate == latest && latest - FetchedAt.Value <= LatestDateWarning;
            if (!alreadyClose) return Change(BcUpdateScheduleChangeKind.LatestDateClose, null);
        }
        return null;

        BcUpdateScheduleChange Change(BcUpdateScheduleChangeKind kind, DateTime? previous) =>
            new(after.ProjectId, after.Id, kind, version, date, after.BcNextUpdateLatestDate, previous);
    }
}
