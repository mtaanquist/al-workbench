using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.Mcp.Dtos;
using ALDevToolbox.Services.Mcp.Tools;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// MCP parity for "create repository" (issue #622).
///
/// <para>The point being pinned is not that the option exists but that it goes
/// through the same service the New Workspace page uses, so an agent gets the
/// same answers a person would - see "Keeping MCP parity with the web UI" in
/// PROJECT.md. In particular the tool takes a repository <em>name</em> and never
/// an owner: the organisation is the one this organisation connected, so there
/// is nothing for an agent to point somewhere else.</para>
/// </summary>
public sealed class GenerateWorkspaceRepositoryToolTests : IDisposable
{
    private const int UserId = 902;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "CRONUS-Customer";
    private const string Repo = $"{OrgLogin}/{RepoName}";

    private readonly TestDb _db = new();

    private static readonly IOptions<McpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new McpOptions());

    public GenerateWorkspaceRepositoryToolTests()
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
    public async Task Without_the_option_it_still_just_hands_back_the_zip()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var result = await tools.GenerateWorkspaceAsync(PlanInput());

        result.CreatedRepository.Should().BeNull();
        result.ContentBase64.Should().NotBeEmpty();
        api.Calls.Should().BeEmpty("nothing is asked of GitHub unless a repository was asked for");
    }

    [Fact]
    public async Task With_the_option_it_reports_the_repository_it_created()
    {
        await ReadyAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateWorkspaceAsync(PlanInput(), createRepository: RepoName);

        result.CreatedRepository.Should().NotBeNull();
        result.CreatedRepository!.RepositoryFullName.Should().Be(Repo);
        result.CreatedRepository.HtmlUrl.Should().Be($"https://github.com/{Repo}");
        result.CreatedRepository.CloneUrl.Should().Be($"https://github.com/{Repo}.git");
        result.CreatedRepository.DefaultBranch.Should().Be("main");
        result.CreatedRepository.IsPrivate.Should().BeTrue();
        result.CreatedRepository.FileCount.Should().BeGreaterThan(0);
        // The ZIP alongside it is the one whose files are in the repository, not
        // a second generation with different extension GUIDs.
        result.ContentBase64.Should().NotBeEmpty();
    }

    [Fact]
    public async Task It_can_only_create_in_the_organisation_the_workbench_organisation_connected()
    {
        await ReadyAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        // An agent naming an owner does not get one: the whole string is the
        // repository name, and a name with a slash in it is not one GitHub
        // would keep - so it is refused rather than quietly re-aimed.
        var act = () => tools.GenerateWorkspaceAsync(
            PlanInput(), createRepository: "someone-else/theirs");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Validation failed");
        api.Calls.Should().NotContain(c => c.Contains("someone-else"));
        api.Calls.Should().NotContain(c => c.Contains("/repos"));
    }

    [Fact]
    public async Task A_caller_outside_the_github_organisation_is_refused()
    {
        await ReadyAsync();
        var api = WritableApi()
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NotFound);
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.GenerateWorkspaceAsync(PlanInput(), createRepository: RepoName);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain(OrgLogin);
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Fact]
    public async Task An_unlinked_caller_is_told_to_connect_their_github_account()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await SeedTemplateAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var act = () => tools.GenerateWorkspaceAsync(PlanInput(), createRepository: RepoName);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Connect your own GitHub account");
    }

    [Fact]
    public async Task With_no_solution_named_it_reports_the_one_it_created()
    {
        await ReadyAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateWorkspaceAsync(PlanInput(), createRepository: RepoName);

        // An agent that created a repository has to be able to say where the
        // customer now lives, the same as the page does (#759).
        result.CreatedRepository!.SolutionId.Should().NotBeNull();
        result.CreatedRepository.SolutionName.Should().Be("CRONUS Customer");
        result.CreatedRepository.SolutionCreated.Should().BeTrue();
        result.CreatedRepository.SolutionWarning.Should().BeNull();
    }

    [Fact]
    public async Task A_named_solution_is_the_one_the_repository_lands_on()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("CRONUS Denmark");
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateWorkspaceAsync(
            PlanInput(), createRepository: RepoName, solutionId: solutionId);

        result.CreatedRepository!.SolutionId.Should().Be(solutionId);
        result.CreatedRepository.SolutionName.Should().Be("CRONUS Denmark");
        result.CreatedRepository.SolutionCreated.Should().BeFalse();

        await using var read = _db.NewContext();
        var repo = await read.OeProjectRepositories.AsNoTracking()
            .SingleAsync(r => r.ProjectId == solutionId);
        repo.Url.Should().Be($"https://github.com/{Repo}.git");
        repo.DisplayName.Should().Be(RepoName);
    }

    [Fact]
    public async Task With_solutions_switched_off_it_creates_the_repository_and_no_solution()
    {
        await ReadyAsync();
        await DisableSolutionsForOrgAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateWorkspaceAsync(PlanInput(), createRepository: RepoName);

        result.CreatedRepository!.RepositoryFullName.Should().Be(Repo);
        result.CreatedRepository.SolutionId.Should().BeNull();
        result.CreatedRepository.SolutionName.Should().BeNull();
        result.CreatedRepository.SolutionCreated.Should().BeFalse();
        result.CreatedRepository.SolutionWarning.Should().BeNull();

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Naming_a_solution_while_they_are_switched_off_is_a_validation_error()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("CRONUS Denmark");
        await DisableSolutionsForOrgAsync();
        var api = WritableApi();
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        // An agent is told why rather than having the argument ignored - and
        // an MCP request learns the organisation's switched-off tools from the
        // organisation row, since its principal carries no such claim.
        var act = () => tools.GenerateWorkspaceAsync(
            PlanInput(), createRepository: RepoName, solutionId: solutionId);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Validation failed").And.Contain("switched off");
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    /// <summary>
    /// Switches Solutions off for the acting organisation, the way an org Admin
    /// does on the Administration tools page.
    /// </summary>
    private async Task DisableSolutionsForOrgAsync()
    {
        await using var ctx = _db.NewContext();
        var org = await ctx.Organizations.SingleAsync(o => o.Id == TestDb.DefaultOrgId);
        org.DisabledTools = new List<string> { nameof(ALDevToolbox.Domain.Tools.ToolKey.Projects) };
        await ctx.SaveChangesAsync();
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>One solution this caller owns, for the tool to name.</summary>
    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = new ALDevToolbox.Domain.Entities.ObjectExplorer.OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            CreatedByUserId = UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private (WorkspaceTools Tools, ALDevToolbox.Data.AppDbContext Context) NewTools(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        var tools = new WorkspaceTools(
            new TemplateService(ctx, NullLogger<TemplateService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx)),
            new ModuleService(ctx, NullLogger<ModuleService>.Instance, _db.OrgContext, new FolderTreeHydrator(ctx)),
            new CatalogService(ctx, NullLogger<CatalogService>.Instance, _db.OrgContext),
            _db.NewGenerationService(ctx),
            _db.NewGitHubExtensionDeliveryService(ctx, client, access),
            _db.NewGitHubWorkspaceRepositoryService(ctx, client, access),
            Options);
        return (tools, ctx);
    }

    private static ProjectPlanInput PlanInput() => new(
        TemplateKey: "runtime-test",
        WorkspaceName: "CRONUS Customer",
        ExtensionPrefix: "CRONUS",
        Brief: "Test brief.",
        Description: "Test description.",
        ApplicationVersion: "24.0.0.0",
        RuntimeVersion: "15",
        CoreIdRangeFrom: 90000,
        CoreIdRangeTo: 90999,
        IncludeExamples: true,
        SelectedExtensionPaths: null,
        SelectedModuleKeys: null);

    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created,
                FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Put, $"/repos/{Repo}/contents/", HttpStatusCode.Created, FakeGitHubApi.FileWriteJson())
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("blob-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree-sha"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit-sha"))
            .On(HttpMethod.Patch, $"/repos/{Repo}/git/refs/heads/", HttpStatusCode.OK, FakeGitHubApi.ShaJson("new-commit-sha"))
            // GitHub refuses the Git Data API until a repository has a commit,
            // which is what the Contents write above is for.
            .EmptyRepository(Repo);

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
        await using (var ctx = _db.NewContext())
        {
            await _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api)).LinkAsync("the-code");
        }
        await SeedTemplateAsync();
    }

    private async Task SeedTemplateAsync()
    {
        var template = TemplateBuilder.Default();
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
