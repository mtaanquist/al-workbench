using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// Collects a command's log lines and sends them in batches: one post every
/// <see cref="FlushInterval"/> at most, at most <see cref="MaxLinesPerPost"/> lines
/// each, in order. A verb can then report as chattily as it likes and the gateway
/// stays far inside the workbench's per-address rate limit. See
/// <c>.design/on-prem-gateway.md</c>, "Commands" (the rate-limit paragraph).
///
/// <para>A post that does not get through keeps its lines for the next one. When AL
/// Workbench answers that the command is no longer this gateway's (409),
/// <see cref="Closed"/> is cancelled so the verb can stop, and the rest is dropped.</para>
/// </summary>
public sealed class ProgressBatcher : IDisposable
{
    /// <summary>The workbench refuses more lines than this in one post (<c>GatewayCommandService.MaxProgressLines</c>).</summary>
    public const int MaxLinesPerPost = 200;

    /// <summary>Longer lines are cut, so one runaway line cannot make a post too large.</summary>
    public const int MaxLineLength = 2000;

    /// <summary>Lines held while waiting to be sent; beyond it new lines are counted and left out.</summary>
    public const int MaxBufferedLines = 10_000;

    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(3);

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task> _post;
    private readonly IGatewayClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly LinkedList<string> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _leftOut;

    public ProgressBatcher(Func<IReadOnlyList<string>, CancellationToken, Task> post, IGatewayClock clock, ILogger logger)
    {
        _post = post;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Cancelled once AL Workbench says the command is no longer this gateway's.</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Lines waiting to be sent.</summary>
    public int Pending
    {
        get { lock (_lock) return _pending.Count; }
    }

    public void Add(string line)
    {
        line ??= string.Empty;
        if (line.Length > MaxLineLength) line = line[..MaxLineLength] + "...";
        lock (_lock)
        {
            if (_closed.IsCancellationRequested) return;
            if (_pending.Count >= MaxBufferedLines)
            {
                _leftOut++;
                return;
            }
            _pending.AddLast(line);
        }
    }

    /// <summary>
    /// Sends a batch every <see cref="FlushInterval"/> until <paramref name="stop"/> is
    /// cancelled (the verb finished). Posts themselves run under <paramref name="ct"/>,
    /// so stopping never cuts a post in half.
    /// </summary>
    public async Task RunAsync(CancellationToken stop, CancellationToken ct)
    {
        while (!stop.IsCancellationRequested && !_closed.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(FlushInterval, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            await FlushOnceAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends what is left after the verb finished, still one post per
    /// <see cref="FlushInterval"/>. Gives up after a few failed attempts rather than
    /// hold up the result: the result matters more than the last lines.
    /// </summary>
    public async Task DrainAsync(CancellationToken ct)
    {
        var failures = 0;
        var first = true;
        while (HasWork() && failures < 3 && !_closed.IsCancellationRequested)
        {
            if (!first) await _clock.Delay(FlushInterval, ct).ConfigureAwait(false);
            first = false;
            if (!await FlushOnceAsync(ct).ConfigureAwait(false)) failures++;
        }
    }

    /// <summary>Sends one batch. False when it did not get through and the lines were kept.</summary>
    public async Task<bool> FlushOnceAsync(CancellationToken ct)
    {
        var (batch, leftOut) = Take();
        if (batch.Count == 0) return true;
        try
        {
            await _post(batch, ct).ConfigureAwait(false);
            return true;
        }
        catch (CommandClosedException ex)
        {
            _logger.LogWarning("AL Workbench is no longer expecting progress on this command: {Reason}", ex.Message);
            lock (_lock) _pending.Clear();
            _closed.Cancel();
            return true;
        }
        catch (Exception ex) when (ex is WorkbenchUnreachableException or WorkbenchRefusedException or GatewayNotApprovedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogDebug("Progress lines not sent yet: {Reason}", ex.Message);
            PutBack(batch, leftOut);
            return false;
        }
    }

    public void Dispose() => _closed.Dispose();

    private bool HasWork()
    {
        lock (_lock) return _pending.Count > 0 || _leftOut > 0;
    }

    /// <summary>The next batch, and how many left-out lines its closing note counts (0 when it has none).</summary>
    private (List<string> Batch, int LeftOut) Take()
    {
        lock (_lock)
        {
            var batch = new List<string>(Math.Min(_pending.Count + 1, MaxLinesPerPost));
            while (batch.Count < MaxLinesPerPost && _pending.First is { } node)
            {
                batch.Add(node.Value);
                _pending.RemoveFirst();
            }
            var leftOut = 0;
            if (_leftOut > 0 && batch.Count < MaxLinesPerPost && _pending.Count == 0)
            {
                leftOut = _leftOut;
                batch.Add($"... {leftOut} more lines were left out because they came faster than they could be sent.");
                _leftOut = 0;
            }
            return (batch, leftOut);
        }
    }

    private void PutBack(List<string> batch, int leftOut)
    {
        lock (_lock)
        {
            // The closing note is written afresh next time, with the count it then has.
            var lines = leftOut > 0 ? batch.Count - 1 : batch.Count;
            for (var i = lines - 1; i >= 0; i--)
            {
                _pending.AddFirst(batch[i]);
            }
            _leftOut += leftOut;
        }
    }
}
