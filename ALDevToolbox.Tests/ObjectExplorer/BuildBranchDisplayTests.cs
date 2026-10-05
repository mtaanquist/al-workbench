using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The branch a pipeline's builds show. A build of the default branch names it: the
/// one the build recorded, or for a build made before that was recorded, the
/// repositories' default branch as GitHub last reported it. Only when neither is
/// known does it say "(default branch)".
/// </summary>
public sealed class BuildBranchDisplayTests : IDisposable
{
    private readonly TestDb _db = new();

    public BuildBranchDisplayTests() => _db.OrgContext.IsSiteAdmin = true;

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_build_shows_the_branch_it_built_then_its_recorded_default_then_the_known_default()
    {
        var (projectId, pipelineId, repositoryId) = await SeedAsync();
        var named = await SeedBuildAsync(projectId, pipelineId, branch: "release/29", defaultBranch: null, minutesAgo: 30);
        var recorded = await SeedBuildAsync(projectId, pipelineId, branch: null, defaultBranch: "main", minutesAgo: 20);
        var older = await SeedBuildAsync(projectId, pipelineId, branch: null, defaultBranch: null, minutesAgo: 10);

        var unknown = (await ListAsync(pipelineId)).ToDictionary(b => b.Id, b => b.ShownBranch);
        unknown[named].Should().Be("release/29");
        unknown[recorded].Should().Be("main");
        unknown[older].Should().Be("(default branch)", "nothing says which branch was the default");

        await SeedDefaultBranchHeadAsync(repositoryId, "trunk");

        var known = (await ListAsync(pipelineId)).ToDictionary(b => b.Id, b => b.ShownBranch);
        known[recorded].Should().Be("main", "what the build recorded beats what is known today");
        known[older].Should().Be("trunk");

        await using var ctx = _db.NewContext();
        var row = (await new ArtifactService(ctx, new ProjectAccess(ctx, _db.OrgContext)).ListPipelinesAsync())
            .Single(r => r.Id == pipelineId);
        row.Latest!.ShownBranch.Should().Be("trunk", "the newest build is the older-style one");
    }

    private async Task<List<BuildRow>> ListAsync(int pipelineId)
    {
        await using var ctx = _db.NewContext();
        return await new ArtifactService(ctx, new ProjectAccess(ctx, _db.OrgContext)).ListBuildsAsync(pipelineId);
    }

    private async Task<(int ProjectId, int PipelineId, int RepositoryId)> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus-dk/" + Guid.NewGuid().ToString("N") + ".git", DisplayName = "cronus-apps",
        };
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Default branch",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjectRepositories.Add(repository);
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return (project.Id, pipeline.Id, repository.Id);
    }

    private async Task<int> SeedBuildAsync(int projectId, int pipelineId, string? branch, string? defaultBranch, int minutesAgo)
    {
        await using var ctx = _db.NewContext();
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = ProjectBuildStatus.Ready, Branch = branch, DefaultBranch = defaultBranch,
            StartedAt = at, FinishedAt = at.AddMinutes(3),
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private async Task SeedDefaultBranchHeadAsync(int repositoryId, string branch)
    {
        await using var ctx = _db.NewContext();
        ctx.OeRepositoryBranchHeads.Add(new OeRepositoryBranchHead
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectRepositoryId = repositoryId, Branch = branch,
            HeadSha = new string('a', 40), PushedAt = DateTime.UtcNow, IsDefaultBranch = true, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }
}
