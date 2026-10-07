using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// A build that finds the release it parents onto still importing elsewhere waits for
/// it, now that builds run side by side (#1137), but never for an import that has not
/// started and never past the limit.
/// </summary>
public sealed class ParentIngestWaitTests
{
    private static Task<int?> Wait(Queue<ProjectBuildService.IngestState> states, TimeProvider clock, CancellationToken ct = default) =>
        ProjectBuildService.WaitForIngestAsync(
            42, "BC 28.0", _ => Task.FromResult(states.Count > 1 ? states.Dequeue() : states.Peek()),
            clock, NullLogger.Instance, ct);

    private static ProjectBuildService.IngestState State(string? status, bool waiting = false) => new(status, waiting);

    [Fact]
    public async Task A_ready_release_is_used_at_once() =>
        (await Wait(new([State("ready")]), new SkippingClock())).Should().Be(42);

    [Fact]
    public async Task A_release_whose_import_failed_is_not_used() =>
        (await Wait(new([State("failed")]), new SkippingClock())).Should().BeNull();

    [Fact]
    public async Task An_import_still_waiting_for_a_worker_is_not_waited_on() =>
        (await Wait(new([State("ingesting", waiting: true)]), new SkippingClock())).Should().Be(42);

    [Fact]
    public async Task A_running_import_is_waited_for_until_it_finishes()
    {
        var clock = new SkippingClock();
        var states = new Queue<ProjectBuildService.IngestState>([State("ingesting"), State("ingesting"), State("ready")]);

        (await Wait(states, clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
        clock.Waited.Should().Be(2 * ProjectBuildService.IngestPollInterval);
    }

    [Fact]
    public async Task The_build_carries_on_after_the_limit()
    {
        var clock = new SkippingClock();

        (await Wait(new([State("ingesting")]), clock).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(42);
        clock.Waited.Should().BeGreaterThanOrEqualTo(ProjectBuildService.IngestWaitLimit)
            .And.BeLessThan(ProjectBuildService.IngestWaitLimit + 2 * ProjectBuildService.IngestPollInterval);
    }

    /// <summary>A clock whose every delay ends at once, moving time on by the delay.</summary>
    private sealed class SkippingClock : TimeProvider
    {
        private readonly DateTimeOffset _start = new(2026, 10, 7, 1, 0, 0, TimeSpan.Zero);
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
}
