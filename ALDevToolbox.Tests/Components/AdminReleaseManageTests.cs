using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/admin/object-explorer/release/{id}/manage</c>, the danger zone: a live release
/// offers only the reversible soft delete, and the permanent delete appears once it is
/// soft-deleted, behind typing the release's label (#1438).
/// </summary>
/// <remarks>
/// Statically rendered with plain form posts, so it is driven through the real app (as
/// <see cref="SiteAdminTenantBackupsTests"/> is) and the release management service is
/// the app's own, resolved from its container.
/// </remarks>
[Collection(EndpointFactoryCollection.Name)]
public sealed class AdminReleaseManageTests : IDisposable
{
    private const string AdminEmail = "admin@cronus.example";
    private const string AdminPassword = "correct horse battery staple";
    private const string Label = "Business Central 26.0 (DK)";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Permanent_delete_is_offered_only_once_the_release_is_soft_deleted()
    {
        var id = await SeedReleaseAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAdminAsync(factory);
        var manage = $"/admin/object-explorer/release/{id}/manage";

        var live = await GetAsync(client, manage);
        Forms(live).Should().Contain($"/admin/object-explorer/{id}/soft-delete")
            .And.NotContain($"/admin/object-explorer/{id}/hard-delete");

        using var softDeleted = await PostAsync(client, $"/admin/object-explorer/{id}/soft-delete", TokenIn(live), []);
        var after = await GetAsync(client, manage);

        Forms(after).Should().Contain($"/admin/object-explorer/{id}/hard-delete")
            .And.NotContain($"/admin/object-explorer/{id}/soft-delete");
        new HtmlParser().ParseDocument(after)
            .QuerySelector($"form[action='/admin/object-explorer/{id}/hard-delete'] input[name=ConfirmLabel]")!
            .HasAttribute("required").Should().BeTrue();
        (await DeletedAtAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task A_wrong_label_keeps_the_release_and_the_exact_label_deletes_it()
    {
        var id = await SeedReleaseAsync(softDeleted: true);
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInAdminAsync(factory);
        var token = TokenIn(await GetAsync(client, $"/admin/object-explorer/release/{id}/manage"));

        using var wrong = await PostAsync(client, $"/admin/object-explorer/{id}/hard-delete", token,
            [new("ConfirmLabel", "Business Central 26.0")]);

        Uri.UnescapeDataString(wrong.Headers.Location!.OriginalString)
            .Should().Contain("Type the exact Release label to confirm permanent deletion.");
        (await ExistsAsync(id)).Should().BeTrue();

        using var right = await PostAsync(client, $"/admin/object-explorer/{id}/hard-delete", token,
            [new("ConfirmLabel", Label)]);

        right.Headers.Location!.OriginalString.Should().Be($"/admin/object-explorer?ok=hard-deleted&id={id}");
        (await ExistsAsync(id)).Should().BeFalse();
    }

    private async Task<int> SeedReleaseAsync(bool softDeleted = false)
    {
        await using var seed = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = Label, DedupKey = "bc:26.0:dk",
            Kind = "first_party", Status = "ready", DeletedAt = softDeleted ? DateTime.UtcNow : null,
            ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        seed.OeReleases.Add(release);
        await seed.SaveChangesAsync();
        return release.Id;
    }

    private async Task<DateTime?> DeletedAtAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeReleases.AsNoTracking().Where(r => r.Id == id).Select(r => r.DeletedAt)
            .SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<bool> ExistsAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeReleases.AsNoTracking().AnyAsync(r => r.Id == id, TestContext.Current.CancellationToken);
    }

    private static List<string?> Forms(string html) =>
        new HtmlParser().ParseDocument(html).QuerySelectorAll("form").Select(f => f.GetAttribute("action")).ToList();

    private static async Task<string> GetAsync(HttpClient client, string url)
    {
        using var page = await client.GetAsync(url, TestContext.Current.CancellationToken);
        page.StatusCode.Should().Be(HttpStatusCode.OK, url);
        return await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string TokenIn(string html)
    {
        var match = Regex.Match(html, """name="__RequestVerificationToken"[^>]*value="([^"]+)""");
        match.Success.Should().BeTrue("the page must carry an antiforgery token");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string url, string token, KeyValuePair<string, string>[] fields) =>
        await client.PostAsync(url, new FormUrlEncodedContent(
            fields.Append(new KeyValuePair<string, string>("__RequestVerificationToken", token))),
            TestContext.Current.CancellationToken);

    private async Task<HttpClient> SignedInAdminAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = AdminEmail,
                DisplayName = "Admin",
                PasswordHash = new AuthService(seed, NullLogger<AuthService>.Instance, TimeProvider.System)
                    .HashPassword(AdminPassword),
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await seed.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        var token = TokenIn(await GetAsync(client, "/login"));
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
