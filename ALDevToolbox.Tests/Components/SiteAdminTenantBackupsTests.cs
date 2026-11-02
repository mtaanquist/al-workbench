using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// <c>/site-admin/backup-storage/snapshots</c>: Restore replaces an organisation's data
/// and Delete cannot be undone, so both forms ask the browser to confirm before they
/// post, and a pinned snapshot offers no Delete at all (#1438).
/// </summary>
/// <remarks>
/// The page is statically rendered and its actions are plain form posts, so it is
/// driven through the real app rather than bUnit: the page is fetched as a SiteAdmin
/// sees it, and its forms are posted exactly as the browser would once the person says
/// yes. Saying no to a browser <c>confirm()</c> sends nothing, so "dismiss leaves the
/// data" is the form's <c>onsubmit</c> returning what <c>confirm()</c> answered.
/// </remarks>
public sealed class SiteAdminTenantBackupsTests : IDisposable
{
    private const string AdminEmail = "siteadmin@cronus.example";
    private const string AdminPassword = "correct horse battery staple";
    private const string Page = "/site-admin/backup-storage/snapshots";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_pinned_snapshot_offers_restore_but_no_delete_and_both_forms_ask_first()
    {
        var (pinned, unpinned) = await SeedSnapshotsAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInSiteAdminAsync(factory);

        var html = await GetAsync(client, Page);

        var pinnedRow = RowFor(html, "pinned.dump");
        var unpinnedRow = RowFor(html, "unpinned.dump");
        Form(pinnedRow, $"/site-admin/tenant-backups/{pinned}/delete").Should().BeNull(
            "retention keeps a pinned snapshot, and deleting it would undo that silently");
        AsksFirst(Form(pinnedRow, $"/site-admin/tenant-backups/{pinned}/restore"));
        AsksFirst(Form(unpinnedRow, $"/site-admin/tenant-backups/{unpinned}/restore"));
        AsksFirst(Form(unpinnedRow, $"/site-admin/tenant-backups/{unpinned}/delete"));
    }

    [Fact]
    public async Task Confirming_delete_removes_the_snapshot_and_a_pinned_one_is_refused_even_when_posted()
    {
        var (pinned, unpinned) = await SeedSnapshotsAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignedInSiteAdminAsync(factory);
        var html = await GetAsync(client, Page);
        var token = TokenIn(html);

        using var deleted = await PostAsync(client, Form(RowFor(html, "unpinned.dump"),
            $"/site-admin/tenant-backups/{unpinned}/delete")!.GetAttribute("action")!, token);
        using var refused = await PostAsync(client, $"/site-admin/tenant-backups/{pinned}/delete", token);

        deleted.Headers.Location!.OriginalString.Should().Be($"{Page}?ok=deleted");
        Uri.UnescapeDataString(refused.Headers.Location!.OriginalString).Should().Contain("Unpin the snapshot before deleting it.");
        await using var ctx = _db.NewContext();
        (await ctx.PerTenantBackups.AsNoTracking().Select(b => b.Id).ToListAsync(TestContext.Current.CancellationToken))
            .Should().Equal(pinned);
    }

    private async Task<(int Pinned, int Unpinned)> SeedSnapshotsAsync()
    {
        await using var seed = _db.NewContext();
        PerTenantBackup Snapshot(string file, bool isPinned) => new()
        {
            OrganizationId = TestDb.OtherOrgId, FileName = file, FileSizeBytes = 2048,
            CreatedAt = DateTime.UtcNow, Kind = BackupKind.Scheduled,
            SchemaVersion = PerTenantBackupService.CurrentSchemaVersion, IsPinned = isPinned,
        };
        var pinned = Snapshot("pinned.dump", isPinned: true);
        var unpinned = Snapshot("unpinned.dump", isPinned: false);
        seed.PerTenantBackups.AddRange(pinned, unpinned);
        await seed.SaveChangesAsync();
        return (pinned.Id, unpinned.Id);
    }

    private static IElement RowFor(string html, string fileName) =>
        new HtmlParser().ParseDocument(html).QuerySelectorAll("tr")
            .Single(r => r.QuerySelector("code")?.TextContent == fileName);

    private static IElement? Form(IElement row, string action) =>
        row.QuerySelectorAll("form").SingleOrDefault(f => f.GetAttribute("action") == action);

    private static void AsksFirst(IElement? form)
    {
        form.Should().NotBeNull();
        form!.GetAttribute("onsubmit").Should().StartWith("return confirm(",
            "the post only goes when the person answers yes");
    }

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

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token) =>
        await client.PostAsync(url, new FormUrlEncodedContent(
            [new KeyValuePair<string, string>("__RequestVerificationToken", token)]),
            TestContext.Current.CancellationToken);

    private async Task<HttpClient> SignedInSiteAdminAsync(EndpointFactory factory)
    {
        await using (var seed = _db.NewContext())
        {
            seed.Users.Add(new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = AdminEmail,
                DisplayName = "Site Admin",
                PasswordHash = TestPasswords.Hash(AdminPassword),
                Role = UserRole.Admin,
                IsSiteAdmin = true,
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
