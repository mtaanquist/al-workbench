using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Routing;

/// <summary>
/// Pins the legacy route redirects: the redirect-only pages under
/// <c>Components/Pages/Legacy/</c> and the alias endpoints in
/// <c>Endpoints/LegacyRedirectEndpoints.cs</c>. They are unlinked by design, so
/// nothing else in the suite would notice a page being dropped, a route typo, or
/// a rename that quietly re-pointed one at the wrong target — the failure mode is
/// a stale bookmark 404ing months later.
///
/// The table below mirrors <c>Components/Pages/Legacy/README.md</c>; keep the two
/// in step. Every case runs as a signed-in Admin + SiteAdmin so the auth guard on
/// each page (deliberately mirroring the page it forwards to) doesn't swallow the
/// redirect under test. See issue #700 for why these are kept rather than deleted.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class LegacyRedirectTests : IDisposable
{
    private const string AdminEmail = "legacy-admin@cronus.test";
    private const string AdminPassword = "correct-horse-battery-staple";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public LegacyRedirectTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    // Redirect-only pages.
    [InlineData("/compare", "/diff")]
    [InlineData("/artifacts", "/pipelines/builds")]
    [InlineData("/artifacts/7", "/solutions/7")]
    [InlineData("/admin/configuration", "/admin/administration/identity")]
    [InlineData("/admin/configuration/identity", "/admin/administration/identity")]
    [InlineData("/admin/configuration/defaults", "/admin/templates/defaults")]
    [InlineData("/admin/configuration/files", "/admin/templates/files")]
    [InlineData("/admin/configuration/logo", "/admin/templates/defaults")]
    [InlineData("/admin/templates/logo", "/admin/templates/defaults")]
    [InlineData("/admin/configuration/workspace", "/admin/templates/workspace")]
    [InlineData("/admin/configuration/mcp", "/admin/administration/tools")]
    [InlineData("/admin/administration/mcp", "/admin/administration/tools")]
    [InlineData("/admin/export", "/admin/administration/export")]
    [InlineData("/admin/oauth-clients", "/admin/administration/oauth-clients")]
    [InlineData("/admin/users", "/admin/administration/users")]
    [InlineData("/admin/users/new", "/admin/administration/users/new")]
    [InlineData("/site-admin/access-tokens", "/site-admin/connections/access-tokens")]
    [InlineData("/site-admin/oauth-clients", "/site-admin/connections/oauth-clients")]
    [InlineData("/site-admin/backups", "/site-admin/backup-storage/database")]
    [InlineData("/site-admin/storage", "/site-admin/backup-storage/storage")]
    [InlineData("/site-admin/tenant-backups", "/site-admin/backup-storage/snapshots")]
    [InlineData("/site-admin/settings/mcp", "/site-admin/settings/tools")]
    // Alias endpoints (LegacyRedirectEndpoints.cs).
    [InlineData("/projects/extension", "/templates/extension")]
    [InlineData("/projects/extension?template=demo", "/templates/extension?template=demo")]
    // Projects became Solutions. /projects/extension above is the generator alias,
    // which the int constraint on /projects/{id} has to leave alone.
    [InlineData("/projects", "/solutions")]
    [InlineData("/projects/new", "/solutions/new")]
    [InlineData("/projects/12", "/solutions/12")]
    [InlineData("/snippets", "/cookbook")]
    [InlineData("/snippets/suggest", "/cookbook/suggest")]
    [InlineData("/snippets/12", "/cookbook/12")]
    [InlineData("/admin/snippets", "/admin/cookbook")]
    [InlineData("/admin/snippets/new", "/admin/cookbook/new")]
    [InlineData("/admin/snippets/12", "/admin/cookbook/12")]
    [InlineData("/admin/snippets/suggestions", "/admin/cookbook/suggestions")]
    [InlineData("/admin/snippets/suggestions?focus=5", "/admin/cookbook/suggestions?focus=5")]
    [InlineData("/api/snippets/12/download", "/api/cookbook/12/download")]
    [InlineData("/releases", "/pipelines/deployments")]
    [InlineData("/releases/12", "/pipelines/deployments/12")]
    public async Task A_legacy_route_still_redirects_to_its_current_route(string oldRoute, string newRoute)
    {
        await SeedAdminAsync();
        using var client = _factory.CreateClient();
        await SignInAsync(client);

        using var response = await client.GetAsync(oldRoute);

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.Redirect, HttpStatusCode.MovedPermanently, HttpStatusCode.Found);
        var location = response.Headers.Location!.OriginalString;
        // Blazor's server-side NavigateTo produces an absolute Location; the
        // endpoint aliases produce a relative one. Compare on the path+query.
        var actual = location.StartsWith('/')
            ? location
            : new Uri(location).PathAndQuery;
        actual.Should().Be(newRoute,
            "the legacy route {0} exists only to forward old bookmarks to {1}", oldRoute, newRoute);
    }

    private async Task SeedAdminAsync()
    {
        await using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = AdminEmail,
            DisplayName = "Legacy Admin",
            PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                .HashPassword(AdminPassword),
            Role = UserRole.Admin,
            IsSiteAdmin = true,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        await seed.SaveChangesAsync();
    }

    private static async Task SignInAsync(HttpClient client)
    {
        using var form = await client.GetAsync("/login");
        form.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await form.Content.ReadAsStringAsync();
        var token = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        token.Success.Should().BeTrue("the sign-in form must carry an antiforgery token");

        using var response = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", AdminEmail),
            new KeyValuePair<string, string>("Password", AdminPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token.Groups[1].Value),
        }));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().NotContain("err=",
            "the seeded admin must actually sign in, or every case below would only prove the login guard works");
    }
}
