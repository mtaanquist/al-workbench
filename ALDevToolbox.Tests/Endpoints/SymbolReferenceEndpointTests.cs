using System.Net;
using System.Text.RegularExpressions;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The <c>SymbolReference.json</c> download through the real host. The route
/// reads the module table directly rather than through the source viewer, so it
/// carries its own Private-solution check and its own release pin; these tests
/// hold both (#1347). It also writes a file name built from the release label
/// and the app name, which may hold letters such as the Danish ae (#1339).
///
/// <para>The cases share one host (#1536, #1582). Each seeds its own three users,
/// solution and releases, and asks only for those.</para>
/// </summary>
public sealed class SymbolReferenceEndpointTests : IClassFixture<SharedEndpointHost>
{
    private const string Password = "correct horse battery staple";
    private const string SymbolJson = "{\"RuntimeVersion\":\"14.0\"}";

    private readonly SharedEndpointHost _host;
    private readonly TestDb _db;
    private readonly EndpointFactory _factory;
    private readonly string _ownerEmail = SharedEndpointHost.UniqueEmail("owner");
    private readonly string _memberEmail = SharedEndpointHost.UniqueEmail("mel");
    private readonly string _outsiderEmail = SharedEndpointHost.UniqueEmail("nils");

    public SymbolReferenceEndpointTests(SharedEndpointHost host)
    {
        _host = host;
        _db = host.Db;
        _factory = host.Factory;
    }

    [Fact]
    public async Task A_member_of_a_private_solution_downloads_the_symbols()
    {
        var seed = await SeedAsync(ProjectVisibility.Private);
        using var client = await SignInAsync(_memberEmail);

        using var response = await client.GetAsync(Url(seed.ReleaseId, seed.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(SymbolJson);
    }

    [Fact]
    public async Task An_outsider_gets_not_found_for_a_private_solutions_symbols()
    {
        var seed = await SeedAsync(ProjectVisibility.Private);
        using var client = await SignInAsync(_outsiderEmail);

        using var response = await client.GetAsync(Url(seed.ReleaseId, seed.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a release under a Private solution the caller has no grant on reads as missing");
    }

    [Fact]
    public async Task A_visible_release_paired_with_another_releases_module_is_not_found()
    {
        // The Private release's module, asked for under an unlinked release the
        // outsider can see: the visibility check passes on the release id, so
        // only the module's own release pin keeps it out.
        var seed = await SeedAsync(ProjectVisibility.Private);
        var open = await SeedReleaseAsync("Open release", "Open app");
        using var client = await SignInAsync(_outsiderEmail);

        using var response = await client.GetAsync(Url(open.ReleaseId, seed.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_name_with_a_non_ascii_letter_downloads_with_that_name()
    {
        var seed = await SeedReleaseAsync("BC 25 Lærling", "Løn og ferie");
        using var client = await SignInAsync(_outsiderEmail);

        using var response = await client.GetAsync(Url(seed.ReleaseId, seed.ModuleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "a non-ASCII header value used to be a 500");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition!.FileNameStar
            .Should().Be("BC-25-Lærling-Løn-og-ferie.SymbolReference.json");
    }

    // ---- helpers -----------------------------------------------------------

    private static string Url(int releaseId, long moduleId) =>
        $"/api/object-explorer/release/{releaseId}/modules/{moduleId}/symbol-reference";

    private sealed record Seeded(int ReleaseId, long ModuleId);

    /// <summary>Three users, a solution at <paramref name="visibility"/> with the member's team on it, and a release linked to it.</summary>
    private async Task<Seeded> SeedAsync(ProjectVisibility visibility)
    {
        int ownerId, memberId;
        await using (var ctx = _db.NewContext())
        {
            var owner = NewUser(ctx, _ownerEmail, UserRole.Editor);
            var member = NewUser(ctx, _memberEmail, UserRole.User);
            NewUser(ctx, _outsiderEmail, UserRole.User);
            await ctx.SaveChangesAsync();
            ownerId = owner.Id;
            memberId = member.Id;
        }

        var seeded = await SeedReleaseAsync("CRONUS build 1", "CRONUS Core");

        await using (var ctx = _db.NewContext())
        {
            var now = DateTime.UtcNow;
            // Solution and team names are unique in an organisation, and the class shares one.
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var team = new Team { OrganizationId = TestDb.DefaultOrgId, Name = $"Denmark engagement {suffix}", CreatedAt = now, UpdatedAt = now };
            ctx.Teams.Add(team);
            var project = new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = $"CRONUS Denmark {suffix}",
                CreatedByUserId = ownerId,
                Visibility = visibility,
                CreatedAt = now,
                UpdatedAt = now,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();

            ctx.TeamMembers.Add(new TeamMember { OrganizationId = TestDb.DefaultOrgId, TeamId = team.Id, UserId = memberId, CreatedAt = now });
            ctx.Set<OeProjectTeam>().Add(new OeProjectTeam { OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, TeamId = team.Id, CreatedAt = now });
            ctx.OeImportJobs.Add(new OeImportJob
            {
                OrganizationId = TestDb.DefaultOrgId,
                ReleaseId = seeded.ReleaseId,
                ProjectId = project.Id,
                Kind = "project_build",
                Status = "done",
                CreatedAt = now,
            });
            await ctx.SaveChangesAsync();
        }
        return seeded;
    }

    /// <summary>A ready release with one module carrying a stored symbol file.</summary>
    private async Task<Seeded> SeedReleaseAsync(string label, string appName)
    {
        await using var ctx = _db.NewContext();
        if (!ctx.Users.Any(u => u.Email == _outsiderEmail))
        {
            NewUser(ctx, _outsiderEmail, UserRole.User);
        }
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "project",
            Status = "ready",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);

        var hash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        ctx.OeFileContents.Add(new OeFileContent
        {
            ContentHash = hash,
            Content = SymbolJson,
            ContentLength = SymbolJson.Length,
            LineCount = 1,
        });
        await ctx.SaveChangesAsync();

        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = appName,
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = now,
            SymbolReferenceContentHash = hash,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();
        return new Seeded(release.Id, module.Id);
    }

    private User NewUser(Data.AppDbContext ctx, string email, UserRole role)
    {
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = email,
            DisplayName = email,
            PasswordHash = _host.HashPassword(Password),
            Role = role,
            Status = UserStatus.Active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ctx.Users.Add(user);
        return user;
    }

    private async Task<HttpClient> SignInAsync(string email)
    {
        var client = _factory.CreateClient();
        var token = await TokenFromAsync(client, "/login");
        using var login = await client.PostAsync("/auth/login", new FormUrlEncodedContent(
        [
            new("Email", email), new("Password", Password), new("__RequestVerificationToken", token),
        ]));
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().NotContain("err=");
        return client;
    }

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the page should carry an antiforgery token");
        return match.Groups[1].Value;
    }
}
