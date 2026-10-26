using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Once a day, refreshes every module copied from a GitHub repository (#1415).
/// A push to the module's branch already refreshes it straight away when the
/// GitHub App is installed on the repository; this pass is for public
/// repositories outside the connected organisation, which send no pushes, and a
/// safety net for a push that never arrived. Modules pinned to a commit are
/// skipped by <see cref="ModuleSourceService.RefreshAllAsync"/>.
///
/// <para>Same shape as <see cref="GitHub.DependencyDriftScheduler"/>: the
/// organisations table carries no tenant filter, so listing it needs no bypass,
/// and each organisation's modules are refreshed inside its own
/// <see cref="AmbientOrganizationScope"/>. Opt out with
/// <c>DISABLE_MODULE_SOURCE_REFRESH_SCHEDULER=1</c>.</para>
/// </summary>
public sealed class ModuleSourceRefreshScheduler : PolledScheduler
{
    /// <summary>
    /// The UTC hour the daily pass runs in: after the 01:00 preview checks, clear
    /// of the 05:00 dependency drift pass.
    /// </summary>
    internal const int SweepHourUtc = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ModuleSourceRefreshScheduler> _logger;

    private DateOnly? _lastSweptUtcDate;

    public ModuleSourceRefreshScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<ModuleSourceRefreshScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // One commit lookup per module, and a download of a few megabytes for the
        // ones that moved: half an hour is a generous ceiling.
        : base(logger, heartbeats, nameof(ModuleSourceRefreshScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_MODULE_SOURCE_REFRESH_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Whether a poll at <paramref name="nowUtc"/> should sweep: inside the sweep hour and not yet done today.</summary>
    internal static bool IsDue(DateTime nowUtc, DateOnly? lastSweptUtcDate) =>
        nowUtc.Hour == SweepHourUtc && lastSweptUtcDate != DateOnly.FromDateTime(nowUtc);

    protected override async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        if (!IsDue(nowUtc, _lastSweptUtcDate)) return;

        await SweepAsync(ct).ConfigureAwait(false);
        _lastSweptUtcDate = DateOnly.FromDateTime(nowUtc);
    }

    /// <summary>
    /// One pass over every active organisation. Internal so a test can drive it
    /// directly against a seeded database. Returns how many modules took a new copy.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        var orgs = await SweptOrganizations.ListAsync(_services, ct).ConfigureAwait(false);

        var updated = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            // Each organisation restarts the active clock: the ceiling bounds one
            // organisation's pass, not the whole deployment's.
            Heartbeat.BeginActive();
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                var sources = scope.ServiceProvider.GetRequiredService<ModuleSourceService>();
                updated += await sources.RefreshAllAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "ModuleSourceRefreshScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        if (updated > 0)
        {
            _logger.LogInformation("ModuleSourceRefreshScheduler updated {Count} module(s) from their repositories.", updated);
        }
        return updated;
    }
}
