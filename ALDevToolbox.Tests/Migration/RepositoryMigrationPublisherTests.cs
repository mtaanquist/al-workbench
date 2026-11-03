using System.Diagnostics;
using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// The last step of Migrate repositories (#1701): the workspace a preview built
/// is pushed into a new GitHub repository as one commit, the customer becomes a
/// solution, and every run is recorded. A solution of the same name is refused,
/// unless it came from an earlier migration whose repository is gone and the
/// Admin chose to replace it.
/// </summary>
public sealed class RepositoryMigrationPublisherTests : IDisposable
{
    private const int UserId = 812;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoName = "cronus-as";
    private const string Repo = $"{OrgLogin}/{RepoName}";

    private readonly TestDb _db = new();
    private readonly MigrationTempDir _temp = new();

    public RepositoryMigrationPublisherTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "admin@cronus.example",
            DisplayName = "Ad Min",
            PasswordHash = "x",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose()
    {
        _temp.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task The_workspace_is_pushed_as_one_commit_naming_its_sources_and_the_run_is_recorded()
    {
        if (!GitAvailable()) return;
        await ReadyAsync();
        var (workspace, bare) = Workspace();
        var api = WritableApi();

        MigrationOutcome outcome;
        await using (var ctx = _db.NewContext())
        {
            var publisher = NewPublisher(ctx, api, bare);
            outcome = await publisher.PublishAsync(Job(), Request(), workspace, Commits, CancellationToken.None);
        }

        outcome.RepositoryFullName.Should().Be(Repo);
        outcome.FileCount.Should().Be(2);
        outcome.SolutionId.Should().NotBeNull();
        api.Calls.Should().Contain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
        api.Calls.Should().NotContain(c => c.Contains("/git/blobs"), "the files go up in one push, not one call each");

        Git(bare, "ls-tree", "-r", "--name-only", "main").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().BeEquivalentTo("cronus-base/app.json", "CRONUS.code-workspace");
        var message = Git(bare, "log", "-1", "--format=%B", "main");
        message.Should().Contain("cronus-base at abc1234").And.Contain("https://dev.azure.com/cronus/Sales");

        await using var check = _db.NewContext();
        var row = await check.RepositoryMigrations.AsNoTracking().SingleAsync();
        row.Succeeded.Should().BeTrue();
        row.RepositoryFullName.Should().Be(Repo);
        row.ProjectId.Should().Be(outcome.SolutionId);
        row.CreatedByUserId.Should().Be(UserId);
        row.Sources.Should().Be("cronus-base@abc1234");
    }

    [Fact]
    public async Task A_push_that_fails_says_to_delete_the_half_made_repository_and_is_recorded_as_failed()
    {
        if (!GitAvailable()) return;
        await ReadyAsync();
        var (workspace, _) = Workspace();

        await using (var ctx = _db.NewContext())
        {
            var publisher = NewPublisher(ctx, WritableApi(), Path.Combine(_temp.Path, "no-such-remote"));
            var act = () => publisher.PublishAsync(Job(), Request(), workspace, Commits, CancellationToken.None);
            var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which;
            error.Errors[GitHubWorkspaceRepositoryService.RepositoryField]
                .Should().Contain("could not be pushed").And.Contain($"Delete {Repo} on GitHub");
        }

        await using var check = _db.NewContext();
        var row = await check.RepositoryMigrations.AsNoTracking().SingleAsync();
        row.Succeeded.Should().BeFalse();
        row.Message.Should().Contain("could not be pushed");
    }

    [Fact]
    public async Task A_solution_of_the_same_name_that_no_migration_made_is_refused_before_anything_is_created()
    {
        await ReadyAsync();
        await SeedSolutionAsync("CRONUS A/S");
        var api = WritableApi();

        await using var ctx = _db.NewContext();
        var publisher = NewPublisher(ctx, api, _temp.Path);
        (await publisher.FindSolutionClashAsync("cronus a/s", CancellationToken.None))!.CanReplace.Should().BeFalse();

        var act = () => publisher.CheckAsync("CRONUS A/S", Request(), CancellationToken.None);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors
            .Should().ContainKey(RepositoryMigrationService.SolutionField);
        api.Calls.Should().NotContain(c => c.Contains($"/orgs/{OrgLogin}/repos"));
    }

    [Fact]
    public async Task A_solution_from_a_migration_whose_repository_still_exists_cannot_be_replaced()
    {
        await ReadyAsync();
        var solutionId = await SeedSolutionAsync("CRONUS A/S");
        await SeedHistoryAsync(solutionId, $"{OrgLogin}/old-cronus");
        var api = WritableApi().On(HttpMethod.Get, $"/repos/{OrgLogin}/old-cronus", HttpStatusCode.OK,
            FakeGitHubApi.RepositoryJson($"{OrgLogin}/old-cronus"));

        await using var ctx = _db.NewContext();
        var clash = await NewPublisher(ctx, api, _temp.Path).FindSolutionClashAsync("CRONUS A/S", CancellationToken.None);

        clash!.Id.Should().Be(solutionId);
        clash.CanReplace.Should().BeFalse();
    }

    [Fact]
    public async Task Replacing_a_solution_whose_migrated_repository_is_gone_deletes_it_and_creates_a_new_one()
    {
        if (!GitAvailable()) return;
        await ReadyAsync();
        var oldId = await SeedSolutionAsync("CRONUS A/S");
        await SeedHistoryAsync(oldId, $"{OrgLogin}/old-cronus");
        var api = WritableApi().On(HttpMethod.Get, $"/repos/{OrgLogin}/old-cronus", HttpStatusCode.NotFound);
        var (workspace, bare) = Workspace();

        MigrationOutcome outcome;
        await using (var ctx = _db.NewContext())
        {
            var publisher = NewPublisher(ctx, api, bare);
            var clash = await publisher.FindSolutionClashAsync("CRONUS A/S", CancellationToken.None);
            clash!.CanReplace.Should().BeTrue();

            // Without the Admin's say-so it is still refused.
            var refused = () => publisher.CheckAsync("CRONUS A/S", Request(), CancellationToken.None);
            (await refused.Should().ThrowAsync<PlanValidationException>()).Which.Errors[RepositoryMigrationService.SolutionField]
                .Should().Contain("Replace the existing solution");

            outcome = await publisher.PublishAsync(Job(), Request() with { ReplaceSolutionId = oldId }, workspace, Commits, CancellationToken.None);
        }

        outcome.SolutionId.Should().NotBe(oldId);
        await using var check = _db.NewContext();
        (await check.OeProjects.AsNoTracking().SingleAsync(p => p.Id == oldId)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_page_is_told_up_front_why_a_repository_cannot_be_created()
    {
        await using var ctx = _db.NewContext();
        var readiness = await NewPublisher(ctx, WritableApi(), _temp.Path).CheckGitHubAsync(CancellationToken.None);
        readiness.Blocker.Should().Contain("GitHub is not set up");
    }

    [Fact]
    public async Task The_page_is_told_up_front_which_github_organisation_the_repository_goes_to()
    {
        await ReadyAsync();
        await using var ctx = _db.NewContext();
        var readiness = await NewPublisher(ctx, WritableApi(), _temp.Path).CheckGitHubAsync(CancellationToken.None);
        readiness.Should().Be(new MigrationGitHubReadiness(OrgLogin, null));
    }

    // --- helpers ------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Commits = new Dictionary<string, string> { ["cronus-base"] = "abc1234" };

    private static MigrationCreateRequest Request() =>
        new(RepoName, IsPrivate: true, AddPipelines: false, ReplaceSolutionId: null, Commits);

    private static MigrationPreviewJob Job() => new(
        Guid.NewGuid(),
        new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, UserId, false, false),
        new AzureDevOpsProject("cronus", "Sales"),
        [new AzureDevOpsRepository("r1", "cronus-base", "https://dev.azure.com/cronus/Sales/_git/cronus-base",
            "https://dev.azure.com/cronus/Sales/_git/cronus-base", "main", 1, false)],
        MigrationTestServices.Target());

    /// <summary>A built workspace folder, and an empty bare repository the push lands in.</summary>
    private (string Workspace, string Bare) Workspace()
    {
        var workspace = _temp.Sub("workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "cronus-base"));
        File.WriteAllText(Path.Combine(workspace, "cronus-base", "app.json"), "{}");
        File.WriteAllText(Path.Combine(workspace, "CRONUS.code-workspace"), "{}");
        var bare = _temp.Sub("bare.git");
        Git(bare, "init", "--quiet", "--bare");
        return (workspace, bare);
    }

    private RepositoryMigrationPublisher NewPublisher(AppDbContext ctx, FakeGitHubApi api, string pushTo)
    {
        var client = _db.NewGitHubAppClient(ctx, api);
        var publisher = _db.NewRepositoryMigrationPublisher(ctx, client, _db.NewGitHubAccessService(ctx, client));
        publisher.PushUrl = _ => pushTo;
        return publisher;
    }

    private static FakeGitHubApi WritableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, $"/orgs/{OrgLogin}/members/", HttpStatusCode.NoContent)
            .On(HttpMethod.Post, $"/orgs/{OrgLogin}/repos", HttpStatusCode.Created, FakeGitHubApi.RepositoryJson(Repo))
            .On(HttpMethod.Post, $"/repos/{Repo}/git/refs", HttpStatusCode.Created, """{"ref":"refs/heads/x"}""");

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
        await _db.LinkGitHubAccountAsync(UserId);
        await MigrationTestServices.SeedTemplateAsync(_db);
    }

    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            Visibility = ProjectVisibility.Public,
            DefaultArtifactCountry = "dk",
            CreatedByUserId = UserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task SeedHistoryAsync(int solutionId, string repository)
    {
        await using var ctx = _db.NewContext();
        ctx.RepositoryMigrations.Add(new RepositoryMigration
        {
            OrganizationId = TestDb.DefaultOrgId,
            CreatedByUserId = UserId,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            SourceProject = "cronus/Sales",
            SourceProjectUrl = "https://dev.azure.com/cronus/Sales",
            Sources = "cronus-base@abc1234",
            CustomerName = "CRONUS A/S",
            TemplateKey = MigrationTestServices.TemplateKey,
            RepositoryFullName = repository,
            RepositoryUrl = "https://github.com/" + repository,
            ProjectId = solutionId,
            Succeeded = true,
        });
        await ctx.SaveChangesAsync();
    }

    private static string Git(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
