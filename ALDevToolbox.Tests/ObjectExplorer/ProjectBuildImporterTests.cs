using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Drives <see cref="ProjectBuildImporter.StartBuildAsync"/> against the shared
/// <see cref="TestDb"/> fixture: a build is a run of a <see cref="OePipeline"/>, so the
/// pipeline's extension selection is snapshotted onto the <see cref="OeProjectBuild"/>
/// row (and the build is linked to both pipeline and project). The clone/compile path
/// the build then runs is exercised by the staging smoke, not here.
/// </summary>
public sealed class ProjectBuildImporterTests : IDisposable
{
    private readonly TestDb _db = new();

    private const int UserId = 1066;

    public ProjectBuildImporterTests()
    {
        // A build trigger requires owner/Admin rights; act as a SiteAdmin so the
        // access gate passes. A manual build also needs something to clone with,
        // so the acting user has a GitHub build token unless a test removes it.
        _db.OrgContext.IsSiteAdmin = true;
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "builder@cronus.example",
            DisplayName = "Builder",
            PasswordHash = "x",
            Role = UserRole.Admin,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
        NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, "ghp_pasted", clear: false).GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task StartBuildAsync_snapshots_the_pipelines_selection_onto_the_build()
    {
        var selection = new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" };
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", JsonSerializer.Serialize(selection));

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.PipelineId.Should().Be(pipelineId);
        build.ProjectId.Should().Be(projectId);
        build.Status.Should().Be(ProjectBuildStatus.Queued);
        build.RequestedAppIdsJson.Should().NotBeNull();
        JsonSerializer.Deserialize<List<string>>(build.RequestedAppIdsJson!).Should().Equal(selection);
    }

    [Fact]
    public async Task StartBuildAsync_builds_against_the_current_version()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.BcTarget.Should().Be(ProjectBuildTarget.Current);
        build.Trigger.Should().Be(ProjectBuildTrigger.Manual);
    }

    [Theory]
    [InlineData(ProjectBuildTarget.NextMinor)]
    [InlineData(ProjectBuildTarget.NextMajor)]
    public async Task StartPreviewCheckAsync_queues_a_check_build_of_the_pipeline(string target)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var queue = new ReleaseImportQueue();

        var releaseId = await NewImporter(ctx, queue).StartPreviewCheckAsync(pipelineId, target);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.BcTarget.Should().Be(target);
        build.Trigger.Should().Be(ProjectBuildTrigger.PreviewCheck);
        build.PipelineId.Should().Be(pipelineId);
    }

    [Fact]
    public async Task StartPreviewCheckAsync_refuses_the_current_version()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartPreviewCheckAsync(pipelineId, ProjectBuildTarget.Current);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task A_pull_request_build_always_builds_against_the_current_version()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();

        var (_, buildId) = await NewImporter(ctx, new ReleaseImportQueue()).StartPullRequestBuildAsync(
            projectId, repositoryId, "cronus/core", installationId: 1, headSha: new string('a', 40),
            headRef: "feature/vat", pullRequestNumber: 7, checkRunId: null);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId)).BcTarget.Should().Be(ProjectBuildTarget.Current);
    }

    [Fact]
    public async Task StartBuildAsync_stores_null_when_the_pipeline_builds_everything()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "All", requestedAppIdsJson: null);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.RequestedAppIdsJson.Should().BeNull("a null pipeline selection means build every discovered extension");
    }

    [Fact]
    public async Task StartBuildAsync_rejects_a_pipeline_whose_project_has_no_repositories()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx); // no repositories
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task StartBuildAsync_rejects_a_missing_pipeline()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(424242);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    // --- Pull-request builds (#627) ----------------------------------------

    [Fact]
    public async Task StartPullRequestBuildAsync_records_the_pull_request_on_the_build()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId)
            .Select(r => r.Id).SingleAsync();
        var queue = new ReleaseImportQueue();

        var (releaseId, buildId) = await NewImporter(ctx, queue).StartPullRequestBuildAsync(
            projectId: projectId,
            repositoryId: repositoryId,
            repositoryFullName: "cronus-dk/customer-app",
            installationId: 42,
            headSha: "abc123",
            headRef: "feature/vat",
            pullRequestNumber: 7,
            checkRunId: 555);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        build.ReleaseId.Should().Be(releaseId);
        build.Trigger.Should().Be(ProjectBuildTrigger.PullRequest);
        build.PullRequestNumber.Should().Be(7);
        build.HeadSha.Should().Be("abc123");
        build.Branch.Should().Be("feature/vat");
        build.CheckRunId.Should().Be(555);
        build.PipelineId.Should().BeNull("a pull-request build is not a run of a pipeline");
        build.StartedByUserId.Should().BeNull("nobody pressed a button - GitHub asked");
        build.RequestedAppIdsJson.Should().BeNull("with no selection to honour, every extension is compiled");
        build.Status.Should().Be(ProjectBuildStatus.Queued);
    }

    [Fact]
    public async Task StartPullRequestBuildAsync_queues_a_job_carrying_the_head_and_the_installation()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId)
            .Select(r => r.Id).SingleAsync();
        var queue = new ReleaseImportQueue();

        var (releaseId, _) = await NewImporter(ctx, queue).StartPullRequestBuildAsync(
            projectId, repositoryId, "cronus-dk/customer-app", 42, "abc123", "feature/vat", 7, 555);

        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.ReleaseId.Should().Be(releaseId);
        var source = job.Source.Should().BeOfType<ReleaseImportSource.PullRequestBuild>().Subject;
        source.ProjectId.Should().Be(projectId);
        source.RepositoryId.Should().Be(repositoryId);
        source.HeadSha.Should().Be("abc123");
        source.InstallationId.Should().Be(42);
        source.RepositoryFullName.Should().Be("cronus-dk/customer-app");
        source.PullRequestNumber.Should().Be(7);
    }

    [Fact]
    public async Task StartPullRequestBuildAsync_writes_no_durable_job_row()
    {
        // A pull-request build is deliberately not resumed across a restart: by
        // then the head may have moved, and re-running would complete a check run
        // about a commit nobody is looking at any more.
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId)
            .Select(r => r.Id).SingleAsync();

        var (releaseId, _) = await NewImporter(ctx, new ReleaseImportQueue()).StartPullRequestBuildAsync(
            projectId, repositoryId, "cronus-dk/customer-app", 42, "abc123", "feature/vat", 7, null);

        await using var read = _db.NewContext();
        (await read.OeImportJobs.AnyAsync(j => j.ReleaseId == releaseId)).Should().BeFalse();
    }

    [Fact]
    public async Task StartPullRequestBuildAsync_accepts_a_build_with_no_check_run()
    {
        // GitHub can refuse the check run (a missing grant, most often). The build
        // still runs and is still visible in the workbench; it simply reports nowhere.
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId)
            .Select(r => r.Id).SingleAsync();

        var (_, buildId) = await NewImporter(ctx, new ReleaseImportQueue()).StartPullRequestBuildAsync(
            projectId, repositoryId, "cronus-dk/customer-app", 42, "abc123", "feature/vat", 7, checkRunId: null);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId)).CheckRunId.Should().BeNull();
    }

    [Fact]
    public async Task StartPullRequestBuildAsync_refuses_a_solution_that_is_gone()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartPullRequestBuildAsync(
            424242, 1, "cronus-dk/customer-app", 42, "abc123", "feature/vat", 7, null);

        await act.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task StartBuildAsync_refuses_a_person_with_nothing_to_clone_with_before_a_build_exists()
    {
        await using var ctx = _db.NewContext();
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var queue = new ReleaseImportQueue();

        var act = () => NewImporter(ctx, queue).StartBuildAsync(pipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Be(CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.GitHub));
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AnyAsync()).Should().BeFalse("the person is warned instead of getting a build that fails");
        (await read.OeReleases.AnyAsync(r => r.Kind == "project")).Should().BeFalse();
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task StartBuildAsync_refuses_when_one_repository_has_a_provider_the_person_cannot_reach()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.AzureDevOps,
            Url = "https://dev.azure.com/cronus/core/_git/reports",
            DisplayName = "reports",
        });
        await ctx.SaveChangesAsync();
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Be(CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.AzureDevOps));
    }

    [Fact]
    public async Task StartBuildAsync_names_every_repository_host_the_person_cannot_reach()
    {
        await using var ctx = _db.NewContext();
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);
        var projectId = await SeedProjectWithRepoAsync(ctx);
        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.AzureDevOps,
            Url = "https://dev.azure.com/cronus/core/_git/reports",
            DisplayName = "reports",
        });
        await ctx.SaveChangesAsync();
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Contain(CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.GitHub))
            .And.Contain(CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.AzureDevOps));
    }

    [Fact]
    public async Task A_preview_check_is_not_refused_up_front_when_there_is_nothing_to_clone_with()
    {
        // It runs unattended; its build reports the reason instead of pausing the check.
        await using var ctx = _db.NewContext();
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue())
            .StartPreviewCheckAsync(pipelineId, ProjectBuildTarget.NextMinor);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId)).Trigger
            .Should().Be(ProjectBuildTrigger.PreviewCheck);
    }

    [Theory]
    [InlineData(ProjectBuildStatus.Queued)]
    [InlineData(ProjectBuildStatus.Building)]
    public async Task StartBuildAsync_refuses_while_a_build_of_the_pipeline_is_running(string status)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await SetStatusAsync(first, status);
        var queue = new ReleaseImportQueue();

        var act = () => NewImporter(ctx, queue).StartBuildAsync(pipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Contain("already running");
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == pipelineId)).Should().Be(1);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(ProjectBuildStatus.Ready)]
    [InlineData(ProjectBuildStatus.Failed)]
    public async Task StartBuildAsync_starts_again_once_the_previous_build_finished(string status)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await SetStatusAsync(first, status);

        await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == pipelineId)).Should().Be(2);
    }

    [Fact]
    public async Task A_running_build_of_another_pipeline_does_not_block_this_one()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var other = await SeedPipelineAsync(ctx, projectId, "Test", requestedAppIdsJson: null);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(other);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_running_preview_check_does_not_block_a_manual_build()
    {
        // The nightly check runs on its own schedule; the pipeline page and list do
        // not show it as the pipeline's build, so it must not hold up Build either.
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        await NewImporter(ctx, new ReleaseImportQueue()).StartPreviewCheckAsync(pipelineId, ProjectBuildTarget.NextMinor);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_running_manual_build_does_not_block_the_preview_check()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartPreviewCheckAsync(pipelineId, ProjectBuildTarget.NextMajor);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_build_whose_job_was_lost_does_not_lock_the_pipeline()
    {
        // The startup sweep fails a release whose job is gone but leaves the build row
        // queued; trusting the row alone would disable Build for good.
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await using (var write = _db.NewContext())
        {
            var release = await write.OeReleases.SingleAsync(r => r.Id == first);
            release.Status = "failed";
            await write.SaveChangesAsync();
        }

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await act.Should().NotThrowAsync();
    }

    // ── Running an existing build again (#1110) ───────────────────────

    [Fact]
    public async Task A_finished_build_can_be_built_again_when_nothing_else_of_its_pipeline_runs()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await FinishAsync(first);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).EnsureCanRebuildAsync(first, projectId, "Retry");

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_build_is_not_built_again_while_another_build_of_its_pipeline_runs()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await FinishAsync(first);
        await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).EnsureCanRebuildAsync(first, projectId, "Retry");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Retry");
    }

    [Fact]
    public async Task A_build_is_not_built_again_by_someone_who_cannot_manage_its_solution()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await FinishAsync(first);
        await using (var write = _db.NewContext())
        {
            var project = await write.OeProjects.SingleAsync(p => p.Id == projectId);
            project.Visibility = ProjectVisibility.Private;
            write.Users.Add(new User
            {
                Id = 9631, OrganizationId = TestDb.DefaultOrgId, Email = "nils@example.com", PasswordHash = "x",
                DisplayName = "Nils", Role = UserRole.Editor, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
            });
            await write.SaveChangesAsync();
        }
        // An Editor who is not on the private solution's team.
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = 9631;

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).EnsureCanRebuildAsync(first, projectId, "Retry");

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
    }

    [Fact]
    public async Task A_build_is_not_built_again_by_someone_with_nothing_to_clone_with()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await FinishAsync(first);
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).EnsureCanRebuildAsync(first, projectId, "Symbols");

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Symbols"]
            .Should().Be(CloneCredentialResolver.NothingToCloneWith(RepositoryProvider.GitHub));
    }

    private async Task FinishAsync(int releaseId)
    {
        await SetStatusAsync(releaseId, ProjectBuildStatus.Ready);
        await using var write = _db.NewContext();
        var release = await write.OeReleases.SingleAsync(r => r.Id == releaseId);
        release.Status = "ready";
        await write.SaveChangesAsync();
    }

    private async Task SetStatusAsync(int releaseId, string status)
    {
        await using var write = _db.NewContext();
        var build = await write.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.Status = status;
        await write.SaveChangesAsync();
    }

    [Fact]
    public async Task A_manual_build_is_still_stamped_manual()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.Trigger.Should().Be(ProjectBuildTrigger.Manual);
        build.PullRequestNumber.Should().BeNull();
        build.CheckRunId.Should().BeNull();
    }

    // --- Building on push (#1079) ------------------------------------------

    private const string PushedSha = "1111111111111111111111111111111111111111";

    [Fact]
    public async Task A_build_on_push_is_queued_at_the_pushed_commit_as_a_pipeline_build()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var repositoryId = await ctx.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var queue = new ReleaseImportQueue();

        var releaseId = await NewImporter(ctx, queue).StartPushBuildAsync(pipelineId, repositoryId, PushedSha);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        build.Trigger.Should().Be(ProjectBuildTrigger.Push);
        build.PipelineId.Should().Be(pipelineId);
        build.HeadSha.Should().Be(PushedSha);
        build.HeadRepositoryId.Should().Be(repositoryId);
        build.StartedByUserId.Should().Be(UserId);
        build.BcTarget.Should().Be(ProjectBuildTarget.Current);
        queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.JobRowId.Should().NotBe(0, "a build on push is resumed after a restart like any pipeline build");
    }

    [Fact]
    public async Task A_build_on_push_waits_behind_a_running_build_instead_of_being_refused()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var first = await NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);
        await SetStatusAsync(first, ProjectBuildStatus.Building);

        await NewImporter(ctx, new ReleaseImportQueue()).StartPushBuildAsync(pipelineId, 1, PushedSha);
        await NewImporter(ctx, new ReleaseImportQueue()).StartPushBuildAsync(pipelineId, 1, PushedSha.Replace('1', '2'));

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == pipelineId)).Should().Be(3);
    }

    [Fact]
    public async Task A_waiting_build_on_push_holds_up_a_manual_build()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        await NewImporter(ctx, new ReleaseImportQueue()).StartPushBuildAsync(pipelineId, 1, PushedSha);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartBuildAsync(pipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Contain("already running");
    }

    [Fact]
    public async Task Past_the_waiting_limit_a_push_moves_the_newest_waiting_build_onto_its_commit()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var released = new List<int>();
        for (var i = 0; i < ProjectBuildImporter.MaxWaitingPushBuilds; i++)
        {
            released.Add(await NewImporter(ctx, new ReleaseImportQueue())
                .StartPushBuildAsync(pipelineId, 1, new string((char)('a' + i), 40)));
        }
        var queue = new ReleaseImportQueue();

        var releaseId = await NewImporter(ctx, queue).StartPushBuildAsync(pipelineId, 7, PushedSha);

        releaseId.Should().Be(released[^1]);
        queue.Reader.TryRead(out _).Should().BeFalse("no new build is queued past the limit");
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.CountAsync(b => b.PipelineId == pipelineId)).Should().Be(ProjectBuildImporter.MaxWaitingPushBuilds);
        var newest = await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == releaseId);
        newest.HeadSha.Should().Be(PushedSha);
        newest.HeadRepositoryId.Should().Be(7);
        (await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == released[0])).HeadSha.Should().Be(new string('a', 40));
    }

    [Fact]
    public async Task A_build_that_has_started_is_never_moved_onto_a_later_push()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);
        var released = new List<int>();
        for (var i = 0; i < ProjectBuildImporter.MaxWaitingPushBuilds; i++)
        {
            released.Add(await NewImporter(ctx, new ReleaseImportQueue())
                .StartPushBuildAsync(pipelineId, 1, new string((char)('a' + i), 40)));
        }
        await SetStatusAsync(released[0], ProjectBuildStatus.Building);

        var releaseId = await NewImporter(ctx, new ReleaseImportQueue()).StartPushBuildAsync(pipelineId, 1, PushedSha);

        released.Should().NotContain(releaseId, "only four are still waiting, so this push gets its own build");
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.ReleaseId == released[0])).HeadSha.Should().Be(new string('a', 40));
    }

    [Fact]
    public async Task A_build_on_push_is_refused_up_front_when_its_person_has_nothing_to_clone_with()
    {
        await using var ctx = _db.NewContext();
        await NewTokens(ctx).SaveTokenAsync(RepositoryProvider.GitHub, null, clear: true);
        var projectId = await SeedProjectWithRepoAsync(ctx);
        var pipelineId = await SeedPipelineAsync(ctx, projectId, "Production", requestedAppIdsJson: null);

        var act = () => NewImporter(ctx, new ReleaseImportQueue()).StartPushBuildAsync(pipelineId, 1, PushedSha);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Pipeline"]
            .Should().Be(ProjectBuildImporter.NothingToCloneWithOnPush(RepositoryProvider.GitHub));
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AnyAsync()).Should().BeFalse();
    }

    private ProjectBuildImporter NewImporter(Data.AppDbContext ctx, ReleaseImportQueue queue)
    {
        var translations = new TranslationImportService(
            ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(
            ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations,
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);
        var persistedJobs = new PersistedImportJobs(ctx, TimeProvider.System);
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var credentials = new CloneCredentialResolver(
            NewTokens(ctx),
            _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, new FakeGitHubApi())),
            _db.OrgContext, NullLogger<CloneCredentialResolver>.Instance);
        return new ProjectBuildImporter(
            importer, queue, persistedJobs, ctx, _db.OrgContext, access, credentials, TimeProvider.System,
            NullLogger<ProjectBuildImporter>.Instance);
    }

    private UserRepositoryTokenService NewTokens(Data.AppDbContext ctx) => new(
        ctx, _db.OrgContext, NullLogger<UserRepositoryTokenService>.Instance, _db.DataProtectionProvider);

    private static async Task<int> SeedProjectAsync(Data.AppDbContext ctx)
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

    private static async Task<int> SeedProjectWithRepoAsync(Data.AppDbContext ctx)
    {
        var projectId = await SeedProjectAsync(ctx);
        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus/core",
            DisplayName = "core",
        });
        await ctx.SaveChangesAsync();
        return projectId;
    }

    private static async Task<int> SeedPipelineAsync(Data.AppDbContext ctx, int projectId, string name, string? requestedAppIdsJson)
    {
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            RequestedAppIdsJson = requestedAppIdsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }
}
