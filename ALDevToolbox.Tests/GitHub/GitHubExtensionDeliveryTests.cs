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
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// "Add to repository" (issue #623): what the commit contains, where it lands,
/// and every refusal on the way.
///
/// <para>The rules being pinned here are the ones a mistake would be expensive
/// for: the write goes onto a branch of its own and never the default one, the
/// files are the same ones the ZIP carries, and a repository the picker would
/// not have offered is refused whoever asks.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.
/// The request shapes are GitHub's documented ones - they have not been
/// exercised against api.github.com from this environment.</para>
/// </summary>
public sealed class GitHubExtensionDeliveryTests : IDisposable
{
    private const int UserId = 701;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";
    private const string BaseSha = "base-commit-sha";

    private readonly TestDb _db = new();

    public GitHubExtensionDeliveryTests()
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
    public async Task It_opens_a_pull_request_from_its_own_branch_into_the_default_branch()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        delivery.PullRequest.Number.Should().Be(7);
        delivery.PullRequest.HtmlUrl.Should().Be("https://github.com/cronus-dk/solution-a/pull/7");
        delivery.PullRequest.HeadBranch.Should().Be("aldt/add-MyCustomFeature");
        delivery.FolderName.Should().Be("MyCustomFeature");

        var pull = BodyOf(api, "POST", "/pulls");
        pull.Should().Contain("\"head\":\"aldt/add-MyCustomFeature\"");
        pull.Should().Contain("\"base\":\"main\"");
    }

    [Fact]
    public async Task Nothing_is_ever_written_to_the_default_branch()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        // The only ref written is the new branch. main is read to find the
        // commit to branch from, and never touched again - even though this
        // repository has no branch protection to stop us.
        api.Bodies.Should().NotContain(b => b.Body.Contains("refs/heads/main"));
        api.Calls.Should().NotContain(c => c.StartsWith("PATCH", StringComparison.Ordinal));
        BodyOf(api, "POST", "/git/refs").Should().Contain("refs/heads/aldt/add-MyCustomFeature");
    }

    [Fact]
    public async Task The_commit_carries_the_extension_folder_and_leaves_the_repositorys_own_root_files_alone()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain("MyCustomFeature/app.json");
        tree.Should().Contain($"\"base_tree\":\"base-tree-sha\"",
            "the new files are layered onto what the default branch already has");
        // The repository already has a root of its own; a second .gitignore and
        // README one level down inside the extension folder would be noise.
        tree.Should().NotContain("MyCustomFeature/.gitignore");
        tree.Should().NotContain("MyCustomFeature/README.md");
        delivery.FileCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_sibling_workspace_gets_its_workspace_file_updated_in_the_same_pull_request()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"),
            new SiblingWorkspaceContext("CRONUS Customer", Array.Empty<string>(), new[] { "Core" }),
            Repo);

        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain("CRONUSCustomer.code-workspace",
            "the new folder has to be listed in the workspace file or it will not open with the rest");
    }

    [Fact]
    public async Task The_zip_it_hands_back_is_the_one_it_committed()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var delivery = await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        // Generating a second time would mint fresh extension GUIDs, so a
        // download offered beside the pull request has to be these bytes.
        delivery.ArchiveFileName.Should().Be("MyCustomFeature.zip");
        delivery.Archive.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_taken_branch_name_is_stepped_rather_than_moved()
    {
        await ReadyAsync();
        var api = WritableApi();
        // A first attempt is already open on aldt/add-MyCustomFeature; its pull
        // request may be under review, so it must not be rewound.
        api.OnSequence(HttpMethod.Post, $"/repos/{Repo}/git/refs",
            (HttpStatusCode.UnprocessableEntity, "{\"message\":\"Reference already exists\"}"),
            (HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha")));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var delivery = await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        delivery.PullRequest.HeadBranch.Should().Be("aldt/add-MyCustomFeature-2");
    }

    [Fact]
    public async Task A_422_that_is_not_a_taken_name_is_reported_rather_than_retried()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.UnprocessableEntity,
                "{\"message\":\"Object does not exist\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        // Stepping the name would retry a broken request nine more times and
        // then blame the user's branches for it.
        (await act.Should().ThrowAsync<GitHubApiException>())
            .Which.Message.Should().Contain("Object does not exist");
        api.Calls.Count(c => c.Contains("git/refs")).Should().Be(1);
    }

    [Fact]
    public async Task A_repository_outside_the_connected_organisation_is_refused()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, "someone-else/theirs");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GitHubRepository");
    }

    [Fact]
    public async Task A_repository_the_user_cannot_open_is_refused()
    {
        await ReadyAsync();
        var api = WritableApi().On(HttpMethod.Get, "/repos/cronus-dk/not-mine", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, "cronus-dk/not-mine");

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task An_unlinked_user_is_refused_before_anything_is_generated()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await SeedTemplateAsync(TemplateBuilder.Default());
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("Connect your own GitHub account");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"));
    }

    [Fact]
    public async Task A_repository_with_no_commits_yet_is_refused_with_a_reason_the_user_can_act_on()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.NotFound);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("no commits");
    }

    [Fact]
    public async Task An_extension_folder_that_is_already_there_is_refused_rather_than_overwritten()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/MyCustomFeature/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("MyCustomFeature/app.json", "{}"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "My Custom Feature"), sibling: null, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["GitHubRepository"].Should().Contain("already has an extension");
    }

    [Fact]
    public async Task An_invalid_plan_is_refused_by_the_generators_own_rules()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(WritableApi());
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "9 Lives"), sibling: null, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("ExtensionName");
    }

    [Fact]
    public async Task An_invalid_plan_is_refused_before_github_is_asked_anything()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "9 Lives"), sibling: null, Repo);

        await act.Should().ThrowAsync<PlanValidationException>();
        api.Calls.Should().BeEmpty("an extension nobody could generate is not worth a round trip");
    }

    [Fact]
    public async Task A_repository_holding_a_solution_is_joined_as_that_solution_without_being_told()
    {
        await ReadyAsync();
        var api = WritableApi().On(HttpMethod.Get, $"/repos/{Repo}/contents/{WorkspaceConfigService.FileName}",
            HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(WorkspaceConfigService.FileName, SavedSolutionConfig()));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        // No sibling: the MCP tools never pass one, and the repository's own
        // settings are what make this an extension of CRONUS rather than a
        // stranger dropped in beside it.
        var delivery = await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking", idFrom: 51000, idTo: 51999), sibling: null, Repo);

        delivery.FolderName.Should().Be("Banking");
        BodyOf(api, "POST", "/pulls").Should().Contain("Add the CRO Banking extension");
        var tree = BodyOf(api, "POST", "/git/trees");
        tree.Should().Contain("Banking/app.json");
        tree.Should().Contain("CRONUSCustomer.code-workspace");
        tree.Should().Contain($"\"path\":\"{WorkspaceConfigService.FileName}\"",
            "the solution's saved settings list the new extension so the next one starts after it");
        tree.Should().NotContain($"Banking/{WorkspaceConfigService.FileName}",
            "the solution's settings at the root already describe it, so it gets no copy of its own");
    }

    [Fact]
    public async Task A_repository_holding_a_solution_refuses_an_id_range_already_in_use()
    {
        await ReadyAsync();
        var api = WritableApi().On(HttpMethod.Get, $"/repos/{Repo}/contents/{WorkspaceConfigService.FileName}",
            HttpStatusCode.OK, FakeGitHubApi.FileContentsJson(WorkspaceConfigService.FileName, SavedSolutionConfig()));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var act = () => service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking", idFrom: 50000, idTo: 50999), sibling: null, Repo);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["IdRangeFrom"].Should().Contain("CRO Core");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"), "nothing is written for a refused plan");
    }

    [Fact]
    public async Task Example_files_are_left_out_of_any_repository()
    {
        var template = TemplateBuilder.Default().WithCoreFolder("src", ("Example.Table.al", "table 50000 Example { }"));
        template.WorkspaceExtensions.Single(e => e.Path == TemplateBuilder.CoreExtensionPath)
            .Folders.Single().Files.Single().IsExample = true;
        await ReadyAsync(template);
        var api = WritableApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        // No saved settings to say what the repository holds, and the caller
        // asked for examples - but whatever is there already may have its own.
        await service.AddExtensionAsync(
            PlanBuilder.ExtensionPlan(extensionName: "Banking") with { IncludeExamples = true }, sibling: null, Repo);

        BodyOf(api, "POST", "/git/trees").Should().NotContain("Example.Table.al");
    }

    /// <summary>The settings a workbench-generated CRONUS solution saved at its root.</summary>
    private static string SavedSolutionConfig() =>
        new WorkspaceConfigService(null!).BuildWorkspace(
            PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Customer", shortName: "CRO", extensionPrefix: "CRO"),
            [new WorkspaceExtensionIdentity(
                WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "CRO Core", "Core", "CRONUS", 50000, 50999)]);

    // --- helpers ------------------------------------------------------------

    private (GitHubExtensionDeliveryService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewGitHubExtensionDeliveryService(ctx, client, access), ctx);
    }

    private static string BodyOf(FakeGitHubApi api, string method, string pathSuffix) =>
        api.Bodies
            .Where(b => b.Call.StartsWith(method, StringComparison.Ordinal) && b.Call.Contains(pathSuffix))
            .Select(b => b.Body)
            .LastOrDefault()
            ?? throw new InvalidOperationException($"No {method} request to a path containing '{pathSuffix}' was made.");

    /// <summary>
    /// A GitHub that answers every call the commit needs: the repository, the
    /// branch it starts from, the blobs, the tree, the commit, the new branch
    /// and the pull request.
    /// </summary>
    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK,
                $"{{\"ref\":\"refs/heads/main\",\"object\":{{\"sha\":\"{BaseSha}\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/{BaseSha}", HttpStatusCode.OK,
                $"{{\"sha\":\"{BaseSha}\",\"tree\":{{\"sha\":\"base-tree-sha\"}}}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound)
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                "{\"number\":7,\"html_url\":\"https://github.com/cronus-dk/solution-a/pull/7\"}");

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
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked, and a template to generate from.</summary>
    private async Task ReadyAsync(RuntimeTemplate? template = null)
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync();
        await SeedTemplateAsync(template ?? TemplateBuilder.Default());
    }

    /// <summary>
    /// Seeds the template and joins it to the organisation's files, so the
    /// generated extension has the same shape the download does - see
    /// <c>StandaloneExtensionGenerationTests</c>, which does the same.
    /// </summary>
    private async Task SeedTemplateAsync(RuntimeTemplate template)
    {
        await using var ctx = _db.NewContext();
        ctx.RuntimeTemplates.Add(template);
        await ctx.SaveChangesAsync();

        var orgFileIds = await ctx.OrganizationFiles
            .Where(f => f.OrganizationId == template.OrganizationId)
            .OrderBy(f => f.Ordering)
            .Select(f => f.Id)
            .ToListAsync();
        for (var i = 0; i < orgFileIds.Count; i++)
        {
            ctx.Set<RuntimeTemplateIncludedFile>().Add(new RuntimeTemplateIncludedFile
            {
                OrganizationId = template.OrganizationId,
                RuntimeTemplateId = template.Id,
                OrganizationFileId = orgFileIds[i],
                Ordering = i,
            });
        }
        if (orgFileIds.Count > 0)
        {
            await ctx.SaveChangesAsync();
        }
    }
}
