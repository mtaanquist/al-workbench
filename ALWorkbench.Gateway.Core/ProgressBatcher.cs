using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// Collects a command's log lines and sends them in batches: one post every
/// <see cref="FlushInterval"/> at most, at most <see cref="MaxLinesPerPost"/> lines
/// each, in order. A verb can then report as chattily as it likes and the gateway
/// stays far inside the workbench's per-address rate limit. See
/// <c>.design/on-prem-gateway.md</c>, "Commands" (the rate-limit paragraph).
///
/// <para>Each batch carries a number (1, 2, ...). A post that does not get through is
/// sent again unchanged, same lines and same number, before anything newer, so when
/// only the answer was lost the workbench recognises the number and does not append
/// the lines twice. When AL Workbench answers that the command is no longer this
/// gateway's (409), <see cref="Closed"/> is cancelled so the verb can stop, and the
/// rest is dropped.</para>
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

    private readonly Func<IReadOnlyList<string>, long, CancellationToken, Task> _post;
    private readonly IGatewayClock _clock;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly LinkedList<string> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _leftOut;
    private long _sequence;
    private Batch? _unsent;

    /// <param name="post">Sends one batch: its lines and its number.</param>
    public ProgressBatcher(Func<IReadOnlyList<string>, long, CancellationToken, Task> post, IGatewayClock clock, ILogger logger)
    {
        _post = post;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Cancelled once AL Workbench says the command is no longer this gateway's.</summary>
    public CancellationToken Closed => _closed.Token;

    /// <summary>Lines waiting to be sent, including a batch that did not get through.</summary>
    public int Pending
    {
        get
        {
            lock (_lock) return _pending.Count + (_unsent?.Lines.Count ?? 0);
        }
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

    /// <summary>Sends one batch. False when it did not get through and is kept, unchanged, for the next attempt.</summary>
    public async Task<bool> FlushOnceAsync(CancellationToken ct)
    {
        var batch = Next();
        if (batch is null) return true;
        try
        {
            await _post(batch.Lines, batch.Sequence, ct).ConfigureAwait(false);
            lock (_lock) _unsent = null;
            return true;
        }
        catch (CommandClosedException ex)
        {
            _logger.LogWarning("AL Workbench is no longer expecting progress on this command: {Reason}", ex.Message);
            lock (_lock)
            {
                _pending.Clear();
                _unsent = null;
            }
            _closed.Cancel();
            return true;
        }
        catch (Exception ex) when (ex is WorkbenchUnreachableException or WorkbenchRefusedException or GatewayNotApprovedException
                                   or GatewayCredentialRefusedException or GatewayOutdatedException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Kept as _unsent: it may have arrived with only the answer lost, so it goes
            // again exactly as it was, under the same number.
            _logger.LogDebug("Progress batch {Sequence} not confirmed yet: {Reason}", batch.Sequence, ex.Message);
            return false;
        }
    }

    public void Dispose() => _closed.Dispose();

    private bool HasWork()
    {
        lock (_lock) return _unsent is not null || _pending.Count > 0 || _leftOut > 0;
    }

    /// <summary>The batch that did not get through last time, else a new one from the waiting lines.</summary>
    private Batch? Next()
    {
        lock (_lock)
        {
            if (_unsent is not null) return _unsent;
            var lines = new List<string>(Math.Min(_pending.Count + 1, MaxLinesPerPost));
            while (lines.Count < MaxLinesPerPost && _pending.First is { } node)
            {
                lines.Add(node.Value);
                _pending.RemoveFirst();
            }
            if (_leftOut > 0 && lines.Count < MaxLinesPerPost && _pending.Count == 0)
            {
                lines.Add($"... {_leftOut} more lines were left out because they came faster than they could be sent.");
                _leftOut = 0;
            }
            if (lines.Count == 0) return null;
            _unsent = new Batch(++_sequence, lines);
            return _unsent;
        }
    }

    private sealed record Batch(long Sequence, IReadOnlyList<string> Lines);
}
