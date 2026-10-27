namespace ALDevToolbox.Components.Shared;

/// <summary>
/// The calls a page has under way off its own event handlers - a poll tick, a timer's
/// re-read - so that leaving the page can cancel them and wait for the last one to
/// finish before the circuit's scope, and with it the database connection, goes. A
/// page that only polls through fire-and-forget tasks otherwise returns from
/// <c>DisposeAsync</c> while a tick is still on its way to the database; the page tests
/// met that as #924, and <c>EnvironmentDetail.razor</c>'s Read / Write helpers are the
/// same shape written into one page.
/// </summary>
public sealed class PageCalls
{
    private readonly CancellationTokenSource _gone = new();
    private int _running;
    private TaskCompletionSource? _quiet;

    /// <summary>Cancelled when the page goes; hand it to the reads the calls make.</summary>
    public CancellationToken Token => _gone.Token;

    public bool IsGone => _gone.IsCancellationRequested;

    /// <summary>Counts <paramref name="call"/> as under way until it ends, however it ends.</summary>
    public async Task Track(Task call)
    {
        Interlocked.Increment(ref _running);
        try
        {
            await call;
        }
        finally
        {
            if (Interlocked.Decrement(ref _running) == 0) _quiet?.TrySetResult();
        }
    }

    /// <summary>
    /// Cancels <see cref="Token"/> and waits until nothing tracked is still running,
    /// including a call a finishing one starts next: the flag is armed before each look
    /// at the count, so a call that ends between the two is not missed. The source is
    /// left undisposed on purpose - a continuation queued behind this one may still ask
    /// it for the token, and a cancelled source with nothing registered holds nothing.
    /// </summary>
    public async ValueTask LeaveAsync()
    {
        await _gone.CancelAsync();
        while (true)
        {
            var quiet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _quiet = quiet;
            if (Volatile.Read(ref _running) == 0) break;
            await quiet.Task;
        }
    }
}
