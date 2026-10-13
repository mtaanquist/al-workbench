using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Offsite;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// Pins the schedule-decision rules in <see cref="BackupScheduler"/>: when
/// does it fire, when does it stay quiet. The hosted service's
/// <c>Task.Delay</c> loop isn't exercised — we call <c>TickOnceAsync</c>
/// directly with a deterministic clock so the tests stay fast and stable.
/// Real shelling out to <c>pg_dump</c> is gated by
/// <see cref="PgToolFactAttribute"/>: hosts without the tool skip the
/// "actually wrote a backup" assertions but still exercise the decision
/// surface via the no-op branches.
/// </summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _backupsDir;
    private readonly BackupOptions _options;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 14, 0, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceModeState _maintenance = new();

    public BackupSchedulerTests()
    {
        _backupsDir = Path.Combine(Path.GetTempPath(), "aldt-scheduler-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_backupsDir);
        // This fixture's own directory, handed to the services rather than set
        // as a process-wide variable every other fixture also reads (#733).
        _options = new BackupOptions { Directory = _backupsDir };
        _db.OrgContext.IsSiteAdmin = true;
        _db.OrgContext.CurrentUserId = null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_backupsDir, recursive: true); } catch { /* best effort */ }
        _db.Dispose();
    }

    [Fact]
    public async Task Tick_before_the_scheduled_time_does_not_fire()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(1)); // 01:00 UTC — an hour before the window.

        await TickOnceAsync();

        await using var read = _db.NewContext();
        (await read.Backups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tick_when_the_schedule_is_disabled_does_not_fire()
    {
        await SeedSettingsAsync(enabled: false, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(3)); // 03:00 UTC — past the window, but disabled.

        await TickOnceAsync();

        await using var read = _db.NewContext();
        (await read.Backups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tick_with_a_scheduled_backup_already_in_today_window_does_not_double_fire()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2)); // 02:00 UTC.
        await using (var seed = _db.NewContext())
        {
            // A pre-existing scheduled backup at 02:00 — today's slot is taken.
            seed.Backups.Add(new Backup
            {
                FileName = "already.dump",
                Kind = BackupKind.Scheduled,
                FileSizeBytes = 1,
                CreatedAt = _clock.GetUtcNow().UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        await TickOnceAsync();

        await using var read = _db.NewContext();
        (await read.Backups.CountAsync()).Should().Be(1, "a second tick within the same daily window must not duplicate");
    }

    [PgToolFact]
    public async Task Tick_at_or_after_the_scheduled_time_creates_one_backup_row()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5)); // 02:00:05 UTC.

        await TickOnceAsync();

        await using var read = _db.NewContext();
        var rows = await read.Backups.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle()
            .Which.Kind.Should().Be(BackupKind.Scheduled);
    }

    [PgToolFact]
    public async Task Two_ticks_a_day_apart_create_two_distinct_backups()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(1));

        await TickOnceAsync();
        _clock.Advance(TimeSpan.FromDays(1));
        await TickOnceAsync();

        await using var read = _db.NewContext();
        (await read.Backups.CountAsync(b => b.Kind == BackupKind.Scheduled))
            .Should().Be(2, "the next-day window opens a fresh slot");
    }

    /// <summary>
    /// A failed scheduled dump leaves no file behind and is not tried again on every
    /// one-minute poll for the rest of the day, only after a pause (#1269).
    /// </summary>
    [Fact]
    public async Task A_failed_scheduled_backup_leaves_no_file_and_waits_before_trying_again()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));
        var options = _options with { PgDumpPath = StubPgDump(exitCode: 1, out var runs) };
        var scheduler = NewScheduler();

        await TickOnceAsync(scheduler, options);

        Runs(runs).Should().Be(1);
        Directory.GetFiles(_backupsDir).Should().BeEmpty("a failed dump must not leave a file retention cannot see");
        await using (var read = _db.NewContext())
            (await read.Backups.CountAsync()).Should().Be(0);

        _clock.Advance(TimeSpan.FromMinutes(1));
        await TickOnceAsync(scheduler, options);
        Runs(runs).Should().Be(1, "the next poll does not try again straight away");

        _clock.Advance(BackupScheduler.FailedRunBackoff);
        await TickOnceAsync(scheduler, options);
        Runs(runs).Should().Be(2, "it does try again once the pause is over");
    }

    // ===== The scheduled per-tenant snapshots (#1279) =====

    [Fact]
    public async Task Each_organisation_gets_one_snapshot_per_window_and_system_and_pending_ones_none()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));
        var pending = await SeedOrganizationAsync("pending", isPending: true);
        var served = await SeedOrganizationAsync("served", isPending: false);
        await using (var seed = _db.NewContext())
        {
            // Today's slot for this one is already taken.
            seed.PerTenantBackups.Add(new PerTenantBackup
            {
                OrganizationId = served, FileName = "served-today" + PerTenantBackupService.FileSuffix, FileSizeBytes = 1,
                CreatedAt = _clock.GetUtcNow().UtcDateTime.AddSeconds(-1), Kind = BackupKind.Scheduled,
                SchemaVersion = PerTenantBackupService.CurrentSchemaVersion,
            });
            await seed.SaveChangesAsync();
        }
        var options = _options with { PgDumpPath = StubPgDump(exitCode: 0, out _) };

        await TickOnceAsync(options: options);

        await using var read = _db.NewContext();
        var perOrg = await read.PerTenantBackups.AsNoTracking()
            .GroupBy(b => b.OrganizationId)
            .ToDictionaryAsync(g => g.Key, g => g.Count());
        perOrg.Should().BeEquivalentTo(new Dictionary<int, int>
        {
            [TestDb.OtherOrgId] = 1,
            [served] = 1,
        }, "the system organisation and a pending one get none, and a served slot is not taken twice");
        perOrg.Should().NotContainKey(pending);
    }

    [Fact]
    public async Task Single_tenant_mode_takes_the_backup_but_no_per_tenant_snapshots()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));
        var options = _options with { PgDumpPath = StubPgDump(exitCode: 0, out _) };

        await TickOnceAsync(NewScheduler(singleTenant: true), options);

        await using var read = _db.NewContext();
        (await read.Backups.CountAsync()).Should().Be(1);
        (await read.PerTenantBackups.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_tick_during_a_restore_takes_nothing()
    {
        await SeedSettingsAsync(enabled: true, scheduledTimeUtc: new TimeOnly(2, 0));
        _clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5));
        var options = _options with { PgDumpPath = StubPgDump(exitCode: 0, out var runs) };
        _maintenance.Enter("Restoring from a backup.");

        await TickOnceAsync(options: options);

        Runs(runs).Should().Be(0);
        await using var read = _db.NewContext();
        (await read.Backups.CountAsync()).Should().Be(0);
        (await read.PerTenantBackups.CountAsync()).Should().Be(0);
    }

    private async Task<int> SeedOrganizationAsync(string slug, bool isPending)
    {
        await using var ctx = _db.NewContext();
        var org = new Organization { Name = slug, Slug = slug, IsPending = isPending, CreatedAt = DateTime.UtcNow };
        ctx.Organizations.Add(org);
        await ctx.SaveChangesAsync();
        return org.Id;
    }

    // ===== Fixture helpers =====

    private async Task SeedSettingsAsync(bool enabled, TimeOnly scheduledTimeUtc)
    {
        await using var ctx = _db.NewContext();
        // The MoveSeedToSystemOrg migration ensures the singleton row exists;
        // tests load the database fresh via Migrate so it's already present.
        var settings = await ctx.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1);
        if (settings is null)
        {
            settings = new SystemSettings { Id = 1 };
            ctx.SystemSettings.Add(settings);
        }
        settings.BackupScheduleEnabled = enabled;
        settings.BackupScheduleTimeUtc = scheduledTimeUtc;
        settings.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await ctx.SaveChangesAsync();
    }

    private BackupScheduler NewScheduler(bool singleTenant = false) => new(
        new ServiceCollection().BuildServiceProvider(), _clock,
        new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(singleTenant),
        _maintenance,
        NullLogger<BackupScheduler>.Instance, new WorkerHeartbeatRegistry());

    /// <summary>
    /// A stand-in <c>pg_dump</c>: records each run in <paramref name="counterPath"/>,
    /// writes a few bytes to the <c>-f</c> target the way a real dump starts, and
    /// exits with <paramref name="exitCode"/>. Lets the schedule's failure rules run
    /// without the real tool.
    /// </summary>
    private string StubPgDump(int exitCode, out string counterPath)
    {
        if (OperatingSystem.IsWindows()) Assert.Skip("The stand-in pg_dump is a shell script.");
        var dir = Path.Combine(_backupsDir, "bin");
        Directory.CreateDirectory(dir);
        counterPath = Path.Combine(dir, "runs.txt");
        var script = Path.Combine(dir, "pg_dump");
        File.WriteAllText(script,
            "#!/bin/sh\n"
            + $"echo run >> '{counterPath}'\n"
            + "while [ $# -gt 0 ]; do\n"
            + "  if [ \"$1\" = \"-f\" ]; then shift; printf 'PGDMP' > \"$1\"; fi\n"
            + "  shift\n"
            + "done\n"
            + $"exit {exitCode}\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private static int Runs(string counterPath) =>
        File.Exists(counterPath) ? File.ReadAllLines(counterPath).Length : 0;

    private async Task TickOnceAsync(BackupScheduler? scheduler = null, BackupOptions? options = null)
    {
        scheduler ??= NewScheduler();
        options ??= _options;
        await using var ctx = _db.NewContext();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ctx.Database.GetConnectionString(),
        }).Build();
        var backups = new BackupService(
            ctx, _db.OrgContext, _maintenance, config,
            NullLogger<BackupService>.Instance, _clock, options);
        var perTenant = new PerTenantBackupService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), config,
            NullLogger<PerTenantBackupService>.Instance, _clock, options);
        var systemSettings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);
        var providerFactory = new OffsiteStorageProviderFactory(NullLoggerFactory.Instance);
        var offsite = new OffsiteBackupService(
            ctx, systemSettings, backups, perTenant, providerFactory, NullLogger<OffsiteBackupService>.Instance, _clock,
            DeploymentIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "aldt-test-" + Guid.NewGuid().ToString("N")), NullLogger.Instance));
        await scheduler.TickOnceAsync(ctx, backups, perTenant, offsite, CancellationToken.None);
    }
}
