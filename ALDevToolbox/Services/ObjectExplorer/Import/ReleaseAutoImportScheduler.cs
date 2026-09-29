using ALDevToolbox.Data;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Import;

/// <summary>
/// Hosted service that imports new Microsoft OnPrem Business Central releases
/// once a day for every org that opted in
/// (<c>OrganizationSettings.AutoImportReleasesEnabled</c> + a country). Mirrors
/// <see cref="BackupScheduler"/>'s poll-every-minute / run-once-daily shape and
/// <c>PerTenantBackupService</c>'s per-org iteration.
///
/// <para>
/// Each org's import runs under its own <see cref="AmbientOrganizationScope"/>
/// so EF query filters and the importer's org guard behave exactly as in a real
/// request — no new <c>IgnoreQueryFilters()</c> inside the per-org work. The
/// sweep is naturally idempotent: <see cref="ArtifactReleaseImporter"/> skips a
/// version whose release label already exists, so a re-run (after a restart, or
/// the same day) downloads nothing new. Orgs that also opted into preview
/// builds (<c>AutoImportPreviewsEnabled</c>) get the insider channel's upcoming
/// majors on the same pass, and each pass retires previews whose version has
/// since shipped. The run hour is configurable via
/// <c>RELEASE_AUTO_IMPORT_HOUR_UTC</c>; opt out entirely with
/// <c>DISABLE_RELEASE_AUTO_IMPORT_SCHEDULER=1</c>.
/// </para>
///
/// <para>
/// The sweep skips the system org in multi-tenant deployments (there it's only
/// the template source other orgs fork from). Under <c>SINGLE_TENANT_MODE</c>
/// the system org <em>is</em> the one working org, so it's included instead —
/// otherwise the sweep would have nothing to do and never run (issue #518).
/// </para>
/// </summary>
public sealed class ReleaseAutoImportScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReleaseAutoImportScheduler> _logger;
    private readonly bool _singleTenant;
    private readonly int _hourUtc;

    // In-memory "ran today" guard. Re-running is harmless (dedup), so we don't
    // persist this — a restart at worst re-sweeps once, which enqueues nothing
    // new because every existing release label is skipped.
    private DateOnly _lastSweepDate = DateOnly.MinValue;

    public ReleaseAutoImportScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<ReleaseAutoImportScheduler> logger,
        WorkerHeartbeatRegistry heartbeats,
        ISingleTenantMode singleTenant)
        // Poll every minute; the sweep resolves + enqueues quickly (downloads run
        // on ReleaseImportWorker, not here), so a 30-minute active ceiling is
        // ample even for many opted-in orgs.
        : base(logger, heartbeats, nameof(ReleaseAutoImportScheduler),
            pollInterval: PollInterval,
            maxActiveDuration: TimeSpan.FromMinutes(30),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_RELEASE_AUTO_IMPORT_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _singleTenant = singleTenant.IsEnabled;
        _hourUtc = ResolveHourUtc();
    }

    private static int ResolveHourUtc()
    {
        var raw = Environment.GetEnvironmentVariable("RELEASE_AUTO_IMPORT_HOUR_UTC");
        if (int.TryParse(raw, out var hour) && hour is >= 0 and <= 23) return hour;
        return 4; // Quiet pre-dawn UTC window, after the 3am OE vacuum.
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        if (nowUtc.Hour < _hourUtc) return;
        var today = DateOnly.FromDateTime(nowUtc);
        if (_lastSweepDate >= today) return;

        await SweepAsync(ct).ConfigureAwait(false);
        _lastSweepDate = today;
    }

    /// <summary>
    /// The opted-in orgs (id + country list) a sweep should import for: active
    /// (non-pending) orgs whose settings enable auto-import with a country.
    /// <paramref name="includeSystemOrg"/> is <c>true</c> only under
    /// <c>SINGLE_TENANT_MODE</c>, where the system org is the one working org;
    /// otherwise the system org is skipped. Static + DB-only so it's unit-tested
    /// without the importer or the poll loop. See issue #518.
    /// </summary>
    internal static async Task<List<(int OrganizationId, string Countries, bool IsSystem, bool IncludePreviews)>> ResolveTargetsAsync(
        AppDbContext db, bool includeSystemOrg, CancellationToken ct)
    {
        // Enumerates the orgs to sweep; each org's import then runs pinned inside
        // its own AmbientOrganizationScope.
        var activeOrgs = await db.Organizations.AsNoTracking()
            .Where(o => (includeSystemOrg || !o.IsSystem) && !o.IsPending)
            .Select(o => new { o.Id, o.IsSystem })
            .ToListAsync(ct).ConfigureAwait(false);
        // IsSystem travels with the id: the sweep stamps it on the ambient identity so a
        // scheduled import into the system org sees the same quota rule the interactive
        // path does (issue #694).
        var activeSet = activeOrgs.ToDictionary(o => o.Id, o => o.IsSystem);

        // Fence category 3 (scheduler, no request org): opt-in settings for the same sweep.
        var rows = await db.OrganizationSettings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.AutoImportReleasesEnabled && s.AutoImportCountry != null && s.AutoImportCountry != "")
            .Select(s => new { s.OrganizationId, s.AutoImportCountry, s.AutoImportPreviewsEnabled })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows
            .Where(r => activeSet.ContainsKey(r.OrganizationId))
            .Select(r => (r.OrganizationId, r.AutoImportCountry!, activeSet[r.OrganizationId], r.AutoImportPreviewsEnabled))
            .ToList();
    }

    /// <summary>
    /// One pass over every opted-in org. Internal so a test can drive it directly
    /// against a seeded database without the <see cref="Task.Delay"/> loop.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        List<(int OrganizationId, string Countries, bool IsSystem, bool IncludePreviews)> targets;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            targets = await ResolveTargetsAsync(db, includeSystemOrg: _singleTenant, ct).ConfigureAwait(false);
        }

        // Log even the no-op case: a sweep that runs but finds nothing to do is
        // otherwise indistinguishable from one that never ran (the "never checked"
        // symptom in issue #518).
        if (targets.Count == 0)
        {
            _logger.LogInformation("ReleaseAutoImportScheduler swept: no opted-in orgs to import for.");
            return;
        }
        _logger.LogInformation("ReleaseAutoImportScheduler sweeping {Count} opted-in org(s).", targets.Count);

        // Per-sweep tally so the log shows the sweep ran even when nothing new
        // was queued — the "silent when idle" gap behind issue #518. Every
        // outcome (queued / already-imported / nothing-resolved / failed) is
        // logged, so an operator can tell "ran, found nothing" from "never ran".
        var queued = 0;
        var alreadyImported = 0;
        var notFound = 0;
        var failed = 0;
        var previewsQueued = 0;
        var previewsRetired = 0;

        foreach (var (orgId, countries, isSystem, includePreviews) in targets)
        {
            // The setting may hold a comma-separated list ("w1,dk,nl") — one
            // import per code. Each code fails independently so a bad country
            // can't block the rest of the org's list (or the next org).
            foreach (var country in Services.Organizations.OrganizationConfigService.ParseAutoImportCountries(countries))
            {
                try
                {
                    using var ambient = AmbientOrganizationScope.Enter(
                        AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                    await using var scope = _services.CreateAsyncScope();
                    var importer = scope.ServiceProvider.GetRequiredService<ArtifactReleaseImporter>();
                    var outcome = await importer.ImportAsync(country, version: null, ct).ConfigureAwait(false);
                    switch (outcome.Status)
                    {
                        case ArtifactImportStatus.Queued:
                            queued++;
                            _logger.LogInformation(
                                "Auto-import queued {Label} for org {OrgId} (country {Country}).",
                                outcome.Label, orgId, country);
                            break;
                        case ArtifactImportStatus.AlreadyImported:
                            alreadyImported++;
                            _logger.LogInformation(
                                "Auto-import found {Label} already imported for org {OrgId} (country {Country}); skipped.",
                                outcome.Label, orgId, country);
                            break;
                        case ArtifactImportStatus.NotFound:
                            notFound++;
                            _logger.LogWarning(
                                "Auto-import resolved no artifact for org {OrgId} (country {Country}); nothing queued.",
                                orgId, country);
                            break;
                    }

                    // Retire previews the shipped catalogue has caught up with, for
                    // every opted-in org — a preview imported while the box was
                    // ticked should still be replaced after it's unticked.
                    previewsRetired += await importer.SupersedePreviewsAsync(country, ct).ConfigureAwait(false);

                    if (includePreviews)
                    {
                        foreach (var preview in await importer.ImportPreviewsAsync(country, ct).ConfigureAwait(false))
                        {
                            if (preview.Status is ArtifactImportStatus.Queued or ArtifactImportStatus.Replaced)
                            {
                                previewsQueued++;
                                _logger.LogInformation(
                                    "Auto-import queued preview {Label} for org {OrgId} (country {Country}, {Status}).",
                                    preview.Label, orgId, country, preview.Status);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Auto-import failed for org {OrgId} (country {Country}).", orgId, country);
                }
            }

            await StampLastRunAsync(orgId, isSystem, ct).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "ReleaseAutoImportScheduler sweep complete: {Queued} queued, {Already} already imported, {NotFound} not found, {Failed} failed, {PreviewsQueued} preview(s) queued, {PreviewsRetired} preview(s) retired across {Orgs} org(s).",
            queued, alreadyImported, notFound, failed, previewsQueued, previewsRetired, targets.Count);
    }

    /// <summary>
    /// Records that the sweep visited this org (even when every version was
    /// already imported or a country failed) so the artifacts import page can
    /// show "last checked". Runs inside the org's ambient scope — the normal
    /// EF query filter applies, no filter escape needed. Best-effort: a failure
    /// here only costs the timestamp, never the sweep.
    /// </summary>
    private async Task StampLastRunAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        try
        {
            using var ambient = AmbientOrganizationScope.Enter(
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OrganizationSettings.SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (row is null) return;
            row.AutoImportLastRunAt = _clock.GetUtcNow().UtcDateTime;
            row.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // The config cache has no TTL — it only refreshes on invalidation, so
            // a direct write like this must invalidate or the page shows a stale
            // "last checked" until the next settings save.
            scope.ServiceProvider.GetRequiredService<Services.Organizations.OrganizationConfigService>().InvalidateCache(orgId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't stamp auto-import last-run for org {OrgId}.", orgId);
        }
    }
}
