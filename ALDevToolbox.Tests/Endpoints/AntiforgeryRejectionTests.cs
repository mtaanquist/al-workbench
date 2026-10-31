using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Antiforgery enforcement is a single-line opt-in
/// (<c>ValidateAntiforgeryAsync</c> at the top of every POST handler). The
/// framework owns the actual cookie/token shape; these tests pin that we wire
/// the validation up: a POST with no token gets the helper's fixed 400 and the
/// handler never runs. <c>UseAntiforgery()</c> does not cover a handler that
/// reads the form itself (#671), so the call is the only guard.
///
/// <para>The cases share one host (#1536, #1582), each with a user of its own. A
/// refused POST changes nothing, which is what every case asserts.</para>
/// </summary>
public sealed class AntiforgeryRejectionTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";
    private const string RefusalBody = "Antiforgery validation failed. Reload the form and try again.";

    /// <summary>
    /// POST endpoints that do not take the browser's antiforgery token, each
    /// with the reason. Anything not listed must refuse a token-less POST with
    /// <see cref="RefusalBody"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotBrowserForms =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["/mcp/"] = "bearer-token API for assistants; a browser cookie is not accepted there (McpEnabledToggleTests)",
            ["/github/webhook"] = "called by GitHub and authenticated by the HMAC signature",
            ["/oauth/register"] = "anonymous OAuth client registration; JSON, no cookie involved",
            ["/oauth/authorize"] = "OpenIddict refuses a malformed request before the handler; the token check behind it has its own test below",
            ["/api/sealed/{token}/reveal"] = "a Sealed recipient has no account or session to forge; pinned to the link's token",
            ["/api/compare/diff"] = "stateless diff for the in-browser compare utility; changes nothing",
            ["/auth/passkey/login"] = "WebAuthn JSON ceremony; the signed challenge is the anti-replay proof",
            ["/auth/passkey/login/options"] = "WebAuthn JSON ceremony; hands out a challenge and changes nothing",
            ["/auth/step-up/passkey"] = "WebAuthn JSON ceremony; the signed challenge is the anti-replay proof",
            ["/auth/step-up/passkey/options"] = "WebAuthn JSON ceremony; hands out a challenge and changes nothing",
        };

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _email = SharedEndpointHost.UniqueEmail("mette");

    public AntiforgeryRejectionTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
    }

    /// <summary>
    /// The login case posts a real user's real password, so a handler that ran
    /// would visibly sign them in. The others would redirect with an
    /// <c>ok=</c>/<c>err=</c> outcome; only the helper answers this 400 (#1425).
    /// </summary>
    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signup")]
    [InlineData("/auth/forgot-password")]
    [InlineData("/auth/logout")]
    public async Task Post_without_antiforgery_token_is_rejected(string path)
    {
        await SeedUserAsync();
        using var client = _factory.CreateClient();
        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", _email),
            new KeyValuePair<string, string>("Password", Password),
        });

        using var response = await client.PostAsync(path, body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"{path} must not run its handler without a valid antiforgery token");
        (await response.Content.ReadAsStringAsync()).Should().Be(RefusalBody);
        var setCookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.ToArray()
            : Array.Empty<string>();
        setCookie.Should().NotContain(c => c.StartsWith("alwb_auth=", StringComparison.Ordinal),
            "an auth cookie shipped on an antiforgery failure means the handler ran");
    }

    /// <summary>
    /// Drift guard over every POST in the endpoint table (#1425): signed in as
    /// an Admin who is also a SiteAdmin, so every role gate lets the request
    /// through to the handler, a POST without a token must get the helper's
    /// 400. A new form handler that forgets the call fails here.
    /// </summary>
    [Fact]
    public async Task Every_form_post_refuses_a_request_without_a_token()
    {
        await SeedUserAsync(isSiteAdmin: true);
        using var client = _factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using (var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = _email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        })))
        {
            login.StatusCode.Should().Be(HttpStatusCode.Redirect);
            login.Headers.Location!.OriginalString.Should().NotContain("err=");
        }

        var posts = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true)
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>() is null)
            .Select(e => e.RoutePattern.RawText!)
            .Distinct()
            // Sign out last, so a logout that ran cannot turn every later row anonymous.
            .OrderBy(r => r == "/auth/logout")
            .ThenBy(r => r, StringComparer.Ordinal)
            .ToList();
        posts.Should().HaveCountGreaterThan(90, "the walk must actually reach the form handlers");
        NotBrowserForms.Keys.Where(k => !posts.Contains(k)).Should().BeEmpty(
            "each exception must name a POST endpoint that still exists");

        var failures = new List<string>();
        foreach (var route in posts.Where(r => !NotBrowserForms.ContainsKey(r)))
        {
            var path = Regex.Replace(route, @"\{[^}]+\}", "1");
            using var response = await client.PostAsync(path,
                new FormUrlEncodedContent(new Dictionary<string, string> { ["Name"] = "CRONUS" }));
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.BadRequest || body != RefusalBody)
            {
                failures.Add($"{route}: {(int)response.StatusCode} {response.Headers.Location} {body[..Math.Min(body.Length, 80)].ReplaceLineEndings(" ")}");
            }
        }

        string.Join(Environment.NewLine, failures).Should().BeEmpty(
            "a form POST must call ValidateAntiforgeryAsync before it does anything; "
            + "an endpoint that takes no browser form belongs in NotBrowserForms with the reason");
    }

    /// <summary>
    /// The OAuth consent POST binds <c>HttpContext</c> and reads the form by
    /// hand, so <c>UseAntiforgery()</c> never covers it — the handler has to
    /// call the helper itself (issue #671). The request below is a *valid*
    /// authorisation request for a freshly registered client, so OpenIddict's
    /// middleware passes it through to our handler; only the antiforgery token
    /// is missing. Without the guard the handler runs and redirects to the
    /// login challenge, so the 400 (and its body) is what pins the fix.
    /// </summary>
    [Fact]
    public async Task OAuth_authorize_post_without_antiforgery_token_is_rejected()
    {
        using var client = _factory.CreateClient();

        // Anonymous Dynamic Client Registration — the same door the attack in
        // the issue walks through.
        using var registration = await client.PostAsync(
            "/oauth/register",
            new StringContent(
                """{"redirect_uris":["http://127.0.0.1:41999/callback"],"client_name":"CRONUS test client"}""",
                System.Text.Encoding.UTF8,
                "application/json"));
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registered = System.Text.Json.JsonDocument.Parse(
            await registration.Content.ReadAsStringAsync());
        var clientId = registered.RootElement.GetProperty("client_id").GetString()!;

        var body = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("decision", "allow"),
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("redirect_uri", "http://127.0.0.1:41999/callback"),
            new KeyValuePair<string, string>("response_type", "code"),
            new KeyValuePair<string, string>("scope", "mcp"),
            new KeyValuePair<string, string>("code_challenge", "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM"),
            new KeyValuePair<string, string>("code_challenge_method", "S256"),
        });

        using var response = await client.PostAsync("/oauth/authorize", body);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the consent POST must refuse before running without a valid antiforgery token");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Antiforgery");
    }

    // ---- helpers -----------------------------------------------------------

    private async Task SeedUserAsync(bool isSiteAdmin = false)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = _email,
            DisplayName = "Mette",
            PasswordHash = _host.HashPassword(Password),
            Role = UserRole.Admin,
            IsSiteAdmin = isSiteAdmin,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
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
