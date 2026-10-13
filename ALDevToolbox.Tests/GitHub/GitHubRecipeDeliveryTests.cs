using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// "Apply a recipe to a repository" (issue #626): what the commit contains,
/// which branch it lands on, what a second apply does, and every refusal on the
/// way.
///
/// <para>The rules worth a mistake being expensive: the write is the acting
/// user's and never touches the default branch, a pull request that is still
/// open takes the second commit rather than getting a sibling beside it, a
/// merged branch is stepped past rather than rewound, and the apply is recorded
/// against the repository so a later fix knows where to go.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.</para>
/// </summary>
public sealed class GitHubRecipeDeliveryTests : IDisposable
{
    private const int UserId = 731;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";
    private const string BaseSha = "base-commit-sha";
    private const string BranchSha = "branch-commit-sha";
    private const string Branch = "aldt/recipe-doc-attachments";

    private readonly TestDb _db = new();

    public GitHubRecipeDeliveryTests()
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
    public async Task It_opens_a_pull_request_from_the_recipes_own_branch_into_the_default_branch()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.ApplyAsync(recipeId, Repo);

        delivery.IsNewPullRequest.Should().BeTrue();
        delivery.PullRequest.Number.Should().Be(11);
        delivery.PullRequest.HeadBranch.Should().Be(Branch);
        delivery.FileCount.Should().Be(2);

        var pull = BodyOf(api, "POST", "/pulls");
        pull.Should().Contain($"\"head\":\"{Branch}\"");
        pull.Should().Contain("\"base\":\"main\"");
    }

    [Fact]
    public async Task The_commit_carries_exactly_the_recipes_paths()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.ApplyAsync(recipeId, Repo);

        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain("Setup/Setup.Table.al", "a file's folder comes from the recipe, not from us");
        tree.Should().Contain("Attach.Codeunit.al");
        // Layered onto what the default branch already has, so a file the recipe
        // carries replaces the repository's copy and nothing else moves.
        tree.Should().Contain("\"base_tree\":\"base-tree-sha\"");
    }

    [Fact]
    public async Task Nothing_is_written_to_the_default_branch_and_every_write_is_the_users_own()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.ApplyAsync(recipeId, Repo);

        api.Bodies.Should().NotContain(b => b.Body.Contains("refs/heads/main"));
        BodyOf(api, "POST", "/git/refs").Should().Contain($"refs/heads/{Branch}");

        // The security decision this feature turns on: the commit, the branch
        // and the pull request all go out on the acting user's token, so GitHub
        // enforces their own permissions and the pull request is theirs.
        foreach (var call in new[] { "/git/blobs", "/git/trees", "/git/commits", "/git/refs", "/pulls" })
        {
            api.Credentials
                .Where(c => c.Call.StartsWith("POST", StringComparison.Ordinal) && c.Call.Contains(call))
                .Should().OnlyContain(c => c.Token == "ghu_access", $"{call} is written as the user");
        }
    }

    [Fact]
    public async Task A_second_apply_joins_the_pull_request_that_is_still_open()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK,
                $"[{{\"number\":11,\"html_url\":\"https://github.com/{Repo}/pull/11\"}}]")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/{Branch}\",\"object\":{{\"sha\":\"{BranchSha}\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/{BranchSha}", HttpStatusCode.OK,
                $"{{\"sha\":\"{BranchSha}\",\"tree\":{{\"sha\":\"branch-tree-sha\"}}}}")
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/{Branch}", HttpStatusCode.OK,
                FakeGitHubApi.ShaJson("new-commit-sha"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.ApplyAsync(recipeId, Repo);

        delivery.IsNewPullRequest.Should().BeFalse();
        delivery.PullRequest.Number.Should().Be(11);
        // A second review beside the first is the thing to avoid; the branch is
        // moved on instead, and it is built on its own head, not on main's.
        api.Calls.Should().NotContain(c => c.Contains("POST") && c.EndsWith("/pulls", StringComparison.Ordinal));
        BodyOf(api, "POST", "/git/trees").Should().Contain("\"base_tree\":\"branch-tree-sha\"");
        BodyOf(api, "POST", "/git/commits").Should().Contain($"\"parents\":[\"{BranchSha}\"]");
    }

    [Fact]
    public async Task A_branch_whose_pull_request_was_merged_is_stepped_rather_than_reused()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        // The first round's branch is still there, its pull request merged and
        // closed. Rewinding it would rewrite merged history.
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/{Branch}\",\"object\":{{\"sha\":\"{BranchSha}\"}}}}")
            // The fake matches routes by path prefix, and the stepped name is
            // one of the first name's - so say outright that -2 is free.
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{Branch}-2", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.ApplyAsync(recipeId, Repo);

        delivery.PullRequest.HeadBranch.Should().Be($"{Branch}-2");
        api.Calls.Should().NotContain(c => c.StartsWith("PATCH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_apply_is_recorded_against_the_repository_and_the_customer()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var projectId = await SeedProjectAsync("CRONUS A/S");
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        await service.ApplyAsync(recipeId, Repo, "cronus a/s");

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.Source.Should().Be(RecipeUseSource.Repository);
        row.Repository.Should().Be(Repo);
        row.CustomerName.Should().Be("cronus a/s");
        row.ProjectId.Should().Be(projectId, "a name that matches a solution is stamped with it, as a download is");
        row.DownloadedByUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task An_apply_with_no_customer_still_records_the_repository()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        await service.ApplyAsync(recipeId, Repo);

        await using var verify = _db.NewContext();
        var row = await verify.RecipeDownloads.SingleAsync(d => d.RecipeId == recipeId);
        row.CustomerName.Should().BeNull();
        row.Repository.Should().Be(Repo);
    }

    [Fact]
    public async Task A_recipe_that_does_not_exist_is_refused_before_github_is_asked_anything()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.ApplyAsync(9999, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Recipe");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_recipe_is_refused_before_github_is_asked_anything()
    {
        // It used to pass the lookup, create the branch, the commit and the
        // pull request, and only then fail recording the apply (#1251).
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        await using (var ctx = _db.NewContext())
        {
            (await ctx.Recipes.SingleAsync(r => r.Id == recipeId)).DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }
        var api = WritableApi();
        var (service, svcCtx) = NewService(api);
        await using var _ = svcCtx;

        var act = () => service.ApplyAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Recipe");
        api.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_recipe_with_no_files_is_refused_with_a_reason_an_editor_can_act_on()
    {
        await ReadyAsync();
        var recipeId = await SeedEmptyRecipeAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var act = () => service.ApplyAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Recipe"].Should().Contain("no files");
    }

    [Fact]
    public async Task A_repository_outside_the_connected_organisation_is_refused()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.ApplyAsync(recipeId, "someone-else/theirs");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubRepository");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"));
    }

    [Fact]
    public async Task An_unlinked_user_is_refused_before_anything_is_committed()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.ApplyAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("Connect your own GitHub account");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"));
    }

    [Fact]
    public async Task A_repository_with_no_commits_yet_is_refused_with_a_reason_the_user_can_act_on()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.ApplyAsync(recipeId, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("no commits");
    }

    [Fact]
    public async Task Nothing_is_recorded_when_github_refuses_the_commit()
    {
        await ReadyAsync();
        var recipeId = await SeedRecipeAsync();
        var api = WritableApi()
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Forbidden,
                "{\"message\":\"Pull request creation is disabled\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await Assert.ThrowsAsync<GitHubApiException>(() => service.ApplyAsync(recipeId, Repo));

        // The history says where the recipe went. A row for a pull request that
        // was never opened would send the next fix to a repository that never
        // took it.
        await using var verify = _db.NewContext();
        (await verify.RecipeDownloads.AnyAsync(d => d.RecipeId == recipeId)).Should().BeFalse();
    }

    // --- helpers ------------------------------------------------------------

    private (GitHubRecipeDeliveryService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubRecipeDeliveryService(ctx, client, access), ctx);
    }

    private static string BodyOf(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Bodies
            .Where(b => b.Call.StartsWith(method, StringComparison.Ordinal) && b.Call.Contains(pathSuffix))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    /// <summary>
    /// A GitHub that answers every call the commit needs, with no pull request
    /// open on the recipe's branch and no such branch yet - the first apply.
    /// </summary>
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
        var recipe = RecipeBuilder.Default("Doc attachments")
            .WithFile("Attach.Codeunit.al", "// attach")
            .WithFile("Setup.Table.al", "// setup", relativePath: "Setup");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<int> SeedEmptyRecipeAsync()
    {
        await using var ctx = _db.NewContext();
        var recipe = RecipeBuilder.Default("Empty recipe");
        ctx.Recipes.Add(recipe);
        await ctx.SaveChangesAsync();
        return recipe.Id;
    }

    private async Task<int> SeedProjectAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
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

    private async Task LinkAsync()
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked.</summary>
    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync();
    }
}
