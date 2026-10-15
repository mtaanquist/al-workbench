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
