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
    private volatile string? _reason;
    private DateTime _startedAt;

    public bool IsActive => _reason is not null;
    public string? Reason => _reason;
    public DateTime StartedAtUtc => _startedAt;

    /// <summary>
    /// Flips the flag on, or replaces the reason when it is already on. The
    /// caller owns the <c>try/finally</c> that calls <see cref="Exit"/>. The
    /// one deliberate exception is a restore that fails after the database was
    /// dropped: it leaves the flag on, with a reason saying so, until a SiteAdmin
    /// restores another backup or the process restarts (#1321).
    /// </summary>
    public void Enter(string reason)
    {
        _reason = reason;
        _startedAt = DateTime.UtcNow;
    }

    public void Exit() => _reason = null;
}
