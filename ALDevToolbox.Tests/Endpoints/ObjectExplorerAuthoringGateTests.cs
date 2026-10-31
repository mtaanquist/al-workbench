using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Every POST under <c>/admin/object-explorer</c> changes releases, so it is
/// gated to Admin and Editor (<c>RequireObjectExplorerAuthoring</c>).
/// <c>AuthenticationRequiredTests</c> only shows an anonymous POST is refused,
/// and it accepts the antiforgery 400, so it would stay green with the role
/// requirement gone. These pin the role gate itself (#1273).
/// </summary>
public sealed class ObjectExplorerAuthoringGateTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string UserEmail = "mel@cronus.example";
    private const string EditorEmail = "erik@cronus.example";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Every_object_explorer_admin_post_requires_admin_or_editor()
    {
        using var factory = new EndpointFactory(_db);
        using var _ = factory.CreateClient();

        var posts = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/admin/object-explorer", StringComparison.OrdinalIgnoreCase) == true)
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                .Contains("POST", StringComparer.OrdinalIgnoreCase) == true)
            .ToList();

        posts.Should().NotBeEmpty(because: "the walk must actually find the endpoints it guards");
        foreach (var endpoint in posts)
        {
            var roles = AllowedRoles(endpoint);
            roles.Should().NotBeEmpty(
                because: $"{endpoint.RoutePattern.RawText} must be limited to roles, not just signed-in users");
            roles.Should().BeSubsetOf(
                new[] { HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole },
                because: $"{endpoint.RoutePattern.RawText} changes releases, which a User may not do");
        }
    }

    [Fact]
    public async Task A_signed_in_user_with_a_valid_token_cannot_soft_delete_a_release()
    {
        await SeedUserAsync(UserEmail, UserRole.User);
        var releaseId = await SeedReleaseAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, UserEmail);
        var token = await TokenFromAsync(client, "/notifications");

        using var response = await client.PostAsync($"/admin/object-explorer/{releaseId}/soft-delete",
            Form(("__RequestVerificationToken", token)));

        // The cookie handler answers a role refusal by sending the browser to
        // its access-denied page (/login) rather than a bare 403.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            response.Headers.Location!.AbsolutePath.Should().Be("/login");
        }
        (await DeletedAtAsync(releaseId)).Should().BeNull(because: "a User must not be able to delete a release");
    }

    [Fact]
    public async Task An_editor_with_a_valid_token_can_soft_delete_a_release()
    {
        // The control for the test above: the same request from an Editor
        // goes through, so the refusal there is the role, not the token.
        await SeedUserAsync(EditorEmail, UserRole.Editor);
        var releaseId = await SeedReleaseAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory, EditorEmail);
        var token = await TokenFromAsync(client, "/notifications");

        using var response = await client.PostAsync($"/admin/object-explorer/{releaseId}/soft-delete",
            Form(("__RequestVerificationToken", token)));

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("ok=soft-deleted");
        (await DeletedAtAsync(releaseId)).Should().NotBeNull();
    }

    // ---- helpers -----------------------------------------------------------

    private static HashSet<string> AllowedRoles(RouteEndpoint endpoint)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
        {
            foreach (var requirement in policy.Requirements.OfType<RolesAuthorizationRequirement>())
            {
                roles.UnionWith(requirement.AllowedRoles);
            }
        }
        foreach (var data in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
        {
            if (string.IsNullOrWhiteSpace(data.Roles)) continue;
            roles.UnionWith(data.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return roles;
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<DateTime?> DeletedAtAsync(int releaseId)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.OeReleases.AsNoTracking().SingleAsync(r => r.Id == releaseId)).DeletedAt;
    }

    private async Task<int> SeedReleaseAsync()
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "BC 26.0 CRONUS",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private async Task SeedUserAsync(string email, UserRole role)
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory, string email)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", Form(
            ("Email", email), ("Password", Password), ("__RequestVerificationToken", token)));
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
