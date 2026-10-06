using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Keeps every organisation's application-version catalogue up to date with
/// the Business Central waves Microsoft has shipped: once a day it reads the
/// shipped versions off the Microsoft symbol feed and hands them to
/// <see cref="ApplicationVersionService.AddNewWavesAsync"/>, which adds a row
/// for each wave newer than the catalogue's newest. What gets added, and why
/// runtimes with a minor part stay manual, is on <see cref="BusinessCentralWaves"/>.
///
/// <para>
/// The first pass runs shortly after startup, then once per UTC day. The day is
/// remembered in memory only: re-running is harmless, because a wave the
/// catalogue already has is never added twice. A feed that cannot be reached
/// leaves the day unmarked, so the next poll tries again. Each org runs under
/// its own <see cref="AmbientOrganizationScope"/>, so the normal query filter
/// scopes every read and write; the org list itself is not tenant-filtered.
/// Opt out with <c>DISABLE_APPLICATION_VERSION_SYNC=1</c>.
/// </para>
/// </summary>
public sealed class ApplicationVersionSyncScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApplicationVersionSyncScheduler> _logger;

    private DateOnly _lastSyncDate = DateOnly.MinValue;

    public ApplicationVersionSyncScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<ApplicationVersionSyncScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        // One small JSON fetch and a few inserts per org. Ten minutes is a
        // wedged-job ceiling; three hours of silence covers the hourly poll.
        : base(logger, heartbeats, nameof(ApplicationVersionSyncScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromHours(3),
            disableEnvVar: "DISABLE_APPLICATION_VERSION_SYNC")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (_lastSyncDate >= today) return;

        if (await SyncAsync(ct).ConfigureAwait(false))
        {
            _lastSyncDate = today;
        }
    }

    /// <summary>
    /// One pass over every active organisation. Returns false when the feed could
    /// not be read or an org failed, so the caller tries again on the next poll.
    /// Internal so a test can drive it without the poll loop.
    /// </summary>
    internal async Task<bool> SyncAsync(CancellationToken ct)
    {
        IReadOnlyList<string> versions;
        try
        {
            var feed = _services.GetRequiredService<AlSymbolFeedResolver>();
            versions = await feed.ListMicrosoftApplicationVersionsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Application-version sync could not read the Microsoft symbol feed; will retry.");
            return false;
        }

        if (BusinessCentralWaves.FromVersions(versions).Count == 0)
        {
            // An empty or unrecognisable index is a feed problem, not "nothing new".
            _logger.LogWarning("Application-version sync found no release waves among {Count} feed version(s); will retry.", versions.Count);
            return false;
        }

        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            orgs = await ResolveTargetsAsync(db, ct).ConfigureAwait(false);
        }

        var added = 0;
        var failed = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<ApplicationVersionService>();
                added += (await service.AddNewWavesAsync(versions, ct).ConfigureAwait(false)).Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One org's failure (a database error, or an admin saving the same
                // key at that moment) must not cost the others their update.
                failed++;
                _logger.LogError(ex, "Application-version sync failed for org {OrgId}.", orgId);
            }
        }

        _logger.LogInformation(
            "Application-version sync complete: {Added} wave(s) added, {Failed} failed, across {Orgs} org(s).",
            added, failed, orgs.Count);
        // A failed org is retried on the next poll; re-running is harmless for the rest.
        return failed == 0;
    }

    /// <summary>
    /// The organisations to sync: every one that is not still awaiting approval,
    /// the system org included.
    /// </summary>
    internal static async Task<List<(int Id, bool IsSystem)>> ResolveTargetsAsync(AppDbContext db, CancellationToken ct)
    {
        var rows = await db.Organizations.AsNoTracking()
            .Where(o => !o.IsPending)
            .OrderBy(o => o.Id)
            .Select(o => new { o.Id, o.IsSystem })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(o => (o.Id, o.IsSystem)).ToList();
    }
}
