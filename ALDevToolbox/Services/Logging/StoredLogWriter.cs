using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using Npgsql;
using NpgsqlTypes;

namespace ALDevToolbox.Services.Logging;

/// <summary>
/// Copies the lines queued in <see cref="StoredLogBuffer"/> into <c>app_log_entries</c>
/// every couple of seconds, and once an hour deletes the lines older than the
/// retention setting. See <c>.design/deployment.md</c>, "Stored logs".
///
/// <para>
/// It writes with a binary <c>COPY</c> on a connection of its own rather than through
/// EF: a busy minute can produce thousands of lines, and a batch should cost one round
/// trip. Its data source has no logger factory, so the database driver's own logging
/// cannot feed lines back into the queue it is draining.
/// </para>
///
/// <para>
/// On the <see cref="PolledScheduler"/> base, so it skips its polls while a restore
/// runs (lines wait in the queue, or are dropped once it is full) and its heartbeat
/// shows on the workers page. Opt out with <c>DISABLE_STORED_LOGS=1</c>; lines are
/// then never written, the queue fills, and further lines are dropped.
/// </para>
/// </summary>
public sealed class StoredLogWriter : PolledScheduler, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <summary>Lines per <c>COPY</c>.</summary>
    internal const int BatchSize = 1_000;

    /// <summary>Lines per poll, so one poll cannot hold the loop for long behind a backlog.</summary>
    internal const int MaxLinesPerPoll = 20_000;

    /// <summary>Rows per delete statement, so a large sweep never holds one long lock.</summary>
    internal int DeleteBatchSize { get; init; } = 20_000;

    /// <summary>
    /// Delete statements per poll. A sweep that has millions of rows to catch up on (the
    /// retention was just lowered) carries on over the next polls rather than holding
    /// this one, so lines keep being written and the heartbeat never reads as stuck.
    /// </summary>
    internal int DeleteSlicesPerPoll { get; init; } = 5;

    /// <summary>
    /// How many polls in a row a batch may fail before it is dropped. Long enough to ride
    /// out a database restart (about a minute); short enough that a batch the database
    /// will never accept does not hold everything behind it for long.
    /// </summary>
    internal const int MaxBatchAttempts = 30;

    private readonly StoredLogBuffer _buffer;
    private readonly StartupReadinessState? _readiness;
    private readonly TimeProvider _clock;
    private readonly ILogger<StoredLogWriter> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private DateTimeOffset? _lastSweepUtc;
    private bool _sweeping;

    /// <summary>A batch taken off the queue whose write failed, tried again on the next poll.</summary>
    private List<StoredLogLine>? _retry;
    private int _retryAttempts;

    public StoredLogWriter(
        StoredLogBuffer buffer,
        IConfiguration configuration,
        TimeProvider clock,
        ILogger<StoredLogWriter> logger,
        WorkerHeartbeatRegistry heartbeats,
        StartupReadinessState? readiness = null,
        MaintenanceModeState? maintenance = null)
        : base(logger, heartbeats, nameof(StoredLogWriter),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(1),
            disableEnvVar: "DISABLE_STORED_LOGS",
            maintenance: maintenance)
    {
        _buffer = buffer;
        _readiness = readiness;
        _clock = clock;
        _logger = logger;
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required for StoredLogWriter.");
        _dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        // The table exists once migrations have run; until then lines wait in the queue.
        if (_readiness is { IsReady: false }) return;
        await FlushAsync(ct);
        var now = _clock.GetUtcNow();
        if (_sweeping || _lastSweepUtc is null || now - _lastSweepUtc.Value >= SweepInterval)
        {
            var (_, done) = await SweepAsync(ct);
            _sweeping = !done;
            if (done) _lastSweepUtc = now;
        }
    }

    /// <summary>
    /// Writes what is queued, a batch at a time. A batch that fails is kept and tried
    /// again first on the next poll, so a database restart loses nothing the queue can
    /// hold meanwhile; lines are only dropped once the queue is full, or after
    /// <see cref="MaxBatchAttempts"/> failed polls for the same batch.
    /// </summary>
    internal async Task<int> FlushAsync(CancellationToken ct)
    {
        var written = 0;
        var taken = 0;
        while (taken < MaxLinesPerPoll)
        {
            var batch = _retry;
            if (batch is null)
            {
                batch = new List<StoredLogLine>(BatchSize);
                var n = _buffer.Take(batch, BatchSize);
                if (n == 0) break;
                taken += n;
                if (batch.Count == 0) continue;
            }
            else
            {
                taken += batch.Count;
            }
            try
            {
                await CopyAsync(batch, ct);
                written += batch.Count;
                _retry = null;
                _retryAttempts = 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _retry = batch;
                if (++_retryAttempts >= MaxBatchAttempts)
                {
                    _buffer.CountDropped(batch.Count);
                    _retry = null;
                    _retryAttempts = 0;
                    _logger.LogWarning(ex, "Gave up storing {Count} log line(s); they are still in the container log.", batch.Count);
                }
                else if (_retryAttempts == 1)
                {
                    _logger.LogWarning(ex, "Could not store {Count} log line(s); trying again on the next poll.", batch.Count);
                }
                break;
            }
            catch (OperationCanceledException)
            {
                // Shutting down mid-write: keep the batch for the final flush.
                _retry = batch;
                throw;
            }
        }
        return written;
    }

    private async Task CopyAsync(List<StoredLogLine> batch, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var writer = await conn.BeginBinaryImportAsync(
            "COPY app_log_entries (timestamp, level, category, worker, message, exception) FROM STDIN (FORMAT BINARY)", ct);
        foreach (var line in batch)
        {
            await writer.StartRowAsync(ct);
            await writer.WriteAsync(line.Timestamp, NpgsqlDbType.TimestampTz, ct);
            await writer.WriteAsync((int)line.Level, NpgsqlDbType.Integer, ct);
            await writer.WriteAsync(Clean(line.Category), NpgsqlDbType.Text, ct);
            if (line.Worker is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(Clean(line.Worker), NpgsqlDbType.Text, ct);
            await writer.WriteAsync(Clean(line.Message), NpgsqlDbType.Text, ct);
            if (line.Exception is null) await writer.WriteNullAsync(ct);
            else await writer.WriteAsync(Clean(line.Exception), NpgsqlDbType.Text, ct);
        }
        await writer.CompleteAsync(ct);
    }

    /// <summary>PostgreSQL text cannot hold a NUL character, and one would fail the whole batch.</summary>
    private static string Clean(string text) => text.Contains('\0') ? text.Replace("\0", string.Empty) : text;

    /// <summary>
    /// Deletes lines older than the retention setting, a slice at a time, for at most
    /// <see cref="DeleteSlicesPerPoll"/> slices. Done is true once a slice came back short,
    /// meaning nothing past the retention is left.
    /// </summary>
    internal async Task<(int Deleted, bool Done)> SweepAsync(CancellationToken ct)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime.AddDays(-_buffer.RetentionDays);
        var deleted = 0;
        var done = false;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        for (var slice = 0; slice < DeleteSlicesPerPoll; slice++)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM app_log_entries
                WHERE id IN (SELECT id FROM app_log_entries WHERE timestamp < $1 LIMIT $2)
                """;
            cmd.Parameters.Add(new NpgsqlParameter { Value = cutoff, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new NpgsqlParameter { Value = DeleteBatchSize });
            var n = await cmd.ExecuteNonQueryAsync(ct);
            deleted += n;
            if (n < DeleteBatchSize)
            {
                done = true;
                break;
            }
        }
        if (deleted > 0)
        {
            // Not stored (this class's own category), but the container log has it.
            _logger.LogInformation("Deleted {Count} stored log line(s) older than {Cutoff:o}.", deleted, cutoff);
        }
        return (deleted, done);
    }

    /// <summary>
    /// One last write on the way down, so the lines that explain a shutdown are kept.
    /// Bounded: a database that is already gone must not hold the shutdown up.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // Not into a database a restore is replacing, nor before migrations made the table.
        if (_readiness is { IsReady: false } || Maintenance is { IsActive: true }
            || Environment.GetEnvironmentVariable("DISABLE_STORED_LOGS") == "1") return;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try { await FlushAsync(timeout.Token); }
        catch (OperationCanceledException) { }
    }

    public override void Dispose()
    {
        _dataSource.Dispose();
        base.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync();
        base.Dispose();
    }
}
