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
    /// <param name="name">
    /// The worker's name, kept so a refused restore can say what it waited for
    /// (#1579). Pass the same name to <see cref="EndJob"/>. Null for the database
    /// guard's per-command count, which is too frequent to name.
    /// </param>
    public bool TryBeginJob(string? name = null)
    {
        Interlocked.Increment(ref _activeJobs);
        if (!IsActive)
        {
            Track(name, +1);
            return true;
        }
        Interlocked.Decrement(ref _activeJobs);
        return false;
    }

    /// <summary>
    /// Counts a job that runs whatever the flag says, for a worker that applies its
    /// own maintenance policy inside the job. The restore still waits for it.
    /// </summary>
    public void BeginJobRegardless(string? name = null)
    {
        Interlocked.Increment(ref _activeJobs);
        Track(name, +1);
    }

    /// <summary>Releases what <see cref="TryBeginJob"/> or <see cref="BeginJobRegardless"/> claimed, under the same name.</summary>
    public void EndJob(string? name = null)
    {
        Track(name, -1);
        Interlocked.Decrement(ref _activeJobs);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _runningByName = new();

    private void Track(string? name, int delta)
    {
        if (name is null) return;
        var now = _runningByName.AddOrUpdate(name, delta, (_, n) => n + delta);
        if (now <= 0) _runningByName.TryRemove(new KeyValuePair<string, int>(name, now));
    }

    /// <summary>
    /// The named jobs running now, one entry per running job, and how many counted
    /// jobs carry no name (database commands in flight, mostly), by name. For the message a
    /// refused restore gives (#1579).
    /// </summary>
    public (IReadOnlyList<string> Named, int Unnamed) RunningJobs()
    {
        var named = new List<string>();
        foreach (var (name, count) in _runningByName.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            for (var i = 0; i < count; i++) named.Add(name);
        }
        return (named, Math.Max(0, ActiveJobs - named.Count));
    }

    // ---- Database access during a restore (#1571) ---------------------------------

    private const int ReadsOnly = 1;
    private const int ReadsAndWrites = 2;

    private readonly AsyncLocal<int> _databaseAllowance = new();

    /// <summary>
    /// True in a flow that <see cref="Data.MaintenanceDatabaseGuard"/> lets read and
    /// write the database while maintenance mode is on.
    /// </summary>
    public bool DatabaseAllowedHere => _databaseAllowance.Value >= ReadsAndWrites;

    /// <summary>True in a flow the guard lets read, but not write, during maintenance.</summary>
    public bool DatabaseReadsAllowedHere => _databaseAllowance.Value >= ReadsOnly;

    /// <summary>
    /// Lets the current flow, and everything it awaits, read and write the database
    /// while maintenance mode is on, until the result is disposed. For the restore's
    /// own work, and a background job already counted on <see cref="ActiveJobs"/>
    /// (the restore waits for it, so it must be able to finish). Nothing else should
    /// take it. The allowance is an <see cref="AsyncLocal{T}"/>, so it travels into
    /// anything started under it: no detached work (a fire-and-forget task, a timer)
    /// may be started inside it, or that work would keep the allowance after the
    /// scope ends.
    /// </summary>
    public IDisposable AllowDatabase() => Allow(ReadsAndWrites);

    /// <summary>
    /// Lets the current flow read, but not write, the database while maintenance mode
    /// is on. For a request that arrives during maintenance: authentication and the
    /// gates ahead of the maintenance page read the database to get there, and a write
    /// on the way (a token refresh, a first Microsoft sign-in) must not land in the
    /// database being restored. The same rule as <see cref="AllowDatabase"/> applies
    /// to detached work.
    /// </summary>
    public IDisposable AllowDatabaseReads() => Allow(ReadsOnly);

    private IDisposable Allow(int level)
    {
        var previous = _databaseAllowance.Value;
        _databaseAllowance.Value = Math.Max(previous, level);
        return new DatabaseAllowance(this, previous);
    }

    private sealed class DatabaseAllowance(MaintenanceModeState owner, int previous) : IDisposable
    {
        public void Dispose() => owner._databaseAllowance.Value = previous;
    }

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
