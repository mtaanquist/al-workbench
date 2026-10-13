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
    /// Even when pg_restore blows up mid-flight, the <c>finally</c> in
    /// <see cref="BackupService.RestoreAsync"/> must lift maintenance mode —
    /// otherwise the app stays 503 for non-SiteAdmin until someone restarts
    /// the container.
    /// </summary>
    [PgToolFact]
    public async Task RestoreAsync_lifts_maintenance_when_pg_restore_fails()
    {
        var svc = NewService();
        var backup = await svc.CreateAsync(BackupKind.AdHoc);
        // Replace the dump file with garbage. pg_restore will read the header,
        // refuse, and exit non-zero — BackupService should surface that and
        // still leave maintenance mode lifted.
        var path = Path.Combine(_backupsDir, backup.FileName);
        await File.WriteAllBytesAsync(path, new byte[] { 0x00, 0x01, 0x02, 0x03 });

        Func<Task> act = () => svc.RestoreAsync(backup.Id);

        await act.Should().ThrowAsync<InvalidOperationException>();
        _maintenance.IsActive.Should().BeFalse(
            "maintenance mode must be lifted even when pg_restore fails");
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

    private string WriteFakeTool(string name, string body)
    {
        var path = Path.Combine(_backupsDir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body.Replace("\r\n", "\n") + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
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

    private BackupService NewService(BackupOptions? options = null)
    {
        var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = ctx.Database.GetConnectionString(),
            })
            .Build();
        return new BackupService(ctx, _db.OrgContext, _maintenance, config,
            NullLogger<BackupService>.Instance, _clock, options ?? _options);
    }
}
