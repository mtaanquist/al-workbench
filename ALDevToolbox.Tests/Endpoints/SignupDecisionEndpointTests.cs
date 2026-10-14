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

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task A_decision_on_another_organisations_request_is_refused_and_changes_nothing(string action)
    {
        // The endpoint reads the request inside the tenant filter (#1337), so
        // another org's id finds nothing and the service's refusal is what
        // the admin sees.
        var requestId = await SeedAsync(requestOrgId: TestDb.OtherOrgId);
        var (client, token) = await SignInAsAdminAsync();

        using var response = await PostAsync(client, $"/admin/users/{requestId}/{action}", token);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("err=")
            .And.Contain(Uri.EscapeDataString("Request not found in this organisation."));
        await using var read = _db.NewContext();
        (await read.SignupRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId)).Decision
            .Should().Be(SignupDecision.Pending);
        (await read.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "newcomer@cronus.example"))
            .Should().BeTrue("a refused rejection must not delete the other org's pending user");
    }

    private async Task<int> SeedAsync(int requestOrgId = TestDb.DefaultOrgId)
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
            OrganizationId = requestOrgId,
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
            OrganizationId = requestOrgId,
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
