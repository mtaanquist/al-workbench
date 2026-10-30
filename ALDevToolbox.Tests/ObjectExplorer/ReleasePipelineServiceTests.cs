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
/// schema-sync modes are validated, plus update, archiving, and the list
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
    public async Task CreateReleasePipelineAsync_defaults_a_blank_sync_mode_to_add()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, ""));

        await using var read = _db.NewContext();
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == id)).SchemaSyncMode.Should().Be(BcSyncMode.Add);
    }

    // ── "Right away" is chosen per deployment, not set on a pipeline (#1471) ────

    [Theory]
    [InlineData(BcDeploymentSchedule.Immediate)]
    [InlineData("")]
    public async Task A_new_pipeline_cannot_be_set_to_install_right_away(string schedule)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, schedule, BcSyncMode.Add));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["DeploymentSchedule"]
            .Should().Contain("choose that when you deploy it");
        (await _db.NewContext().OeReleasePipelines.AnyAsync(r => r.ProjectId == projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task A_pipeline_cannot_be_changed_to_install_right_away()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var input = new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add);
        var id = await NewService(ctx).CreateReleasePipelineAsync(input);

        var act = () => NewService(_db.NewContext()).UpdateReleasePipelineAsync(id, input with { DeploymentSchedule = BcDeploymentSchedule.Immediate });

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("DeploymentSchedule");
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).DeploymentSchedule
            .Should().Be(BcDeploymentSchedule.NextMinorUpdate);
    }

    [Fact]
    public async Task A_pipeline_already_set_to_install_right_away_keeps_it_when_saved()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var input = new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add);
        var id = await NewService(ctx).CreateReleasePipelineAsync(input);
        // Set before #1471, when the editor still offered it.
        await ctx.OeReleasePipelines.Where(r => r.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.DeploymentSchedule, BcDeploymentSchedule.Immediate));

        await NewService(_db.NewContext()).UpdateReleasePipelineAsync(id,
            input with { DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.ForceSync });

        var rp = await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    [Fact]
    public async Task A_deployment_pipeline_is_named_after_its_build_pipeline_and_environment()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var envId = await SeedEnvironmentAsync(ctx, projectId, "Production");

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "  ", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("main to Production");
        rp.NameIsCustom.Should().BeFalse();
    }

    [Fact]
    public async Task A_deployment_pipeline_from_github_releases_is_named_after_the_repository()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repoId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, "Production");

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, null, 0, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, repoId));

        await using var read = _db.NewContext();
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == id)).Name.Should().Be("cronus-customer releases to Production");
    }

    [Fact]
    public async Task A_taken_generated_name_asks_for_a_name_of_its_own_and_keeps_the_one_typed()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var envId = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Name")
            .WhoseValue.Should().Contain("main to Production");

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "main to Production - hotfix", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(projectId, "main to Production - hotfix", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("main to Production - hotfix");
        rp.NameIsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task Changing_the_environment_drops_a_typed_name_left_as_it_was()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var prod = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var test = await SeedEnvironmentAsync(ctx, projectId, "Test");
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "main - hotfixes", buildId, prod, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(projectId, "main - hotfixes", buildId, test, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("main to Test");
        rp.NameIsCustom.Should().BeFalse();
    }

    [Fact]
    public async Task Changing_the_source_drops_a_typed_name_left_as_it_was()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var main = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var release = await SeedBuildPipelineAsync(ctx, projectId, "release/26");
        var prod = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "main - hotfixes", main, prod, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(projectId, "main - hotfixes", release, prod, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("release/26 to Production");
        rp.NameIsCustom.Should().BeFalse();
    }

    [Fact]
    public async Task A_name_typed_in_the_same_save_as_an_environment_change_is_kept()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var prod = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var test = await SeedEnvironmentAsync(ctx, projectId, "Test");
        var svc = NewService(ctx);
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "main - hotfixes", buildId, prod, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(projectId, "main - hotfixes to Test", buildId, test, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("main - hotfixes to Test");
        rp.NameIsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_name_stays_when_the_generated_name_for_the_new_environment_is_taken()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, "main");
        var prod = await SeedEnvironmentAsync(ctx, projectId, "Production");
        var test = await SeedEnvironmentAsync(ctx, projectId, "Test");
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, null, buildId, test, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "main - hotfixes", buildId, prod, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(projectId, "main - hotfixes", buildId, test, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("main - hotfixes");
        rp.NameIsCustom.Should().BeTrue();
        rp.ProjectEnvironmentId.Should().Be(test);
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_duplicate_name_in_the_same_project_case_insensitively()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "Production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(projectId, "production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

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
            projectA, "Production", buildA, envA, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var act = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectB, "Production", buildB, envB, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_a_missing_project()
    {
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(424242, "Rel", 1, 1, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

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
            new ReleasePipelineInput(projectA, "Rel", otherBuild, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

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
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("Retired", "the refusal names the environment the consultant picked");
    }

    [Fact]
    public async Task CreateReleasePipelineAsync_rejects_an_environment_that_will_never_take_an_install()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production", status: "PreparingFailed");

        var act = () => NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironmentId"];
        error.Should().Contain("PreparingFailed", "the same wording the delivery gate uses, just earlier");
    }

    [Theory]
    [InlineData("Preparing")]
    [InlineData("Upgrading")]
    public async Task CreateReleasePipelineAsync_accepts_a_busy_environment(string status)
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        // A new environment reads as Preparing until the next refresh, long after it is
        // up; it takes installs again on its own, and each deployment checks again.
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Development", status: status);

        var id = await NewService(ctx).CreateReleasePipelineAsync(
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        id.Should().BeGreaterThan(0);
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
            new ReleasePipelineInput(projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

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
            projectId, "Production", buildId, prodEnv, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Sandbox", buildId, sandboxEnv, BcDeploymentSchedule.NextMajorUpdate, BcSyncMode.ForceSync));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.Name.Should().Be("Sandbox");
        rp.ProjectEnvironmentId.Should().Be(sandboxEnv);
        rp.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMajorUpdate);
        rp.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
    }

    // ── Archiving (#1466) ──────────────────────────────────────────────────────

    /// <summary>A deployment pipeline under a fresh solution, and the service that made it.</summary>
    private async Task<(int ProjectId, int BuildId, int EnvId, int Id, ReleasePipelineInput Input)> SeedArchivableAsync(AppDbContext ctx)
    {
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var input = new ReleasePipelineInput(projectId, "Production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add);
        var id = await NewService(ctx).CreateReleasePipelineAsync(input);
        return (projectId, buildId, envId, id, input);
    }

    [Fact]
    public async Task An_archived_pipeline_leaves_the_lists_but_its_page_and_history_stay()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        var svc = NewService(ctx);
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);

        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);

        await using var read = _db.NewContext();
        var reader = NewService(read);
        (await reader.ListReleasePipelinesAsync(seed.ProjectId)).Should().BeEmpty();
        (await reader.ListReleasePipelineOverviewAsync()).Should().NotContain(r => r.Id == seed.Id);
        var archived = (await reader.ListReleasePipelineOverviewAsync(includeArchived: true)).Single(r => r.Id == seed.Id);
        archived.Archived.Should().BeTrue();
        archived.Disabled.Should().BeTrue();
        var rp = await reader.GetReleasePipelineAsync(seed.Id);
        rp.Should().NotBeNull("the page still opens, with its history");
        rp!.ArchivedAt.Should().NotBeNull();
        rp.DeletedAt.Should().BeNull();
    }

    [Fact]
    public async Task Only_a_disabled_pipeline_can_be_archived()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);

        var act = () => NewService(ctx).SetReleasePipelineArchivedAsync(seed.Id, true);

        var errors = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors["ReleasePipeline"].Should().Be("Disable this deployment pipeline before archiving it.");
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_archived_pipeline_can_be_neither_enabled_nor_edited_until_it_is_restored()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        var svc = NewService(ctx);
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);
        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);

        var enable = () => NewService(_db.NewContext()).SetReleasePipelineDisabledAsync(seed.Id, false);
        var edit = () => NewService(_db.NewContext()).UpdateReleasePipelineAsync(seed.Id, seed.Input);

        (await enable.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ReleasePipeline"]
            .Should().Be(ReleasePipelineService.ArchivedRefusal);
        (await edit.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ReleasePipeline"]
            .Should().Be(ReleasePipelineService.ArchivedRefusal);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).DisabledAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Restoring_brings_the_pipeline_back_still_disabled()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        var svc = NewService(ctx);
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);
        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);

        await NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, false);

        var row = (await NewService(_db.NewContext()).ListReleasePipelinesAsync(seed.ProjectId)).Single();
        row.Archived.Should().BeFalse();
        row.Disabled.Should().BeTrue();
    }

    [Fact]
    public async Task Archiving_frees_the_name_and_restoring_is_refused_while_another_pipeline_has_it()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        var svc = NewService(ctx);
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);
        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);

        // The same setup again generates the same name, which the archived one no longer holds.
        var replacement = await NewService(_db.NewContext()).CreateReleasePipelineAsync(seed.Input);
        var restore = () => NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, false);

        await using var read = _db.NewContext();
        var names = await read.OeReleasePipelines.Where(r => r.Id == seed.Id || r.Id == replacement).Select(r => r.Name).ToListAsync();
        names.Distinct(StringComparer.OrdinalIgnoreCase).Should().ContainSingle();
        (await restore.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ReleasePipeline"]
            .Should().Contain("Another deployment pipeline in this solution is called");
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Archiving_or_restoring_twice_changes_nothing()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        var svc = NewService(ctx);
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);
        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);
        var archivedAt = (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt;

        await NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, true);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().Be(archivedAt);

        await NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, false);
        await NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, false);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task The_database_refuses_an_archived_pipeline_that_is_not_disabled()
    {
        // What keeps an archive and an enable racing each other from leaving a hidden
        // pipeline that still deploys.
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);

        var act = () => _db.NewContext().OeReleasePipelines.Where(r => r.Id == seed.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ArchivedAt, DateTime.UtcNow));

        (await act.Should().ThrowAsync<Npgsql.PostgresException>()).Which.ConstraintName
            .Should().Be("ck_oe_release_pipelines_archived_is_disabled");
    }

    [Fact]
    public async Task Someone_who_manages_the_solution_can_archive_and_restore_without_being_an_admin()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedArchivableAsync(ctx);
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = await SeedUserAsync(ctx, "Team Member");

        var svc = NewService(_db.NewContext());
        await svc.SetReleasePipelineDisabledAsync(seed.Id, true);
        await svc.SetReleasePipelineArchivedAsync(seed.Id, true);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().NotBeNull();

        await NewService(_db.NewContext()).SetReleasePipelineArchivedAsync(seed.Id, false);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == seed.Id)).ArchivedAt.Should().BeNull();
    }

    [Fact]
    public async Task Editing_a_deleted_deployment_pipeline_is_not_reported_as_a_name_clash()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);
        var input = new ReleasePipelineInput(projectId, "Production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add);
        var id = await svc.CreateReleasePipelineAsync(input);
        // Nothing deletes a deployment pipeline any more (#1466), but ones deleted before
        // that, and those of a deleted solution, still carry the marker.
        await ctx.OeReleasePipelines.Where(r => r.Id == id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.DeletedAt, DateTime.UtcNow));

        var act = () => svc.UpdateReleasePipelineAsync(id, input);

        // The editor opens a custom-name field for any Name error, so this must not be one.
        var errors = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors.Should().NotContainKey("Name");
        errors["ReleasePipeline"].Should().Be("This deployment pipeline no longer exists.");
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
            projectId, "Contoso → Production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));

        var rows = await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId);

        rows.Should().ContainSingle();
        var row = rows[0];
        row.BuildPipelineName.Should().Be("Nightly");
        row.EnvironmentName.Should().Be("Production");
        row.EnvironmentMissing.Should().BeFalse();
    }

    [Fact]
    public async Task A_pipeline_says_so_when_its_build_pipeline_was_deleted()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId, name: "Nightly");
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production");
        await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Nightly to Production", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        (await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId)).Single().BuildPipelineDeleted.Should().BeFalse();

        // Deleted before deleting one in use was refused.
        await ctx.OePipelines.Where(p => p.Id == buildId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.DeletedAt, DateTime.UtcNow));

        var row = (await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId)).Single();
        row.BuildPipelineDeleted.Should().BeTrue();
        row.BuildPipelineName.Should().Be("Nightly");
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
            projectId, "CRONUS App -> JLE", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
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
            projectId, "CRONUS to UAT", buildPipelineId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        var idle = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS to UAT again", buildPipelineId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
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

    // --- The branch rule -------------------------------------------------------

    [Fact]
    public async Task A_pipeline_saves_and_lists_its_branch_rule()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            RestrictBranch: true, AllowedBranch: " release/28.2 "));

        var row = (await svc.ListReleasePipelinesAsync(projectId)).Single(r => r.Id == id);
        row.RestrictBranch.Should().BeTrue();
        row.AllowedBranch.Should().Be("release/28.2");

        // Blank is the repositories' default branch.
        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            RestrictBranch: true, AllowedBranch: "  "));
        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.RestrictBranch.Should().BeTrue();
        rp.AllowedBranch.Should().BeNull();
    }

    [Fact]
    public async Task A_branch_rule_with_an_invalid_branch_name_is_refused()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var act = () => NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            RestrictBranch: true, AllowedBranch: "main..test"));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("AllowedBranch");
    }

    [Fact]
    public async Task A_pipeline_that_installs_github_releases_keeps_no_branch_rule()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repositoryId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", 0, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, repositoryId,
            RestrictBranch: true, AllowedBranch: "main"));

        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.RestrictBranch.Should().BeFalse();
        rp.AllowedBranch.Should().BeNull();
    }

    [Fact]
    public async Task A_release_pipeline_can_draw_from_a_repositorys_github_releases_instead_of_a_build_pipeline()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var repositoryId = await SeedRepositoryAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId);

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "CRONUS -> Production", 0, envId,
            BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
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
            projectId, "Rel", 0, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            ReleaseArtifactSource.GithubRelease, null));
        (await missing.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("GithubReleaseRepositoryId");

        var elsewhere = () => svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, "Rel", 0, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
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
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
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
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).PrepareReleaseOnNewBuild.Should().BeFalse();

        await svc.UpdateReleasePipelineAsync(id, new ReleasePipelineInput(
            projectId, "Rel", buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
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
            projectId, "Rel", 0, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
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
            projectId, "CRONUS to UAT", buildPipelineId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add));
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

    // ── Deploying to a sandbox without approval (#1096) ─────────────────────────

    [Fact]
    public async Task Deploying_without_approval_is_saved_for_a_sandbox_and_runs_as_the_person_who_saved_it()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, type: "Sandbox");
        var first = await SeedUserAsync(ctx, "First Person");
        _db.OrgContext.CurrentUserId = first;
        var svc = NewService(ctx);

        var id = await svc.CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: true, DeployWithoutApproval: true));

        var rp = await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id);
        rp.DeployWithoutApproval.Should().BeTrue();
        rp.DeployWithoutApprovalByUserId.Should().Be(first);
        (await NewService(_db.NewContext()).ListReleasePipelinesAsync(projectId)).Single().DeployWithoutApproval.Should().BeTrue();

        // Someone else saving it with the option on makes them the person it runs as.
        var second = await SeedUserAsync(ctx, "Second Person");
        _db.OrgContext.CurrentUserId = second;
        var input = new ReleasePipelineInput(
            projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: true, DeployWithoutApproval: true);
        await NewService(_db.NewContext()).UpdateReleasePipelineAsync(id, input);
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).DeployWithoutApprovalByUserId.Should().Be(second);

        // Off clears it.
        await NewService(_db.NewContext()).UpdateReleasePipelineAsync(id, input with { DeployWithoutApproval = false });
        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).DeployWithoutApprovalByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Turning_on_deploying_without_approval_takes_the_step_up_rule_deploying_takes()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, type: "Sandbox");
        var input = new ReleasePipelineInput(
            projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: true, DeployWithoutApproval: false);
        var id = await NewService(ctx).CreateReleasePipelineAsync(input);
        // The organisation asks for a recent second factor before deploying (#1127).
        await ctx.Organizations.Where(o => o.Id == TestDb.DefaultOrgId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.StepUpTools,
                ALDevToolbox.Domain.Tools.ToolCatalog.Format(new[] { ALDevToolbox.Domain.Tools.ToolKey.Releases })));
        // A cookie session with no recent second factor.
        var session = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(ALDevToolbox.Services.HttpOrganizationContext.UserIdClaim, "1"),
            new System.Security.Claims.Claim(ALDevToolbox.Services.HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new System.Security.Claims.Claim(ALDevToolbox.Endpoints.EndpointHelpers.DisabledToolsClaim, string.Empty),
            new System.Security.Claims.Claim(ALDevToolbox.Endpoints.EndpointHelpers.StepUpToolsClaim, string.Empty),
        }, "test"));
        ReleasePipelineService Svc(AppDbContext c) => new(c, _db.OrgContext, new ProjectAccess(c, _db.OrgContext),
            new ALDevToolbox.Services.Tools.ToolEnablement(TestDb.EverythingEnabled(),
                new TestDb.FixedHttpContextAccessor(new Microsoft.AspNetCore.Http.DefaultHttpContext { User = session }),
                c, _db.OrgContext, TimeProvider.System),
            NullLogger<ReleasePipelineService>.Instance);

        // Saving with it off needs nothing more.
        await Svc(_db.NewContext()).UpdateReleasePipelineAsync(id, input with { PrepareReleaseOnNewBuild = false });

        var turnOn = () => Svc(_db.NewContext()).UpdateReleasePipelineAsync(id, input with { DeployWithoutApproval = true });
        await turnOn.Should().ThrowAsync<ALDevToolbox.Services.Tools.StepUpRequiredException>();
        var otherEnvId = await SeedEnvironmentAsync(ctx, projectId, name: "Sandbox2", type: "Sandbox");
        var create = () => Svc(_db.NewContext()).CreateReleasePipelineAsync(input with { ProjectEnvironmentId = otherEnvId, DeployWithoutApproval = true });
        await create.Should().ThrowAsync<ALDevToolbox.Services.Tools.StepUpRequiredException>();

        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).DeployWithoutApproval.Should().BeFalse();
    }

    [Fact]
    public async Task Deploying_without_approval_is_refused_for_an_environment_that_is_not_a_sandbox()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, name: "Production");

        var act = () => NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: true, DeployWithoutApproval: true));

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DeployWithoutApproval")
            .WhoseValue.Should().Contain("isn't a sandbox");
    }

    [Fact]
    public async Task Deploying_without_approval_needs_a_prepared_deployment_to_skip()
    {
        await using var ctx = _db.NewContext();
        var projectId = await SeedProjectAsync(ctx);
        var buildId = await SeedBuildPipelineAsync(ctx, projectId);
        var envId = await SeedEnvironmentAsync(ctx, projectId, type: "Sandbox");

        var id = await NewService(ctx).CreateReleasePipelineAsync(new ReleasePipelineInput(
            projectId, null, buildId, envId, BcDeploymentSchedule.NextMinorUpdate, BcSyncMode.Add,
            PrepareReleaseOnNewBuild: false, DeployWithoutApproval: true));

        (await _db.NewContext().OeReleasePipelines.SingleAsync(r => r.Id == id)).DeployWithoutApproval.Should().BeFalse();
    }

    private static async Task<int> SeedUserAsync(AppDbContext ctx, string displayName)
    {
        var user = new ALDevToolbox.Domain.Entities.User
        {
            OrganizationId = TestDb.DefaultOrgId,
            Email = $"u{Guid.NewGuid():N}@example.com",
            PasswordHash = "x",
            DisplayName = displayName,
            Role = ALDevToolbox.Domain.Entities.UserRole.User,
            Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
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
        new(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext), _db.NewToolEnablement(ctx), NullLogger<ReleasePipelineService>.Instance);

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
        TimeOnly? windowStart = null, TimeOnly? windowEnd = null, string type = "Production")
    {
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name ?? "Env " + Guid.NewGuid().ToString("N"),
            Type = type,
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
