using System.Globalization;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The identity checks in the Microsoft callback's two signed-in modes
/// (<see cref="EntraAuthEndpoints.OnTicketReceivedAsync"/>). Step-up refuses
/// unless the user on this request's cookie is the step-up user and the
/// identity Microsoft returned is linked to them; link refuses a cookie that
/// belongs to someone other than the link target, so a callback URL replayed
/// from another session links nothing (#1346). The OIDC handshake itself is
/// stood in for by building the ticket the handler would have produced.
/// </summary>
public sealed class EntraCallbackIdentityTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string OtherOid = "bbbbbbbb-0000-0000-0000-000000000001";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Step_up_refuses_when_the_cookie_belongs_to_another_user()
    {
        await SeedEntraAsync();
        var stepUpUser = await SeedUserAsync("mette@cronus.com");
        var cookieUser = await SeedUserAsync("jonas@cronus.com");
        await SeedLinkAsync(stepUpUser, Oid);

        var (http, auth) = await RunCallbackAsync(
            cookieUserId: cookieUser,
            oid: Oid,
            items: StepUpItems(stepUpUser));

        http.Response.Headers.Location.ToString()
            .Should().Be($"/login/challenge?{RouteConstants.ErrQuery}=entra-mismatch");
        auth.SignIns.Should().Be(0, "a refused step-up must not re-issue the cookie");
    }

    [Fact]
    public async Task Step_up_goes_ahead_for_the_cookie_user_with_their_own_linked_identity()
    {
        // The control for the two refusals: the same harness with the matching
        // cookie and the user's own identity completes the step-up, so the
        // refusals are the identity checks and not a gap in the harness.
        await SeedEntraAsync();
        var stepUpUser = await SeedUserAsync("mette@cronus.com");
        await SeedLinkAsync(stepUpUser, Oid);

        var (http, auth) = await RunCallbackAsync(
            cookieUserId: stepUpUser,
            oid: Oid,
            items: StepUpItems(stepUpUser));

        http.Response.Headers.Location.ToString().Should().Be("/");
        auth.SignIns.Should().Be(1, "a completed step-up re-issues the cookie with the fresh stamp");
    }

    [Fact]
    public async Task Step_up_refuses_an_identity_linked_to_someone_else()
    {
        await SeedEntraAsync();
        var stepUpUser = await SeedUserAsync("mette@cronus.com");
        var colleague = await SeedUserAsync("jonas@cronus.com");
        await SeedLinkAsync(stepUpUser, Oid);
        await SeedLinkAsync(colleague, OtherOid);

        // The right person is signed in here, but a colleague completed the
        // Microsoft prompt with their own account.
        var (http, auth) = await RunCallbackAsync(
            cookieUserId: stepUpUser,
            oid: OtherOid,
            items: StepUpItems(stepUpUser));

        http.Response.Headers.Location.ToString()
            .Should().Be($"/login/challenge?{RouteConstants.ErrQuery}=entra-mismatch");
        auth.SignIns.Should().Be(0);
    }

    [Fact]
    public async Task Link_refuses_when_the_cookie_belongs_to_another_user()
    {
        await SeedEntraAsync();
        var linkTarget = await SeedUserAsync("mette@cronus.com");
        var cookieUser = await SeedUserAsync("jonas@cronus.com");

        var (http, _) = await RunCallbackAsync(
            cookieUserId: cookieUser,
            oid: Oid,
            items: new() { [EntraAuthEndpoints.LinkUserIdItem] = Id(linkTarget) });

        http.Response.Headers.Location.ToString()
            .Should().Be($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=entra-failed");
        (await LinkCountAsync()).Should().Be(0, "a replayed callback must link nothing");
    }

    [Fact]
    public async Task Link_goes_ahead_when_the_cookie_is_the_link_target()
    {
        // The control for the refusal above: the same callback with the
        // matching cookie does link, so the refusal is the cookie check.
        await SeedEntraAsync();
        var linkTarget = await SeedUserAsync("mette@cronus.com");

        var (http, _) = await RunCallbackAsync(
            cookieUserId: linkTarget,
            oid: Oid,
            items: new() { [EntraAuthEndpoints.LinkUserIdItem] = Id(linkTarget) });

        http.Response.Headers.Location.ToString().Should().Be("/account/security?ok=ms-linked");
        (await LinkCountAsync()).Should().Be(1);
    }

    // ---- Harness ---------------------------------------------------------------

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string?> StepUpItems(int userId) => new()
    {
        [StepUpEndpoints.EntraStepUpUserIdItem] = Id(userId),
        [StepUpEndpoints.EntraStepUpIssuedAtItem] = DateTime.UtcNow.AddMinutes(-1).ToString("o", CultureInfo.InvariantCulture),
    };

    private async Task<(HttpContext Http, CookieAuthenticationService Auth)> RunCallbackAsync(
        int cookieUserId, string oid, Dictionary<string, string?> items)
    {
        await using var ctx = _db.NewContext();
        var auth = new CookieAuthenticationService(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(HttpOrganizationContext.UserIdClaim, Id(cookieUserId)),
            new Claim(HttpOrganizationContext.OrganizationIdClaim, Id(TestDb.DefaultOrgId)),
        ], CookieAuthenticationDefaults.AuthenticationScheme)));
        var entra = new EntraSignInService(ctx,
            new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System),
            _db.OrgContext, _db.DataProtectionProvider, TimeProvider.System,
            NullLogger<EntraSignInService>.Instance);
        // Everything a completed step-up needs too, so a refusal below fails by
        // its assertion rather than by a service the success path would miss.
        var services = new ServiceCollection()
            .AddSingleton(entra)
            .AddSingleton(ctx)
            .AddSingleton(new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System))
            .AddSingleton<IOrganizationContext>(new AmbientOrganizationContext
            {
                CurrentUserId = cookieUserId,
                CurrentOrganizationId = TestDb.DefaultOrgId,
            })
            .AddSingleton(TimeProvider.System)
            .AddSingleton(_db.DataProtectionProvider)
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .AddSingleton<IAuthenticationService>(auth)
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };

        var microsoft = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tid", Tenant),
            new Claim("oid", oid),
            new Claim("preferred_username", "someone@cronus.com"),
        ], OpenIdConnectDefaults.AuthenticationScheme));
        var properties = new AuthenticationProperties(items) { RedirectUri = "/" };
        var context = new TicketReceivedContext(
            http,
            new AuthenticationScheme(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(),
            new AuthenticationTicket(microsoft, properties, OpenIdConnectDefaults.AuthenticationScheme));

        await EntraAuthEndpoints.OnTicketReceivedAsync(context);
        return (http, auth);
    }

    private async Task SeedEntraAsync()
    {
        await using var ctx = _db.NewContext();
        var row = await ctx.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
            ctx.OrganizationSettings.Add(row);
        }
        row.EntraEnabled = true;
        row.EntraAllowedTenantIds = [Tenant];
        row.UpdatedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task SeedLinkAsync(int userId, string oid)
    {
        await using var ctx = _db.NewContext();
        ctx.UserExternalLogins.Add(new UserExternalLogin
        {
            UserId = userId,
            Provider = EntraSignInService.ProviderName,
            Issuer = Tenant,
            Subject = oid,
            DisplayIdentity = "seeded",
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> LinkCountAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.UserExternalLogins.AsNoTracking().CountAsync();
    }

    /// <summary>
    /// Answers the callback's cookie lookup with a fixed signed-in user and
    /// counts sign-ins, which is what a completed step-up would do.
    /// </summary>
    private sealed class CookieAuthenticationService(ClaimsPrincipal cookieUser) : IAuthenticationService
    {
        public int SignIns { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(cookieUser, CookieAuthenticationDefaults.AuthenticationScheme)));
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            SignIns++;
            return Task.CompletedTask;
        }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
