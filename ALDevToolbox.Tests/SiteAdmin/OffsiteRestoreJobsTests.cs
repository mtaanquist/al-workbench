using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// <see cref="OffsiteRestoreJobs"/> is a tiny in-memory state machine, but
/// every status transition is what the SiteAdmin sees through the JSON
/// status endpoint and what the JS poller decides to keep polling for.
/// Pin the transitions and the terminal-row eviction window.
/// </summary>
public sealed class OffsiteRestoreJobsTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 20, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Enqueue_returns_id_and_records_pending_job()
    {
        var jobs = new OffsiteRestoreJobs(_clock);

        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "backups/aldevtoolbox-20260520T010000Z-scheduled.dump", "aldevtoolbox-20260520T010000Z-scheduled.dump");

        var job = jobs.Get(id);
        job.Should().NotBeNull();
        job!.Status.Should().Be(OffsiteRestoreJobStatus.Pending);
        job.BytesDownloaded.Should().Be(0);
        job.TotalBytes.Should().BeNull();
        job.Error.Should().BeNull();
        job.BackupId.Should().BeNull();
    }

    [Fact]
    public void Enqueue_writes_to_queue_reader_for_worker_to_pick_up()
    {
        var jobs = new OffsiteRestoreJobs(_clock);

        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k", "f.dump");

        jobs.Reader.TryRead(out var fromQueue).Should().BeTrue();
        fromQueue.Should().Be(id);
        jobs.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void Status_transitions_running_then_completed_with_progress()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k", "f.dump");

        jobs.MarkRunning(id, totalBytes: 1024);
        jobs.ReportProgress(id, bytesDownloaded: 256, totalBytes: 1024);
        jobs.ReportProgress(id, bytesDownloaded: 1024, totalBytes: 1024);
        jobs.MarkCompleted(id, backupId: 42);

        var job = jobs.Get(id);
        job!.Status.Should().Be(OffsiteRestoreJobStatus.Completed);
        job.BytesDownloaded.Should().Be(1024);
        job.TotalBytes.Should().Be(1024);
        job.BackupId.Should().Be(42);
        job.Error.Should().BeNull();
    }

    [Fact]
    public void Failed_status_carries_error_message()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k", "f.dump");

        jobs.MarkRunning(id, totalBytes: null);
        jobs.MarkFailed(id, "AccessDenied — check the secret key.");

        var job = jobs.Get(id);
        job!.Status.Should().Be(OffsiteRestoreJobStatus.Failed);
        job.Error.Should().Be("AccessDenied — check the secret key.");
        job.BackupId.Should().BeNull();
    }

    [Fact]
    public void Terminal_jobs_are_evicted_after_an_hour()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k", "f.dump");
        jobs.MarkCompleted(id, backupId: 1);

        // Just under the cutoff: the job is still visible.
        _clock.Advance(TimeSpan.FromMinutes(59));
        jobs.Get(id).Should().NotBeNull();

        // Past the cutoff: a touch of List() triggers eviction.
        _clock.Advance(TimeSpan.FromMinutes(2));
        jobs.List();
        jobs.Get(id).Should().BeNull();
    }

    [Fact]
    public void Active_jobs_are_not_evicted_even_when_old()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k", "f.dump");
        jobs.MarkRunning(id, totalBytes: null);

        _clock.Advance(TimeSpan.FromHours(6));
        jobs.List();

        jobs.Get(id).Should().NotBeNull();
        jobs.Get(id)!.Status.Should().Be(OffsiteRestoreJobStatus.Running);
    }

    [Fact]
    public void List_returns_newest_first()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        var first = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k1", "f1.dump");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k2", "f2.dump");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var third = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k3", "f3.dump");

        var list = jobs.List();

        list.Select(j => j.Id).Should().Equal(third, second, first);
    }

    /// <summary>
    /// An exception escaping the per-job work must not reach the host: the
    /// default <c>BackgroundServiceExceptionBehavior</c> is StopHost, so one
    /// bad restore would take the whole app down. Resolving the restore
    /// service from an empty provider throws outside the inner catch, which
    /// is exactly the escape path issue #682 was about. The worker must log
    /// it, fail the job, and keep draining the queue.
    /// </summary>
    [Fact]
    public async Task Worker_survives_an_exception_escaping_a_job_and_keeps_draining()
    {
        var jobs = new OffsiteRestoreJobs(_clock);
        // Empty provider: GetRequiredService<OffsiteBackupService>() throws.
        var services = new ServiceCollection().BuildServiceProvider();
        var worker = new OffsiteRestoreWorker(
            jobs, services, NullLogger<OffsiteRestoreWorker>.Instance, new WorkerHeartbeatRegistry(), new MaintenanceModeState());

        var first = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k1", "f1.dump");
        var second = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, "k2", "f2.dump");

        await worker.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline
            && (jobs.Get(first)!.Status != OffsiteRestoreJobStatus.Failed
                || jobs.Get(second)!.Status != OffsiteRestoreJobStatus.Failed))
        {
            await Task.Delay(20);
        }
        await worker.StopAsync(CancellationToken.None);

        jobs.Get(first)!.Status.Should().Be(OffsiteRestoreJobStatus.Failed);
        jobs.Get(first)!.Error.Should().NotBeNullOrEmpty();
        // The second job proves the loop was not torn down by the first.
        jobs.Get(second)!.Status.Should().Be(OffsiteRestoreJobStatus.Failed);
    }
}
