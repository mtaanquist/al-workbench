using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ALDevToolbox.Services.Cookbook;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// MCP parity for "apply a recipe to a repository" (issue #626).
///
/// <para>The point being pinned is not that the tool exists but that it goes
/// through the same service the Cookbook page uses, so an agent cannot reach a
/// repository the picker would have refused - see "Keeping MCP parity with the
/// web UI" in PROJECT.md - and that a refusal reaches the agent as an
/// <see cref="McpException"/> it can act on rather than as a stack trace.</para>
/// </summary>
public sealed class ApplyRecipeToolTests : IDisposable
{
    private const int UserId = 941;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";
    private const string BaseSha = "base-commit-sha";
    private const string Branch = "aldt/recipe-doc-attachments";

    private readonly TestDb _db = new();

    public ApplyRecipeToolTests()
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

    [Fact]
    public async Task It_reports_the_pull_request_it_opened()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.ApplyRecipeAsync(recipeId, Repo, "CRONUS A/S");

        result.RepositoryFullName.Should().Be(Repo);
        result.Branch.Should().Be(Branch);
        result.BaseBranch.Should().Be("main");
        result.PullRequestNumber.Should().Be(11);
        result.PullRequestUrl.Should().Be($"https://github.com/{Repo}/pull/11");
        result.IsNewPullRequest.Should().BeTrue();

        // The agent's apply is recorded like anyone else's, so the admin page
        // knows to send a later fix here too.
        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Source.Should().Be(RecipeUseSource.Repository);
        row.Repository.Should().Be(Repo);
        row.CustomerName.Should().Be("CRONUS A/S");
    }

    [Fact]
    public async Task It_cannot_reach_a_repository_outside_the_connected_organisation()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi()
            // The agent's user can see this one on GitHub; it is simply not in
            // the organisation the workbench organisation connected, so the web UI
            // would never offer it - and neither may the tool.
            .On(HttpMethod.Get, "/repos/someone-else/theirs", HttpStatusCode.OK,
                FakeGitHubApi.RepositoryJson("someone-else/theirs"));
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.ApplyRecipeAsync(recipeId, "someone-else/theirs");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Validation failed");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"));
    }

    [Fact]
    public async Task An_unknown_recipe_is_refused_as_a_validation_failure()
    {
        await ReadyAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var act = () => tools.ApplyRecipeAsync(9999, Repo);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("was not found");
    }

    [Fact]
    public async Task An_unlinked_caller_is_told_to_connect_their_github_account()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var recipeId = await SeedRecipeAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var act = () => tools.ApplyRecipeAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Connect your own GitHub account");
    }

    [Fact]
    public async Task A_github_refusal_is_reported_as_githubs_own_words()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi()
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Forbidden,
                "{\"message\":\"Pull request creation is disabled\"}");
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.ApplyRecipeAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("GitHub refused the request")
            .And.Contain("Pull request creation is disabled");
    }

    // --- helpers ------------------------------------------------------------

    private (CookbookTools Tools, ALDevToolbox.Data.AppDbContext Context) NewTools(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        var tools = new CookbookTools(
            _db.NewRecipeService(ctx),
            ctx,
            new RecipeSuggestionService(ctx, NullLogger<RecipeSuggestionService>.Instance, _db.OrgContext, _db.NewQuotaGuard(ctx)),
            _db.OrgContext,
            _db.DataProtectionProvider,
            TimeProvider.System,
            _db.NewGitHubRecipeDeliveryService(ctx, client, access));
        return (tools, ctx);
    }

    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, "[]")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.NotFound)
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/{BaseSha}", HttpStatusCode.OK,
                $"{{\"sha\":\"{BaseSha}\",\"tree\":{{\"sha\":\"base-tree-sha\"}}}}")
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $"{{\"number\":11,\"html_url\":\"https://github.com/{Repo}/pull/11\"}}");

    private async Task<int> SeedRecipeAsync()
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default("Doc attachments").WithFile("Attach.Codeunit.al", "// attach");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

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

    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
    }
}
