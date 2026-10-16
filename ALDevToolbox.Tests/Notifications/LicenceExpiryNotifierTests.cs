using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// The licence expiry notices for on-premises environments (#1369): 30 days ahead, 7 days
/// ahead and once expired, each sent once, and started again by a new licence.
/// </summary>
public sealed class LicenceExpiryNotifierTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _owner = null!;
    private int _instanceId;
    private int _environmentId;

    public LicenceExpiryNotifierTests()
    {
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData(45, null)]
    [InlineData(31, null)]
    [InlineData(30, 30)]
    [InlineData(8, 30)]
    [InlineData(7, 7)]
    [InlineData(1, 7)]
    [InlineData(0, 0)]
    [InlineData(-3, 0)]
    public void Each_day_falls_in_the_nearest_threshold(int daysLeft, int? threshold) =>
        LicenceExpiryNotifier.ThresholdFor(daysLeft).Should().Be(threshold);

    [Fact]
    public async Task Each_threshold_is_notified_once()
    {
        await SetExpiryAsync(Today.AddDays(20));

        (await RunAsync()).Should().Be(1, "inside 30 days");
        (await RunAsync()).Should().Be(0, "the 30-day notice has gone");

        _clock.Advance(TimeSpan.FromDays(14)); // 6 days left
        (await RunAsync()).Should().Be(1, "inside 7 days");
        (await RunAsync()).Should().Be(0);

        _clock.Advance(TimeSpan.FromDays(6)); // the day it expires
        (await RunAsync()).Should().Be(1, "expired");
        _clock.Advance(TimeSpan.FromDays(3));
        (await RunAsync()).Should().Be(0, "expired is said once");

        (await TitlesAsync()).Should().Equal(
            "Licence expires in 20 days: CRONUS Coffee - Production",
            "Licence expires in 6 days: CRONUS Coffee - Production",
            "Licence expired: CRONUS Coffee - Production");
    }

    [Fact]
    public async Task Someone_who_chose_email_for_solutions_gets_the_notice_by_email()
    {
        await using (var ctx = _db.NewContext())
        {
            _db.OrgContext.CurrentUserId = _owner.Id;
            await new NotificationPreferenceService(ctx, _db.OrgContext, _clock, NullLogger<NotificationPreferenceService>.Instance)
                .SetEmailForCurrentUserAsync(NotificationCategory.Solutions, NotificationDelivery.Immediately);
            _db.OrgContext.CurrentUserId = null;
        }
        await SetExpiryAsync(Today.AddDays(30));

        (await RunAsync()).Should().Be(1);

        var sent = _email.Sent.Single();
        sent.To.Should().Be("owner@cronus.example");
        sent.Subject.Should().Be("Licence expires in 30 days: CRONUS Coffee - Production");
    }

    [Fact]
    public async Task A_licence_first_seen_inside_a_week_sends_only_the_nearest_notice()
    {
        await SetExpiryAsync(Today.AddDays(5));

        (await RunAsync()).Should().Be(1);
        (await RunAsync()).Should().Be(0);

        (await TitlesAsync()).Should().Equal("Licence expires in 5 days: CRONUS Coffee - Production");
    }

    [Fact]
    public async Task A_new_licence_starts_the_thresholds_again()
    {
        await SetExpiryAsync(Today.AddDays(10));
        (await RunAsync()).Should().Be(1);

        // The customer's new licence runs another year: nothing to say, and the old notice
        // is forgotten so the new one's turn comes.
        await SetExpiryAsync(Today.AddDays(365));
        (await RunAsync()).Should().Be(0);
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == _environmentId);
            env.LicenseExpiryNoticeFor.Should().BeNull();
            env.LicenseExpiryNoticeDays.Should().BeNull();
        }

        _clock.Advance(TimeSpan.FromDays(340)); // 25 days left
        (await RunAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_licence_with_no_expiry_or_far_off_sends_nothing()
    {
        await SetExpiryAsync(null);
        (await RunAsync()).Should().Be(0);

        await SetExpiryAsync(Today.AddDays(200));
        (await RunAsync()).Should().Be(0);
        (await TitlesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_notice_that_fails_to_send_is_not_lost_and_goes_on_the_next_sweep()
    {
        await SetExpiryAsync(Today.AddDays(20));

        (await RunAsync(failSend: true)).Should().Be(0, "the send failed");
        await using (var ctx = _db.NewContext())
        {
            var env = await ctx.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Id == _environmentId);
            env.LicenseExpiryNoticeFor.Should().BeNull("the claim is put back");
            env.LicenseExpiryNoticeDays.Should().BeNull();
        }
        (await TitlesAsync()).Should().BeEmpty();

        (await RunAsync()).Should().Be(1, "the next sweep sends it");
        (await TitlesAsync()).Should().Equal("Licence expires in 20 days: CRONUS Coffee - Production");
    }

    [Fact]
    public async Task Days_are_counted_on_the_solutions_calendar()
    {
        // 23:30 UTC on 15 October is already 16 October in Copenhagen, so a licence that
        // runs out on 16 October has expired there, while UTC would still say one day left.
        _clock.Advance(TimeSpan.FromHours(14.5)); // 23:30 UTC
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjects.ExecuteUpdateAsync(s => s.SetProperty(p => p.BcTimeZone, "Europe/Copenhagen"));
        }
        await SetExpiryAsync(new DateOnly(2026, 10, 16));

        (await RunAsync()).Should().Be(1);

        (await TitlesAsync()).Should().Equal("Licence expired: CRONUS Coffee - Production");
    }

    [Fact]
    public async Task An_unlinked_environment_is_not_notified()
    {
        await SetExpiryAsync(Today.AddDays(3));
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == _environmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.MissingSince, Start.UtcDateTime));
        }

        (await RunAsync()).Should().Be(0);
    }

    // ---------------------------------------------------------------- helpers

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    /// <summary>The owner's in-app notifications, oldest first. Email for solutions is off unless chosen.</summary>
    private async Task<List<string>> TitlesAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.UserNotifications.AsNoTracking()
            .Where(n => n.UserId == _owner.Id)
            .OrderBy(n => n.Id)
            .Select(n => n.Title)
            .ToListAsync();
    }

    /// <param name="failSend">Makes reading the followers fail, so the notice cannot be sent.</param>
    private async Task<int> RunAsync(bool failSend = false)
    {
        await using var ctx = _db.NewContext();
        await using var followsCtx = failSend ? _db.NewContext(new FailingReads()) : _db.NewContext();
        var preferences = new NotificationPreferenceService(ctx, _db.OrgContext, _clock, NullLogger<NotificationPreferenceService>.Instance);
        var notifications = new NotificationService(
            ctx, _db.NewContextFactory(), preferences, _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin(Origin), _db.OrgContext, _clock, NullLogger<NotificationService>.Instance);
        var follows = new ProjectFollowService(followsCtx, _db.OrgContext, new ProjectAccess(followsCtx, _db.OrgContext), _clock,
            NullLogger<ProjectFollowService>.Instance);
        return await new LicenceExpiryNotifier(ctx, notifications, follows, _clock, NullLogger<LicenceExpiryNotifier>.Instance)
            .NotifyDueAsync();
    }

    private async Task SetExpiryAsync(DateOnly? expiresOn)
    {
        await using var ctx = _db.NewContext();
        var licence = await ctx.GatewayInstanceLicences.SingleOrDefaultAsync(l => l.InstanceId == _instanceId);
        if (licence is null)
        {
            ctx.GatewayInstanceLicences.Add(new GatewayInstanceLicence
            {
                OrganizationId = TestDb.DefaultOrgId, InstanceId = _instanceId, Licensee = "CRONUS A/S",
                ExpiresOn = expiresOn, Text = "licence", TextSha256 = new string('0', 64), ReadAt = Start.UtcDateTime,
            });
        }
        else
        {
            licence.ExpiresOn = expiresOn;
        }
        await ctx.SaveChangesAsync();
    }

    private async Task SetUpAsync()
    {
        _owner = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "owner@cronus.example");
        var now = Start.UtcDateTime;
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", CreatedByUserId = _owner.Id, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS server room", MachineName = "CRONUS-BC01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now, LastSeenAt = now,
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();
        var instance = new GatewayInstance
        {
            OrganizationId = TestDb.DefaultOrgId, GatewayId = gateway.Id, Machine = "CRONUS-BC01", Name = "BC250", State = "Running",
            Version = "25.4.29661.29927", FirstReportedAt = now, ReportedAt = now, ReadAt = now,
        };
        ctx.GatewayInstances.Add(instance);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = instance.Id, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        _instanceId = instance.Id;
        _environmentId = env.Id;
    }

    private sealed class FailingReads : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The database went away.");
    }

    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject)> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            Sent.Add((toEmail, content.Subject));
            return Task.CompletedTask;
        }
    }
}
