using System.Globalization;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The rules that turn the step-up stamps into refusals: the route gate, the
/// cookie revalidation that must keep the stamps, the challenge state a code
/// endpoint may act on, the passkey user-verification flag, the consent a
/// bearer session must have been approved under, the action-level guard, and
/// the freshness an admin needs to change the rule at all.
/// </summary>
public sealed class StepUpEnforcementTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Now));
    public void Dispose() => _db.Dispose();

    private static ClaimsPrincipal CookieUser(int userId, int orgId, string stepUpTools = "", DateTime? strongAt = null, bool pat = false, int? windowMinutes = null)
    {
        var claims = new List<Claim>
        {
            new(HttpOrganizationContext.UserIdClaim, userId.ToString(CultureInfo.InvariantCulture)),
            new(HttpOrganizationContext.OrganizationIdClaim, orgId.ToString(CultureInfo.InvariantCulture)),
            new(EndpointHelpers.DisabledToolsClaim, string.Empty),
            new(EndpointHelpers.StepUpToolsClaim, stepUpTools),
        };
        if (windowMinutes is { } w) claims.Add(new(EndpointHelpers.StepUpWindowClaim, w.ToString(CultureInfo.InvariantCulture)));
        if (strongAt is { } at) claims.Add(new(StepUpAuth.StrongAuthAtClaim, at.ToString("o", CultureInfo.InvariantCulture)));
        if (pat) claims.Add(new("pat_id", "1"));
        return new(new ClaimsIdentity(claims, "test"));
    }

    private async Task<int> SeedUserAsync(int orgId = TestDb.DefaultOrgId, string email = "alice@cronus.test")
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = orgId, Email = email, DisplayName = "Alice", PasswordHash = "x",
            Role = UserRole.Admin, Status = UserStatus.Active, CreatedAt = Now,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task MarkAsync(params ToolKey[] tools)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.FirstAsync(o => o.Id == TestDb.DefaultOrgId);
        org.StepUpTools = ToolCatalog.Format(tools);
        await ctx.SaveChangesAsync();
    }

    // ---- ToolAccessGate middleware ------------------------------------------

    private async Task<HttpContext> RunGateAsync(ClaimsPrincipal? user, string path, string method = "GET")
    {
        var services = new ServiceCollection()
            .AddSingleton<IToolAvailability>(TestDb.EverythingEnabled())
            .AddSingleton<TimeProvider>(_clock)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseToolAccessGate();
        app.Run(ctx => { ctx.Response.StatusCode = 299; return Task.CompletedTask; });
        var pipeline = app.Build();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Path = path;
        http.Request.QueryString = new QueryString("?tab=history");
        http.Request.Method = method;
        http.Response.Body = new MemoryStream();
        if (user is not null) http.User = user;
        await pipeline(http);
        return http;
    }

    [Fact]
    public async Task Gate_sends_a_stale_get_to_step_up_and_back()
    {
        var http = await RunGateAsync(CookieUser(1, 1, "Releases", strongAt: Now.AddMinutes(-30)), "/pipelines/deployments/7");

        http.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        http.Response.Headers.Location.ToString()
            .Should().Be("/auth/step-up?return=" + Uri.EscapeDataString("/pipelines/deployments/7?tab=history"));
    }

    [Fact]
    public async Task Gate_refuses_a_stale_post_and_passes_a_fresh_session_or_an_unmarked_tool()
    {
        (await RunGateAsync(CookieUser(1, 1, "Releases"), "/pipelines/deployments/7", "POST"))
            .Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        (await RunGateAsync(CookieUser(1, 1, "Releases", strongAt: Now.AddMinutes(-1)), "/pipelines/deployments/7"))
            .Response.StatusCode.Should().Be(299);
        (await RunGateAsync(CookieUser(1, 1, "Releases"), "/cookbook"))
            .Response.StatusCode.Should().Be(299);
        (await RunGateAsync(null, "/pipelines/deployments/7"))
            .Response.StatusCode.Should().Be(299, "an anonymous request is authorization's to redirect");
    }

    [Fact]
    public async Task Gate_uses_the_organisations_own_window()
    {
        var tenMinutesAgo = Now.AddMinutes(-10);
        (await RunGateAsync(CookieUser(1, 1, "Releases", strongAt: tenMinutesAgo, windowMinutes: 5), "/pipelines/deployments/7"))
            .Response.StatusCode.Should().Be(StatusCodes.Status302Found, "a five-minute window has run out");
        (await RunGateAsync(CookieUser(1, 1, "Releases", strongAt: tenMinutesAgo, windowMinutes: 60), "/pipelines/deployments/7"))
            .Response.StatusCode.Should().Be(299, "an hour has not");
    }

    // ---- CookieSessionRevalidation keeps the stamps ---------------------------

    [Fact]
    public async Task Revalidation_rebuilds_the_claims_and_puts_the_stamps_back()
    {
        var userId = await SeedUserAsync();
        await using var ctx = _db.NewContext();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_clock)
            .AddSingleton(ctx)
            .AddSingleton<IAuthenticationService, NoOpAuthenticationService>()
            .BuildServiceProvider();
        var props = new AuthenticationProperties { IsPersistent = true };
        props.Items[EndpointHelpers.SignedInAtKey] = Now.ToString("o", CultureInfo.InvariantCulture);
        StepUpAuth.Stamp(props, SignInMethod.Totp, Now.AddMinutes(-2));
        var identity = new ClaimsIdentity(
            [new Claim(HttpOrganizationContext.UserIdClaim, userId.ToString(CultureInfo.InvariantCulture))],
            CookieAuthenticationDefaults.AuthenticationScheme);
        var context = new CookieValidatePrincipalContext(
            new DefaultHttpContext { RequestServices = services },
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(new ClaimsPrincipal(identity), props, CookieAuthenticationDefaults.AuthenticationScheme));

        await CookieSessionRevalidation.ValidateAsync(context);

        context.Principal.Should().NotBeNull();
        context.Principal!.FindFirst(ClaimTypes.Email).Should().NotBeNull("the claims were rebuilt from the row");
        StepUpAuth.Method(context.Principal).Should().Be(SignInMethod.Totp);
        StepUpAuth.StrongAuthAt(context.Principal).Should().Be(Now.AddMinutes(-2));
    }

    private sealed class NoOpAuthenticationService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    // ---- The challenge state a code endpoint may act on -----------------------

    private (HttpContext Http, IDataProtectionProvider Protection) ChallengeContext(int currentUserId)
    {
        var services = new ServiceCollection()
            .AddSingleton<IOrganizationContext>(new AmbientOrganizationContext { CurrentUserId = currentUserId, CurrentOrganizationId = 1 })
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        return (http, _db.DataProtectionProvider);
    }

    private static string Cookie(HttpContext http)
    {
        var header = http.Response.Headers.SetCookie.ToString();
        var start = header.IndexOf('=') + 1;
        return Uri.UnescapeDataString(header[start..header.IndexOf(';')]);
    }

    [Fact]
    public void A_code_endpoint_refuses_a_method_the_account_never_enrolled_and_another_users_step_up()
    {
        var (issue, protection) = ChallengeContext(currentUserId: 7);
        EndpointHelpers.SetMfaPendingCookie(issue, protection,
            new EndpointHelpers.MfaPending(7, TotpEnabled: false, EmailMfaEnabled: true, Now, "/", StepUp: true));
        var raw = Cookie(issue);

        var (mine, _) = ChallengeContext(currentUserId: 7);
        mine.Request.Headers.Cookie = $"{EndpointHelpers.MfaPendingCookieName}={Uri.EscapeDataString(raw)}";
        AccountMfaEndpoints.ReadChallengeState(mine, protection, _clock, requiresEmail: true).Should().NotBeNull();
        AccountMfaEndpoints.ReadChallengeState(mine, protection, _clock, requiresTotp: true)
            .Should().BeNull("an authenticator code cannot confirm an account without an authenticator");

        var (theirs, _) = ChallengeContext(currentUserId: 8);
        theirs.Request.Headers.Cookie = $"{EndpointHelpers.MfaPendingCookieName}={Uri.EscapeDataString(raw)}";
        AccountMfaEndpoints.ReadChallengeState(theirs, protection, _clock, requiresEmail: true)
            .Should().BeNull("a step-up state replayed under another user's cookie must not verify for them");
    }

    // ---- Microsoft step-up: auth_time is enforced only when Microsoft sends it ----

    [Fact]
    public void A_microsoft_step_up_is_accepted_without_auth_time_and_checked_when_it_is_there()
    {
        var issuedAt = Now;
        // Entra omits auth_time unless the registration lists it as an
        // optional claim; refusing on absence locked Microsoft users out.
        EntraAuthEndpoints.StepUpReauthenticated(null, issuedAt).Should().BeTrue();
        EntraAuthEndpoints.StepUpReauthenticated(Now.AddSeconds(30), issuedAt).Should().BeTrue("signed in after the step-up started");
        EntraAuthEndpoints.StepUpReauthenticated(Now.AddSeconds(-30), issuedAt).Should().BeTrue("a minute of clock skew is allowed");
        EntraAuthEndpoints.StepUpReauthenticated(Now.AddMinutes(-10), issuedAt).Should().BeFalse("Microsoft reused a session from before the step-up");
    }

    // ---- Passkey user verification flag ---------------------------------------

    [Fact]
    public void The_user_verified_bit_is_read_from_the_authenticator_data_flags()
    {
        static Fido2NetLib.AuthenticatorAssertionRawResponse With(byte flags)
        {
            var authData = new byte[37];
            authData[32] = flags;
            return new Fido2NetLib.AuthenticatorAssertionRawResponse
            {
                Response = new Fido2NetLib.AuthenticatorAssertionRawResponse.AssertionResponse
                {
                    AuthenticatorData = authData, ClientDataJson = [], Signature = [],
                },
            };
        }

        PasskeyService.UserVerifiedFlag(With(0x05)).Should().BeTrue("UP and UV");
        PasskeyService.UserVerifiedFlag(With(0x01)).Should().BeFalse("UP alone is a touch, not a check");
        PasskeyService.UserVerifiedFlag(new Fido2NetLib.AuthenticatorAssertionRawResponse()).Should().BeFalse("no data, no verification");
    }

    // ---- Consent must have been approved from a fresh session -----------------

    [Fact]
    public async Task A_bearer_session_counts_only_when_its_consent_was_approved_with_a_second_factor()
    {
        var userId = await SeedUserAsync();
        await using (var seed = _db.NewContext())
        {
            seed.OAuthConsents.Add(new OAuthConsent
            {
                UserId = userId, OrganizationId = TestDb.DefaultOrgId, ClientId = "old-client",
                ScopesGranted = "mcp", GrantedAt = Now.AddDays(-30),
            });
            seed.OAuthConsents.Add(new OAuthConsent
            {
                UserId = userId, OrganizationId = TestDb.DefaultOrgId, ClientId = "new-client",
                ScopesGranted = "mcp", GrantedAt = Now, StrongAuthAt = Now.AddMinutes(-1),
            });
            await seed.SaveChangesAsync();
        }
        await using var ctx = _db.NewContext();
        var consents = new OAuthConsentService(ctx);

        static ClaimsPrincipal Bearer(int userId, string? clientId)
        {
            var claims = new List<Claim> { new(HttpOrganizationContext.UserIdClaim, userId.ToString(CultureInfo.InvariantCulture)) };
            if (clientId is not null) claims.Add(new(OpenIddictConstants.Claims.ClientId, clientId));
            return new(new ClaimsIdentity(claims, "test"));
        }

        (await consents.WasApprovedWithStrongAuthAsync(Bearer(userId, "new-client"))).Should().BeTrue();
        (await consents.WasApprovedWithStrongAuthAsync(Bearer(userId, "old-client")))
            .Should().BeFalse("approved before the org marked a tool, so the person never confirmed");
        (await consents.WasApprovedWithStrongAuthAsync(Bearer(userId, null)))
            .Should().BeFalse("a token naming no client fails closed");
    }

    // ---- The action-level guard ----------------------------------------------

    private ToolEnablement ToolsFor(AppDbContext ctx, ClaimsPrincipal? user) =>
        new(TestDb.EverythingEnabled(),
            new TestDb.FixedHttpContextAccessor(user is null ? null : new DefaultHttpContext { User = user }),
            ctx, _db.OrgContext, _clock);

    [Fact]
    public async Task The_guard_refuses_a_stale_cookie_session_on_a_marked_tool_and_nothing_else()
    {
        await MarkAsync(ToolKey.Releases);
        await using var ctx = _db.NewContext();

        var stale = () => ToolsFor(ctx, CookieUser(1, 1)).EnsureStepUpAsync(ToolKey.Releases);
        (await stale.Should().ThrowAsync<StepUpRequiredException>()).Which.Errors.Keys.Should().Contain("StepUp");

        await ToolsFor(ctx, CookieUser(1, 1, strongAt: Now.AddMinutes(-3))).EnsureStepUpAsync(ToolKey.Releases);
        await ToolsFor(ctx, CookieUser(1, 1)).EnsureStepUpAsync(ToolKey.Cookbook);
        await ToolsFor(ctx, null).EnsureStepUpAsync(ToolKey.Releases);
        await ToolsFor(ctx, CookieUser(1, 1, pat: true)).EnsureStepUpAsync(ToolKey.Releases);
    }

    // ---- Changing the rule needs a fresh session --------------------------------

    [Fact]
    public async Task Marking_or_unmarking_a_tool_needs_a_fresh_session()
    {
        await using var ctx = _db.NewContext();
        var stale = new TestDb.FixedHttpContextAccessor(new DefaultHttpContext { User = CookieUser(1, 1) });
        var fresh = new TestDb.FixedHttpContextAccessor(new DefaultHttpContext { User = CookieUser(1, 1, strongAt: Now) });
        var tools = new ToolEnablement(TestDb.EverythingEnabled(), fresh, ctx, _db.OrgContext, _clock);

        var refused = () => NewOrgAdmin(ctx, tools, stale).SetStepUpToolsAsync([ToolKey.Releases]);
        (await refused.Should().ThrowAsync<Domain.ValueObjects.PlanValidationException>()).Which.Errors.Keys.Should().Contain("StepUp");

        await NewOrgAdmin(ctx, tools, fresh).SetStepUpToolsAsync([ToolKey.Releases]);
        (await NewOrgAdmin(ctx, tools, fresh).GetToolsViewAsync()).StepUpTools.Should().BeEquivalentTo([ToolKey.Releases]);

        var unmark = () => NewOrgAdmin(ctx, tools, stale).SetStepUpToolsAsync([]);
        await unmark.Should().ThrowAsync<Domain.ValueObjects.PlanValidationException>("switching the rule off is the change a stolen session would make");

        var stretch = () => NewOrgAdmin(ctx, tools, stale).SetStepUpWindowAsync(600);
        await stretch.Should().ThrowAsync<Domain.ValueObjects.PlanValidationException>("stretching the window is the other change a stolen session would make");
        await NewOrgAdmin(ctx, tools, fresh).SetStepUpWindowAsync(30);
        (await NewOrgAdmin(ctx, tools, fresh).GetToolsViewAsync()).StepUpWindowMinutes.Should().Be(30);
    }

    [Fact]
    public async Task Letting_ai_assistants_deploy_to_production_needs_a_fresh_session_once_any_tool_is_marked()
    {
        await using var ctx = _db.NewContext();
        var stale = new TestDb.FixedHttpContextAccessor(new DefaultHttpContext { User = CookieUser(1, 1) });
        var fresh = new TestDb.FixedHttpContextAccessor(new DefaultHttpContext { User = CookieUser(1, 1, strongAt: Now) });
        var tools = new ToolEnablement(TestDb.EverythingEnabled(), fresh, ctx, _db.OrgContext, _clock);

        // Nothing marked: the organisation asks for no second factor, so neither does this.
        await NewOrgAdmin(ctx, tools, stale).SetAgentsMayDeployToProductionAsync(true);
        await NewOrgAdmin(ctx, tools, stale).SetAgentsMayDeployToProductionAsync(false);

        await MarkAsync(ToolKey.Cookbook);
        var allow = () => NewOrgAdmin(ctx, tools, stale).SetAgentsMayDeployToProductionAsync(true);
        (await allow.Should().ThrowAsync<Domain.ValueObjects.PlanValidationException>()).Which.Errors.Keys.Should().Contain("StepUp");

        await NewOrgAdmin(ctx, tools, fresh).SetAgentsMayDeployToProductionAsync(true);
        await NewOrgAdmin(ctx, tools, stale).SetAgentsMayDeployToProductionAsync(false);
        (await NewOrgAdmin(ctx, tools, fresh).GetToolsViewAsync()).AgentsMayDeployToProduction
            .Should().BeFalse("turning a protection back on never needs a second factor");
    }

    [Fact]
    public async Task The_window_must_be_between_a_minute_and_a_day()
    {
        await using var ctx = _db.NewContext();
        var fresh = new TestDb.FixedHttpContextAccessor(new DefaultHttpContext { User = CookieUser(1, 1, strongAt: Now) });
        var tools = new ToolEnablement(TestDb.EverythingEnabled(), fresh, ctx, _db.OrgContext, _clock);

        foreach (var bad in new[] { 0, -5, 24 * 60 + 1 })
        {
            var act = () => NewOrgAdmin(ctx, tools, fresh).SetStepUpWindowAsync(bad);
            (await act.Should().ThrowAsync<Domain.ValueObjects.PlanValidationException>()).Which.Errors.Keys.Should().Contain("StepUpWindow");
        }
        await NewOrgAdmin(ctx, tools, fresh).SetStepUpWindowAsync(24 * 60);
        (await tools.StepUpWindowAsync()).Should().Be(TimeSpan.FromHours(24));
    }

    private ALDevToolbox.Services.Organizations.OrganizationAdminService NewOrgAdmin(AppDbContext ctx, ToolEnablement tools, IHttpContextAccessor http) =>
        new(ctx, _db.OrgContext, _db.McpAvailability,
            new AuthService(ctx, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance, _clock),
            _db.NewOrganizationConfigService(ctx), _db.DataProtectionProvider, tools, http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ALDevToolbox.Services.Organizations.OrganizationAdminService>.Instance);
}
