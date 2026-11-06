namespace ALDevToolbox.Services.Logging;

/// <summary>
/// Logger provider that copies each line into <see cref="StoredLogBuffer"/>, next to
/// the console provider, so the line can also be read on <c>/site-admin/logs</c>. It
/// only formats and queues; <see cref="StoredLogWriter"/> does the database work off
/// the logging thread.
/// </summary>
public sealed class StoredLogProvider : ILoggerProvider
{
    /// <summary>
    /// Categories never stored. The writer's own lines would feed back into the queue
    /// they describe: a failing insert logs a warning, which queues a line, which the
    /// next insert fails to write.
    /// </summary>
    internal const string OwnNamespace = "ALDevToolbox.Services.Logging.";

    /// <summary>
    /// Categories stored from Warning up only. The HTTP client writes four Information
    /// lines for every request it sends, so a build fetching symbol packages would fill
    /// the table with them while saying nothing a failure line does not.
    /// </summary>
    internal const string HttpClientNamespace = "System.Net.Http.HttpClient.";

    private readonly StoredLogBuffer _buffer;
    private readonly TimeProvider _clock;

    public StoredLogProvider(StoredLogBuffer buffer, TimeProvider clock)
    {
        _buffer = buffer;
        _clock = clock;
    }

    public ILogger CreateLogger(string categoryName) =>
        categoryName.StartsWith(OwnNamespace, StringComparison.Ordinal)
            ? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance
            : new StoredLogger(categoryName, _buffer, _clock,
                floor: categoryName.StartsWith(HttpClientNamespace, StringComparison.Ordinal)
                    ? LogLevel.Warning : LogLevel.Trace);

    public void Dispose() { }

    private sealed class StoredLogger : ILogger
    {
        private readonly string _category;
        private readonly StoredLogBuffer _buffer;
        private readonly TimeProvider _clock;
        private readonly LogLevel _floor;

        public StoredLogger(string category, StoredLogBuffer buffer, TimeProvider clock, LogLevel floor)
        {
            _category = category;
            _buffer = buffer;
            _clock = clock;
            _floor = floor;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= _floor && _buffer.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            string message;
            string? error;
            try
            {
                message = formatter(state, exception);
                error = exception?.ToString();
            }
            catch (Exception)
            {
                // A formatter that throws must not take the caller down with it; the
                // console provider still has the line.
                return;
            }
            _buffer.Add(new StoredLogLine(
                _clock.GetUtcNow().UtcDateTime,
                logLevel,
                _category,
                WorkerLogScope.Current,
                Cut(message, StoredLogBuffer.MaxMessageLength),
                error is null ? null : Cut(error, StoredLogBuffer.MaxExceptionLength)));
        }

        private static string Cut(string text, int max) =>
            text.Length <= max ? text : string.Concat(text.AsSpan(0, max), " [cut]");
    }
}
