using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="PipelinesDashboardService"/> over a seeded database (#955): each tile's
/// number, each kind of "Needs attention" row, the merged activity timeline's order, and
/// the visibility gate the two lists share. See <c>.design/saas-delivery.md</c>,
/// "Pipelines dashboard".
/// </summary>
public sealed class PipelinesDashboardServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DateTime _now = DateTime.UtcNow;

    public PipelinesDashboardServiceTests()
    {
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task An_organisation_with_no_pipelines_gets_zeros_and_empty_lists()
    {
        await SeedSolutionAsync("CRONUS");

        var d = await GetAsync();

        d.BuildPipelineCount.Should().Be(0);
        d.DeploymentPipelineCount.Should().Be(0);
        d.LastBuild.Should().BeNull();
        d.ShippingNow.Should().BeEmpty();
        d.Attention.Should().BeEmpty();
        d.Activity.Should().BeEmpty();
    }

    [Fact]
    public async Task Build_tiles_count_pipelines_this_weeks_builds_and_the_pipelines_whose_newest_build_failed()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var baseLine = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        var sales = await SeedBuildPipelineAsync(projectId, "CRONUS Sales");
        await SeedBuildAsync(projectId, baseLine, ProjectBuildStatus.Ready, _now.AddDays(-2));
        var failed = await SeedBuildAsync(projectId, baseLine, ProjectBuildStatus.Failed, _now.AddHours(-1),
            branch: "main", failure: "The app did not compile.");
        await SeedBuildAsync(projectId, sales, ProjectBuildStatus.Ready, _now.AddDays(-10));
        var running = await SeedBuildAsync(projectId, sales, ProjectBuildStatus.Building, _now.AddMinutes(-5), branch: "feature/x");

        var d = await GetAsync();

        d.BuildPipelineCount.Should().Be(2);
        d.BuildsThisWeek.Should().Be(3, "the ten-day-old build is outside the last seven days");
        d.FailedBuildPipelines.Should().Be(1);
        d.LastBuild.Should().BeEquivalentTo(new { BuildId = running, PipelineId = sales, PipelineName = "CRONUS Sales", Branch = "feature/x" });

        var row = d.Attention.Should().ContainSingle().Which;
        row.Kind.Should().Be(PipelinesAttentionKind.FailedBuild);
        row.BuildId.Should().Be(failed);
        row.PipelineId.Should().Be(baseLine);
        row.Branch.Should().Be("main");
        row.Detail.Should().Be("The app did not compile.");
    }

    [Fact]
    public async Task A_failed_preview_check_counts_as_failing_and_is_counted_separately()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var broken = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        var checkedPipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Retail");
        await SeedBuildAsync(projectId, broken, ProjectBuildStatus.Failed, _now.AddHours(-3));
        await SeedBuildAsync(projectId, checkedPipeline, ProjectBuildStatus.Ready, _now.AddHours(-2));
        // Last night's check is the pipeline's newest build, but not its "latest".
        await SeedPreviewCheckAsync(projectId, checkedPipeline, ProjectBuildStatus.Failed, _now.AddHours(-1));

        var d = await GetAsync();

        d.FailedBuildPipelines.Should().Be(2, "a red next-major check is the signal the check exists to give");
        d.FailedPreviewBuildPipelines.Should().Be(1);
        d.Attention.Where(a => a.Kind == PipelinesAttentionKind.FailedBuild).Select(a => a.PipelineId)
            .Should().Equal((int?)broken);
    }

    [Fact]
    public async Task A_check_that_built_but_could_not_compile_an_extension_counts_as_failed()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var pipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-2));
        var check = await SeedPreviewCheckAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-1));
        await using (var ctx = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "CRONUS Base on BC 29.0", Kind = "project", Status = "ready",
                ImportedAt = _now, CreatedAt = _now, UpdatedAt = _now,
            };
            ctx.OeReleases.Add(release);
            await ctx.SaveChangesAsync();
            ctx.OeProjectBuildResults.Add(new OeProjectBuildResult
            {
                OrganizationId = TestDb.DefaultOrgId, ReleaseId = release.Id, AppName = "CRONUS Sales",
                AppId = Guid.NewGuid().ToString(), Status = ProjectBuildResultStatus.Failed, CreatedAt = _now,
            });
            await ctx.SaveChangesAsync();
            await ctx.OeProjectBuilds.Where(b => b.Id == check)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.ReleaseId, release.Id));
        }

        var d = await GetAsync();

        d.FailedBuildPipelines.Should().Be(1);
        d.FailedPreviewBuildPipelines.Should().Be(1);
    }

    [Fact]
    public async Task A_passing_check_does_not_hide_a_failed_build()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var pipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Failed, _now.AddHours(-2));
        await SeedPreviewCheckAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-1));

        var d = await GetAsync();

        d.FailedBuildPipelines.Should().Be(1);
        d.FailedPreviewBuildPipelines.Should().Be(0);
    }

    private async Task<int> SeedPreviewCheckAsync(int projectId, int pipelineId, string status, DateTime startedAt)
    {
        var id = await SeedBuildAsync(projectId, pipelineId, status, startedAt);
        await using var ctx = _db.NewContext();
        await ctx.OeProjectBuilds.Where(b => b.Id == id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMajor)
                .SetProperty(b => b.Trigger, ProjectBuildTrigger.PreviewCheck));
        await ctx.OePipelines.Where(p => p.Id == pipelineId)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheck, true));
        return id;
    }

    [Fact]
    public async Task A_failed_check_stops_counting_once_the_check_is_turned_off()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var pipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-2));
        await SeedPreviewCheckAsync(projectId, pipeline, ProjectBuildStatus.Failed, _now.AddHours(-1));
        await using (var ctx = _db.NewContext())
        {
            await ctx.OePipelines.Where(p => p.Id == pipeline)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheck, false));
        }

        var d = await GetAsync();

        d.FailedBuildPipelines.Should().Be(0);
        d.FailedPreviewBuildPipelines.Should().Be(0);
    }

    [Fact]
    public async Task A_pipeline_whose_newest_build_passed_is_not_failing_even_after_an_older_failure()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var pipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Failed, _now.AddHours(-3));
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-1));

        var d = await GetAsync();

        d.FailedBuildPipelines.Should().Be(0);
        d.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task Deployment_tiles_and_attention_cover_shipping_failed_waiting_and_broken_environments()
    {
        var s = await SeedDeploymentFleetAsync();

        var d = await GetAsync();

        d.DeploymentPipelineCount.Should().Be(5);
        d.ShippingNow.Should().ContainSingle().Which.Should().Be(
            new PipelinesShipping(s.Live, "CRONUS", "UAT", 2, 3));
        d.FailedDeploymentPipelines.Should().Be(1);
        d.WaitingForApproval.Should().Be(1);
        d.OldestWaitingAt.Should().BeCloseTo(_now.AddDays(-3), TimeSpan.FromSeconds(1));
        d.LastDeploymentAt.Should().BeCloseTo(_now.AddHours(-5).AddMinutes(2), TimeSpan.FromSeconds(1));

        d.Attention.Select(a => (a.Kind, a.PipelineId)).Should().BeEquivalentTo(new[]
        {
            (PipelinesAttentionKind.FailedDeployment, (int?)s.Failed),
            (PipelinesAttentionKind.WaitingForApproval, (int?)s.Waiting),
            (PipelinesAttentionKind.EnvironmentDeleting, (int?)s.Deleting),
        });
        var failed = d.Attention.Single(a => a.Kind == PipelinesAttentionKind.FailedDeployment);
        failed.Detail.Should().Be("CRONUS Sales", "the row names the app the deployment stopped on");
        failed.EnvironmentName.Should().Be("Test");
        d.Attention.Single(a => a.Kind == PipelinesAttentionKind.WaitingForApproval).BuildId.Should().Be(s.BuildId);
    }

    [Fact]
    public async Task Attention_rows_run_newest_first_with_undated_rows_after_them()
    {
        var s = await SeedDeploymentFleetAsync();
        await using (var ctx = _db.NewContext())
        {
            var gone = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = s.ProjectId, Name = "Old", Type = "Sandbox",
                Status = "Active", FetchedAt = _now, MissingSince = _now.AddHours(-2),
            };
            ctx.OeProjectEnvironments.Add(gone);
            await ctx.SaveChangesAsync();
            ctx.OeReleasePipelines.Add(ReleasePipeline(s.ProjectId, "CRONUS to Old", s.BuildPipelineId, gone.Id));
            await ctx.SaveChangesAsync();
        }

        var d = await GetAsync();

        d.Attention.Select(a => a.Kind).Should().Equal(
            PipelinesAttentionKind.EnvironmentMissing,   // 2 hours ago
            PipelinesAttentionKind.FailedDeployment,     // 5 hours ago
            PipelinesAttentionKind.WaitingForApproval,   // 3 days ago
            PipelinesAttentionKind.EnvironmentDeleting); // no moment on record
    }

    [Fact]
    public async Task Expiring_secrets_are_flagged_per_solution_and_once_for_the_shared_registration()
    {
        var own = await SeedSolutionAsync("CRONUS", tenant: true, ownClient: true, ownExpires: _now.AddDays(6));
        var later = await SeedSolutionAsync("Fabrikam", tenant: true, ownClient: true, ownExpires: _now.AddDays(60));
        var sharedA = await SeedSolutionAsync("Contoso", tenant: true);
        var sharedB = await SeedSolutionAsync("Adatum", tenant: true);
        var noPipeline = await SeedSolutionAsync("Litware", tenant: true, ownClient: true, ownExpires: _now.AddDays(2));
        await using (var ctx = _db.NewContext())
        {
            var settings = await ctx.OrganizationSettings.FirstOrDefaultAsync(o => o.OrganizationId == TestDb.DefaultOrgId);
            if (settings is null)
            {
                settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
                ctx.OrganizationSettings.Add(settings);
            }
            settings.BcClientId = "11111111-1111-1111-1111-111111111111";
            settings.BcClientSecretExpiresAt = _now.AddDays(3);
            await ctx.SaveChangesAsync();
        }
        foreach (var id in new[] { own, later, sharedA, sharedB })
        {
            await SeedDeploymentPipelineAsync(id);
        }

        var d = await GetAsync();

        var ownRow = d.Attention.Should().ContainSingle(a => a.Kind == PipelinesAttentionKind.SecretExpiring).Which;
        ownRow.ProjectId.Should().Be(own);
        ownRow.ExpiresAt.Should().BeCloseTo(_now.AddDays(6), TimeSpan.FromSeconds(1));
        var shared = d.Attention.Should().ContainSingle(a => a.Kind == PipelinesAttentionKind.SharedSecretExpiring).Which;
        shared.SolutionCount.Should().Be(2);
        shared.ProjectName.Should().Be("Adatum", "the first of them by name");
        d.Attention.Should().NotContain(a => a.ProjectId == noPipeline,
            "a solution with no deployment pipeline has nothing a lapsed secret would stop");
    }

    [Fact]
    public async Task The_activity_feed_merges_builds_and_deployments_newest_first_and_stops_at_ten()
    {
        var s = await SeedDeploymentFleetAsync();
        await using (var ctx = _db.NewContext())
        {
            var pr = new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = s.ProjectId, PipelineId = s.BuildPipelineId,
                Status = ProjectBuildStatus.Ready, Trigger = ProjectBuildTrigger.PullRequest, PullRequestNumber = 42,
                Branch = "feature/x", StartedAt = _now.AddMinutes(-30), FinishedAt = _now.AddMinutes(-20),
            };
            ctx.OeProjectBuilds.Add(pr);
            // A dismissed prepared deployment never happened, so it is not activity.
            ctx.OeProjectDeliveries.Add(Delivery(s.ProjectId, s.Waiting, s.BuildId, ProjectDeliveryStatus.Dismissed,
                _now.AddMinutes(-1), _now.AddMinutes(-1)));
            await ctx.SaveChangesAsync();
        }
        for (var i = 0; i < 8; i++)
        {
            await SeedBuildAsync(s.ProjectId, s.BuildPipelineId, ProjectBuildStatus.Ready, _now.AddDays(-20 - i));
        }

        var d = await GetAsync();

        d.Activity.Should().HaveCount(PipelinesDashboardService.ActivityRows);
        d.Activity.Select(a => a.At).Should().BeInDescendingOrder();
        d.Activity.Should().NotContain(a => a.At > _now.AddMinutes(-2), "the dismissed deployment is left out");
        d.Activity[0].Kind.Should().Be(PipelinesActivityKind.DeploymentRunning, "the live deployment started three minutes ago");
        d.Activity[0].CurrentApp.Should().Be(2);
        d.Activity[0].AppCount.Should().Be(3);
        var prRow = d.Activity[1];
        prRow.Kind.Should().Be(PipelinesActivityKind.BuildSucceeded);
        prRow.Actor.Should().Be(PipelinesActor.PullRequest);
        prRow.PullRequestNumber.Should().Be(42);
        d.Activity.Should().Contain(a => a.Kind == PipelinesActivityKind.DeploymentFailed && a.FailedAppName == "CRONUS Sales");
        d.Activity.Should().Contain(a => a.Kind == PipelinesActivityKind.DeploymentPrepared && a.Actor == PipelinesActor.Pipeline);
    }

    [Fact]
    public async Task A_person_who_started_a_build_is_named_on_its_row()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var pipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        int userId;
        await using (var ctx = _db.NewContext())
        {
            var user = new User
            {
                OrganizationId = TestDb.DefaultOrgId, Email = "kj@example.com", DisplayName = "K. Jensen",
                PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = _now,
            };
            ctx.Users.Add(user);
            await ctx.SaveChangesAsync();
            userId = user.Id;
        }
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Building, _now.AddMinutes(-5), startedBy: userId);

        var row = (await GetAsync()).Activity.Should().ContainSingle().Which;

        row.Kind.Should().Be(PipelinesActivityKind.BuildRunning);
        row.Actor.Should().Be(PipelinesActor.Person);
        row.ActorName.Should().Be("K. Jensen");
    }

    [Fact]
    public async Task A_private_solution_the_caller_cannot_see_contributes_nothing()
    {
        var hidden = await SeedSolutionAsync("CRONUS");
        await using (var ctx = _db.NewContext())
        {
            var p = await ctx.OeProjects.SingleAsync(x => x.Id == hidden);
            p.Visibility = ProjectVisibility.Private;
            await ctx.SaveChangesAsync();
        }
        var pipeline = await SeedBuildPipelineAsync(hidden, "CRONUS Base");
        await SeedBuildAsync(hidden, pipeline, ProjectBuildStatus.Failed, _now.AddHours(-1));
        await SeedDeploymentPipelineAsync(hidden);
        _db.OrgContext.IsSiteAdmin = false;

        var d = await GetAsync();

        d.BuildPipelineCount.Should().Be(0);
        d.DeploymentPipelineCount.Should().Be(0);
        d.Attention.Should().BeEmpty();
        d.Activity.Should().BeEmpty();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<PipelinesDashboardData> GetAsync()
    {
        await using var ctx = _db.NewContext();
        var access = new ProjectAccess(ctx, _db.OrgContext);
        var service = new PipelinesDashboardService(
            ctx, access,
            new ReleasePipelineService(ctx, _db.OrgContext, access, _db.NewToolEnablement(ctx), NullLogger<ReleasePipelineService>.Instance),
            new DeliveryFeedService(ctx, access),
            new BuildFreshnessService(ctx, access),
            new DisplayTimeZone(_db.NewContextFactory(), _db.OrgContext, NullLogger<DisplayTimeZone>.Instance),
            TimeProvider.System);
        return await service.GetAsync();
    }

    private async Task<int> SeedSolutionAsync(
        string name, bool tenant = false, bool ownClient = false, DateTime? ownExpires = null)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedAt = _now, UpdatedAt = _now,
            BcTenantId = tenant ? Guid.Parse("22222222-2222-2222-2222-222222222222") : null,
            BcClientId = ownClient ? "33333333-3333-3333-3333-333333333333" : null,
            BcClientSecretExpiresAt = ownExpires,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> SeedBuildPipelineAsync(int projectId, string name)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, CreatedAt = _now, UpdatedAt = _now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private async Task<int> SeedBuildAsync(
        int projectId, int pipelineId, string status, DateTime startedAt,
        string? branch = null, string? failure = null, int? startedBy = null)
    {
        await using var ctx = _db.NewContext();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId, Status = status,
            Branch = branch, FailureMessage = failure, StartedByUserId = startedBy, StartedAt = startedAt,
            FinishedAt = status is ProjectBuildStatus.Ready or ProjectBuildStatus.Failed ? startedAt.AddMinutes(4) : null,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private async Task<int> SeedDeploymentPipelineAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Main", CreatedAt = _now, UpdatedAt = _now,
        };
        var env = Environment(projectId, "Production", "Production");
        ctx.OePipelines.Add(pipeline);
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        var rp = ReleasePipeline(projectId, "To Production", pipeline.Id, env.Id);
        ctx.OeReleasePipelines.Add(rp);
        await ctx.SaveChangesAsync();
        return rp.Id;
    }

    private sealed record Fleet(
        int ProjectId, int BuildPipelineId, int BuildId, int Live, int Failed, int Waiting, int Deleting, int Quiet);

    /// <summary>
    /// Five deployment pipelines into one solution: one installing app 2 of 3 now, one whose
    /// last deployment failed on its second app, one holding a prepared deployment, one aimed
    /// at an environment Business Central is deleting, and one never used.
    /// </summary>
    private async Task<Fleet> SeedDeploymentFleetAsync()
    {
        var projectId = await SeedSolutionAsync("CRONUS");
        var buildPipeline = await SeedBuildPipelineAsync(projectId, "CRONUS Base");
        var buildId = await SeedBuildAsync(projectId, buildPipeline, ProjectBuildStatus.Ready, _now.AddDays(-4));

        await using var ctx = _db.NewContext();
        var uat = Environment(projectId, "UAT", "Sandbox");
        var test = Environment(projectId, "Test", "Sandbox");
        var sandbox = Environment(projectId, "Sandbox", "Sandbox");
        var deleting = Environment(projectId, "Old UAT", "Sandbox");
        deleting.Status = "SoftDeleting";
        var production = Environment(projectId, "Production", "Production");
        ctx.OeProjectEnvironments.AddRange(uat, test, sandbox, deleting, production);
        await ctx.SaveChangesAsync();

        var live = ReleasePipeline(projectId, "CRONUS to UAT", buildPipeline, uat.Id);
        var failed = ReleasePipeline(projectId, "CRONUS to Test", buildPipeline, test.Id);
        var waiting = ReleasePipeline(projectId, "CRONUS to Sandbox", buildPipeline, sandbox.Id);
        var gone = ReleasePipeline(projectId, "CRONUS to Old UAT", buildPipeline, deleting.Id);
        var quiet = ReleasePipeline(projectId, "CRONUS to Production", buildPipeline, production.Id);
        ctx.OeReleasePipelines.AddRange(live, failed, waiting, gone, quiet);
        await ctx.SaveChangesAsync();

        ctx.OeProjectDeliveries.AddRange(
            Delivery(projectId, live.Id, buildId, ProjectDeliveryStatus.Installing, _now.AddMinutes(-3), null,
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Installing, ProjectDeliveryResultStatus.Pending),
            Delivery(projectId, failed.Id, buildId, ProjectDeliveryStatus.Failed, _now.AddHours(-5), _now.AddHours(-5).AddMinutes(2),
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed),
            Delivery(projectId, waiting.Id, buildId, ProjectDeliveryStatus.Proposed, _now.AddDays(-3), null));
        await ctx.SaveChangesAsync();

        return new Fleet(projectId, buildPipeline, buildId, live.Id, failed.Id, waiting.Id, gone.Id, quiet.Id);
    }

    private OeProjectEnvironment Environment(int projectId, string name, string type) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = type,
        Status = "Active", FetchedAt = _now,
    };

    private OeReleasePipeline ReleasePipeline(int projectId, string name, int buildPipelineId, int environmentId) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name,
        BuildPipelineId = buildPipelineId, ProjectEnvironmentId = environmentId,
        DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
        CreatedAt = _now, UpdatedAt = _now,
    };

    private static OeProjectDelivery Delivery(
        int projectId, int releasePipelineId, int buildId, string status, DateTime at, DateTime? finishedAt,
        params string[] apps)
    {
        var running = status is not (ProjectDeliveryStatus.Proposed or ProjectDeliveryStatus.Dismissed);
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, ReleasePipelineId = releasePipelineId,
            ProjectBuildId = buildId, EnvironmentName = "UAT", Status = status,
            ScheduledFor = at, StartedAt = running ? at : null, FinishedAt = finishedAt,
            CreatedAt = at, UpdatedAt = finishedAt ?? at,
        };
        string[] names = ["CRONUS Base", "CRONUS Sales", "CRONUS Reports"];
        for (var i = 0; i < apps.Length; i++)
        {
            delivery.Results.Add(new OeProjectDeliveryResult
            {
                OrganizationId = TestDb.DefaultOrgId, Ordering = i, AppName = names[i], AppVersion = "1.0.0.0",
                Status = apps[i], CreatedAt = at, UpdatedAt = at,
            });
        }
        return delivery;
    }
}
