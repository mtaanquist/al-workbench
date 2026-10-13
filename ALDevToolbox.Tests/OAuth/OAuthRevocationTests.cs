using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Disconnecting an assistant, or deleting its client, takes its access token
/// away at once rather than when the token expires an hour later (#1246).
/// Through the real app: a person signs in, approves a registered client, the
/// client trades the code for an access token and calls <c>/mcp</c> with it.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class OAuthRevocationTests : IDisposable
{
    private const string Email = "consultant@cronus.example";
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "http://127.0.0.1:41999/callback";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Disconnecting_the_assistant_stops_its_access_token_at_once()
    {
        await EnableMcpAsync();
        using var factory = new EndpointFactory(_db);
        using var browser = await SignedInAsync(factory);
        var (clientId, accessToken) = await ConnectAssistantAsync(browser);
        using var assistant = factory.CreateClient();
        (await CallMcpAsync(assistant, accessToken)).Should().NotBe(HttpStatusCode.Unauthorized,
            "the freshly issued token works before it is revoked");

        using (var scope = factory.Services.CreateScope())
        {
            await using var read = _db.NewContext();
            var consentId = await read.OAuthConsents.IgnoreQueryFilters()
                .Where(c => c.ClientId == clientId).Select(c => c.Id).SingleAsync();
            var admin = scope.ServiceProvider.GetRequiredService<OAuthClientAdminService>();
            await admin.RevokeConsentAsync(consentId, actorUserId: 0, ignoreOrgScope: true);
        }

        (await CallMcpAsync(assistant, accessToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Deleting_the_client_stops_its_access_token_at_once()
    {
        await EnableMcpAsync();
        using var factory = new EndpointFactory(_db);
        using var browser = await SignedInAsync(factory);
        var (clientId, accessToken) = await ConnectAssistantAsync(browser);
        using var assistant = factory.CreateClient();
        (await CallMcpAsync(assistant, accessToken)).Should().NotBe(HttpStatusCode.Unauthorized);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<OAuthClientAdminService>().DeleteClientAsync(clientId);
        }

        (await CallMcpAsync(assistant, accessToken)).Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpStatusCode> CallMcpAsync(HttpClient client, string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    /// <summary>
    /// Registers a client, approves it as the signed-in person and redeems the
    /// code, the way an assistant connects. Returns the client id and the
    /// access token.
    /// </summary>
    private static async Task<(string ClientId, string AccessToken)> ConnectAssistantAsync(HttpClient browser)
    {
        var registration = JsonSerializer.Serialize(new { redirect_uris = new[] { RedirectUri }, client_name = "CRONUS assistant" });
        using var registered = await browser.PostAsync("/oauth/register",
            new StringContent(registration, Encoding.UTF8, "application/json"));
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registeredDoc = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var clientId = registeredDoc.RootElement.GetProperty("client_id").GetString()!;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = "cronus",
        };

        // The consent page carries the antiforgery token the approval posts back.
        using var consentRedirect = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize!));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var consentPage = await browser.GetAsync(consentRedirect.Headers.Location!);
        consentPage.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = Regex.Match(await consentPage.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;

        var approval = new Dictionary<string, string>(authorize)
        {
            ["decision"] = "allow",
            ["__RequestVerificationToken"] = token,
        };
        using var approved = await browser.PostAsync("/oauth/authorize", new FormUrlEncodedContent(approval));
        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var code = QueryHelpers.ParseQuery(approved.Headers.Location!.Query)["code"].ToString();
        code.Should().NotBeEmpty();

        using var exchanged = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        }));
        var exchangedBody = await exchanged.Content.ReadAsStringAsync();
        exchanged.StatusCode.Should().Be(HttpStatusCode.OK, exchangedBody);
        using var tokenDoc = JsonDocument.Parse(exchangedBody);
        return (clientId, tokenDoc.RootElement.GetProperty("access_token").GetString()!);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

    private async Task<HttpClient> SignedInAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = Email,
                DisplayName = "Nils Consultant",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        var token = Regex.Match(await loginPage.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", Email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }
}
