using System.Threading.Channels;

namespace ALDevToolbox.Services.Logging;

/// <summary>One log line on its way to <c>app_log_entries</c>.</summary>
public sealed record StoredLogLine(
    DateTime Timestamp,
    LogLevel Level,
    string Category,
    string? Worker,
    string Message,
    string? Exception);

/// <summary>
/// The in-memory queue between <see cref="StoredLogProvider"/>, which every logger
/// writes into, and <see cref="StoredLogWriter"/>, which copies it to the database in
/// batches. Also holds the two settings the hot path needs without a query: the least
/// severe level stored, and how many days to keep.
///
/// <para>
/// Bounded, and a full queue drops the line rather than waiting: logging must never
/// slow down or block the work it describes. The count of dropped lines is shown on
/// the logs page so a gap is visible rather than silent.
/// </para>
/// </summary>
public sealed class StoredLogBuffer
{
    /// <summary>Lines held while the database is behind, unreachable, or not migrated yet.</summary>
    public const int Capacity = 20_000;

    /// <summary>Longest message kept, in characters. Anything past it is cut.</summary>
    public const int MaxMessageLength = 8_000;

    /// <summary>Longest exception text kept, in characters.</summary>
    public const int MaxExceptionLength = 32_000;

    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 30;

    private readonly Channel<StoredLogLine> _channel = Channel.CreateBounded<StoredLogLine>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    private volatile int _minimumLevel = (int)LogLevel.Information;
    private volatile int _retentionDays = 7;
    private long _dropped;

    /// <summary>The least severe level stored. <see cref="LogLevel.None"/> stores nothing.</summary>
    public LogLevel MinimumLevel => (LogLevel)_minimumLevel;

    public int RetentionDays => _retentionDays;

    /// <summary>Lines dropped since the app started: the queue was full, or the database refused them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Lines waiting to be written.</summary>
    public int Pending => _channel.Reader.Count;

    /// <summary>Applies the site settings. Called at startup, after a restore and on save.</summary>
    public void Apply(LogLevel minimumLevel, int retentionDays)
    {
        _minimumLevel = (int)Normalise(minimumLevel);
        _retentionDays = Math.Clamp(retentionDays, MinRetentionDays, MaxRetentionDays);
    }

    /// <summary>True when a line at <paramref name="level"/> would be stored.</summary>
    public bool IsEnabled(LogLevel level)
    {
        var minimum = (LogLevel)_minimumLevel;
        return minimum != LogLevel.None && level != LogLevel.None && level >= minimum;
    }

    /// <summary>Queues a line; counts it as dropped when the queue is full.</summary>
    public void Add(StoredLogLine line)
    {
        if (!_channel.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped);
    }

    /// <summary>Counts lines taken off the queue that could not be written.</summary>
    public void CountDropped(int count) => Interlocked.Add(ref _dropped, count);

    /// <summary>
    /// Moves up to <paramref name="max"/> queued lines into <paramref name="into"/>,
    /// leaving out any below the level stored now: lines queued before the settings
    /// were read at startup, or before a SiteAdmin raised the level.
    /// </summary>
    public int Take(List<StoredLogLine> into, int max)
    {
        var taken = 0;
        while (taken < max && _channel.Reader.TryRead(out var line))
        {
            taken++;
            if (IsEnabled(line.Level)) into.Add(line);
        }
        return taken;
    }

    /// <summary>
    /// Only Information and up can be stored: the app's logging configuration drops
    /// Debug and Trace before any provider sees them, so offering them would promise
    /// lines that never arrive.
    /// </summary>
    public static LogLevel Normalise(LogLevel level) => level switch
    {
        LogLevel.None => LogLevel.None,
        <= LogLevel.Information => LogLevel.Information,
        LogLevel.Critical => LogLevel.Error,
        _ => level,
    };
}
