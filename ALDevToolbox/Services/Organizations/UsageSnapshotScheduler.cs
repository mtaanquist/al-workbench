using System.Diagnostics;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Hosted service that refreshes the per-organisation storage snapshots in
/// <c>organization_usage_snapshots</c> every <see cref="RunInterval"/> (hourly).
/// Computing usage live means a sequential <c>COUNT(*)</c> over every tenanted
/// table — hundreds of milliseconds on a populated tenant — which used to run
/// on every authenticated navigation because the sidebar <c>StorageBar</c>
/// computed live. Moving the work to this background pass lets the bar (and the
/// SiteAdmin storage page) read a cached row instead.
///
/// <para>
/// Modelled after <see cref="ObjectExplorer.ObjectExplorerVacuumScheduler"/>:
/// polls every minute, ticks the heartbeat each poll, and fires once
/// <see cref="RunInterval"/> has elapsed since the last success. Polling (rather
/// than a single long <c>Task.Delay</c>) keeps the heartbeat fresh and means a
/// missed slot after a restart is caught on the next minute. The first pass
/// runs ~20s after startup so the figures are populated promptly without
/// racing the startup migrations.
/// </para>
///
/// <para>
/// Same opt-out shape as the other schedulers: set
/// <c>DISABLE_USAGE_SNAPSHOT_SCHEDULER=1</c> to keep the timer from starting
/// (tests, CI). The display surfaces degrade gracefully when no snapshot
/// exists yet — the bar simply hides.
/// </para>
/// </summary>
public sealed class UsageSnapshotScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    // Hourly, not quarter-hourly (#684). The sweep still counts rows in every
    // small tenanted table, and a capacity bar does not need 15-minute
    // freshness — the quota guard computes live (behind its own 60-second
    // cache) when a write actually has to be refused, so a stale snapshot
    // never lets an org over its limit.
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<UsageSnapshotScheduler> _logger;
    private DateTimeOffset? _lastRunUtc;

    public UsageSnapshotScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<UsageSnapshotScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build it by hand keep compiling; production DI
        // always supplies the singleton, so the base skips its ticks during a restore.
        Operations.MaintenanceModeState? maintenance = null)
        // Poll every minute, stale at 5 (same as the vacuum scheduler). A
        // single recompute sweep is short; a 10-minute active ceiling is plenty.
        : base(logger, heartbeats, nameof(UsageSnapshotScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_USAGE_SNAPSHOT_SCHEDULER",
            maintenance: maintenance)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastRunUtc is { } last && now - last < RunInterval) return;

        var sw = Stopwatch.StartNew();
        await using var scope = _services.CreateAsyncScope();
        var usage = scope.ServiceProvider.GetRequiredService<DatabaseUsageService>();
        await usage.RecomputeSnapshotsAsync(ct);

        _lastRunUtc = now;
        _logger.LogDebug("Usage snapshot recompute finished in {ElapsedMs}ms.", sw.ElapsedMilliseconds);
    }
}
