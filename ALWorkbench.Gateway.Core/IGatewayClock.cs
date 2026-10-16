using System.Diagnostics;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The gateway's sense of time: the wall clock (for token times and log lines), a
/// monotonic stopwatch (for counting down a command's seconds left, which must not
/// jump when someone corrects the wall clock) and waiting. One seam for all three so
/// tests can run the loop's back-off and heartbeat schedule without real waits.
/// </summary>
public interface IGatewayClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>A monotonic reading to pass to <see cref="Elapsed"/> later.</summary>
    long Timestamp();

    /// <summary>How long has passed since <paramref name="timestamp"/>.</summary>
    TimeSpan Elapsed(long timestamp);

    Task Delay(TimeSpan delay, CancellationToken ct);
}

/// <summary>The real clock.</summary>
public sealed class SystemGatewayClock : IGatewayClock
{
    public static readonly SystemGatewayClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long Timestamp() => Stopwatch.GetTimestamp();

    public TimeSpan Elapsed(long timestamp) => Stopwatch.GetElapsedTime(timestamp);

    public Task Delay(TimeSpan delay, CancellationToken ct) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, ct);
}
