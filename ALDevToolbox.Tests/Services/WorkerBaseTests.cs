using System.Reflection;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
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
        public TestQueue() : base(capacity: 8, keySelector: job => job.Id) { }
    }

    private sealed class RerunningTestQueue : JobQueue<TestJob, int>
    {
        public RerunningTestQueue() : base(capacity: 8, keySelector: job => job.Id, rerunIfRequestedWhileRunning: true) { }
    }

    private sealed class TestDrainWorker : QueueDrainWorker<TestJob>
    {
        private readonly TestQueue _queue;
        public List<int> Ran { get; } = new();

        public TestDrainWorker(TestQueue queue, WorkerHeartbeatRegistry heartbeats, MaintenanceModeState? maintenance = null)
            : base(queue.Reader, NullLogger.Instance, heartbeats, "TestDrainWorker", TimeSpan.FromMinutes(1), maintenance) =>
            _queue = queue;

        protected override Task RunJobAsync(TestJob job, CancellationToken ct)
        {
            lock (Ran) Ran.Add(job.Id);
            if (job.Throws) throw new InvalidOperationException("job blew up");
            return Task.CompletedTask;
        }

        protected override void OnJobFinished(TestJob job) => _queue.Complete(job.Id);

        public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
    }

    private sealed class TestScheduler : PolledScheduler
    {
        public int Ticks;

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
            return Task.CompletedTask;
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
    /// A job picked up during a restore waits for the site to reopen and then runs. It
    /// is not dropped: an import has no scheduler that would offer it again (#1538).
    /// </summary>
    [Fact]
    public async Task QueueDrainWorker_holds_a_job_while_maintenance_mode_is_on_and_runs_it_after()
    {
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring backup test.dump");
        var queue = new TestQueue();
        var worker = new TestDrainWorker(queue, new WorkerHeartbeatRegistry(), maintenance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new TestJob(1), cts.Token);
        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            // Long enough for the loop to have picked the job up and reached the gate.
            while (queue.Reader.Count > 0 && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
            await Task.Delay(QueueDrainWorker<TestJob>.MaintenancePollInterval / 2, cts.Token);
            lock (worker.Ran) worker.Ran.Should().BeEmpty("nothing runs during a restore");
            queue.IsInFlight(1).Should().BeTrue("the job is held, not dropped");

            maintenance.Exit();
            while (!worker.Ran.Contains(1) && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
            lock (worker.Ran) worker.Ran.Should().Equal(1);
        }
        finally
        {
            await cts.CancelAsync();
            try { await loop; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// An import the worker has picked up and is holding still counts as pending, so a
    /// restore that starts meanwhile is refused rather than dropping the schema under
    /// it once the site reopens (#1538).
    /// </summary>
    [Fact]
    public async Task An_import_held_by_its_worker_still_counts_as_pending()
    {
        var maintenance = new MaintenanceModeState();
        maintenance.Enter("Restoring backup test.dump");
        var queue = new ReleaseImportQueue();
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = new ExposedReleaseImportWorker(queue, services, maintenance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));

        await queue.EnqueueAsync(new ReleaseImportJob(
            7,
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(1, isSystem: false),
            new ReleaseImportSource.Url("https://example.invalid/dvd.zip")), cts.Token);
        queue.PendingCount.Should().Be(1, "a queued import is pending");

        var loop = worker.RunLoopAsync(cts.Token);
        try
        {
            while (queue.Reader.Count > 0 && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
            queue.Reader.Count.Should().Be(0, "the worker has picked the import up");
            queue.PendingCount.Should().Be(1, "a held import is still pending");
        }
        finally
        {
            await cts.CancelAsync();
            try { await loop; } catch (OperationCanceledException) { }
        }
        queue.PendingCount.Should().Be(0, "an import the worker let go of is no longer pending");
    }

    private sealed class ExposedReleaseImportWorker : ReleaseImportWorker
    {
        public ExposedReleaseImportWorker(ReleaseImportQueue queue, IServiceProvider services, MaintenanceModeState maintenance)
            : base(queue, services, NullLogger<ReleaseImportWorker>.Instance, new WorkerHeartbeatRegistry(), maintenance)
        { }

        public Task RunLoopAsync(CancellationToken ct) => ExecuteAsync(ct);
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
