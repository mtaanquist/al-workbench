using System.Diagnostics;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Hosted service that runs <c>VACUUM</c> against the Object Explorer
/// content tables once a day. A hard-deleted Release cascades through
/// <c>oe_module_files</c> and friends and leaves millions of dead tuples;
/// a plain <c>VACUUM</c> marks their space reusable and cleans the indexes
/// (the trigram GIN indexes included) so later imports fill it instead of
/// growing the files. It does not shrink the files - only empty pages at
/// the end of a table are given back, and only <c>VACUUM FULL</c> rewrites
/// the heap. Operators used to be told to run it manually — this scheduler
/// does it for them.
///
/// <para>
/// Modelled after <see cref="BackupScheduler"/>: polls every minute,
/// tracks the last run in memory (cheap, and missing a day on app restart
/// is harmless — autovacuum still keeps the table queryable), and fires once
/// the day's slot has passed since the last run. A sweep that fails also
/// counts as the day's run: retrying it every minute would start the same
/// long statement again all day, cancelling autovacuum on that table each
/// time it waits for the lock (#1632).
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

    /// <summary>
    /// Command timeout for each <c>VACUUM</c>. Npgsql's 30-second default is far
    /// too short once a hard delete has left a large table full of dead tuples:
    /// the GIN cleanup reads the whole trigram index. Thirty minutes keeps the
    /// whole sweep inside the one-hour active ceiling, the way the release
    /// delete and wipe lift theirs (<see cref="ReleaseManagementService"/>).
    /// </summary>
    internal static readonly TimeSpan VacuumCommandTimeout = TimeSpan.FromMinutes(30);

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
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build it by hand keep compiling; production DI
        // always supplies the singleton, so the base skips its ticks during a restore.
        Operations.MaintenanceModeState? maintenance = null)
        // Same shape as BackupScheduler: poll every minute, stale at 5; a
        // single VACUUM sweep is short, an hour ceiling is plenty.
        : base(logger, heartbeats, nameof(ObjectExplorerVacuumScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromHours(1),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_OE_VACUUM_SCHEDULER",
            maintenance: maintenance)
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
        db.Database.SetCommandTimeout(VacuumCommandTimeout);

        try
        {
            foreach (var sql in VacuumStatements)
            {
                // VACUUM can't run inside a transaction. Npgsql's
                // ExecuteSqlRawAsync without an ambient transaction issues
                // a one-shot statement, which is what we want.
                await db.Database.ExecuteSqlRawAsync(sql, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Counted as the day's run: the next try is tomorrow's slot, not the
            // next poll (see the class comment).
            _lastRunUtc = now;
            _logger.LogWarning(ex,
                "VACUUM of the oe_* tables failed after {ElapsedMs}ms; trying again at tomorrow's slot.",
                sw.ElapsedMilliseconds);
            return;
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
