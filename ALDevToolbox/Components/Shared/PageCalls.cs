namespace ALDevToolbox.Components.Shared;

/// <summary>
/// The calls a page has under way off its own event handlers - a poll tick, a timer's
/// re-read, a load - so that leaving the page can cancel them and wait for the last one
/// to finish before the circuit's scope, and with it the database connection, goes. A
/// page that only polls through fire-and-forget tasks otherwise returns from
/// <c>DisposeAsync</c> while a tick is still on its way to the database; the page tests
/// met that as #924. A call is only cancelled if it was handed <see cref="Token"/>.
/// </summary>
public sealed class PageCalls
{
    private readonly CancellationTokenSource _gone = new();
    private readonly TaskCompletionSource _quiet = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // One for the page itself, which LeaveAsync gives back, plus one per call under
    // way. Every change is an Interlocked one and only the step to zero completes
    // _quiet, so there is no flag to arm and no wake-up to lose.
    private int _running = 1;
    private int _left;

    /// <summary>Cancelled when the page goes; hand it to the reads the calls make.</summary>
    public CancellationToken Token => _gone.Token;

    public bool IsGone => _gone.IsCancellationRequested;

    /// <summary>
    /// Counts <paramref name="call"/> as under way until it ends, however it ends. A
    /// call that a tracked call starts before it finishes is waited for as well: it is
    /// counted before the finishing one is let go, so the count never touches zero in
    /// between. A call tracked once leaving has finished is not waited for by anyone;
    /// the pages check <see cref="IsGone"/> before they start one.
    /// </summary>
    public async Task Track(Task call)
    {
        Interlocked.Increment(ref _running);
        try
        {
            await call;
        }
        finally
        {
            Release();
        }
    }

    /// <inheritdoc cref="Track(Task)"/>
    public async Task<T> Track<T>(Task<T> call)
    {
        Interlocked.Increment(ref _running);
        try
        {
            return await call;
        }
        finally
        {
            Release();
        }
    }

    /// <summary>
    /// Cancels <see cref="Token"/> and waits until nothing tracked is still running.
    /// The source is left undisposed on purpose - a continuation queued behind this one
    /// may still ask it for the token, and a cancelled source with nothing registered
    /// holds nothing.
    /// </summary>
    public async ValueTask LeaveAsync()
    {
        await _gone.CancelAsync();
        if (Interlocked.Exchange(ref _left, 1) == 0) Release();
        await _quiet.Task;
    }

    private void Release()
    {
        if (Interlocked.Decrement(ref _running) == 0) _quiet.TrySetResult();
    }
}
