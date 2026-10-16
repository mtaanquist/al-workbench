using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Components.Shared;

/// <summary>
/// How one environment's lifecycle state is shown in a fleet table: the row keyline,
/// the glyph in the state column and the word behind it. Shared by the Environments
/// list and the Upgrades page, which list the same rows and must not describe the
/// same environment two ways. See .design/environment-updates.md.
/// </summary>
public static class FleetRowState
{
    /// <summary>
    /// The state in words a consultant would use. The words live in
    /// <see cref="BcEnvironmentStatus.StatusWord(string?)"/>, because the command
    /// palette describes the same environment in a result row and the two must agree.
    /// </summary>
    public static string StatusWord(UpgradeFleetRow row) => BcEnvironmentStatus.StatusWord(row.Status);

    /// <summary>
    /// True when the row exists but Business Central told us no lifecycle state for it.
    /// Note this is NOT "never read": an environment row is only created from a
    /// successful listing, so <c>fetched_at</c> is always set. What can be missing is
    /// the state itself, and saying "not read yet" for that would be wrong.
    /// </summary>
    public static bool StateUnknown(UpgradeFleetRow row) => string.IsNullOrWhiteSpace(row.Status);

    /// <summary>True for a plainly running environment - the one state that needs no word on screen.</summary>
    public static bool IsRunning(UpgradeFleetRow row) => BcEnvironmentStatus.IsRunning(row.Status);

    /// <summary>
    /// The row's keyline state. An unread environment takes the neutral queued
    /// keyline: the design system has no "unknown" row state, and inventing one here
    /// would put app-only CSS into a sheet that is byte-locked to the handoff. The
    /// icon and the hover word are what separate "we never got an answer" from
    /// "Microsoft suspended this" - see <see cref="StatusIcon"/>.
    /// </summary>
    public static string RowState(UpgradeFleetRow row)
    {
        if (StateUnknown(row)) return "is-queued";
        return row.Status!.ToLowerInvariant() switch
        {
            "active" => "is-published",
            "updating" or "upgrading" or "preparing" or "starting" or "stopping" => "is-running",
            "suspended" or "softdeleted" => "is-queued",
            _ => "is-failed",
        };
    }

    /// <summary>
    /// True for an on-premises row whose state is the last one its gateway sent rather
    /// than a current one: the gateway is offline, stopped reporting the tier, or has not
    /// read it for over an hour. Such a row must not read as healthy, whatever it last said.
    /// An on-premises row with no tier behind it at all counts too.
    /// </summary>
    public static bool IsOutOfTouch(UpgradeFleetRow row, DateTime now) =>
        row.IsOnPremises && (row.OnPremises is not { } tier || tier.NeedsAttention(now));

    /// <summary>
    /// <see cref="StatusWord(UpgradeFleetRow)"/>, except that an out-of-touch on-premises
    /// row says it is the last state seen ("Last seen running").
    /// </summary>
    public static string StatusWord(UpgradeFleetRow row, DateTime now) => IsOutOfTouch(row, now)
        ? StateUnknown(row) ? "No current state" : $"Last seen {StatusWord(row).ToLowerInvariant()}"
        : StatusWord(row);

    /// <summary>
    /// <see cref="RowState(UpgradeFleetRow)"/>, except that an out-of-touch on-premises row
    /// takes the warning keyline (the sheet's draft colour) whatever it last reported.
    /// </summary>
    public static string RowState(UpgradeFleetRow row, DateTime now) =>
        IsOutOfTouch(row, now) ? "is-draft" : RowState(row);

    /// <summary><see cref="StatusIcon(UpgradeFleetRow)"/>, with the warning glyph for an out-of-touch row.</summary>
    public static string StatusIcon(UpgradeFleetRow row, DateTime now) =>
        IsOutOfTouch(row, now) ? "triangle-alert" : StatusIcon(row);

    public static string StatusIcon(UpgradeFleetRow row)
    {
        // Checked before the keyline state, because unread and suspended share that
        // state but must never look the same.
        if (StateUnknown(row)) return "circle-dashed";
        return RowState(row) switch
        {
            "is-published" => "circle-check",
            "is-running" => "clock",
            "is-queued" => "circle-pause",
            _ => "circle-alert",
        };
    }
}
