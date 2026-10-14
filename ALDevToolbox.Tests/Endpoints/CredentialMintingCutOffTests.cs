using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
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

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public CredentialMintingCutOffTests()
    {
        _factory = new EndpointFactory(_db);
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
