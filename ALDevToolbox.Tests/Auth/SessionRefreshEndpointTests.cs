using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Operations;
using Microsoft.Extensions.DependencyInjection;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Auth;

/// <summary>
/// <c>/auth/session/refresh</c> is where a stale interactive page is sent
/// (#1534). It must apply the row now, not after the five-minute cookie
/// throttle, or the reloaded page would connect with the old claims again.
///
/// <para>The cases share one host (#1536, #1582), each signing in a user of its
/// own. The one that puts the host into maintenance mode boots a host of its own.</para>
/// </summary>
public sealed class SessionRefreshEndpointTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _email = SharedEndpointHost.UniqueEmail("sam.berg");

    public SessionRefreshEndpointTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
    }

    [Fact]
    public async Task A_demoted_admin_is_refreshed_out_of_the_admin_pages()
    {
        var (client, userId) = await SignedInAdminAsync();
        // The first request after sign-in runs the cookie re-check and starts
        // its throttle, as an admin's earlier page loads would have.
        using (var first = await client.GetAsync(RouteConstants.AdminUsers))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        await SetAsync(userId, u => u.Role = UserRole.User);

        using (var stale = await client.GetAsync(RouteConstants.AdminUsers))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie re-check is throttled, which is why the refresh exists");
        }

        using var refresh = await client.GetAsync($"{CookieSessionRevalidation.RefreshPath}?return=%2Fadmin%2Fadministration%2Fusers");
        refresh.StatusCode.Should().Be(HttpStatusCode.Redirect);
        refresh.Headers.Location!.OriginalString.Should().Be(RouteConstants.AdminUsers);

        using var after = await client.GetAsync(RouteConstants.AdminUsers);
        after.StatusCode.Should().NotBe(HttpStatusCode.OK, "the re-issued cookie says User now");
    }

    [Fact]
    public async Task A_current_admin_comes_straight_back()
    {
        var (client, _) = await SignedInAdminAsync();

        using var refresh = await client.GetAsync($"{CookieSessionRevalidation.RefreshPath}?return=%2Fadmin%2Fadministration%2Fusers");
        refresh.Headers.Location!.OriginalString.Should().Be(RouteConstants.AdminUsers);
        using var after = await client.GetAsync(RouteConstants.AdminUsers);
        after.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_disabled_user_is_signed_out()
    {
        var (client, userId) = await SignedInAdminAsync();
        await SetAsync(userId, u => u.Status = UserStatus.Disabled);

        using var refresh = await client.GetAsync($"{CookieSessionRevalidation.RefreshPath}?return=%2F");
        refresh.StatusCode.Should().Be(HttpStatusCode.Redirect);
        refresh.Headers.Location!.OriginalString.Should().Contain("/login");

        using var after = await client.GetAsync(RouteConstants.AdminUsers);
        after.StatusCode.Should().Be(HttpStatusCode.Redirect);
        after.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task During_maintenance_a_site_admin_is_sent_back_untouched()
    {
        // A SiteAdmin is the one user the maintenance gate lets through, and a
        // restore empties the users table; checking then would sign them out
        // in the middle of it (#1269).
        using var factory = new EndpointFactory(_db);
        var (client, userId) = await SignedInAdminAsync(factory, siteAdmin: true);
        using (var first = await client.GetAsync(RouteConstants.AdminUsers)) { }
        var maintenance = factory.Services.GetRequiredService<MaintenanceModeState>();
        maintenance.Enter("Restoring a backup");
        try
        {
            await using (var ctx = _db.NewContext())
            {
                (await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId)).Status = UserStatus.Disabled;
                await ctx.SaveChangesAsync();
            }

            using var refresh = await client.GetAsync($"{CookieSessionRevalidation.RefreshPath}?return=%2Fsite-admin");
            refresh.StatusCode.Should().Be(HttpStatusCode.Redirect);
            refresh.Headers.Location!.OriginalString.Should().Be("/site-admin");
            refresh.Headers.Contains("Set-Cookie").Should().BeFalse("the session is left as it is");
        }
        finally
        {
            maintenance.Exit();
        }
    }

    [Fact]
    public async Task The_return_address_stays_on_this_site()
    {
        var (client, _) = await SignedInAdminAsync();

        using var refresh = await client.GetAsync($"{CookieSessionRevalidation.RefreshPath}?return=%2F%2Fevil.example%2F");
        refresh.Headers.Location!.OriginalString.Should().Be("/");
    }

    private async Task SetAsync(int userId, Action<User> change)
    {
        await using var ctx = _db.NewContext();
        change(await ctx.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId));
        await ctx.SaveChangesAsync();
    }

    private Task<(HttpClient Client, int UserId)> SignedInAdminAsync() => SignedInAdminAsync(_factory);

    private async Task<(HttpClient Client, int UserId)> SignedInAdminAsync(EndpointFactory factory, bool siteAdmin = false)
    {
        int userId;
        await using (var seed = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = _email,
                DisplayName = "Sam Berg",
                PasswordHash = _host.HashPassword(Password),
                Role = UserRole.Admin,
                IsSiteAdmin = siteAdmin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        var client = factory.CreateClient();
        using var loginPage = await client.GetAsync("/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Email", _email),
            new KeyValuePair<string, string>("Password", Password),
            new KeyValuePair<string, string>("__RequestVerificationToken", AntiforgeryToken(await loginPage.Content.ReadAsStringAsync())),
        }));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return (client, userId);
    }

    private static string AntiforgeryToken(string html) =>
        Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""").Groups[1].Value;
}
