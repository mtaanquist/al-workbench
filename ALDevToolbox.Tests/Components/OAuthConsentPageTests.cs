using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/oauth/consent</c>: the list of what an assistant will be able to do shows only
/// the permissions AL Workbench grants, in the approver's words, however many others the
/// request names (#1438).
/// </summary>
public sealed class OAuthConsentPageTests : IDisposable
{
    private const string Email = "alice@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_the_permissions_this_site_grants_are_listed_whatever_else_is_asked_for()
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAsync(factory);

        using var page = await client.GetAsync(
            "/oauth/consent?client_id=claude&redirect_uri=http%3A%2F%2F127.0.0.1%3A41999%2Fcallback"
            + "&scope=offline_access%20files%3Awrite%20mcp", TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        new HtmlParser().ParseDocument(html).QuerySelectorAll(".check-list .check strong")
            .Select(s => s.TextContent).Should().Equal("Use AL Workbench tools", "Stay signed in");
    }

    /// <summary>
    /// The page has no wording for a scope it does not know and refuses to render one,
    /// so a scope added to the allow-list without wording fails here rather than on the
    /// page a person is about to approve.
    /// </summary>
    [Fact]
    public async Task Every_scope_this_site_grants_has_wording_on_the_consent_page()
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAsync(factory);
        var scopes = Uri.EscapeDataString(string.Join(' ', ALDevToolbox.Endpoints.OAuthEndpoints.SupportedScopes));

        using var page = await client.GetAsync(
            "/oauth/consent?client_id=claude&redirect_uri=http%3A%2F%2F127.0.0.1%3A41999%2Fcallback&scope=" + scopes,
            TestContext.Current.CancellationToken);

        page.StatusCode.Should().Be(HttpStatusCode.OK, "a scope without wording makes the page fail");
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var listed = new HtmlParser().ParseDocument(html).QuerySelectorAll(".check-list .check").ToList();
        listed.Should().HaveCount(ALDevToolbox.Endpoints.OAuthEndpoints.SupportedScopes.Length);
        foreach (var item in listed)
        {
            var name = item.QuerySelector("strong")!.TextContent;
            name.Should().NotBeNullOrWhiteSpace();
            ALDevToolbox.Endpoints.OAuthEndpoints.SupportedScopes.Should().NotContain(name,
                "the protocol name means nothing to the person approving");
            item.TextContent.Should().MatchRegex(@" - \S", "each permission says what it lets the assistant do");
        }
    }

    [Fact]
    public async Task The_page_names_the_client_by_the_name_it_registered_not_its_id()
    {
        // #1617: a dynamically registered client's id is 32 random characters.
        using var factory = new EndpointFactory(_db);
        const string clientId = "3f9c0e5a7d2b4c1e8f6a9b0c2d4e6b21a";
        using (var scope = factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            await applications.CreateAsync(new OpenIddict.Abstractions.OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                DisplayName = "Claude",
                ClientType = OpenIddict.Abstractions.OpenIddictConstants.ClientTypes.Public,
                RedirectUris = { new Uri("http://127.0.0.1:41999/callback") },
            }, TestContext.Current.CancellationToken);
        }
        using var client = await SignedInAsync(factory);

        using var page = await client.GetAsync(
            $"/oauth/consent?client_id={clientId}&redirect_uri=http%3A%2F%2F127.0.0.1%3A41999%2Fcallback&scope=mcp",
            TestContext.Current.CancellationToken);

        var document = new HtmlParser().ParseDocument(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.QuerySelector("h1")!.TextContent.Trim().Should().Be("Authorise Claude");
        document.Body!.TextContent.Should().NotContain(clientId);
        document.Body.TextContent.Should().NotContain("Nothing outside this list",
            "the list does not name every tool, so it must not claim to be complete");
        document.QuerySelector(".check-list")!.TextContent.Should().Contain("deploy builds");
    }

    [Fact]
    public async Task A_client_the_site_does_not_know_is_named_by_its_id()
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAsync(factory);

        using var page = await client.GetAsync(
            "/oauth/consent?client_id=unregistered-client&redirect_uri=http%3A%2F%2F127.0.0.1%3A41999%2Fcallback&scope=mcp",
            TestContext.Current.CancellationToken);

        var document = new HtmlParser().ParseDocument(await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.QuerySelector("h1")!.TextContent.Trim().Should().Be("Authorise unregistered-client");
    }

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private async Task<HttpClient> SignedInAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = Email,
                DisplayName = "Alice",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(Password),
                Role = UserRole.User,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var login = await client.GetAsync("/login", TestContext.Current.CancellationToken);
        var token = TokenIn(await login.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        using var signIn = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", Email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }
}
