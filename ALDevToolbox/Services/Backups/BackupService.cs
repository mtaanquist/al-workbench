using System.Diagnostics;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services.Backups;

/// <summary>
/// Row view returned by <see cref="BackupService.ListAsync"/>. Carries the
/// metadata the <c>/site-admin/backups</c> page renders without re-reading
/// disk on every request.
/// </summary>
public sealed record BackupRow(
    int Id,
    string FileName,
    long FileSizeBytes,
    DateTime CreatedAt,
    int? CreatedByUserId,
    string? CreatedByEmail,
    BackupKind Kind,
    bool IsPinned,
    DateTime? OffsiteUploadedAt,
    string? OffsiteObjectKey,
    string? RecoveredFromDeploymentId = null);

/// <summary>
/// Shells out to <c>pg_dump</c> / <c>pg_restore</c> against the application
/// database. The connection metadata comes from the <see cref="AppDbContext"/>
/// connection string so operators only configure credentials once.
///
/// <para>
/// Restores run in-place: the service flips
/// <see cref="MaintenanceModeState"/> on, drops every object in the
/// <c>public</c> schema, runs <c>pg_restore</c>, and lifts maintenance mode
/// once the restore has finished. A restore that fails after the drop keeps
/// maintenance mode on (#1321). See <c>.design/milestones.md</c> M18.
/// </para>
/// </summary>
public sealed class BackupService
{
    /// <summary>Filename suffix every backup carries — kept stable so manual recovery is straightforward.</summary>
    public const string BackupFileSuffix = ".dump";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly MaintenanceModeState _maintenance;
    private readonly ILogger<BackupService> _logger;
    private readonly TimeProvider _clock;
    private readonly string _connectionString;
    private readonly string _backupsDirectory;
    private readonly string _pgDumpPath;
    private readonly string _pgRestorePath;
    private readonly TimeSpan _activeJobsWait;
    private readonly SystemSettingsService? _systemSettings;
    private readonly RestoreInventoryHold? _restoreInventory;
    private readonly DeploymentIdentity? _deployment;

    public BackupService(
        AppDbContext db,
        IOrganizationContext orgContext,
        MaintenanceModeState maintenance,
        IConfiguration configuration,
        ILogger<BackupService> logger,
        TimeProvider clock,
        // Optional so the tests that build this service by hand keep compiling.
        // In production DI it is always the instance registered from configuration.
        BackupOptions? options = null,
        // Optional for the same reason; production DI always supplies it, and a
        // restore re-applies the restored settings row through it (#1255).
        SystemSettingsService? systemSettings = null,
        // Optional for the same reason; production DI always supplies the
        // singleton, which carries the inventory across a failed restore (#1321).
        RestoreInventoryHold? restoreInventory = null,
        // Optional for the same reason; production DI always supplies it. Only
        // the restore of a recovered dump needs it (#1322).
        DeploymentIdentity? deployment = null)
    {
        _db = db;
        _deployment = deployment;
        _systemSettings = systemSettings;
        _restoreInventory = restoreInventory;
        _orgContext = orgContext;
        _maintenance = maintenance;
        _logger = logger;
        _clock = clock;
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required for BackupService.");
        var backupOptions = options ?? new BackupOptions();
        _backupsDirectory = backupOptions.Directory;
        _pgDumpPath = backupOptions.PgDumpPath;
        _pgRestorePath = backupOptions.PgRestorePath;
        _activeJobsWait = backupOptions.ActiveJobsWait;
    }

    /// <summary>Absolute path to the backups directory. Created on demand.</summary>
    public string BackupsDirectory => _backupsDirectory;

    /// <summary>
    /// Cross-org listing of every backup row, newest first. The
    /// <c>/site-admin/backups</c> page calls this on load and after every
    /// pin / unpin / delete action.
    /// </summary>
    public async Task<List<BackupRow>> ListAsync(CancellationToken ct = default)
    {
        RequireSiteAdmin();
        return await _db.Backups.AsNoTracking()
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new BackupRow(
                b.Id,
                b.FileName,
                b.FileSizeBytes,
                b.CreatedAt,
                b.CreatedByUserId,
                b.CreatedByUser == null ? null : b.CreatedByUser.Email,
                b.Kind,
                b.IsPinned,
                b.OffsiteUploadedAt,
                b.OffsiteObjectKey,
                b.RecoveredFromDeploymentId))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Streams a backup's content to the caller. Returns <c>null</c> when the
    /// row is missing or the file on disk has been removed out-of-band.
    /// </summary>
    public async Task<(Backup Row, FileStream Stream)?> OpenForDownloadAsync(int id, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var row = await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null) return null;
        var path = ResolveFilePath(row.FileName);
        if (!File.Exists(path)) return null;
        return (row, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true));
    }

    /// <summary>
    /// Runs <c>pg_dump</c> in custom format (<c>-Fc</c>) against the
    /// application database, writes the file under <see cref="BackupsDirectory"/>,
    /// inserts a row in the <c>backups</c> table, then prunes the oldest
    /// unpinned files past the retention count.
    /// </summary>
    public async Task<Backup> CreateAsync(BackupKind kind, CancellationToken ct = default)
    {
        if (kind == BackupKind.AdHoc)
        {
            RequireSiteAdmin();
        }

        Directory.CreateDirectory(_backupsDirectory);

        // Serialize create+prune against other backups and any restore: two
        // concurrent runs would both Skip(retention) and delete the same overflow
        // rows (double-delete / DbUpdateConcurrencyException), and a backup taken
        // mid-restore reads a schema being dropped. See issues #370 and #371.
        await using var coordination = await BackupCoordination.AcquireAsync(_connectionString, ct);

        var timestamp = _clock.GetUtcNow().UtcDateTime;
        var label = kind == BackupKind.Scheduled ? "scheduled" : "adhoc";
        // Millisecond precision so two runs in the same second don't collide on
        // the target path (the advisory lock serializes them, but they can still
        // land in the same wall-clock second). See issue #371.
        var fileName = $"aldevtoolbox-{timestamp:yyyyMMddTHHmmssfffZ}-{label}{BackupFileSuffix}";
        var targetPath = ResolveFilePath(fileName);

        var sw = Stopwatch.StartNew();
        Backup row;
        long size;
        try
        {
            await RunPgToolAsync(
                _pgDumpPath,
                new[] { "-Fc", "--no-owner", "--no-privileges", "-f", targetPath },
                ct);
            sw.Stop();

            size = new FileInfo(targetPath).Length;
            row = new Backup
            {
                FileName = fileName,
                FileSizeBytes = size,
                CreatedAt = timestamp,
                CreatedByUserId = kind == BackupKind.AdHoc ? _orgContext.CurrentUserId : null,
                Kind = kind,
                IsPinned = false,
            };
            _db.Backups.Add(row);
            await _db.SaveChangesAsync(ct);
        }
        catch
        {
            // A cancelled or failed pg_dump leaves a partial file with no row
            // behind it, which retention would never prune (#1213); so does a
            // dump whose row could not be written (#1269).
            TryDeleteFile(fileName);
            throw;
        }

        _logger.LogInformation(
            "Backup {FileName} written in {ElapsedMs} ms ({SizeBytes} bytes, kind={Kind}).",
            fileName, sw.ElapsedMilliseconds, size, kind);

        await PruneRetentionAsync(ct);
        return row;
    }

    /// <summary>
    /// Pins or unpins a backup so retention pruning skips (or stops skipping)
    /// it. Idempotent; an already-pinned row that's pinned again is a no-op.
    /// </summary>
    public async Task SetPinnedAsync(int id, bool pinned, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var row = await _db.Backups.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["BackupId"] = "Backup not found." });
        }
        if (row.IsPinned == pinned) return;
        row.IsPinned = pinned;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Backup {FileName} pinned={Pinned}.", row.FileName, pinned);
    }

    /// <summary>
    /// Deletes a backup file and its DB row. Pinned backups must be
    /// unpinned first — pinning is the customer-facing way to opt out of
    /// deletion, and a single click shouldn't bypass it.
    /// </summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var row = await _db.Backups.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null) return;
        if (row.IsPinned)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "Unpin the backup before deleting it.",
            });
        }
        TryDeleteFile(row.FileName);
        _db.Backups.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Backup {FileName} deleted by SiteAdmin {UserId}.", row.FileName, _orgContext.CurrentUserId);
    }

    /// <summary>
    /// In-place restore: flips maintenance mode on, drops every object in
    /// the <c>public</c> schema, runs <c>pg_restore</c>, brings the restored
    /// schema up to this version's migrations, puts back the inventory of
    /// backup files, re-applies the restored settings to the in-memory copies,
    /// and lifts maintenance mode.
    ///
    /// <para>
    /// The <c>backups</c> and <c>per_tenant_backups</c> tables describe files
    /// on disk, which a restore does not rewind. The dump carries those tables
    /// as they were when it was taken (without its own row, which is inserted
    /// after <c>pg_dump</c> finishes), so they are read before the drop and put
    /// back afterwards; otherwise every newer backup, the restored one included,
    /// would drop out of listing, restore and retention (#1254). Migrations and
    /// the in-memory settings otherwise only run at startup, so a dump from an
    /// older version would leave the application on the old schema until the
    /// next restart (#1255).
    /// </para>
    ///
    /// <para>
    /// A file that is not the size it was taken at, or that <c>pg_restore</c>
    /// cannot read from end to end, is refused before anything changes. If the
    /// restore still fails once the schema has been dropped, the schema is
    /// migrated and the inventory put back as far as possible, maintenance mode
    /// is held on (<see cref="MaintenanceModeState.HoldAfterFailedRestore"/>),
    /// the inventory read before the failure is kept in
    /// <see cref="RestoreInventoryHold"/> for the next restore, and
    /// <see cref="BackupRestoreFailedException"/> is thrown: reopening would
    /// hand every user an empty or half-loaded database, while staying closed
    /// leaves the SiteAdmin the backups page to restore another one (#1321).
    /// </para>
    ///
    /// <para>
    /// <paramref name="ct"/> only covers the checks and the wait for the
    /// backup lock. Once maintenance mode is on, the drop and the
    /// <c>pg_restore</c> run to completion even if the caller goes away (the
    /// SiteAdmin closes the tab, a proxy drops the long POST): stopping halfway
    /// would leave a half-loaded schema, and lifting maintenance mode while an
    /// orphaned <c>pg_restore</c> is still writing would let every user, and
    /// the backup scheduler, in on it. See issue #1213.
    /// </para>
    /// </summary>
    public async Task RestoreAsync(int id, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var row = await _db.Backups.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id, ct);
        if (row is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["BackupId"] = "Backup not found." });
        }

        // A dump downloaded with Recover was made by another installation. It
        // may only replace a fresh one, checked here in the SiteAdmin's request
        // rather than on the download worker; and it is this restore, not the
        // download, that makes this installation take over the other one's
        // backups (#1322). Once that has happened its dumps are no longer
        // foreign, so restoring one again is an ordinary restore.
        var takeOver = row.RecoveredFromDeploymentId is { } recoveredFrom
            && _deployment?.IsFormer(recoveredFrom) != true
            ? recoveredFrom
            : null;
        if (takeOver is not null)
        {
            var blocker = await RecoveryBlockerAsync(ct);
            if (blocker is not null)
            {
                throw new PlanValidationException(new Dictionary<string, string> { ["BackupId"] = blocker });
            }
        }
        var path = ResolveFilePath(row.FileName);
        if (!File.Exists(path))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "Backup file is missing from the backups directory.",
            });
        }

        // Read the whole dump before anything is dropped (#1321).
        await EnsureDumpIsReadableAsync(row, path, ct);

        // Same advisory lock the backup path takes, so a scheduled pg_dump (or
        // off-site prune) can't run against the schema we're about to drop. See
        // issue #370.
        await using var coordination = await BackupCoordination.AcquireAsync(_connectionString, ct);

        var inventory = await CaptureInventoryForRestoreAsync(ct);

        // A restore can start while an earlier failed one holds the site closed;
        // if this one fails before the drop, that hold has to come back as it was.
        var before = _maintenance.Capture();
        _maintenance.Enter($"Restoring backup {row.FileName}");
        // With maintenance mode on, no scheduler tick or queued job starts. Wait,
        // for a limited time, for those already running to finish: an import or
        // build would keep writing into the dropped and half-loaded schema. If they
        // do not finish, refuse rather than drop under them (#1538). Jobs still
        // queued do not hold the restore up; they are dropped once it completes.
        bool quiet;
        try
        {
            quiet = await _maintenance.WaitForActiveJobsAsync(_activeJobsWait, ct);
        }
        catch
        {
            _maintenance.Restore(before);
            throw;
        }
        if (!quiet)
        {
            var running = _maintenance.ActiveJobs;
            _maintenance.Restore(before);
            _logger.LogWarning(
                "Refusing to restore {FileName}: {Running} background job(s) were still running after {Wait}.",
                row.FileName, running, _activeJobsWait);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "An import, a build or another background task is still running, and a restore would "
                    + "replace the database under it. Wait until imports and builds have finished, then restore. "
                    + "Nothing was changed.",
            });
        }
        var sw = Stopwatch.StartNew();
        // Deliberately not the caller's token from here on; see the remarks above.
        var destructive = CancellationToken.None;
        var dropped = false;
        var restored = false;
        try
        {
            // Drop everything in `public` so pg_restore lays the schema down on
            // a clean slate. The dump itself doesn't drop the schema (only the
            // owned objects) because that gives partial restores room to fail
            // mid-flight without nuking unrelated state. Both statements go in
            // one command, so they commit or fail together.
            await ExecuteSqlAsync(
                "DROP SCHEMA IF EXISTS public CASCADE; CREATE SCHEMA public;",
                destructive);
            dropped = true;

            var builder = new NpgsqlConnectionStringBuilder(_connectionString);
            await RunPgToolAsync(
                _pgRestorePath,
                new[]
                {
                    "--no-owner",
                    "--no-privileges",
                    "--clean",
                    "--if-exists",
                    "-d", builder.Database ?? string.Empty,
                    path,
                },
                destructive);

            await MigrateAsync(destructive);
            await RestoreInventoryAsync(inventory, destructive);
            if (_systemSettings is not null) await _systemSettings.ReloadAfterRestoreAsync(destructive);
            restored = true;
            _restoreInventory?.Clear();

            sw.Stop();
            // Imports and builds the dump caught part-way have no job behind them in
            // this process. The startup reconcile marks them, so the backups page
            // asks the SiteAdmin to restart (#1538).
            _logger.LogInformation(
                "Restored backup {FileName} in {ElapsedMs} ms. Restart the application so imports and builds the backup caught part-way are marked as interrupted.",
                row.FileName, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (dropped)
        {
            // The old database is gone and the new one is missing or partial.
            // Reopening would hand every user an empty or half-loaded site, so
            // maintenance mode stays on. Bring the schema up to date and put the
            // inventory back so the backups page still lists every file and the
            // SiteAdmin can restore another one from it (#1321).
            _logger.LogError(ex,
                "Restore of backup {FileName} failed after the database was dropped; staying in maintenance mode.",
                row.FileName);
            // What goes back after a failure is degraded: per-tenant snapshots
            // whose organisation is gone are left out and missing creators are
            // cleared. Keep what was read before this restore, so the next one
            // puts the full list back rather than the degraded one.
            _restoreInventory?.Hold(inventory);
            await RecoverAfterFailedRestoreAsync(inventory);
            _maintenance.HoldAfterFailedRestore(
                $"Restoring backup {row.FileName} failed. The database may be empty or only partly loaded.");
            throw new BackupRestoreFailedException(
                "The restore failed. The site is still closed to everyone except site administrators. "
                    + "Restore another backup to reopen it.",
                ex);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // The drop never happened, so the database is as it was. Rethrown as
            // the type the restore endpoint already reports to the SiteAdmin.
            _logger.LogError(ex, "Restore of backup {FileName} failed before anything was changed.", row.FileName);
            throw new InvalidOperationException(
                "The database could not be prepared for the restore; nothing was changed. See the server logs for details.",
                ex);
        }
        finally
        {
            // Anything queued or held from before belongs to the database just
            // replaced; moving the epoch on, before the site reopens, makes the
            // queues and workers drop it (#1538).
            if (dropped) _maintenance.AdvanceEpoch();
            if (restored) _maintenance.Exit();
            // Nothing was dropped, so put back whatever was there before: open, or
            // still held closed by an earlier failed restore.
            else if (!dropped) _maintenance.Restore(before);
        }

        if (takeOver is not null)
        {
            // Only once the restore has succeeded, so a failed one takes nothing
            // over; and outside the block above, so a failure to record it is not
            // mistaken for a failed restore of a database that loaded fine. Kept
            // on the key volume, which the restore does not touch.
            try
            {
                _deployment!.RecordFormer(takeOver, _clock.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "The backup was restored, but this server could not record that it replaces the installation that made it: " + ex.Message, ex);
            }
            _logger.LogWarning(
                "Restored backup {FileName}, made by another installation; this installation now treats that installation's off-site backups as restorable here.",
                row.FileName);
        }
    }

    /// <summary>
    /// Refuses a backup file before maintenance mode or the drop when it is not
    /// the size it was when it was taken, or when <c>pg_restore</c> cannot read
    /// it from end to end. The read decompresses every table into
    /// <c>/dev/null</c> without touching the database, so it catches a file cut
    /// short or damaged part-way, which reading only the table of contents does
    /// not. It costs one extra pass over the file. It cannot catch a dump that
    /// reads cleanly but that this database will not accept (#1321).
    /// </summary>
    private async Task EnsureDumpIsReadableAsync(Backup row, string path, CancellationToken ct)
    {
        if (row.FileSizeBytes > 0 && new FileInfo(path).Length != row.FileSizeBytes)
        {
            _logger.LogWarning(
                "Refusing to restore {FileName}: {Actual} bytes on disk, {Expected} recorded when it was taken.",
                row.FileName, new FileInfo(path).Length, row.FileSizeBytes);
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "This backup file is not the size it was when it was taken, so it is probably damaged "
                    + "or incomplete. Nothing was changed - pick another backup.",
            });
        }
        try
        {
            await RunPgToolAsync(_pgRestorePath, new[] { "-f", "/dev/null", path }, ct);
        }
        catch (InvalidOperationException)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["BackupId"] = "This backup file can't be read. It may be damaged or incomplete, or taken by a newer "
                    + "version of PostgreSQL. Nothing was changed - pick another backup.",
            });
        }
    }

    /// <summary>
    /// The <c>backups</c> and <c>per_tenant_backups</c> rows to put back after
    /// this restore. Normally what the tables hold now. After a failed restore,
    /// the list kept from before that failure, plus any row added since (an
    /// off-site download, a new backup), matched by file name (#1321). If the
    /// failure left no tables to read, the kept list is all there is.
    /// </summary>
    private async Task<BackupInventory> CaptureInventoryForRestoreAsync(CancellationToken ct)
    {
        var held = _restoreInventory?.Held;
        if (held is null) return await CaptureInventoryAsync(ct);
        BackupInventory current;
        try
        {
            current = await CaptureInventoryAsync(ct);
        }
        catch (PostgresException ex)
        {
            _logger.LogWarning(ex, "Could not read the backup tables; restoring with the list kept from the failed restore.");
            return held;
        }
        return BackupInventory.Merge(held, current);
    }

    /// <summary>
    /// Brings the restored schema up to this version's migrations. A dump taken
    /// by an older version restores an older schema, and startup is the only
    /// other place migrations run, so without this every query on a newer
    /// column fails until the next restart (#1255).
    /// </summary>
    /// <remarks>
    /// The command timeout is startup's half hour, which a large data migration may
    /// need. Waiting for a lock is another matter: a session left holding one would
    /// otherwise keep the restore, with the site closed, waiting the full half hour
    /// before it failed. For the migrate only, the context connects with a one-minute
    /// <c>lock_timeout</c> in its startup options, so that fails in a minute instead
    /// (#1538). The connection string goes back as it was afterwards.
    /// </remarks>
    private async Task MigrateAsync(CancellationToken ct)
    {
        var original = _db.Database.GetConnectionString();
        var withLockTimeout = new NpgsqlConnectionStringBuilder(original)
        {
            Options = "-c lock_timeout=" + (long)RestoreMigrateLockTimeout.TotalMilliseconds,
        };
        await _db.Database.CloseConnectionAsync();
        _db.Database.SetConnectionString(withLockTimeout.ConnectionString);
        _db.Database.SetCommandTimeout(ALDevToolbox.Endpoints.StartupTasks.MigrationCommandTimeout);
        try
        {
            await _db.Database.MigrateAsync(ct);
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
            _db.Database.SetConnectionString(original);
            _db.Database.SetCommandTimeout((int?)null);
        }
    }

    /// <summary>How long the restore's migrate waits for a lock before it fails.</summary>
    internal static readonly TimeSpan RestoreMigrateLockTimeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Best effort after a restore that failed once the database was dropped:
    /// migrate whatever is there (an empty schema becomes an empty but complete
    /// one), put the backup inventory back, and reload the in-memory settings.
    /// Each step is guarded on its own so one failure does not stop the next;
    /// the original failure is what the caller reports.
    /// </summary>
    private async Task RecoverAfterFailedRestoreAsync(BackupInventory inventory)
    {
        var ct = CancellationToken.None;
        try
        {
            await MigrateAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Migrating the database after a failed restore also failed.");
        }
        try
        {
            await RestoreInventoryAsync(inventory, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Putting back the backup inventory after a failed restore also failed.");
        }
        try
        {
            if (_systemSettings is not null) await _systemSettings.ReloadAfterRestoreAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reloading settings after a failed restore also failed.");
        }
    }

    /// <summary>
    /// Why this installation can't take over another installation's backups
    /// (#1322), as a sentence for the SiteAdmin; <see langword="null"/> when it
    /// can. It has to be fresh: no organisation besides the system one, no
    /// user besides the bootstrap admin, no solutions and no releases. Its own
    /// backups don't count: a new server takes one on its first start, and
    /// with the rest empty it holds nothing. Restoring another installation's
    /// dump over anything more would replace real data with a neighbour's,
    /// which the deployment stamp exists to stop.
    ///
    /// <para>
    /// Only the organisation check reads a table without a query
    /// filter. Users, solutions and releases are counted through the normal
    /// filter, so they count the SiteAdmin caller's own organisation. That is
    /// enough, and needs no <c>IgnoreQueryFilters()</c>: the organisation check
    /// runs first, and when no other organisation exists every row belongs to
    /// the system org, which is then the caller's. It is also why this only
    /// means anything in a SiteAdmin request, never on a background worker.
    /// </para>
    /// </summary>
    public async Task<string?> RecoveryBlockerAsync(CancellationToken ct = default)
    {
        RequireSiteAdmin();
        if (_deployment is null || !_deployment.IsPersistent)
        {
            return "This server can't record which installation it replaces, because its key storage isn't writable.";
        }
        if (_deployment.Former is not null)
        {
            return "This installation has already taken over another installation's backups. Remove that on the Whole site tab of Backup & storage first.";
        }
        if (await _db.Organizations.AsNoTracking().AnyAsync(o => !o.IsSystem, ct))
        {
            return "This installation already has organisations of its own, so another installation's backup can't be restored over it.";
        }
        if (await _db.Users.AsNoTracking().CountAsync(ct) > 1)
        {
            return "This installation already has users besides the first administrator, so another installation's backup can't be restored over it.";
        }
        if (await _db.OeProjects.AsNoTracking().AnyAsync(ct) || await _db.OeReleases.AsNoTracking().AnyAsync(ct))
        {
            return "This installation already has solutions or releases of its own, so another installation's backup can't be restored over it.";
        }
        // Deliberately no check on this server's own backups: a new server takes
        // one within seconds of its first start, and the checks above already
        // show the site is empty, so that backup holds nothing worth protecting.
        return null;
    }

    /// <summary>True when <see cref="RecoveryBlockerAsync"/> finds nothing in the way.</summary>
    public async Task<bool> CanRecoverAnotherInstallationAsync(CancellationToken ct = default) =>
        await RecoveryBlockerAsync(ct) is null;

    /// <summary>
    /// Forgets the installation this one was rebuilt from (#1322). Its off-site
    /// backups read as another installation's again: they can no longer be
    /// restored here, and retention still leaves them alone.
    /// </summary>
    public void ForgetFormerInstallation()
    {
        RequireSiteAdmin();
        if (_deployment?.Former is null) return;
        _deployment.ForgetFormer();
        _logger.LogInformation("SiteAdmin {UserId} removed the record of the installation this one was rebuilt from.", _orgContext.CurrentUserId);
    }

    /// <summary>
    /// Removes the oldest unpinned files past the retention count, writing
    /// one log line per deletion. Called automatically at the end of every
    /// successful backup.
    /// </summary>
    public async Task PruneRetentionAsync(CancellationToken ct = default)
    {
        var retention = await _db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => (int?)s.BackupRetentionCount)
            .FirstOrDefaultAsync(ct) ?? 14;
        if (retention < 1) retention = 1;

        // Order newest first, keep (Skip) the most recent `retention` unpinned
        // rows, delete the older remainder. Pinned rows never enter the
        // candidate set at all.
        var unpinned = await _db.Backups
            .Where(b => !b.IsPinned)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        if (unpinned.Count <= retention) return;

        var toDelete = unpinned.Skip(retention).ToList();
        foreach (var row in toDelete)
        {
            TryDeleteFile(row.FileName);
            _db.Backups.Remove(row);
            _logger.LogInformation("Pruned backup {FileName} (retention={Retention}).", row.FileName, retention);
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Resolves a <c>backups</c> row's <c>FileName</c> to an absolute path
    /// inside the backups directory, refusing any name that could escape it.
    /// Public so sibling services that combine a DB-sourced file name with
    /// <see cref="BackupsDirectory"/> (e.g. <see cref="OffsiteBackupService"/>'s
    /// upload path) reuse the same guard instead of re-implementing it. See #480.
    /// </summary>
    public string ResolveFilePath(string fileName)
    {
        // Defence against a tampered DB row: a path-separator or
        // .. component would let a delete or download escape the
        // backups directory.
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to handle suspicious backup file name: {fileName}");
        }
        return Path.Combine(_backupsDirectory, fileName);
    }

    private void TryDeleteFile(string fileName)
    {
        try
        {
            var path = ResolveFilePath(fileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to delete backup file {FileName}; row will be removed regardless.", fileName);
        }
    }

    private async Task RunPgToolAsync(string fileName, IReadOnlyList<string> argv, CancellationToken ct)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // pg_dump prefers libpq-style env vars over command-line args so
        // the password never lands in `/proc/<pid>/cmdline`. pg_restore
        // also accepts a libpq connection URI via -d, which we use below.
        psi.Environment["PGHOST"] = builder.Host ?? "localhost";
        psi.Environment["PGPORT"] = builder.Port.ToString();
        psi.Environment["PGUSER"] = builder.Username ?? string.Empty;
        psi.Environment["PGPASSWORD"] = builder.Password ?? string.Empty;
        psi.Environment["PGDATABASE"] = builder.Database ?? string.Empty;
        foreach (var arg in argv) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process only closes our handle: the tool itself would
            // keep running, still writing, after the caller has moved on and
            // released the backup lock. Stop it and wait until it is really gone
            // before letting the cancellation through. See issue #1213.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the cancellation and the kill.
            }
            await process.WaitForExitAsync(CancellationToken.None);
            _logger.LogWarning("{Tool} was cancelled and has been stopped.", fileName);
            throw;
        }
        var stderr = await stderrTask;
        _ = await stdoutTask;

        if (process.ExitCode != 0)
        {
            // Log the raw stderr at Error for operators, but keep it out of the
            // thrown message: the endpoints reflect ex.Message into a redirect
            // query string, and connection errors can echo host/user/database
            // detail. See issue #379.
            _logger.LogError(
                "{Tool} exited {ExitCode}: {Stderr}", fileName, process.ExitCode, stderr);
            throw new InvalidOperationException(
                $"The backup tool failed (exit {process.ExitCode}). See the server logs for details.");
        }
    }

    private async Task ExecuteSqlAsync(string sql, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void RequireSiteAdmin() => _orgContext.RequireSiteAdmin();

    private async Task<BackupInventory> CaptureInventoryAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COALESCE(jsonb_agg(to_jsonb(b)), '[]'::jsonb)::text FROM backups b),
                   (SELECT COALESCE(jsonb_agg(to_jsonb(p)), '[]'::jsonb)::text FROM per_tenant_backups p)
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new BackupInventory(reader.GetString(0), reader.GetString(1));
    }

    /// <summary>
    /// Replaces the restored <c>backups</c> and <c>per_tenant_backups</c> rows
    /// with the ones read before the restore, so the tables again describe the
    /// files actually on disk. Two references may not survive the restore: a
    /// creator who does not exist in the restored database is cleared (the row
    /// is kept), and a per-tenant snapshot whose organisation does not exist is
    /// left out, since there is no organisation left to restore it into.
    /// </summary>
    private async Task RestoreInventoryAsync(BackupInventory inventory, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                DELETE FROM per_tenant_backups;
                DELETE FROM backups;

                INSERT INTO backups
                SELECT * FROM jsonb_populate_recordset(NULL::backups, (
                    SELECT COALESCE(jsonb_agg(
                        CASE WHEN EXISTS (SELECT 1 FROM users u WHERE u.id = (e->>'created_by_user_id')::int)
                             THEN e ELSE e - 'created_by_user_id' END), '[]'::jsonb)
                    FROM jsonb_array_elements(@backups::jsonb) e));

                INSERT INTO per_tenant_backups
                SELECT * FROM jsonb_populate_recordset(NULL::per_tenant_backups, (
                    SELECT COALESCE(jsonb_agg(
                        CASE WHEN EXISTS (SELECT 1 FROM users u WHERE u.id = (e->>'created_by_user_id')::int)
                             THEN e ELSE e - 'created_by_user_id' END), '[]'::jsonb)
                    FROM jsonb_array_elements(@perTenant::jsonb) e
                    WHERE EXISTS (SELECT 1 FROM organizations o WHERE o.id = (e->>'organization_id')::int)));

                SELECT setval(pg_get_serial_sequence('backups', 'id'),
                              GREATEST((SELECT COALESCE(MAX(id), 0) FROM backups), 1));
                SELECT setval(pg_get_serial_sequence('per_tenant_backups', 'id'),
                              GREATEST((SELECT COALESCE(MAX(id), 0) FROM per_tenant_backups), 1));
                """;
            cmd.Parameters.AddWithValue("backups", inventory.Backups);
            cmd.Parameters.AddWithValue("perTenant", inventory.PerTenantBackups);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}

/// <summary>
/// A full restore failed after the database was dropped. Maintenance mode is
/// still on, and stays on until a SiteAdmin restores another backup (or the
/// container restarts). Derives from <see cref="InvalidOperationException"/> so
/// the restore endpoint reports it like any other failed restore. See #1321.
/// </summary>
public sealed class BackupRestoreFailedException : InvalidOperationException
{
    public BackupRestoreFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
