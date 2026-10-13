using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The shared repository read gate (issue #623): the five states the picker
/// renders, the two-credential list behind it, and the resolver every caller -
/// the page and the MCP tool alike - has to go through.
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.</para>
/// </summary>
public sealed class GitHubRepositoryServiceTests : IDisposable
{
    private const int UserId = 601;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";

    private readonly TestDb _db = new();

    public GitHubRepositoryServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "dev@cronus.example",
            DisplayName = "dev@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    // --- The five states the picker renders --------------------------------

    [Fact]
    public async Task Without_an_app_registration_the_picker_is_told_the_server_is_not_set_up()
    {
        var (service, ctx, _) = NewService(new FakeGitHubApi());
        await using var _1 = ctx;

        var access = await service.GetAccessAsync();

        access.Readiness.Should().Be(GitHubRepositoryReadiness.NotConfigured);
        access.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task With_an_app_but_no_connected_organisation_the_picker_is_told_to_connect_one()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx, _) = NewService(new FakeGitHubApi());
        await using var _1 = ctx;

        (await service.GetAccessAsync()).Readiness.Should().Be(GitHubRepositoryReadiness.NotConnected);
    }

    [Fact]
    public async Task A_connected_organisation_with_an_unlinked_user_asks_them_to_link()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var (service, ctx, _) = NewService(new FakeGitHubApi());
        await using var _1 = ctx;

        var access = await service.GetAccessAsync();

        access.Readiness.Should().Be(GitHubRepositoryReadiness.NotLinked);
        access.OrgLogin.Should().Be(OrgLogin, "the copy names the organisation the user would be seeing");
    }

    [Fact]
    public async Task A_link_whose_credentials_are_gone_asks_them_to_link_again()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync(new FakeGitHubApi());

        // An access token that has lapsed with no refresh token behind it is a
        // link that will fail the first time a feature leans on it.
        await using (var ctx = _db.NewContext())
        {
            var row = ctx.UserExternalLogins.Single(l => l.Provider == GitHubAccessService.ProviderName);
            row.RefreshTokenEncrypted = null;
            row.AccessTokenExpiresAt = DateTime.UtcNow.AddHours(-1);
            await ctx.SaveChangesAsync();
        }

        var (service, ctx2, _) = NewService(new FakeGitHubApi());
        await using var _1 = ctx2;

        (await service.GetAccessAsync()).Readiness.Should().Be(GitHubRepositoryReadiness.LinkNeedsRepair);
    }

    [Fact]
    public async Task Everything_in_place_is_ready()
    {
        await ReadyAsync();
        var (service, ctx, _) = NewService(new FakeGitHubApi());
        await using var _1 = ctx;

        (await service.GetAccessAsync()).IsReady.Should().BeTrue();
    }

    [Fact]
    public async Task None_of_the_states_before_ready_calls_github()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = new FakeGitHubApi();
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        await service.GetAccessAsync();

        // The picker has to render its guidance while GitHub is down, which it
        // can only do if answering "why not" costs no call.
        api.Calls.Should().BeEmpty();
    }

    // --- The list ----------------------------------------------------------

    [Fact]
    public async Task The_list_is_the_installations_repositories_narrowed_to_the_ones_the_user_can_open()
    {
        await ReadyAsync();
        // The installation was granted all three; this person can open two.
        var api = ListableApi(
            granted: ["cronus-dk/solution-b", "cronus-dk/solution-a", "cronus-dk/secret"],
            visible: ["cronus-dk/solution-b", "cronus-dk/solution-a"])
            .On(HttpMethod.Get, "/repos/cronus-dk/secret", HttpStatusCode.NotFound);
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        var repos = await service.ListAccessibleAsync();

        repos.Select(r => r.FullName).Should().Equal("cronus-dk/solution-a", "cronus-dk/solution-b");
    }

    [Fact]
    public async Task The_list_asks_github_once_which_repositories_the_user_can_reach_rather_than_once_per_repository()
    {
        await ReadyAsync();
        var api = ListableApi(
                granted: ["cronus-dk/solution-b", "cronus-dk/solution-a", "cronus-dk/secret"],
                visible: [])
            .On(HttpMethod.Get, $"/user/installations/{InstallationId}/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson("cronus-dk/solution-a", "cronus-dk/solution-b"));
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        var repos = await service.ListAccessibleAsync();

        repos.Select(r => r.FullName).Should().Equal("cronus-dk/solution-a", "cronus-dk/solution-b");
        api.Calls.Should().NotContain(c => c.Contains("/repos/cronus-dk/"),
            "one read of the person's reach replaces a call per repository (#1232)");
    }

    [Fact]
    public async Task When_github_will_not_say_what_the_user_can_reach_each_repository_is_asked_about_instead()
    {
        await ReadyAsync();
        var api = ListableApi(
                granted: ["cronus-dk/solution-a", "cronus-dk/secret"],
                visible: ["cronus-dk/solution-a"])
            .On(HttpMethod.Get, "/repos/cronus-dk/secret", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/user/installations/{InstallationId}/repositories", HttpStatusCode.Forbidden,
                "{\"message\":\"API rate limit exceeded.\"}");
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        (await service.ListAccessibleAsync()).Select(r => r.FullName).Should().Equal("cronus-dk/solution-a");
    }

    [Fact]
    public async Task An_unlinked_user_is_offered_nothing_and_no_installation_token_is_minted()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = ListableApi("cronus-dk/solution-a");
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        (await service.ListAccessibleAsync()).Should().BeEmpty();
        api.Calls.Should().BeEmpty();
    }

    // --- The resolver every caller goes through ----------------------------

    [Fact]
    public async Task Resolving_returns_the_repository_when_the_user_can_open_it()
    {
        await ReadyAsync();
        var (service, ctx, _) = NewService(ListableApi("cronus-dk/solution-a"));
        await using var _1 = ctx;

        var repo = await service.ResolveAsync("cronus-dk/solution-a");

        repo.Should().NotBeNull();
        repo!.Owner.Should().Be("cronus-dk");
        repo.Name.Should().Be("solution-a");
        repo.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public async Task Resolving_refuses_a_repository_outside_the_connected_organisation()
    {
        await ReadyAsync();
        var api = ListableApi("cronus-dk/solution-a")
            // The user can see this one perfectly well on GitHub - it is simply
            // not in the organisation their workbench organisation connected.
            .On(HttpMethod.Get, "/repos/someone-else/private-thing", HttpStatusCode.OK,
                FakeGitHubApi.RepositoryJson("someone-else/private-thing"));
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        (await service.ResolveAsync("someone-else/private-thing")).Should().BeNull();
        api.Calls.Should().NotContain(c => c.Contains("someone-else"),
            "the owner is checked before GitHub is asked anything about it");
    }

    [Fact]
    public async Task Resolving_refuses_a_repository_the_user_cannot_open()
    {
        await ReadyAsync();
        var api = ListableApi()
            .On(HttpMethod.Get, "/repos/cronus-dk/not-mine", HttpStatusCode.NotFound);
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        // GitHub answers 404 rather than 403 for a repository you cannot see,
        // and that is a refusal, not a "gone".
        (await service.ResolveAsync("cronus-dk/not-mine")).Should().BeNull();
    }

    [Fact]
    public async Task Resolving_refuses_when_the_user_has_not_linked()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var (service, ctx, _) = NewService(ListableApi("cronus-dk/solution-a"));
        await using var _1 = ctx;

        (await service.ResolveAsync("cronus-dk/solution-a")).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("solution-a")]
    [InlineData("cronus-dk/solution-a/extra")]
    public async Task Resolving_refuses_anything_that_is_not_owner_slash_name(string candidate)
    {
        await ReadyAsync();
        var (service, ctx, _) = NewService(ListableApi("cronus-dk/solution-a"));
        await using var _1 = ctx;

        (await service.ResolveAsync(candidate)).Should().BeNull();
    }

    // --- Reading the saved config ------------------------------------------

    [Fact]
    public async Task The_saved_config_is_read_from_the_repository_root()
    {
        await ReadyAsync();
        var api = ListableApi("cronus-dk/solution-a")
            .On(HttpMethod.Get, "/repos/cronus-dk/solution-a/contents/workspace.aldt.toml",
                HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("workspace.aldt.toml", "kind = \"workspace\"\n"));
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        var repo = await service.ResolveAsync("cronus-dk/solution-a");
        var config = await service.TryReadWorkspaceConfigAsync(repo!);

        config.Should().NotBeNull();
        config!.Text.Should().Contain("kind = \"workspace\"");
    }

    [Fact]
    public async Task A_repository_without_a_saved_config_reads_as_nothing_rather_than_a_failure()
    {
        await ReadyAsync();
        var api = ListableApi("cronus-dk/solution-a")
            .On(HttpMethod.Get, "/repos/cronus-dk/solution-a/contents/", HttpStatusCode.NotFound);
        var (service, ctx, _) = NewService(api);
        await using var _1 = ctx;

        var repo = await service.ResolveAsync("cronus-dk/solution-a");

        (await service.TryReadWorkspaceConfigAsync(repo!)).Should().BeNull();
    }

    // --- helpers ------------------------------------------------------------

    private (GitHubRepositoryService Service, AppDbContext Context, GitHubAccessService Access) NewService(
        FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubRepositoryService(ctx, client, access), ctx, access);
    }

    /// <summary>A deployment with an app registration complete enough for both halves.</summary>
    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    /// <summary>Connects the organisation without going through the guarded ConnectAsync.</summary>
    private async Task ConnectOrganisationAsync()
    {
        await using var ctx = _db.NewContext();
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

    private async Task LinkAsync(FakeGitHubApi api)
    {
        api.On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
           .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked.</summary>
    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync(new FakeGitHubApi());
    }

    /// <summary>
    /// A GitHub that can mint an installation token, list
    /// <paramref name="fullNames"/>, and say yes to each of them.
    /// </summary>
    private static FakeGitHubApi ListableApi(params string[] fullNames) =>
        ListableApi(fullNames, fullNames);

    /// <summary>
    /// A GitHub where the installation was granted <paramref name="granted"/>
    /// and the acting user can open <paramref name="visible"/> - the two lists
    /// the picker has to reconcile.
    /// </summary>
    private static FakeGitHubApi ListableApi(string[] granted, string[] visible)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(granted));
        foreach (var name in visible)
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name));
        }
        return api;
    }
}
