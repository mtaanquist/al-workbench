using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Components.Pages.Admin;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// OEI2-5: the bulk "Backfill system references" post, and the notice it lands
/// on. The service is covered elsewhere; these pin the endpoint itself - it
/// answers at once with more releases than the import queue holds, rather than
/// waiting for room (#1542), and its redirect carries how many were queued and
/// how many skipped - and the three wordings of the notice.
/// </summary>
public sealed class BulkBackfillEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string EditorEmail = "erik@cronus.example";
    // More than the sixteen jobs the import queue holds.
    private const int Releases = 20;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_bulk_backfill_answers_at_once_and_reports_what_it_queued_and_skipped()
    {
        await SeedUserAsync();
        var ids = await SeedReadyReleasesAsync(Releases);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);
        // One release already has a backfill waiting from an earlier click.
        // Written once the app has started, so startup does not pick it up and
        // run it before the post.
        await SeedQueuedBackfillAsync(ids[0]);
        var token = await TokenFromAsync(client, "/notifications");

        var clock = Stopwatch.StartNew();
        using var response = await client.PostAsync("/admin/object-explorer/backfill-system-references-all",
            Form(("__RequestVerificationToken", token)));
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should()
            .Be($"/admin/object-explorer?ok=backfill-all-queued&id={Releases - 1}&skipped=1");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30),
            "the jobs are fed to the queue in the background, never awaited by the request");
    }

    [Theory]
    [InlineData("0", "3", "Nothing new to queue. 3 release(s) already had one waiting and were left alone.")]
    [InlineData("5", "2", "Queued a system-reference backfill for 5 ready release(s). 2 release(s) already had one waiting and were left alone. They run in the background")]
    [InlineData("5", "0", "Queued a system-reference backfill for 5 ready release(s). They run in the background")]
    public void The_notice_says_what_was_queued_and_what_was_left_alone(string queued, string skipped, string expected)
    {
        AdminObjectExplorerIndex.FormatBackfillQueued(queued, skipped).Should().StartWith(expected);
    }

    // ---- helpers -----------------------------------------------------------

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private async Task<List<int>> SeedReadyReleasesAsync(int count)
    {
        await using var ctx = _db.NewContext();
        var releases = Enumerable.Range(1, count).Select(i => new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = $"BC 26.{i} CRONUS",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        }).ToList();
        ctx.OeReleases.AddRange(releases);
        await ctx.SaveChangesAsync();
        return releases.Select(r => r.Id).ToList();
    }

    private async Task SeedQueuedBackfillAsync(int releaseId)
    {
        await using var ctx = _db.NewContext();
        ctx.OeImportJobs.Add(new OeImportJob
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = releaseId,
            Kind = "backfill_system_references",
            Status = "queued",
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedUserAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = EditorEmail,
            DisplayName = EditorEmail,
            PasswordHash = new AuthService(ctx, NullLogger<AuthService>.Instance, TimeProvider.System).HashPassword(Password),
            Role = UserRole.Editor,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        await ctx.SaveChangesAsync();
    }

    private static async Task<HttpClient> SignInAsync(EndpointFactory factory)
    {
        var client = factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", Form(
            ("Email", EditorEmail), ("Password", Password), ("__RequestVerificationToken", token)));
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
