using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Each account tab has its own address (#1088): /account/security, with Profile the
/// bare /account. The ?section= links in sent emails and bookmarks, and the redirects
/// that only carry a result code, settle on that address with the rest of the query
/// kept. Through the real routing and prerender, which is where the redirect happens.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class AccountTabAddressTests : IDisposable
{
    private const string Email = "consultant@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData("/account?section=security", "/account/security")]
    [InlineData("/account?section=notifications", "/account/notifications")]
    [InlineData("/account?section=repos&ok=github-linked", "/account/repos?ok=github-linked")]
    [InlineData("/account?section=profile", "/account")]
    [InlineData("/account?ok=password", "/account/security?ok=password")]
    [InlineData("/account?required=1", "/account/security?required=1")]
    [InlineData("/account/profile", "/account")]
    [InlineData("/account/Security", "/account/security")]
    [InlineData("/account/nonsense", "/account")]
    [InlineData("/account/2", "/account")]
    [InlineData("/account/security,notifications", "/account")]
    [InlineData("/account?section=security&err=Totp&msg=Can%27t%20do%20A%26B%2Bc", "/account/security?err=Totp&msg=Can%27t%20do%20A%26B%2Bc")]
    [InlineData("/account?ok=x&section=ai&msg=y", "/account/ai?ok=x&msg=y")]
    [InlineData("/account?err=NewPassword&msg=Too%20short", "/account/security?err=NewPassword&msg=Too%20short")]
    [InlineData("/account?ok=passkey-added", "/account/security?ok=passkey-added")]
    public async Task An_address_settles_on_the_tab_s_own(string asked, string settled)
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAsync(factory);

        using var response = await client.GetAsync(asked);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        PathAndQuery(response.Headers.Location!).Should().Be(settled);
    }

    [Theory]
    [InlineData("/account", "Profile")]
    [InlineData("/account/notifications", "Notifications")]
    [InlineData("/account/security?ok=password", "Sign-in &amp; security")]
    [InlineData("/account/ai", "AI assistants")]
    [InlineData("/account/repos", "Repository access")]
    public async Task A_tab_s_address_opens_on_it(string address, string tab)
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAsync(factory);

        using var page = await client.GetAsync(address);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        Regex.Match(html, @"<a class=""header-tab is-active"" href=""([^""]+)""[^>]*>([^<]+)</a>")
            .Groups.Cast<Group>().Skip(1).Select(g => g.Value)
            .Should().Equal(address.Split('?')[0], tab);
    }

    private static string PathAndQuery(Uri location) =>
        location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;

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
