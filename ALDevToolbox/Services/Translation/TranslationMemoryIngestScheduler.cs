using ALDevToolbox.Data;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Hosted service that once a night offers every GitHub-connected organisation
/// to <see cref="TranslationMemoryIngestService"/>, so the Translator's
/// suggestions keep up with what colleagues translate in code without anybody
/// pressing anything (issue #631).
///
/// <para>Shaped like <see cref="ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshScheduler"/>:
/// poll on a short interval, enumerate organisations, and do the per-org work
/// inside that org's <see cref="AmbientOrganizationScope"/> so the EF query
/// filter behaves exactly as it does in a request. The <em>only</em> cross-org
/// read is the organisation enumeration, which needs no bypass because the
/// organisations table carries no tenant filter - there is no
/// <c>IgnoreQueryFilters()</c> anywhere in this feature.</para>
///
/// <para>It runs at <see cref="SweepHourUtc"/>, once per UTC day. Nothing here
/// is fatal: an organisation that fails is logged and the sweep carries on, and
/// a missed night costs only suggestions nobody had yesterday either. Opt out
/// with <c>DISABLE_TRANSLATION_MEMORY_INGEST_SCHEDULER=1</c>.</para>
/// </summary>
public sealed class TranslationMemoryIngestScheduler : PolledScheduler
{
    /// <summary>The UTC hour the nightly sweep runs in - quiet for European working hours, and an hour clear of the environment refresh.</summary>
    internal const int SweepHourUtc = 2;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<TranslationMemoryIngestScheduler> _logger;

    // The last UTC date a sweep ran, so the five-minute poll fires the sweep
    // once per night rather than twelve times inside the sweep hour.
    private DateOnly? _lastSweptUtcDate;

    public TranslationMemoryIngestScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<TranslationMemoryIngestScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build it by hand keep compiling; production DI
        // always supplies the singleton, so the base skips its ticks during a restore.
        Operations.MaintenanceModeState? maintenance = null)
        // A sweep does the GitHub reads itself rather than enqueuing them, so it
        // is allowed to take a while: a large organisation is one tree read per
        // repository plus the files that moved.
        : base(logger, heartbeats, nameof(TranslationMemoryIngestScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromHours(1),
            maxIdleSilence: TimeSpan.FromMinutes(30),
            disableEnvVar: "DISABLE_TRANSLATION_MEMORY_INGEST_SCHEDULER",
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
    /// One pass over every organisation, ingesting for the ones that have
    /// connected a GitHub organisation. Internal so a test can drive it
    /// directly against a seeded database without the <see cref="Task.Delay"/>
    /// loop. Returns how many organisations were ingested for.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The one cross-org read: which organisations to sweep. It needs no
            // bypass - the organisations table carries no tenant filter. Pending
            // signups have nothing to ingest for.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var ingested = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();

                // Ask the cheap question first: an organisation with no GitHub
                // connection has nothing to read, and saying so from the
                // database costs no call to GitHub.
                var connection = scope.ServiceProvider.GetRequiredService<GitHubConnectionService>();
                if (!(await connection.GetStatusAsync(ct).ConfigureAwait(false)).IsConnected) continue;

                var ingest = scope.ServiceProvider.GetRequiredService<TranslationMemoryIngestService>();
                var summary = await ingest.IngestCurrentOrganisationAsync(ct).ConfigureAwait(false);
                ingested++;

                if (summary.PairsLearned > 0 || summary.RepositoriesFailed > 0)
                {
                    _logger.LogInformation(
                        "Nightly translation memory ingest for org {OrgId}: Learned={PairsLearned} Failed={RepositoriesFailed}",
                        orgId, summary.PairsLearned, summary.RepositoriesFailed);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TranslationMemoryIngestScheduler sweep failed for org {OrgId}.", orgId);
            }
        }

        return ingested;
    }
}
