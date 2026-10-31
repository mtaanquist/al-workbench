using System.Diagnostics;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth; // FakeTimeProvider (test double)
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Behavioural tests for <see cref="BackupService"/>. Exercises the full
/// shell-out path against the per-test Postgres instance — so the suite
/// only runs when <c>pg_dump</c> / <c>pg_restore</c> are on PATH (the
/// runtime image ships them; <see cref="PgToolFactAttribute"/> skips when
/// they aren't installed locally).
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _backupsDir;
    private readonly MaintenanceModeState _maintenance = new();
    // Shared by every service the fixture builds, as the DI singleton is (#1321).
    private readonly RestoreInventoryHold _restoreInventory = new();
    // Controllable clock so tests give backups distinct created_at values by
    // advancing it, rather than sleeping on the real clock (flaky at coarse
    // resolution / under CI load). See issue #395.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero));
    private readonly BackupOptions _options;

    public BackupServiceTests()
    {
        _backupsDir = Path.Combine(Path.GetTempPath(), "aldt-backup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupsDir);
        // This fixture's own directory, handed to the service rather than set
        // as a process-wide variable every other fixture also reads (#733).
        _options = new BackupOptions { Directory = _backupsDir };
        // BackupService treats SiteAdmin as required for AdHoc operations.
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_backupsDir, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    [PgToolFact]
    public async Task Create_writes_file_and_inserts_db_row()
    {
        var svc = NewService();

        var backup = await svc.CreateAsync(BackupKind.AdHoc);

        backup.FileName.Should().EndWith(BackupService.BackupFileSuffix);
        backup.Kind.Should().Be(BackupKind.AdHoc);
        File.Exists(Path.Combine(_backupsDir, backup.FileName)).Should().BeTrue();

        await using var ctx = _db.NewContext();
        var stored = await ctx.Backups.AsNoTracking().SingleAsync();
        stored.FileSizeBytes.Should().BeGreaterThan(0);
        stored.IsPinned.Should().BeFalse();
    }

    [PgToolFact]
    public async Task Backup_then_restore_round_trips_a_marker_row()
    {
        var svc = NewService();

        // Mark the DB with a sentinel row before backup.
        await using (var ctx = _db.NewContext())
        {
            ctx.Organizations.Add(new Organization
            {
                Name = "Marker", Slug = "marker-pre-backup",
                IsPending = false, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var backup = await svc.CreateAsync(BackupKind.AdHoc);

        // Drop the marker, write a different one, then restore.
        await using (var ctx = _db.NewContext())
        {
            var marker = await ctx.Organizations.IgnoreQueryFilters()
                .FirstAsync(o => o.Slug == "marker-pre-backup");
            ctx.Organizations.Remove(marker);
            ctx.Organizations.Add(new Organization
            {
                Name = "Replacement", Slug = "marker-post-backup",
                IsPending = false, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        await svc.RestoreAsync(backup.Id);

        await using (var ctx = _db.NewContext())
        {
            (await ctx.Organizations.IgnoreQueryFilters()
                .AnyAsync(o => o.Slug == "marker-pre-backup"))
                .Should().BeTrue("the restore brought back the pre-backup marker");
            (await ctx.Organizations.IgnoreQueryFilters()
                .AnyAsync(o => o.Slug == "marker-post-backup"))
                .Should().BeFalse("post-backup writes are discarded on restore");
        }
        _maintenance.IsActive.Should().BeFalse("maintenance mode is lifted after a successful restore");
    }

    /// <summary>
    /// The restore closes the database to ordinary work (#1571) and then does
    /// its own: the inventory, the migration, the retention sweep. With the
    /// guard installed and nothing else exempting it, it still round-trips.
    /// </summary>
    [PgToolFact]
    public async Task Restore_gets_its_own_work_past_the_maintenance_database_guard()
    {
        var svc = NewService(guarded: true);
        await using (var ctx = _db.NewContext())
        {
            ctx.Organizations.Add(new Organization
            {
                Name = "Marker", Slug = "marker-guarded",
                IsPending = false, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var backup = await svc.CreateAsync(BackupKind.AdHoc);

        await svc.RestoreAsync(backup.Id);

        _maintenance.IsActive.Should().BeFalse();
        _maintenance.ActiveJobs.Should().Be(0);
        await using var check = _db.NewContext();
        (await check.Organizations.IgnoreQueryFilters().AnyAsync(o => o.Slug == "marker-guarded")).Should().BeTrue();
        (await check.Backups.AsNoTracking().AnyAsync(b => b.Id == backup.Id)).Should().BeTrue("the inventory was put back");
    }

    [PgToolFact]
    public async Task Retention_prunes_oldest_unpinned_backups_and_keeps_pinned()
    {
        // Configure retention=3 to keep the test fast.
        await using (var ctx = _db.NewContext())
        {
            var settings = await ctx.SystemSettings.FirstAsync(s => s.Id == 1);
            settings.BackupRetentionCount = 3;
            await ctx.SaveChangesAsync();
        }
        var svc = NewService();

        // Take 5 backups. Pin the oldest so retention can't touch it.
        var first = await svc.CreateAsync(BackupKind.AdHoc);
        await svc.SetPinnedAsync(first.Id, pinned: true);
        // PruneRetentionAsync runs at the tail of CreateAsync. Advance the fake
        // clock so the remaining 4 get strictly-increasing created_at values —
        // deterministic ordering without sleeping on the real clock. See #395.
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(1));
            await svc.CreateAsync(BackupKind.AdHoc);
        }

        await using var read = _db.NewContext();
        var rows = await read.Backups.AsNoTracking()
            .OrderBy(b => b.CreatedAt).ToListAsync();
        // 1 pinned + 3 most-recent unpinned = 4 rows survive.
        rows.Should().HaveCount(4);
        rows.Should().Contain(b => b.Id == first.Id && b.IsPinned,
            "pinned backups are exempt from retention");
        // The first unpinned backup (oldest after the pinned one) must be
        // the one pruned — there were 4 unpinned, retention is 3.
        rows.Count(b => !b.IsPinned).Should().Be(3);
    }

    [PgToolFact]
    public async Task Delete_refuses_pinned_backups()
    {
        var svc = NewService();
        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        await svc.SetPinnedAsync(backup.Id, pinned: true);

        Func<Task> act = () => svc.DeleteAsync(backup.Id);
        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("BackupId");
    }

    /// <summary>
    /// Failure-path coverage for the in-place restore (M21 test gap-fill).
    /// A non-existent ID must fail validation *before* maintenance mode flips
    /// — otherwise an operator's typo would 503 the app for nothing.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_with_unknown_id_throws_without_entering_maintenance()
    {
        // No pg tools required — this path never reaches the shell-out.
        var svc = NewService();

        Func<Task> act = () => svc.RestoreAsync(id: 99_999);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("BackupId");
        _maintenance.IsActive.Should().BeFalse(
            "validation refusal must not leave the app stuck in maintenance mode");
    }

    /// <summary>
    /// A row that exists in the DB but whose file has been deleted from disk
    /// (operator wiped the volume, file got truncated) must also fail before
    /// maintenance flips.
    /// </summary>
    [PgToolFact]
    public async Task RestoreAsync_when_file_missing_throws_without_entering_maintenance()
    {
        var svc = NewService();
        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        // Simulate the on-disk file disappearing while the row stayed in the DB.
        File.Delete(Path.Combine(_backupsDir, backup.FileName));

        Func<Task> act = () => svc.RestoreAsync(backup.Id);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors.Should().ContainKey("BackupId");
        ex.Which.Errors["BackupId"].Should().Contain("missing");
        _maintenance.IsActive.Should().BeFalse(
            "the missing-file refusal must short-circuit before maintenance flips");
    }

    /// <summary>
    /// A file that is no longer the size recorded when it was taken (here cut
    /// to a few bytes) is refused before maintenance mode or the drop, so the
    /// database the site is running on is left alone. See issue #1321.
    /// </summary>
    [PgToolFact]
    public async Task RestoreAsync_refuses_a_file_whose_size_changed_before_dropping_anything()
    {
        var (svc, backup) = await MarkerAndBackupAsync();
        await File.WriteAllBytesAsync(Path.Combine(_backupsDir, backup.FileName), new byte[] { 0x00, 0x01, 0x02, 0x03 });

        Func<Task> act = () => svc.RestoreAsync(backup.Id);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["BackupId"].Should().Contain("not the size it was when it was taken");
        await AssertNothingChangedAsync(backup.Id);
    }

    /// <summary>
    /// A dump cut short well past its header, with a row that agrees (an
    /// off-site copy that was already short when downloaded). Reading only the
    /// table of contents passes such a file and the restore then loads tables
    /// with no rows; reading it end to end refuses it before anything is
    /// dropped. See issue #1321.
    /// </summary>
    [PgToolFact]
    public async Task RestoreAsync_refuses_a_dump_cut_short_past_its_header()
    {
        var (svc, backup) = await MarkerAndBackupAsync();
        var path = Path.Combine(_backupsDir, backup.FileName);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var kept = bytes[..(bytes.Length * 7 / 10)];
        await File.WriteAllBytesAsync(path, kept, TestContext.Current.CancellationToken);
        await SetRecordedSizeAsync(backup.Id, kept.Length);

        Func<Task> act = () => svc.RestoreAsync(backup.Id);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["BackupId"].Should().Contain("can't be read");
        await AssertNothingChangedAsync(backup.Id);
    }

    /// <summary>
    /// A dump damaged part-way through its data, with its size unchanged, is
    /// refused before anything is dropped. See issue #1321.
    /// </summary>
    [PgToolFact]
    public async Task RestoreAsync_refuses_a_dump_damaged_part_way_through()
    {
        var (svc, backup) = await MarkerAndBackupAsync();
        var path = Path.Combine(_backupsDir, backup.FileName);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        // Overwrite a stretch in the middle, where the compressed table data is.
        for (var i = bytes.Length * 4 / 10; i < bytes.Length * 6 / 10; i++) bytes[i] = 0xFF;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);

        Func<Task> act = () => svc.RestoreAsync(backup.Id);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["BackupId"].Should().Contain("can't be read");
        await AssertNothingChangedAsync(backup.Id);
    }

    /// <summary>
    /// A restore that starts while an earlier failed one holds the site closed,
    /// and fails before its own drop (here the drop is refused by the server),
    /// must leave the site held closed, not open the emptied site to everyone.
    /// See issue #1321.
    /// </summary>
    [Fact]
    public async Task A_restore_that_fails_at_the_drop_keeps_an_earlier_hold_in_place()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var tool = WriteFakeTool("fake-pg-restore", "exit 0");
        var backupId = await AddBackupRowWithFileAsync();
        await using (var ctx = _db.NewContext())
        {
            await ctx.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION refuse_drop() RETURNS event_trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'drop refused for the test'; END $$;
                CREATE EVENT TRIGGER refuse_drop ON ddl_command_start
                    WHEN TAG IN ('DROP SCHEMA') EXECUTE FUNCTION refuse_drop();
                """, TestContext.Current.CancellationToken);
        }
        _maintenance.HoldAfterFailedRestore("Restoring backup earlier.dump failed.");
        var held = _maintenance.Capture();
        var svc = NewService(_options with { PgRestorePath = tool });

        try
        {
            Func<Task> act = () => svc.RestoreAsync(backupId);

            (await act.Should().ThrowAsync<InvalidOperationException>())
                .Which.Should().NotBeOfType<BackupRestoreFailedException>("nothing was dropped");
            _maintenance.Capture().Should().Be(held, "the earlier hold comes back exactly as it was");
        }
        finally
        {
            await using var ctx = _db.NewContext();
            await ctx.Database.ExecuteSqlRawAsync("DROP EVENT TRIGGER refuse_drop;", TestContext.Current.CancellationToken);
            _maintenance.Exit();
        }
    }

    /// <summary>
    /// A failure that empties the database can only put back the per-tenant
    /// snapshots whose organisations exist, which is none of them. The next
    /// restore that succeeds must put back the full list read before that
    /// failure, not the degraded one, or those snapshots drop out of the UI
    /// for good. See issue #1321.
    /// </summary>
    [PgToolFact]
    public async Task A_successful_restore_after_a_failed_one_puts_back_the_inventory_from_before_the_failure()
    {
        int userId;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.OtherOrgId, Email = "admin@cronus.example", DisplayName = "CRONUS Admin",
                PasswordHash = "x", Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            userId = user.Id;
            ctx.PerTenantBackups.Add(new PerTenantBackup
            {
                OrganizationId = TestDb.OtherOrgId, FileName = "other-20260515T000000Z-adhoc.tenant.zip",
                FileSizeBytes = 1, CreatedAt = DateTime.UtcNow, Kind = BackupKind.AdHoc, SchemaVersion = 3,
                CreatedByUserId = userId,
            });
            await ctx.SaveChangesAsync();
        }
        // A day on, so the real backup's file name differs from the stand-in's.
        _clock.Advance(TimeSpan.FromDays(1));
        var good = await NewService().CreateAsync(BackupKind.AdHoc);
        var brokenId = await AddBackupRowWithFileAsync();

        var failingTool = WriteFakeTool("failing-pg-restore", """
            [ "$1" = "-f" ] && exit 0
            exit 1
            """);
        Func<Task> fail = () => NewService(_options with { PgRestorePath = failingTool }).RestoreAsync(brokenId);
        await fail.Should().ThrowAsync<BackupRestoreFailedException>();
        await using (var ctx = _db.NewContext())
        {
            (await ctx.PerTenantBackups.IgnoreQueryFilters().AsNoTracking().AnyAsync())
                .Should().BeFalse("its organisation is gone, so the snapshot could not go back after the failure");
        }

        await NewService().RestoreAsync(good.Id);

        _maintenance.IsActive.Should().BeFalse();
        await using (var ctx = _db.NewContext())
        {
            var snapshot = await ctx.PerTenantBackups.IgnoreQueryFilters().AsNoTracking().SingleAsync();
            snapshot.FileName.Should().Be("other-20260515T000000Z-adhoc.tenant.zip");
            snapshot.CreatedByUserId.Should().Be(userId, "its creator is back too");
            (await ctx.Backups.AsNoTracking().Select(b => b.Id).ToListAsync())
                .Should().BeEquivalentTo([good.Id, brokenId]);
        }
    }

    private async Task<(BackupService Service, Backup Backup)> MarkerAndBackupAsync()
    {
        var svc = NewService();
        await using (var ctx = _db.NewContext())
        {
            ctx.Organizations.Add(new Organization
            {
                Name = "Marker", Slug = "marker-before-refused-restore",
                IsPending = false, CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        return (svc, await svc.CreateAsync(BackupKind.AdHoc));
    }

    private async Task SetRecordedSizeAsync(int backupId, long size)
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.Backups.SingleAsync(b => b.Id == backupId);
        row.FileSizeBytes = size;
        await ctx.SaveChangesAsync();
    }

    private async Task AssertNothingChangedAsync(int backupId)
    {
        _maintenance.IsActive.Should().BeFalse("nothing was changed, so the site stays open");
        await using var ctx = _db.NewContext();
        (await ctx.Organizations.IgnoreQueryFilters()
            .AnyAsync(o => o.Slug == "marker-before-refused-restore"))
            .Should().BeTrue("the database was not dropped");
        (await ctx.Backups.AsNoTracking().Select(b => b.Id).ToListAsync())
            .Should().Equal([backupId]);
    }

    /// <summary>
    /// A dump that reads cleanly can still fail during the restore itself
    /// (the server refuses it), after the schema is gone. The site must not reopen on
    /// that, and the backups page must still work so the SiteAdmin can restore
    /// another backup from it; that restore then reopens the site. See #1321.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_that_fails_after_the_drop_stays_in_maintenance_and_can_restore_another_backup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var failFlag = Path.Combine(_backupsDir, "fail-restore");
        // Stand-in pg_restore: the read-through check always passes; the
        // restore itself fails while the flag file exists, having restored nothing.
        var tool = WriteFakeTool("fake-pg-restore", $"""
            [ "$1" = "-f" ] && exit 0
            [ -f '{failFlag}' ] && exit 1
            exit 0
            """);
        await File.WriteAllTextAsync(failFlag, "", TestContext.Current.CancellationToken);

        var brokenId = await AddBackupRowWithFileAsync();
        int otherId;
        await using (var ctx = _db.NewContext())
        {
            var other = new Backup
            {
                FileName = "aldevtoolbox-20260515T000000000Z-adhoc" + BackupService.BackupFileSuffix,
                FileSizeBytes = 15,
                CreatedAt = _clock.GetUtcNow().UtcDateTime.AddDays(1),
                Kind = BackupKind.AdHoc,
                IsPinned = true,
            };
            ctx.Backups.Add(other);
            await ctx.SaveChangesAsync();
            otherId = other.Id;
        }
        await File.WriteAllTextAsync(
            Path.Combine(_backupsDir, "aldevtoolbox-20260515T000000000Z-adhoc" + BackupService.BackupFileSuffix),
            "not a real dump", TestContext.Current.CancellationToken);
        var svc = NewService(_options with { PgRestorePath = tool });

        Func<Task> act = () => svc.RestoreAsync(brokenId);

        var ex = await act.Should().ThrowAsync<BackupRestoreFailedException>();
        ex.Which.Message.Should().Contain("Restore another backup");
        _maintenance.IsActive.Should().BeTrue("the database was dropped and nothing usable replaced it");
        _maintenance.IsHeldAfterFailedRestore.Should().BeTrue();
        _maintenance.Reason.Should().Contain("failed");

        // The schema was migrated and the inventory put back, so the page the
        // SiteAdmin lands on still lists every backup.
        var listed = await svc.ListAsync();
        listed.Select(b => b.Id).Should().BeEquivalentTo([brokenId, otherId]);
        listed.Single(b => b.Id == otherId).IsPinned.Should().BeTrue();

        File.Delete(failFlag);
        await NewService(_options with { PgRestorePath = tool }).RestoreAsync(otherId);

        _maintenance.IsActive.Should().BeFalse("a restore that completes reopens the site");
    }

    /// <summary>
    /// The restore POST is bound to the browser request. If the SiteAdmin
    /// closes the tab (or a proxy drops the long request) after the schema has
    /// been dropped, maintenance mode must stay on and the restore must run to
    /// the end, not hand a half-loaded database back to every user while an
    /// orphaned pg_restore keeps writing. See issue #1213.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_keeps_maintenance_on_until_pg_restore_finishes_when_the_caller_cancels()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var started = Path.Combine(_backupsDir, "restore-started");
        var release = Path.Combine(_backupsDir, "restore-release");
        // Stand-in pg_restore: announces itself, then waits until the test lets
        // it finish (or the fixture directory goes away, so it never outlives the test).
        var tool = WriteFakeTool("fake-pg-restore", $"""
            [ "$1" = "-f" ] && exit 0
            : > '{started}'
            while [ -d '{_backupsDir}' ] && [ ! -f '{release}' ]; do sleep 0.05; done
            exit 0
            """);
        var backupId = await AddBackupRowWithFileAsync();
        var svc = NewService(_options with { PgRestorePath = tool });
        using var cts = new CancellationTokenSource();

        var restore = svc.RestoreAsync(backupId, cts.Token);
        await WaitForFileAsync(started);
        _maintenance.IsActive.Should().BeTrue();

        await cts.CancelAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);

        restore.IsCompleted.Should().BeFalse("the restore keeps going after the caller has gone away");
        _maintenance.IsActive.Should().BeTrue("maintenance mode stays on while pg_restore is still running");

        await File.WriteAllTextAsync(release, "", TestContext.Current.CancellationToken);
        await restore.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        _maintenance.IsActive.Should().BeFalse("maintenance mode is lifted once pg_restore has exited");
    }

    /// <summary>
    /// A restore rewinds the database but not the backups directory. The
    /// stand-in pg_restore restores nothing, which is the extreme of a dump
    /// taken by an older version: the schema has to come back from this
    /// version's migrations (#1255), the inventory of backup files has to
    /// survive (#1254), and the in-memory settings have to follow the restored
    /// row rather than keep what was there before (#1255).
    /// </summary>
    [Fact]
    public async Task RestoreAsync_migrates_keeps_the_backup_inventory_and_reloads_settings()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var tool = WriteFakeTool("fake-pg-restore", "exit 0");

        var restoredId = await AddBackupRowWithFileAsync();
        int newerId;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = "admin@cronus.example", DisplayName = "CRONUS Admin",
                PasswordHash = "x", Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            var newer = new Backup
            {
                FileName = "aldevtoolbox-20260515T000000000Z-adhoc" + BackupService.BackupFileSuffix,
                FileSizeBytes = 15,
                CreatedAt = _clock.GetUtcNow().UtcDateTime.AddDays(1),
                CreatedByUserId = user.Id,
                Kind = BackupKind.AdHoc,
                IsPinned = true,
                OffsiteObjectKey = "aldevtoolbox-20260515T000000000Z-adhoc.dump",
            };
            ctx.Backups.Add(newer);
            // The stand-in pg_restore restores nothing, so the database it leaves is a
            // freshly migrated one: the system org is the only organisation in it.
            ctx.PerTenantBackups.Add(new PerTenantBackup
            {
                OrganizationId = TestDb.SystemOrgId, FileName = "default-20260515T000000Z-adhoc.tenant.zip",
                FileSizeBytes = 1, CreatedAt = DateTime.UtcNow, Kind = BackupKind.AdHoc, SchemaVersion = 3,
            });
            ctx.PerTenantBackups.Add(new PerTenantBackup
            {
                OrganizationId = TestDb.OtherOrgId, FileName = "other-20260515T000000Z-adhoc.tenant.zip",
                FileSizeBytes = 1, CreatedAt = DateTime.UtcNow, Kind = BackupKind.AdHoc, SchemaVersion = 3,
            });
            await ctx.SaveChangesAsync();
            newerId = newer.Id;
        }

        var mcp = new ALDevToolbox.Services.Mcp.McpAvailabilityState();
        var tools = new ALDevToolbox.Services.Tools.ToolAvailabilityState(mcp);
        tools.Set([ALDevToolbox.Domain.Tools.ToolKey.Piper]);
        var svc = NewService(_options with { PgRestorePath = tool }, mcp, tools);

        await svc.RestoreAsync(restoredId);

        await using (var ctx = _db.NewContext())
        {
            var rows = await ctx.Backups.AsNoTracking().OrderBy(b => b.Id).ToListAsync();
            rows.Select(b => b.Id).Should().Equal([restoredId, newerId],
                "the restored backup and every newer one must still be listed");
            var newer = rows.Single(b => b.Id == newerId);
            newer.IsPinned.Should().BeTrue();
            newer.OffsiteObjectKey.Should().Be("aldevtoolbox-20260515T000000000Z-adhoc.dump");
            newer.CreatedByUserId.Should().BeNull("the creator does not exist in the restored database");

            (await ctx.PerTenantBackups.AsNoTracking().Select(b => b.OrganizationId).ToListAsync())
                .Should().Equal([TestDb.SystemOrgId], "a snapshot whose organisation is not in the restored database has nowhere to go");

            // The identity sequence is realigned past the re-inserted ids.
            var fresh = new Backup { FileName = "after.dump", CreatedAt = DateTime.UtcNow, Kind = BackupKind.AdHoc };
            ctx.Backups.Add(fresh);
            await ctx.SaveChangesAsync();
            fresh.Id.Should().BeGreaterThan(newerId);
        }

        tools.IsSiteEnabled(ALDevToolbox.Domain.Tools.ToolKey.Piper)
            .Should().BeTrue("the in-memory tool toggles must follow the restored settings row");
        _maintenance.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// An ad-hoc backup cancelled mid-dump must stop pg_dump rather than leave
    /// it running unsupervised after the backup lock has been released, and
    /// must not leave the partial file behind. See issue #1213.
    /// </summary>
    [Fact]
    public async Task CreateAsync_stops_pg_dump_and_removes_the_partial_file_when_cancelled()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_dump.");
        var pidFile = Path.Combine(_backupsDir, "dump-pid");
        // Stand-in pg_dump: creates the -f target (the last argument), records
        // its pid, then becomes a long sleep that only a kill will end.
        var tool = WriteFakeTool("fake-pg-dump", $"""
            for a; do last="$a"; done
            : > "$last"
            echo $$ > '{pidFile}.tmp' && mv '{pidFile}.tmp' '{pidFile}'
            exec sleep 300
            """);
        var svc = NewService(_options with { PgDumpPath = tool });
        using var cts = new CancellationTokenSource();

        var create = svc.CreateAsync(BackupKind.AdHoc, cts.Token);
        await WaitForFileAsync(pidFile);
        var pid = int.Parse((await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Trim());

        try
        {
            await cts.CancelAsync();
            Func<Task> act = () => create.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<OperationCanceledException>();

            Directory.Exists($"/proc/{pid}").Should().BeFalse("the cancelled pg_dump must not keep running");
            Directory.GetFiles(_backupsDir, "*" + BackupService.BackupFileSuffix)
                .Should().BeEmpty("the partial dump is removed");
            await using var ctx = _db.NewContext();
            (await ctx.Backups.AsNoTracking().AnyAsync(TestContext.Current.CancellationToken))
                .Should().BeFalse();
        }
        finally
        {
            // Without the fix the stand-in would outlive the test by five minutes.
            try { Process.GetProcessById(pid).Kill(); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// One backup or restore at a time (#370, #371): while another session holds
    /// the shared lock a backup is refused at once rather than queued behind it,
    /// and the next one after the lock is released goes through.
    /// </summary>
    [Fact]
    public async Task CreateAsync_is_refused_while_another_backup_or_restore_holds_the_lock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_dump.");
        var tool = WriteFakeTool("fake-pg-dump", """
            for a; do last="$a"; done
            echo dump > "$last"
            """);
        var svc = NewService(_options with { PgDumpPath = tool });

        await using (await HoldBackupLockAsync())
        {
            Func<Task> act = () => svc.CreateAsync(BackupKind.AdHoc);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Another backup or restore is already in progress*");
            Directory.GetFiles(_backupsDir, "*" + BackupService.BackupFileSuffix).Should().BeEmpty();
        }

        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        File.Exists(Path.Combine(_backupsDir, backup.FileName)).Should().BeTrue("the lock is free again");
    }

    /// <summary>
    /// A restore that finds the lock taken must refuse before it closes the site
    /// or drops anything: the holder may be a backup reading the schema, or
    /// another restore that has already dropped it.
    /// </summary>
    [Fact]
    public async Task RestoreAsync_is_refused_while_another_backup_or_restore_holds_the_lock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var tool = WriteFakeTool("fake-pg-restore", "exit 0");
        var backupId = await AddBackupRowWithFileAsync();
        var svc = NewService(_options with { PgRestorePath = tool });

        await using (await HoldBackupLockAsync())
        {
            Func<Task> act = () => svc.RestoreAsync(backupId);

            (await act.Should().ThrowAsync<InvalidOperationException>()
                    .WithMessage("Another backup or restore is already in progress*"))
                .Which.Should().NotBeOfType<BackupRestoreFailedException>();
        }

        _maintenance.IsActive.Should().BeFalse("a refused restore never closes the site");
        await using var ctx = _db.NewContext();
        (await ctx.Backups.AsNoTracking().AnyAsync(b => b.Id == backupId, TestContext.Current.CancellationToken))
            .Should().BeTrue("nothing was dropped");
    }

    /// <summary>
    /// While a failed restore holds the site closed, the database is empty or
    /// half-loaded. A backup of it would be useless, and the retention prune that
    /// follows would delete real backups to make room for it. So it is refused,
    /// and nothing is written or pruned (#1610).
    /// </summary>
    [Fact]
    public async Task CreateAsync_is_refused_while_a_failed_restore_holds_the_site_closed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_dump.");
        var tool = WriteFakeTool("fake-pg-dump", """
            for a; do last="$a"; done
            echo dump > "$last"
            """);
        await using (var ctx = _db.NewContext())
        {
            var settings = await ctx.SystemSettings.FirstAsync(s => s.Id == 1, TestContext.Current.CancellationToken);
            settings.BackupRetentionCount = 1;
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var existingId = await AddBackupRowWithFileAsync();
        _clock.Advance(TimeSpan.FromSeconds(1));
        var svc = NewService(_options with { PgDumpPath = tool });
        _maintenance.HoldAfterFailedRestore("Restoring backup earlier.dump failed.");

        Func<Task> act = () => svc.CreateAsync(BackupKind.AdHoc);

        var ex = await act.Should().ThrowAsync<PlanValidationException>();
        ex.Which.Errors["BackupId"].Should().Contain("paused").And.Contain("Nothing was deleted");
        Directory.GetFiles(_backupsDir, "*-adhoc" + BackupService.BackupFileSuffix)
            .Should().ContainSingle("no dump was written and the existing one was not pruned");
        await using (var read = _db.NewContext())
        {
            (await read.Backups.AsNoTracking().Select(b => b.Id).ToListAsync(TestContext.Current.CancellationToken))
                .Should().Equal([existingId]);
        }
        _maintenance.IsHeldAfterFailedRestore.Should().BeTrue("a refused backup leaves the hold alone");

        // Once the site reopens, backups run again, and retention with them.
        _maintenance.Exit();
        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        await using var after = _db.NewContext();
        (await after.Backups.AsNoTracking().Select(b => b.Id).ToListAsync(TestContext.Current.CancellationToken))
            .Should().Equal([backup.Id]);
    }

    /// <summary>
    /// The readability check reads the whole dump and can take minutes. The restore
    /// holds the backup lock through it, so a scheduled backup cannot run its
    /// retention prune meanwhile and delete the file the check has just passed,
    /// which would leave the restore to drop the schema and then fail (#1610).
    /// </summary>
    [Fact]
    public async Task RestoreAsync_holds_the_backup_lock_while_it_checks_the_file()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var checking = Path.Combine(_backupsDir, "checking");
        var release = Path.Combine(_backupsDir, "release");
        // Stand-in pg_restore: the read-through check signals it has started and
        // waits to be released; the restore itself succeeds.
        var tool = WriteFakeTool("fake-pg-restore", $"""
            if [ "$1" = "-f" ]; then
              touch '{checking}'
              while [ ! -f '{release}' ]; do sleep 0.05; done
            fi
            exit 0
            """);
        var backupId = await AddBackupRowWithFileAsync();

        var restore = NewService(_options with { PgRestorePath = tool }).RestoreAsync(backupId);
        try
        {
            await WaitForFileAsync(checking);

            Func<Task> backup = () => NewService().CreateAsync(BackupKind.Scheduled);
            await backup.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Another backup or restore is already in progress*");
        }
        finally
        {
            await File.WriteAllTextAsync(release, "", TestContext.Current.CancellationToken);
        }

        await restore;
        _maintenance.IsActive.Should().BeFalse();
    }

    private Task<BackupLockHolder> HoldBackupLockAsync() =>BackupLockHolder.AcquireAsync(_db.ConnectionString);

    private string WriteFakeTool(string name, string body)
    {
        var path = Path.Combine(_backupsDir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static ReleaseImportJob ImportJob(int releaseId) => new(
        releaseId,
        AmbientOrganizationScope.OrganizationIdentity.ForOrganization(TestDb.DefaultOrgId, isSystem: false),
        new ReleaseImportSource.Url("https://example.invalid/dvd.zip"));

    /// <summary>
    /// A stand-in pg_restore. The read-through check always passes, and the restore
    /// itself leaves a marker file so a test can tell whether the drop and load ran.
    /// </summary>
    private (string Tool, string Restored) MarkingRestoreTool()
    {
        var restored = Path.Combine(_backupsDir, "restored");
        var tool = WriteFakeTool("fake-pg-restore", $"""
            [ "$1" = "-f" ] && exit 0
            touch '{restored}'
            exit 0
            """);
        return (tool, restored);
    }

    /// <summary>
    /// An import, build or sweep that keeps running past the wait would write into
    /// the schema the restore drops and reloads. So the restore is refused before
    /// the drop, with a message on the backup field that says what to wait for, and
    /// the site reopens (#1538).
    /// </summary>
    [Fact]
    public async Task RestoreAsync_is_refused_when_a_running_job_does_not_finish_in_time()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var (tool, restored) = MarkingRestoreTool();
        var backupId = await AddBackupRowWithFileAsync();
        _maintenance.BeginJobRegardless(); // An import that is mid-ingest.
        try
        {
            Func<Task> act = () => NewService(_options with
            {
                PgRestorePath = tool,
                ActiveJobsWait = TimeSpan.FromMilliseconds(200),
            }).RestoreAsync(backupId);

            var ex = await act.Should().ThrowAsync<PlanValidationException>();
            ex.Which.Errors["BackupId"].Should().Contain("still running")
                .And.Contain("until imports and builds have finished")
                .And.Contain("a page that is still loading or saving", "a long page query is counted too (#1571)");
        }
        finally
        {
            _maintenance.EndJob();
        }
        _maintenance.IsActive.Should().BeFalse("nothing was changed, so the site reopens");
        _maintenance.Epoch.Should().Be(0, "nothing was replaced, so queued jobs stay valid");
        File.Exists(restored).Should().BeFalse("nothing was dropped or restored");
        await using var ctx = _db.NewContext();
        (await ctx.Backups.AsNoTracking().Select(b => b.Id).ToListAsync(TestContext.Current.CancellationToken))
            .Should().Equal([backupId], "the database was not dropped");
    }

    /// <summary>
    /// A job that finishes within the wait does not stop the restore: with maintenance
    /// on nothing new starts, so once it is done the restore goes ahead (#1538).
    /// </summary>
    [Fact]
    public async Task RestoreAsync_waits_for_a_running_job_to_finish_and_then_restores()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var (tool, restored) = MarkingRestoreTool();
        var backupId = await AddBackupRowWithFileAsync();
        _maintenance.BeginJobRegardless();
        // The job finishes as soon as the restore has closed the site.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _maintenance.Entered += () => closed.TrySetResult();
        var finisher = Task.Run(async () =>
        {
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            File.Exists(restored).Should().BeFalse("the restore waits for the job before it drops anything");
            _maintenance.EndJob();
        }, TestContext.Current.CancellationToken);

        await NewService(_options with { PgRestorePath = tool }).RestoreAsync(backupId);
        await finisher;

        File.Exists(restored).Should().BeTrue();
        _maintenance.IsActive.Should().BeFalse("a restore that completes reopens the site");
    }

    /// <summary>
    /// A webhook delivery that arrives while a failed restore holds the site closed is
    /// put back on the queue by its worker again and again until the site reopens.
    /// That deferral touches no database and must not count as running work, or no
    /// restore could ever start, including the one that would reopen the site (#1538,
    /// #1321).
    /// </summary>
    [Fact]
    public async Task A_webhook_deferred_during_maintenance_does_not_stop_the_restore()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var (tool, restored) = MarkingRestoreTool();
        var backupId = await AddBackupRowWithFileAsync();
        _maintenance.HoldAfterFailedRestore("Restoring backup earlier.dump failed.");
        var queue = new ALDevToolbox.Services.GitHub.GitHubWebhookQueue();
        using var services = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection());
        var worker = new ALDevToolbox.Services.GitHub.GitHubPullRequestBuildWorker(
            queue, services, _maintenance,
            NullLogger<ALDevToolbox.Services.GitHub.GitHubPullRequestBuildWorker>.Instance,
            new ALDevToolbox.Services.Workers.WorkerHeartbeatRegistry())
        {
            MaintenanceRetryDelay = TimeSpan.FromMilliseconds(20),
        };
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        worker.Deferring += _ => deferred.TrySetResult();
        queue.TryEnqueue(new ALDevToolbox.Services.GitHub.GitHubPushJob(
            1, "cronus/app", "https://github.com/cronus/app.git", "delivery-1", "main", "abc", "000", "main",
            "sam", Forced: false, Deleted: false, DateTime.UtcNow, 0, [], DocumentationOnly: false)).Should().BeTrue();

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await deferred.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await NewService(_options with { PgRestorePath = tool, ActiveJobsWait = TimeSpan.FromSeconds(5) })
                .RestoreAsync(backupId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        File.Exists(restored).Should().BeTrue("the deferring webhook did not hold the restore up");
        _maintenance.IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Jobs still queued do not hold the restore up, and they are not run against the
    /// restored database afterwards: they were queued for releases, builds and
    /// solutions of the database that was replaced. A job queued after the restore runs
    /// as normal (#1538).
    /// </summary>
    [Fact]
    public async Task Jobs_queued_before_a_restore_are_dropped_once_it_completes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var (tool, _) = MarkingRestoreTool();
        var backupId = await AddBackupRowWithFileAsync();
        var imports = new ReleaseImportQueue(_maintenance);
        var builds = new ProjectBuildQueue(ProjectBuildQueue.MaxConcurrency, _maintenance);
        var discovery = new ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue(_maintenance);
        var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(TestDb.DefaultOrgId, isSystem: false);
        await imports.EnqueuePacedAsync(ImportJob(41) with { JobRowId = 41 }, TestContext.Current.CancellationToken);
        builds.Enqueue(ImportJob(42) with { BuildOrder = ProjectBuildOrder.Unordered });
        discovery.TryEnqueue(new(43, identity)).Should().BeTrue();

        await NewService(_options with { PgRestorePath = tool }).RestoreAsync(backupId);

        imports.Reader.TryRead(out _).Should().BeFalse("the import was queued against the old database");
        builds.Reader.TryRead(out _).Should().BeFalse("so was the build");
        builds.WaitingCount.Should().Be(0);
        discovery.Reader.TryRead(out _).Should().BeFalse("and the discovery");
        discovery.IsInFlight(43).Should().BeFalse("a dropped discovery frees its solution for the next request");

        // The paced slot the dropped import held is free again, and new work runs.
        await imports.EnqueuePacedAsync(ImportJob(44) with { JobRowId = 44 }, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        imports.Reader.TryRead(out var fresh).Should().BeTrue();
        fresh!.ReleaseId.Should().Be(44);
        discovery.TryEnqueue(new(43, identity)).Should().BeTrue();
        discovery.Reader.TryRead(out _).Should().BeTrue();
    }

    /// <summary>
    /// The lock timeout for the restore's migrate is added to the startup options the
    /// operator may already have in the connection string, not put in their place.
    /// </summary>
    [Fact]
    public void The_restore_migrate_keeps_the_operators_connection_options()
    {
        var plain = new Npgsql.NpgsqlConnectionStringBuilder(
            BackupService.WithRestoreMigrateLockTimeout("Host=db;Database=app"));
        plain.Options.Should().Be("-c lock_timeout=60000");

        var withOptions = new Npgsql.NpgsqlConnectionStringBuilder(
            BackupService.WithRestoreMigrateLockTimeout("Host=db;Database=app;Options=-c statement_timeout=0"));
        withOptions.Options.Should().Be("-c statement_timeout=0 -c lock_timeout=60000");
    }

    /// <summary>
    /// A restore that fails after the drop has replaced the database just as much, so
    /// the epoch moves on and what was queued before is dropped too (#1538).
    /// </summary>
    [Fact]
    public async Task A_restore_that_fails_after_the_drop_still_drops_jobs_queued_before_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell script as a stand-in for pg_restore.");
        var tool = WriteFakeTool("fake-pg-restore", """
            [ "$1" = "-f" ] && exit 0
            exit 1
            """);
        var backupId = await AddBackupRowWithFileAsync();
        var imports = new ReleaseImportQueue(_maintenance);
        await imports.EnqueueAsync(ImportJob(45), TestContext.Current.CancellationToken);

        Func<Task> act = () => NewService(_options with { PgRestorePath = tool }).RestoreAsync(backupId);

        await act.Should().ThrowAsync<BackupRestoreFailedException>();
        _maintenance.IsHeldAfterFailedRestore.Should().BeTrue();
        _maintenance.Epoch.Should().Be(1);
        imports.Reader.TryRead(out _).Should().BeFalse();
    }

    private async Task<int> AddBackupRowWithFileAsync()
    {
        const string fileName = "aldevtoolbox-20260514T000000000Z-adhoc" + BackupService.BackupFileSuffix;
        await File.WriteAllTextAsync(Path.Combine(_backupsDir, fileName), "not a real dump");
        await using var ctx = _db.NewContext();
        var row = new Backup
        {
            FileName = fileName,
            FileSizeBytes = 15,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
            Kind = BackupKind.AdHoc,
        };
        ctx.Backups.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private static async Task WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{path} never appeared.");
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
    }

    private BackupService NewService(
        BackupOptions? options = null,
        ALDevToolbox.Services.Mcp.McpAvailabilityState? mcp = null,
        ALDevToolbox.Services.Tools.ToolAvailabilityState? tools = null,
        bool guarded = false)
    {
        var ctx = guarded
            ? new ALDevToolbox.Data.AppDbContext(
                new DbContextOptionsBuilder<ALDevToolbox.Data.AppDbContext>()
                    .UseNpgsql(_db.ConnectionString)
                    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                    .AddInterceptors(
                        TestDb.NewAuditInterceptor(),
                        // Not a SiteAdmin and no request: only the restore's own
                        // allowance can get its commands past the guard.
                        new ALDevToolbox.Data.MaintenanceDatabaseGuard(_maintenance, new AmbientOrganizationContext()))
                    .Options,
                _db.OrgContext)
            : _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ctx.Database.GetConnectionString(),
            })
            .Build();
        var systemSettings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock,
            mcpAvailability: mcp, toolAvailability: tools);
        return new BackupService(ctx, _db.OrgContext, _maintenance, config,
            NullLogger<BackupService>.Instance, _clock, options ?? _options, systemSettings, _restoreInventory);
    }
}
