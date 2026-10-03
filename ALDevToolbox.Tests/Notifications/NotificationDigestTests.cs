using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Tests.Auth;

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// Daily and weekly digests (issue #1037): due items go out once, grouped per
/// person, and nothing about a restart or a failed send loses or repeats one.
/// </summary>
public sealed class NotificationDigestTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";

    // A Wednesday, after the daily cut-off.
    private static readonly DateTime Wednesday0700 = new(2026, 10, 7, 7, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Wednesday0700));

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("2026-10-07T07:00:00", "2026-10-07T06:00:00")]
    [InlineData("2026-10-07T06:00:00", "2026-10-07T06:00:00")]
    [InlineData("2026-10-07T05:59:00", "2026-10-06T06:00:00")]
    public void The_daily_cutoff_is_the_latest_06_00(string now, string expected) =>
        NotificationDigestService.DailyCutoff(Utc(now)).Should().Be(Utc(expected));

    [Theory]
    [InlineData("2026-10-07T07:00:00", "2026-10-05T06:00:00")] // Wednesday
    [InlineData("2026-10-05T06:30:00", "2026-10-05T06:00:00")] // Monday after
    [InlineData("2026-10-05T05:30:00", "2026-09-28T06:00:00")] // Monday before
    [InlineData("2026-10-11T23:00:00", "2026-10-05T06:00:00")] // Sunday
    public void The_weekly_cutoff_is_the_latest_Monday_06_00(string now, string expected) =>
        NotificationDigestService.WeeklyCutoff(Utc(now)).Should().Be(Utc(expected));

    [Fact]
    public async Task Due_items_go_out_once_in_one_email_per_person_and_digest()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));
        await KeepAsync(alex, NotificationCategory.Deployments, NotificationDelivery.Daily, "Deployed: A", Wednesday0700.AddHours(-4));
        await KeepAsync(sam, NotificationCategory.Builds, NotificationDelivery.Weekly, "Build failed: B", Wednesday0700.AddDays(-3));

        (await SendDueAsync()).Should().Be(2);

        _email.Sent.Select(s => (s.To, s.Subject)).Should().BeEquivalentTo(new[]
        {
            ("alex@cronus.example", "Daily digest from AL Workbench: 2 updates"),
            ("sam@cronus.example", "Weekly digest from AL Workbench: 1 update"),
        });
        _email.Sent.Should().AllSatisfy(s => s.Purpose.Should().Be(EmailPurpose.NotificationDigest));
        var alexMail = _email.Sent.Single(s => s.To == "alex@cronus.example");
        alexMail.Html.Should().Contain("Build failed: A").And.Contain("Deployed: A").And.Contain(">Builds<").And.Contain(">Deployments<");
        await ItemCountShouldBeAsync(0);

        _email.Sent.Clear();
        (await SendDueAsync()).Should().Be(0, "a second run, as after a restart, finds nothing left to send");
        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Items_after_the_cutoff_wait_for_the_next_one()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Today", Wednesday0700.AddMinutes(-30));
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Weekly, "This week", Wednesday0700.AddDays(-1));

        (await SendDueAsync()).Should().Be(0);
        await ItemCountShouldBeAsync(2);

        _clock.Advance(TimeSpan.FromDays(1));
        (await SendDueAsync()).Should().Be(1);
        _email.Sent.Single().Html.Should().Contain("Today").And.NotContain("This week");
        await ItemCountShouldBeAsync(1);
    }

    [Fact]
    public async Task A_kind_turned_off_since_is_dropped_unsent()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));
        await KeepAsync(alex, NotificationCategory.Deployments, NotificationDelivery.Daily, "Deployed: A", Wednesday0700.AddHours(-5));
        await ChooseAsync(alex, NotificationCategory.Builds, NotificationDelivery.Off);

        await SendDueAsync();

        _email.Sent.Single().Html.Should().Contain("Deployed: A").And.NotContain("Build failed: A");
        await ItemCountShouldBeAsync(0);
    }

    [Fact]
    public async Task A_digest_that_cannot_be_queued_keeps_its_items()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));
        _email.FailFor = "alex@cronus.example";

        (await SendDueAsync()).Should().Be(0);
        await ItemCountShouldBeAsync(1);

        _email.FailFor = null;
        (await SendDueAsync()).Should().Be(1);
        await ItemCountShouldBeAsync(0);
    }

    [Fact]
    public async Task Without_email_set_up_items_wait_and_old_ones_are_dropped()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Recent", Wednesday0700.AddDays(-2));
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Old", Wednesday0700.AddDays(-31));
        _email.Configured = false;

        (await SendDueAsync()).Should().Be(0);

        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.Select(i => i.Title).ToListAsync()).Should().Equal("Recent");
    }

    [Fact]
    public async Task In_app_notifications_older_than_30_days_are_dropped_in_this_organisation_only()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var stranger = await SeedUserAsync("stranger@fabrikam.example", organizationId: TestDb.OtherOrgId);
        await ListAsync(alex, "Recent", Wednesday0700.AddDays(-29));
        await ListAsync(alex, "Old", Wednesday0700.AddDays(-31));
        await ListAsync(stranger, "Theirs", Wednesday0700.AddDays(-31), TestDb.OtherOrgId);

        await SendDueAsync(origin: null);

        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.IgnoreQueryFilters()
                .OrderBy(n => n.Title).Select(n => n.Title).ToListAsync())
            .Should().Equal("Recent", "Theirs");
    }

    [Fact]
    public async Task A_disabled_person_gets_nothing_and_their_items_go()
    {
        var alex = await SeedUserAsync("alex@cronus.example", UserStatus.Disabled);
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));

        (await SendDueAsync()).Should().Be(0);

        _email.Sent.Should().BeEmpty();
        await ItemCountShouldBeAsync(0);
    }

    [Fact]
    public async Task Without_a_public_base_url_items_wait()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));

        (await SendDueAsync(origin: null)).Should().Be(0);

        _email.Sent.Should().BeEmpty();
        await ItemCountShouldBeAsync(1);
    }

    [Fact]
    public async Task A_shutdown_after_queuing_still_clears_the_items()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Build failed: A", Wednesday0700.AddHours(-5));
        using var shutdown = new CancellationTokenSource();
        _email.OnSend = shutdown.Cancel;

        try { await SendDueAsync(ct: shutdown.Token); }
        catch (OperationCanceledException) { }

        _email.Sent.Should().ContainSingle();
        await ItemCountShouldBeAsync(0);
    }

    [Fact]
    public async Task Another_organisations_items_are_left_alone()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var other = await SeedUserAsync("other@cronus.example", organizationId: TestDb.OtherOrgId);
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Ours", Wednesday0700.AddHours(-5));
        await KeepAsync(other, NotificationCategory.Builds, NotificationDelivery.Daily, "Theirs", Wednesday0700.AddHours(-5),
            organizationId: TestDb.OtherOrgId);
        await KeepAsync(other, NotificationCategory.Builds, NotificationDelivery.Daily, "Theirs, old", Wednesday0700.AddDays(-40),
            organizationId: TestDb.OtherOrgId);

        await SendDueAsync();

        _email.Sent.Select(s => s.To).Should().Equal("alex@cronus.example");
        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        await ItemCountShouldBeAsync(2);
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
    }

    [Fact]
    public async Task An_item_saved_just_after_the_cutoff_waits_a_few_minutes_for_it()
    {
        _clock.Advance(TimeSpan.FromMinutes(-58)); // 06:02, inside the grace
        var alex = await SeedUserAsync("alex@cronus.example");
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Just before", Wednesday0700.AddMinutes(-61));
        await KeepAsync(alex, NotificationCategory.Builds, NotificationDelivery.Daily, "Earlier", Wednesday0700.AddDays(-1).AddHours(-2));

        (await SendDueAsync()).Should().Be(1);

        _email.Sent.Single().Html.Should().Contain("Earlier").And.NotContain("Just before");
    }

    // ---- helpers -----------------------------------------------------------

    private static DateTime Utc(string value) => DateTime.SpecifyKind(DateTime.Parse(value), DateTimeKind.Utc);

    private async Task<int> SendDueAsync(string? origin = Origin, CancellationToken ct = default)
    {
        await using var ctx = _db.NewContext();
        var service = new NotificationDigestService(
            ctx, _email, new EmailRenderer(_services, NullLoggerFactory.Instance), new PublicOrigin(origin),
            _db.OrgContext, _clock, NullLogger<NotificationDigestService>.Instance);
        return await service.SendDueAsync(ct);
    }

    private async Task ItemCountShouldBeAsync(int count)
    {
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.CountAsync()).Should().Be(count);
    }

    private async Task KeepAsync(
        int userId, NotificationCategory category, NotificationDelivery delivery, string title, DateTime createdAt,
        int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        ctx.NotificationDigestItems.Add(new NotificationDigestItem
        {
            UserId = userId, OrganizationId = organizationId, Category = category, Delivery = delivery,
            Title = title, Url = $"{Origin}/pipelines/1", SolutionName = "CRONUS Coffee", CreatedAt = createdAt,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ListAsync(int userId, string title, DateTime createdAt, int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        ctx.UserNotifications.Add(new UserNotification
        {
            UserId = userId, OrganizationId = organizationId, Category = NotificationCategory.Builds,
            Title = title, Path = "/pipelines/1", CreatedAt = createdAt,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ChooseAsync(int userId, NotificationCategory category, NotificationDelivery delivery)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        await new NotificationPreferenceService(ctx, _db.OrgContext, TimeProvider.System,
            NullLogger<NotificationPreferenceService>.Instance).SetEmailForCurrentUserAsync(category, delivery);
        _db.OrgContext.CurrentUserId = null;
    }

    private async Task<int> SeedUserAsync(
        string email, UserStatus status = UserStatus.Active, int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId, Email = email, DisplayName = "Alex Hansen", PasswordHash = "x",
            Role = UserRole.User, Status = status, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, string Html, EmailPurpose Purpose)> Sent { get; } = [];
        public string? FailFor { get; set; }
        public bool Configured { get; set; } = true;
        public Action? OnSend { get; set; }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (toEmail == FailFor) throw new InvalidOperationException("The outbox said no.");
            Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
            OnSend?.Invoke();
            return Task.CompletedTask;
        }
    }
}
