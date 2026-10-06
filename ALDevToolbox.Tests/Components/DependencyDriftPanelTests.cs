using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The dependency-drift panel on the Solutions page (issue #630).
///
/// <para>The named user is a consultant who has just imported a new Business
/// Central release. What matters is that the panel is silent for everybody it
/// cannot help - nothing behind, or no GitHub connection, and the Solutions page
/// looks exactly as it did - and that when it can help, it says how many
/// repositories are behind, what would change in each of them, and opens the
/// pull requests.</para>
///
/// <para>A screenshot is not possible in this environment, so these renders are
/// the evidence for how the panel's states look.</para>
/// </summary>
public sealed class DependencyDriftPanelTests : IDisposable
{
    private const int UserId = 6320;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";

    private const string BehindManifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS",
         "version":"1.0.0.0","application":"27.0.0.0"}
        """;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public DependencyDriftPanelTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("consultant@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<CatalogService>();
        _db.AddStorageServices(_ctx.Services);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(
            typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "consultant@cronus.example",
            DisplayName = "consultant@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_organisation_with_no_github_connection_sees_nothing_at_all()
    {
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = _ctx.Render<DependencyDriftPanel>();

        cut.Markup.Trim().Should().BeEmpty("someone who cannot use this must not be shown machinery explaining it");
    }

    [Fact]
    public async Task Nothing_behind_is_silent_rather_than_an_empty_box()
    {
        await ReadyAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<DependencyDriftPanel>();

        cut.WaitForAssertion(() => cut.Markup.Trim().Should().BeEmpty(), WaitTimeout);
    }

    [Fact]
    public async Task A_repository_that_is_behind_is_listed_with_what_would_change()
    {
        await ReadyAsync();
        await SeedDriftAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<DependencyDriftPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        cut.Markup.Should().Contain("1 repository is still on Business Central 27.0");
        cut.Markup.Should().Contain("Business Central application");
        cut.Markup.Should().Contain("27.0.0.0");
        cut.Markup.Should().Contain("28.2.0.0");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().Contain("Open update pull requests").And.Contain("Check again");
        // The Solutions page's one primary action is New solution; nothing here
        // competes with it.
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task A_finding_from_before_environments_were_the_yardstick_asks_for_a_fresh_check()
    {
        await ReadyAsync();
        await SeedDriftAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<DependencyDriftPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        cut.Find(".drift__sub").TextContent.Should().Contain("Check again to compare them with their solution's production environment");
        cut.Find(".drift__on").TextContent.Should().Contain("Compared with the newest imported release, Business Central 28.2.");
    }

    [Fact]
    public async Task A_repository_measured_against_production_names_the_environment()
    {
        await ReadyAsync();
        await SeedDriftAsync(againstProduction: true);
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<DependencyDriftPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        cut.Find(".drift__sub").TextContent.Should().Contain("than their solution's production environment runs")
            .And.NotContain("imported");
        cut.Find(".drift__on").TextContent
            .Should().Contain("The production environment of CRONUS A/S payments (\"Production\") is on Business Central 28.2.");
    }

    [Fact]
    public async Task Opening_the_pull_requests_shows_the_link_it_got_back()
    {
        await ReadyAsync();
        await SeedDriftAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<DependencyDriftPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);
        await cut.Find("button:contains('Open update pull requests')").ClickAsync(new());

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Pull request opened").And.Contain($"https://github.com/{RepoA}/pull/77"),
            WaitTimeout);
    }

    [Fact]
    public async Task A_repository_the_person_cannot_open_says_so_where_it_is_listed()
    {
        await ReadyAsync();
        await SeedDriftAsync();
        // GitHub answers 404 for every repository, which is what it says about
        // one this person cannot see.
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"));

        var cut = _ctx.Render<DependencyDriftPanel>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);
        await cut.Find("button:contains('Open pull request')").ClickAsync(new());

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("not one the workbench can offer you"), WaitTimeout);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>A GitHub the acting person can open the repository on and commit to.</summary>
    private static FakeGitHubApi WritableApi()
    {
        const string branch = "aldt/bump-bc-28.2";
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
            .On(HttpMethod.Get, $"/repos/{RepoA}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/pulls", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/ref/heads/{branch}", HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}")
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/ref/heads/main", HttpStatusCode.OK,
                "{\"object\":{\"sha\":\"main-head\"}}")
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", BehindManifest))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/commits/", HttpStatusCode.OK,
                "{\"sha\":\"parent\",\"tree\":{\"sha\":\"base-tree\"}}")
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/blobs", HttpStatusCode.Created,
                FakeGitHubApi.ShaJson("new-blob"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/trees", HttpStatusCode.Created,
                FakeGitHubApi.ShaJson("new-tree"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/commits", HttpStatusCode.Created,
                FakeGitHubApi.ShaJson("new-commit"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/refs", HttpStatusCode.Created,
                FakeGitHubApi.ShaJson("new-ref"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/pulls", HttpStatusCode.Created,
                $"{{\"number\":77,\"html_url\":\"https://github.com/{RepoA}/pull/77\"}}");
        return api;
    }

    /// <summary>A solution tracking the repository, and one finding against it.</summary>
    private async Task SeedDriftAsync(bool againstProduction = false)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            Kind = "first_party",
            Status = "ready",
            BcVersion = "28.2.50931.51727",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        ctx.OeProjects.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S payments",
            DefaultArtifactCountry = "dk",
            CreatedByUserId = UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories =
            [
                new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = RepositoryProvider.GitHub,
                    Url = $"https://github.com/{RepoA}.git",
                    DisplayName = "payment-import",
                },
            ],
        });
        await ctx.SaveChangesAsync();

        int? environmentId = null;
        if (againstProduction)
        {
            var environment = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = await ctx.OeProjects.Select(p => p.Id).SingleAsync(),
                Name = "Production",
                Type = "Production",
                Version = "28.2.45123.0",
                FetchedAt = now,
            };
            ctx.OeProjectEnvironments.Add(environment);
            await ctx.SaveChangesAsync();
            environmentId = environment.Id;
        }

        ctx.GitHubRepositoryDrift.Add(new GitHubRepositoryDrift
        {
            EnvironmentId = environmentId,
            OrganizationId = TestDb.DefaultOrgId,
            Repository = RepoA,
            Path = "app.json",
            Field = "application",
            Current = "27.0.0.0",
            Proposed = "28.2.0.0",
            ReleaseId = release.Id,
            DetectedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ReadyAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));

        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = OrgLogin,
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var linkApi = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var linkCtx = _db.NewContext();
        await _db.NewGitHubAccessService(linkCtx, _db.NewGitHubAppClient(linkCtx, linkApi)).LinkAsync("the-code");
    }
}
