using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The compare picker on a pipeline build's release page (#1075). The global
/// release list leaves project builds out, so the same-kind filter the other
/// release kinds use found nothing; a build is offered the other ready builds
/// of its own solution instead, its own pipeline's first.
/// </summary>
public sealed class BuildCompareCandidatesTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ObjectExplorerService NewQuery(AppDbContext ctx) =>
        new(ctx, new ReferenceQueryService(ctx, new ProjectAccess(ctx, _db.OrgContext), _db.OrgContext, NullLogger<ReferenceQueryService>.Instance),
            new ProjectAccess(ctx, _db.OrgContext),
            NullLogger<ObjectExplorerService>.Instance);

    [Fact]
    public async Task Offers_the_other_ready_builds_of_the_solution_own_pipeline_first_newest_first()
    {
        int self, olderSamePipeline, newerSamePipeline, otherPipeline, noPipeline;
        int failedId, deletedId, otherProject;
        int olderBuild, newerBuild, otherPipelineBuild, noPipelineBuild;
        var now = DateTime.UtcNow;
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var main = await SeedPipelineAsync(seed, projectId, "main");
            var test = await SeedPipelineAsync(seed, projectId, "test");

            self = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, projectId, main, self, ProjectBuildStatus.Ready, now.AddDays(-2));
            olderSamePipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.1", bcVersion: "28.1");
            olderBuild = await SeedBuildAsync(seed, projectId, main, olderSamePipeline, ProjectBuildStatus.Ready, now.AddDays(-3));
            newerSamePipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            newerBuild = await SeedBuildAsync(seed, projectId, main, newerSamePipeline, ProjectBuildStatus.Ready, now.AddDays(-1));
            // Newer than everything on main, but another pipeline's, so it comes after them.
            otherPipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            otherPipelineBuild = await SeedBuildAsync(seed, projectId, test, otherPipeline, ProjectBuildStatus.Ready, now);
            noPipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2");
            noPipelineBuild = await SeedBuildAsync(seed, projectId, pipelineId: null, noPipeline, ProjectBuildStatus.Ready, now.AddHours(-1));

            failedId = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", status: "failed");
            await SeedBuildAsync(seed, projectId, main, failedId, ProjectBuildStatus.Failed, now);
            deletedId = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", deleted: true);
            await SeedBuildAsync(seed, projectId, main, deletedId, ProjectBuildStatus.Ready, now);

            var otherProjectId = await SeedProjectAsync(seed, "Other customer");
            otherProject = await SeedProjectReleaseAsync(seed, "Other customer on BC 28.2");
            await SeedBuildAsync(seed, otherProjectId, pipelineId: null, otherProject, ProjectBuildStatus.Ready, now);
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListBuildCompareCandidatesAsync(self);

        rows.Select(r => r.Id).Should().Equal(newerSamePipeline, olderSamePipeline, otherPipeline, noPipeline);
        rows.Select(r => r.Label).Should().Equal(
            $"Build #{newerBuild} of main on BC 28.2",
            $"Build #{olderBuild} of main on BC 28.1",
            $"Build #{otherPipelineBuild} of test on BC 28.2",
            $"Build #{noPipelineBuild}");
        rows.Should().OnlyContain(r => r.Kind == "project" && r.ProjectName == "CRONUS");
    }

    [Fact]
    public async Task Leaves_out_deleted_pipelines_and_unfinished_releases_and_names_preview_builds()
    {
        int self, preview, previewBuild;
        var now = DateTime.UtcNow;
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var main = await SeedPipelineAsync(seed, projectId, "main");
            var gone = await SeedPipelineAsync(seed, projectId, "old", deleted: true);

            self = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, projectId, main, self, ProjectBuildStatus.Ready, now.AddDays(-1));
            preview = await SeedProjectReleaseAsync(seed, "CRONUS on BC 29.0", bcVersion: "29.0");
            previewBuild = await SeedBuildAsync(seed, projectId, main, preview, ProjectBuildStatus.Ready, now,
                bcTarget: ProjectBuildTarget.NextMajor);

            var deletedPipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2");
            await SeedBuildAsync(seed, projectId, gone, deletedPipeline, ProjectBuildStatus.Ready, now);
            // The build reads ready but its release is still being ingested.
            var ingesting = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", status: "ingesting");
            await SeedBuildAsync(seed, projectId, main, ingesting, ProjectBuildStatus.Ready, now);
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListBuildCompareCandidatesAsync(self);

        rows.Should().ContainSingle().Which.Should().Match<ReleaseListItem>(r =>
            r.Id == preview && r.Label == $"Next major preview build #{previewBuild} of main on BC 29.0");
    }

    [Fact]
    public async Task Offers_only_the_most_recent_builds()
    {
        int self;
        var now = DateTime.UtcNow;
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            self = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2");
            await SeedBuildAsync(seed, projectId, pipelineId: null, self, ProjectBuildStatus.Ready, now);
            for (var i = 1; i <= ObjectExplorerService.BuildCompareCandidateCap + 5; i++)
            {
                var release = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2");
                await SeedBuildAsync(seed, projectId, pipelineId: null, release, ProjectBuildStatus.Ready, now.AddMinutes(-i));
            }
        }

        await using var read = _db.NewContext();
        var rows = await NewQuery(read).ListBuildCompareCandidatesAsync(self);

        rows.Should().HaveCount(ObjectExplorerService.BuildCompareCandidateCap);
    }

    [Fact]
    public async Task A_release_no_build_produced_has_no_candidates()
    {
        int release;
        await using (var seed = _db.NewContext())
        {
            release = await SeedProjectReleaseAsync(seed, "CRONUS imported by hand");
        }

        await using var read = _db.NewContext();
        (await NewQuery(read).ListBuildCompareCandidatesAsync(release)).Should().BeEmpty();
    }

    // ── Seed helpers ─────────────────────────────────────────────────────

    private static async Task<int> SeedProjectAsync(AppDbContext ctx, string name)
    {
        var project = new OeProject
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

    private static async Task<int> SeedPipelineAsync(AppDbContext ctx, int projectId, string name, bool deleted = false)
    {
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedProjectReleaseAsync(
        AppDbContext ctx, string label, string status = "ready", bool deleted = false, string? bcVersion = null)
    {
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "project",
            Status = status,
            BcVersion = bcVersion,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            DeletedAt = deleted ? DateTime.UtcNow : null,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private static async Task<int> SeedBuildAsync(
        AppDbContext ctx, int projectId, int? pipelineId, int releaseId, string status, DateTime startedAt,
        string bcTarget = ProjectBuildTarget.Current)
    {
        var build = new OeProjectBuild
        {
            BcTarget = bcTarget,
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipelineId,
            ReleaseId = releaseId,
            Status = status,
            StartedAt = startedAt,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }
}
