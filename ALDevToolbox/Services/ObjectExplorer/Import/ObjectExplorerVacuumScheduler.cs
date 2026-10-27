using System.Diagnostics;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Hosted service that runs <c>VACUUM</c> against the Object Explorer
/// content tables once a day. Postgres autovacuum reclaims tuple slots but
/// not on-disk file size; after a hard-deleted Release cascades through
/// <c>oe_module_files</c> and friends, the on-disk footprint stays high
/// until a plain <c>VACUUM</c> rewrites the heap. Operators used to be
/// told to run it manually — this scheduler does it for them.
///
/// <para>
/// Modelled after <see cref="BackupScheduler"/>: polls every minute,
/// tracks the last successful run in memory (cheap, and missing a day on
/// app restart is harmless — autovacuum still keeps the table queryable),
/// and fires once <see cref="DailyRunUtc"/> has elapsed since the last
/// success.
/// </para>
///
/// <para>
/// The default time is 03:00 UTC: late enough that European nightly
/// imports have settled, early enough not to clash with US-business-hours
/// traffic. Configurable via <c>OE_VACUUM_HOUR_UTC</c> in case a
/// deployment needs to shift it.
/// </para>
/// </summary>
public sealed class ObjectExplorerVacuumScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    // Tables touched by the cascade from a hard-deleted Release. Listed
    // explicitly so VACUUM only rewrites the heaps that actually accrue
    // bloat from this app — autovacuum keeps everything else honest.
    // Each entry is the full SQL we'll execute so EF's "no interpolated
    // SQL" analyser stays happy without disabling the warning.
    private static readonly string[] VacuumStatements = new[]
    {
        "VACUUM oe_module_files;",
        "VACUUM oe_module_objects;",
        "VACUUM oe_module_symbols;",
        "VACUUM oe_modules;",
    };

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ObjectExplorerVacuumScheduler> _logger;
    private readonly int _hourUtc;
    private DateTimeOffset? _lastRunUtc;

    public ObjectExplorerVacuumScheduler(
        IServiceProvider services,
        TimeProvider clock,
        IConfiguration config,
        ILogger<ObjectExplorerVacuumScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // Same shape as BackupScheduler: poll every minute, stale at 5; a
        // single VACUUM sweep is short, an hour ceiling is plenty.
        : base(logger, heartbeats, nameof(ObjectExplorerVacuumScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromHours(1),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_OE_VACUUM_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _hourUtc = ParseHour(config["OE_VACUUM_HOUR_UTC"]);
    }

    private static int ParseHour(string? raw)
    {
        if (int.TryParse(raw, out var parsed) && parsed is >= 0 and <= 23) return parsed;
        return 3;
    }

    protected override Task TickAsync(CancellationToken ct) => TickOnceAsync(ct);

    /// <summary>
    /// One poll: vacuums when the day's slot has come and not been run yet.
    /// Internal so a test can drive it with a fake clock, without the poll loop.
    /// </summary>
    internal async Task TickOnceAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (!ShouldRun(now)) return;

        var sw = Stopwatch.StartNew();
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var sql in VacuumStatements)
        {
            // VACUUM can't run inside a transaction. Npgsql's
            // ExecuteSqlRawAsync without an ambient transaction issues
            // a one-shot statement, which is what we want.
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        }

        _lastRunUtc = now;
        _logger.LogInformation(
            "VACUUM swept {TableCount} oe_* table(s) in {ElapsedMs}ms.",
            VacuumStatements.Length, sw.ElapsedMilliseconds);
    }

    private bool ShouldRun(DateTimeOffset now)
    {
        // Compute the most recent scheduled time at _hourUtc. If we
        // haven't run since that timestamp, it's our window. Re-checking
        // every minute means we always catch up after a missed slot
        // (host was down at the scheduled hour) on the next tick.
        var todayScheduled = new DateTimeOffset(
            now.Year, now.Month, now.Day, _hourUtc, 0, 0, TimeSpan.Zero);
        var scheduled = now >= todayScheduled
            ? todayScheduled
            : todayScheduled.AddDays(-1);

        return _lastRunUtc is null || _lastRunUtc.Value < scheduled;
    }
}
