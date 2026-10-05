using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The palette's visible way in (#888). Nobody finds a hotkey by accident, and
/// on a phone or a tablet there is no hotkey at all - so this button is the
/// only door, and the things that make it a door are worth a test: it is there
/// for a signed-in person, it is absent for everyone else, and its accessible
/// name carries the shortcut rather than leaving the key caps (which are
/// decoration) to say it.
/// </summary>
public sealed class MainLayoutPaletteButtonTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitAuthorizationContext _auth;

    public MainLayoutPaletteButtonTests()
    {
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _auth = _ctx.AddAuthorization();

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton<IDbContextFactory<ALDevToolbox.Data.AppDbContext>>(_db.NewContextFactory());
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<ALDevToolbox.Services.Notifications.InAppNotificationService>();
        _ctx.Services.AddScoped<DisplayTimeZone>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton<IToolAvailability>(new AllToolsOn());
        _ctx.Services.AddSingleton<ISingleTenantMode>(new SingleTenantOff());
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(
            typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    private sealed class AllToolsOn : IToolAvailability
    {
        public bool IsSiteEnabled(ToolKey key) => true;
    }

    private sealed class SingleTenantOff : ISingleTenantMode
    {
        public bool IsEnabled => false;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void A_signed_in_person_gets_a_search_control_in_the_top_bar()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find(".app__top .cmdp-open");
            button.GetAttribute("type").Should().Be("button");
            button.HasAttribute("data-cmdp-open").Should().BeTrue(
                "command-palette.js opens the palette from this attribute, through the "
                + "same code path as the hotkey");
            cut.Find(".cmdp-open__label").TextContent.Trim()
                .Should().Be("Search or jump to...");
        });
    }

    [Fact]
    public void Its_accessible_name_carries_the_shortcut()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var button = cut.Find(".cmdp-open");

            // The key caps are aria-hidden decoration, so the name comes from
            // the label plus a clipped span. command-palette.js rewrites both
            // to Cmd on a Mac; Ctrl is what the server can know.
            button.QuerySelector(".cmdp-open__keys")!.GetAttribute("aria-hidden")
                .Should().Be("true");
            var spoken = button.QuerySelector("[data-cmdp-shortcut]")!;
            spoken.ClassName.Should().Contain("u-sr-only",
                "clipped, not display:none - a hidden span is not in the accessibility tree");
            button.TextContent.Should().Contain("Search or jump to...").And.Contain("Ctrl K");
        });
    }

    [Fact]
    public void An_anonymous_visitor_is_offered_nothing_to_search()
    {
        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() => cut.Find(".app__top").Should().NotBeNull());
        cut.FindAll(".cmdp-open").Should().BeEmpty(
            "there is nowhere for a signed-out visitor to jump to, which is also why "
            + "the palette itself renders nothing for them");
    }

    // ---- the notification bell beside it (#1043) ---------------------------

    [Fact]
    public async Task A_signed_in_person_sees_the_bell_with_their_unread_count()
    {
        var userId = await SeedUserWithUnreadAsync(3);
        _db.OrgContext.CurrentUserId = userId;
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var bell = cut.Find(".app__top button.notif-bell");
            bell.GetAttribute("popovertarget").Should().Be("notif-flyout");
            bell.GetAttribute("aria-label").Should().Be("Notifications, 3 unread");
            cut.Find(".notif-bell__count").TextContent.Should().Be("3");
        });
    }

    [Fact]
    public async Task The_bell_opens_a_flyout_with_the_newest_and_a_way_to_the_full_list()
    {
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/solutions?tab=mine");
        var userId = await SeedUserWithUnreadAsync(InAppNotificationService.FlyoutSize + 1);
        _db.OrgContext.CurrentUserId = userId;
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            var flyout = cut.Find("#notif-flyout");
            flyout.HasAttribute("popover").Should().BeTrue();
            flyout.QuerySelectorAll(".notif").Should().HaveCount(InAppNotificationService.FlyoutSize);
            flyout.QuerySelectorAll(".notif--unread").Should().HaveCount(InAppNotificationService.FlyoutSize);
            flyout.QuerySelector("form[action='/notifications/read-all'] input[name='returnUrl']")!
                .GetAttribute("value").Should().Be("/solutions?tab=mine");
            flyout.QuerySelectorAll("a[href='/notifications']").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task On_the_notifications_page_the_flyout_leaves_mark_all_to_the_page()
    {
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/notifications");
        _db.OrgContext.CurrentUserId = await SeedUserWithUnreadAsync(2);
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() => cut.FindAll("#notif-flyout .notif").Should().HaveCount(2));
        cut.FindAll("#notif-flyout form").Should().BeEmpty();
    }

    [Fact]
    public async Task An_empty_flyout_says_what_will_show_up_and_offers_no_mark_all()
    {
        _db.OrgContext.CurrentUserId = await SeedUserWithUnreadAsync(0);
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
            cut.Find("#notif-flyout .notif-flyout__note").TextContent.Should().Contain("Nothing yet"));
        cut.FindAll("#notif-flyout form").Should().BeEmpty();
        cut.FindAll("#notif-flyout a[href='/notifications']").Should().ContainSingle();
    }

    [Fact]
    public async Task Being_on_the_page_a_notification_is_about_marks_it_read()
    {
        var userId = await SeedUserWithUnreadAsync(2);
        await using (var db = _db.NewContext())
        {
            db.UserNotifications.Add(new ALDevToolbox.Domain.Entities.UserNotification
            {
                UserId = userId, OrganizationId = TestDb.DefaultOrgId,
                Category = ALDevToolbox.Domain.Entities.NotificationCategory.Builds,
                Title = "Build failed: CRONUS Coffee - Release", Path = "/pipelines/2", CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = userId;
        _auth.SetAuthorized("user@example.com");
        // As if they followed the link in the email.
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/pipelines/1#latest");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
            cut.Find(".app__top button.notif-bell").GetAttribute("aria-label").Should().Be("Notifications, 1 unread"));
        await using var read = _db.NewContext();
        (await read.UserNotifications.Where(n => n.ReadAt == null).Select(n => n.Path).ToListAsync())
            .Should().Equal("/pipelines/2");
    }

    [Fact]
    public void Nothing_unread_shows_the_bell_without_a_count()
    {
        _auth.SetAuthorized("user@example.com");

        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() => cut.Find(".notif-bell").GetAttribute("aria-label").Should().Be("Notifications"));
        cut.FindAll(".notif-bell__count").Should().BeEmpty();
    }

    [Fact]
    public void An_anonymous_visitor_gets_no_bell()
    {
        var cut = _ctx.Render<MainLayout>();

        cut.WaitForAssertion(() => cut.Find(".app__top").Should().NotBeNull());
        cut.FindAll(".notif-bell").Should().BeEmpty();
    }

    private async Task<int> SeedUserWithUnreadAsync(int unread)
    {
        await using var db = _db.NewContext();
        var user = new ALDevToolbox.Domain.Entities.User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = "user@example.com", DisplayName = "Alex Hansen",
            PasswordHash = "x", Role = ALDevToolbox.Domain.Entities.UserRole.User,
            Status = ALDevToolbox.Domain.Entities.UserStatus.Active, CreatedAt = DateTime.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        for (var i = 0; i < unread; i++)
        {
            db.UserNotifications.Add(new ALDevToolbox.Domain.Entities.UserNotification
            {
                UserId = user.Id, OrganizationId = TestDb.DefaultOrgId,
                Category = ALDevToolbox.Domain.Entities.NotificationCategory.Builds,
                Title = "Build failed: CRONUS Coffee - Main", Path = "/pipelines/1", CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }
}
