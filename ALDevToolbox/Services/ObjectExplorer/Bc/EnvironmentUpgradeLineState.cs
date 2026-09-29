using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The one word a line of a planned upgrade shows. Derived, never stored: see
/// <see cref="EnvironmentUpgradeLineState.Derive"/>.
/// </summary>
public enum UpgradeLineState
{
    /// <summary>On the upgrade, and nothing has been done to it from there yet.</summary>
    Planned,

    /// <summary>The upgrade moved its update date out to the latest allowed, and that is the last thing it did.</summary>
    DateMoved,

    /// <summary>A slot is booked from the upgrade, or Business Central has been told to start the update and has not yet.</summary>
    Booked,

    /// <summary>Business Central is busy with the environment's update.</summary>
    Running,

    /// <summary>The environment is on the upgrade's target release or a later one.</summary>
    Updated,

    /// <summary>The last action from the upgrade failed, or Business Central reports the update failed.</summary>
    Failed,

    /// <summary>Somebody did the after-upgrade check and ticked it.</summary>
    Checked,
}

/// <summary>The status of a planned upgrade as a whole, derived from its lines.</summary>
public enum UpgradeStatus
{
    /// <summary>Nothing on it has gone further than a moved date.</summary>
    Planned,

    /// <summary>Something is booked or running, or the wave is part way through.</summary>
    InProgress,

    /// <summary>Every line has an answer: updated, failed or checked. Waiting for someone to mark it done.</summary>
    Updated,

    /// <summary>Marked done; in the archive.</summary>
    Done,
}

/// <summary>
/// Works out a planned upgrade's line state and header status from facts already stored: the
/// mirrored fleet row and the action rows that carry the upgrade's id. Pure, so the two
/// rules are tested on their own and the page, the list and a later MCP read cannot
/// disagree. See <c>.design/environment-updates.md</c>, "Planned upgrades: a header with
/// lines", and issue #984.
/// </summary>
public static class EnvironmentUpgradeLineState
{
    /// <summary>
    /// The line's state. The rules run in a fixed order and the first that matches wins:
    /// <list type="number">
    /// <item><b>Checked</b> when somebody ticked it - a person's word outranks the mirror.</item>
    /// <item><b>Running</b> when Business Central is busy with the environment. Same test as
    /// the Upgrades page's watch after Start update (#982): the environment's state is busy,
    /// or its next update says it is running. The two are read from different places and do
    /// not always change together, so either is enough.</item>
    /// <item><b>Updated</b> when the environment is on the target release or a later one,
    /// Major.Minor compared numerically per segment (a string compare puts 10.1 before 9.2).</item>
    /// <item><b>Failed</b> when the latest action from this upgrade failed, or Business
    /// Central reports the environment in a failed state (<c>UpgradingFailed</c> and the
    /// other <c>*Failed</c> statuses the mirror keeps verbatim). The mirror stores no
    /// separate "last update failed" fact, so an update that ran and rolled back to the old
    /// version, leaving the environment <c>Active</c>, is not caught here - the page's
    /// watch sees that one because it saw the environment busy first; a line read cold
    /// cannot tell it from an update that has not started.</item>
    /// <item><b>Booked</b> when a start is waiting for its slot (a <c>Pending</c> Start
    /// update), or the latest action from this upgrade is a Start update Business Central
    /// accepted and the environment has not picked up yet.</item>
    /// <item><b>DateMoved</b> when the latest action this upgrade sent was a date move that
    /// worked.</item>
    /// <item>Otherwise <b>Planned</b> - including a line whose only booking was cancelled.</item>
    /// </list>
    /// </summary>
    /// <param name="row">The environment's fleet row, from the mirror.</param>
    /// <param name="actionsFromThisUpgrade">Every action row for this environment carrying this upgrade's id, in any order.</param>
    /// <param name="isChecked">Whether the line's check is ticked.</param>
    /// <param name="targetVersion">The upgrade's target release, Major.Minor.</param>
    public static UpgradeLineState Derive(
        UpgradeFleetRow row,
        IReadOnlyList<OeEnvironmentUpgradeAction> actionsFromThisUpgrade,
        bool isChecked,
        string targetVersion)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(actionsFromThisUpgrade);

        if (isChecked) return UpgradeLineState.Checked;

        if (IsUpdating(row)) return UpgradeLineState.Running;

        if (IsOnTarget(row.Version, targetVersion)) return UpgradeLineState.Updated;

        var ordered = actionsFromThisUpgrade
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .ToList();
        var latest = ordered.FirstOrDefault();

        if (latest?.Status == UpgradeActionStatus.Failed
            || BcEnvironmentStatus.Classify(row.Status) == BcEnvironmentReadiness.Failed)
        {
            return UpgradeLineState.Failed;
        }

        if (ordered.Any(a => a.Kind == UpgradeActionKind.RunNow && a.Status == UpgradeActionStatus.Pending)
            || latest is { Kind: UpgradeActionKind.RunNow, Status: UpgradeActionStatus.Sent })
        {
            return UpgradeLineState.Booked;
        }

        var latestSent = ordered.FirstOrDefault(a => a.SentAt is not null);
        if (latestSent is { Kind: UpgradeActionKind.PushDateToLatest, Status: UpgradeActionStatus.Sent })
        {
            return UpgradeLineState.DateMoved;
        }

        return UpgradeLineState.Planned;
    }

    /// <summary>
    /// The upgrade's status from its lines' states: <see cref="UpgradeStatus.Done"/> once
    /// closed, whatever the lines say; otherwise <see cref="UpgradeStatus.InProgress"/> while
    /// anything is booked or running; <see cref="UpgradeStatus.Updated"/> once every line is
    /// updated, failed or checked; <see cref="UpgradeStatus.Planned"/> while no line has gone
    /// beyond a moved date (an upgrade with no lines yet is planned). A wave part way
    /// through - some updated, some still only planned - is in progress.
    /// </summary>
    public static UpgradeStatus DeriveStatus(bool isClosed, IEnumerable<UpgradeLineState> lineStates)
    {
        ArgumentNullException.ThrowIfNull(lineStates);
        if (isClosed) return UpgradeStatus.Done;

        var states = lineStates.ToList();
        if (states.Any(s => s is UpgradeLineState.Booked or UpgradeLineState.Running)) return UpgradeStatus.InProgress;
        if (states.All(s => s is UpgradeLineState.Planned or UpgradeLineState.DateMoved)) return UpgradeStatus.Planned;
        if (states.All(s => s is UpgradeLineState.Updated or UpgradeLineState.Failed or UpgradeLineState.Checked)) return UpgradeStatus.Updated;
        return UpgradeStatus.InProgress;
    }

    /// <summary>
    /// The states a second pass picks up: the lines that failed or were never started
    /// (a cancelled booking lands on Planned or DateMoved). Not the updated, checked or
    /// running ones.
    /// </summary>
    public static bool IsLeftover(UpgradeLineState state) =>
        state is UpgradeLineState.Failed or UpgradeLineState.Planned or UpgradeLineState.DateMoved;

    /// <summary>
    /// True when Business Central is busy with the environment. The same test as
    /// <c>UpdateWatch.IsUpdating</c>, the watch after Start update (#982); keep the two
    /// in step.
    /// </summary>
    internal static bool IsUpdating(UpgradeFleetRow row) =>
        BcEnvironmentStatus.Classify(row.Status) == BcEnvironmentReadiness.Busy
        || ProjectConnectionService.IsUpdateUnderWay(row.NextUpdateStatus);

    /// <summary>True when <paramref name="version"/> is at or past the target release, by Major.Minor.</summary>
    internal static bool IsOnTarget(string? version, string targetVersion) =>
        !string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(targetVersion)
        && ProjectConnectionService.CompareVersions(
            UpgradeFleetService.MajorMinor(version), UpgradeFleetService.MajorMinor(targetVersion)) >= 0;
}
