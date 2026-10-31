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
    /// <remarks>
    /// A full fence, not a plain volatile write: a restore turns the flag on and then
    /// reads <see cref="ActiveJobs"/>, while a worker counts its job in and then reads
    /// the flag (<see cref="TryBeginJob"/>). The fence keeps either side from missing
    /// the other (#1538).
    /// </remarks>
    public void Enter(string reason)
    {
        Interlocked.Exchange(ref _state, new(reason, DateTime.UtcNow, false));
        Entered?.Invoke();
    }

    /// <summary>Raised after <see cref="Enter"/> turns the flag on. Test seam.</summary>
    internal event Action? Entered;

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

    // ---- Background jobs across a restore (#1538) ----------------------------------

    private long _epoch;
    private int _activeJobs;

    /// <summary>
    /// Counts restores that replaced the database. A job queued or picked up under an
    /// earlier epoch belongs to a database that is gone, so the queues and workers
    /// drop it rather than run it against the restored one. Advanced before
    /// maintenance mode lifts, so nothing held through the restore can miss it.
    /// </summary>
    public long Epoch => Interlocked.Read(ref _epoch);

    /// <summary>Called by the restore once the database it dropped has been replaced (or failed to be).</summary>
    public void AdvanceEpoch() => Interlocked.Increment(ref _epoch);

    /// <summary>Background jobs that have started and not finished.</summary>
    public int ActiveJobs => Volatile.Read(ref _activeJobs);

    /// <summary>
    /// Claims a slot for a job about to run. False while maintenance mode is on, and
    /// the job must then wait. The count goes up before the flag is read, and
    /// <see cref="Enter"/> is a full fence, so a restore that turns the flag on and
    /// then waits for <see cref="ActiveJobs"/> to reach zero either sees this job or
    /// this job sees the flag.
    /// </summary>
    public bool TryBeginJob()
    {
        Interlocked.Increment(ref _activeJobs);
        if (!IsActive) return true;
        Interlocked.Decrement(ref _activeJobs);
        return false;
    }

    /// <summary>
    /// Counts a job that runs whatever the flag says, for a worker that applies its
    /// own maintenance policy inside the job. The restore still waits for it.
    /// </summary>
    public void BeginJobRegardless() => Interlocked.Increment(ref _activeJobs);

    /// <summary>Releases what <see cref="TryBeginJob"/> or <see cref="BeginJobRegardless"/> claimed.</summary>
    public void EndJob() => Interlocked.Decrement(ref _activeJobs);

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for every running job to finish. Call
    /// with the flag on, so no new job can start meanwhile. True when none is left.
    /// </summary>
    public async Task<bool> WaitForActiveJobsAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ActiveJobs > 0)
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
        }
        return true;
    }
}
