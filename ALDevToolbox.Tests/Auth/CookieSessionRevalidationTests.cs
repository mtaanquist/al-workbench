using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// <see cref="CookieSessionRevalidation"/> is what makes "disable this user"
/// bite before the 30-day cookie expires (issue #412). Elsewhere we test that
/// disabling <em>writes</em> Status; here we test that an already-signed-in
/// session stops working — plus the demotion cases and the 5-minute throttle
/// that a stamp-before-check bug would silently defeat. Clock is faked the
/// same way <c>SiteAdmin/BackupSchedulerTests</c> does it.
/// </summary>
public sealed class CookieSessionRevalidationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly RecordingAuthenticationService _auth = new();
    private readonly ALDevToolbox.Services.Operations.MaintenanceModeState _maintenance = new();
    private int _contextsCreated;

    public CookieSessionRevalidationTests()
    {
        var db = _db;
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddSingleton(_maintenance)
            .AddSingleton<IAuthenticationService>(_auth)
            .AddScoped(_ =>
            {
                Interlocked.Increment(ref _contextsCreated);
                return db.NewContext();
            })
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
    }

    private async Task<User> SeedUserAsync(
        UserRole role = UserRole.Admin,
        UserStatus status = UserStatus.Active,
        bool isSiteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "alice@cronus.test",
            DisplayName = "Alice",
            PasswordHash = "ignored",
            Role = role,
            Status = status,
            IsSiteAdmin = isSiteAdmin,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    /// <summary>Mutates the stored row, standing in for an admin action taken mid-session.</summary>
    private async Task UpdateUserAsync(int userId, Action<User> mutate)
    {
        await using var ctx = _db.NewContext();
        var user = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == userId);
        mutate(user);
        await ctx.SaveChangesAsync();
    }

    private CookieValidatePrincipalContext NewValidationContext(User user, AuthenticationProperties? properties = null)
    {
        var httpContext = new DefaultHttpContext { RequestServices = _services.CreateScope().ServiceProvider };
        var identity = new ClaimsIdentity(
        [
            new Claim(HttpOrganizationContext.UserIdClaim, user.Id.ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity),
            properties ?? new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme,
            displayName: null,
            handlerType: typeof(CookieAuthenticationHandler));
        return new CookieValidatePrincipalContext(httpContext, scheme, new CookieAuthenticationOptions(), ticket);
    }

    [Fact]
    public async Task Disabled_user_is_rejected_and_signed_out()
    {
        var user = await SeedUserAsync();
        await UpdateUserAsync(user.Id, u => u.Status = UserStatus.Disabled);
        var context = NewValidationContext(user);

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().BeNull("a rejected principal is dropped from the request");
        _auth.SignedOutSchemes.Should().ContainSingle()
            .Which.Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public async Task Deleted_user_is_rejected_and_signed_out()
    {
        var user = await SeedUserAsync();
        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.Users.IgnoreQueryFilters().FirstAsync(u => u.Id == user.Id);
            ctx.Users.Remove(row);
            await ctx.SaveChangesAsync();
        }
        var context = NewValidationContext(user);

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().BeNull();
        _auth.SignedOutSchemes.Should().ContainSingle();
    }

    [Fact]
    public async Task Admin_demoted_to_user_gets_the_new_role_on_the_next_request()
    {
        var user = await SeedUserAsync(role: UserRole.Admin);
        await UpdateUserAsync(user.Id, u => u.Role = UserRole.User);
        var context = NewValidationContext(user);

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().NotBeNull();
        context.Principal!.IsInRole("Admin").Should().BeFalse();
        context.Principal.IsInRole("User").Should().BeTrue();
        context.ShouldRenew.Should().BeTrue();
    }

    [Fact]
    public async Task Site_admin_demotion_drops_the_site_admin_claim()
    {
        var user = await SeedUserAsync(role: UserRole.Admin, isSiteAdmin: true);
        var stillSiteAdmin = NewValidationContext(user);
        await CookieSessionRevalidation.ValidateAsync(stillSiteAdmin);
        stillSiteAdmin.Principal.Should().NotBeNull();
        stillSiteAdmin.Principal!.FindFirstValue(HttpOrganizationContext.SiteAdminClaim).Should().Be("true");

        await UpdateUserAsync(user.Id, u => u.IsSiteAdmin = false);
        var afterDemotion = NewValidationContext(user);

        await CookieSessionRevalidation.ValidateAsync(afterDemotion);

        afterDemotion.Principal!.FindFirstValue(HttpOrganizationContext.SiteAdminClaim).Should().BeNull();
        afterDemotion.Principal!.IsInRole(HttpOrganizationContext.SiteAdminRole).Should().BeFalse();
    }

    [Fact]
    public async Task Inside_the_throttle_window_no_database_read_happens()
    {
        var user = await SeedUserAsync();
        var properties = new AuthenticationProperties();
        var first = NewValidationContext(user, properties);
        await CookieSessionRevalidation.ValidateAsync(first);
        var readsAfterFirst = _contextsCreated;

        // A disable landing 4 minutes later must not be picked up yet: the
        // throttle is deliberate, and this is the assertion that catches a
        // stamp-written-before-the-check regression.
        await UpdateUserAsync(user.Id, u => u.Status = UserStatus.Disabled);
        _clock.Advance(TimeSpan.FromMinutes(4));
        var second = NewValidationContext(user, properties);

        await CookieSessionRevalidation.ValidateAsync(second);

        _contextsCreated.Should().Be(readsAfterFirst, "the throttle should short-circuit before resolving a DbContext");
        second.Principal!.Should().NotBeNull();
        _auth.SignedOutSchemes.Should().BeEmpty();
    }

    [Fact]
    public async Task Past_the_throttle_window_the_row_is_read_again()
    {
        var user = await SeedUserAsync();
        var properties = new AuthenticationProperties();
        var first = NewValidationContext(user, properties);
        await CookieSessionRevalidation.ValidateAsync(first);
        var readsAfterFirst = _contextsCreated;

        await UpdateUserAsync(user.Id, u => u.Status = UserStatus.Disabled);
        _clock.Advance(CookieSessionRevalidation.ValidationInterval + TimeSpan.FromSeconds(1));
        var second = NewValidationContext(user, properties);

        await CookieSessionRevalidation.ValidateAsync(second);

        _contextsCreated.Should().BeGreaterThan(readsAfterFirst);
        second.Principal!.Should().BeNull("the disable takes effect once the window has passed");
        _auth.SignedOutSchemes.Should().ContainSingle();
    }

    [Fact]
    public async Task A_cookie_without_a_user_id_claim_is_ignored()
    {
        await SeedUserAsync();
        var httpContext = new DefaultHttpContext { RequestServices = _services.CreateScope().ServiceProvider };
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Email, "alice@cronus.test")], "Cookies")),
            new AuthenticationProperties(),
            CookieAuthenticationDefaults.AuthenticationScheme);
        var context = new CookieValidatePrincipalContext(
            httpContext,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            ticket);

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().NotBeNull();
        _contextsCreated.Should().Be(0);
    }

    /// <summary>
    /// During a restore the users table is emptied or missing, and this check runs ahead
    /// of the maintenance 503. It must leave the cookie alone rather than sign the person
    /// out or fail the request (#1269).
    /// </summary>
    [Fact]
    public async Task During_a_restore_a_cookie_due_for_a_check_is_left_alone()
    {
        var user = await SeedUserAsync();
        // Stands in for the row a restore has not brought back yet.
        await UpdateUserAsync(user.Id, u => u.Status = UserStatus.Disabled);
        _maintenance.Enter("Restoring from a backup.");
        var context = NewValidationContext(user);

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().NotBeNull();
        context.ShouldRenew.Should().BeFalse();
        _auth.SignedOutSchemes.Should().BeEmpty();
        _contextsCreated.Should().Be(0, "nothing may be read from a database that is being restored");

        _maintenance.Exit();
        var after = NewValidationContext(user);
        await CookieSessionRevalidation.ValidateAsync(after);
        after.Principal.Should().BeNull("the check resumes once the restore is over");
    }

    /// <summary>
    /// Records sign-outs so the rejection path can be asserted without a real
    /// authentication stack behind the request.
    /// </summary>
    private sealed class RecordingAuthenticationService : IAuthenticationService
    {
        public List<string?> SignedOutSchemes { get; } = new();

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            SignedOutSchemes.Add(scheme);
            return Task.CompletedTask;
        }
    }
}
