using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Approving an assistant hands it the user's tools for as long as its refresh
/// token lives, so when the organisation asks for a second factor on any tool
/// the consent needs a recent one too: the page sends a stale session to
/// step up before it shows the form, and the approval POST checks again for a
/// form left open past the window. Declining needs no confirmation. See
/// ".design/auth-and-audit.md", "Step-up for sensitive tools".
///
/// <para>The cases share one host (#1536, #1582). The tools marked for step-up
/// are organisation-wide, so each test works in an organisation of its own, with
/// a user of its own.</para>
/// </summary>
public sealed class OAuthConsentStepUpTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";
    private const string RedirectUri = "http://127.0.0.1:41999/callback";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _email = SharedEndpointHost.UniqueEmail("consent");

    /// <summary>This test's own organisation.</summary>
    private readonly int _orgId;

    public OAuthConsentStepUpTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
        _orgId = host.SeedOrganization();
    }

    [Fact]
    public async Task The_consent_page_sends_a_stale_session_to_step_up_and_keeps_the_request()
    {
        var (client, _, authorize) = await StartAsync();
        await MarkStepUpAsync(ToolKey.Releases);

        using var consentRedirect = await client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var consentUrl = consentRedirect.Headers.Location!.OriginalString;
        using var page = await client.GetAsync(consentUrl);

        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        // The page redirects through NavigateTo, which makes the URL absolute.
        var location = new Uri(new Uri("https://localhost/"), page.Headers.Location!).PathAndQuery;
        location.Should().StartWith(StepUpEndpoints.Path + "?return=");
        var back = Uri.UnescapeDataString(location[(location.IndexOf('=') + 1)..]);
        back.Should().StartWith("/oauth/consent?");
        QueryHelpers.ParseQuery(back[back.IndexOf('?')..])["client_id"].ToString().Should().Be(authorize["client_id"]);
    }

    [Fact]
    public async Task Approving_from_a_stale_session_is_sent_to_step_up_and_grants_nothing()
    {
        var (client, userId, authorize) = await StartAsync();
        var token = await ConsentFormTokenAsync(client, authorize);
        // Marked after the form rendered: the form sat open while an admin
        // changed the rule, or past the window.
        await MarkStepUpAsync(ToolKey.Releases);

        using var approved = await PostDecisionAsync(client, authorize, "allow", token);

        approved.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = approved.Headers.Location!.OriginalString;
        location.Should().StartWith(StepUpEndpoints.Path + "?return=");
        Uri.UnescapeDataString(location).Should().Contain("/oauth/consent?").And.Contain(authorize["client_id"]!);
        await using var read = _db.NewContext();
        (await read.OAuthConsents.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId)).Should().BeFalse();
    }

    [Fact]
    public async Task Declining_from_a_stale_session_is_not_sent_to_step_up()
    {
        var (client, userId, authorize) = await StartAsync();
        var token = await ConsentFormTokenAsync(client, authorize);
        await MarkStepUpAsync(ToolKey.Releases);

        using var declined = await PostDecisionAsync(client, authorize, "deny", token);

        declined.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = declined.Headers.Location!.OriginalString;
        location.Should().StartWith(RedirectUri).And.Contain("error=access_denied");
        await using var read = _db.NewContext();
        (await read.OAuthConsents.IgnoreQueryFilters().AnyAsync(c => c.UserId == userId)).Should().BeFalse();
    }

    /// <summary>A user signed in with a password only (never strong), and a registered client's authorize request.</summary>
    private async Task<(HttpClient Client, int UserId, Dictionary<string, string?> Authorize)> StartAsync()
    {
        await EnableMcpAsync();
        var userId = await SeedUserAsync(_email);
        var client = await SignInAsync(_email);

        var registration = JsonSerializer.Serialize(new { redirect_uris = new[] { RedirectUri }, client_name = "CRONUS assistant" });
        using var registered = await client.PostAsync("/oauth/register",
            new StringContent(registration, Encoding.UTF8, "application/json"));
        registered.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        var authorize = new Dictionary<string, string?>
        {
            ["client_id"] = doc.RootElement.GetProperty("client_id").GetString()!,
            ["redirect_uri"] = RedirectUri,
            ["response_type"] = "code",
            ["scope"] = "mcp",
            ["code_challenge"] = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            ["code_challenge_method"] = "S256",
            ["state"] = "cronus",
        };
        return (client, userId, authorize);
    }

    /// <summary>Renders the consent form while nothing is marked, and returns its antiforgery token.</summary>
    private static async Task<string> ConsentFormTokenAsync(HttpClient client, Dictionary<string, string?> authorize)
    {
        using var consentRedirect = await client.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", authorize));
        consentRedirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var page = await client.GetAsync(consentRedirect.Headers.Location!);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        return FormToken(await page.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> PostDecisionAsync(
        HttpClient client, Dictionary<string, string?> authorize, string decision, string token)
    {
        var form = authorize.ToDictionary(p => p.Key, p => p.Value!);
        form["decision"] = decision;
        form["__RequestVerificationToken"] = token;
        return client.PostAsync("/oauth/authorize", new FormUrlEncodedContent(form));
    }

    private async Task MarkStepUpAsync(params ToolKey[] tools)
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.FirstAsync(o => o.Id == _orgId);
        org.StepUpTools = ToolCatalog.Format(tools);
        await ctx.SaveChangesAsync();
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
            ReleaseDownloadDomainAllowlist: null, DisabledTools: Array.Empty<ToolKey>(),
            BuildConcurrency: null));
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = _orgId,
            Email = email,
            DisplayName = "Nils Consultant",
            PasswordHash = _host.HashPassword(Password),
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
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static string FormToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
