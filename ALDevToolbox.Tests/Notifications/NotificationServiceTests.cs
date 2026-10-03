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

namespace ALDevToolbox.Tests.Notifications;

/// <summary>
/// Notification settings and the sender (issue #1034): each recipient gets a
/// notification the way they chose for its category, and nothing about a
/// failed send reaches the caller.
/// </summary>
public sealed class NotificationServiceTests : IDisposable
{
    private const string Origin = "https://workbench.cronus.example";

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly CapturingEmailService _email = new();

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    // ---- preferences -------------------------------------------------------

    [Fact]
    public async Task Every_category_starts_on_immediately()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();

        var choices = await Preferences(ctx).GetForCurrentUserAsync();

        choices.Should().HaveCount(Enum.GetValues<NotificationCategory>().Length);
        choices.Values.Should().AllSatisfy(d => d.Should().Be(NotificationDelivery.Immediately));
    }

    [Fact]
    public async Task A_choice_is_saved_and_changed_in_place()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        _db.OrgContext.CurrentUserId = userId;
        await using (var ctx = _db.NewContext())
        {
            await Preferences(ctx).SetForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Daily);
        }
        await using (var ctx = _db.NewContext())
        {
            await Preferences(ctx).SetForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Off);
        }

        await using var read = _db.NewContext();
        (await Preferences(read).GetForCurrentUserAsync())[NotificationCategory.Builds].Should().Be(NotificationDelivery.Off);
        (await read.UserNotificationSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_choice_is_refused_with_its_field()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using var ctx = _db.NewContext();

        var act = () => Preferences(ctx).SetForCurrentUserAsync(NotificationCategory.Builds, (NotificationDelivery)42);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Delivery");
    }

    // ---- sending -----------------------------------------------------------

    [Fact]
    public async Task Immediately_puts_one_email_per_recipient_on_the_outbox()
    {
        var alex = await SeedUserAsync("alex@cronus.example", "Alex");
        var sam = await SeedUserAsync("sam@cronus.example", "Sam");

        await NotifyAsync(Notification(alex, sam));

        _email.Sent.Select(s => s.To).Should().BeEquivalentTo(["alex@cronus.example", "sam@cronus.example"]);
        _email.Sent.Should().AllSatisfy(s => s.Purpose.Should().Be(EmailPurpose.BuildNotification));
        _email.Sent.Single(s => s.To == "sam@cronus.example").Subject.Should().Be("For Sam");
    }

    [Fact]
    public async Task A_digest_choice_keeps_the_item_instead_of_emailing()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await SetChoiceAsync(alex, NotificationDelivery.Weekly);

        await NotifyAsync(Notification(alex));

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        var item = (await ctx.NotificationDigestItems.ToListAsync()).Should().ContainSingle().Subject;
        item.UserId.Should().Be(alex);
        item.Delivery.Should().Be(NotificationDelivery.Weekly);
        item.Title.Should().Be("Build failed: CRONUS Coffee - Main");
        item.OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task An_urgent_notification_skips_the_digest_but_not_off()
    {
        var weekly = await SeedUserAsync("alex@cronus.example");
        var off = await SeedUserAsync("sam@cronus.example");
        await SetChoiceAsync(weekly, NotificationDelivery.Weekly);
        await SetChoiceAsync(off, NotificationDelivery.Off);

        await NotifyAsync(Notification(weekly, off) with { Urgent = true });

        _email.Sent.Select(s => s.To).Should().Equal("alex@cronus.example");
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Off_sends_and_keeps_nothing()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await SetChoiceAsync(alex, NotificationDelivery.Off);

        await NotifyAsync(Notification(alex));

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_and_pending_people_get_nothing()
    {
        var disabled = await SeedUserAsync("gone@cronus.example", status: UserStatus.Disabled);
        var pending = await SeedUserAsync("new@cronus.example", status: UserStatus.Pending);

        await NotifyAsync(Notification(disabled, pending));

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Someone_in_another_organisation_is_not_found()
    {
        var stranger = await SeedUserAsync("stranger@fabrikam.example", organizationId: TestDb.OtherOrgId);

        await NotifyAsync(Notification(stranger));

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_public_base_url_nothing_is_sent()
    {
        var alex = await SeedUserAsync("alex@cronus.example");

        await NotifyAsync(Notification(alex), origin: null);

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_send_does_not_stop_the_others_or_reach_the_caller()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        var sam = await SeedUserAsync("sam@cronus.example");
        _email.FailFor = "alex@cronus.example";

        await NotifyAsync(Notification(alex, sam));

        _email.Sent.Select(s => s.To).Should().Equal("sam@cronus.example");
    }

    [Fact]
    public void Every_category_has_an_outbox_label()
    {
        foreach (var category in Enum.GetValues<NotificationCategory>())
        {
            var purpose = NotificationService.PurposeFor(category);
            ALDevToolbox.Components.Pages.SiteAdmin.SiteAdminEmail.Describe(purpose).Should().NotBe("Other email");
        }
    }

    // ---- helpers -----------------------------------------------------------

    private static Notification Notification(params int[] recipients) => new(
        NotificationCategory.Builds,
        recipients,
        new NotificationDigestEntry("Build failed: CRONUS Coffee - Main", "error AL0118", $"{Origin}/pipelines/1", "CRONUS Coffee"),
        (_, recipient, _) => Task.FromResult(new EmailContent($"For {recipient.DisplayName}", "<p>Body</p>", "Body")));

    private async Task NotifyAsync(Notification notification, string? origin = Origin)
    {
        await using var ctx = _db.NewContext();
        var service = new NotificationService(
            ctx, Preferences(ctx), _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin(origin), _db.OrgContext, TimeProvider.System, NullLogger<NotificationService>.Instance);
        await service.NotifyAsync(notification);
    }

    private NotificationPreferenceService Preferences(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, TimeProvider.System, NullLogger<NotificationPreferenceService>.Instance);

    private async Task SetChoiceAsync(int userId, NotificationDelivery delivery)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        await Preferences(ctx).SetForCurrentUserAsync(NotificationCategory.Builds, delivery);
        _db.OrgContext.CurrentUserId = null;
    }

    private async Task<int> SeedUserAsync(
        string email, string displayName = "Alex Hansen", UserStatus status = UserStatus.Active,
        int organizationId = TestDb.DefaultOrgId)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = organizationId,
            Email = email,
            DisplayName = displayName,
            PasswordHash = "x",
            Role = UserRole.User,
            Status = status,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private sealed class CapturingEmailService : IEmailService
    {
        public List<(string To, string Subject, EmailPurpose Purpose)> Sent { get; } = [];
        public string? FailFor { get; set; }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (toEmail == FailFor) throw new InvalidOperationException("The mail server said no.");
            Sent.Add((toEmail, content.Subject, purpose));
            return Task.CompletedTask;
        }
    }
}
