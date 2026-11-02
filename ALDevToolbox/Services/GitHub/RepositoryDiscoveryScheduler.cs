using ALDevToolbox.Data;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Hosted service that once a day asks GitHub which repositories in each
/// organisation's connected GitHub organisation look like AL extensions, so the
/// Solutions page can offer the untracked ones without anybody waiting for a
/// probe per repository.
///
/// <para>Mirrors <c>EnvironmentRefreshScheduler</c>: poll on a short interval,
/// enumerate the active organisations, and do the per-organisation work inside
/// that organisation's <see cref="AmbientOrganizationScope"/> so the EF query
/// filter behaves exactly as it does in a request. The <em>only</em> cross-org
/// read is the organisation enumeration, which needs no bypass because the
/// organisations table carries no tenant filter - there is no
/// <c>IgnoreQueryFilters()</c> anywhere on this path.</para>
///
/// <para>Nothing here is fatal: an organisation that fails is logged and the
/// sweep carries on, and a missed day costs only staleness - the panel's "Check
/// GitHub now" runs the same sweep on demand. Opt out with
/// <c>DISABLE_GITHUB_REPOSITORY_DISCOVERY_SCHEDULER=1</c>. See
/// <c>.design/github-integration-phase2.md</c>, issue #629.</para>
/// </summary>
public sealed class RepositoryDiscoveryScheduler : PolledScheduler
{
    /// <summary>The UTC hour the daily sweep runs in - an hour after the environment refresh, so the two do not share a peak.</summary>
    internal const int SweepHourUtc = 4;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<RepositoryDiscoveryScheduler> _logger;

    // The last UTC date a sweep ran, so the five-minute poll fires the sweep once
    // per day rather than twelve times inside the sweep hour.
    private DateOnly? _lastSweptUtcDate;

    public RepositoryDiscoveryScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<RepositoryDiscoveryScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build it by hand keep compiling; production DI
        // always supplies the singleton, so the base skips its ticks during a restore.
        Operations.MaintenanceModeState? maintenance = null)
        // A sweep is one tree read and one file read per repository, so it is the
        // slowest of the polled sweeps; the active ceiling is generous enough for
        // an organisation with hundreds of repositories.
        : base(logger, heartbeats, nameof(RepositoryDiscoveryScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_GITHUB_REPOSITORY_DISCOVERY_SCHEDULER",
            maintenance: maintenance)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(nowUtc);
        if (nowUtc.Hour != SweepHourUtc || _lastSweptUtcDate == today) return;

        await SweepAsync(ct).ConfigureAwait(false);
        _lastSweptUtcDate = today;
    }

    /// <summary>
    /// One pass over every active organisation, sweeping the ones with a GitHub
    /// connection (the rest answer zero and cost one cached read). Internal so a
    /// test can drive it directly against a seeded database without the
    /// <see cref="Task.Delay"/> loop. Returns how many AL repositories were found
    /// across all organisations.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one cross-org read: which organisations to sweep. Pending
            // signups have nothing connected, so they are skipped; the system org
            // is swept like any other, since in single-tenant deployments it is
            // the working organisation.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var found = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                var discovery = scope.ServiceProvider.GetRequiredService<RepositoryDiscoveryService>();
                found += await discovery.SweepCurrentOrganisationAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown, not one organisation's failure: stop the sweep rather
                // than logging an error per remaining organisation.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RepositoryDiscoveryScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        if (found > 0)
        {
            _logger.LogInformation(
                "RepositoryDiscoveryScheduler found {Count} AL repositories across {OrgCount} organisation(s).",
                found, orgs.Count);
        }
        return found;
    }
}
