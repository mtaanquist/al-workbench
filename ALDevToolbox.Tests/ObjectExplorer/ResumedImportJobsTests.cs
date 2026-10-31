using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The jobs the startup reconcile hands back go onto the bounded import queue only
/// once the host has started, so a restart with more jobs than the queue holds no
/// longer blocks startup forever (#1107).
/// </summary>
public sealed class ResumedImportJobsTests
{
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: 1, UserId: null, IsSiteAdmin: false, IsSystemOrganization: true);

    private static List<ReleaseImportJob> Jobs(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new ReleaseImportJob(i, Identity, new ReleaseImportSource.Url($"https://example.com/{i}.zip"), JobRowId: i))
            .ToList();

    [Fact]
    public async Task More_jobs_than_the_queue_holds_do_not_block_and_all_arrive_in_order_once_started()
    {
        var queue = new ReleaseImportQueue();
        var lifetime = new FakeLifetime();
        var jobs = Jobs(40);

        // Returns at once: nothing is written before the host has started.
        ResumedImportJobs.QueueWhenStarted(lifetime, queue, new ProjectBuildQueue(), jobs, NullLogger.Instance);
        queue.Reader.TryRead(out _).Should().BeFalse();

        lifetime.Start();

        var drained = new List<int>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (drained.Count < jobs.Count)
        {
            var job = await queue.Reader.ReadAsync(timeout.Token);
            drained.Add(job.ReleaseId);
        }
        drained.Should().Equal(jobs.Select(j => j.ReleaseId));
    }

    // Builds have their own queue, which never makes a writer wait, so they go back
    // on it before the host has started (#1137).
    [Fact]
    public void Builds_go_back_on_the_build_queue_at_once()
    {
        var queue = new ReleaseImportQueue();
        var builds = new ProjectBuildQueue();
        var jobs = Enumerable.Range(1, 40)
            .Select(i => new ReleaseImportJob(i, Identity, new ReleaseImportSource.ProjectBuild(i), JobRowId: i))
            .Append(new ReleaseImportJob(99, Identity, new ReleaseImportSource.Url("https://example.com/dvd.zip"), JobRowId: 99))
            .ToList();

        ResumedImportJobs.QueueWhenStarted(new FakeLifetime(), queue, builds, jobs, NullLogger.Instance);

        builds.WaitingCount.Should().Be(40);
        queue.Reader.TryRead(out _).Should().BeFalse("imports still wait for the host to start");
    }

    [Fact]
    public async Task Backfills_resumed_after_a_restart_take_one_slot_at_a_time()
    {
        var queue = new ReleaseImportQueue();
        var lifetime = new FakeLifetime();
        var backfills = Enumerable.Range(1, 30)
            .Select(i => new ReleaseImportJob(i, Identity, new ReleaseImportSource.Backfill(), JobRowId: i))
            .ToList();
        try
        {
            ResumedImportJobs.QueueWhenStarted(lifetime, queue, new ProjectBuildQueue(), backfills, NullLogger.Instance);
            lifetime.Start();

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (queue.Reader.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
            await Task.Delay(200);
            queue.Reader.Count.Should().Be(ReleaseImportQueue.PacedJobLimit,
                "hundreds of backfills cut off by a deploy must not fill the queue other imports use");

            queue.Reader.TryRead(out var first).Should().BeTrue();
            queue.MarkPickedUp(first!);
            var next = await queue.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            next.ReleaseId.Should().Be(2);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [Fact]
    public async Task Shutting_down_while_the_queue_is_full_stops_quietly()
    {
        var queue = new ReleaseImportQueue();
        using var stopping = new CancellationTokenSource();
        var run = ResumedImportJobs.EnqueueAllAsync(queue, Jobs(20), NullLogger.Instance, "resumed after restart", stopping.Token);

        // Sixteen fit; the seventeenth waits for a reader that never comes.
        run.IsCompleted.Should().BeFalse();
        await stopping.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        run.IsCompletedSuccessfully.Should().BeTrue();
    }

    /// <summary>
    /// The feed holds jobs read from the database before a restore. Each one keeps the
    /// epoch it was built under, the one waiting on a full queue included, and the feed
    /// stops once a restore has moved the epoch on, so none of them runs against the
    /// restored database (#1538).
    /// </summary>
    [Fact]
    public async Task A_feed_cut_across_a_restore_stops_and_its_jobs_are_dropped()
    {
        var maintenance = new ALDevToolbox.Services.Operations.MaintenanceModeState();
        var queue = new ReleaseImportQueue(maintenance);
        var built = queue.Epoch;
        var run = ResumedImportJobs.EnqueueAllAsync(
            queue, Jobs(20), NullLogger.Instance, "resumed after restart", TestContext.Current.CancellationToken, epoch: built);
        run.IsCompleted.Should().BeFalse("sixteen fit and the seventeenth waits for room");

        maintenance.AdvanceEpoch();

        // Reading makes room; every job read was built before the restore, so none is handed out.
        queue.Reader.TryRead(out _).Should().BeFalse();
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        queue.Reader.TryRead(out _).Should().BeFalse("the job that was waiting for room keeps its old epoch");
        queue.Reader.Count.Should().Be(0, "the feed stopped instead of queuing the rest");
    }

    /// <summary>
    /// A paced backfill built before a restore is dropped at read, and the slot it held
    /// is free for the next one (#1538).
    /// </summary>
    [Fact]
    public async Task A_paced_job_built_before_a_restore_is_dropped_and_frees_its_slot()
    {
        var maintenance = new ALDevToolbox.Services.Operations.MaintenanceModeState();
        var queue = new ReleaseImportQueue(maintenance);
        var built = queue.Epoch;
        var jobs = Jobs(2);
        maintenance.AdvanceEpoch();

        await queue.EnqueuePacedAsync(jobs[0], TestContext.Current.CancellationToken, epoch: built);
        queue.Reader.TryRead(out _).Should().BeFalse();

        await queue.EnqueuePacedAsync(jobs[1], TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        queue.Reader.TryRead(out var fresh).Should().BeTrue();
        fresh!.ReleaseId.Should().Be(2);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
    }
}
