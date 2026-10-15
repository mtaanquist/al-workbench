namespace ALDevToolbox.Services.Operations;

/// <summary>
/// Process-local maintenance-mode flag. <see cref="BackupService.RestoreAsync"/>
/// flips it on for the duration of an in-place restore; the middleware in
/// <c>Program.cs</c> returns <c>503</c> for every non-SiteAdmin request
/// while the flag is set.
///
/// <para>
/// The flag is intentionally not persisted. If more than one app container
/// is ever pointed at the same database, restore is a SiteAdmin-coordinated
/// downtime — see <c>.design/milestones.md</c>'s M18 entry.
/// </para>
/// </summary>
public sealed class MaintenanceModeState
{
    /// <summary>Everything the flag holds, so a caller can put back exactly what was there.</summary>
    public sealed record Snapshot(string? Reason, DateTime StartedAtUtc, bool HeldAfterFailedRestore);

    private volatile Snapshot _state = new(null, default, false);

    public bool IsActive => _state.Reason is not null;

    /// <summary>
    /// Why the site is closed. Shown to SiteAdmins only: it can name a backup
    /// file, so the public maintenance page uses its own generic wording.
    /// </summary>
    public string? Reason => _state.Reason;

    public DateTime StartedAtUtc => _state.StartedAtUtc;

    /// <summary>
    /// True when a restore failed after the database was dropped and the site is
    /// being held closed until a SiteAdmin restores another backup (#1321).
    /// </summary>
    public bool IsHeldAfterFailedRestore => _state.HeldAfterFailedRestore;

    /// <summary>
    /// Flips the flag on for work in progress. The caller owns the
    /// <c>try/finally</c> that calls <see cref="Exit"/> (or <see cref="Restore"/>).
    /// </summary>
    public void Enter(string reason) => _state = new(reason, DateTime.UtcNow, false);

    /// <summary>
    /// Keeps the flag on after a restore failed once the database was dropped.
    /// Nothing calls <see cref="Exit"/> for it: it stays until a SiteAdmin
    /// restores another backup or the process restarts (#1321).
    /// </summary>
    public void HoldAfterFailedRestore(string reason) => _state = new(reason, DateTime.UtcNow, true);

    /// <summary>The current state, to hand back to <see cref="Restore"/>.</summary>
    public Snapshot Capture() => _state;

    /// <summary>Puts back a state taken with <see cref="Capture"/>.</summary>
    public void Restore(Snapshot snapshot) => _state = snapshot;

    public void Exit() => _state = new(null, default, false);
}
