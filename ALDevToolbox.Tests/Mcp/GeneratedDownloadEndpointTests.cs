using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Mcp;

/// <summary>
/// The download link a generate_* tool hands back (#1508), through the real pipeline:
/// the endpoint takes the user and organisation from the signed-in principal, not from
/// anything in the request.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GeneratedDownloadEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public GeneratedDownloadEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_link_serves_the_zip_to_the_user_who_generated_it_and_to_nobody_else()
    {
        var ownerId = await SeedUserAsync("owner@cronus.example");
        await SeedUserAsync("colleague@cronus.example");
        var link = _factory.Services.GetRequiredService<GeneratedDownloadStore>()
            .Put(TestDb.DefaultOrgId, ownerId, "CRONUS.zip", [1, 2, 3])!;

        using var owner = await SignedInAsync("owner@cronus.example");
        using var served = await owner.GetAsync(link.DownloadPath);
        served.StatusCode.Should().Be(HttpStatusCode.OK);
        served.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        served.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        served.Content.Headers.ContentDisposition.FileNameStar.Should().Be("CRONUS.zip");
        served.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await served.Content.ReadAsByteArrayAsync()).Should().Equal(1, 2, 3);

        using var colleague = await SignedInAsync("colleague@cronus.example");
        using var refused = await colleague.GetAsync(link.DownloadPath);
        refused.StatusCode.Should().Be(HttpStatusCode.NotFound, "a colleague in the same organisation gets the answer a wrong token gets");

        using var anonymous = _factory.CreateClient();
        using var signIn = await anonymous.GetAsync(link.DownloadPath);
        signIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signIn.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    private async Task<int> SeedUserAsync(string email)
    {
        await using var seed = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        seed.Users.Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(await loginPage.Content.ReadAsStringAsync())),
        }));
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
