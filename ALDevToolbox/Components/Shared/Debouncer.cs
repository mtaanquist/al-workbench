namespace ALDevToolbox.Components.Shared;

/// <summary>
/// Runs an action once a burst of calls has gone quiet for <c>delay</c>: each call to
/// <see cref="Trigger"/> cancels the one before it. The search boxes on Environments and
/// Upgrades filter through it so the table redraws once a word is typed rather than once
/// per keystroke (#981). The action runs off the renderer's thread, so a component wraps
/// its body in <c>InvokeAsync</c>. A zero delay runs the action straight away, which is
/// what the page tests use instead of sleeping; the wait itself runs on the clock it
/// is given, so its own tests can move time rather than wait for it.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public Debouncer(TimeSpan delay, TimeProvider? clock = null)
    {
        _delay = delay;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Schedules <paramref name="action"/>, dropping any call still waiting.</summary>
    public void Trigger(Func<Task> action)
    {
        if (_disposed) return;
        Cancel();
        var pending = _pending = new CancellationTokenSource();
        _ = RunAsync(action, pending.Token);
    }

    /// <summary>Drops the call still waiting, if there is one.</summary>
    public void Cancel()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel();
    }

    private async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        try
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, _clock, ct);
            if (ct.IsCancellationRequested) return;
            await action();
        }
        // Superseded by a newer keystroke, or the page closed while it waited.
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
