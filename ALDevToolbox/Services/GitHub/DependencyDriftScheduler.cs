using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Hosted service that once a night checks every organisation's tracked repositories
/// against their solutions' environments again, then opens the update pull requests of
/// the solutions that have automatic update pull requests turned on (issue #1104).
///
/// <para>
/// It runs at <see cref="SweepHourUtc"/>, after <see cref="ObjectExplorer.Bc.EnvironmentRefreshScheduler"/>
/// has re-read the environments, so a customer moved to a new Business Central during
/// the day is measured against it by the morning. Until this ran, only a release import
/// or "Check again" rescanned. Mirrors <see cref="ObjectExplorer.Projects.PreviewCheckScheduler"/>:
/// the organisations table carries no tenant filter, so enumerating it needs no bypass,
/// and the per-org work runs inside that org's <see cref="AmbientOrganizationScope"/>.
/// The pull requests of each solution are opened as the person who turned the setting
/// on, in a scope of their own; when that person is gone, the solution shows why
/// instead. Opt out with <c>DISABLE_DEPENDENCY_DRIFT_SCHEDULER=1</c>. See
/// <c>.design/github-integration-phase2.md</c>.
/// </para>
/// </summary>
public sealed class DependencyDriftScheduler : PolledScheduler
{
    /// <summary>The UTC hour the nightly pass runs in, after the environment refresh.</summary>
    internal const int SweepHourUtc = 5;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<DependencyDriftScheduler> _logger;

    private DateOnly? _lastSweptUtcDate;

    public DependencyDriftScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<DependencyDriftScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // A pass reads each tracked repository's manifests once and writes a pull
        // request per behind repository, so half an hour is a generous ceiling.
        : base(logger, heartbeats, nameof(DependencyDriftScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_DEPENDENCY_DRIFT_SCHEDULER")
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
    /// One pass over every active org. Internal so a test can drive it directly against
    /// a seeded database. Returns how many pull requests were opened.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one cross-org read: which orgs to sweep. The system org is swept
            // too, since in single-tenant deployments it is the working org.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var opened = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                opened += await SweepOrganizationAsync(orgId, isSystem, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "DependencyDriftScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        if (opened > 0)
        {
            _logger.LogInformation("DependencyDriftScheduler opened {Count} update pull request(s).", opened);
        }
        return opened;
    }

    private async Task<int> SweepOrganizationAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var drift = scope.ServiceProvider.GetRequiredService<DependencyDriftService>();

        // The scan needs an imported release to file its findings under; an
        // organisation without one has never had the panel either.
        if (await drift.NewestFirstPartyReleaseIdAsync(ct).ConfigureAwait(false) is not { } releaseId) return 0;
        await drift.ScanForReleaseAsync(releaseId, ct).ConfigureAwait(false);

        var solutions = await db.OeProjects.AsNoTracking()
            .Where(p => p.AutoUpdatePullRequests && p.DeletedAt == null)
            .Select(p => new
            {
                p.Id,
                p.AutoUpdatePullRequestsByUserId,
                OwnerActive = p.AutoUpdatePullRequestsByUser != null
                              && p.AutoUpdatePullRequestsByUser.Status == UserStatus.Active,
            })
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var opened = 0;
        // People GitHub is rate limiting tonight: every write on their account is refused
        // until it cools down, so their other solutions wait for the next night.
        var rateLimited = new HashSet<int>();
        foreach (var solution in solutions)
        {
            string? blocked;
            if (solution.AutoUpdatePullRequestsByUserId is not { } userId || !solution.OwnerActive)
            {
                blocked = DependencyDriftService.AutomaticNoOwnerMessage;
            }
            else if (rateLimited.Contains(userId))
            {
                _logger.LogInformation(
                    "Automatic update pull requests for solution {ProjectId} wait for the next night: GitHub is rate limiting user {UserId}.",
                    solution.Id, userId);
                continue;
            }
            else
            {
                // An unexpected failure is the log's to explain, not the solution's;
                // whatever it said before stays until a run says otherwise.
                if (await OpenAsAsync(orgId, isSystem, userId, solution.Id, ct).ConfigureAwait(false) is not { } result) continue;
                opened += result.Opened;
                if (result.RateLimited)
                {
                    // Not something the solution has to fix, so it is not shown there;
                    // nor does a run cut short prove earlier trouble has gone.
                    rateLimited.Add(userId);
                    continue;
                }
                blocked = result.Blocked;
            }
            await SetBlockedAsync(db, solution.Id, blocked, ct).ConfigureAwait(false);
        }
        return opened;
    }

    /// <summary>
    /// Opens one solution's pull requests in a scope of its own, signed in as
    /// <paramref name="userId"/>. Null when it failed unexpectedly.
    /// </summary>
    private async Task<AutomaticUpdatePullRequests?> OpenAsAsync(
        int orgId, bool isSystem, int userId, int projectId, CancellationToken ct)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem, userId));
        await using var scope = _services.CreateAsyncScope();
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<DependencyDriftService>()
                .OpenAutomaticPullRequestsAsync(projectId, ct).ConfigureAwait(false);
            if (result.Blocked is { } reason)
            {
                _logger.LogInformation(
                    "Automatic update pull requests for solution {ProjectId} as user {UserId} were held up: {Reason}",
                    projectId, userId, reason);
            }
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex,
                "DependencyDriftScheduler could not open the update pull requests of solution {ProjectId}.", projectId);
            return null;
        }
    }

    /// <summary>Records why a solution's run was held up, or clears it with null. Writes only when the value changes.</summary>
    private static Task SetBlockedAsync(AppDbContext db, int projectId, string? reason, CancellationToken ct)
    {
        if (reason is { Length: > 500 }) reason = reason[..500];
        return db.OeProjects
            .Where(p => p.Id == projectId && p.AutoUpdatePullRequestsBlocked != reason)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.AutoUpdatePullRequestsBlocked, reason), ct);
    }
}
