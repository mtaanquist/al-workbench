using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The build download routes through the real host: a signed-in colleague gets a
/// build's apps, one at a time or zipped, and someone with no grant on a Private
/// solution gets the same 404 as for a build that does not exist (#1274). The
/// "Download all" zip is streamed to the response (#1263), and the server refuses
/// synchronous writes to it, so these unzip what actually came back.
///
/// <para>The cases share one host (#1536, #1582). Each seeds its own colleague,
/// owner and builds, and asks only for those.</para>
/// </summary>
public sealed class ArtifactEndpointTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _alexEmail = SharedEndpointHost.UniqueEmail("alex");
    private int _ownerUserId;

    public ArtifactEndpointTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
    }

    private sealed record SeededBuild(int BuildId, int[] ArtifactIds);

    [Fact]
    public async Task Download_all_returns_a_zip_holding_every_app_of_the_build()
    {
        await SeedUsersAsync();
        var build = await SeedBuildAsync(ProjectVisibility.Public,
            ("CRONUS_Core_1.0.0.0.app", new byte[] { 1, 2, 3 }),
            ("CRONUS_Sales_1.0.0.0.app", Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray()));
        using var client = await SignInAsync();

        using var response = await client.GetAsync($"/artifacts/build/{build.BuildId}/all");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain($"build-{build.BuildId}-apps.zip");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var entries = archive.Entries.ToDictionary(e => e.FullName, ReadEntry);
        entries.Keys.Should().BeEquivalentTo("CRONUS_Core_1.0.0.0.app", "CRONUS_Sales_1.0.0.0.app");
        entries["CRONUS_Core_1.0.0.0.app"].Should().Equal(1, 2, 3);
        entries["CRONUS_Sales_1.0.0.0.app"].Should().HaveCount(200_000);
        entries["CRONUS_Sales_1.0.0.0.app"][250].Should().Be(250);
    }

    [Fact]
    public async Task A_build_with_no_apps_has_nothing_to_download()
    {
        await SeedUsersAsync();
        var build = await SeedBuildAsync(ProjectVisibility.Public);
        using var client = await SignInAsync();

        using var response = await client.GetAsync($"/artifacts/build/{build.BuildId}/all");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_colleague_with_no_grant_on_a_private_solution_gets_not_found_for_its_downloads()
    {
        await SeedUsersAsync();
        var hidden = await SeedBuildAsync(ProjectVisibility.Private, ("CRONUS_Core_1.0.0.0.app", new byte[] { 1, 2, 3 }));
        var open = await SeedBuildAsync(ProjectVisibility.Public, ("CRONUS_Sales_1.0.0.0.app", new byte[] { 4, 5 }));
        using var client = await SignInAsync();

        foreach (var path in new[]
                 {
                     $"/artifacts/build/{hidden.BuildId}/app/{hidden.ArtifactIds[0]}",
                     $"/artifacts/build/{hidden.BuildId}/all",
                     $"/artifacts/build/{hidden.BuildId}/log",
                     // A build they can see, with the hidden build's app: the pin to the build refuses it.
                     $"/artifacts/build/{open.BuildId}/app/{hidden.ArtifactIds[0]}",
                 })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }

        using var visible = await client.GetAsync($"/artifacts/build/{open.BuildId}/app/{open.ArtifactIds[0]}");
        visible.StatusCode.Should().Be(HttpStatusCode.OK);
        (await visible.Content.ReadAsByteArrayAsync()).Should().Equal(4, 5);
    }

    // ---- helpers -----------------------------------------------------------

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var es = entry.Open()) es.CopyTo(buffer);
        return buffer.ToArray();
    }

    private async Task SeedUsersAsync()
    {
        await using var ctx = _db.NewContext();
        var hash = _host.HashPassword(Password);
        var owner = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = SharedEndpointHost.UniqueEmail("olive"),
            DisplayName = "Olive Owner", PasswordHash = "x", Role = UserRole.Editor, Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.AddRange(
            new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = _alexEmail, DisplayName = "Alex Hansen",
                PasswordHash = hash, Role = UserRole.User, Status = UserStatus.Active,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            },
            owner);
        await ctx.SaveChangesAsync();
        _ownerUserId = owner.Id;
    }

    private async Task<SeededBuild> SeedBuildAsync(ProjectVisibility visibility, params (string FileName, byte[] Content)[] apps)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = $"CRONUS {visibility} {Guid.NewGuid().ToString("N")[..8]}", CreatedByUserId = _ownerUserId,
            Visibility = visibility, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App", CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, PipelineId = pipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        ctx.OeProjectBuildLogs.Add(new OeProjectBuildLog
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
            Section = "Build", Content = "alc ok", Ordering = 0, CreatedAt = now,
        });
        var rows = apps.Select(a => new OeProjectBuildArtifact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = a.FileName,
            AppName = a.FileName, AppVersion = "1.0.0.0", SizeBytes = a.Content.Length, Content = a.Content, CreatedAt = now,
        }).ToList();
        ctx.OeProjectBuildArtifacts.AddRange(rows);
        await ctx.SaveChangesAsync();
        return new SeededBuild(build.Id, rows.Select(r => r.Id).ToArray());
    }

    private async Task<HttpClient> SignInAsync()
    {
        var client = _factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", _alexEmail), new("Password", Password), new("__RequestVerificationToken", token),
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
