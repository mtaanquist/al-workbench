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
[Collection(EndpointFactoryCollection.Name)]
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
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
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
