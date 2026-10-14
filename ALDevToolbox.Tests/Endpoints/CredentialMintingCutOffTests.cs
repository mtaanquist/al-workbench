using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The endpoints that hand out a credential outliving the cookie apply the
/// password-reset cut-off on the request itself (#1336). The ordinary check
/// runs at most every five minutes, so without this a cookie validated just
/// before the owner's reset could still create an access token, and the token
/// would be dated after the reset, so nothing would refuse it later.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class CredentialMintingCutOffTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "http://127.0.0.1:41999/callback";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public CredentialMintingCutOffTests()
    {
        // Passkeys configured, so the passkey leg reaches its filter rather
        // than failing to bind.
        _factory = new EndpointFactory(_db, environment: new Dictionary<string, string?>
        {
            ["Auth__WebAuthn__RpId"] = "localhost",
            ["Auth__WebAuthn__OriginsCsv"] = "https://localhost",
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_session_from_before_a_reset_cannot_create_an_access_token_inside_the_revalidation_window()
    {
        var userId = await SeedUserAsync("stale@cronus.example");
        var client = await SignInAsync("stale@cronus.example");
        // This request revalidates the cookie, so the next five minutes skip it.
        var token = await PageTokenAsync(client);

        // The owner resets the password from another browser.
        await using (var ctx = _db.NewContext())
        {
            var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
            user.CredentialsChangedAt = DateTime.UtcNow.AddMinutes(1);
            await ctx.SaveChangesAsync();
        }

        using var response = await PostFormAsync(client, "/auth/account/access-tokens/create", token,
            ("Name", "Stolen session"), ("TtlDays", "30"));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/login");
        await using var read = _db.NewContext();
        (await read.PersonalAccessTokens.IgnoreQueryFilters().CountAsync(p => p.UserId == userId)).Should().Be(0);
    }

    [Fact]
    public async Task A_current_session_still_creates_an_access_token()
    {
        var userId = await SeedUserAsync("current@cronus.example");
        var client = await SignInAsync("current@cronus.example");
        var token = await PageTokenAsync(client);

        using var response = await PostFormAsync(client, "/auth/account/access-tokens/create", token,
            ("Name", "Laptop"), ("TtlDays", "30"));

        response.Headers.Location!.OriginalString.Should().Be("/account/access-tokens/created");
        await using var read = _db.NewContext();
        (await read.PersonalAccessTokens.IgnoreQueryFilters().CountAsync(p => p.UserId == userId)).Should().Be(1);
    }

    [Fact]
    public async Task A_session_from_before_a_reset_gets_a_401_from_the_passkey_script_call()
    {
        var userId = await SeedUserAsync("stale-passkey@cronus.example");
        var client = await SignInAsync("stale-passkey@cronus.example");
        var token = await PageTokenAsync(client);
        await StampCredentialChangeAsync(userId);

        using var request = new HttpRequestMessage(HttpMethod.Post, AccountPasskeyEndpoints.RegistrationOptionsPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { password = Password }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("RequestVerificationToken", token);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Sign in again");
        response.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith(PasskeyService.RegistrationCookieName));
    }

    [Fact]
    public async Task A_session_from_before_a_reset_cannot_approve_an_assistant()
    {
        await EnableMcpAsync();
        var userId = await SeedUserAsync("stale-oauth@cronus.example");
        var client = await SignInAsync("stale-oauth@cronus.example");

        var registration = JsonSerializer.Serialize(new { redirect_uris = new[] { RedirectUri }, client_name = "CRONUS assistant" });
        using var registered = await client.PostAsync("/oauth/register",
            new StringContent(registration, Encoding.UTF8, "application/json"));
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registeredDoc = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var clientId = registeredDoc.RootElement.GetProperty("client_id").GetString()!;
        var authorize = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
            ["state"] = "cronus",
        };
        using var consentRedirect = await client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var consentPage = await client.GetAsync(consentRedirect.Headers.Location!);
        consentPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = FormToken(await consentPage.Content.ReadAsStringAsync());
        await StampCredentialChangeAsync(userId);

        var approval = authorize.ToDictionary(p => p.Key, p => p.Value!);
        approval["decision"] = "allow";
        approval["__RequestVerificationToken"] = token;
        using var approved = await client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(approval));

        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        approved.Headers.Location!.OriginalString.Should().Be("/login");
        await using var read = _db.NewContext();
        (await read.OAuthConsents.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task Connecting_a_microsoft_account_is_refused_when_a_reset_lands_during_the_round_trip()
    {
        // The handshake started from a current session; the owner reset the
        // password while this browser was at Microsoft.
        var userId = await SeedUserAsync("stale-link@cronus.example");
        var cookie = await SignInCookieAsync("stale-link@cronus.example");
        await StampCredentialChangeAsync(userId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("localhost");
        http.Request.Headers.Cookie = cookie;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        var properties = new AuthenticationProperties();
        properties.Items[EntraAuthEndpoints.LinkUserIdItem] = userId.ToString();
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("tid", "11111111-1111-1111-1111-111111111111"),
            new Claim("oid", "aaaaaaaa-0000-0000-0000-000000000001"),
            new Claim("preferred_username", "stale-link@cronus.example"),
        }, EntraAuthEndpoints.AuthenticationScheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), properties, EntraAuthEndpoints.AuthenticationScheme);
        var context = new TicketReceivedContext(http,
            new AuthenticationScheme(EntraAuthEndpoints.AuthenticationScheme, null, typeof(OpenIdConnectHandler)),
            new OpenIdConnectOptions(), ticket);

        await EntraAuthEndpoints.OnTicketReceivedAsync(context);

        http.Response.Headers.Location.ToString().Should().Be("/login");
        await using var read = _db.NewContext();
        (await read.UserExternalLogins.IgnoreQueryFilters().AnyAsync(l => l.UserId == userId)).Should().BeFalse();
    }

    private async Task StampCredentialChangeAsync(int userId)
    {
        await using var ctx = _db.NewContext();
        var user = await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        user.CredentialsChangedAt = DateTime.UtcNow.AddMinutes(1);
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// Signs in, makes one request so the session has just been revalidated,
    /// and returns the Cookie header the browser would send next.
    /// </summary>
    private async Task<string> SignInCookieAsync(string email)
    {
        var client = _factory.CreateClient(handleCookies: false);
        var jar = new Dictionary<string, string>();
        void Keep(HttpResponseMessage r)
        {
            if (!r.Headers.TryGetValues("Set-Cookie", out var set)) return;
            foreach (var pair in set.Select(c => c.Split(';')[0]))
            {
                var eq = pair.IndexOf('=');
                jar[pair[..eq]] = pair[(eq + 1)..];
            }
        }
        string Header() => string.Join("; ", jar.Select(kv => kv.Key + "=" + kv.Value));

        using var loginPage = await client.GetAsync("/login");
        Keep(loginPage);
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/login")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = Password,
                ["__RequestVerificationToken"] = token,
            }),
        };
        loginRequest.Headers.Add("Cookie", Header());
        using var login = await client.SendAsync(loginRequest);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        Keep(login);

        using var pageRequest = new HttpRequestMessage(HttpMethod.Get, "/account/security");
        pageRequest.Headers.Add("Cookie", Header());
        using var page = await client.SendAsync(pageRequest);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        Keep(page);
        return Header();
    }

    private async Task EnableMcpAsync()
    {
        await using var ctx = _db.NewContextWithAudit(TestDb.NewAuditInterceptor());
        var svc = new SystemSettingsService(ctx, _db.DataProtectionProvider, NullLogger<SystemSettingsService>.Instance, TimeProvider.System);
        await svc.SaveAsync(new SystemSettingsInput(
            SmtpHost: null, SmtpPort: null, SmtpUser: null,
            SmtpPassword: null, ClearSmtpPassword: false,
            SmtpFrom: null, SmtpFromName: null, SmtpUseStartTls: null, BannerText: null,
            BackupScheduleEnabled: true,
            BackupScheduleTimeUtc: new TimeOnly(2, 0),
            BackupRetentionCount: 14,
            PerTenantBackupRetentionCount: 30,
            DefaultStorageQuotaMb: null,
            IndexSizeMultiplier: 0.5m,
            McpEnabled: true,
            SignupEmailDomainAllowlist: null,
            ReleaseDownloadDomainAllowlist: null, DisabledTools: Array.Empty<ALDevToolbox.Domain.Tools.ToolKey>(),
            BuildConcurrency: null));
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = "Nils Consultant",
            PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = FormToken(await loginPage.Content.ReadAsStringAsync());
        using var login = await PostFormAsync(client, "/auth/login", token, ("Email", email), ("Password", Password));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> PageTokenAsync(HttpClient client)
    {
        using var page = await client.GetAsync("/account/security");
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return FormToken(await page.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string FormToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
