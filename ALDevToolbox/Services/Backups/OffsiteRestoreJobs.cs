using System.Collections.Concurrent;
using System.Threading.Channels;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Lifecycle of an off-site restore download job. The SiteAdmin page
/// renders the active rows as a progress bar; terminal rows (Completed,
/// Failed) linger in the dictionary for an hour so a redirected operator
/// can still see what happened.
/// </summary>
public enum OffsiteRestoreJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>
/// Discriminator between whole-DB dump downloads and per-tenant snapshot
/// downloads. The worker uses this to pick the right
/// <see cref="OffsiteBackupService"/> method; the status endpoint and the
/// SiteAdmin pages use it to decide which catalogue surfaces the row.
/// </summary>
public enum OffsiteRestoreJobKind
{
    WholeDb = 0,
    PerTenant = 1,
}

/// <summary>
/// Immutable snapshot of an in-flight or terminal off-site restore job.
/// The <see cref="OffsiteRestoreJobs"/> singleton swaps the value in the
/// dictionary on every progress tick rather than mutating it in place so
/// concurrent JSON-status reads always see a consistent view.
/// </summary>
public sealed record OffsiteRestoreJob(
    Guid Id,
    OffsiteRestoreJobKind Kind,
    string ObjectKey,
    string FileName,
    DateTime StartedAt,
    DateTime UpdatedAt,
    long BytesDownloaded,
    long? TotalBytes,
    OffsiteRestoreJobStatus Status,
    string? Error,
    int? BackupId,
    bool RecoverFromAnotherInstallation = false);

/// <summary>
/// Process-local job tracker for off-site restore downloads. Holds the
/// state every active and recently-terminal job and the queue the
/// background worker drains. Singleton because there's only ever one
/// process-wide worker; the JSON status endpoint and the worker share
/// this instance.
///
/// <para>
/// Job state lives in memory. A process restart drops in-flight jobs —
/// acceptable for a feature that's nearly always going to be used at
/// most once per deployment lifetime. The staging-file rename in
/// <see cref="OffsiteBackupService.DownloadAsync"/> ensures a crash
/// mid-download doesn't leave a corrupt local row behind.
/// </para>
/// </summary>
public sealed class OffsiteRestoreJobs
{
    private static readonly TimeSpan RetainTerminalFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, OffsiteRestoreJob> _jobs = new();
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly TimeProvider _clock;

    public OffsiteRestoreJobs(TimeProvider clock)
    {
        _clock = clock;
    }

    internal ChannelReader<Guid> Reader => _queue.Reader;

    /// <summary>
    /// Registers a new job and enqueues it for the worker. Returns the
    /// freshly-minted id so the endpoint can redirect to a page that
    /// knows which job to poll. <paramref name="recoverFromAnotherInstallation"/>
    /// carries the SiteAdmin's confirmation that a whole-database dump made by
    /// another installation is being restored to rebuild it (#1322); the
    /// download re-checks that this installation is still fresh.
    /// </summary>
    public Guid Enqueue(OffsiteRestoreJobKind kind, string objectKey, string fileName, bool recoverFromAnotherInstallation = false)
    {
        EvictOldTerminal();

        // Dedupe: a double-submit (or retry) for the same object would queue a
        // duplicate multi-GB download; the second runs after the first then fails
        // the "a local backup already exists" guard. If a non-terminal job for
        // this (kind, objectKey) is already pending/running, hand back its id so
        // the caller polls the existing job instead. See issue #381.
        var existing = _jobs.Values.FirstOrDefault(j =>
            j.Kind == kind
            && string.Equals(j.ObjectKey, objectKey, StringComparison.Ordinal)
            && (j.Status == OffsiteRestoreJobStatus.Pending || j.Status == OffsiteRestoreJobStatus.Running));
        if (existing is not null) return existing.Id;

        var now = _clock.GetUtcNow().UtcDateTime;
        var id = Guid.NewGuid();
        var job = new OffsiteRestoreJob(
            Id: id,
            Kind: kind,
            ObjectKey: objectKey,
            FileName: fileName,
            StartedAt: now,
            UpdatedAt: now,
            BytesDownloaded: 0,
            TotalBytes: null,
            Status: OffsiteRestoreJobStatus.Pending,
            Error: null,
            BackupId: null,
            RecoverFromAnotherInstallation: recoverFromAnotherInstallation);
        _jobs[id] = job;
        if (!_queue.Writer.TryWrite(id))
        {
            // Unbounded channel never refuses a write under normal
            // conditions; the only path here is a closed writer, which we
            // never invoke. Surface it loudly if it ever does happen.
            throw new InvalidOperationException("Off-site restore queue is closed.");
        }
        return id;
    }

    public OffsiteRestoreJob? Get(Guid id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Snapshot of every non-evicted job (active + recently terminal), newest first.</summary>
    public IReadOnlyList<OffsiteRestoreJob> List()
    {
        EvictOldTerminal();
        return _jobs.Values
            .OrderByDescending(j => j.StartedAt)
            .ToList();
    }

    internal void MarkRunning(Guid id, long? totalBytes)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Running,
            TotalBytes = totalBytes,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void ReportProgress(Guid id, long bytesDownloaded, long? totalBytes)
    {
        Update(id, j => j with
        {
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes ?? j.TotalBytes,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void MarkCompleted(Guid id, int backupId)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Completed,
            BackupId = backupId,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    internal void MarkFailed(Guid id, string error)
    {
        Update(id, j => j with
        {
            Status = OffsiteRestoreJobStatus.Failed,
            Error = error,
            UpdatedAt = _clock.GetUtcNow().UtcDateTime,
        });
    }

    private void Update(Guid id, Func<OffsiteRestoreJob, OffsiteRestoreJob> mutate)
    {
        // AddOrUpdate's update factory races with concurrent updates;
        // ConcurrentDictionary handles that — the worker is the only
        // mutator anyway, but the loop guard keeps it correct if that
        // changes.
        while (_jobs.TryGetValue(id, out var current))
        {
            var next = mutate(current);
            if (_jobs.TryUpdate(id, next, current)) return;
        }
    }

    private void EvictOldTerminal()
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - RetainTerminalFor;
        foreach (var (id, job) in _jobs)
        {
            if (job.Status is OffsiteRestoreJobStatus.Completed or OffsiteRestoreJobStatus.Failed
                && job.UpdatedAt < cutoff)
            {
                _jobs.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>
/// Drains the <see cref="OffsiteRestoreJobs"/> queue one job at a time
/// and runs each download in a scoped <see cref="OffsiteBackupService"/>.
/// Sequential by design — concurrent multi-gigabyte downloads to the
/// same volume would just thrash disk.
///
/// <para>
/// A download is a background job like any other across a restore (#1579,
/// #1597): started while the site is open it is counted while it downloads,
/// so a restore waits for it (and refuses if it does not finish); one picked
/// up while a restore is running waits until the site reopens. The count is
/// released once the file is on disk, before the final insert waits for the
/// backup lock: a restore that began meanwhile holds that lock while it waits
/// for the count, so the restore goes ahead and the row lands after it. The exception is a site held
/// closed after a failed restore, where pulling a good backup from the bucket
/// is how the SiteAdmin recovers: then the download runs, uncounted so it
/// does not hold up the restore that follows. Either way it is given the
/// database (<see cref="MaintenanceModeState.AllowDatabase"/>), and its final
/// insert takes the backup lock, so the row cannot land inside a restore.
/// </para>
/// </summary>
public sealed class OffsiteRestoreWorker : BackgroundService
{
    private readonly OffsiteRestoreJobs _jobs;
    private readonly IServiceProvider _services;
    private readonly ILogger<OffsiteRestoreWorker> _logger;
    private readonly WorkerHeartbeat _heartbeat;
    private readonly MaintenanceModeState _maintenance;

    /// <summary>How often a download waiting for a restore to finish checks again.</summary>
    internal TimeSpan MaintenancePollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public OffsiteRestoreWorker(
        OffsiteRestoreJobs jobs,
        IServiceProvider services,
        ILogger<OffsiteRestoreWorker> logger,
        WorkerHeartbeatRegistry heartbeats,
        MaintenanceModeState maintenance)
    {
        _jobs = jobs;
        _services = services;
        _logger = logger;
        _maintenance = maintenance;
        // Queue-driven; downloads can be large (multi-GB pg_dump archives) and
        // network-bound. 4-hour active ceiling lets a real restore complete
        // even on a slow link while still flagging a hung connection.
        _heartbeat = heartbeats.Register(nameof(OffsiteRestoreWorker),
            maxActiveDuration: TimeSpan.FromHours(4),
            maxIdleSilence: null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var jobId in _jobs.Reader.ReadAllAsync(stoppingToken))
        {
            _heartbeat.BeginActive();
            try
            {
                await RunOneAsync(jobId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Matches the other background workers: one bad job never kills
                // the loop. Anything escaping RunOneAsync's own catch (scope or
                // service resolution, the unknown-kind guard) would otherwise
                // reach the host, which defaults to StopHost and takes the whole
                // app down. Mark the job failed too, or it sits "Running" in the
                // SiteAdmin poller forever.
                _logger.LogError(ex, "Off-site restore worker tripped on JobId={JobId}.", jobId);
                _jobs.MarkFailed(jobId, ex.Message);
            }
            finally { _heartbeat.EndActive(); }
        }
    }

    private async Task RunOneAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = _jobs.Get(jobId);
        if (job is null) return;

        var counted = await WaitForDatabaseAsync(jobId, stoppingToken);
        // Released early, once the file is on disk (see DownloadAsync), or at the
        // end; whichever comes first, and only once.
        var released = counted ? 0 : 1;
        void ReleaseJob()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) _maintenance.EndJob();
        }
        try
        {
            await RunCountedAsync(jobId, job, ReleaseJob, stoppingToken);
        }
        finally
        {
            ReleaseJob();
        }
    }

    /// <summary>
    /// Claims a job slot while the site is open (true). While a restore runs,
    /// waits for the site to reopen; while it is held closed after a failed
    /// restore, goes ahead uncounted (false). See the class remarks.
    /// </summary>
    private async Task<bool> WaitForDatabaseAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var logged = false;
        while (true)
        {
            if (_maintenance.TryBeginJob()) return true;
            if (_maintenance.IsHeldAfterFailedRestore) return false;
            if (!logged)
            {
                _logger.LogInformation(
                    "Off-site restore job {JobId} is waiting for the restore in progress to finish.", jobId);
                logged = true;
            }
            await Task.Delay(MaintenancePollInterval, stoppingToken);
        }
    }

    private async Task RunCountedAsync(Guid jobId, OffsiteRestoreJob job, Action releaseJob, CancellationToken stoppingToken)
    {
        // Counted, or let through on purpose by WaitForDatabaseAsync: either way
        // the database guard must not refuse this job's commands (#1597),
        // including the final insert after the count is released. Taken inside
        // this method, so it ends with the job; the download starts no detached
        // work that could keep it.
        using var allowDatabase = _maintenance.AllowDatabase();
        await using var scope = _services.CreateAsyncScope();
        var offsite = scope.ServiceProvider.GetRequiredService<OffsiteBackupService>();

        try
        {
            _jobs.MarkRunning(jobId, totalBytes: null);
            var progress = new Progress<(long BytesDownloaded, long? TotalBytes)>(tuple =>
                _jobs.ReportProgress(jobId, tuple.BytesDownloaded, tuple.TotalBytes));
            var backupId = job.Kind switch
            {
                OffsiteRestoreJobKind.WholeDb => await offsite.DownloadAsync(job.ObjectKey, progress, job.RecoverFromAnotherInstallation, stoppingToken, releaseJob),
                OffsiteRestoreJobKind.PerTenant => await offsite.DownloadPerTenantAsync(job.ObjectKey, progress, stoppingToken, releaseJob),
                _ => throw new InvalidOperationException($"Unknown off-site restore job kind: {job.Kind}"),
            };
            _jobs.MarkCompleted(jobId, backupId);
            _logger.LogInformation(
                "Off-site restore job {JobId} ({Kind}) for {ObjectKey} completed as local row {RowId}.",
                jobId, job.Kind, job.ObjectKey, backupId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _jobs.MarkFailed(jobId, "Cancelled by host shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Off-site restore job {JobId} for {ObjectKey} failed.", jobId, job.ObjectKey);
            _jobs.MarkFailed(jobId, ex.Message);
        }
    }
}
