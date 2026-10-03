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
/// Notification settings and the sender (issues #1034 and #1042): each
/// recipient gets a notification the way they chose for its category, in the
/// app and by email, and nothing about a failed send reaches the caller.
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
    public async Task Every_category_starts_in_app_and_emailed_immediately()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();

        var choices = await Preferences(ctx).GetForCurrentUserAsync();

        choices.Should().HaveCount(Enum.GetValues<NotificationCategory>().Length);
        choices.Values.Should().AllSatisfy(c => c.Should().Be(new NotificationChoice(true, NotificationDelivery.Immediately)));
    }

    [Fact]
    public async Task A_choice_is_saved_and_changed_in_place()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        _db.OrgContext.CurrentUserId = userId;
        await using (var ctx = _db.NewContext())
        {
            await Preferences(ctx).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Daily);
        }
        await using (var ctx = _db.NewContext())
        {
            await Preferences(ctx).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Off);
        }

        await using var read = _db.NewContext();
        (await Preferences(read).GetForCurrentUserAsync())[NotificationCategory.Builds].Email.Should().Be(NotificationDelivery.Off);
        (await read.UserNotificationSettings.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_in_app_and_email_choices_are_saved_without_touching_each_other()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using (var ctx = _db.NewContext())
        {
            await Preferences(ctx).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Weekly);
            await Preferences(ctx).SetInAppForCurrentUserAsync(NotificationCategory.Builds, false);
            await Preferences(ctx).SetInAppForCurrentUserAsync(NotificationCategory.Deployments, false);
        }

        await using var read = _db.NewContext();
        var choices = await Preferences(read).GetForCurrentUserAsync();
        choices[NotificationCategory.Builds].Should().Be(new NotificationChoice(false, NotificationDelivery.Weekly));
        choices[NotificationCategory.Deployments].Should().Be(new NotificationChoice(false, NotificationDelivery.Immediately));
    }

    [Fact]
    public async Task An_unknown_category_is_refused_for_the_in_app_choice_too()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using var ctx = _db.NewContext();

        var act = () => Preferences(ctx).SetInAppForCurrentUserAsync((NotificationCategory)42, true);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Category");
    }

    [Fact]
    public async Task An_unknown_choice_is_refused_with_its_field()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using var ctx = _db.NewContext();

        var act = () => Preferences(ctx).SetEmailForCurrentUserAsync(NotificationCategory.Builds, (NotificationDelivery)42);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Delivery");
    }

    [Fact]
    public async Task An_unknown_category_is_refused_with_its_field()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using var ctx = _db.NewContext();

        var act = () => Preferences(ctx).SetEmailForCurrentUserAsync((NotificationCategory)42, NotificationDelivery.Off);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Category");
    }

    [Fact]
    public async Task Two_saves_at_once_for_the_same_choice_both_succeed()
    {
        _db.OrgContext.CurrentUserId = await SeedUserAsync("alex@cronus.example");
        await using var first = _db.NewContext();
        await using var second = _db.NewContext();

        await Task.WhenAll(
            Preferences(first).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Daily),
            Preferences(second).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Weekly));

        await using var read = _db.NewContext();
        (await read.UserNotificationSettings.CountAsync()).Should().Be(1);
        // The same page can save again afterwards: nothing failed is left tracked.
        await Preferences(first).SetEmailForCurrentUserAsync(NotificationCategory.Builds, NotificationDelivery.Off);
        (await Preferences(read).GetForCurrentUserAsync())[NotificationCategory.Builds].Email.Should().Be(NotificationDelivery.Off);
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
    public async Task Every_recipient_sees_it_in_the_app_whatever_their_email_choice()
    {
        var now = await SeedUserAsync("now@cronus.example");
        var weekly = await SeedUserAsync("weekly@cronus.example");
        var noEmail = await SeedUserAsync("noemail@cronus.example");
        await SetChoiceAsync(weekly, NotificationDelivery.Weekly);
        await SetChoiceAsync(noEmail, NotificationDelivery.Off);

        await NotifyAsync(Notification(now, weekly, noEmail));

        await using var ctx = _db.NewContext();
        var listed = await ctx.UserNotifications.OrderBy(n => n.UserId).ToListAsync();
        listed.Select(n => n.UserId).Should().Equal(now, weekly, noEmail);
        listed.Should().AllSatisfy(n =>
        {
            n.Title.Should().Be("Build failed: CRONUS Coffee - Main");
            n.Detail.Should().Be("error AL0118");
            n.Path.Should().Be("/pipelines/1");
            n.SolutionName.Should().Be("CRONUS Coffee");
            n.Category.Should().Be(NotificationCategory.Builds);
            n.OrganizationId.Should().Be(TestDb.DefaultOrgId);
            n.ReadAt.Should().BeNull();
        });
    }

    [Fact]
    public async Task In_app_off_lists_nothing_but_still_emails()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        _db.OrgContext.CurrentUserId = alex;
        await using (var prefs = _db.NewContext())
        {
            await Preferences(prefs).SetInAppForCurrentUserAsync(NotificationCategory.Builds, false);
        }
        _db.OrgContext.CurrentUserId = null;

        await NotifyAsync(Notification(alex));

        _email.Sent.Select(s => s.To).Should().Equal("alex@cronus.example");
        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_and_pending_people_get_nothing()
    {
        var disabled = await SeedUserAsync("gone@cronus.example", status: UserStatus.Disabled);
        var pending = await SeedUserAsync("new@cronus.example", status: UserStatus.Pending);

        await NotifyAsync(Notification(disabled, pending));

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        (await ctx.UserNotifications.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Someone_in_another_organisation_is_not_found()
    {
        var stranger = await SeedUserAsync("stranger@fabrikam.example", organizationId: TestDb.OtherOrgId);

        await NotifyAsync(Notification(stranger));

        _email.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_a_public_base_url_nothing_is_emailed_but_it_shows_in_the_app()
    {
        var now = await SeedUserAsync("alex@cronus.example");
        var daily = await SeedUserAsync("sam@cronus.example");
        await SetChoiceAsync(daily, NotificationDelivery.Daily);

        await NotifyAsync(Notification(now, daily), origin: null);

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.AnyAsync()).Should().BeFalse();
        (await ctx.UserNotifications.Select(n => n.UserId).OrderBy(id => id).ToListAsync()).Should().Equal(now, daily);
    }

    [Fact]
    public async Task A_digest_item_links_to_the_page_on_the_public_address()
    {
        var alex = await SeedUserAsync("alex@cronus.example");
        await SetChoiceAsync(alex, NotificationDelivery.Daily);

        await NotifyAsync(Notification(alex));

        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.SingleAsync()).Url.Should().Be($"{Origin}/pipelines/1");
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
    public async Task Without_email_set_up_immediately_sends_nothing_but_digests_still_keep()
    {
        var now = await SeedUserAsync("now@cronus.example");
        var later = await SeedUserAsync("later@cronus.example");
        await SetChoiceAsync(later, NotificationDelivery.Daily);
        _email.Configured = false;

        await NotifyAsync(Notification(now, later));

        _email.Sent.Should().BeEmpty();
        await using var ctx = _db.NewContext();
        (await ctx.NotificationDigestItems.Select(i => i.UserId).ToListAsync()).Should().Equal(later);
    }

    [Fact]
    public async Task The_callers_unsaved_changes_are_left_alone()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        await SetChoiceAsync(userId, NotificationDelivery.Daily);
        await using var ctx = _db.NewContext();
        var pending = await ctx.Users.SingleAsync(u => u.Id == userId);
        pending.DisplayName = "Not saved yet";

        await NotifyAsync(Notification(userId), ctx: ctx);

        await using var read = _db.NewContext();
        (await read.Users.SingleAsync(u => u.Id == userId)).DisplayName.Should().Be("Alex Hansen");
        (await read.NotificationDigestItems.CountAsync()).Should().Be(1);
        ctx.ChangeTracker.Entries().Should().ContainSingle(e => e.State == EntityState.Modified);
    }

    [Fact]
    public async Task A_digest_that_cannot_be_kept_leaves_the_caller_able_to_save()
    {
        var userId = await SeedUserAsync("alex@cronus.example");
        await SetChoiceAsync(userId, NotificationDelivery.Weekly);
        await using var ctx = _db.NewContext();
        // PostgreSQL refuses a NUL character in text, so this item fails to save.
        var unsavable = Notification(userId) with { Summary = new NotificationSummary("Bad\0title", null, "/", null) };

        await NotifyAsync(unsavable, ctx: ctx);

        (await ctx.Users.SingleAsync(u => u.Id == userId)).DisplayName = "Renamed";
        await ctx.SaveChangesAsync();
        await using var read = _db.NewContext();
        (await read.NotificationDigestItems.CountAsync()).Should().Be(0);
        (await read.UserNotifications.CountAsync()).Should().Be(0);
        (await read.Users.SingleAsync(u => u.Id == userId)).DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public async Task A_timeout_inside_a_send_is_a_failed_send_not_a_cancellation()
    {
        var slow = await SeedUserAsync("slow@cronus.example");
        var fine = await SeedUserAsync("fine@cronus.example");
        _email.TimeOutFor = "slow@cronus.example";

        await NotifyAsync(Notification(slow, fine));

        _email.Sent.Select(s => s.To).Should().Equal("fine@cronus.example");
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
        new NotificationSummary("Build failed: CRONUS Coffee - Main", "error AL0118", "/pipelines/1", "CRONUS Coffee"),
        (_, recipient, _) => Task.FromResult(new EmailContent($"For {recipient.DisplayName}", "<p>Body</p>", "Body")));

    private async Task NotifyAsync(
        Notification notification, string? origin = Origin, ALDevToolbox.Data.AppDbContext? ctx = null)
    {
        await using var owned = ctx is null ? _db.NewContext() : null;
        ctx ??= owned!;
        var service = new NotificationService(
            ctx, _db.NewContextFactory(), Preferences(ctx), _email, new EmailRenderer(_services, NullLoggerFactory.Instance),
            new PublicOrigin(origin), _db.OrgContext, TimeProvider.System, NullLogger<NotificationService>.Instance);
        await service.NotifyAsync(notification);
    }

    private NotificationPreferenceService Preferences(ALDevToolbox.Data.AppDbContext ctx) =>
        new(ctx, _db.OrgContext, TimeProvider.System, NullLogger<NotificationPreferenceService>.Instance);

    private async Task SetChoiceAsync(int userId, NotificationDelivery delivery)
    {
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        await Preferences(ctx).SetEmailForCurrentUserAsync(NotificationCategory.Builds, delivery);
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
        public string? TimeOutFor { get; set; }
        public bool Configured { get; set; } = true;

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task SendAsync(string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (toEmail == FailFor) throw new InvalidOperationException("The mail server said no.");
            if (toEmail == TimeOutFor) throw new TaskCanceledException("The mail server timed out.");
            Sent.Add((toEmail, content.Subject, purpose));
            return Task.CompletedTask;
        }
    }
}
