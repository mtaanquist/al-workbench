using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// Writes the gateway's log to one plain text file per day
/// (<c>gateway-20261014.log</c>) in the log folder, and removes files older than
/// <see cref="RetainDays"/> days. This is the log customer IT reads beside the
/// Windows event log: every command, who asked for it, and how it ended. Files are
/// opened for append with read sharing, so they can be read while the service runs.
/// </summary>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    public const int RetainDays = 30;

    private readonly string _directory;
    private readonly IGatewayClock _clock;
    private readonly LogLevel _minimum;
    private readonly Lock _lock = new();
    private string? _currentDay;

    public RollingFileLoggerProvider(string directory, IGatewayClock clock, LogLevel minimum = LogLevel.Information)
    {
        _directory = directory;
        _clock = clock;
        _minimum = minimum;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this);

    public void Dispose()
    {
    }

    /// <summary>The file a line written now goes to.</summary>
    public string CurrentFile => Path.Combine(_directory, FileNameFor(_clock.UtcNow));

    private static string FileNameFor(DateTimeOffset at) =>
        "gateway-" + at.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    private void Write(LogLevel level, string message, Exception? exception)
    {
        var now = _clock.UtcNow;
        var text = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture))
            .Append(' ').Append(LevelWord(level)).Append(' ')
            .Append(message.ReplaceLineEndings(" "));
        if (exception is not null) text.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message.ReplaceLineEndings(" "));
        text.Append(Environment.NewLine);

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var day = FileNameFor(now);
                if (day != _currentDay)
                {
                    _currentDay = day;
                    Prune(now);
                }
                using var stream = new FileStream(Path.Combine(_directory, day), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                stream.Write(Encoding.UTF8.GetBytes(text.ToString()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A log that cannot be written must not stop the gateway; the event log still has it.
            }
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var cutoff = FileNameFor(now.AddDays(-RetainDays));
        foreach (var file in Directory.EnumerateFiles(_directory, "gateway-*.log"))
        {
            // The names sort by date, so an ordinal comparison is a date comparison.
            if (string.CompareOrdinal(Path.GetFileName(file), cutoff) < 0) File.Delete(file);
        }
    }

    private static string LevelWord(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly RollingFileLoggerProvider _provider;

        public FileLogger(RollingFileLoggerProvider provider)
        {
            _provider = provider;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _provider._minimum;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            _provider.Write(logLevel, formatter(state, exception), exception);
        }
    }
}
