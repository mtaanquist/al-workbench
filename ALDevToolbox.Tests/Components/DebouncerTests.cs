using ALDevToolbox.Components.Shared;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The wait behind the Environments and Upgrades search boxes (#981): a burst of
/// keystrokes filters once, with the last one, and nothing fires after the page closes.
/// </summary>
public sealed class DebouncerTests
{
    // On a clock of its own, so a stall between two keystrokes can never let an earlier
    // one through (#1439): time moves only when the test says so.
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);
    private readonly ManualClock _clock = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task A_burst_of_calls_runs_only_the_last_one_once()
    {
        using var debouncer = new Debouncer(Delay, _clock);
        var ran = new List<int>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        for (var i = 1; i <= 5; i++)
        {
            var n = i;
            debouncer.Trigger(() =>
            {
                lock (ran) ran.Add(n);
                done.TrySetResult();
                return Task.CompletedTask;
            });
        }

        // One wait left, for the last call, and it has not fired: the earlier four were
        // dropped rather than left to run, and the burst has not been quiet long enough.
        _clock.PendingTimers.Should().Be(1, "only the last call is still waiting");
        _clock.Advance(Delay - TimeSpan.FromMilliseconds(1));
        _clock.PendingTimers.Should().Be(1, "the burst has not been quiet for the whole delay yet");

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await done.Task.WaitAsync(Settle, TestContext.Current.CancellationToken);
        _clock.Advance(Delay * 3);
        lock (ran) ran.Should().Equal(5);
    }

    [Fact]
    public async Task Nothing_runs_once_it_is_disposed()
    {
        var debouncer = new Debouncer(Delay, _clock);
        var ran = false;
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        debouncer.Dispose();
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        await AdvancePastAsync();
        ran.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_drops_the_waiting_call()
    {
        using var debouncer = new Debouncer(Delay, _clock);
        var ran = false;
        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        debouncer.Cancel();

        await AdvancePastAsync();
        ran.Should().BeFalse();
    }

    /// <summary>
    /// Moves past the delay with a second debouncer waiting on the same clock, and waits
    /// for that one to run: a call that was not dropped falls due before it, so by then it
    /// has had its turn.
    /// </summary>
    private async Task AdvancePastAsync()
    {
        using var witness = new Debouncer(Delay, _clock);
        var witnessed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        witness.Trigger(() => { witnessed.TrySetResult(); return Task.CompletedTask; });

        _clock.Advance(Delay * 4);
        await witnessed.Task.WaitAsync(Settle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_zero_delay_runs_the_action_straight_away()
    {
        using var debouncer = new Debouncer(TimeSpan.Zero);
        var ran = false;

        debouncer.Trigger(() => { ran = true; return Task.CompletedTask; });

        ran.Should().BeTrue();
    }
}
