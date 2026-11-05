using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Generation;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The Repositories list's workspace files column (issue #1719, part 2): which of the
/// files the workbench manages a repository is missing or has changed, and the pull
/// request that brings them back in step.
/// </summary>
public sealed class RepositoryFilesServiceTests : IDisposable
{
    private const int UserId = 6340;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = "cronus-dk/payment-import";
    private const string StandardPath = ".github/workflows/build.yml";
    private const string StandardContent = "name: Build\non: push\n";

    private readonly TestDb _db = new();

    public RepositoryFilesServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
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
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    // ── Comparing ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_check_records_the_files_a_repository_is_missing_or_has_changed()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var expected = await ExpectedAsync();
        var gitignore = expected.Single(f => f.Path == PlatformOrganizationFiles.GitignorePath);

        var api = RepositoryApi()
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{StandardPath}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(StandardPath, StandardContent.Replace("\n", "\r\n")))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/{PlatformOrganizationFiles.GitignorePath}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(".gitignore", gitignore.Content + "\nmy-local-stuff/\n"));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.CheckRepositoryAsync(Repo)).Should().Be(RepositoryCheckOutcome.Checked);

        await using var read = _db.NewContext();
        var check = await read.GitHubRepositoryFileChecks.AsNoTracking().SingleAsync();
        check.Repository.Should().Be(Repo);
        check.ChangedPaths.Should().Equal(PlatformOrganizationFiles.GitignorePath);
        check.MissingPaths.Should().Contain(PlatformOrganizationFiles.RulesetPath);
        check.MissingPaths.Should().NotContain(StandardPath, "a different line ending is not a different file");
        check.MissingPaths.Concat(check.ChangedPaths).Should().NotContain(
            [PlatformOrganizationFiles.ReadmePath, PlatformOrganizationFiles.AppJsonPath],
            "files with placeholders are starting points the team edits");
        check.PullRequestNumber.Should().BeNull();
    }

    [Fact]
    public async Task A_repository_without_the_saved_workspace_settings_is_compared_on_its_standards_only()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var api = RepositoryApi(withSavedSettings: false);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.CheckRepositoryAsync(Repo);

        await using var read = _db.NewContext();
        var check = await read.GitHubRepositoryFileChecks.AsNoTracking().SingleAsync();
        check.MissingPaths.Should().Equal(StandardPath);
        check.ChangedPaths.Should().BeEmpty();
    }

    [Fact]
    public async Task A_repository_of_a_solution_the_viewer_cannot_see_is_not_checked_for_them()
    {
        await ReadyAsync();
        await SeedSolutionAsync(ProjectVisibility.Private);
        var api = RepositoryApi();
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.CheckRepositoryAsync(Repo)).Should().Be(RepositoryCheckOutcome.NotTracked);
        api.Calls.Should().NotContain(c => c.Contains($"/repos/{Repo}"));
    }

    [Fact]
    public void Workspace_settings_the_team_added_are_kept_and_only_what_differs_is_written()
    {
        var expected = new ExpectedRepositoryFile("cronus.code-workspace",
            """{"folders":[],"settings":{"al.enableCodeAnalysis":true,"editor":{"tabSize":4}}}""",
            IsWorkspaceSettings: true);

        RepositoryFilesService.Updated(expected, """
            {
              // the team's own
              "folders": [{"path": "Core"}],
              "settings": {"al.enableCodeAnalysis": true, "editor": {"tabSize": 4, "wordWrap": "on"}, "mine": 1,},
            }
            """).Should().BeNull("every value the organisation sets is already there");

        var updated = RepositoryFilesService.Updated(expected,
            """{"folders":[{"path":"Core"}],"settings":{"editor":{"tabSize":2,"wordWrap":"on"},"mine":1}}""");

        updated.Should().NotBeNull();
        var json = System.Text.Json.Nodes.JsonNode.Parse(updated!)!;
        json["folders"]![0]!["path"]!.GetValue<string>().Should().Be("Core");
        json["settings"]!["al.enableCodeAnalysis"]!.GetValue<bool>().Should().BeTrue();
        json["settings"]!["editor"]!["tabSize"]!.GetValue<int>().Should().Be(4);
        json["settings"]!["editor"]!["wordWrap"]!.GetValue<string>().Should().Be("on");
        json["settings"]!["mine"]!.GetValue<int>().Should().Be(1);
    }

    // ── The pull request ─────────────────────────────────────────────────

    [Fact]
    public async Task The_pull_request_writes_only_the_files_that_differ_and_is_recorded_on_the_row()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var expected = await ExpectedAsync();
        var api = WritableApi(expected, except: StandardPath);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;
        await service.CheckRepositoryAsync(Repo);

        var result = await service.OpenPullRequestAsync(Repo);

        result.Refusal.Should().BeNull();
        result.IsNewPullRequest.Should().BeTrue();
        result.FileCount.Should().Be(1);
        result.PullRequest!.Number.Should().Be(31);
        api.Bodies.Count(b => b.Call.Contains("/git/blobs")).Should().Be(1);
        api.Bodies.Single(b => b.Call.StartsWith("POST") && b.Call.EndsWith("/git/refs")).Body
            .Should().Contain($"refs/heads/{RepositoryFilesService.Branch}");
        api.Bodies.Single(b => b.Call.StartsWith("POST") && b.Call.EndsWith("/pulls")).Body
            .Should().Contain("build.yml");

        await using var read = _db.NewContext();
        (await read.GitHubRepositoryFileChecks.AsNoTracking().SingleAsync()).PullRequestNumber.Should().Be(31);
    }

    [Fact]
    public async Task Nothing_is_opened_when_every_file_already_matches()
    {
        await ReadyAsync();
        await SeedSolutionAsync();
        var expected = await ExpectedAsync();
        var api = WritableApi(expected, except: null);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = await service.OpenPullRequestAsync(Repo);

        result.PullRequest.Should().BeNull();
        result.IsUpToDate.Should().BeTrue();
        api.Calls.Should().NotContain(c => c.Contains("/git/blobs"));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private (RepositoryFilesService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        var service = new RepositoryFilesService(
            ctx,
            client,
            _db.NewGitHubConnectionService(ctx, access),
            _db.NewDependencyDriftService(ctx, client, access),
            new WorkspaceConfigService(ctx),
            _db.NewGenerationService(ctx),
            _db.OrgContext,
            TimeProvider.System,
            NullLogger<RepositoryFilesService>.Instance);
        return (service, ctx);
    }

    /// <summary>What the organisation expects in <see cref="Repo"/>, worked out the way a check does.</summary>
    private async Task<List<ExpectedRepositoryFile>> ExpectedAsync()
    {
        var (service, ctx) = NewService(RepositoryApi());
        await using var _ = ctx;
        return await service.ExpectedFilesAsync(
            "token", new GitHubRepositorySummary(Repo, OrgLogin, "payment-import", "main", true, null, $"https://github.com/{Repo}", $"https://github.com/{Repo}.git"), default);
    }

    /// <summary>
    /// The installation can read <see cref="Repo"/>, which holds the saved settings of a
    /// CRONUS workspace and nothing else: every other file answers 404.
    /// </summary>
    private static FakeGitHubApi RepositoryApi(bool withSavedSettings = true)
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Get, $"/repos/{Repo}/contents/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
            .On(HttpMethod.Get, $"/repos/{Repo}/pulls", HttpStatusCode.OK, "[]");
        if (withSavedSettings)
        {
            api.On(HttpMethod.Get, $"/repos/{Repo}/contents/{WorkspaceConfigService.FileName}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(WorkspaceConfigService.FileName, SavedSettings()));
        }
        return api;
    }

    /// <summary>
    /// A repository that holds every expected file as the organisation has it, except
    /// <paramref name="except"/>, and that the person can commit to.
    /// </summary>
    private static FakeGitHubApi WritableApi(IReadOnlyList<ExpectedRepositoryFile> expected, string? except)
    {
        var api = RepositoryApi();
        foreach (var file in expected.Where(f => f.Path != except))
        {
            api.On(HttpMethod.Get, $"/repos/{Repo}/contents/{file.Path}", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson(file.Path, file.Content));
        }
        return api
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/{RepositoryFilesService.Branch}", HttpStatusCode.NotFound,
                "{\"message\":\"Not Found\"}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/ref/heads/main", HttpStatusCode.OK, "{\"object\":{\"sha\":\"main-head\"}}")
            .On(HttpMethod.Get, $"/repos/{Repo}/git/commits/", HttpStatusCode.OK, "{\"sha\":\"main-head\",\"tree\":{\"sha\":\"base-tree\"}}")
            .On(HttpMethod.Post, $"/repos/{Repo}/git/blobs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-blob"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/trees", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-tree"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/commits", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-commit"))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, FakeGitHubApi.ShaJson("new-ref"))
            .On(HttpMethod.Post, $"/repos/{Repo}/pulls", HttpStatusCode.Created,
                $"{{\"number\":31,\"html_url\":\"https://github.com/{Repo}/pull/31\"}}");
    }

    private static string SavedSettings() =>
        new WorkspaceConfigService(null!).BuildWorkspace(
            PlanBuilder.WorkspacePlan(workspaceName: "CRONUS Customer", shortName: "CRO", extensionPrefix: "CRO"),
            [new WorkspaceExtensionIdentity(
                WorkspaceExtensionIdentity.CoreKind, null, Guid.NewGuid(), "CRO Core", "Core", "CRONUS", 50000, 50999)]);

    private async Task SeedSolutionAsync(ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        ctx.OeProjects.Add(new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS A/S payments",
            DefaultArtifactCountry = "dk",
            Visibility = visibility,
            CreatedByUserId = visibility == ProjectVisibility.Private ? null : UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories =
            [
                new OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = RepositoryProvider.GitHub,
                    Url = $"https://github.com/{Repo}",
                    DisplayName = "payment-import",
                    AccessConfirmedAt = now,
                },
            ],
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Deployment configured, organisation connected, user linked, a template and a standard.</summary>
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
            ctx.GitHubRepositoryStandardFiles.Add(new GitHubRepositoryStandardFile
            {
                OrganizationId = TestDb.DefaultOrgId,
                Path = StandardPath,
                Content = StandardContent,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();

            var template = TemplateBuilder.Default();
            ctx.RuntimeTemplates.Add(template);
            await ctx.SaveChangesAsync();
            var fileIds = await ctx.OrganizationFiles.OrderBy(f => f.Ordering).Select(f => f.Id).ToListAsync();
            for (var i = 0; i < fileIds.Count; i++)
            {
                ctx.RuntimeTemplateIncludedFiles.Add(new RuntimeTemplateIncludedFile
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    RuntimeTemplateId = template.Id,
                    OrganizationFileId = fileIds[i],
                    Ordering = i,
                });
            }
            await ctx.SaveChangesAsync();
        }

        var link = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var linkCtx = _db.NewContext();
        await _db.NewGitHubAccessService(linkCtx, _db.NewGitHubAppClient(linkCtx, link)).LinkAsync("the-code");
    }
}
