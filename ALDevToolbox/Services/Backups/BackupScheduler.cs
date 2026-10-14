using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Hosted service that drives the daily scheduled backup. Polls
/// <c>system_settings</c> once a minute and, if the configured time has
/// arrived since the last successful scheduled backup, kicks off a new one
/// in the background. Operators can disable the schedule without losing
/// the time-of-day setting.
///
/// <para>
/// The poll interval is intentionally short and tracked against
/// <see cref="Backup.CreatedAt"/> — the host clock isn't always in sync
/// with a wall clock at sub-minute precision, and we'd rather take the
/// backup a minute late than skip a day entirely.
/// </para>
/// </summary>
public sealed class BackupScheduler : PolledScheduler
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ISingleTenantMode _singleTenant;
    private readonly MaintenanceModeState _maintenance;
    private readonly ILogger<BackupScheduler> _logger;

    /// <summary>How long a failed scheduled backup waits before the next attempt.</summary>
    internal static readonly TimeSpan FailedRunBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Set when today's scheduled backup failed: no new attempt before this
    /// instant. In memory only, so a restart tries again straight away.
    /// </summary>
    private DateTime? _retryNotBeforeUtc;

    public BackupScheduler(IServiceProvider services, TimeProvider clock, ISingleTenantMode singleTenant, MaintenanceModeState maintenance, ILogger<BackupScheduler> logger, WorkerHeartbeatRegistry heartbeats)
        // Poll every minute; flag stale if no tick has landed in 5 (~3x the poll
        // interval). Active-duration ceiling matches the longest legitimate backup run
        // we'd allow — pg_dump on a large Postgres takes a while, so keep it generous.
        : base(logger, heartbeats, nameof(BackupScheduler),
            pollInterval: TimeSpan.FromMinutes(1),
            maxActiveDuration: TimeSpan.FromHours(2),
            maxIdleSilence: TimeSpan.FromMinutes(5),
            disableEnvVar: "DISABLE_BACKUP_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _singleTenant = singleTenant;
        _maintenance = maintenance;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        var perTenant = scope.ServiceProvider.GetRequiredService<PerTenantBackupService>();
        var offsite = scope.ServiceProvider.GetRequiredService<OffsiteBackupService>();
        await TickOnceAsync(db, backups, perTenant, offsite, ct);
    }

    /// <summary>
    /// One scheduler decision against the supplied scope. Public for tests
    /// (Issue #71): they drive the schedule decision matrix directly with a
    /// <see cref="TimeProvider"/> they control, bypassing the
    /// <c>Task.Delay</c> loop in <see cref="ExecuteAsync"/>.
    /// </summary>
    internal async Task TickOnceAsync(AppDbContext db, BackupService backups, PerTenantBackupService perTenant, OffsiteBackupService offsite, CancellationToken ct)
    {
        // Skip the whole tick during an in-place restore: the backup create
        // takes the same advisory lock and would fail anyway, but the off-site
        // upload/prune below query the DB whose public schema is being dropped.
        // See issue #370.
        if (_maintenance.IsActive)
        {
            _logger.LogInformation("BackupScheduler skipping tick — maintenance mode active ({Reason}).", _maintenance.Reason);
            return;
        }

        var settings = await db.SystemSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null || !settings.BackupScheduleEnabled) return;

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var scheduled = settings.BackupScheduleTimeUtc;
        var todayWindow = new DateTime(
            nowUtc.Year, nowUtc.Month, nowUtc.Day,
            scheduled.Hour, scheduled.Minute, 0,
            DateTimeKind.Utc);

        // Run if we're at or past today's window and the most recent
        // scheduled backup is older than the window — i.e. today's slot
        // hasn't been served yet. Using `>=` rather than equality means
        // a poll that lands one minute late still triggers.
        if (nowUtc < todayWindow) return;

        var lastScheduled = await db.Backups.AsNoTracking()
            .Where(b => b.Kind == BackupKind.Scheduled)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => (DateTime?)b.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (lastScheduled is not null && lastScheduled >= todayWindow) return;

        // A failed run is tried again after a pause, not on every one-minute
        // poll for the rest of the day (#1269).
        if (_retryNotBeforeUtc is { } retryAt && nowUtc < retryAt) return;

        _logger.LogInformation(
            "BackupScheduler triggering scheduled backup (scheduled-for={Scheduled:o}, now={Now:o}).",
            todayWindow, nowUtc);
        Backup? created = null;
        try
        {
            created = await backups.CreateAsync(BackupKind.Scheduled, ct);
            _retryNotBeforeUtc = null;
        }
        catch (Exception ex)
        {
            _retryNotBeforeUtc = nowUtc + FailedRunBackoff;
            _logger.LogError(ex, "Scheduled backup failed; trying again after {RetryAt:o}.", _retryNotBeforeUtc);
        }

        if (created is not null)
        {
            try
            {
                await offsite.UploadAsync(created.Id, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Off-site upload failed for backup {FileName}.", created.FileName);
            }
            try
            {
                await offsite.PruneAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Off-site prune failed.");
            }
        }

        // Per-tenant snapshots are a multi-tenant surface — single-tenant
        // deployments rely on the full pg_dump above and hide snapshots, so
        // skip the loop entirely there.
        if (_singleTenant.IsEnabled) return;

        // Per-tenant snapshots run after the full backup so the full pg_dump
        // still exists if a per-tenant write fails midway. Each org is
        // independent — a failure on one org logs and continues.
        var orgs = await db.Organizations
            .AsNoTracking()
            .Where(o => !o.IsSystem && !o.IsPending)
            .Select(o => o.Id)
            .ToListAsync(ct);
        foreach (var orgId in orgs)
        {
            PerTenantBackup? perTenantRow = null;
            try
            {
                var lastPerTenant = await db.PerTenantBackups
                    // Pinned to b.OrganizationId == orgId from the sweep's own org enumeration.
                    .AsNoTracking()
                    .Where(b => b.OrganizationId == orgId && b.Kind == BackupKind.Scheduled)
                    .OrderByDescending(b => b.CreatedAt)
                    .Select(b => (DateTime?)b.CreatedAt)
                    .FirstOrDefaultAsync(ct);
                if (lastPerTenant is not null && lastPerTenant >= todayWindow) continue;
                perTenantRow = await perTenant.CreateAsync(orgId, BackupKind.Scheduled, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled per-tenant snapshot failed for org {OrgId}.", orgId);
            }

            // Mirror the freshly-written per-tenant snapshot off-site too —
            // a tenant rolling back to yesterday is still a useful surface
            // after a whole-deployment disaster recovery.
            if (perTenantRow is not null)
            {
                try
                {
                    await offsite.UploadPerTenantAsync(perTenantRow.Id, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Off-site upload of per-tenant snapshot {FileName} (org {OrgId}) failed.",
                        perTenantRow.FileName, orgId);
                }
            }
        }
    }
}
