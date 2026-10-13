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
/// Approving or rejecting a signup that has already been decided - a double
/// click, or a second admin on a page they loaded earlier - lands back on the
/// users page with the reason, not on the error page (#1260).
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class SignupDecisionEndpointTests : IDisposable
{
    private const string AdminEmail = "admin@cronus.example";
    private const string Password = "correct horse battery staple";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public SignupDecisionEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task A_second_decision_on_the_same_request_redirects_with_the_reason(string action)
    {
        var requestId = await SeedAsync();
        var (client, token) = await SignInAsAdminAsync();

        using var first = await PostAsync(client, $"/admin/users/{requestId}/{action}", token);
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        first.Headers.Location!.OriginalString.Should().NotContain("err=");

        using var second = await PostAsync(client, $"/admin/users/{requestId}/{action}", token);
        second.StatusCode.Should().Be(HttpStatusCode.Redirect, "the request was already decided; that is not a server error");
        second.Headers.Location!.OriginalString.Should().Contain("err=")
            .And.Contain(Uri.EscapeDataString("This request has already been decided."));
    }

    private async Task<int> SeedAsync()
    {
        await using var seed = _db.NewContext();
        var hash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password);
        seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AdminEmail,
            DisplayName = "Ada Admin",
            PasswordHash = hash,
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        var pending = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = "newcomer@cronus.example",
            DisplayName = "Nina Newcomer",
            PasswordHash = hash,
            Role = UserRole.User,
            Status = UserStatus.Pending,
            CreatedAt = DateTime.UtcNow,
        };
        seed.Users.Add(pending);
        await seed.SaveChangesAsync();
        var request = new SignupRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            UserId = pending.Id,
            Email = pending.Email,
            RequestedAt = DateTime.UtcNow,
            Decision = SignupDecision.Pending,
        };
        seed.SignupRequests.Add(request);
        await seed.SaveChangesAsync();
        return request.Id;
    }

    private async Task<(HttpClient Client, string Token)> SignInAsAdminAsync()
    {
        var client = _factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await PostAsync(client, "/auth/login",
            AntiforgeryToken(await loginPage.Content.ReadAsStringAsync()),
            ("Email", AdminEmail), ("Password", Password));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");

        using var page = await client.GetAsync("/account/security");
        return (client, AntiforgeryToken(await page.Content.ReadAsStringAsync()));
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(path, new FormUrlEncodedContent(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))));

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
