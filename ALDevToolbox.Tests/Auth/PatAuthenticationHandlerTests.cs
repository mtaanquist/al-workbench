using System.Security.Claims;
using System.Text.Encodings.Web;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Unit-level coverage for <see cref="PatAuthenticationHandler"/>: a valid
/// bearer token mounts the same claim names <see cref="HttpOrganizationContext"/>
/// expects from the cookie path; missing, malformed, or non-PAT bearer
/// headers fall through silently (NoResult) so another scheme may handle
/// them; revoked or tampered tokens fail authentication.
/// </summary>
public sealed class PatAuthenticationHandlerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 5, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private Task<User> SeedUserAsync() => SeedUserAsync(UserRole.Admin, "alice@example.com");

    private async Task<User> SeedUserAsync(UserRole role, string email)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = "ignored",
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user;
    }

    private async Task<PatAuthenticationHandler> NewHandlerAsync(
        HttpContext httpContext, ALDevToolbox.Services.Operations.MaintenanceModeState? maintenance = null)
    {
        var ctx = _db.NewContext();
        var service = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);

        var optionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new PatAuthenticationHandler(
            optionsMonitor, NullLoggerFactory.Instance, UrlEncoder.Default, service, maintenance);
        await handler.InitializeAsync(
            new AuthenticationScheme(PatAuthenticationHandler.AuthenticationScheme, displayName: null, handlerType: typeof(PatAuthenticationHandler)),
            httpContext);
        return handler;
    }

    [Fact]
    public async Task During_a_restore_a_token_is_left_unvalidated_and_its_last_use_unwritten()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "cursor", expiresAt: null);
        }
        var maintenance = new ALDevToolbox.Services.Operations.MaintenanceModeState();
        maintenance.Enter("Restoring backup test.dump");
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer " + issued.Plaintext;

        var result = await (await NewHandlerAsync(httpContext, maintenance)).AuthenticateAsync();

        result.None.Should().BeTrue("the request is left anonymous for the maintenance page (#1571)");
        await using var check = _db.NewContext();
        check.PersonalAccessTokens.Single(t => t.Id == issued.Id).LastUsedAt.Should().BeNull();
    }

    [Fact]
    public async Task Valid_bearer_mounts_principal_with_organisation_claims()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "cursor", expiresAt: null);
        }

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer " + issued.Plaintext;

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirst(HttpOrganizationContext.UserIdClaim)!.Value.Should().Be(user.Id.ToString());
        principal.FindFirst(HttpOrganizationContext.OrganizationIdClaim)!.Value.Should().Be(user.OrganizationId.ToString());
        principal.FindFirst(ClaimTypes.Email)!.Value.Should().Be("alice@example.com");
        principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be(UserRole.Admin.ToString());
        principal.FindFirst("pat_id")!.Value.Should().Be(issued.Id.ToString());
        principal.Identity!.AuthenticationType.Should().Be(PatAuthenticationHandler.AuthenticationScheme);
    }

    [Fact]
    public async Task Editor_pat_mounts_editor_role_claim()
    {
        var user = await SeedUserAsync(UserRole.Editor, "ed@example.com");
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "cursor", expiresAt: null);
        }

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer " + issued.Plaintext;

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        var principal = result.Principal!;
        principal.FindFirst(ClaimTypes.Role)!.Value.Should().Be("Editor");
        principal.IsInRole("Editor").Should().BeTrue();
        principal.IsInRole("Admin").Should().BeFalse();
    }

    [Fact]
    public async Task Missing_authorization_header_returns_no_result()
    {
        var httpContext = new DefaultHttpContext();
        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Non_pat_bearer_returns_no_result_so_another_scheme_can_handle_it()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer some-other-jwt-or-opaque-token";

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Non_bearer_scheme_returns_no_result()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Basic dXNlcjpwYXNz";

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Tampered_pat_fails_authentication()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "cursor", expiresAt: null);
        }
        var tampered = issued.Plaintext[..^4] + "ZZZZ";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer " + tampered;

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    [Fact]
    public async Task Revoked_pat_fails_authentication()
    {
        var user = await SeedUserAsync();
        IssuedToken issued;
        await using (var ctx = _db.NewContext())
        {
            var svc = new PersonalAccessTokenService(ctx, _clock, NullLogger<PersonalAccessTokenService>.Instance);
            issued = await svc.IssueAsync(user.Id, user.OrganizationId, "cursor", expiresAt: null);
            await svc.RevokeAsync(issued.Id);
        }

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Authorization"] = "Bearer " + issued.Plaintext;

        var handler = await NewHandlerAsync(httpContext);
        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
    }

    private sealed class TestOptionsMonitor<TOptions> : IOptionsMonitor<TOptions> where TOptions : class, new()
    {
        public TestOptionsMonitor(TOptions current) { CurrentValue = current; }
        public TOptions CurrentValue { get; }
        public TOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TOptions, string?> listener) => null;
    }
}
