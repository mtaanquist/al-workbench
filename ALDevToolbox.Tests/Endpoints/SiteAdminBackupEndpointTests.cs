using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The guards on the site admin's backup routes that live in the handlers rather than
/// the services: the whole-database restore's confirmation tick, and the filter that
/// turns the per-tenant backup and storage routes off in single-tenant mode.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class SiteAdminBackupEndpointTests : IDisposable
{
    private const string AdminEmail = "siteadmin@cronus.example";
    private const string AdminPassword = "correct horse battery staple";
    private const string Disabled = "This feature is disabled on this deployment.";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A restore drops and replaces the whole database, so a post without the tick (a
    /// replayed form, a stray click) is turned back before the service is called. The
    /// id is unknown: had the service been called, the answer would be "not found".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("off")]
    public async Task A_restore_without_the_confirmation_tick_is_turned_back(string? confirm)
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, RouteConstants.SiteAdminBackups);

        using var response = await PostAsync(client, "/site-admin/backups/99999/restore", token,
            confirm is null ? [] : [new("Confirm", confirm)]);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri.UnescapeDataString(response.Headers.Location!.OriginalString)
            .Should().Be($"{RouteConstants.SiteAdminBackups}?msg=Tick the confirmation box before restoring.");
        factory.Services.GetRequiredService<MaintenanceModeState>().IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task A_confirmed_restore_of_an_unknown_backup_says_so()
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, RouteConstants.SiteAdminBackups);

        using var response = await PostAsync(client, "/site-admin/backups/99999/restore", token, [new("Confirm", "on")]);

        Uri.UnescapeDataString(response.Headers.Location!.OriginalString)
            .Should().Be($"{RouteConstants.SiteAdminBackups}?msg=Backup not found.");
        factory.Services.GetRequiredService<MaintenanceModeState>().IsActive.Should().BeFalse();
    }

    /// <summary>
    /// Single-tenant mode hides these surfaces in the UI and must disable them too, or the
    /// routes stay live with nothing on screen pointing at them. Each carries the filter
    /// separately, so each is asked.
    /// </summary>
    [Fact]
    public async Task In_single_tenant_mode_every_per_tenant_backup_and_storage_route_is_not_found()
    {
        using var factory = new EndpointFactory(_db, environment: new Dictionary<string, string?>
        {
            ["SINGLE_TENANT_MODE"] = "1",
        });
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/account");

        var routes = new[]
        {
            "/site-admin/settings/quotas/save",
            $"/site-admin/storage/{TestDb.OtherOrgId}/backup",
            $"/site-admin/storage/{TestDb.OtherOrgId}/quota",
            "/site-admin/tenant-backups/1/upload",
            "/site-admin/tenant-backups/offsite/download",
            "/site-admin/tenant-backups/1/pin",
            "/site-admin/tenant-backups/1/unpin",
            "/site-admin/tenant-backups/1/delete",
            "/site-admin/tenant-backups/1/restore",
        };
        foreach (var route in routes)
        {
            using var post = await PostAsync(client, route, token, [new("Confirm", "on")]);
            post.StatusCode.Should().Be(HttpStatusCode.NotFound, route);
            (await post.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(Disabled, route);
        }

        using var download = await client.GetAsync("/site-admin/tenant-backups/1/download", TestContext.Current.CancellationToken);
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await download.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Be(Disabled);
    }

    /// <summary>The other half: without the mode the same routes reach their handlers.</summary>
    [Fact]
    public async Task Outside_single_tenant_mode_the_per_tenant_routes_reach_their_handlers()
    {
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInSiteAdminAsync(factory);
        var token = await TokenFromAsync(client, "/account");

        using var pin = await PostAsync(client, "/site-admin/tenant-backups/1/pin", token, []);

        (await pin.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotBe(Disabled);
        pin.StatusCode.Should().NotBe(HttpStatusCode.NotFound);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string token, KeyValuePair<string, string>[] fields) =>
        await client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))),
            TestContext.Current.CancellationToken);

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url, TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK, url);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private async Task<HttpClient> SignedInSiteAdminAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = AdminEmail,
                DisplayName = "Site Admin",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(AdminPassword),
                Role = UserRole.Admin,
                IsSiteAdmin = true,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", AdminEmail),
            new KeyValuePair<string, string>("Password", AdminPassword),
            new KeyValuePair<string, string>("__RequestVerificationToken", token),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }
}
