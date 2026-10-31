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
            // A preview check from before #1140, which still indexed its objects.
            preview = await SeedProjectReleaseAsync(seed, "CRONUS on BC 29.0", bcVersion: "29.0", sourceFileCount: 12);
            previewBuild = await SeedBuildAsync(seed, projectId, main, preview, ProjectBuildStatus.Ready, now,
                bcTarget: ProjectBuildTarget.NextMajor);
            // A preview check that recorded compile results only: nothing to compare.
            var checkOnly = await SeedProjectReleaseAsync(seed, "CRONUS on BC 29.1", bcVersion: "29.1");
            await SeedBuildAsync(seed, projectId, main, checkOnly, ProjectBuildStatus.Ready, now,
                bcTarget: ProjectBuildTarget.NextMinor);

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

    // ── The file viewer's "Compare with release..." picker (#1570) ──────

    private ReleaseComparisonService NewComparison(AppDbContext ctx) =>
        new(ctx, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleaseComparisonService>.Instance);

    [Fact]
    public async Task File_in_a_build_is_offered_the_solutions_other_builds_with_the_file_named_and_newest_first()
    {
        // Every build release shares "CRONUS on BC 28.2", so the picker used to
        // list each one under that label, sorted by it - no way to tell which
        // was the previous build.
        long anchorFile;
        int olderSamePipeline, newerSamePipeline, olderBuild, newerBuild;
        var now = DateTime.UtcNow;
        var appId = Guid.NewGuid();
        await using (var seed = _db.NewContext())
        {
            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var main = await SeedPipelineAsync(seed, projectId, "main");

            var self = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, projectId, main, self, ProjectBuildStatus.Ready, now.AddDays(-2));
            anchorFile = await SeedAppFileAsync(seed, self, appId, "src/Sales.al");

            olderSamePipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            olderBuild = await SeedBuildAsync(seed, projectId, main, olderSamePipeline, ProjectBuildStatus.Ready, now.AddDays(-3));
            await SeedAppFileAsync(seed, olderSamePipeline, appId, "src/Sales.al");

            newerSamePipeline = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            newerBuild = await SeedBuildAsync(seed, projectId, main, newerSamePipeline, ProjectBuildStatus.Ready, now.AddDays(-1));
            await SeedAppFileAsync(seed, newerSamePipeline, appId, "src/Sales.al");

            // A build of the same app without this file: nothing to compare against.
            var withoutFile = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, projectId, main, withoutFile, ProjectBuildStatus.Ready, now);
            await SeedAppFileAsync(seed, withoutFile, appId, "src/Other.al");

            // Another solution's build carrying the same app and path.
            var otherProjectId = await SeedProjectAsync(seed, "Other customer");
            var otherProject = await SeedProjectReleaseAsync(seed, "Other customer on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, otherProjectId, pipelineId: null, otherProject, ProjectBuildStatus.Ready, now);
            await SeedAppFileAsync(seed, otherProject, appId, "src/Sales.al");
        }

        await using var read = _db.NewContext();
        var targets = await NewComparison(read).GetCompareTargetsAsync(anchorFile);

        targets.Select(t => t.ReleaseId).Should().Equal(newerSamePipeline, olderSamePipeline);
        targets.Select(t => t.ReleaseLabel).Should().Equal(
            $"Build #{newerBuild} of main on BC 28.2",
            $"Build #{olderBuild} of main on BC 28.2");
    }

    [Fact]
    public async Task File_in_an_imported_release_is_not_offered_builds()
    {
        // Builds stay out of the global pickers; only the build's own Solution
        // offers them.
        long anchorFile;
        int otherImport;
        var appId = Guid.NewGuid();
        await using (var seed = _db.NewContext())
        {
            var anchorRelease = await SeedReleaseAsync(seed, "CRONUS 1.0", "third_party");
            anchorFile = await SeedAppFileAsync(seed, anchorRelease, appId, "src/Sales.al");
            otherImport = await SeedReleaseAsync(seed, "CRONUS 1.1", "third_party");
            await SeedAppFileAsync(seed, otherImport, appId, "src/Sales.al");

            var projectId = await SeedProjectAsync(seed, "CRONUS");
            var build = await SeedProjectReleaseAsync(seed, "CRONUS on BC 28.2", bcVersion: "28.2");
            await SeedBuildAsync(seed, projectId, pipelineId: null, build, ProjectBuildStatus.Ready, DateTime.UtcNow);
            await SeedAppFileAsync(seed, build, appId, "src/Sales.al");
        }

        await using var read = _db.NewContext();
        var targets = await NewComparison(read).GetCompareTargetsAsync(anchorFile);

        targets.Should().ContainSingle().Which.Should().Match<CompareTargetOption>(t =>
            t.ReleaseId == otherImport && t.ReleaseLabel == "CRONUS 1.1");
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
        AppDbContext ctx, string label, string status = "ready", bool deleted = false, string? bcVersion = null,
        int sourceFileCount = 0)
    {
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "project",
            Status = status,
            BcVersion = bcVersion,
            SourceFileCount = sourceFileCount,
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

    private static async Task<int> SeedReleaseAsync(AppDbContext ctx, string label, string kind)
    {
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = kind,
            Status = "ready",
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    /// <summary>One module of <paramref name="appId"/> in the release, holding one file at <paramref name="path"/>.</summary>
    private static async Task<long> SeedAppFileAsync(AppDbContext ctx, int releaseId, Guid appId, string path)
    {
        const string hash = "build-compare-hash";
        if (!ctx.OeFileContents.Any(c => c.ContentHash == hash))
        {
            ctx.OeFileContents.Add(new OeFileContent { ContentHash = hash, Content = "// x", ContentLength = 4, LineCount = 1 });
        }
        var module = new OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = releaseId,
            AppId = appId,
            Name = "CRONUS Sales",
            Publisher = "CRONUS",
            Version = "1.0.0.0",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.OeModules.Add(module);
        await ctx.SaveChangesAsync();
        var file = new OeModuleFile
        {
            OrganizationId = TestDb.DefaultOrgId,
            ModuleId = module.Id,
            Path = path,
            ContentHash = hash,
            LineCount = 1,
        };
        ctx.OeModuleFiles.Add(file);
        await ctx.SaveChangesAsync();
        return file.Id;
    }
}
