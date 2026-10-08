using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// A push or preview build that waited behind another is checked again when its turn
/// comes, so it does not run once the pipeline no longer asks for it (#1112).
/// </summary>
public sealed class AutomatedBuildRecheckTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(ProjectBuildTrigger.Push)]
    [InlineData(ProjectBuildTrigger.PreviewCheck)]
    public async Task A_build_the_pipeline_still_asks_for_starts(string trigger)
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, trigger, alice);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().BeNull();
        (await StatusAsync(releaseId)).Should().Be(ProjectBuildStatus.Building);
    }

    [Theory]
    [InlineData(ProjectBuildTrigger.Push)]
    [InlineData(ProjectBuildTrigger.PreviewCheck)]
    public async Task A_deleted_pipeline_refuses_its_waiting_builds(string trigger)
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, trigger, alice);
        await UpdatePipelineAsync(pipelineId, p => p.DeletedAt = DateTime.UtcNow);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("The pipeline was deleted before this build started.");
        (await StatusAsync(releaseId)).Should().Be(ProjectBuildStatus.Queued, "the worker fails it with the reason");
    }

    [Theory]
    [InlineData(ProjectBuildTrigger.Push)]
    [InlineData(ProjectBuildTrigger.PreviewCheck)]
    public async Task A_disabled_pipeline_refuses_its_waiting_builds(string trigger)
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, trigger, alice);
        await UpdatePipelineAsync(pipelineId, p => p.DisabledAt = DateTime.UtcNow);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("The pipeline was disabled before this build started.");
    }

    [Fact]
    public async Task Turning_building_on_push_off_refuses_the_waiting_push_builds()
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.Push, alice);
        await UpdatePipelineAsync(pipelineId, p => p.BuildOnPush = false);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("Building automatically on push was turned off before this build started.");
    }

    [Fact]
    public async Task Turning_the_preview_check_off_refuses_the_waiting_check()
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.PreviewCheck, alice);
        await UpdatePipelineAsync(pipelineId, p => p.PreviewCheck = false);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("The nightly preview check was turned off before this build started.");
    }

    [Fact]
    public async Task Someone_else_taking_the_builds_over_refuses_the_ones_waiting_as_the_old_person()
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var bob = await SeedUserAsync("bob@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.Push, alice);
        await UpdatePipelineAsync(pipelineId, p => p.BuildOnPushByUserId = bob);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("Someone else took over this pipeline's automatic builds before this build started.");
    }

    [Fact]
    public async Task A_person_who_left_refuses_the_builds_waiting_as_them()
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.Push, alice);
        await using (var ctx = _db.NewContext())
        {
            await ctx.Users.Where(u => u.Id == alice)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, UserStatus.Disabled));
        }

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("The person this build runs as no longer has an active account.");
    }

    [Fact]
    public async Task A_person_who_can_no_longer_manage_the_solution_refuses_the_builds_waiting_as_them()
    {
        var owner = await SeedUserAsync("owner@cronus.test");
        var alice = await SeedUserAsync("alice@cronus.test", UserRole.User);
        var (projectId, pipelineId) = await SeedPipelineAsync(alice, projectOwner: owner);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.Push, alice);

        var refusal = await RefusalAsync(projectId, releaseId, alice);

        refusal.Should().Be("The person this build runs as can no longer manage this solution.");
    }

    [Theory]
    [InlineData(ProjectBuildTrigger.Push)]
    [InlineData(ProjectBuildTrigger.PreviewCheck)]
    public async Task Retrying_a_finished_build_runs_as_the_person_who_asked_whatever_the_pipeline_says(string trigger)
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var admin = await SeedUserAsync("admin@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, trigger, alice, finished: true);
        await UpdatePipelineAsync(pipelineId, p => { p.BuildOnPush = false; p.PreviewCheck = false; });

        var refusal = await RefusalAsync(projectId, releaseId, admin);

        refusal.Should().BeNull("a retry is a person's choice, not the pipeline's automation");
        (await StatusAsync(releaseId)).Should().Be(ProjectBuildStatus.Building);
    }

    [Fact]
    public async Task Starting_a_build_stamps_when_it_left_the_queue_and_clears_the_earlier_attempts_failure()
    {
        var alice = await SeedUserAsync("alice@cronus.test");
        var (projectId, pipelineId) = await SeedPipelineAsync(alice);
        var releaseId = await SeedBuildAsync(projectId, pipelineId, ProjectBuildTrigger.Manual, alice, finished: true);
        await using (var ctx = _db.NewContext())
        {
            // A retry of a build that failed: the row still carries the old reason.
            await ctx.OeProjectBuilds.Where(b => b.ReleaseId == releaseId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.FailureMessage, "Compile failed."));
        }
        var clock = new ALDevToolbox.Tests.Auth.FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 30, 0, TimeSpan.Zero));

        (await RefusalAsync(projectId, releaseId, alice, clock)).Should().BeNull();

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.ReleaseId == releaseId);
        build.Status.Should().Be(ProjectBuildStatus.Building);
        build.BuildingStartedAt.Should().Be(clock.GetUtcNow().UtcDateTime,
            "the build history times the build from here, not from when it was queued (#1137)");
        build.FailureMessage.Should().BeNull("the earlier attempt's reason is no longer the build's");
    }

    /// <summary>
    /// Runs the build as <paramref name="userId"/>, the way the worker does, and returns
    /// the refusal it gave, or null when it got past the check. Past it, the build goes
    /// on to clone with dependencies this test does not provide, so whatever fails there
    /// is not a refusal.
    /// </summary>
    private async Task<string?> RefusalAsync(int projectId, int releaseId, int userId, TimeProvider? clock = null)
    {
        var context = _db.OrgContext;
        context.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        // Never reached when the build is refused: the check runs before anything is cloned.
        var service = new ProjectBuildService(
            ctx, context, new ProjectAccess(ctx, context),
            null!, null!, null!, null!, null!, null!, null!, clock ?? TimeProvider.System, NullLogger<ProjectBuildService>.Instance);
        try
        {
            await service.BuildAsync(projectId, releaseId, ct: TestContext.Current.CancellationToken);
            return null;
        }
        catch (PlanValidationException ex)
        {
            return ex.Errors["Build"];
        }
        catch (Exception ex) when (ex is NullReferenceException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            context.CurrentUserId = null;
        }
    }

    private async Task<string> StatusAsync(int releaseId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectBuilds.AsNoTracking().Where(b => b.ReleaseId == releaseId).Select(b => b.Status).SingleAsync();
    }

    private async Task<int> SeedUserAsync(string email, UserRole role = UserRole.Admin)
    {
        await using var ctx = _db.NewContext();
        var user = new User
        {
            OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = email, PasswordHash = "x",
            Role = role, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        };
        ctx.Users.Add(user);
        await ctx.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(int ProjectId, int PipelineId)> SeedPipelineAsync(int runsAs, int? projectOwner = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS " + Guid.NewGuid().ToString("N")[..8],
            CreatedByUserId = projectOwner ?? runsAs,
            DefaultArtifactCountry = "dk",
            Visibility = projectOwner is null ? ProjectVisibility.Public : ProjectVisibility.ReadOnly,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "main " + Guid.NewGuid().ToString("N")[..8],
            BuildOnPush = true,
            BuildOnPushByUserId = runsAs,
            PreviewCheck = true,
            PreviewCheckByUserId = runsAs,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return (project.Id, pipeline.Id);
    }

    private async Task<int> SeedBuildAsync(int projectId, int pipelineId, string trigger, int startedBy, bool finished = false)
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId, Label = "CRONUS " + Guid.NewGuid().ToString("N"), Kind = "project",
            Status = "ingesting", ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = ProjectBuildStatus.Queued, Trigger = trigger, StartedByUserId = startedBy,
            BcTarget = trigger == ProjectBuildTrigger.PreviewCheck ? ProjectBuildTarget.NextMajor : ProjectBuildTarget.Current,
            StartedAt = DateTime.UtcNow, Release = release,
            // A finished build put back in the queue by Retry keeps its finish time.
            FinishedAt = finished ? DateTime.UtcNow : null,
        });
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    private async Task UpdatePipelineAsync(int pipelineId, Action<OePipeline> change)
    {
        await using var ctx = _db.NewContext();
        var pipeline = await ctx.OePipelines.SingleAsync(p => p.Id == pipelineId);
        change(pipeline);
        await ctx.SaveChangesAsync();
    }
}
