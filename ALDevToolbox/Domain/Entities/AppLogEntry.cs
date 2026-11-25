namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One stored log line, kept so a SiteAdmin can read the app's logs on
/// <c>/site-admin/logs</c> instead of on the host. Written in batches by
/// <see cref="ALDevToolbox.Services.Logging.StoredLogWriter"/>, never through EF, and
/// deleted once older than the retention on <see cref="SystemSettings.LogRetentionDays"/>.
/// Site-wide, so it carries no organisation and no query filter; only the SiteAdmin
/// console reads it. Backups leave its rows out (see <c>BackupService</c>).
/// </summary>
public class AppLogEntry
{
    public long Id { get; set; }

    public DateTime Timestamp { get; set; }

    public Microsoft.Extensions.Logging.LogLevel Level { get; set; }

    /// <summary>The logger's category: the full name of the class that wrote the line.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// The background worker that was running when the line was written, by the name
    /// it reports its heartbeat under (<c>ProjectBuildWorker 3</c>). Null for a web
    /// request or startup.
    /// </summary>
    public string? Worker { get; set; }

    public string Message { get; set; } = string.Empty;

    /// <summary>The exception's full text, stack trace included, when the line carried one.</summary>
    public string? Exception { get; set; }
}
