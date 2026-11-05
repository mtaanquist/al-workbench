using System.Net;
using ALDevToolbox.Components.Pages.Repositories;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Repositories list (issue #1719). The named user is a lead developer keeping
/// customer repositories current: they need to see which repositories are behind
/// their customer's Business Central, and open the pull requests that move them on
/// from the row - while somebody without a GitHub link still gets the list, just
/// without actions they cannot use.
/// </summary>
public sealed class RepositoriesListTests : IDisposable
{
    private const int UserId = 6340;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";
    private const string RepoB = "cronus-dk/warehouse-ext";

    private const string BehindManifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS",
         "version":"1.0.0.0","application":"27.0.0.0"}
        """;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public RepositoriesListTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("lead@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<CatalogService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped(sp => _db.NewQuotaGuard(sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>()));
        _db.AddRepositoryReadProbe(_ctx.Services);
        _db.AddStorageServices(_ctx.Services);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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
            Email = "lead@cronus.example",
            DisplayName = "lead@cronus.example",
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
    public async Task No_repositories_says_where_to_add_one()
    {
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = _ctx.Render<RepositoriesList>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No repositories yet"), WaitTimeout);
        cut.FindAll("a[href='/solutions']").Should().Contain(a => a.TextContent.Contains("Go to solutions"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_repository_behind_its_customer_shows_a_warning_next_to_its_version()
    {
        await ReadyAsync();
        await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<RepositoriesList>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        var row = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("payment-import"));
        row.TextContent.Should().Contain("CRONUS A/S payments").And.Contain("28.2").And.Contain("27.0");
        row.QuerySelector(".repos__warn")!.GetAttribute("title")
            .Should().Contain("Business Central application 27.0.0.0 to 28.2.0.0");
        var upToDate = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("warehouse-ext"));
        upToDate.QuerySelector(".repos__warn").Should().BeNull();
        // Generate is the app's one primary action; nothing here competes with it.
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Updates_available_shows_only_the_repositories_that_are_behind()
    {
        await ReadyAsync();
        await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<RepositoriesList>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoB), WaitTimeout);
        await cut.Find("button.pill-tab:contains('Updates available')").ClickAsync(new());

        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("payment-import");
    }

    [Fact]
    public async Task Opening_the_update_pull_request_from_the_row_shows_the_link_it_got_back()
    {
        await ReadyAsync();
        await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<RepositoriesList>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);
        await cut.Find("button.menu__item:contains('Create app.json pull request')").ClickAsync(new());

        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain("Pull request opened").And.Contain($"https://github.com/{RepoA}/pull/77"),
            WaitTimeout);
    }

    [Fact]
    public async Task A_repository_whose_workspace_files_differ_says_how_many_and_offers_a_pull_request()
    {
        await ReadyAsync();
        await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.GitHubRepositoryFileChecks.AddRange(
                new GitHubRepositoryFileCheck
                {
                    OrganizationId = TestDb.DefaultOrgId, Repository = RepoA, CheckedAt = DateTime.UtcNow,
                    MissingPaths = [".github/workflows/refresh-test.yml"], ChangedPaths = [".gitignore"],
                },
                new GitHubRepositoryFileCheck
                {
                    OrganizationId = TestDb.DefaultOrgId, Repository = RepoB, CheckedAt = DateTime.UtcNow,
                });
            await ctx.SaveChangesAsync();
        }
        _db.AddGitHubServices(_ctx.Services, WritableApi());

        var cut = _ctx.Render<RepositoriesList>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        var behind = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("payment-import"));
        behind.TextContent.Should().Contain("2 files to update");
        behind.QuerySelectorAll(".repos__warn").Select(w => w.GetAttribute("title")).Should()
            .Contain(t => t != null && t.Contains("Missing: .github/workflows/refresh-test.yml") && t.Contains("Different: .gitignore"));
        behind.QuerySelectorAll("button.menu__item").Select(b => b.TextContent).Should()
            .Contain("Create workspace files pull request");
        var upToDate = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("warehouse-ext"));
        upToDate.TextContent.Should().Contain("Up to date");
        upToDate.QuerySelectorAll("button.menu__item").Select(b => b.TextContent).Should()
            .NotContain(t => t.Contains("workspace files"));
    }

    [Fact]
    public async Task Somebody_without_a_github_link_sees_the_list_without_the_github_actions()
    {
        await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = _ctx.Render<RepositoriesList>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(RepoA), WaitTimeout);

        cut.FindAll("button.menu__item").Select(b => b.TextContent).Should()
            .NotContain(t => t.Contains("pull request") || t.Contains("Check again"));
        cut.Markup.Should().NotContain("Check all again");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private static FakeGitHubApi WritableApi()
    {
        const string branch = "aldt/bump-bc-28.2";
        return new FakeGitHubApi()
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
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-blob"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-ref"))
            .On(HttpMethod.Post, $"/repos/{RepoA}/pulls", HttpStatusCode.Created,
                $"{{\"number\":77,\"html_url\":\"https://github.com/{RepoA}/pull/77\"}}");
    }

    /// <summary>Two live solutions on 28.2: one repository a wave behind, one up to date.</summary>
    private async Task SeedAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new OeRelease
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
        foreach (var (name, repo) in new[] { ("CRONUS A/S payments", RepoA), ("CRONUS A/S warehouse", RepoB) })
        {
            ctx.OeProjects.Add(new OeProject
            {
                OrganizationId = TestDb.DefaultOrgId,
                Name = name,
                DefaultArtifactCountry = "dk",
                CreatedByUserId = UserId,
                CreatedAt = now,
                UpdatedAt = now,
                Repositories =
                [
                    new OeProjectRepository
                    {
                        OrganizationId = TestDb.DefaultOrgId,
                        Provider = RepositoryProvider.GitHub,
                        Url = $"https://github.com/{repo}.git",
                        DisplayName = repo.Split('/')[^1],
                        AccessConfirmedAt = now,
                    },
                ],
            });
        }
        await ctx.SaveChangesAsync();

        foreach (var project in await ctx.OeProjects.ToListAsync())
        {
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = project.Id,
                Name = "Production",
                Type = "Production",
                Version = "28.2.45123.0",
                FetchedAt = now,
            });
        }
        ctx.GitHubRepositoryChecks.AddRange(
            new GitHubRepositoryCheck { OrganizationId = TestDb.DefaultOrgId, Repository = RepoA, Application = "27.0", CheckedAt = now },
            new GitHubRepositoryCheck { OrganizationId = TestDb.DefaultOrgId, Repository = RepoB, Application = "28.2", CheckedAt = now });
        await ctx.SaveChangesAsync();
        ctx.GitHubRepositoryDrift.Add(new GitHubRepositoryDrift
        {
            OrganizationId = TestDb.DefaultOrgId,
            Repository = RepoA,
            Path = "app.json",
            Field = "application",
            Current = "27.0.0.0",
            Proposed = "28.2.0.0",
            ReleaseId = release.Id,
            EnvironmentId = await ctx.OeProjectEnvironments.Where(e => e.Project!.Name == "CRONUS A/S payments").Select(e => e.Id).SingleAsync(),
            DetectedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ReadyAsync()
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));

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
