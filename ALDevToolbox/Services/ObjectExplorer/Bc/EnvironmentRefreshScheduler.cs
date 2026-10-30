using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Hosted service that offers every Business-Central-connected project to
/// <see cref="EnvironmentRefreshQueue"/> on each organisation's own schedule, so the
/// fleet view of "when does each customer get their next platform update?" is answered
/// from cached rows instead of a hundred live round trips. Mirrors
/// <see cref="DeliveryScheduler"/>: poll on a short interval, enumerate active orgs, and
/// do the per-org work inside that org's <see cref="AmbientOrganizationScope"/> so the EF
/// query filter behaves exactly as in a request. The <em>only</em> cross-org read is the
/// active-org enumeration, which needs no bypass because the organisations table carries
/// no tenant filter; the settings and project queries stay org-scoped.
///
/// <para>
/// How often is the organisation's choice (<see cref="EnvironmentRefreshInterval"/>),
/// once a day by default. Runs are laid out from <see cref="SweepHourUtc"/>, and every
/// choice divides a day, so whatever an organisation picks it still gets a run in that
/// quiet hour - the one the 05:00 automatic update pull requests read from. A sweep is
/// two admin-center calls and more per environment against every customer's tenant, so
/// a project Business Central has asked us to leave alone (a throttled read's
/// Retry-After) is skipped until that time has passed. Nothing here is fatal: an org or
/// a project that fails is logged and the sweep carries on, and a missed run costs only
/// staleness, which the mirrored fetched-at timestamps make visible. Opt out with
/// <c>DISABLE_ENVIRONMENT_REFRESH_SCHEDULER=1</c>. See <c>.design/saas-delivery.md</c>,
/// <c>.design/environment-updates.md</c> and issue #657.
/// </para>
/// </summary>
public sealed class EnvironmentRefreshScheduler : PolledScheduler
{
    /// <summary>The UTC hour every organisation's runs are laid out from - quiet for European working hours.</summary>
    internal const int SweepHourUtc = 3;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a run's slot opens a poll may still start it. An hour, as when the
    /// sweep was nightly only, or the interval itself when that is shorter: a slot that
    /// was missed entirely (the app was down) is not made up later in the day, the next
    /// one simply comes round.
    /// </summary>
    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly EnvironmentRefreshQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ILogger<EnvironmentRefreshScheduler> _logger;
    private readonly Operations.MaintenanceModeState? _maintenance;

    // The slot each org last ran in, so the five-minute poll runs each slot once rather
    // than on every poll inside it. Only TickAsync touches it, one poll at a time.
    private readonly Dictionary<int, DateTime> _lastSlotByOrg = new();

    /// <summary>
    /// How far past the hour the runs start. Every installation of this workbench - and
    /// a good deal else in the world - fires on the hour; a few random minutes keeps us
    /// out of that spike at Microsoft's sign-in and admin endpoints. Chosen once per
    /// process.
    /// </summary>
    internal static readonly TimeSpan MaxStartOffset = TimeSpan.FromMinutes(40);
    private readonly TimeSpan _startOffset;

    public EnvironmentRefreshScheduler(
        IServiceProvider services,
        EnvironmentRefreshQueue queue,
        TimeProvider clock,
        ILogger<EnvironmentRefreshScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so a scheduler built by hand keeps compiling; production DI
        // always supplies the singleton.
        Operations.MaintenanceModeState? maintenance = null)
        // Polls every 5 minutes; a sweep only enumerates and enqueues (the round trips
        // run on EnvironmentRefreshWorker), so a 10-minute active ceiling is ample.
        : base(logger, heartbeats, nameof(EnvironmentRefreshScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_ENVIRONMENT_REFRESH_SCHEDULER")
    {
        _services = services;
        _queue = queue;
        _clock = clock;
        _logger = logger;
        _maintenance = maintenance;
        _startOffset = TimeSpan.FromMinutes(Random.Shared.Next(0, (int)MaxStartOffset.TotalMinutes + 1));
    }

    /// <summary>
    /// The slot a poll at <paramref name="nowUtc"/> should run, or null when there is
    /// nothing to do. Slots are every <paramref name="interval"/> counted from
    /// <see cref="SweepHourUtc"/> plus <paramref name="startOffset"/>; one is due while it
    /// is less than <see cref="MaxCatchUp"/> (or the interval, if shorter) old and has not
    /// already run.
    /// </summary>
    internal static DateTime? DueSlot(DateTime nowUtc, TimeSpan interval, TimeSpan startOffset, DateTime? lastSlot)
    {
        var anchor = nowUtc.Date + TimeSpan.FromHours(SweepHourUtc) + startOffset;
        if (anchor > nowUtc) anchor -= TimeSpan.FromDays(1);
        var slot = anchor + TimeSpan.FromTicks((nowUtc - anchor).Ticks / interval.Ticks * interval.Ticks);
        if (slot == lastSlot) return null;
        var catchUp = interval < MaxCatchUp ? interval : MaxCatchUp;
        return nowUtc - slot < catchUp ? slot : null;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        // Not while a restore runs or a failed one holds the site closed (#1321). The
        // slot stays owed, so a later poll inside its catch-up still runs it.
        if (_maintenance?.IsActive == true) return;

        await SweepDueAsync(_clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One pass over every active org, enqueuing the BC-connected projects of each org
    /// whose slot is due at <paramref name="nowUtc"/>. Internal so a test can drive it
    /// directly against a seeded database without the <see cref="Task.Delay"/> loop.
    /// Returns how many projects were enqueued.
    /// </summary>
    internal async Task<int> SweepDueAsync(DateTime nowUtc, CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one sanctioned cross-org read: which orgs to sweep. Only pending
            // signups are skipped — like DeliveryScheduler, the system org is swept too,
            // since in single-tenant deployments it is the working org.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var enqueued = 0;
        var deferred = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            DateTime? slot = null;
            try
            {
                var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem);
                using var ambient = AmbientOrganizationScope.Enter(identity);
                await using var scope = _services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // Org-scoped by the query filter, and the predicate names the org in scope.
                var minutes = await db.OrganizationSettings.AsNoTracking()
                    .Where(s => s.OrganizationId == orgId)
                    .Select(s => (int?)s.EnvironmentRefreshIntervalMinutes)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                slot = DueSlot(nowUtc, EnvironmentRefreshInterval.Normalise(minutes), _startOffset,
                    _lastSlotByOrg.TryGetValue(orgId, out var last) ? last : null);
                if (slot is null) continue;

                var projectIds = await ResolveProjectIdsAsync(db, ct).ConfigureAwait(false);

                foreach (var projectId in projectIds)
                {
                    // Business Central asked us to wait; asking again early would only
                    // earn another refusal and lengthen the wait.
                    if (_queue.DeferredUntil(projectId, nowUtc) is not null)
                    {
                        deferred++;
                        continue;
                    }

                    if (await _queue.EnqueueAsync(new EnvironmentRefreshJob(projectId, identity), ct).ConfigureAwait(false))
                    {
                        enqueued++;
                    }

                    // A full queue makes the line above wait for the worker, which is
                    // progress, not a stall: each job that gets in restarts the active
                    // clock, so /healthz/workers only complains when nothing has gone in
                    // for the whole ceiling - which is a worker that has actually stopped.
                    Heartbeat.BeginActive();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "EnvironmentRefreshScheduler sweep failed for org {OrgId}.", orgId);
            }

            // A failed org is not retried on every poll for the rest of its slot: the next
            // slot asks again, as the nightly sweep always did.
            if (slot is { } ran) _lastSlotByOrg[orgId] = ran;
        }

        if (enqueued > 0 || deferred > 0)
        {
            _logger.LogInformation(
                "EnvironmentRefreshScheduler enqueued {Count} project(s) for an environment refresh; {Deferred} skipped because Business Central asked us to wait.",
                enqueued, deferred);
        }
        return enqueued;
    }

    /// <summary>
    /// The projects one org offers to a sweep: the live ones whose Business Central
    /// connection is complete enough to answer. A project missing any credential part
    /// would only produce a token failure per night, so it is not offered at all.
    /// Org-scoped by the EF query filter on <paramref name="db"/> — no
    /// <c>IgnoreQueryFilters()</c> here. Internal so a test can drive it against a seeded
    /// database without the hosted-service loop.
    /// </summary>
    internal static Task<List<int>> ResolveProjectIdsAsync(AppDbContext db, CancellationToken ct)
        => ProjectConnectionService.ConfiguredProjects(db)
            .Select(p => p.Id)
            .ToListAsync(ct);
}
