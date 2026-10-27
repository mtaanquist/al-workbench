using ALDevToolbox.Components.Shared;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// What the polling pages' <c>DisposeAsync</c> rests on (#1437): leaving cancels the
/// calls under way and comes back only once the last of them has ended, so a page
/// test can drop its database the moment the renderer is gone.
/// </summary>
public sealed class PageCallsTests
{
    [Fact]
    public async Task Leaving_cancels_the_call_under_way_and_waits_for_it_to_end()
    {
        var calls = new PageCalls();
        var ended = false;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

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
        await Task.Delay(50);
        leaving.IsCompleted.Should().BeFalse("the call has not ended yet");

        letGo.SetResult();
        await leaving.WaitAsync(TimeSpan.FromSeconds(5));
        ended.Should().BeTrue();
    }

    [Fact]
    public async Task Leaving_with_nothing_under_way_returns_at_once()
    {
        var calls = new PageCalls();
        await calls.Track(Task.CompletedTask);

        await calls.LeaveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        calls.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task A_call_that_fails_still_lets_leaving_finish()
    {
        var calls = new PageCalls();
        var failing = calls.Track(Task.FromException(new InvalidOperationException("tick failed")));

        await failing.Invoking(t => t).Should().ThrowAsync<InvalidOperationException>();
        await calls.LeaveAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
}
