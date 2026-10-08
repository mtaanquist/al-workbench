using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// A build that finds the release it parents onto still importing elsewhere waits for
/// it, now that builds run side by side (#1137), but never for an import that has not
/// started, never past the limit, and not for an import nothing is working on any more
/// (#1180). Only a real wait gives up the build's place in the queue.
/// </summary>
public sealed class ParentIngestWaitTests
{
    private int _abandoned;
    private int _steppedAside;

    private Task<int?> Wait(Queue<ProjectBuildService.IngestState> states, TimeProvider clock, CancellationToken ct = default) =>
        ProjectBuildService.WaitForIngestAsync(
            42, "BC 28.0", _ => Task.FromResult(states.Count > 1 ? states.Dequeue() : states.Peek()),
            _ =>
            {
                Interlocked.Increment(ref _abandoned);
                return Task.CompletedTask;
            },
            wait =>
            {
                Interlocked.Increment(ref _steppedAside);
                return wait();
            },
            clock, NullLogger.Instance, ct);

    private static ProjectBuildService.IngestState State(string? status, bool waiting = false, bool running = true) =>
        new(status, waiting, running);

    [Fact]
    public async Task A_ready_release_is_used_at_once_without_giving_up_the_build_place()
    {
        (await Wait(new([State("ready")]), new SkippingClock())).Should().Be(42);
        _steppedAside.Should().Be(0);
    }

    [Fact]
    public async Task A_release_whose_import_failed_is_not_used() =>
        (await Wait(new([State("failed")]), new SkippingClock())).Should().BeNull();

    [Fact]
    public async Task An_import_still_waiting_for_a_worker_is_not_waited_on()
    {
        (await Wait(new([State("ingesting", waiting: true)]), new SkippingClock())).Should().Be(42);
        _steppedAside.Should().Be(0);
    }

    [Fact]
    public async Task A_running_import_is_waited_for_until_it_finishes()
    {
        var clock = new SkippingClock();
        var states = new Queue<ProjectBuildService.IngestState>([State("ingesting"), State("ingesting"), State("ready")]);

        (await Wait(states, clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
        clock.Waited.Should().Be(2 * ProjectBuildService.IngestPollInterval);
        _steppedAside.Should().Be(1, "the build gives its place to another build while it waits");
        _abandoned.Should().Be(0);
    }

    [Fact]
    public async Task The_build_carries_on_after_the_limit()
    {
        var clock = new SkippingClock();

        (await Wait(new([State("ingesting")]), clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
        clock.Waited.Should().BeGreaterThanOrEqualTo(ProjectBuildService.IngestWaitLimit)
            .And.BeLessThan(ProjectBuildService.IngestWaitLimit + 2 * ProjectBuildService.IngestPollInterval);
    }

    [Fact]
    public async Task An_import_nothing_is_working_on_is_failed_and_not_used_after_one_poll()
    {
        var clock = new SkippingClock();

        (await Wait(new([State("ingesting", running: false)]), clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
        clock.Waited.Should().Be(ProjectBuildService.IngestPollInterval);
        _abandoned.Should().Be(1);
    }

    [Fact]
    public async Task An_import_that_has_not_been_picked_up_yet_is_not_taken_for_abandoned()
    {
        // A release exists a moment before its import is recorded as running.
        var clock = new SkippingClock();
        var states = new Queue<ProjectBuildService.IngestState>(
            [State("ingesting", running: false), State("ingesting"), State("ingesting", running: false), State("ready")]);

        (await Wait(states, clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
        _abandoned.Should().Be(0);
    }
}

/// <summary>A clock whose every delay ends at once, moving time on by the delay.</summary>
internal sealed class SkippingClock : TimeProvider
{
    private readonly DateTimeOffset _start = DateTimeOffset.UtcNow;
    private long _waitedTicks;

    public TimeSpan Waited => TimeSpan.FromTicks(Interlocked.Read(ref _waitedTicks));

    public override DateTimeOffset GetUtcNow() => _start + Waited;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Interlocked.Add(ref _waitedTicks, dueTime.Ticks);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
