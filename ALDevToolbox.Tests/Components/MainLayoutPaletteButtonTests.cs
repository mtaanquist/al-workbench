using ALDevToolbox.Components.Layout;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
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
            var bell = cut.Find(".app__top a.notif-bell");
            bell.GetAttribute("href").Should().Be("/notifications");
            bell.GetAttribute("aria-label").Should().Be("Notifications, 3 unread");
            cut.Find(".notif-bell__count").TextContent.Should().Be("3");
        });
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
