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
/// CRUD + validation for <see cref="ReleasePipelineService"/> against the shared
/// <see cref="TestDb"/> fixture: name required and unique per project (free to repeat
/// across projects), the source build pipeline and target environment must belong to
/// the same project, the environment must have a company picked, the version /
/// schema-sync modes are validated, plus update, soft-delete, and the list
/// projection. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class ReleasePipelineServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public ReleasePipelineServiceTests()
    {
        // Manage rights come from the parent project's owner; act as SiteAdmin so the
        // access gate passes without seeding a user.
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateReleasePipelineAsync_persists_all_fields_with_mode_defaults()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId,
            BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Contoso → Production");
        rp.ProjectId.Should().Be(projectId);
        rp.BuildPipelineId.Should().Be(buildId);
        rp.ProjectEnvironmentId.Should().Be(envId);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMinorUpdate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_defaults_blank_modes_to_current_version_and_add()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "", ""));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_requires_a_name()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "  ", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_allows_the_same_name_in_a_different_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var buildA = await SeedBuildPipelineAsync(ctx, projectA);
        var envA = await SeedEnvironmentAsync(ctx, projectA);
        var buildB = await SeedBuildPipelineAsync(ctx, projectB);
        var envB = await SeedEnvironmentAsync(ctx, projectB);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectA, "Production", buildA, envA, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectB, "Production", buildB, envB, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(424242, "Rel", 1, 1, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Project");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_build_pipeline_from_another_project()
    {
        await using var ctx = _db.NewContext();
        var projectA = await SeedProjectAsync(ctx);
        var projectB = await SeedProjectAsync(ctx);
        var otherBuild = await SeedBuildPipelineAsync(ctx, projectB);
        var envId = await SeedEnvironmentAsync(ctx, projectA);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectA, "Rel", otherBuild, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("BuildPipelineId");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_that_is_no_longer_in_business_central()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Retired", missing: true);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("Retired", "the refusal names the environment the consultant picked");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_that_cannot_take_an_install()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production", status: "Upgrading");

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("Upgrading", "the same wording the delivery gate uses, just earlier");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_accepts_an_environment_with_no_status_yet()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        // Rows fetched before the status was captured have none; that must not block.
        var envId = await SeedEnvironmentAsync(ctx, projectId, status: null);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        id.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_unknown_version_mode()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, "Whenever", BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("DeploymentSchedule");
    }

    [Fact]
    public async Task A_pipeline_can_install_in_its_environments_delivery_window_when_it_has_one()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId,
            windowStart: new TimeOnly(22, 0), windowEnd: new TimeOnly(4, 0));

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.OurDeliveryWindow, BcSyncMode.Add));

        await using var read = _db.NewContext();
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == id))
            .DeploymentSchedule.Should().Be(BcDeploymentSchedule.OurDeliveryWindow);
    }

    [Fact]
    public async Task The_delivery_window_is_refused_for_an_environment_without_one()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production");

        var act = () => NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.OurDeliveryWindow, BcSyncMode.Add));

        var errors = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors.Should().ContainKey("DeploymentSchedule");
        errors["DeploymentSchedule"].Should().Contain("'Production' has no delivery window");
    }

    [Fact]
    public async Task Business_Centrals_own_update_window_is_still_not_pickable()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId,
            windowStart: new TimeOnly(22, 0), windowEnd: new TimeOnly(4, 0));

        var act = () => NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.UpdateWindow, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("DeploymentSchedule");
    }

    [Fact]
    public async Task UpdateReleasePipelineAsync_changes_target_and_modes()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var prodEnv = await SeedEnvironmentAsync(ctx, projectId);
        var sandboxEnv = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, prodEnv, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Sandbox", buildId, sandboxEnv, BcDeploymentSchedule.NextMajorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Sandbox");
        rp.ProjectEnvironmentId.Should().Be(sandboxEnv);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMajorUpdate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    [Fact]
    public async Task SoftDeleteReleasePipelineAsync_hides_the_release_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        await svc.SoftDeleteReleasePipelineAsync(id);

        await using var read = _db.NewContext();
        (await NewService(read).GetReleasePipelineAsync(id)).Should().BeNull();
        (await read.OeReleasePipelines.IgnoreQueryFilters().SingleAsync(r => r.Id == id)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task ListReleasePipelinesAsync_resolves_source_and_target_names()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, name: "Nightly");
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production");
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Contoso → Production", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));

        var rows = await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.BuildPipelineName.Should().Be("Nightly");
        row.EnvironmentName.Should().Be("Production");
        row.EnvironmentMissing.Should().BeFalse();
    }

    [Theory]
    [InlineData("Active", null)]
    [InlineData("Upgrading", null)]
    [InlineData("SoftDeleted", "being removed")]
    [InlineData("Failed", "failed in Business Central")]
    public async Task A_pipeline_says_so_when_its_environment_will_never_take_a_release(string status, string? problem)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, name: "Nightly");
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "JLE");
        await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS App -> JLE", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        await ctx.OeProjectEnvironments.Where(e => e.Id == envId)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, status));

        var row = (await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId)).Single();

        row.EnvironmentProblem.Should().Be(problem,
            "a busy environment passes on its own; one that is being removed or has failed does not");
    }

    // ── The Releases list's delivery summary (#935) ──────────────────────────

    [Fact]
    public async Task The_overview_sums_up_each_pipelines_newest_live_and_next_release()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildPipelineId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "UAT");
        var svc = NewService(ctx);
        var busy = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS to UAT", buildPipelineId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        var idle = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS to UAT again", buildPipelineId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        await ctx.OeProjectEnvironments.Where(e => e.Id == envId).ExecuteUpdateAsync(u => u
            .SetProperty(e => e.BcNextUpdateVersion, "28.4")
            .SetProperty(e => e.BcNextUpdateType, "Major")
            .SetProperty(e => e.BcNextUpdateDate, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc)));
        var buildId = await SeedBuildAsync(ctx, projectId, buildPipelineId);

        var t0 = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        // A deployed release that took six minutes, then a later one that failed on its second app.
        await SeedDeliveryAsync(ctx, projectId, busy, buildId, ProjectDeliveryStatus.Deployed,
            startedAt: t0, finishedAt: t0.AddMinutes(6), apps: [ProjectDeliveryResultStatus.Completed]);
        var failed = await SeedDeliveryAsync(ctx, projectId, busy, buildId, ProjectDeliveryStatus.Failed,
            startedAt: t0.AddHours(1), finishedAt: t0.AddHours(1).AddMinutes(2),
            apps: [ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed, ProjectDeliveryResultStatus.Skipped],
            appNames: ["CRONUS Base", "CRONUS Sales", "CRONUS Reports"]);
        // One installing its second of three apps right now.
        var running = await SeedDeliveryAsync(ctx, projectId, busy, buildId, ProjectDeliveryStatus.Installing,
            startedAt: t0.AddHours(2), finishedAt: null,
            apps: [ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Installing, ProjectDeliveryResultStatus.Pending]);
        // Two waiting for their time; the earlier one is next, whichever was created first.
        await SeedDeliveryAsync(ctx, projectId, busy, buildId, ProjectDeliveryStatus.Scheduled,
            startedAt: null, finishedAt: null, apps: [], scheduledFor: t0.AddDays(3));
        var soonest = await SeedDeliveryAsync(ctx, projectId, busy, buildId, ProjectDeliveryStatus.Scheduled,
            startedAt: null, finishedAt: null, apps: [], scheduledFor: t0.AddDays(1), outsideWindow: true);

        var rows = await NewService(_db.NewContext()).ListReleasePipelineOverviewAsync();

        var row = rows.Single(r => r.Id == busy);
        row.LastDelivery.Should().NotBeNull();
        row.LastDelivery!.DeliveryId.Should().Be(failed);
        row.LastDelivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        row.LastDelivery.FailedAppName.Should().Be("CRONUS Sales");
        row.LastDelivery.At.Should().Be(t0.AddHours(1).AddMinutes(2));

        row.LiveDelivery.Should().NotBeNull();
        row.LiveDelivery!.DeliveryId.Should().Be(running);
        row.LiveDelivery.Phase.Should().Be(ProjectDeliveryResultStatus.Installing);
        row.LiveDelivery.CurrentApp.Should().Be(2);
        row.LiveDelivery.AppsDone.Should().Be(1);
        row.LiveDelivery.AppCount.Should().Be(3);
        row.LiveDelivery.StartedAt.Should().Be(t0.AddHours(2));
        row.LiveDelivery.PreviousDuration.Should().Be(TimeSpan.FromMinutes(6), "the last successful release, not the failed one");

        row.NextDelivery.Should().NotBeNull();
        row.NextDelivery!.DeliveryId.Should().Be(soonest);
        row.NextDelivery.OutsideWindow.Should().BeTrue();

        row.EnvironmentNextUpdate.Should().Be(new EnvironmentNextUpdate(
            new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc), "28.4", "Major"));

        var quiet = rows.Single(r => r.Id == idle);
        quiet.LastDelivery.Should().BeNull("nothing has been released through it");
        quiet.LiveDelivery.Should().BeNull();
        quiet.NextDelivery.Should().BeNull();
    }

    [Fact]
    public void A_live_release_that_is_between_apps_counts_the_next_one_as_in_hand()
    {
        var live = ReleasePipelineLiveDelivery.From(1, ProjectDeliveryStatus.Installing, null,
            [ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Pending], null);

        live.Phase.Should().BeNull();
        live.CurrentApp.Should().Be(2);
        live.AppsDone.Should().Be(1);

        var claimed = ReleasePipelineLiveDelivery.From(2, ProjectDeliveryStatus.Claimed, null, [], null);
        claimed.CurrentApp.Should().Be(0, "a release with no apps listed has no app in hand");
    }

    private static async Task<int> SeedBuildAsync(AppDbContext ctx, int projectId, int pipelineId)
    {
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipelineId,
            Status = ProjectBuildStatus.Ready,
            StartedAt = DateTime.UtcNow,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private static async Task<int> SeedDeliveryAsync(
        AppDbContext ctx, int projectId, int releasePipelineId, int buildId, string status,
        DateTime? startedAt, DateTime? finishedAt, string[] apps, string[]? appNames = null,
        DateTime? scheduledFor = null, bool outsideWindow = false)
    {
        var created = startedAt ?? scheduledFor ?? DateTime.UtcNow;
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            ReleasePipelineId = releasePipelineId,
            ProjectBuildId = buildId,
            EnvironmentName = "UAT",
            Status = status,
            ScheduledFor = scheduledFor ?? created,
            ScheduledOutsideWindow = outsideWindow,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            CreatedAt = created,
            UpdatedAt = finishedAt ?? created,
        };
        for (var i = 0; i < apps.Length; i++)
        {
            delivery.Results.Add(new OeProjectDeliveryResult
            {
                OrganizationId = TestDb.DefaultOrgId,
                Ordering = i,
                AppName = appNames?[i] ?? $"App {i + 1}",
                AppVersion = "1.0.0.0",
                Status = apps[i],
                CreatedAt = created,
                UpdatedAt = created,
            });
        }
        ctx.OeProjectDeliveries.Add(delivery);
        await ctx.SaveChangesAsync();
        return delivery.Id;
    }

    // ── Artifact source (#632) ───────────────────────────────────────────────

    [Fact]
    public async Task A_release_pipeline_can_draw_from_a_repositorys_github_releases_instead_of_a_build_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repositoryId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS -> Production", 0, envId,
            BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, repositoryId));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.ArtifactSource.Should().Be(ReleaseArtifactSource.GithubRelease);
        rp.GithubReleaseRepositoryId.Should().Be(repositoryId);
        // Exactly one source: naming both would leave "what does this install" with two answers.
        rp.BuildPipelineId.Should().BeNull();
    }

    [Fact]
    public async Task A_release_sourced_pipeline_needs_a_repository_of_its_own_solution()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var otherProject = await SeedProjectAsync(ctx);
        var otherRepository = await SeedRepositoryAsync(ctx, otherProject);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var missing = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", 0, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, null));
        (await missing.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GithubReleaseRepositoryId");

        var elsewhere = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", 0, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, otherRepository));
        (await elsewhere.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GithubReleaseRepositoryId");
    }

    [Fact]
    public async Task Switching_a_pipeline_to_github_releases_clears_the_build_pipeline_it_used_to_draw_from()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var repositoryId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, repositoryId));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.BuildPipelineId.Should().BeNull();
        rp.GithubReleaseRepositoryId.Should().Be(repositoryId);
    }

    // ── Prepared releases (#934) ─────────────────────────────────────────────

    [Fact]
    public async Task Prepare_a_release_when_a_new_build_succeeds_is_off_by_default_and_saved_when_asked()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).PrepareReleaseOnNewBuild.Should().BeFalse();

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: true));

        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).PrepareReleaseOnNewBuild.Should().BeTrue();
        (await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId)).Single().PrepareReleaseOnNewBuild.Should().BeTrue();
    }

    [Fact]
    public async Task A_pipeline_that_installs_github_releases_never_prepares_on_a_new_build()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repositoryId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", 0, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, repositoryId, PrepareReleaseOnNewBuild: true));

        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).PrepareReleaseOnNewBuild
            .Should().BeFalse("there is no build pipeline whose new builds it could follow");
    }

    [Fact]
    public async Task The_overview_and_the_solution_name_the_release_waiting_for_approval()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildPipelineId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "UAT");
        var svc = NewService(ctx);
        var rpId = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS to UAT", buildPipelineId, envId, BcDeploymentSchedule.Immediate, BcSyncMode.Add));
        var buildId = await SeedBuildAsync(ctx, projectId, buildPipelineId);
        var t0 = new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);
        var deployed = await SeedDeliveryAsync(ctx, projectId, rpId, buildId, ProjectDeliveryStatus.Deployed,
            startedAt: t0, finishedAt: t0.AddMinutes(4), apps: [ProjectDeliveryResultStatus.Completed]);
        // One dismissed before anyone approved it: it never was a release, so it is not "the last release".
        await SeedDeliveryAsync(ctx, projectId, rpId, buildId, ProjectDeliveryStatus.Dismissed,
            startedAt: null, finishedAt: t0.AddHours(2), apps: [], scheduledFor: t0.AddHours(1));
        var waiting = await SeedDeliveryAsync(ctx, projectId, rpId, buildId, ProjectDeliveryStatus.Proposed,
            startedAt: null, finishedAt: null, apps: [], scheduledFor: t0.AddHours(3));

        var row = (await NewService(_db.NewContext()).ListReleasePipelineOverviewAsync()).Single(r => r.Id == rpId);
        row.ProposedDelivery.Should().Be(new ReleasePipelineProposedDelivery(waiting, buildId, t0.AddHours(3)));
        row.NextDelivery.Should().BeNull("a prepared release is not scheduled until someone approves it");
        row.LastDelivery!.DeliveryId.Should().Be(deployed);

        var solution = await NewService(_db.NewContext()).ListWaitingForApprovalAsync(projectId);
        solution.Should().ContainSingle().Which.Should().Be(new ReleaseWaitingForApproval(rpId, "CRONUS to UAT", waiting, buildId));
    }

    private static async Task<int> SeedRepositoryAsync(AppDbContext ctx, int projectId)
    {
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus-dk/cronus-" + Guid.NewGuid().ToString("N") + ".git",
            DisplayName = "cronus-customer",
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();
        return repository.Id;
    }

    private ReleasePipelineService NewService(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), NullLogger<ReleasePipelineService>.Instance);

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

    private static async Task<int> SeedBuildPipelineAsync(AppDbContext ctx, int projectId, string? name = null)
    {
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Build " + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private static async Task<int> SeedEnvironmentAsync(
        AppDbContext ctx, int projectId, string? name = null, string? status = null, bool missing = false,
        TimeOnly? windowStart = null, TimeOnly? windowEnd = null)
    {
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Env " + Guid.NewGuid().ToString("N"),
            Type = "Production",
            Status = status,
            MissingSince = missing ? DateTime.UtcNow : null,
            FetchedAt = DateTime.UtcNow,
            UpdateWindowStart = windowStart,
            UpdateWindowEnd = windowEnd,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return env.Id;
    }
}
