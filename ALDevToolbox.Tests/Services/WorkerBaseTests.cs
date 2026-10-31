using System.Reflection;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using AwesomeAssertions;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Services;

/// <summary>
/// The contracts the shared background-worker bases in <c>Services/Workers/</c> own on
/// behalf of every queue, drain worker and scheduler built on them (issue #693): the
/// in-flight dedupe gate, "one bad job never wedges the drain loop", and the
/// <c>DISABLE_*</c> opt-out honoured inside the service rather than at registration.
/// </summary>
public sealed class WorkerBaseTests
{
    private sealed record TestJob(int Id, bool Throws = false);

    private sealed class TestQueue : JobQueue<TestJob, int>
    {
        public TestQueue(MaintenanceModeState? maintenance = null)
            : base(capacity: 8, keySelector: job => job.Id, maintenance: maintenance) { }
    }

    private sealed class RerunningTestQueue : JobQueue<TestJob, int>
    {
        public RerunningTestQueue() : base(capacity: 8, keySelector: job => job.Id, rerunIfRequestedWhileRunning: true) { }
    }

    private sealed class TestDrainWorker : QueueDrainWorker<TestJob>
    {
        private readonly TestQueue _queue;
        private readonly bool _holds;
        public List<int> Ran { get; } = new();

        /// <summary>Every job that finished, ran or not, in order. Tests await it instead of sleeping.</summary>
        public Channel<int> Finished { get; } = Channel.CreateUnbounded<int>();

        /// <summary>Runs as a job is picked up, before the maintenance gate.</summary>
        public Action<TestJob>? Starting { get; init; }

        /// <summary>The job's body, after it is recorded in <see cref="Ran"/>.</summary>
        public Func<TestJob, Task>? Body { get; init; }

        public TestDrainWorker(
            TestQueue queue, WorkerHeartbeatRegistry heartbeats, MaintenanceModeState? maintenance = null, bool holds = true)
            : base(queue.Reader, NullLogger.Instance, heartbeats, "TestDrainWorker", TimeSpan.FromMinutes(1), maintenance)
        {
            _queue = queue;
            _holds = holds;
        }

        protected override bool HoldsJobsDuringMaintenance => _holds;

        protected override async Task RunJobAsync(TestJob job, CancellationToken ct)
        {
            lock (Ran) Ran.Add(job.Id);
            if (job.Throws) throw new InvalidOperationException("job blew up");
            if (Body is not null) await Body(job);
        }

        protected override void OnJobStarting(TestJob job) => Starting?.Invoke(job);

        protected override void OnJobFinished(TestJob job)
        {
            _queue.Complete(job.Id);
            Finished.Writer.TryWrite(job.Id);
        }

        public bool HasRun(int id)
        {
            lock (Ran) return Ran.Contains(id);
        }

        public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class TestScheduler : PolledScheduler
    {
        public int Ticks;
        public Func<Task>? OnTick { get; init; }

        public TestScheduler(WorkerHeartbeatRegistry heartbeats, string disableEnvVar, MaintenanceModeState? maintenance = null)
            : base(NullLogger.Instance, heartbeats, "TestScheduler",
                pollInterval: TimeSpan.FromMilliseconds(10),
                maxActiveDuration: TimeSpan.FromMinutes(1),
                maxIdleSilence: TimeSpan.FromMinutes(1),
                disableEnvVar: disableEnvVar,
                maintenance: maintenance)
        { }

        protected override Task TickAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Ticks);
            return OnTick?.Invoke() ?? Task.CompletedTask;
        }

        public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    [Fact]
    public async Task JobQueue_gates_on_the_key_until_the_job_completes()
    {
        var queue = new TestQueue();

        (await queue.EnqueueAsync(new TestJob(7))).Should().BeTrue();
        queue.IsInFlight(7).Should().BeTrue();
        queue.IsInFlight(8).Should().BeFalse("only the enqueued key is in flight");

        (await queue.EnqueueAsync(new TestJob(7))).Should()
            .BeFalse("a job with the same key is already queued or running");
        (await queue.EnqueueAsync(new TestJob(8))).Should().BeTrue("a different key is not gated");

        queue.Complete(7);
        queue.IsInFlight(7).Should().BeFalse();
        (await queue.EnqueueAsync(new TestJob(7))).Should().BeTrue("the gate reopened on Complete");
    }

    /// <summary>
    /// A request that arrives while its key's job is running may be about inputs the
    /// running job has already read - a repository added to a solution mid-discovery
    /// (#1263) - so a re-running queue holds it and queues it once the run completes.
    /// </summary>
    [Fact]
    public async Task A_rerunning_JobQueue_queues_a_request_made_while_the_key_runs_once_it_completes()
    {
        var queue = new RerunningTestQueue();

        (await queue.EnqueueAsync(new TestJob(7))).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.Start(7);

        (await queue.EnqueueAsync(new TestJob(7))).Should().BeFalse("the key is running, so the request is held");
        queue.TryEnqueue(new TestJob(7)).Should().BeFalse("several held requests collapse into one re-run");
        queue.Reader.TryRead(out _).Should().BeFalse("nothing is queued while the first run is going");

        queue.Complete(7);
        queue.IsInFlight(7).Should().BeTrue("the held request took the slot");
        queue.Reader.TryRead(out var rerun).Should().BeTrue();
        rerun!.Id.Should().Be(7);
        queue.Reader.TryRead(out _).Should().BeFalse("the held requests ran once, not twice");

        queue.Start(7);
        queue.Complete(7);
        queue.IsInFlight(7).Should().BeFalse("nothing arrived during the re-run");
    }

    [Fact]
    public async Task A_rerunning_JobQueue_still_coalesces_a_request_for_a_key_that_is_only_queued()
    {
        var queue = new RerunningTestQueue();

        (await queue.EnqueueAsync(new TestJob(7))).Should().BeTrue();
        (await queue.EnqueueAsync(new TestJob(7))).Should().BeFalse();
        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.Start(7);
        queue.Complete(7);

        queue.IsInFlight(7).Should().BeFalse("the queued job had not started, so it covered the request");
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_plain_JobQueue_drops_a_request_made_while_the_key_runs()
    {
        var queue = new TestQueue();

        (await queue.EnqueueAsync(new TestJob(7))).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.Start(7);
        (await queue.EnqueueAsync(new TestJob(7))).Should().BeFalse();
        queue.Complete(7);

        queue.IsInFlight(7).Should().BeFalse();
        queue.Reader.TryRead(out _).Should().BeFalse("deliveries and refreshes must never run twice");
    }

    [Fact]
    public async Task QueueDrainWorker_completes_a_failed_job_and_keeps_draining()
    {
        var queue = new TestQueue();
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1, Throws: true));
        var loop = worker.RunLoopAsync(cts.Token);

        // The failing job releases its in-flight slot (OnJobFinished runs in finally)
        // and the loop stays alive to take the next one.
        while (queue.IsInFlight(1) && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
        queue.IsInFlight(1).Should().BeFalse("a thrown job must still release the gate");

        (await queue.EnqueueAsync(new TestJob(2))).Should().BeTrue();
        while (!worker.Ran.Contains(2) && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
        worker.Ran.Should().Equal(1, 2);

        await cts.CancelAsync();
        try { await loop; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PolledScheduler_skips_the_loop_when_its_disable_variable_is_set()
    {
        // Unique per test run so a parallel test never sees this variable.
        var envVar = "DISABLE_TEST_SCHEDULER_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(envVar, "1");
        try
        {
            var scheduler = new TestScheduler(new WorkerHeartbeatRegistry(), envVar);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            // Returns immediately — no startup delay, no ticks — rather than running
            // because the registration guard in Program.cs happened not to be there.
            await scheduler.RunLoopAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(2));

            scheduler.Ticks.Should().Be(0);
        }
        finally
        {
            Environment.SetEnvironmentVariable(envVar, null);
        }
    }

    /// <summary>
    /// While a restore runs, no sweep may read or write the database being dropped and
    /// reloaded, so a poll is skipped; the first poll after it runs as usual (#1538).
    /// </summary>
    [Fact]
    public async Task PolledScheduler_skips_its_tick_while_maintenance_mode_is_on()
    {
        var maintenance = new MaintenanceModeState();
        var scheduler = new TestScheduler(new WorkerHeartbeatRegistry(), "DISABLE_UNUSED_" + Guid.NewGuid().ToString("N"), maintenance);
        var ct = TestContext.Current.CancellationToken;

        maintenance.Enter("Restoring backup test.dump");
        (await scheduler.RunTickAsync(ct)).Should().BeFalse();
        scheduler.Ticks.Should().Be(0, "no sweep runs during a restore");

        maintenance.Exit();
        (await scheduler.RunTickAsync(ct)).Should().BeTrue();
        scheduler.Ticks.Should().Be(1, "the sweep runs again once the site reopens");
    }

    /// <summary>
    /// A scheduler tick that is running counts as an active job, so a restore that
    /// starts mid-sweep waits for it to finish (#1538).
    /// </summary>
    [Fact]
    public async Task PolledScheduler_counts_a_running_tick_as_an_active_job()
    {
        var maintenance = new MaintenanceModeState();
        var allowedInTick = false;
        var inTick = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new TestScheduler(new WorkerHeartbeatRegistry(), "DISABLE_UNUSED_" + Guid.NewGuid().ToString("N"), maintenance)
        {
            OnTick = async () =>
            {
                inTick.TrySetResult(maintenance.ActiveJobs);
                await release.Task;
                allowedInTick = maintenance.DatabaseAllowedHere;
            },
        };

        var tick = scheduler.RunTickAsync(TestContext.Current.CancellationToken);
        (await inTick.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().Be(1);
        maintenance.Enter("Restoring backup test.dump");
        release.SetResult();
        (await tick.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Should().BeTrue();
        maintenance.ActiveJobs.Should().Be(0, "the tick released its count when it finished");
        allowedInTick.Should().BeTrue("the restore waits for a counted tick, so it may finish on the database (#1571)");
    }

    /// <summary>
    /// While a restore runs a worker picks up nothing new, so the job stays queued; it
    /// runs once the site reopens if no restore replaced the database meanwhile (#1538).
    /// </summary>
    [Fact]
    public async Task QueueDrainWorker_picks_up_nothing_while_maintenance_mode_is_on()
    {
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring backup test.dump");
        var queue = new TestQueue(maintenance);
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            queue.Reader.Count.Should().Be(1, "the job is still queued, not taken");
            worker.HasRun(1).Should().BeFalse();

            maintenance.Exit();
            (await worker.Finished.Reader.ReadAsync(cts.Token)).Should().Be(1);
            worker.HasRun(1).Should().BeTrue("no restore replaced the database, so the job is still good");
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// A job taken off the queue just as a restore began is held, not run into the
    /// database being dropped. If the restore replaced the database, the job is
    /// dropped when the site reopens, because it belongs to the old one (#1538).
    /// </summary>
    [Fact]
    public async Task A_job_held_across_a_restore_that_replaced_the_database_is_dropped()
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);
        var holding = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance)
        {
            // The restore begins between the worker taking the job and starting it.
            Starting = _ => maintenance.Enter("Restoring backup test.dump"),
        };
        worker.Holding += job => holding.TrySetResult(job.Id);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            (await holding.Task.WaitAsync(cts.Token)).Should().Be(1);
            maintenance.ActiveJobs.Should().Be(0, "a held job is not counted, so the restore need not wait for it");

            maintenance.AdvanceEpoch();
            maintenance.Exit();

            (await worker.Finished.Reader.ReadAsync(cts.Token)).Should().Be(1);
            worker.HasRun(1).Should().BeFalse("the job was queued against the database the restore replaced");
            queue.IsInFlight(1).Should().BeFalse("a dropped job still releases its key");
            maintenance.ActiveJobs.Should().Be(0);
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>The same hold, ended without the database being replaced, runs the job (#1538).</summary>
    [Fact]
    public async Task A_job_held_while_nothing_was_replaced_runs_once_the_site_reopens()
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance)
        {
            Starting = _ => maintenance.Enter("Restoring backup test.dump"),
        };
        worker.Holding += _ => holding.TrySetResult();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            await holding.Task.WaitAsync(cts.Token);
            maintenance.Exit(); // e.g. the restore was refused before the drop

            (await worker.Finished.Reader.ReadAsync(cts.Token)).Should().Be(1);
            worker.HasRun(1).Should().BeTrue();
        }
        finally
        {
            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// A running job is counted on the flag for as long as it runs, so the restore
    /// waits for it; a worker with its own maintenance policy is counted the same way
    /// (#1538).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_running_job_counts_as_active_until_it_finishes(bool holds)
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);
        var running = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowedAfterRestoreBegan = false;
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance, holds)
        {
            Body = async _ =>
            {
                running.TrySetResult(maintenance.ActiveJobs);
                await release.Task;
                // The restore waits for this job, so the database guard must let it finish (#1571).
                allowedAfterRestoreBegan = maintenance.DatabaseAllowedHere;
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            (await running.Task.WaitAsync(cts.Token)).Should().Be(1);
            maintenance.Enter("Restoring backup test.dump");
            (await maintenance.WaitForActiveJobsAsync(TimeSpan.FromMilliseconds(100), cts.Token))
                .Should().BeFalse("the job is still running");

            release.SetResult();
            (await worker.Finished.Reader.ReadAsync(cts.Token)).Should().Be(1);
            (await maintenance.WaitForActiveJobsAsync(TimeSpan.FromSeconds(5), cts.Token)).Should().BeTrue();
            allowedAfterRestoreBegan.Should().BeTrue("a counted job may finish its database work");
            maintenance.DatabaseAllowedHere.Should().BeFalse("the allowance stays inside the job");
        }
        finally
        {
            maintenance.Exit();
            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// A worker with its own maintenance policy that picks a job up during maintenance
    /// only defers it, so the job is not counted: otherwise a job deferring and
    /// requeueing for as long as the site is closed would make every restore wait it
    /// out and refuse (#1538).
    /// </summary>
    [Fact]
    public async Task A_job_deferred_during_maintenance_is_not_counted()
    {
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring backup test.dump");
        var queue = new TestQueue(maintenance);
        var inBody = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? allowed = null;
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance, holds: false)
        {
            Body = async _ =>
            {
                allowed = maintenance.DatabaseAllowedHere;
                inBody.TrySetResult(maintenance.ActiveJobs);
                await release.Task; // deferring, as the webhook worker waits before requeueing
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            (await inBody.Task.WaitAsync(cts.Token)).Should().Be(0);
            (await maintenance.WaitForActiveJobsAsync(TimeSpan.Zero, cts.Token))
                .Should().BeTrue("a restore does not wait for a job that only defers");
            allowed.Should().BeFalse("an uncounted job must not reach the database during the restore (#1571)");
        }
        finally
        {
            release.TrySetResult();
            maintenance.Exit();
            await cts.CancelAsync();
            await loop;
        }
    }

    /// <summary>
    /// A key left queued from before a restore does not swallow a new request for the
    /// same key: the old job is dropped at read, and the new one takes its place
    /// without the drop freeing the new one's key (#1538).
    /// </summary>
    [Fact]
    public async Task A_key_queued_before_a_restore_does_not_swallow_a_new_request()
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);

        (await queue.EnqueueAsync(new TestJob(7), TestContext.Current.CancellationToken)).Should().BeTrue();
        maintenance.AdvanceEpoch();

        queue.TryEnqueue(new TestJob(7)).Should().BeTrue("the queued slot belongs to a job that will be dropped");
        queue.TryEnqueue(new TestJob(7)).Should().BeFalse("the new request gates the key as usual");

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.Id.Should().Be(7);
        queue.IsInFlight(7).Should().BeTrue("dropping the old job left the new one's key alone");
        queue.Complete(7);
        queue.IsInFlight(7).Should().BeFalse();
    }

    /// <summary>
    /// A queue given the flag drops at read a job queued before the epoch moved on,
    /// and frees its key for the next request (#1538).
    /// </summary>
    [Fact]
    public async Task A_queue_drops_a_job_from_before_the_epoch_moved_on()
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);

        await queue.EnqueueAsync(new TestJob(1), TestContext.Current.CancellationToken);
        maintenance.AdvanceEpoch();
        await queue.EnqueueAsync(new TestJob(2), TestContext.Current.CancellationToken);

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.Id.Should().Be(2, "job 1 was queued before the restore and is dropped");
        queue.IsInFlight(1).Should().BeFalse();
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    /// <summary>
    /// A request whose write waits on a full queue claims its key; if a restore frees
    /// the key meanwhile and a newer request claims it, the first one giving up must
    /// not release the newer request's claim (#1579).
    /// </summary>
    [Fact]
    public async Task A_cancelled_enqueue_does_not_release_a_newer_claim_on_the_same_key()
    {
        var maintenance = new MaintenanceModeState();
        var queue = new TestQueue(maintenance);
        for (var i = 1; i <= 8; i++) await queue.EnqueueAsync(new TestJob(i), TestContext.Current.CancellationToken);

        using var first = new CancellationTokenSource();
        var older = queue.EnqueueAsync(new TestJob(100), first.Token).AsTask();
        maintenance.AdvanceEpoch();
        using var second = new CancellationTokenSource();
        var newer = queue.EnqueueAsync(new TestJob(100), second.Token).AsTask();
        queue.IsInFlight(100).Should().BeTrue();

        await first.CancelAsync();
        await FluentActions.Awaiting(() => older).Should().ThrowAsync<OperationCanceledException>();

        queue.IsInFlight(100).Should().BeTrue("the newer request still holds its claim");
        await second.CancelAsync();
        await FluentActions.Awaiting(() => newer).Should().ThrowAsync<OperationCanceledException>();
        queue.IsInFlight(100).Should().BeFalse("its own failed write releases it");
    }

    /// <summary>
    /// The bases count a running job under the worker's name, so a refused restore can
    /// say what it waited for (#1579). A job that ends takes its name with it.
    /// </summary>
    [Fact]
    public void Running_jobs_are_kept_by_name_and_the_rest_counted_as_unnamed()
    {
        var maintenance = new MaintenanceModeState();
        maintenance.TryBeginJob("ProjectBuildWorker 1").Should().BeTrue();
        maintenance.TryBeginJob("ProjectBuildWorker 1").Should().BeTrue();
        maintenance.TryBeginJob().Should().BeTrue();

        var (named, unnamed) = maintenance.RunningJobs();
        named.Should().Equal("ProjectBuildWorker 1", "ProjectBuildWorker 1");
        unnamed.Should().Be(1);

        maintenance.Enter("Restoring");
        maintenance.TryBeginJob("ReleaseImportWorker").Should().BeFalse();
        maintenance.RunningJobs().Named.Should().NotContain("ReleaseImportWorker", "a refused claim is not counted");
        maintenance.Exit();

        maintenance.EndJob("ProjectBuildWorker 1");
        maintenance.EndJob("ProjectBuildWorker 1");
        maintenance.EndJob();
        maintenance.RunningJobs().Named.Should().BeEmpty();
        maintenance.ActiveJobs.Should().Be(0);
        ALDevToolbox.Services.Backups.BackupService.DescribeRunning(["ProjectBuildWorker 1", "DeliveryWorker 2", "SomethingNew"], 0)
            .Should().Be("a build, a deployment, background upkeep");
    }

    /// <summary>
    /// The maintenance gate lives in the bases, so it only works if every scheduler
    /// and drain worker takes the flag from DI and hands it on. A new one that forgets
    /// would run straight through a restore (#1538).
    /// </summary>
    [Fact]
    public void Every_scheduler_and_drain_worker_takes_the_maintenance_flag()
    {
        var types = typeof(PolledScheduler).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && (t.IsSubclassOf(typeof(PolledScheduler)) || IsDrainWorker(t)))
            .ToList();
        types.Should().NotBeEmpty();

        var missing = types
            .Where(t => !t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(MaintenanceModeState))))
            .Select(t => t.FullName)
            .ToList();
        missing.Should().BeEmpty("each must accept the maintenance flag so DI hands it to the base");

        static bool IsDrainWorker(Type t)
        {
            for (var b = t.BaseType; b is not null; b = b.BaseType)
                if (b.IsGenericType && b.GetGenericTypeDefinition() == typeof(QueueDrainWorker<>)) return true;
            return false;
        }
    }
}
