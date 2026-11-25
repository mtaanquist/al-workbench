using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Hosted service that prunes old <c>login_attempts</c> rows so the table
/// doesn't grow unbounded. The rate-limiter only ever reads a ~15-minute
/// window (see <see cref="Account.AuthService"/>), so anything older is dead
/// weight; <see cref="RetentionWindow"/> keeps a generous forensic buffer past
/// that. The delete is a single set-based <c>ExecuteDeleteAsync</c> over the
/// leading-<c>timestamp</c> index added in #403.
///
/// <para>
/// Modelled on <see cref="UsageSnapshotScheduler"/>: polls every minute, ticks
/// the heartbeat each poll, and fires once <see cref="RunInterval"/> has elapsed
/// since the last success. Opt out with <c>DISABLE_LOGIN_ATTEMPT_PRUNE_SCHEDULER=1</c>.
/// </para>
/// </summary>
public sealed class LoginAttemptPruneScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(6);

    /// <summary>How long a login attempt is kept before the sweep removes it.</summary>
    public static readonly TimeSpan RetentionWindow = TimeSpan.FromDays(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<LoginAttemptPruneScheduler> _logger;
    private DateTimeOffset? _lastRunUtc;

    public LoginAttemptPruneScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<LoginAttemptPruneScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build it by hand keep compiling; production DI
        // always supplies the singleton, so the base skips its ticks during a restore.
        Operations.MaintenanceModeState? maintenance = null)
        : base(logger, heartbeats, nameof(LoginAttemptPruneScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_LOGIN_ATTEMPT_PRUNE_SCHEDULER",
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

        var cutoff = now.UtcDateTime - RetentionWindow;
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deleted = await db.LoginAttempts
            .Where(a => a.Timestamp < cutoff)
            .ExecuteDeleteAsync(ct);

        _lastRunUtc = now;
        if (deleted > 0)
        {
            _logger.LogInformation("Pruned {Count} login attempt(s) older than {Cutoff:o}.", deleted, cutoff);
        }
    }
}
