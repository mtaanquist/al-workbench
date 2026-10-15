using ALDevToolbox.Components.Pages.SiteAdmin;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Offsite;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Issue #970: the backup schedule is stored as a UTC time of day but shown and
/// typed in the organisation's display zone, so the settings page and the backups
/// list agree with each other and with the backup times listed beside them.
/// Europe/Copenhagen is UTC+2 in July and UTC+1 in January; the stored 02:00 UTC
/// is shown as 04:00 and 03:00.
/// </summary>
public sealed class SiteAdminBackupScheduleTests : IDisposable
{
    private static readonly DateTimeOffset Summer = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Winter = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly MaintenanceModeState _maintenance = new();
    private readonly string _deploymentDir = Path.Combine(Path.GetTempPath(), "aldt-test-deploy-" + Guid.NewGuid().ToString("N"));

    public SiteAdminBackupScheduleTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("siteadmin@cronus.example");
        auth.SetRoles("SiteAdmin");
        // BackupService.ListAsync is SiteAdmin-only.
        _db.OrgContext.IsSiteAdmin = true;

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _db.ConnectionString,
            })
            .Build();

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddSingleton<IConfiguration>(config);
        _ctx.Services.AddDataProtection();
        _ctx.Services.AddScoped<SystemSettingsService>();
        _ctx.Services.AddSingleton(_maintenance);
        _ctx.Services.AddScoped<BackupService>();
        _ctx.Services.AddScoped(sp => new PerTenantBackupService(
            sp.GetRequiredService<AppDbContext>(), _db.OrgContext,
            _db.NewQuotaGuard(sp.GetRequiredService<AppDbContext>()),
            _db.NewOrganizationConfigService(sp.GetRequiredService<AppDbContext>()), config,
            NullLogger<PerTenantBackupService>.Instance, TimeProvider.System));
        _ctx.Services.AddSingleton<IOffsiteStorageProviderFactory>(new OffsiteStorageProviderFactory(NullLoggerFactory.Instance));
        _ctx.Services.AddSingleton(DeploymentIdentity.LoadOrCreate(_deploymentDir, NullLogger.Instance));
        _ctx.Services.AddScoped<OffsiteBackupService>();
        _ctx.Services.AddSingleton(sp => new OffsiteRestoreJobs(TimeProvider.System));
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
        try { Directory.Delete(_deploymentDir, recursive: true); } catch { /* best effort */ }
    }

    private void At(DateTimeOffset now, bool singleTenant = false)
    {
        _ctx.Services.AddSingleton<TimeProvider>(new FixedClock(now));
        _ctx.Services.AddSingleton<ISingleTenantMode>(new SingleTenantModeState(singleTenant));
    }

    private Task UseZoneAsync(string zoneId) =>
        _db.NewOrganizationAdminService(_db.NewContext()).SetDisplayTimeZoneAsync(zoneId);

    private async Task<TimeOnly> StoredScheduleAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.SystemSettings.AsNoTracking().Select(s => s.BackupScheduleTimeUtc).SingleAsync();
    }

    [Fact]
    public async Task The_settings_page_shows_the_schedule_in_the_zone_with_the_stored_utc_time_in_the_caption()
    {
        At(Summer);
        await UseZoneAsync("Europe/Copenhagen");
        (await StoredScheduleAsync()).Should().Be(new TimeOnly(2, 0));

        var cut = _ctx.Render<SiteAdminSettingsBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name=BackupScheduleTime]").GetAttribute("value").Should().Be("04:00");
            cut.Find("input[name=BackupScheduleOffsetMinutes]").GetAttribute("value").Should().Be("120");
            cut.Markup.Should().Contain(
                "Stored as 02:00 UTC; the backup runs at the same UTC time all year, so it shifts by an hour in your zone when the clocks change.");
            cut.Markup.Should().Contain("Europe/Copenhagen");
            cut.FindAll("input[name=BackupScheduleTimeUtc]").Should().BeEmpty();
            cut.Find("input[name=BackupScheduleTime]").GetAttribute("aria-label").Should().NotContain("UTC");
        });
    }

    [Fact]
    public async Task In_winter_the_same_stored_time_is_shown_an_hour_earlier()
    {
        At(Winter);
        await UseZoneAsync("Europe/Copenhagen");

        var cut = _ctx.Render<SiteAdminSettingsBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name=BackupScheduleTime]").GetAttribute("value").Should().Be("03:00");
            cut.Find("input[name=BackupScheduleOffsetMinutes]").GetAttribute("value").Should().Be("60");
        });
    }

    [Fact]
    public async Task With_several_organisations_the_caption_says_whose_zone_it_is()
    {
        // TestDb seeds two organisations.
        At(Summer);
        await UseZoneAsync("Europe/Copenhagen");

        var cut = _ctx.Render<SiteAdminSettingsBackups>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(
            "In your organisation's time zone, Europe/Copenhagen; site admins in other organisations see it in theirs."));
    }

    [Fact]
    public async Task In_single_tenant_mode_the_caption_does_not_mention_other_organisations()
    {
        At(Summer, singleTenant: true);
        await UseZoneAsync("Europe/Copenhagen");

        var cut = _ctx.Render<SiteAdminSettingsBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("In Europe/Copenhagen.");
            cut.Markup.Should().NotContain("other organisations");
        });
    }

    [Fact]
    public void Without_a_zone_the_schedule_is_shown_in_utc_and_says_so()
    {
        At(Summer, singleTenant: true);

        var cut = _ctx.Render<SiteAdminSettingsBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[name=BackupScheduleTime]").GetAttribute("value").Should().Be("02:00");
            cut.Markup.Should().Contain("In UTC. Pick a quiet hour.");
            cut.Markup.Should().NotContain("Stored as");
        });
    }

    [Fact]
    public async Task The_backups_list_gives_the_schedule_in_the_same_zone_as_the_backups_it_lists()
    {
        At(Summer);
        await UseZoneAsync("Europe/Copenhagen");
        await using (var ctx = _db.NewContext())
        {
            ctx.Backups.Add(new Backup
            {
                FileName = "aldevtoolbox-20260701-020000.dump",
                FileSizeBytes = 2048,
                CreatedAt = new DateTime(2026, 7, 1, 2, 0, 0, DateTimeKind.Utc),
                Kind = BackupKind.Scheduled,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<SiteAdminBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("A new one is taken daily at");
            var schedule = cut.Find("span[title='02:00 UTC']");
            schedule.TextContent.Should().Be("04:00");
            // The zone is named, because the file names beside it carry the UTC time.
            cut.Find("p.card__sub").TextContent.Should().Contain("daily at 04:00 (Europe/Copenhagen).");
            cut.Markup.Should().NotContain("04:00 UTC").And.NotContain("02:00 UTC.");
            cut.Markup.Should().Contain("2026-07-01 04:00:00", "the listed backup ran at the scheduled time, shown in the same zone");
        });
    }

    /// <summary>
    /// A restore that fails after the drop keeps the site in maintenance mode
    /// (#1321). The SiteAdmin lands back on the backups page, which has to say
    /// so, or nothing tells them the site is still closed to everyone else.
    /// </summary>
    [Fact]
    public void After_a_failed_restore_the_backups_page_says_the_site_is_held_closed()
    {
        At(Summer);
        _maintenance.HoldAfterFailedRestore(
            "Restoring backup aldevtoolbox-20260701T020000000Z-scheduled.dump failed. The database may be empty or only partly loaded.");

        var cut = _ctx.Render<SiteAdminBackups>();

        cut.WaitForAssertion(() =>
        {
            var alerts = cut.FindAll("[role=alert]");
            alerts.Should().ContainSingle("one notice, not a failure message beside a maintenance warning");
            var alert = alerts[0].TextContent;
            alert.Should().Contain("Restoring backup aldevtoolbox-20260701T020000000Z-scheduled.dump failed.");
            alert.Should().Contain("may be empty or only partly loaded");
            alert.Should().Contain("Restore another backup below to reopen it.");
        });
    }

    [Fact]
    public void While_a_restore_runs_the_backups_page_says_the_site_is_closed_until_it_finishes()
    {
        At(Summer);
        _maintenance.Enter("Restoring backup aldevtoolbox-20260701T020000000Z-scheduled.dump");

        var cut = _ctx.Render<SiteAdminBackups>();

        cut.WaitForAssertion(() =>
        {
            var alert = cut.Find("[role=alert]").TextContent;
            alert.Should().Contain("closed to everyone except site administrators until it finishes");
            alert.Should().NotContain("Restore another backup");
        });
    }

    [Fact]
    public void The_backups_page_has_no_maintenance_notice_when_the_site_is_open()
    {
        At(Summer);

        var cut = _ctx.Render<SiteAdminBackups>();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("A complete copy of everything on this site.");
            cut.Markup.Should().NotContain("closed to everyone");
        });
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
