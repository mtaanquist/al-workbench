using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Diff;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The Object Explorer routes #1338 and #1339 changed, through the real host:
/// the system-reference backfill refuses releases it cannot work on, and the
/// file compare page refuses to diff files that are too long and says so
/// when every diff slot is taken.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class ObjectExplorerLowsEndpointTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private const string EditorEmail = "erik@cronus.example";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    // ── Backfill system references ──────────────────────────────────────

    [Fact]
    public async Task Backfill_of_a_missing_release_is_not_found()
    {
        await SeedEditorAsync();
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await PostBackfillAsync(client, 987_654);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JobCountAsync(987_654)).Should().Be(0);
    }

    [Fact]
    public async Task Backfill_of_another_organisations_release_is_not_found_and_writes_nothing()
    {
        await SeedEditorAsync();
        var foreignId = await SeedReleaseAsync(TestDb.OtherOrgId, "ready");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await PostBackfillAsync(client, foreignId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await JobCountAsync(foreignId)).Should().Be(0, because: "it used to write a job row into the caller's org");
    }

    [Theory]
    [InlineData("failed", false)]
    [InlineData("ready", true)]
    public async Task Backfill_of_a_failed_or_deleted_release_redirects_with_an_error_and_queues_nothing(
        string status, bool deleted)
    {
        await SeedEditorAsync();
        var releaseId = await SeedReleaseAsync(TestDb.DefaultOrgId, status, deleted);
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await PostBackfillAsync(client, releaseId);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location!.OriginalString;
        location.Should().StartWith($"/admin/object-explorer/release/{releaseId}/manage?err=Backfill");
        location.Should().NotContain("ok=");
        (await JobCountAsync(releaseId)).Should().Be(0,
            because: "a refused job row would hide the release's real source from Retry");
    }

    [Fact]
    public async Task Backfill_of_a_ready_release_is_queued()
    {
        await SeedEditorAsync();
        var releaseId = await SeedReleaseAsync(TestDb.DefaultOrgId, "ready");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        using var response = await PostBackfillAsync(client, releaseId);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("ok=backfill-queued");
        (await JobCountAsync(releaseId)).Should().Be(1);
    }

    // ── File compare page ───────────────────────────────────────────────

    [Fact]
    public async Task Compare_page_does_not_diff_files_over_the_line_cap()
    {
        await SeedEditorAsync();
        var longText = string.Join('\n', Enumerable.Range(0, 15_001).Select(i => $"// line {i}"));
        var (left, right) = await SeedFilePairAsync(longText, "codeunit 50100 \"CRONUS Short\" { }\n");
        using var factory = new EndpointFactory(_db);
        using var client = await SignInAsync(factory);

        var html = await client.GetStringAsync($"/object-explorer/compare/file?left={left}&right={right}");

        html.Should().Contain("These files are too long to compare here");
        html.Should().Contain($"/object-explorer/file/{left}").And.Contain($"/object-explorer/file/{right}");
        html.Should().NotContain("data-diff=", because: "no diff was run");
        html.Should().NotContain("Ignore whitespace", because: "there is nothing to re-diff");
    }

    [Fact]
    public async Task Compare_page_says_the_server_is_busy_when_no_diff_slot_frees_up()
    {
        await SeedEditorAsync();
        var (left, right) = await SeedFilePairAsync(
            "codeunit 50100 \"CRONUS Old\" { }\n", "codeunit 50100 \"CRONUS New\" { }\n");
        // One slot, already taken, and a short wait so the test does not sit
        // through the real one.
        var gate = new CompareDiffGate(1, TimeSpan.FromMilliseconds(50));
        await gate.Semaphore.WaitAsync();
        using var factory = new EndpointFactory(_db, services =>
            services.AddKeyedSingleton(CompareDiffGate.FileCompareKey, gate));
        using var client = await SignInAsync(factory);

        var html = await client.GetStringAsync($"/object-explorer/compare/file?left={left}&right={right}");

        html.Should().Contain("The server is busy comparing other files");
        html.Should().NotContain("data-diff=");

        // And the same pair diffs once a slot is free.
        gate.Semaphore.Release();
        var diffed = await client.GetStringAsync($"/object-explorer/compare/file?left={left}&right={right}");
        diffed.Should().Contain("data-diff=").And.NotContain("The server is busy");
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<HttpResponseMessage> PostBackfillAsync(HttpClient client, int releaseId)
    {
        var token = await TokenFromAsync(client, "/notifications");
        return await client.PostAsync($"/admin/object-explorer/{releaseId}/backfill-system-references",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
    }

    private async Task<int> JobCountAsync(int releaseId)
    {
        await using var ctx = _db.NewContext();
        // Counted across every org on purpose: the foreign-id case must not
        // have written a row anywhere. Test-only read, not request code.
        return await ctx.OeImportJobs.IgnoreQueryFilters().AsNoTracking().CountAsync(j => j.ReleaseId == releaseId);
    }

    private async Task<int> SeedReleaseAsync(int organizationId, string status, bool deleted = false)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = organizationId,
            Label = "BC 26.0 CRONUS",
            Kind = "first_party",
            Status = status,
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    /// <summary>One release, one app, and two files in it with the given text.</summary>
    private async Task<(long Left, long Right)> SeedFilePairAsync(string leftText, string rightText)
    {
        var releaseId = await SeedReleaseAsync(TestDb.DefaultOrgId, "ready");
        await using var ctx = _db.NewContext();
        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = releaseId,
            AppId = Guid.NewGuid(),
            Name = "CRONUS Core",
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();

        var left = AddFile(ctx, module.Id, "src/Left.Codeunit.al", leftText);
        var right = AddFile(ctx, module.Id, "src/Right.Codeunit.al", rightText);
        await ctx.SaveChangesAsync();
        return (left.Id, right.Id);
    }

    private static OeModuleFile AddFile(Data.AppDbContext ctx, long moduleId, string path, string content)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant() + Guid.NewGuid().ToString("N");
        var lines = content.Count(c => c == '\n') + 1;
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = content,
            ContentLength = content.Length,
            LineCount = lines,
        });
        var file = new OeModuleFile
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = moduleId,
            Path = path,
            ContentHash = hash,
            LineCount = lines,
        };
        ctx.OeModuleFiles.Add(file);
        return file;
    }

    private async Task SeedEditorAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = EditorEmail,
            DisplayName = "Erik Hansen",
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
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", EditorEmail), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
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
