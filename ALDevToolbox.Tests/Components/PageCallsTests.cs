using ALDevToolbox.Components.Shared;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// What the pages' <c>DisposeAsync</c> rests on (#1437): leaving cancels the calls
/// under way and comes back only once the last of them has ended, so a page test can
/// drop its database the moment the renderer is gone.
/// </summary>
public sealed class PageCallsTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Leaving_cancels_the_call_under_way_and_waits_for_it_to_end()
    {
        var calls = new PageCalls();
        var ended = false;
        var started = Signal();
        var letGo = Signal();

        _ = calls.Track(Task.Run(async () =>
        {
            started.SetResult();
            // Cancelled, but still holding on - as a query disposing its command would.
            try { await Task.Delay(Timeout.Infinite, calls.Token); }
            catch (OperationCanceledException) { }
            await letGo.Task;
            ended = true;
        }));
        await started.Task;

        var leaving = calls.LeaveAsync().AsTask();
        calls.IsGone.Should().BeTrue();
        leaving.IsCompleted.Should().BeFalse("the call has not ended yet");

        letGo.SetResult();
        await leaving.WaitAsync(Wait);
        ended.Should().BeTrue();
    }

    [Fact]
    public async Task Leaving_with_nothing_under_way_returns_at_once()
    {
        var calls = new PageCalls();
        await calls.Track(Task.CompletedTask);

        await calls.LeaveAsync().AsTask().WaitAsync(Wait);

        calls.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_call_that_fails_while_leaving_waits_still_lets_leaving_finish()
    {
        var calls = new PageCalls();
        var fail = Signal();
        var failing = calls.Track(Task.Run(async () =>
        {
            await fail.Task;
            throw new InvalidOperationException("tick failed");
        }));

        var leaving = calls.LeaveAsync().AsTask();
        leaving.IsCompleted.Should().BeFalse("the call is still running");

        fail.SetResult();
        await failing.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        await leaving.WaitAsync(Wait);
    }

    [Fact]
    public async Task A_call_a_finishing_call_starts_is_waited_for_too()
    {
        var calls = new PageCalls();
        var first = Signal();
        var second = Signal();
        var secondEnded = false;

        // The shape of a poll tick that finishes by starting the next read.
        var firstCall = calls.Track(Task.Run(async () =>
        {
            await first.Task;
            _ = calls.Track(Task.Run(async () =>
            {
                await second.Task;
                secondEnded = true;
            }));
        }));

        var leaving = calls.LeaveAsync().AsTask();
        first.SetResult();
        // Once the first call has ended, the second has been counted: it was tracked
        // before the first let go. Only the second is left to hold leaving back.
        await firstCall.WaitAsync(Wait, TestContext.Current.CancellationToken);
        leaving.IsCompleted.Should().BeFalse("the second call is still running");

        second.SetResult();
        await leaving.WaitAsync(Wait);
        secondEnded.Should().BeTrue();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
