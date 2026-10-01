using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// CRUD + validation for <see cref="PipelineService"/> against the shared
/// <see cref="TestDb"/> fixture: name required and unique per project (but free to
/// repeat across projects), the selection serialised to JSON (null = build all),
/// update, and soft-delete. See <c>.design/artifacts.md</c>.
/// </summary>
public sealed class PipelineServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public PipelineServiceTests()
    {
        // Manage rights come from the parent project's owner; act as SiteAdmin so the
        // access gate passes without seeding a user.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreatePipelineAsync_persists_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var selection = new[] { "11111111-1111-1111-1111-111111111111" };

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", selection));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Production");
        pipeline.ProjectId.Should().Be(projectId);
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal(selection);
    }

    [Fact]
    public async Task CreatePipelineAsync_stores_null_selection_for_build_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "All", SelectedAppIds: null));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).RequestedAppIdsJson.Should().BeNull();
    }

    [Fact]
    public async Task CreatePipelineAsync_requires_a_name()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "  ", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectId, "production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreatePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        await svc.CreatePipelineAsync(new PipelineInput(projectA, "Production", null));

        var act = () => svc.CreatePipelineAsync(new PipelineInput(projectB, "Production", null));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreatePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(424242, "Production", null));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task UpdatePipelineAsync_changes_name_and_selection()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Test", new[] { "aaa" }));

        await using var read = _db.NewContext();
        var pipeline = await read.OePipelines.SingleAsync(p => p.Id == id);
        pipeline.Name.Should().Be("Test");
        JsonSerializer.Deserialize<List<string>>(pipeline.RequestedAppIdsJson!).Should().Equal("aaa");
    }

    [Fact]
    public async Task SoftDeletePipelineAsync_hides_the_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await svc.SoftDeletePipelineAsync(id);

        await using var read = _db.NewContext();
        (await NewService(read).GetPipelineAsync(id)).Should().BeNull();
        (await read.OePipelines.IgnoreQueryFilters().SingleAsync(p => p.Id == id)).DeletedAt.Should().NotBeNull();
    }

    // --- Build against (#994) ----------------------------------------------

    [Fact]
    public async Task A_new_pipeline_builds_against_the_current_version_unless_told_otherwise()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var id = await NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).BcTarget.Should().Be(ProjectBuildTarget.Current);
    }

    [Fact]
    public async Task A_pipeline_keeps_and_changes_the_version_it_builds_against()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);

        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Next major check", null, BcTarget: ProjectBuildTarget.NextMajor));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).BcTarget.Should().Be(ProjectBuildTarget.NextMajor);
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Next major check", null, BcTarget: ProjectBuildTarget.NextMinor));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).BcTarget.Should().Be(ProjectBuildTarget.NextMinor);
        }
    }

    [Theory]
    [InlineData("next")]
    [InlineData("NextMajor")]
    [InlineData("version_mode")]
    public async Task A_target_the_tool_does_not_know_is_rejected_against_the_field(string target)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null, BcTarget: target));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("BcTarget");
    }

    [Fact]
    public async Task A_pipeline_a_deployment_pipeline_draws_from_cannot_switch_to_a_preview_version()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null));
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Production", Type = "Production",
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        ctx.OeReleasePipelines.Add(new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Production deploy",
            BuildPipelineId = id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var act = () => svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, BcTarget: ProjectBuildTarget.NextMajor));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("BcTarget");
        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == id)).BcTarget.Should().Be(ProjectBuildTarget.Current);
    }

    [Fact]
    public async Task A_preview_pipeline_cannot_also_publish_github_releases()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repo = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus/core", DisplayName = "core",
        };
        ctx.OeProjectRepositories.Add(repo);
        await ctx.SaveChangesAsync();

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(
            projectId, "Next major check", null, GithubReleaseRepositoryId: repo.Id, BcTarget: ProjectBuildTarget.NextMajor));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("GithubReleaseRepositoryId");
    }

    // --- The watched branch (#963) ------------------------------------------

    [Fact]
    public async Task A_pipeline_keeps_the_branch_it_was_given_and_a_blank_one_means_the_default()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);

        var named = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Release", null, Branch: " release/25.0 "));
        var blank = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Main", null, Branch: "   "));

        await using var read = _db.NewContext();
        (await read.OePipelines.SingleAsync(p => p.Id == named)).Branch.Should().Be("release/25.0");
        (await read.OePipelines.SingleAsync(p => p.Id == blank)).Branch.Should().BeNull(
            "blank means each repository's default branch, stored as no branch at all");
    }

    [Fact]
    public async Task Editing_a_pipeline_changes_and_clears_its_branch()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var svc = NewService(ctx);
        var id = await svc.CreatePipelineAsync(new PipelineInput(projectId, "Production", null, Branch: "main"));

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, Branch: "develop"));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).Branch.Should().Be("develop");
        }

        await svc.UpdatePipelineAsync(id, new PipelineInput(projectId, "Production", null, Branch: null));
        await using (var read = _db.NewContext())
        {
            (await read.OePipelines.SingleAsync(p => p.Id == id)).Branch.Should().BeNull();
        }
    }

    [Theory]
    [InlineData("-main")]
    [InlineData("feature..vat")]
    [InlineData("feature/vat/")]
    [InlineData("/feature")]
    [InlineData("feature//vat")]
    [InlineData("feature/.hidden")]
    [InlineData("main.lock")]
    [InlineData("main.")]
    [InlineData("feature vat")]
    [InlineData("feature~1")]
    [InlineData("feature:vat")]
    [InlineData("feat^")]
    [InlineData("feat*")]
    [InlineData("feat?")]
    [InlineData("feat[1]")]
    [InlineData("feat\\vat")]
    [InlineData("feat@{1}")]
    public async Task A_branch_name_git_would_refuse_is_rejected_against_the_branch_field(string branch)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);

        var act = () => NewService(ctx).CreatePipelineAsync(new PipelineInput(projectId, "Production", null, Branch: branch));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Branch");
    }

    [Theory]
    [InlineData("main", true)]
    [InlineData("release/25.0", true)]
    [InlineData("feature/CRONUS-123_vat-fix", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("-main", false)]
    [InlineData(".main", false)]
    [InlineData("feature..vat", false)]
    [InlineData("feature/vat/", false)]
    [InlineData("/feature", false)]
    [InlineData("feature//vat", false)]
    [InlineData("feature/.hidden", false)]
    [InlineData("main.lock", false)]
    [InlineData("main.lock/x", false)]
    [InlineData("main.", false)]
    [InlineData("feature vat", false)]
    [InlineData("feature~1", false)]
    [InlineData("feat\\vat", false)]
    [InlineData("æøå", false)]
    public void The_browser_pattern_and_the_server_rule_agree(string branch, bool valid)
    {
        // The pattern= on the editor field mirrors the server rule; a browser
        // anchors the pattern at both ends, so the test does too.
        ALDevToolbox.Services.ObjectExplorer.Projects.GitBranchName.IsValid(branch).Should().Be(valid);
        System.Text.RegularExpressions.Regex
            .IsMatch(branch, "^(?:" + ALDevToolbox.Services.ObjectExplorer.Projects.GitBranchName.HtmlPattern + ")$")
            .Should().Be(valid);
    }

    private PipelineService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<PipelineService>.Instance);

    private static async Task<int> SeedProjectAsync(AppDbContext ctx)
    {
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }
}
