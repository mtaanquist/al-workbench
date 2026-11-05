using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Templates;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The rows behind the Repositories list (issue #1719): one per repository on a
/// solution the viewer may see, with its last build, what the customer runs and what
/// its app.json asks for - all from the database, so the page never waits on GitHub.
/// </summary>
public sealed class RepositoryListServiceTests : IDisposable
{
    private const int UserId = 6330;
    private const string RepoA = "cronus-dk/payment-import";
    private const string RepoB = "cronus-dk/warehouse-ext";

    private readonly TestDb _db = new();

    public RepositoryListServiceTests()
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

    [Fact]
    public async Task A_repository_behind_its_customer_says_what_it_is_on_and_what_would_move()
    {
        var (projectId, repositoryId) = await SeedSolutionAsync(RepoA, "CRONUS A/S payments", production: "28.2.45123.0");
        await SeedCheckAsync(RepoA, "27.0");
        await SeedDriftAsync(RepoA, "application", "27.0.0.0", "28.2.0.0");
        var buildId = await SeedBuildAsync(projectId, repositoryId, ProjectBuildStatus.Failed);

        var row = (await ListAsync()).Should().ContainSingle().Subject;

        row.Repository.Should().Be(RepoA);
        row.HtmlUrl.Should().Be($"https://github.com/{RepoA}");
        row.SolutionName.Should().Be("CRONUS A/S payments");
        row.BcVersion.Should().Be("28.2");
        row.Environment!.Name.Should().Be("Production");
        row.AppJsonApplication.Should().Be("27.0");
        row.HasAppJsonUpdate.Should().BeTrue();
        row.Changes.Should().ContainSingle(c => c.Current == "27.0.0.0" && c.Proposed == "28.2.0.0");
        row.LastBuild!.Id.Should().Be(buildId);
        row.LastBuild.Status.Should().Be(ProjectBuildStatus.Failed);
        row.PullRequest.Should().BeNull();
    }

    [Fact]
    public async Task An_up_to_date_repository_still_shows_its_version_with_nothing_to_update()
    {
        await SeedSolutionAsync(RepoB, "CRONUS A/S warehouse", production: "28.2.45123.0");
        await SeedCheckAsync(RepoB, "28.2");

        var row = (await ListAsync()).Should().ContainSingle().Subject;

        row.AppJsonApplication.Should().Be("28.2");
        row.HasAppJsonUpdate.Should().BeFalse();
        row.LastBuild.Should().BeNull();
    }

    [Fact]
    public async Task An_update_pull_request_already_open_for_the_version_is_linked()
    {
        await SeedSolutionAsync(RepoA, "CRONUS A/S payments", production: "28.2.45123.0");
        await SeedDriftAsync(RepoA, "application", "27.0.0.0", "28.2.0.0");
        await using (var ctx = _db.NewContext())
        {
            ctx.GitHubUpdatePullRequests.Add(new GitHubUpdatePullRequest
            {
                OrganizationId = TestDb.DefaultOrgId,
                Repository = RepoA,
                Version = "28.2",
                PullRequestNumber = 12,
                HtmlUrl = $"https://github.com/{RepoA}/pull/12",
                OpenedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var row = (await ListAsync()).Should().ContainSingle().Subject;

        row.PullRequest.Should().Be(new RepositoryListPullRequest(12, $"https://github.com/{RepoA}/pull/12"));
    }

    [Fact]
    public async Task A_solution_with_no_environment_has_no_bc_version_to_compare_with()
    {
        await SeedSolutionAsync(RepoA, "CRONUS A/S payments", production: null);

        var row = (await ListAsync()).Should().ContainSingle().Subject;

        row.Environment.Should().BeNull();
        row.BcVersion.Should().BeNull();
    }

    [Fact]
    public async Task A_private_solutions_repository_is_not_listed_to_someone_who_is_not_on_it()
    {
        await SeedSolutionAsync(RepoA, "CRONUS A/S payments", production: "28.2.45123.0", visibility: ProjectVisibility.Private);
        await SeedSolutionAsync(RepoB, "CRONUS A/S warehouse", production: "28.2.45123.0");

        (await ListAsync()).Select(r => r.Repository).Should().Equal(RepoB);
    }

    [Fact]
    public async Task A_pull_request_build_is_not_the_last_build_of_the_branch()
    {
        var (projectId, repositoryId) = await SeedSolutionAsync(RepoA, "CRONUS A/S payments", production: null);
        var branchBuild = await SeedBuildAsync(projectId, repositoryId, ProjectBuildStatus.Ready, startedAt: DateTime.UtcNow.AddHours(-2));
        await SeedBuildAsync(projectId, repositoryId, ProjectBuildStatus.Failed, trigger: ProjectBuildTrigger.PullRequest);

        var row = (await ListAsync()).Should().ContainSingle().Subject;

        row.LastBuild!.Id.Should().Be(branchBuild);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private async Task<List<RepositoryListRow>> ListAsync()
    {
        await using var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, new FakeGitHubApi());
        var access = _db.NewGitHubAccessService(ctx, client);
        var drift = _db.NewDependencyDriftService(ctx, client, access);
        var service = new RepositoryListService(
            ctx,
            new ALDevToolbox.Services.ObjectExplorer.ProjectAccess(ctx, _db.OrgContext),
            drift,
            new CatalogService(ctx, NullLogger<CatalogService>.Instance, _db.OrgContext),
            _db.OrgContext);
        return await service.GetAsync();
    }

    private async Task<(int ProjectId, int RepositoryId)> SeedSolutionAsync(
        string fullName, string name, string? production, ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            Provider = RepositoryProvider.GitHub,
            Url = $"https://github.com/{fullName}.git",
            DisplayName = fullName.Split('/')[^1],
            AccessConfirmedAt = now,
        };
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            Visibility = visibility,
            CreatedByUserId = visibility == ProjectVisibility.Private ? null : UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories = [repository],
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        if (production is not null)
        {
            ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = project.Id,
                Name = "Production",
                Type = "Production",
                Version = production,
                FetchedAt = now,
            });
            await ctx.SaveChangesAsync();
        }
        return (project.Id, repository.Id);
    }

    private async Task SeedCheckAsync(string fullName, string application)
    {
        await using var ctx = _db.NewContext();
        ctx.GitHubRepositoryChecks.Add(new GitHubRepositoryCheck
        {
            OrganizationId = TestDb.DefaultOrgId,
            Repository = fullName.ToLowerInvariant(),
            Application = application,
            CheckedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedDriftAsync(string fullName, string field, string current, string proposed)
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
        await ctx.SaveChangesAsync();
        ctx.GitHubRepositoryDrift.Add(new GitHubRepositoryDrift
        {
            OrganizationId = TestDb.DefaultOrgId,
            Repository = fullName,
            Path = "app.json",
            Field = field,
            Current = current,
            Proposed = proposed,
            ReleaseId = release.Id,
            DetectedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task<int> SeedBuildAsync(
        int projectId, int repositoryId, string status, string trigger = ProjectBuildTrigger.Push, DateTime? startedAt = null)
    {
        await using var ctx = _db.NewContext();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Status = status,
            Trigger = trigger,
            StartedAt = startedAt ?? DateTime.UtcNow,
            FinishedAt = status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed ? DateTime.UtcNow : null,
        };
        build.RepoCommits.Add(new OeProjectBuildRepoCommit
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectRepositoryId = repositoryId,
            RepoUrl = "https://github.com/x",
            RepoDisplayName = "x",
            CommitHash = "abc123",
        });
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }
}
