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

    private async Task TickOnceAsync()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var scheduler = new BackupScheduler(
            sp, _clock,
            new ALDevToolbox.Services.SingleTenant.SingleTenantModeState(false),
            _maintenance,
            NullLogger<BackupScheduler>.Instance, new WorkerHeartbeatRegistry());
        await using var ctx = _db.NewContext();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = ctx.Database.GetConnectionString(),
        }).Build();
        var backups = new BackupService(
            ctx, _db.OrgContext, _maintenance, config,
            NullLogger<BackupService>.Instance, _clock, _options);
        var perTenant = new PerTenantBackupService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), _db.NewOrganizationConfigService(ctx), config,
            NullLogger<PerTenantBackupService>.Instance, _clock, _options);
        var systemSettings = new SystemSettingsService(
            ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, _clock);
        var providerFactory = new OffsiteStorageProviderFactory(NullLoggerFactory.Instance);
        var offsite = new OffsiteBackupService(
            ctx, systemSettings, backups, perTenant, providerFactory, NullLogger<OffsiteBackupService>.Instance, _clock,
            DeploymentIdentity.LoadOrCreate(Path.Combine(Path.GetTempPath(), "aldt-test-" + Guid.NewGuid().ToString("N")), NullLogger.Instance));
        await scheduler.TickOnceAsync(ctx, backups, perTenant, offsite, CancellationToken.None);
    }
}
