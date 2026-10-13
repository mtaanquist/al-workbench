using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// The strong-auth gate through the real pipeline, for a member of an
/// organisation that requires two-factor or a passkey who has not set one up
/// yet. <see cref="StrongAuthGateTests"/> drives the middleware alone; these
/// need the real routing (which endpoint a path resolves to) and the real
/// token-create form.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class StrongAuthGateEndToEndTests : IDisposable
{
    private const string Email = "consultant@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public StrongAuthGateEndToEndTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task An_unenrolled_member_cannot_mint_an_access_token()
    {
        // The token outlives the session and is never gated itself, so
        // minting one would sidestep the requirement (#1206).
        using var client = await SignedInUnenrolledAsync();
        using var page = await client.GetAsync("/account/ai");
        page.StatusCode.Should().Be(HttpStatusCode.OK, "the page the form lives on stays reachable");

        using var response = await client.PostAsync("/auth/account/access-tokens/create", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Name", "CRONUS laptop"),
            new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(await page.Content.ReadAsStringAsync())),
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using var read = _db.NewContext();
        (await read.PersonalAccessTokens.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    private async Task<HttpClient> SignedInUnenrolledAsync()
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
            var settings = await seed.OrganizationSettings.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                seed.OrganizationSettings.Add(settings);
            }
            settings.RequireStrongAuth = true;
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
}
