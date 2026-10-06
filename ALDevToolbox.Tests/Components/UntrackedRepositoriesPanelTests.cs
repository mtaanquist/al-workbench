using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
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

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The untracked-repositories panel on the Solutions page (issue #629).
///
/// <para>The named user is a BC consultant who has just connected their
/// company's GitHub organisation. What matters is that the panel is silent for
/// everybody it cannot help - an organisation with no GitHub connection sees the
/// Solutions page exactly as it was - and that when it can help, it says what is
/// untracked, offers to track it under a name and a country the person confirms,
/// and lets them set one aside.</para>
///
/// <para>A screenshot is not possible in this environment, so these renders are
/// the evidence for how the panel's states look.</para>
/// </summary>
public sealed class UntrackedRepositoriesPanelTests : IDisposable
{
    private const int UserId = 6291;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public UntrackedRepositoriesPanelTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("consultant@cronus.example");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<ProjectService>();
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

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();

        cut.Markup.Trim().Should().BeEmpty("someone who cannot use this must not be shown machinery explaining it");
    }

    [Fact]
    public async Task Nothing_untracked_says_so_rather_than_leaving_an_empty_box()
    {
        await ReadyAsync();
        _db.AddGitHubServices(_ctx.Services, VisibleApi());

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();

        // The panel appears once readiness is known and fills in once GitHub has
        // answered, so the assertion is the wait.
        cut.WaitForAssertion(
            () => cut.Markup.Should().Contain($"Every AL repository in {OrgLogin} that you can see already has a solution"),
            WaitTimeout);
    }

    [Fact]
    public async Task An_untracked_repository_is_listed_with_what_it_holds_and_what_can_be_done_with_it()
    {
        await ReadyAsync();
        await SeedCandidateAsync();
        _db.AddGitHubServices(_ctx.Services, VisibleApi(RepoA));

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();
        WaitForListed(cut, RepoA);

        cut.Markup.Should().Contain("1 AL repository has no solution yet");
        cut.Markup.Should().Contain("Payment Import");
        cut.Find($"a[href='https://github.com/{RepoA}']").Should().NotBeNull();
        var buttons = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        buttons.Should().Contain("Create a solution").And.Contain("Hide").And.Contain("Check GitHub again");
        // The Solutions page's one primary action is New solution; nothing here
        // competes with it.
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Tracking_asks_for_a_name_and_a_country_first_and_then_creates_the_solution()
    {
        await ReadyAsync(autoImportCountry: "dk");
        await SeedCandidateAsync();
        _db.AddGitHubServices(_ctx.Services, VisibleApi(RepoA));

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();
        WaitForListed(cut, RepoA);
        await cut.Find("button:contains('Create a solution')").ClickAsync(new());

        // Both fields arrive filled in from what the workbench already knows.
        cut.Find("#track-name").GetAttribute("value").Should().Be("Payment Import");
        cut.Find("#track-country").GetAttribute("value").Should().Be("dk");

        await cut.Find("button:contains('Create solution')").ClickAsync(new());

        await using var read = _db.NewContext();
        var project = await read.OeProjects.Include(p => p.Repositories).SingleAsync();
        project.Name.Should().Be("Payment Import");
        project.DefaultArtifactCountry.Should().Be("dk");
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri
            .Should().EndWith($"/solutions/{project.Id}/repositories",
                "the repository that was just tracked is what they came to see, and the solution now opens on Customer");
    }

    [Fact]
    public async Task A_name_the_organisation_already_uses_is_refused_beside_the_field()
    {
        await ReadyAsync(autoImportCountry: "dk");
        await SeedCandidateAsync();
        await SeedSolutionNamedAsync("Payment Import");
        _db.AddGitHubServices(_ctx.Services, VisibleApi(RepoA));

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();
        WaitForListed(cut, RepoA);
        await cut.Find("button:contains('Create a solution')").ClickAsync(new());
        await cut.Find("button:contains('Create solution')").ClickAsync(new());

        cut.Markup.Should().Contain("Another solution already uses this name");
        await using var read = _db.NewContext();
        (await read.OeProjects.CountAsync()).Should().Be(1, "the refused one was not created");
    }

    [Fact]
    public async Task Hiding_a_repository_takes_it_off_the_list_and_says_so()
    {
        await ReadyAsync();
        await SeedCandidateAsync();
        _db.AddGitHubServices(_ctx.Services, VisibleApi(RepoA));

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();
        WaitForListed(cut, RepoA);
        await cut.Find("button:contains('Hide')").ClickAsync(new());

        cut.WaitForAssertion(
            () =>
            {
                cut.FindAll(".untracked-repos__row").Should().BeEmpty();
                cut.Find(".untracked-repos__hidden").TextContent.Should().Contain($"{RepoA} hidden");
            },
            WaitTimeout);
        await using var read = _db.NewContext();
        (await read.GitHubRepositoryCandidates.SingleAsync()).IgnoredAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Undo_puts_a_hidden_repository_back_on_the_list()
    {
        await ReadyAsync();
        await SeedCandidateAsync();
        _db.AddGitHubServices(_ctx.Services, VisibleApi(RepoA));

        var cut = _ctx.Render<UntrackedRepositoriesPanel>();
        WaitForListed(cut, RepoA);
        await cut.Find("button:contains('Hide')").ClickAsync(new());
        cut.WaitForAssertion(() => cut.Find(".untracked-repos__hidden"), WaitTimeout);

        await cut.Find(".untracked-repos__hidden button").ClickAsync(new());

        cut.WaitForAssertion(
            () =>
            {
                cut.FindAll(".untracked-repos__row").Should().ContainSingle();
                cut.FindAll(".untracked-repos__hidden").Should().BeEmpty();
            },
            WaitTimeout);
        await using var read = _db.NewContext();
        (await read.GitHubRepositoryCandidates.SingleAsync()).IgnoredAt.Should().BeNull();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// Long enough for the panel's two steps - readiness from the database, then
    /// the narrowing call to GitHub - on a loaded build agent.
    /// </summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Waits until the panel has rendered the repository row.</summary>
    private static void WaitForListed(IRenderedComponent<UntrackedRepositoriesPanel> cut, string fullName) =>
        cut.WaitForAssertion(() => cut.Markup.Should().Contain(fullName), WaitTimeout);

    /// <summary>A GitHub the acting user can open <paramref name="visible"/> on, and nothing else.</summary>
    private static FakeGitHubApi VisibleApi(params string[] visible)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        foreach (var name in visible)
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name));
        }
        return api;
    }

    private async Task SeedCandidateAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        ctx.GitHubRepositoryCandidates.Add(new GitHubRepositoryCandidate
        {
            OrganizationId = TestDb.DefaultOrgId,
            FullName = RepoA,
            HtmlUrl = $"https://github.com/{RepoA}",
            CloneUrl = $"https://github.com/{RepoA}.git",
            DefaultBranch = "main",
            AppName = "Payment Import",
            AppId = "1c0ffee0-0000-4000-8000-000000000001",
            AppJsonPath = "app.json",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedSolutionNamedAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        ctx.OeProjects.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ReadyAsync(string? autoImportCountry = null)
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
                AutoImportCountry = autoImportCountry,
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
