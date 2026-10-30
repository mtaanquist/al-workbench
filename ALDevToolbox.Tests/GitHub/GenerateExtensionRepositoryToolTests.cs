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
/// MCP parity for "add to repository" (issue #623).
///
/// <para>The point being pinned is not that the option exists but that it goes
/// through the same service the New Extension page uses, so an agent cannot
/// reach a repository the picker would have refused - see "Keeping MCP parity
/// with the web UI" in PROJECT.md.</para>
/// </summary>
public sealed class GenerateExtensionRepositoryToolTests : IDisposable
{
    private const int UserId = 901;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/solution-a";
    private const string BaseSha = "base-commit-sha";

    private readonly TestDb _db = new();

    private static readonly IOptions<McpOptions> Options =
        Microsoft.Extensions.Options.Options.Create(new McpOptions());

    public GenerateExtensionRepositoryToolTests()
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
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateExtensionAsync(PlanInput());

        result.AddedToRepository.Should().BeNull();
        result.DownloadPath.Should().StartWith(GeneratedDownloadStore.PathPrefix, "without a repository the link is how the user gets the ZIP");
    }

    [Fact]
    public async Task With_the_option_it_reports_the_pull_request_it_opened()
    {
        await ReadyAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var result = await tools.GenerateExtensionAsync(PlanInput(), addToRepository: Repo);

        result.AddedToRepository.Should().NotBeNull();
        result.AddedToRepository!.PullRequestNumber.Should().Be(7);
        result.AddedToRepository.RepositoryFullName.Should().Be(Repo);
        result.AddedToRepository.Branch.Should().Be("aldt/add-MyCustomFeature");
        result.AddedToRepository.BaseBranch.Should().Be("main");
        // The ZIP alongside it is the one that went into the pull request, not
        // a second generation with different extension GUIDs.
        result.DownloadPath.Should().BeNull("the repository holds the files");
    }

    [Fact]
    public async Task It_cannot_reach_a_repository_outside_the_connected_organisation()
    {
        await ReadyAsync();
        var api = WritableApi()
            // The agent's user can see this one on GitHub; it is simply not in
            // the organisation the workbench organisation connected, so the web
            // UI would never offer it - and neither may the tool.
            .On(HttpMethod.Get, "/repos/someone-else/theirs", HttpStatusCode.OK,
                FakeGitHubApi.RepositoryJson("someone-else/theirs"));
        var (tools, ctx) = NewTools(api);
        await using var _ = ctx;

        var act = () => tools.GenerateExtensionAsync(PlanInput(), addToRepository: "someone-else/theirs");

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Validation failed");
        api.Calls.Should().NotContain(c => c.Contains("git/blobs"));
    }

    [Fact]
    public async Task An_unlinked_caller_is_told_to_connect_their_github_account()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await SeedTemplateAsync();
        var (tools, ctx) = NewTools(WritableApi());
        await using var _ = ctx;

        var act = () => tools.GenerateExtensionAsync(PlanInput(), addToRepository: Repo);

        (await act.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Contain("Connect your own GitHub account");
    }

    // --- helpers ------------------------------------------------------------

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
            new GeneratedDownloadStore(TimeProvider.System),
            _db.OrgContext,
            Options);
        return (tools, ctx);
    }

    private static StandaloneExtensionPlanInput PlanInput() => new(
        TemplateKey: "runtime-test",
        ExtensionName: "My Custom Feature",
        Brief: "Standalone brief.",
        Description: "Standalone description.",
        ApplicationVersion: "24.0.0.0",
        RuntimeVersion: "15",
        IdRangeFrom: 70000,
        IdRangeTo: 70999,
        IncludeExamples: true,
        Publisher: "CRONUS",
        Dependencies: null);

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
