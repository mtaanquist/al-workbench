using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The maintenance gate a restore raises (#1279), through the real pipeline:
/// everyone but a SiteAdmin gets the 503 page with a <c>Retry-After</c>, while the
/// health probes and the SiteAdmin console stay reachable so the operator can
/// watch the restore and the proxy does not flap the container.
/// <c>MaintenanceModeStateTests</c> covers the flag itself.
/// </summary>
public sealed class MaintenanceModeMiddlewareTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string MaintenancePage = "Maintenance in progress";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task During_a_restore_everyone_but_a_site_admin_gets_the_503_and_the_probes_stay_reachable()
    {
        await SeedUserAsync("consultant@cronus.example", isSiteAdmin: false);
        await SeedUserAsync("operator@cronus.example", isSiteAdmin: true);
        using var factory = new EndpointFactory(_db);
        // Signed in before the restore starts: the login page is behind the gate too.
        using var consultant = await SignInAsync(factory, "consultant@cronus.example");
        using var siteAdmin = await SignInAsync(factory, "operator@cronus.example");
        using var anonymous = factory.CreateClient();

        var maintenance = factory.Services.GetRequiredService<MaintenanceModeState>();
        maintenance.Enter("Restoring from a backup.");
        try
        {
            await AssertGatedAsync(anonymous, "/");
            await AssertGatedAsync(consultant, "/");
            await AssertGatedAsync(consultant, "/solutions");

            await AssertNotGatedAsync(anonymous, "/healthz");
            await AssertNotGatedAsync(anonymous, "/readyz");
            await AssertNotGatedAsync(siteAdmin, "/");
            await AssertNotGatedAsync(siteAdmin, "/site-admin");
        }
        finally
        {
            maintenance.Exit();
        }

        await AssertNotGatedAsync(consultant, "/");
    }

    /// <summary>
    /// The reason names the backup file and is for the SiteAdmin. Everyone else
    /// gets generic wording, and no promise of a quick return while a failed
    /// restore holds the site closed (#1321).
    /// </summary>
    [Fact]
    public async Task The_503_page_keeps_the_reason_to_itself_and_does_not_promise_a_quick_return_after_a_failed_restore()
    {
        using var factory = new EndpointFactory(_db);
        using var anonymous = factory.CreateClient();
        var maintenance = factory.Services.GetRequiredService<MaintenanceModeState>();
        try
        {
            maintenance.Enter("Restoring backup aldevtoolbox-20260701T020000000Z-adhoc.dump");
            using (var response = await anonymous.GetAsync("/", TestContext.Current.CancellationToken))
            {
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                body.Should().NotContain("aldevtoolbox-20260701T020000000Z-adhoc.dump");
                body.Should().Contain("It will be back shortly.");
            }

            maintenance.HoldAfterFailedRestore("Restoring backup aldevtoolbox-20260701T020000000Z-adhoc.dump failed.");
            using (var response = await anonymous.GetAsync("/", TestContext.Current.CancellationToken))
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                body.Should().NotContain("aldevtoolbox-20260701T020000000Z-adhoc.dump");
                body.Should().NotContain("shortly");
                body.Should().Contain("closed while a site administrator restores it from a backup");
            }
        }
        finally
        {
            maintenance.Exit();
        }
    }

    /// <summary>
    /// The OAuth token endpoints and the Microsoft sign-in callback are handled
    /// inside authentication, ahead of the gate, and would write (a refresh
    /// grant's token rows, a first sign-in's user). They get the maintenance
    /// page before authentication runs (#1571). The personal access token
    /// side is pinned in <c>PatAuthenticationHandlerTests</c>.
    /// </summary>
    [Fact]
    public async Task During_a_restore_the_token_endpoints_and_sign_in_callbacks_are_closed_before_they_can_write()
    {
        using var factory = new EndpointFactory(_db);
        using var anonymous = factory.CreateClient();
        var maintenance = factory.Services.GetRequiredService<MaintenanceModeState>();
        maintenance.Enter("Restoring from a backup.");
        try
        {
            foreach (var path in new[] { "/oauth/token", "/oauth/revoke", "/oauth/introspect" })
            {
                using var response = await anonymous.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = "a-refresh-token",
                    ["client_id"] = "cronus-assistant",
                }), TestContext.Current.CancellationToken);
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, path);
                (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain(MaintenancePage);
            }

            await AssertGatedAsync(anonymous, "/signin-microsoft?code=abc&state=xyz");

            // The authorization request's client-metadata lookup writes the client row
            // inside authentication, and end-session is validated there too (#1598).
            await AssertGatedAsync(anonymous,
                "/oauth/authorize?client_id=https%3A%2F%2Fclaude.ai%2Foauth%2Fmcp-oauth-client-metadata"
                + "&response_type=code&redirect_uri=https%3A%2F%2Fclaude.ai%2Fapi%2Fmcp%2Fauth_callback"
                + "&code_challenge=abc&code_challenge_method=S256&scope=mcp");
            await AssertGatedAsync(anonymous, "/oauth/logout");

            // An assistant on an access token gets the 503 and its Retry-After, not a 401.
            using var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
            mcp.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "aldt_pat_notarealtoken");
            using var mcpResponse = await anonymous.SendAsync(mcp, TestContext.Current.CancellationToken);
            mcpResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            mcpResponse.Headers.RetryAfter.Should().NotBeNull();
        }
        finally
        {
            maintenance.Exit();
        }
    }

    private static async Task AssertGatedAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, $"{path} is behind the gate");
        response.Headers.RetryAfter.Should().NotBeNull($"{path} tells the client when to come back");
        (await response.Content.ReadAsStringAsync()).Should().Contain(MaintenancePage);
    }

    private static async Task AssertNotGatedAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        // A probe may answer 503 for its own reasons; what matters is that the gate did not.
        response.Headers.RetryAfter.Should().BeNull($"{path} must pass the gate");
        (await response.Content.ReadAsStringAsync()).Should().NotContain(MaintenancePage);
    }

    private async Task SeedUserAsync(string email, bool isSiteAdmin)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = TestPasswords.Hash(Password),
            Role = isSiteAdmin ? UserRole.Admin : UserRole.User,
            IsSiteAdmin = isSiteAdmin,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory, string email)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
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

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url);
        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var match = Regex.Match(await page.Content.ReadAsStringAsync(),
            """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
