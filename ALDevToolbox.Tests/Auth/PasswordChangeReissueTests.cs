using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// Changing your own password re-issues your cookie from the principal it
/// already carries. The claims on that cookie have to carry the new sign-in
/// time as well as the properties, or an open page, which only sees the
/// claims, would judge the session by the old one (#1567).
/// </summary>
public sealed class PasswordChangeReissueTests : IDisposable
{
    private const string Email = "sam.berg@cronus.example";
    private const string Password = "correct horse battery staple";
    private const string NewPassword = "a different long passphrase";

    private readonly TestDb _db = new();
    private readonly SignInRecorder _recorder = new();
    private readonly EndpointFactory _factory;

    public PasswordChangeReissueTests()
    {
        _factory = new EndpointFactory(_db, services =>
        {
            services.AddScoped<AuthenticationService>();
            services.AddScoped<IAuthenticationService>(sp =>
                new RecordingAuthenticationService(sp.GetRequiredService<AuthenticationService>(), _recorder));
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_reissued_cookie_carries_the_new_sign_in_time_on_its_claims()
    {
        var client = await SignedInAsync();
        var atSignIn = _recorder.SignIns.Should().ContainSingle().Subject;
        var originalSignIn = EndpointHelpers.SignedInAt(atSignIn.Principal);
        originalSignIn.Should().NotBe(DateTime.MinValue, "a fresh sign-in mirrors the time onto the claims");
        await Task.Delay(TimeSpan.FromMilliseconds(20));

        using var page = await client.GetAsync(RouteConstants.AccountSecurity);
        using var change = await client.PostAsync("/auth/account/password", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("CurrentPassword", Password),
            new KeyValuePair<string, string>("NewPassword", NewPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(await page.Content.ReadAsStringAsync())),
        }));
        change.StatusCode.Should().Be(HttpStatusCode.Redirect);
        change.Headers.Location!.OriginalString.Should().Contain("ok=password");

        var reissue = _recorder.SignIns.Last();
        var stamp = reissue.Properties!.Items[EndpointHelpers.SignedInAtKey];
        reissue.Principal.FindAll(EndpointHelpers.SignedInAtClaim).Should().ContainSingle()
            .Which.Value.Should().Be(stamp, "the claim and the properties have to agree");
        EndpointHelpers.SignedInAt(reissue.Principal).Should().BeAfter(originalSignIn);
    }

    private async Task<HttpClient> SignedInAsync()
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = Email,
                DisplayName = "Sam Berg",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", Email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(await loginPage.Content.ReadAsStringAsync())),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;

    private sealed class SignInRecorder
    {
        private readonly List<(ClaimsPrincipal Principal, AuthenticationProperties? Properties)> _signIns = new();

        public IReadOnlyList<(ClaimsPrincipal Principal, AuthenticationProperties? Properties)> SignIns
        {
            get { lock (_signIns) return _signIns.ToList(); }
        }

        public void Add(ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            lock (_signIns) _signIns.Add((principal, properties));
        }
    }

    /// <summary>The real service, with each cookie sign-in written down on the way through.</summary>
    private sealed class RecordingAuthenticationService(IAuthenticationService inner, SignInRecorder recorder) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => inner.AuthenticateAsync(context, scheme);
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.ChallengeAsync(context, scheme, properties);
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.ForbidAsync(context, scheme, properties);
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => inner.SignOutAsync(context, scheme, properties);

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties)
        {
            if (scheme is null or CookieAuthenticationDefaults.AuthenticationScheme) recorder.Add(principal, properties);
            return inner.SignInAsync(context, scheme, principal, properties);
        }
    }
}
