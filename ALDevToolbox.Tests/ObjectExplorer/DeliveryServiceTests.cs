using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Notifications;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// Create + run for <see cref="DeliveryService"/> against the shared <see cref="TestDb"/>,
/// driving the publish orchestration through fake <see cref="IBcAppManagementClient"/>
/// and <see cref="IDeliveryTokenSource"/> seams (no real BC). Covers the snapshot at
/// creation, the validation guards, the happy-path upload→install→poll in dependency
/// order, partial failure (fail + skip the rest), a clean token failure, the claim
/// no-op, and the rules that exist only because Business Central does its own
/// scheduling: a deferred install is handed off rather than watched, and it is refused
/// where BC wouldn't honour our ordering. See <c>.design/saas-delivery.md</c>.
/// </summary>
public sealed class DeliveryServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppManagementClient _apps = new();
    private readonly FakeTokenSource _tokens = new();
    private readonly FakeAdminClient _admin = new();
    private readonly DeliveryQueue _queue = new();

    public DeliveryServiceTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ReleaseBuildNowAsync_creates_delivery_with_snapshot_and_pending_results()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.EnvironmentName.Should().Be("Production");
        delivery.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        delivery.SchemaSyncMode.Should().Be(BcSyncMode.Add);
        delivery.Results.Should().HaveCount(2);
        delivery.Results.Select(r => r.AppName).Should().Equal("CRONUS Core", "CRONUS Sales");
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Pending);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_non_successful_build()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, buildStatus: ProjectBuildStatus.Failed);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    [Theory]
    [InlineData(ProjectBuildTarget.NextMinor)]
    [InlineData(ProjectBuildTarget.NextMajor)]
    public async Task ReleaseBuildNowAsync_refuses_a_preview_build(string target)
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakePreviewAsync(ctx, seed.BuildId, target);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var errors = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors;
        errors.Should().ContainKey("Build");
        errors["Build"].Should().Contain("preview build");
        (await _db.NewContext().OeProjectDeliveries.AnyAsync(d => d.ProjectBuildId == seed.BuildId)).Should().BeFalse();
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_refuses_a_preview_build_for_later_too()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakePreviewAsync(ctx, seed.BuildId, ProjectBuildTarget.NextMajor);

        var act = () => NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddDays(1));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_an_environment_that_is_upgrading()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, "Upgrading"));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironment"];
        error.Should().Contain("Upgrading", "the refusal names the status the consultant will see in Business Central");
    }

    [Fact]
    public async Task RunDeliveryAsync_fails_the_run_when_the_environment_started_upgrading_after_it_was_scheduled()
    {
        int deliveryId;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            // Active at scheduling time - the cached-status gate lets this through.
            deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        }

        // ...and an update lands in Business Central before the worker picks it up.
        _admin.OnGet = name => new BcEnvironment(name, "Production") { Status = "Upgrading" };

        await using (var run = _db.NewContext())
        {
            await NewService(run).RunDeliveryAsync(deliveryId);
        }

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("Upgrading");
        _admin.Requested.Should().Contain("Production", "the run re-reads the environment before uploading");
        _apps.UploadedOrder.Should().BeEmpty("nothing may be uploaded to an environment that can't take it");

        // The page shouldn't keep showing the status the delivery just contradicted.
        var env = await read.OeProjectEnvironments.AsNoTracking().SingleAsync(e => e.Name == "Production" && e.ProjectId == delivery.ProjectId);
        env.Status.Should().Be("Upgrading");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_build_from_a_different_build_pipeline()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        // A second build pipeline in the same project, with its own successful build.
        var otherPipeline = await SeedPipelineAsync(ctx, seed.ProjectId);
        var otherBuild = await SeedBuildAsync(ctx, seed.ProjectId, otherPipeline, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, otherBuild);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    /// <summary>
    /// A release pipeline that draws from a repository's GitHub releases takes the
    /// build that was staged from one - no pipeline of its own, the tag recorded on it -
    /// and nothing else. See <c>.design/github-integration-phase2.md</c> (#632).
    /// </summary>
    [Fact]
    public async Task ReleaseBuildNowAsync_accepts_a_build_staged_from_a_github_release()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
        var staged = await SeedStagedBuildAsync(ctx, seed.ProjectId, "v1.0.0.0", new[] { "CRONUS Core" });

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, staged);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId);
        delivery.ProjectBuildId.Should().Be(staged);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_staged_build_on_a_pipeline_that_releases_builds()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var staged = await SeedStagedBuildAsync(ctx, seed.ProjectId, "v1.0.0.0", new[] { "CRONUS Core" });

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, staged);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Build");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_rejects_a_pipeline_build_on_a_release_sourced_pipeline()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Build"].Should().Contain("GitHub releases");
    }

    [Fact]
    public async Task RunDeliveryAsync_publishes_all_apps_in_order_and_marks_deployed()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "succeeded";
        _apps.StatusByApp["CRONUS Reports"] = "succeeded";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        (await NewService(runCtx).RunDeliveryAsync(deliveryId)).Should().BeTrue();

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        delivery.ClaimedAt.Should().NotBeNull();
        delivery.StartedAt.Should().NotBeNull();
        delivery.FinishedAt.Should().NotBeNull();
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Completed);
        delivery.Results.Should().OnlyContain(r => r.OperationId != null, "the operation id is what the poll and the admin center key on");
        delivery.Results.Should().OnlyContain(r => r.AppId != null, "BC reads the app id out of the uploaded package");
        // One upload triggered per app, in dependency (stored) order.
        _apps.UploadedOrder.Should().Equal("CRONUS Core", "CRONUS Sales", "CRONUS Reports");
    }

    [Fact]
    public async Task RunDeliveryAsync_marks_failed_and_skips_remaining_when_an_install_fails()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "failed";
        _apps.StatusByApp["CRONUS Reports"] = "succeeded";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("CRONUS Sales");
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Completed);
        results[1].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[2].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        // The failed app's dependent was never triggered.
        _apps.UploadedOrder.Should().Equal("CRONUS Core", "CRONUS Sales");
    }

    [Fact]
    public async Task RunDeliveryAsync_fails_cleanly_when_the_token_cannot_be_acquired()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _tokens.Throw = new BcApiException(null, "The Business Central client secret has expired. Rotate it in Entra and re-enter it before releasing.");

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("expired");
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
        _apps.UploadedOrder.Should().BeEmpty(); // never reached the publish
    }

    [Fact]
    public async Task RunDeliveryAsync_is_a_noop_when_the_delivery_is_not_scheduled()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        // Simulate another worker already past the scheduled state.
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Deployed));

        await using var runCtx = _db.NewContext();
        (await NewService(runCtx).RunDeliveryAsync(deliveryId)).Should().BeFalse("this run claimed nothing, so nothing is announced");

        _apps.UploadedOrder.Should().BeEmpty(); // the claim CAS found it already taken
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status
            .Should().Be(ProjectDeliveryStatus.Deployed);
    }

    [Fact]
    public async Task ListDeliveryHistoryAsync_returns_deliveries_with_their_app_rows()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        _apps.StatusByApp["CRONUS Core"] = "succeeded";
        _apps.StatusByApp["CRONUS Sales"] = "succeeded";
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var runCtx = _db.NewContext()) await NewService(runCtx).RunDeliveryAsync(deliveryId);

        var history = await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId);

        var row = history.Should().ContainSingle().Subject;
        row.Id.Should().Be(deliveryId);
        row.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        row.IsLive.Should().BeFalse();
        row.Apps.Select(a => a.AppName).Should().Equal("CRONUS Core", "CRONUS Sales");
        row.Apps.Should().OnlyContain(a => a.Status == ProjectDeliveryResultStatus.Completed);
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_for_a_future_time_stays_scheduled_and_is_not_enqueued()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });

        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(3));

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status
            .Should().Be(ProjectDeliveryStatus.Scheduled);
        _queue.Reader.TryRead(out _).Should().BeFalse("a future delivery is left for the scheduler, not enqueued now");
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_due_now_is_enqueued_immediately()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });

        await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow);

        _queue.Reader.TryRead(out _).Should().BeTrue("a release due now is enqueued straight away");
    }

    [Fact]
    public async Task ScheduleDeliveryAsync_flags_a_time_outside_the_window()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await SetWindowAsync(ctx, seed.EnvironmentId, new TimeOnly(22, 0), new TimeOnly(6, 0)); // UTC project tz

        // 12:00 UTC tomorrow is outside a 22:00–06:00 window.
        var outside = new DateTime(DateTime.UtcNow.Year, 1, 2, 12, 0, 0, DateTimeKind.Utc).AddYears(1);
        var insideId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId,
            new DateTime(outside.Year, outside.Month, outside.Day, 23, 0, 0, DateTimeKind.Utc));
        var outsideId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, outside);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == insideId)).ScheduledOutsideWindow.Should().BeFalse();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == outsideId)).ScheduledOutsideWindow.Should().BeTrue();
    }

    [Fact]
    public async Task A_delivery_window_pipeline_records_Immediate_as_sent_and_that_its_rule_chose_the_time()
    {
        await using var ctx = _db.NewContext();
        // Two apps: a deferred schedule would be refused, and the window is not one.
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" },
            deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        await SetWindowAsync(ctx, seed.EnvironmentId, new TimeOnly(22, 0), new TimeOnly(4, 0)); // UTC project tz
        var opening = UpdateWindow.NextOpeningUtc(new TimeOnly(22, 0), new TimeOnly(4, 0), TimeZoneInfo.Utc,
            DateTime.UtcNow.AddHours(1));
        var byRule = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, opening.AddMinutes(1));

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == byRule);
        delivery.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate,
            "the window is ours: Business Central is told to install on arrival");
        delivery.ScheduledByDeliveryWindow.Should().BeTrue();
        delivery.ScheduledOutsideWindow.Should().BeFalse();
    }

    [Fact]
    public async Task A_delivery_window_pipeline_released_now_sends_Immediate_and_records_the_override()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        // A one-minute window that has just closed, so "now" is outside it.
        var closed = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(-2));
        await SetWindowAsync(ctx, seed.EnvironmentId, closed, closed.AddMinutes(1));
        _apps.StatusByApp["CRONUS Core"] = "succeeded";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using var runCtx = _db.NewContext();
        await NewService(runCtx).RunDeliveryAsync(deliveryId);

        _apps.LastSchedule.Should().Be(BcDeploymentSchedule.Immediate, "our own value never reaches the API");
        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        delivery.ScheduledByDeliveryWindow.Should().BeTrue();
        delivery.ScheduledOutsideWindow.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueDueDeliveriesAsync_enqueues_due_rows_and_skips_future_ones()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var svc = NewService(ctx);
        await svc.ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        await svc.ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(5));
        DrainQueue();

        // Sweep at now+2h: the first is due, the second isn't.
        var enqueued = await NewService(_db.NewContext()).EnqueueDueDeliveriesAsync(DateTime.UtcNow.AddHours(2));

        enqueued.Should().Be(1);
    }

    [Fact]
    public async Task FailInterruptedDeliveriesAsync_fails_orphaned_in_progress_runs()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        // Simulate a crash mid-publish.
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Uploading));

        var failed = await NewService(_db.NewContext()).FailInterruptedDeliveriesAsync();

        failed.Should().HaveCount(1);
        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.Include(x => x.Results).SingleAsync(x => x.Id == deliveryId);
        d.Status.Should().Be(ProjectDeliveryStatus.Failed);
        d.FailureMessage.Should().Contain("interrupted");
        d.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
    }

    [Fact]
    public async Task CancelDeliveryAsync_cancels_a_scheduled_delivery_but_refuses_a_claimed_one()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var scheduledId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        await NewService(_db.NewContext()).CancelDeliveryAsync(scheduledId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == scheduledId)).Status
            .Should().Be(ProjectDeliveryStatus.Cancelled);

        // A claimed delivery can no longer be cancelled.
        var claimedId = await NewService(_db.NewContext()).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        await ctx.OeProjectDeliveries.Where(d => d.Id == claimedId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed));
        var act = () => NewService(_db.NewContext()).CancelDeliveryAsync(claimedId);
        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Delivery");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_moves_a_scheduled_delivery_to_a_picked_time()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        var newTime = DateTime.UtcNow.AddHours(8);

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.AtTime, newTime);

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        d.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        d.ScheduledFor.Should().BeCloseTo(newTime, TimeSpan.FromSeconds(1));
        d.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        _queue.Reader.TryRead(out _).Should().BeFalse("a later time is left for the scheduler");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_asks_for_a_time_when_none_was_picked()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.AtTime, atUtc: null);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ScheduledFor");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_is_refused_without_manage_rights()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var when = DateTime.UtcNow.AddHours(1);
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, when);
        await ctx.OeProjects.Where(p => p.Id == seed.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, ProjectVisibility.ReadOnly));
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = await SeedUserAsync("Someone else");

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.AtTime, DateTime.UtcNow.AddHours(8));

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        (await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).ScheduledFor
            .Should().BeCloseTo(when, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_refuses_a_picked_time_that_has_gone()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.AtTime, DateTime.UtcNow.AddHours(-2));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ScheduledFor");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_now_queues_it_straight_away_and_records_the_closed_window()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        // A one-minute window twelve hours away is shut now.
        var shut = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(12));
        await SetWindowAsync(ctx, seed.EnvironmentId, shut, shut.AddMinutes(1));
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(12));
        DrainQueue();

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.Now);

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        d.ScheduledFor.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
        d.ScheduledByDeliveryWindow.Should().BeFalse();
        d.ScheduledOutsideWindow.Should().BeTrue();
        _queue.Reader.TryRead(out var job).Should().BeTrue("now means the worker takes it at once");
        job!.DeliveryId.Should().Be(deliveryId);
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_to_the_delivery_window_takes_its_next_opening()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var start = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
        await SetWindowAsync(ctx, seed.EnvironmentId, start, start.AddMinutes(30));
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.DeliveryWindow);

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        TimeOnly.FromDateTime(d.ScheduledFor).Should().BeCloseTo(start, TimeSpan.FromMinutes(1));
        d.ScheduledFor.Should().BeAfter(DateTime.UtcNow);
        d.ScheduledByDeliveryWindow.Should().BeTrue();
        d.ScheduledOutsideWindow.Should().BeFalse();
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_refuses_the_delivery_window_when_the_environment_has_none()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.DeliveryWindow);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Timing");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_to_the_next_minor_update_hands_it_to_business_central_now()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MirrorInstalledAsync(ctx, seed, "CRONUS Core");
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(4));
        DrainQueue();

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.NextMinorUpdate);

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        d.DeploymentSchedule.Should().Be(BcDeploymentSchedule.NextMinorUpdate);
        d.ScheduledOutsideWindow.Should().BeFalse("Business Central's update runs on Microsoft's schedule, not ours");
        _queue.Reader.TryRead(out _).Should().BeTrue("the apps go up now and wait in Business Central");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_refuses_a_later_update_for_an_app_the_environment_does_not_have()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(4));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.NextMajorUpdate);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Timing"].Should().Contain("isn't installed");
        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(deliveryId))!.LaterUpdateUnavailable.Should().Contain("isn't installed");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_refuses_a_later_update_for_several_apps()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        await MirrorInstalledAsync(ctx, seed, "CRONUS Core", "CRONUS Sales");
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(4));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.NextMinorUpdate);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Timing"].Should().Contain("2 apps");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_refuses_a_delivery_that_has_started()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed));

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.Now);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Delivery");
        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(deliveryId)).Should().BeNull();
    }

    [Fact]
    public async Task GetRescheduleOptionsAsync_opens_on_the_delivery_window_for_a_window_booking()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        var start = TimeOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
        await SetWindowAsync(ctx, seed.EnvironmentId, start, start.AddMinutes(30));
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(6).AddMinutes(1));

        var options = await NewService(_db.NewContext()).GetRescheduleOptionsAsync(deliveryId);

        options.Should().NotBeNull();
        options!.CurrentTiming.Should().Be(RescheduleTiming.DeliveryWindow);
        options.NextWindowOpeningUtc.Should().NotBeNull();
        options.LaterUpdateUnavailable.Should().Contain("isn't installed");
    }

    [Fact]
    public async Task RunDeliveryAsync_leaves_a_queued_delivery_alone_once_it_was_moved_to_later()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        // Due now, so it is queued; then moved to tomorrow before the worker gets to it.
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ScheduledFor, DateTime.UtcNow.AddDays(1)));

        var ran = await NewService(_db.NewContext()).RunDeliveryAsync(deliveryId);

        ran.Should().BeFalse("the stale queue entry must not install a deployment booked for tomorrow");
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_back_from_a_later_update_to_a_time_sends_it_immediate_again()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, schemaSyncMode: BcSyncMode.ForceSync);
        await MirrorInstalledAsync(ctx, seed, "CRONUS Core");
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(4));
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeploymentSchedule, BcDeploymentSchedule.NextMajorUpdate));

        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(deliveryId))!.CurrentTiming
            .Should().Be(RescheduleTiming.NextMajorUpdate);
        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.AtTime, DateTime.UtcNow.AddHours(6));

        await using var read = _db.NewContext();
        var d = await read.OeProjectDeliveries.SingleAsync(x => x.Id == deliveryId);
        d.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        d.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync, "a reschedule changes when, not how");
    }

    [Fact]
    public async Task RescheduleDeliveryAsync_to_an_open_delivery_window_queues_it_now()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var now = TimeOnly.FromDateTime(DateTime.UtcNow);
        await SetWindowAsync(ctx, seed.EnvironmentId, now.AddHours(-1), now.AddHours(1));
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(5));

        await NewService(_db.NewContext()).RescheduleDeliveryAsync(deliveryId, RescheduleTiming.DeliveryWindow);

        _queue.Reader.TryRead(out var job).Should().BeTrue("the window is open, so it goes now");
        job!.DeliveryId.Should().Be(deliveryId);
    }

    /// <summary>Gives the build's apps ids and puts them in the environment's app list as last read.</summary>
    private static async Task MirrorInstalledAsync(AppDbContext ctx, Seed seed, params string[] appNames)
    {
        foreach (var name in appNames)
        {
            var appId = Guid.NewGuid();
            await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId && a.AppName == name)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId.ToString()));
            ctx.OeEnvironmentApps.Add(new OeEnvironmentApp
            {
                OrganizationId = TestDb.DefaultOrgId, EnvironmentId = seed.EnvironmentId, AppId = appId,
                Name = name, Publisher = "CRONUS A/S", Version = "0.9.0.0", FetchedAt = DateTime.UtcNow,
            });
        }
        await ctx.SaveChangesAsync();
    }

    // ── Moving a deployment Business Central is holding (#1097) ────────────────

    /// <summary>A one-app deployment handed to Business Central for its next minor update.</summary>
    private async Task<(Seed Seed, int DeliveryId)> HandedOffAsync()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        _apps.Installed.Add(InstalledApp("CRONUS Core"));
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);
        DrainQueue();
        return (seed, deliveryId);
    }

    [Fact]
    public async Task Moving_a_held_deployment_to_now_cancels_business_centrals_copy_and_deploys_it_again()
    {
        var (seed, heldId) = await HandedOffAsync();
        await using (var check = _db.NewContext())
        {
            (await NewService(check).GetRescheduleOptionsAsync(heldId))!.HeldByBusinessCentral.Should().BeTrue();
        }

        var newId = await NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);

        newId.Should().NotBe(heldId, "Business Central's copy can't be moved, only replaced");
        await using var read = _db.NewContext();
        var held = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == heldId);
        var result = held.Results.Single();
        _apps.Removed.Should().ContainSingle().Which.Should().Be(
            (Guid.Parse(result.AppId!), result.AppVersion, BcDeploymentSchedule.NextMinorUpdate));
        held.Status.Should().Be(ProjectDeliveryStatus.Cancelled);
        held.DiagnosticsLog.Should().Contain($"deployment #{newId}");
        result.Status.Should().Be(ProjectDeliveryResultStatus.Skipped);

        var moved = await read.OeProjectDeliveries.SingleAsync(d => d.Id == newId);
        moved.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        moved.ProjectBuildId.Should().Be(seed.BuildId);
        moved.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        moved.ScheduledFor.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        _queue.Reader.TryRead(out var job).Should().BeTrue();
        job!.DeliveryId.Should().Be(newId);
    }

    [Fact]
    public async Task Moving_a_held_deployment_to_a_picked_time_books_the_new_one_for_then()
    {
        var (_, heldId) = await HandedOffAsync();
        var at = DateTime.UtcNow.AddDays(2);

        var newId = await NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.AtTime, at);

        await using var read = _db.NewContext();
        var moved = await read.OeProjectDeliveries.SingleAsync(d => d.Id == newId);
        moved.ScheduledFor.Should().BeCloseTo(at, TimeSpan.FromSeconds(1));
        moved.DeploymentSchedule.Should().Be(BcDeploymentSchedule.Immediate);
        _queue.Reader.TryRead(out _).Should().BeFalse("it isn't due yet");
    }

    [Fact]
    public async Task When_business_central_refuses_the_cancel_nothing_is_moved()
    {
        var (_, heldId) = await HandedOffAsync();
        _apps.RemoveRefusal = "No scheduled operation found.";

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Delivery"].Should().Contain("nothing was changed");
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.CountAsync()).Should().Be(1, "the replacement is removed again");
        (await read.OeProjectDeliveries.SingleAsync()).Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_held_deployment_can_only_be_moved_once()
    {
        var (_, heldId) = await HandedOffAsync();
        await NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.AtTime, DateTime.UtcNow.AddDays(2));

        var again = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);

        await again.Should().ThrowAsync<PlanValidationException>();
        _apps.Removed.Should().ContainSingle();
        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.CountAsync()).Should().Be(2, "one held run and the one that replaced it");
    }

    /// <summary>
    /// The cancel's answer is lost (a timeout, the page closing). Whether the move finishes
    /// is settled by asking Business Central what it still holds, so a dropped copy is
    /// always replaced and a kept one never doubled.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancel_whose_answer_is_lost_is_settled_by_what_business_central_still_holds(bool reachedIt)
    {
        var (_, heldId) = await HandedOffAsync();
        await using (var ctx = _db.NewContext())
        {
            var result = await ctx.OeProjectDeliveryResults.SingleAsync(r => r.ProjectDeliveryId == heldId);
            _apps.Scheduled.Add(ScheduledOperation(Guid.Parse(result.AppId!), "CRONUS Core", result.AppVersion, BcDeploymentSchedule.NextMinorUpdate));
        }
        _apps.RemoveFault = new HttpRequestException("The connection was reset.");
        _apps.RemoveTakesEffect = reachedIt;

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);

        await using var read = _db.NewContext();
        if (reachedIt)
        {
            await act.Should().NotThrowAsync();
            (await read.OeProjectDeliveries.SingleAsync(d => d.Id == heldId)).Status.Should().Be(ProjectDeliveryStatus.Cancelled);
            (await read.OeProjectDeliveries.CountAsync(d => d.Status == ProjectDeliveryStatus.Scheduled)).Should().Be(1);
        }
        else
        {
            await act.Should().ThrowAsync<HttpRequestException>();
            (await read.OeProjectDeliveries.SingleAsync()).Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        }
    }

    [Fact]
    public async Task A_held_deployment_whose_pipeline_now_targets_another_environment_is_not_moved()
    {
        var (seed, heldId) = await HandedOffAsync();
        await using (var ctx = _db.NewContext())
        {
            var sandbox = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "Sandbox", Type = "Sandbox", FetchedAt = DateTime.UtcNow,
            };
            ctx.OeProjectEnvironments.Add(sandbox);
            await ctx.SaveChangesAsync();
            await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ProjectEnvironmentId, sandbox.Id));
        }

        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(heldId)).Should().BeNull();
        (await NewService(_db.NewContext()).WhyNotReschedulableAsync(heldId)).Should().Contain("no longer deploys to Production");
        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);
        await act.Should().ThrowAsync<PlanValidationException>();
        _apps.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_deployment_cannot_be_moved_to_the_update_it_already_waits_for()
    {
        var (_, heldId) = await HandedOffAsync();

        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.NextMinorUpdate);

        await act.Should().ThrowAsync<PlanValidationException>();
        _apps.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_deployment_the_environment_has_installed_is_no_longer_offered_or_moved()
    {
        var (seed, heldId) = await HandedOffAsync();
        await using (var ctx = _db.NewContext())
        {
            var result = await ctx.OeProjectDeliveryResults.SingleAsync(r => r.ProjectDeliveryId == heldId);
            ctx.OeEnvironmentApps.Add(new OeEnvironmentApp
            {
                OrganizationId = TestDb.DefaultOrgId, EnvironmentId = seed.EnvironmentId, AppId = Guid.Parse(result.AppId!),
                Name = "CRONUS Core", Publisher = "CRONUS A/S", Version = result.AppVersion, FetchedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(heldId)).Should().BeNull();
        var act = () => NewService(_db.NewContext()).RescheduleDeliveryAsync(heldId, RescheduleTiming.Now);
        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Delivery"].Should().Contain("isn't holding");
        _apps.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_held_deployment_replaced_by_a_later_run_is_no_longer_offered()
    {
        var (seed, heldId) = await HandedOffAsync();
        await using (var ctx = _db.NewContext())
        {
            var laterId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
            await using var run = _db.NewContext();
            await NewService(run).RunDeliveryAsync(laterId);
        }

        (await NewService(_db.NewContext()).GetRescheduleOptionsAsync(heldId)).Should().BeNull();
    }

    [Fact]
    public async Task ListWaitingDeploymentsAsync_lists_bookings_and_the_deployment_business_central_holds()
    {
        var (seed, heldId) = await HandedOffAsync();
        int laterId;
        await using (var ctx = _db.NewContext())
        {
            laterId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddDays(1));
        }

        var waiting = await NewService(_db.NewContext()).ListWaitingDeploymentsAsync(seed.ProjectId, seed.EnvironmentId);

        waiting.Select(w => (w.DeliveryId, w.HeldByBusinessCentral)).Should().Equal((laterId, false), (heldId, true));
        (await NewService(_db.NewContext()).ListWaitingDeploymentsAsync(seed.ProjectId, seed.EnvironmentId + 1000))
            .Should().BeEmpty("another environment's list holds none of them");
        await using var read = _db.NewContext();
        var result = await read.OeProjectDeliveryResults.SingleAsync(r => r.ProjectDeliveryId == heldId);
        waiting[1].AppId.Should().Be(Guid.Parse(result.AppId!));
        waiting[1].AppVersion.Should().Be(result.AppVersion);
    }

    // ── Deferred installs: Business Central takes over ─────────────────────────

    [Fact]
    public async Task RunDeliveryAsync_hands_a_deferred_install_to_bc_instead_of_waiting_for_it()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        // The app is already there, so BC accepts a deferred schedule for it.
        _apps.Installed.Add(InstalledApp("CRONUS Core"));

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        ProjectDeliveryStatus.IsTerminal(delivery.Status).Should().BeTrue(
            "nothing further happens on our side once BC has scheduled it");
        delivery.FinishedAt.Should().NotBeNull();
        delivery.FailureMessage.Should().BeNull();
        var result = delivery.Results.Single();
        result.Status.Should().Be(ProjectDeliveryResultStatus.Scheduled);
        result.OperationId.Should().NotBeNull("cancelling it in BC later needs the operation");
        _apps.UploadedOrder.Should().Equal("CRONUS Core");
        _apps.LastSchedule.Should().Be(BcDeploymentSchedule.NextMinorUpdate);
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_several_apps_on_a_deferred_schedule()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" },
            deploymentSchedule: BcDeploymentSchedule.NextMajorUpdate);

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        var error = (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["DeploymentSchedule"];
        error.Should().Contain("order",
            "BC picks the install order inside its own window, so our dependency order stops meaning anything");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RunDeliveryAsync_refuses_a_deferred_first_install_of_an_app_bc_has_never_seen()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        // Nothing installed: this is the app's first visit to the environment.

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("CRONUS Core").And.Contain("isn't installed");
        _apps.UploadedOrder.Should().BeEmpty("the rule is checked before anything is uploaded");
    }

    // ── Legacy values from the retired upload API ─────────────────────────────

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_a_pipeline_still_holding_the_old_version_wording()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: "Current Version");

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DeploymentSchedule");
        _apps.UploadedOrder.Should().BeEmpty("the data migration is required, not optional");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_a_pipeline_still_holding_the_spaced_force_sync()
    {
        await using var ctx = _db.NewContext();
        // The App Management API spells it "ForceSync"; the old one had a space.
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, schemaSyncMode: "Force Sync");

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("SchemaSyncMode");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    // ── What actually goes over the wire ──────────────────────────────────────

    [Fact]
    public async Task RunDeliveryAsync_uploads_with_dependency_resolution_on_and_no_language()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.ApplicationFamily, "BusinessCentral"));

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        _apps.LastInstallDependencies.Should().BeTrue(
            "the API defaults it to false, and BC can resolve dependencies it can already see");
        _apps.LastLanguageId.Should().BeEmpty(
            "we have no language concept, and guessing one would set the install locale wrong");
        _apps.LastFamily.Should().Be("BusinessCentral", "the family is whatever the API called it");
        _apps.LastSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task RunDeliveryAsync_records_the_failure_codes_rather_than_the_localized_message()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _apps.StatusByApp["CRONUS Core"] = "failed";

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries
            .Include(d => d.Results)
            .SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        // The codes are the part that means the same thing in every tenant's language.
        delivery.Results.Single().Message.Should()
            .Contain("ExtensionChangeFailed").And.Contain("TenantSyncFailure");
    }

    [Fact]
    public async Task RunDeliveryAsync_stores_one_line_on_the_delivery_and_the_detail_on_the_app()
    {
        // The shape a real failed install produced (#930): the wrapper, then JSON whose
        // message is in the environment's language, quotes escaped.
        const string danish = "Udvidelsen \"CRONUS Core\" kunne ikke installeres, fordi feltet 12 \"Zone Priority\" er fjernet.";
        const string raw = "A request to the Data Plane Admin Service failed. Http status code: BadRequest Error: "
            + "{ \"code\": \"ExtensionChangeFailed\", \"message\": \"Udvidelsen \\\"CRONUS Core\\\" kunne ikke installeres, fordi feltet 12 \\\"Zone Priority\\\" er fjernet.\" }";
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _apps.StatusByApp["CRONUS Core"] = "failed";
        _apps.FailedErrorMessage = raw;

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        var app = delivery.Results.Single();

        delivery.FailureMessage.Should().Be($"Business Central refused a schema change while installing CRONUS Core {app.AppVersion}.",
            "the delivery carries one line; the detail is the app's");
        app.Message.Should().Be(
            "Business Central refused a schema change (a renamed or removed table or field). "
            + "Deploy again with Force sync to push it through, or keep the old names. "
            + "Error code: ExtensionChangeFailed. Business Central's message: " + danish);
        app.Message.Should().NotContain("Data Plane Admin Service").And.NotContain("{");
        // The log keeps the response whole, for support and for the page to read back.
        delivery.DiagnosticsLog.Should().Contain(
            $"FAILED CRONUS Core {app.AppVersion}: Business Central reported the install as failed (ExtensionChangeFailed). {raw}");
    }

    // ── Deferred pre-checks: the app id, and a version already waiting (#936, #937) ──

    [Fact]
    public async Task RunDeliveryAsync_deferred_check_finds_a_renamed_app_by_its_id()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        var appId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId.ToString()));
        // Installed under the name it had before a rename: the same app.
        _apps.Installed.Add(InstalledApp("CRONUS Core (old name)") with { AppId = appId });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId))
            .Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        _apps.UploadedOrder.Should().Equal("CRONUS Core");
    }

    [Fact]
    public async Task RunDeliveryAsync_deferred_check_does_not_take_another_app_with_the_same_name_for_this_one()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, Guid.NewGuid().ToString()));
        // Another publisher's app that happens to share the name.
        _apps.Installed.Add(InstalledApp("CRONUS Core"));
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("isn't installed");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RunDeliveryAsync_deferred_check_falls_back_to_the_name_when_the_artifact_has_no_id()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        // The seeded artifact carries no app id, as one written before #922 doesn't.
        _apps.Installed.Add(InstalledApp("cronus core"));
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId))
            .Status.Should().Be(ProjectDeliveryStatus.HandedOff);
    }

    [Fact]
    public async Task RunDeliveryAsync_refuses_a_version_already_waiting_for_the_same_schedule_and_skips_the_rest()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports" });
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var artifacts = await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId).OrderBy(a => a.Id).ToListAsync();
        for (var i = 0; i < artifacts.Count; i++)
        {
            artifacts[i].AppId = ids[i].ToString();
            _apps.Installed.Add(InstalledApp(artifacts[i].AppName) with { AppId = ids[i] });
        }
        await ctx.SaveChangesAsync();
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        // A several-app delivery on a deferred schedule can't be created any more, but one
        // from before that rule can still run.
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeploymentSchedule, BcDeploymentSchedule.NextMinorUpdate));
        // Sales 1.0.1.0 was released for the next minor update once already.
        _apps.Scheduled.Add(ScheduledOperation(ids[1], "CRONUS Sales", "1.0.1.0", BcDeploymentSchedule.NextMinorUpdate));

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        const string sentence = "CRONUS Sales 1.0.1.0 is already waiting for the next minor update on Production; cancel it there first.";
        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(sentence);
        delivery.DiagnosticsLog.Should().Contain(sentence);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Scheduled);
        results[1].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[1].Message.Should().Be(sentence);
        results[2].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        _apps.UploadedOrder.Should().Equal(new[] { "CRONUS Core" }, "the refused version never reaches Business Central");
    }

    // ── Older than what is installed ──────────────────────────────────────────

    [Fact]
    public async Task ReleaseBuildNowAsync_refuses_a_build_older_than_the_environment_has()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var appId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId.ToString()));
        // The environment's app list as last read: Core is already at a newer version.
        ctx.OeEnvironmentApps.Add(new OeEnvironmentApp
        {
            OrganizationId = TestDb.DefaultOrgId, EnvironmentId = seed.EnvironmentId, AppId = appId,
            Name = "CRONUS Core", Publisher = "CRONUS A/S", Version = "1.0.10.0", FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Build"].Should()
            .StartWith($"CRONUS Core 1.0.0.0 in build #{seed.BuildId} is older than 1.0.10.0, which is already installed in Production.");
    }

    [Fact]
    public async Task ReleaseBuildNowAsync_compares_versions_by_number_not_as_text()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var appId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AppId, appId.ToString())
                .SetProperty(a => a.AppVersion, "1.0.10.0"));
        // "1.0.9.0" sorts after "1.0.10.0" as text, but it is the older version.
        ctx.OeEnvironmentApps.Add(new OeEnvironmentApp
        {
            OrganizationId = TestDb.DefaultOrgId, EnvironmentId = seed.EnvironmentId, AppId = appId,
            Name = "CRONUS Core", Publisher = "CRONUS A/S", Version = "1.0.9.0", FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        deliveryId.Should().BePositive();
    }

    [Fact]
    public async Task RunDeliveryAsync_refuses_an_app_older_than_the_live_environment_has_and_skips_the_rest()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        var salesId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId && a.AppName == "CRONUS Sales")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, salesId.ToString()));
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        // Installed since the deployment was made: Business Central would refuse Sales 1.0.1.0.
        // It is the second app, so a check made app by app would already have sent Core.
        _apps.Installed.Add(InstalledApp("CRONUS Sales") with { AppId = salesId, Version = "1.0.5.0" });

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().StartWith("CRONUS Sales 1.0.1.0 is older than 1.0.5.0, which is already installed in Production.");
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        results[1].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        _apps.UploadedOrder.Should().BeEmpty("nothing is sent once any app would be refused");
    }

    // ── Apps carried over unchanged from an earlier build (#1094) ──────────────

    [Fact]
    public async Task An_unchanged_app_with_a_newer_version_installed_is_skipped_rather_than_refused()
    {
        // Another pipeline of the solution put a newer Core into the environment. This
        // build did not change Core, so there is nothing to refuse: it is left alone.
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        var coreId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId && a.AppName == "CRONUS Core")
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AppId, coreId.ToString())
                .SetProperty(a => a.CarriedFromBuildId, seed.BuildId - 1));
        ctx.OeEnvironmentApps.Add(new OeEnvironmentApp
        {
            OrganizationId = TestDb.DefaultOrgId, EnvironmentId = seed.EnvironmentId, AppId = coreId,
            Name = "CRONUS Core", Publisher = "CRONUS A/S", Version = "1.0.10.0", FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { AppId = coreId, Version = "1.0.10.0" });

        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().NotBe(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        results[0].Message.Should().Be("Unchanged; Production already has the newer 1.0.10.0.");
        _apps.UploadedOrder.Should().Equal(new[] { "CRONUS Sales" });
    }

    [Fact]
    public async Task An_unchanged_app_already_waiting_for_the_update_is_skipped_and_the_rest_go_ahead()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var artifacts = await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId).OrderBy(a => a.Id).ToListAsync();
        for (var i = 0; i < artifacts.Count; i++)
        {
            artifacts[i].AppId = ids[i].ToString();
            _apps.Installed.Add(InstalledApp(artifacts[i].AppName) with { AppId = ids[i], Version = "0.9.0.0" });
        }
        artifacts[0].CarriedFromBuildId = seed.BuildId - 1;
        await ctx.SaveChangesAsync();
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeploymentSchedule, BcDeploymentSchedule.NextMinorUpdate));
        // The earlier build's deployment already queued Core at this version.
        _apps.Scheduled.Add(ScheduledOperation(ids[0], "CRONUS Core", artifacts[0].AppVersion, BcDeploymentSchedule.NextMinorUpdate));

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().NotBe(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        results[0].Message.Should().Be("Unchanged; this version is already waiting for the update.");
        _apps.UploadedOrder.Should().Equal(new[] { "CRONUS Sales" });
    }

    [Fact]
    public async Task RunDeliveryAsync_does_not_take_another_publishers_newer_app_with_the_same_name_as_this_one()
    {
        // An artifact retained before app ids were stamped can only be matched by name,
        // and a name can belong to another publisher's app: that is no reason to refuse.
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { Version = "9.0.0.0" });

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).Status.Should().Be(ProjectDeliveryStatus.Deployed);
        _apps.UploadedOrder.Should().Equal("CRONUS Core");
    }

    // ── The branch rule ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("main", "main", true)]
    [InlineData(null, null, true)]
    [InlineData("main", "test/new-posting", false)]
    [InlineData(null, "test/new-posting", false)]
    [InlineData("main", null, false)]
    public async Task A_pipeline_with_a_branch_rule_only_deploys_builds_from_that_branch(
        string? allowed, string? built, bool accepted)
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RestrictBranch, true).SetProperty(r => r.AllowedBranch, allowed));
        await ctx.OeProjectBuilds.Where(b => b.Id == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Branch, built));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        if (accepted)
        {
            await act.Should().NotThrowAsync();
        }
        else
        {
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["Build"].Should()
                .Contain($"Build #{seed.BuildId} was built from").And.Contain("this deployment pipeline only deploys builds from");
        }
    }

    [Fact]
    public async Task Without_the_branch_rule_a_build_from_any_branch_deploys()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjectBuilds.Where(b => b.Id == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Branch, "test/new-posting"));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_branch_rule_does_not_apply_to_a_pipeline_that_installs_github_releases()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await MakeReleaseSourcedAsync(ctx, seed.ReleasePipelineId);
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RestrictBranch, true).SetProperty(r => r.AllowedBranch, "main"));
        var stagedId = await SeedStagedBuildAsync(ctx, seed.ProjectId, "v1.0.0.0", new[] { "CRONUS Core" });

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, stagedId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunDeliveryAsync_does_not_refuse_a_version_waiting_for_a_different_schedule()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        var appId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId.ToString()));
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { AppId = appId });
        _apps.Scheduled.Add(ScheduledOperation(appId, "CRONUS Core", "1.0.0.0", BcDeploymentSchedule.NextMajorUpdate));
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId))
            .Status.Should().Be(ProjectDeliveryStatus.HandedOff);
        _apps.UploadedOrder.Should().Equal("CRONUS Core");
    }

    // ── Release again, and the apps already on the version (#931) ─────────────

    /// <summary>A release of <paramref name="appNames"/> that failed on <paramref name="failOn"/>.</summary>
    private async Task<(Seed Seed, int DeliveryId)> FailedReleaseAsync(string[] appNames, string failOn)
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames);
        _apps.StatusByApp[failOn] = "failed";
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);
        DrainQueue();
        _apps.StatusByApp.Remove(failOn);
        _apps.UploadedOrder.Clear();
        return (seed, deliveryId);
    }

    [Fact]
    public async Task ReleaseAgainAsync_with_force_sync_once_snapshots_it_on_the_new_delivery_only()
    {
        var (seed, failedId) = await FailedReleaseAsync(new[] { "CRONUS Core" }, failOn: "CRONUS Core");

        var againId = await NewService(_db.NewContext()).ReleaseAgainAsync(failedId, forceSyncOnce: true);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(againId);

        await using var read = _db.NewContext();
        var again = await read.OeProjectDeliveries.SingleAsync(d => d.Id == againId);
        again.Id.Should().NotBe(failedId);
        again.ProjectBuildId.Should().Be(seed.BuildId);
        again.ReleasePipelineId.Should().Be(seed.ReleasePipelineId);
        again.SchemaSyncMode.Should().Be(BcSyncMode.ForceSync);
        again.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        _apps.LastSyncMode.Should().Be(BcSyncMode.ForceSync, "the upload is sent with the one-time mode");
        again.DiagnosticsLog.Should().Contain("Schema sync: Force sync, this deployment only.");
        (await read.OeReleasePipelines.SingleAsync(r => r.Id == seed.ReleasePipelineId))
            .SchemaSyncMode.Should().Be(BcSyncMode.Add, "the pipeline's own setting is untouched");

        // And the release after it is back on the pipeline's mode.
        var nextId = await NewService(_db.NewContext()).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        (await read.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == nextId))
            .SchemaSyncMode.Should().Be(BcSyncMode.Add);
    }

    [Fact]
    public async Task ReleaseAgainAsync_without_force_sync_keeps_the_pipelines_mode()
    {
        var (_, failedId) = await FailedReleaseAsync(new[] { "CRONUS Core" }, failOn: "CRONUS Core");

        var againId = await NewService(_db.NewContext()).ReleaseAgainAsync(failedId, forceSyncOnce: false);

        await using var read = _db.NewContext();
        var again = await read.OeProjectDeliveries.SingleAsync(d => d.Id == againId);
        again.SchemaSyncMode.Should().Be(BcSyncMode.Add);
        again.Status.Should().Be(ProjectDeliveryStatus.Scheduled, "it is queued to run now");
    }

    [Fact]
    public async Task ReleaseAgainAsync_refuses_a_release_that_did_not_fail()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var scheduledId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        var act = () => NewService(_db.NewContext()).ReleaseAgainAsync(scheduledId, forceSyncOnce: true);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Delivery"].Should().Contain("Only a failed deployment");
    }

    [Fact]
    public async Task RunDeliveryAsync_skips_an_app_already_on_the_version_and_installs_the_next()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Base", "CRONUS Core" });
        var baseId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId && a.AppName == "CRONUS Base")
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, baseId.ToString()));
        // Base went in on the release that failed on Core; Core is still on the old one.
        _apps.Installed.Add(InstalledApp("CRONUS Base") with { AppId = baseId, Version = "1.0.0.0" });
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { Version = "0.9.0.0" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Skipped);
        results[0].Message.Should().Be("Already on 1.0.0.0.");
        results[1].Status.Should().Be(ProjectDeliveryResultStatus.Completed);
        _apps.UploadedOrder.Should().Equal(new[] { "CRONUS Core" }, "Business Central would refuse a version it already has");
    }

    [Fact]
    public async Task RunDeliveryAsync_with_every_app_already_on_the_version_uploads_nothing_and_is_deployed()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" },
            deploymentSchedule: BcDeploymentSchedule.NextMinorUpdate);
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { Version = "1.0.0.0" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed, "nothing was handed to Business Central to install later");
        delivery.DiagnosticsLog.Should().Contain("nothing to install");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    // ── What the run records for the release page (#929) ──────────────────────

    [Fact]
    public async Task RunDeliveryAsync_records_previous_versions_per_app_timings_and_when_installing_began()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        // The environment has an older Core already; Sales is new to it.
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { Version = "0.9.0.0" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == deliveryId);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].PreviousVersion.Should().Be("0.9.0.0", "the run read what was installed before its first upload");
        results[1].PreviousVersion.Should().BeNull("an app the environment never had has nothing to move from");
        foreach (var r in results)
        {
            r.StartedAt.Should().NotBeNull();
            r.FinishedAt.Should().NotBeNull();
            r.FinishedAt.Should().BeOnOrAfter(r.StartedAt!.Value);
        }
        delivery.InstallStartedAt.Should().NotBeNull("the first upload was accepted and installing began");
        delivery.InstallStartedAt.Should().BeOnOrAfter(delivery.StartedAt!.Value).And.BeOnOrBefore(delivery.FinishedAt!.Value);
    }

    [Fact]
    public async Task RunDeliveryAsync_matches_the_previous_version_on_the_app_id_before_the_name()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var appId = Guid.NewGuid();
        await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.AppId, appId.ToString()));
        // The same app under the name it had before a rename, and an unrelated app that
        // happens to carry today's name: the id decides.
        _apps.Installed.Add(InstalledApp("CRONUS Core (old name)") with { AppId = appId, Version = "0.8.0.0" });
        _apps.Installed.Add(InstalledApp("CRONUS Core") with { Version = "0.1.0.0" });
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveryResults.SingleAsync(r => r.ProjectDeliveryId == deliveryId))
            .PreviousVersion.Should().Be("0.8.0.0");
    }

    [Fact]
    public async Task CancelDeliveryAsync_records_who_cancelled_and_the_history_names_them()
    {
        const int userId = 73_001;
        await using (var users = _db.NewContext())
        {
            users.Users.Add(new ALDevToolbox.Domain.Entities.User
            {
                Id = userId, OrganizationId = TestDb.DefaultOrgId, Email = "k.jensen@example.com",
                PasswordHash = "x", DisplayName = "K. Jensen",
                Role = ALDevToolbox.Domain.Entities.UserRole.Editor, Status = ALDevToolbox.Domain.Entities.UserStatus.Active,
            });
            await users.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = userId;
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));

        await NewService(_db.NewContext()).CancelDeliveryAsync(deliveryId);

        await using var read = _db.NewContext();
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == deliveryId)).CancelledByUserId.Should().Be(userId);
        var row = (await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId)).Single();
        row.CancelledByName.Should().Be("K. Jensen");
    }

    [Fact]
    public async Task ListDeliveryHistoryAsync_numbers_releases_per_pipeline_and_takes_the_newest_first()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var ids = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await NewService(_db.NewContext()).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1 + i)));
        }

        var newestTwo = await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId, 2);
        var all = await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId);

        newestTwo.Select(r => r.Id).Should().Equal(ids[2], ids[1]);
        newestTwo.Select(r => r.Number).Should().Equal(3, 2);
        all.Select(r => r.Number).Should().Equal(3, 2, 1);
        all.Should().OnlyContain(r => r.DeploymentSchedule == BcDeploymentSchedule.Immediate && r.SchemaSyncMode == BcSyncMode.Add);
    }

    [Fact]
    public async Task GetSkipReasonsAsync_names_the_skipped_apps_that_depend_on_the_failed_one()
    {
        await using var ctx = _db.NewContext();
        var names = new[] { "CRONUS Core", "CRONUS Sales", "CRONUS Reports", "CRONUS Tools", "CRONUS Extras" };
        var seed = await SeedAsync(ctx, appNames: names);
        var ids = names.Select(_ => Guid.NewGuid()).ToArray();
        // Reports needs Sales, Tools needs Reports (so Sales through it), Extras needs
        // only Core - it was skipped by the run's rule, not because it needed Sales.
        var deps = new (string Id, string Name, string Version)[][]
        {
            [],
            [(ids[0].ToString(), names[0], "1.0.0.0")],
            [(ids[1].ToString(), names[1], "1.0.1.0")],
            [(ids[2].ToString(), names[2], "1.0.2.0")],
            [(ids[0].ToString(), names[0], "1.0.0.0")],
        };
        var artifacts = await ctx.OeProjectBuildArtifacts.Where(a => a.ProjectBuildId == seed.BuildId).OrderBy(a => a.Id).ToListAsync();
        for (var i = 0; i < artifacts.Count; i++)
        {
            artifacts[i].AppId = ids[i].ToString();
            artifacts[i].Content = SyntheticApp.Build(ids[i].ToString(), names[i], "CRONUS", artifacts[i].AppVersion, deps[i]);
        }
        await ctx.SaveChangesAsync();
        _apps.StatusByApp["CRONUS Sales"] = "failed";
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        var reasons = await NewService(_db.NewContext()).GetSkipReasonsAsync(deliveryId);

        reasons.Should().NotBeNull();
        reasons!.FailedAppName.Should().Be("CRONUS Sales");
        reasons.FailedAppVersion.Should().Be("1.0.1.0");
        reasons.DependentOrderings.Should().BeEquivalentTo(new[] { 2, 3 });

        await using var read = _db.NewContext();
        var skipped = await read.OeProjectDeliveryResults
            .Where(r => r.ProjectDeliveryId == deliveryId && r.Status == ProjectDeliveryResultStatus.Skipped).ToListAsync();
        skipped.Should().HaveCount(3).And.OnlyContain(r => r.StartedAt == null && r.FinishedAt == null,
            "an app that was never attempted has no timings to show");
    }

    [Fact]
    public async Task GetSkipReasonsAsync_says_nothing_about_dependencies_when_the_manifests_cannot_be_read()
    {
        await using var ctx = _db.NewContext();
        // The seed's artifacts are three bytes each: no manifest to read.
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        _apps.StatusByApp["CRONUS Core"] = "failed";
        var deliveryId = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(deliveryId);

        var reasons = await NewService(_db.NewContext()).GetSkipReasonsAsync(deliveryId);

        reasons!.FailedAppName.Should().Be("CRONUS Core");
        reasons.DependentOrderings.Should().BeNull("the page then says only that the app was skipped after the failure");
    }

    // An older version than any seeded build carries: an app already on the build's
    // version is skipped by the run (#931), which the tests using this don't mean.
    private static BcInstalledApp InstalledApp(string name) => new(
        AppId: Guid.NewGuid(), Name: name, Publisher: "CRONUS A/S", Version: "0.9.0.0",
        State: "Installed", AppType: "tenant", CanBeUninstalled: true,
        LastOperationId: null, LastUpdateAttemptResult: string.Empty);

    private static BcScheduledPteOperation ScheduledOperation(Guid appId, string name, string version, string schedule) => new(
        Id: Guid.NewGuid(), AppId: appId, Type: "install", Status: BcAppOperationStatus.Scheduled, RawStatus: "scheduled",
        TargetAppVersion: version, ScheduleKind: schedule, Name: name, Publisher: "CRONUS A/S",
        SyncMode: BcSyncMode.Add, LanguageId: string.Empty, CreatedOn: DateTimeOffset.UtcNow);

    // ── Prepared releases (#934) ──────────────────────────────────────────────

    [Fact]
    public async Task ProposeReleasesForBuildAsync_prepares_a_proposed_release_that_sends_nothing()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core", "CRONUS Sales" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);

        var prepared = await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);

        prepared.Should().HaveCount(1);
        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Proposed);
        delivery.ProjectBuildId.Should().Be(seed.BuildId);
        delivery.TriggeredByUserId.Should().BeNull("nobody has approved it yet");
        delivery.Results.Select(r => r.AppName).Should().BeEquivalentTo("CRONUS Core", "CRONUS Sales");
        delivery.DiagnosticsLog.Should().Contain($"Prepared from build #{seed.BuildId}");
        _queue.Reader.TryRead(out _).Should().BeFalse("a prepared release is never queued");
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_leaves_a_pipeline_that_did_not_ask_for_it()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });

        var prepared = await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);

        prepared.Should().BeEmpty();
        (await _db.NewContext().OeProjectDeliveries.AnyAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId)).Should().BeFalse();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_ignores_pull_request_builds()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await ctx.OeProjectBuilds.Where(b => b.Id == seed.BuildId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.Trigger, ProjectBuildTrigger.PullRequest));

        (await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_ignores_preview_builds()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await MakePreviewAsync(ctx, seed.BuildId, ProjectBuildTarget.NextMajor);

        (await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId)).Should().BeEmpty();
        (await _db.NewContext().OeProjectDeliveries.AnyAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId)).Should().BeFalse();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_schedules_by_the_delivery_window_rule()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        // A one-minute window an hour and a half from now: never open while the test runs.
        var start = TimeOnly.FromDateTime(DateTime.UtcNow.AddMinutes(90));
        await SetWindowAsync(ctx, seed.EnvironmentId, start, start.AddMinutes(1));

        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId);
        delivery.ScheduledFor.Should().BeCloseTo(DateTime.UtcNow.AddMinutes(90), TimeSpan.FromMinutes(2));
        delivery.ScheduledByDeliveryWindow.Should().BeTrue();
        delivery.ScheduledOutsideWindow.Should().BeFalse();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_replaces_an_unapproved_proposal_from_an_older_build()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        var svc = NewService(ctx);
        await svc.ProposeReleasesForBuildAsync(seed.BuildId);
        var newer = await SeedBuildAsync(ctx, seed.ProjectId, seed.BuildPipelineId, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });

        await NewService(_db.NewContext()).ProposeReleasesForBuildAsync(newer);

        await using var read = _db.NewContext();
        var rows = await read.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).OrderBy(d => d.Id).ToListAsync();
        rows.Should().HaveCount(2, "a newer build replaces the proposal rather than stacking a queue");
        rows[0].Status.Should().Be(ProjectDeliveryStatus.Dismissed, "a replaced proposal is never a cancelled release");
        rows[0].ReplacedByProjectBuildId.Should().Be(newer);
        rows[0].DismissReason.Should().Be($"Replaced by build #{newer}");
        rows[0].CancelledByUserId.Should().BeNull("nobody replaced it; a newer build did");
        rows[0].FinishedAt.Should().NotBeNull();
        rows[0].DiagnosticsLog.Should().Contain($"Replaced by build #{newer} before anyone approved it.");
        rows[1].Status.Should().Be(ProjectDeliveryStatus.Proposed);
        rows[1].ProjectBuildId.Should().Be(newer);

        // Preparing the same build again changes nothing.
        (await NewService(_db.NewContext()).ProposeReleasesForBuildAsync(newer)).Should().BeEmpty();
        // And the history row says what became of the replaced one.
        var history = await NewService(_db.NewContext()).ListDeliveryHistoryAsync(seed.ReleasePipelineId);
        var replaced = history.Single(h => h.Id == rows[0].Id);
        replaced.IsDismissed.Should().BeTrue();
        replaced.ReplacedByBuildId.Should().Be(newer);
        history.Single(h => h.Id == rows[1].Id).IsProposed.Should().BeTrue();
    }

    [Fact]
    public async Task EnqueueDueDeliveriesAsync_never_enqueues_a_proposed_release()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);

        // Long after its time: still nothing, because nobody has approved it.
        var enqueued = await NewService(_db.NewContext()).EnqueueDueDeliveriesAsync(DateTime.UtcNow.AddDays(3));

        enqueued.Should().Be(0);
        _queue.Reader.TryRead(out _).Should().BeFalse();
        // And a worker handed its id anyway would not claim it.
        var id = await _db.NewContext().OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        await NewService(_db.NewContext()).RunDeliveryAsync(id);
        (await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == id)).Status.Should().Be(ProjectDeliveryStatus.Proposed);
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task ApproveProposalAsync_schedules_it_as_an_ordinary_release_run_by_the_approver()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var id = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        var approver = await SeedUserAsync("K. Jensen");
        _db.OrgContext.CurrentUserId = approver;
        _apps.StatusByApp["CRONUS Core"] = "succeeded";

        await NewService(_db.NewContext()).ApproveProposalAsync(id);

        await using (var read = _db.NewContext())
        {
            var delivery = await read.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id);
            delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
            delivery.TriggeredByUserId.Should().Be(approver);
            delivery.ScheduledFor.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1), "an immediate pipeline installs right away once approved");
            delivery.DiagnosticsLog.Should().Contain("Approved by K. Jensen.");
        }
        _queue.Reader.TryRead(out var job).Should().BeTrue("a release due now is queued on approval, like a hand-made one");
        job!.DeliveryId.Should().Be(id);

        // ...and runs exactly like one, keeping its history above the run's own lines.
        await NewService(_db.NewContext()).RunDeliveryAsync(id);
        await using var after = _db.NewContext();
        var ran = await after.OeProjectDeliveries.AsNoTracking().SingleAsync(d => d.Id == id);
        ran.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        ran.DiagnosticsLog.Should().Contain("Prepared from build #").And.Contain("Approved by K. Jensen.");
        var history = await NewService(after).ListDeliveryHistoryAsync(seed.ReleasePipelineId);
        history.Single().IsDismissed.Should().BeFalse("an approved release is a release");
        history.Single().DismissReason.Should().BeNull();
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("dismiss")]
    [InlineData("replace")]
    [InlineData("delete the pipeline")]
    public async Task Settling_a_prepared_deployment_marks_everyones_approval_request_read(string how)
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var id = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        var owner = await SeedUserAsync("Owner");
        var creator = await SeedUserAsync("Creator");
        var mine = await AddNotificationAsync(owner, NotificationSubject.Delivery(id));
        var theirs = await AddNotificationAsync(creator, NotificationSubject.Delivery(id));
        var other = await AddNotificationAsync(owner, NotificationSubject.Delivery(id + 1000));
        var tellsOnly = await AddNotificationAsync(owner, subject: null);
        _db.OrgContext.CurrentUserId = owner;
        _apps.StatusByApp["CRONUS Core"] = "succeeded";

        switch (how)
        {
            case "approve":
                await NewService(_db.NewContext()).ApproveProposalAsync(id);
                break;
            case "dismiss":
                await NewService(_db.NewContext()).DismissProposalAsync(id, null);
                break;
            case "delete the pipeline":
                await using (var del = _db.NewContext())
                {
                    await new ReleasePipelineService(del, _db.OrgContext, new ProjectAccess(del, _db.OrgContext),
                        NullLogger<ReleasePipelineService>.Instance).SoftDeleteReleasePipelineAsync(seed.ReleasePipelineId);
                }
                break;
            default:
                var newer = await SeedBuildAsync(ctx, seed.ProjectId, seed.BuildPipelineId, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });
                await NewService(_db.NewContext()).ProposeReleasesForBuildAsync(newer);
                break;
        }

        await using var read = _db.NewContext();
        var readAt = await read.UserNotifications.AsNoTracking().ToDictionaryAsync(n => n.Id, n => n.ReadAt);
        readAt[mine].Should().NotBeNull();
        readAt[theirs].Should().NotBeNull("the other person asked to approve it has nothing left to do either");
        readAt[other].Should().BeNull("a different deployment is still waiting");
        readAt[tellsOnly].Should().BeNull();
    }

    [Fact]
    public async Task ApproveProposalAsync_refuses_one_that_was_replaced()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var first = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        var newer = await SeedBuildAsync(ctx, seed.ProjectId, seed.BuildPipelineId, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });
        await NewService(_db.NewContext()).ProposeReleasesForBuildAsync(newer);

        var act = () => NewService(_db.NewContext()).ApproveProposalAsync(first);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors["Delivery"].Should().Contain("no longer waiting for approval");
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    // ── Who is online before a deployment ───────────────────────────────────

    private static BcSession Session(int id, string user, string clientType = "WebClient") =>
        new(id, user, clientType, DateTimeOffset.UtcNow, "", "", "", "", "", null, "", null);

    [Fact]
    public async Task CheckOpenSessionsAsync_counts_end_users_and_delegated_users_in_the_target_environment()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _admin.Sessions =
        [
            Session(1, "ola@cronus.com"),
            Session(2, "OLA@cronus.com", "WebServiceClient"),
            Session(3, "anna@cronus.com"),
            Session(4, "USER_E5EE0099AFAB445E8B604FE18E05FC1A"),
            Session(5, "ola@cronus.com", "Background"),
        ];

        var check = await NewService(ctx).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Should().Be(new OpenSessionsCheck("Production", EndUsers: 2, DelegatedUsers: 1));
        _admin.Requested.Should().Equal("Production");
    }

    [Fact]
    public async Task CheckOpenSessionsForDeliveryAsync_asks_the_environment_the_deployment_installs_to()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var deliveryId = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(1));
        // The pipeline is pointed at another environment after the deployment was made.
        var sandbox = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "Sandbox", Type = "Sandbox",
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(sandbox);
        await ctx.SaveChangesAsync();
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.ProjectEnvironmentId, sandbox.Id));
        _admin.Sessions = [Session(1, "ola@cronus.com")];

        var check = await NewService(_db.NewContext()).CheckOpenSessionsForDeliveryAsync(deliveryId);

        check.Should().Be(new OpenSessionsCheck("Production", EndUsers: 1, DelegatedUsers: 0));
        _admin.Requested.Should().Equal("Production");
    }

    [Fact]
    public async Task CheckOpenSessionsAsync_reports_a_Business_Central_failure_instead_of_throwing()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _admin.SessionsThrow = new BcApiException(null, "Couldn't reach the Business Central Admin Center API while reading who is signed in.");

        var check = await NewService(ctx).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Failure.Should().Be("Business Central didn't answer.", "the wire wording stays in the log");
        check.NeedsConfirmation.Should().BeTrue("we could not tell, so the person is asked");
    }

    [Fact]
    public async Task CheckOpenSessionsAsync_gives_up_on_a_slow_answer_and_says_so()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _admin.SessionsDelay = TimeSpan.FromSeconds(30);
        var service = NewService(ctx);
        service.OpenSessionsTimeout = TimeSpan.FromMilliseconds(100);

        var check = await service.CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Failure.Should().Be("Business Central took too long to answer.");
    }

    [Fact]
    public async Task CheckOpenSessionsAsync_reports_a_missing_connection_instead_of_throwing()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        _tokens.Throw = new BcApiException(null, "The Business Central client secret has expired.");

        var check = await NewService(ctx).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Failure.Should().Be("The Business Central client secret has expired.");
    }

    [Fact]
    public async Task CheckOpenSessionsAsync_is_refused_without_manage_rights()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeProjects.Where(p => p.Id == seed.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, ProjectVisibility.ReadOnly));
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = await SeedUserAsync("Someone else");

        var act = () => NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        _admin.Requested.Should().BeEmpty("nobody who may not deploy learns who is signed in");
    }

    [Fact]
    public async Task CheckOpenSessionsAsync_refuses_a_deleted_pipeline()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeletedAt, DateTime.UtcNow));

        var act = () => NewService(ctx).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("ReleasePipeline");
    }

    [Fact]
    public async Task ApproveProposalAsync_is_refused_without_manage_rights()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var id = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        await ctx.OeProjects.Where(p => p.Id == seed.ProjectId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, ProjectVisibility.ReadOnly));
        _db.OrgContext.IsSiteAdmin = false;
        _db.OrgContext.CurrentUserId = await SeedUserAsync("Someone else");

        var approve = () => NewService(_db.NewContext()).ApproveProposalAsync(id);
        var dismiss = () => NewService(_db.NewContext()).DismissProposalAsync(id, null);

        await approve.Should().ThrowAsync<ProjectAccessDeniedException>();
        await dismiss.Should().ThrowAsync<ProjectAccessDeniedException>();
        (await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == id)).Status.Should().Be(ProjectDeliveryStatus.Proposed);
    }

    [Fact]
    public async Task DismissProposalAsync_records_who_and_why()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var id = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();
        var who = await SeedUserAsync("K. Jensen");
        _db.OrgContext.CurrentUserId = who;

        await NewService(_db.NewContext()).DismissProposalAsync(id, "  CRONUS asked us to wait\nuntil after month-end  ");

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.AsNoTracking().Include(d => d.Results).SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Dismissed);
        delivery.DismissReason.Should().Be("CRONUS asked us to wait until after month-end");
        delivery.ReplacedByProjectBuildId.Should().BeNull();
        ProjectDeliveryStatus.IsTerminal(delivery.Status).Should().BeTrue();
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped && r.Message == "Not sent: the deployment was dismissed.");
        delivery.CancelledByUserId.Should().Be(who);
        delivery.FinishedAt.Should().NotBeNull();
        delivery.DiagnosticsLog.Should().Contain("Dismissed by K. Jensen: CRONUS asked us to wait until after month-end");
        var history = await NewService(read).ListDeliveryHistoryAsync(seed.ReleasePipelineId);
        history.Single().IsDismissed.Should().BeTrue();
        history.Single().CancelledByName.Should().Be("K. Jensen");
        history.Single().DismissReason.Should().Be("CRONUS asked us to wait until after month-end");

        // Dismissed once is dismissed: a second go is refused rather than written twice.
        var again = () => NewService(_db.NewContext()).DismissProposalAsync(id, null);
        await again.Should().ThrowAsync<PlanValidationException>();
    }

    [Fact]
    public async Task DismissProposalAsync_refuses_an_overlong_reason()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        var id = await ctx.OeProjectDeliveries.Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).Select(d => d.Id).SingleAsync();

        var act = () => NewService(_db.NewContext()).DismissProposalAsync(id, new string('x', DeliveryService.DismissReasonMaxLength + 1));

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Should().ContainKey("Reason");
    }

    [Fact]
    public async Task A_scheduled_release_cannot_be_approved_or_dismissed()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var id = await NewService(ctx).ScheduleDeliveryAsync(seed.ReleasePipelineId, seed.BuildId, DateTime.UtcNow.AddHours(3));

        var approve = () => NewService(_db.NewContext()).ApproveProposalAsync(id);
        var dismiss = () => NewService(_db.NewContext()).DismissProposalAsync(id, null);

        await approve.Should().ThrowAsync<PlanValidationException>();
        await dismiss.Should().ThrowAsync<PlanValidationException>();
    }

    // ── Deploying to a sandbox without approval (#1096) ──────────────────────────────

    /// <summary>Makes the seeded environment a sandbox and turns deploying without approval on, as a new current user.</summary>
    private async Task<int> DeployWithoutApprovalAsSandboxAsync(AppDbContext ctx, Seed seed, string environmentType = "Sandbox")
    {
        var userId = await SeedUserAsync("Mads Example");
        _db.OrgContext.CurrentUserId = userId;
        await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, environmentType));
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PrepareReleaseOnNewBuild, true)
                .SetProperty(r => r.DeployWithoutApproval, true)
                .SetProperty(r => r.DeployWithoutApprovalByUserId, userId));
        _admin.OnGet = name => new BcEnvironment(name, "Sandbox") { Status = "Active" };
        return userId;
    }

    [Fact]
    public async Task ListDeploymentsWithoutApprovalAsync_lists_only_pipelines_that_ask_for_it()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        (await NewService(ctx).ListDeploymentsWithoutApprovalAsync(seed.BuildId)).Should().BeEmpty();

        var userId = await DeployWithoutApprovalAsSandboxAsync(ctx, seed);

        (await NewService(_db.NewContext()).ListDeploymentsWithoutApprovalAsync(seed.BuildId))
            .Should().Equal(new DeploymentWithoutApproval(seed.ReleasePipelineId, userId));
    }

    [Fact]
    public async Task ListDeploymentsWithoutApprovalAsync_ignores_preview_builds()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        await MakePreviewAsync(ctx, seed.BuildId, ProjectBuildTarget.NextMajor);

        (await NewService(ctx).ListDeploymentsWithoutApprovalAsync(seed.BuildId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ListDeploymentsWithoutApprovalAsync_names_nobody_for_a_disabled_account()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var userId = await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        await ctx.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, ALDevToolbox.Domain.Entities.UserStatus.Disabled));

        (await NewService(_db.NewContext()).ListDeploymentsWithoutApprovalAsync(seed.BuildId))
            .Should().Equal(new DeploymentWithoutApproval(seed.ReleasePipelineId, null));
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_replaces_an_older_build_still_waiting_for_its_window()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" }, deploymentSchedule: BcDeploymentSchedule.OurDeliveryWindow);
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        // A one-minute window an hour and a half from now: never open while the test runs.
        var start = TimeOnly.FromDateTime(DateTime.UtcNow.AddMinutes(90));
        await SetWindowAsync(ctx, seed.EnvironmentId, start, start.AddMinutes(1));
        var first = await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId);
        _queue.Reader.TryRead(out _).Should().BeFalse("it waits for the window");
        var newer = await SeedBuildAsync(ctx, seed.ProjectId, seed.BuildPipelineId, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });

        var second = await NewService(_db.NewContext()).DeployWithoutApprovalAsync(seed.ReleasePipelineId, newer);

        await using var read = _db.NewContext();
        var old = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == first);
        old.Status.Should().Be(ProjectDeliveryStatus.Dismissed);
        old.ReplacedByProjectBuildId.Should().Be(newer);
        old.Results.Should().OnlyContain(r => r.Message!.Contains("replaced"));
        (await read.OeProjectDeliveries.SingleAsync(d => d.Id == second)).Status.Should().Be(ProjectDeliveryStatus.Scheduled);

        // An older build finishing after the newer one changes nothing.
        (await NewService(_db.NewContext()).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId)).Should().BeNull();
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_schedules_and_queues_it_as_the_person_who_turned_it_on()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        var userId = await DeployWithoutApprovalAsSandboxAsync(ctx, seed);

        var id = await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId);

        id.Should().NotBeNull();
        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        delivery.DeployedWithoutApproval.Should().BeTrue();
        delivery.TriggeredByUserId.Should().Be(userId);
        delivery.DiagnosticsLog.Should().Contain($"Started by build #{seed.BuildId}").And.Contain("Runs as Mads Example");
        _queue.Reader.TryRead(out var job).Should().BeTrue("an immediate pipeline deploys right away");
        job!.DeliveryId.Should().Be(id!.Value);
        job.Identity.UserId.Should().Be(userId);

        // The same build again changes nothing.
        DrainQueue();
        (await NewService(_db.NewContext()).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId)).Should().BeNull();
        (await _db.NewContext().OeProjectDeliveries.CountAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId)).Should().Be(1);
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_leaves_a_build_already_deployed_alone_even_when_it_would_now_refuse()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        (await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId)).Should().NotBeNull();
        await using (var change = _db.NewContext())
        {
            var envId = await change.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId).Select(r => r.ProjectEnvironmentId).SingleAsync();
            await change.OeProjectEnvironments.Where(e => e.Id == envId).ExecuteUpdateAsync(u => u.SetProperty(e => e.Type, "Production"));
        }

        // Processing the build again (a resumed import) must not come back as a refusal,
        // which would leave a second deployment waiting for approval beside the first.
        (await NewService(_db.NewContext()).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId)).Should().BeNull();
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_refuses_an_environment_that_is_no_longer_a_sandbox()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed, environmentType: "Production");

        var act = () => NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DeployWithoutApproval");
        (await _db.NewContext().OeProjectDeliveries.AnyAsync(d => d.ReleasePipelineId == seed.ReleasePipelineId)).Should().BeFalse();
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_refuses_to_run_as_anyone_but_the_person_who_turned_it_on()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        _db.OrgContext.CurrentUserId = await SeedUserAsync("Someone Else");

        var act = () => NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeployWithoutApprovalAsync_replaces_a_proposal_still_waiting_on_an_older_build()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);
        await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId);
        await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
        var newer = await SeedBuildAsync(ctx, seed.ProjectId, seed.BuildPipelineId, ProjectBuildStatus.Ready, new[] { "CRONUS Core" });

        await NewService(_db.NewContext()).DeployWithoutApprovalAsync(seed.ReleasePipelineId, newer);

        var rows = await _db.NewContext().OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == seed.ReleasePipelineId).OrderBy(d => d.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].Status.Should().Be(ProjectDeliveryStatus.Dismissed);
        rows[0].ReplacedByProjectBuildId.Should().Be(newer);
        rows[1].Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        rows[1].DeployedWithoutApproval.Should().BeTrue();
    }

    [Fact]
    public async Task ProposeReleasesForBuildAsync_skips_a_pipeline_that_deployed_and_explains_one_that_could_not()
    {
        await using var ctx = _db.NewContext();
        var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
        await PrepareOnNewBuildAsync(ctx, seed.ReleasePipelineId);

        (await NewService(ctx).ProposeReleasesForBuildAsync(seed.BuildId, deployedWithoutApproval: new HashSet<int> { seed.ReleasePipelineId }))
            .Should().BeEmpty("it already deployed");

        var prepared = await NewService(_db.NewContext()).ProposeReleasesForBuildAsync(seed.BuildId,
            notDeployedReasons: new Dictionary<int, string> { [seed.ReleasePipelineId] = "Production is no longer a sandbox." });

        prepared.Should().HaveCount(1);
        var delivery = await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == prepared[0]);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Proposed);
        delivery.DeployedWithoutApproval.Should().BeFalse();
        delivery.DiagnosticsLog.Should().Contain("Not deployed automatically: Production is no longer a sandbox.");
    }

    [Fact]
    public async Task RunDeliveryAsync_deploys_an_unapproved_deployment_to_a_sandbox()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
            id = (await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId))!.Value;
        }
        DrainQueue();

        await using (var run = _db.NewContext()) (await NewService(run).RunDeliveryAsync(id)).Should().BeTrue();

        _apps.UploadedOrder.Should().NotBeEmpty();
        (await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == id)).Status.Should().NotBe(ProjectDeliveryStatus.Failed);
    }

    [Fact]
    public async Task RunDeliveryAsync_refuses_an_unapproved_deployment_once_the_environment_is_production()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
            id = (await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId))!.Value;
        }
        DrainQueue();
        // The environment was turned into a production one between scheduling and running.
        _admin.OnGet = name => new BcEnvironment(name, "Production") { Status = "Active" };

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(id);

        var delivery = await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Contain("no longer a sandbox");
        _apps.UploadedOrder.Should().BeEmpty("an unapproved deployment never reaches a production environment");
    }

    [Fact]
    public async Task RunDeliveryAsync_falls_back_to_the_stored_type_when_the_environment_cannot_be_read()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            await DeployWithoutApprovalAsSandboxAsync(ctx, seed);
            id = (await NewService(ctx).DeployWithoutApprovalAsync(seed.ReleasePipelineId, seed.BuildId))!.Value;
            await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, "Production"));
        }
        DrainQueue();
        _admin.OnGet = _ => throw new BcApiException(null, "Couldn't reach the Business Central Admin Center API.");

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(id);

        (await _db.NewContext().OeProjectDeliveries.SingleAsync(d => d.Id == id)).Status.Should().Be(ProjectDeliveryStatus.Failed);
        _apps.UploadedOrder.Should().BeEmpty();
    }

    [Fact]
    public async Task RunDeliveryAsync_leaves_an_approved_deployment_to_production_alone()
    {
        int id;
        await using (var ctx = _db.NewContext())
        {
            var seed = await SeedAsync(ctx, appNames: new[] { "CRONUS Core" });
            id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        }
        DrainQueue();

        await using (var run = _db.NewContext()) await NewService(run).RunDeliveryAsync(id);

        _apps.UploadedOrder.Should().NotBeEmpty("the sandbox rule is only for deployments nobody approved");
    }

    private static async Task MakePreviewAsync(AppDbContext ctx, int buildId, string target) =>
        await ctx.OeProjectBuilds.Where(b => b.Id == buildId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.BcTarget, target));

    private static async Task PrepareOnNewBuildAsync(AppDbContext ctx, int releasePipelineId) =>
        await ctx.OeReleasePipelines.Where(r => r.Id == releasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.PrepareReleaseOnNewBuild, true));

    private async Task<int> SeedUserAsync(string displayName)
    {
        await using var ctx = _db.NewContext();
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

    private async Task<int> AddNotificationAsync(int userId, string? subject)
    {
        await using var ctx = _db.NewContext();
        var row = new ALDevToolbox.Domain.Entities.UserNotification
        {
            UserId = userId, OrganizationId = TestDb.DefaultOrgId,
            Category = ALDevToolbox.Domain.Entities.NotificationCategory.Deployments,
            Title = "Waiting for approval", Path = "/pipelines/deployments/1", Subject = subject,
            CreatedAt = DateTime.UtcNow,
        };
        ctx.UserNotifications.Add(row);
        await ctx.SaveChangesAsync();
        return row.Id;
    }

    private void DrainQueue()
    {
        while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
    }

    private static async Task SetWindowAsync(AppDbContext ctx, int environmentId, TimeOnly start, TimeOnly end)
    {
        await ctx.OeProjectEnvironments.Where(e => e.Id == environmentId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.UpdateWindowStart, start)
                .SetProperty(e => e.UpdateWindowEnd, end));
    }

    private DeliveryService NewService(AppDbContext ctx)
    {
        var svc = new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            _tokens, _apps, _admin, _queue,
            new ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache(TimeProvider.System),
            _db.NewToolEnablement(ctx),
            NullLogger<DeliveryService>.Instance)
        {
            PollDelay = TimeSpan.Zero,
            PollTimeoutPerApp = TimeSpan.FromSeconds(5),
        };
        return svc;
    }

    // ── Seeding ───────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int BuildPipelineId, int EnvironmentId, int ReleasePipelineId, int BuildId);

    private static async Task<Seed> SeedAsync(AppDbContext ctx, string[] appNames,
        string buildStatus = ProjectBuildStatus.Ready,
        string? deploymentSchedule = null,
        string? schemaSyncMode = null)
    {
        var now = DateTime.UtcNow;
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipelineId = await SeedPipelineAsync(ctx, project.Id);
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App → Production",
            BuildPipelineId = pipelineId, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = deploymentSchedule ?? BcDeploymentSchedule.Immediate,
            SchemaSyncMode = schemaSyncMode ?? BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);
        await ctx.SaveChangesAsync();

        var buildId = await SeedBuildAsync(ctx, project.Id, pipelineId, buildStatus, appNames);
        return new Seed(project.Id, pipelineId, env.Id, releasePipeline.Id, buildId);
    }

    /// <summary>Points a seeded release pipeline at a repository's GitHub releases instead of a build pipeline.</summary>
    private static async Task MakeReleaseSourcedAsync(AppDbContext ctx, int releasePipelineId)
    {
        var rp = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == releasePipelineId);
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = rp.ProjectId,
            Provider = RepositoryProvider.GitHub, Url = "https://github.com/cronus-dk/cronus-customer.git",
            DisplayName = "cronus-customer",
        };
        ctx.OeProjectRepositories.Add(repository);
        await ctx.SaveChangesAsync();

        rp.ArtifactSource = ReleaseArtifactSource.GithubRelease;
        rp.BuildPipelineId = null;
        rp.GithubReleaseRepositoryId = repository.Id;
        await ctx.SaveChangesAsync();
    }

    /// <summary>A build staged from a GitHub release: ready, no pipeline, the tag recorded.</summary>
    private static async Task<int> SeedStagedBuildAsync(AppDbContext ctx, int projectId, string tag, string[] appNames)
    {
        var now = DateTime.UtcNow;
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = null,
            Status = ProjectBuildStatus.Ready, GithubReleaseTag = tag,
            GithubReleaseUrl = $"https://github.com/cronus-dk/cronus-customer/releases/tag/{tag}",
            StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        foreach (var name in appNames)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"{name}_1.0.0.0.app", AppName = name, AppVersion = "1.0.0.0",
                SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private static async Task<int> SeedPipelineAsync(AppDbContext ctx, int projectId)
    {
        var p = new OePipeline { OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Build " + Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        ctx.OePipelines.Add(p);
        await ctx.SaveChangesAsync();
        return p.Id;
    }

    private static async Task<int> SeedBuildAsync(AppDbContext ctx, int projectId, int pipelineId, string status, string[] appNames)
    {
        var now = DateTime.UtcNow;
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = status, StartedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        // Artifacts inserted in dependency order (the build's TopologicalOrder), preserved by id.
        for (var i = 0; i < appNames.Length; i++)
        {
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
                FileName = $"{appNames[i]}_1.0.{i}.0.app", AppName = appNames[i], AppVersion = $"1.0.{i}.0",
                SizeBytes = 10 + i, Content = new byte[] { 1, 2, 3, (byte)i }, CreatedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    // ── Fakes ─────────────────────────────────────────────────────────────────

    private sealed class FakeTokenSource : IDeliveryTokenSource
    {
        public string Token = "fake-token";
        public Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Exception? Throw;
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => Throw is not null ? throw Throw : Task.FromResult(new BcDeliveryContext(Token, TenantId));
    }

    /// <summary>
    /// The claim-time environment re-read. Defaults to an Active environment so the
    /// existing publish tests are unaffected; a test that cares sets <see cref="OnGet"/>.
    /// </summary>
    private sealed class FakeAdminClient : IBcAdminClient
    {
        public Func<string, BcEnvironment?> OnGet = name => new BcEnvironment(name, "Production") { Status = "Active" };
        public List<string> Requested { get; } = new();

        public Task<IReadOnlyList<BcEnvironment>> ListEnvironmentsAsync(string accessToken, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<BcEnvironment>)Array.Empty<BcEnvironment>());

        public Task<BcEnvironment?> GetEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Requested.Add(environmentName);
            return Task.FromResult(OnGet(environmentName));
        }

        // The delivery flow never reads or writes Microsoft's update window - that is
        // context on the project page, not an input to a publish.
        public Task<BcUpdateSettings?> GetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentOperation>> ListEnvironmentOperationsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<BcTenantStorage> GetTenantStorageAsync(string accessToken, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcEnvironmentUpdate>> ListEnvironmentUpdatesAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<BcTimeZone>> ListTimezonesAsync(string accessToken, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetAppUpdateCadenceAsync(string accessToken, string? applicationFamily, string environmentName, string cadence, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool?> GetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetM365AccessAsync(string accessToken, string? applicationFamily, string environmentName, bool enabled, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SelectTargetVersionAsync(string accessToken, string? applicationFamily, string environmentName, string targetVersion, string? targetVersionType, DateTimeOffset? selectedDateTime = null, bool? ignoreUpdateWindow = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetUpdateSettingsAsync(string accessToken, string? applicationFamily, string environmentName, TimeOnly start, TimeOnly end, string windowsTimeZoneId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecoverEnvironmentAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default) => throw new NotSupportedException();
        /// <summary>Who is signed in, for the check before a deployment, unless <see cref="SessionsThrow"/> is set.</summary>
        public IReadOnlyList<BcSession> Sessions = [];
        public Exception? SessionsThrow;
        public TimeSpan SessionsDelay = TimeSpan.Zero;
        public async Task<IReadOnlyList<BcSession>> ListSessionsAsync(string accessToken, string? applicationFamily, string environmentName, CancellationToken ct = default)
        {
            Requested.Add(environmentName);
            if (SessionsDelay > TimeSpan.Zero) await Task.Delay(SessionsDelay, ct);
            return SessionsThrow is not null ? throw SessionsThrow : Sessions;
        }
        public Task CancelSessionAsync(string accessToken, string? applicationFamily, string environmentName, int sessionId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcEnvironmentCopy> CopyEnvironmentAsync(string accessToken, string? applicationFamily, string sourceEnvironmentName, string newEnvironmentName, string newEnvironmentType, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>
    /// The App Management surface. Uploads are recorded in order and answered with an
    /// operation whose ids the run is expected to keep; the poll then reports whatever
    /// <see cref="StatusByApp"/> says for that app. Statuses come back in the upload
    /// endpoint's lowercase spelling, which is not the casing the operations endpoint
    /// uses - that difference is exactly what the run must not depend on.
    /// </summary>
    private sealed class FakeAppManagementClient : IBcAppManagementClient
    {
        /// <summary>App name to the status its install operation reports. Missing = "succeeded".</summary>
        public Dictionary<string, string> StatusByApp { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// A failed operation's errorMessage exactly as Business Central sent it, with no
        /// codes read out beforehand - the codes then only exist inside the text (#930).
        /// Null = the default Danish sentence with the codes alongside.
        /// </summary>
        public string? FailedErrorMessage { get; set; }

        /// <summary>App names in upload order, so a test can assert dependency order was kept.</summary>
        public List<string> UploadedOrder { get; } = new();

        /// <summary>What the environment already has installed. Empty = every app is new to it.</summary>
        public List<BcInstalledApp> Installed { get; } = new();

        /// <summary>Versions already waiting for a schedule. Empty = nothing is queued.</summary>
        public List<BcScheduledPteOperation> Scheduled { get; } = new();

        /// <summary>What the last upload was sent with, for the tests that pin the call.</summary>
        public string? LastSchedule;
        public string? LastSyncMode;
        public string? LastLanguageId;
        public string? LastFamily;
        public bool LastInstallDependencies;

        private readonly Dictionary<Guid, string> _appNameByAppId = new();

        public Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(
            string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
        {
            LastFamily = applicationFamily;
            return Task.FromResult((IReadOnlyList<BcInstalledApp>)Installed);
        }

        public Task<BcAppOperation> InstallPteAsync(
            string accessToken, string applicationFamily, string environmentName, byte[] appBytes, string fileName,
            string deploymentSchedule, string syncMode, string languageId, bool installOrUpdateNeededDependencies,
            CancellationToken ct = default)
        {
            // The seed names artifacts "<App Name>_<version>.app".
            var appName = fileName[..fileName.LastIndexOf('_')];
            UploadedOrder.Add(appName);
            LastSchedule = deploymentSchedule;
            LastSyncMode = syncMode;
            LastLanguageId = languageId;
            LastInstallDependencies = installOrUpdateNeededDependencies;

            var appId = Guid.NewGuid();
            _appNameByAppId[appId] = appName;
            var status = BcDeploymentSchedule.IsDeferred(deploymentSchedule) ? "scheduled" : "running";
            return Task.FromResult(Operation(appId, status));
        }

        public Task<BcAppOperation?> GetAppOperationAsync(
            string accessToken, string applicationFamily, string environmentName, Guid appId, Guid operationId,
            CancellationToken ct = default)
        {
            var name = _appNameByAppId.GetValueOrDefault(appId, string.Empty);
            var status = StatusByApp.GetValueOrDefault(name, "succeeded");
            var operation = Operation(appId, status, operationId);
            if (status == "failed" && FailedErrorMessage is { } raw)
            {
                operation = operation with { ErrorMessage = raw, ErrorCode = string.Empty, InnerErrorCode = string.Empty };
            }
            return Task.FromResult<BcAppOperation?>(operation);
        }

        public Task<IReadOnlyList<BcScheduledPteOperation>> ListScheduledPteOperationsAsync(
            string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => Task.FromResult((IReadOnlyList<BcScheduledPteOperation>)Scheduled);
        public Task<IReadOnlyList<BcAvailableAppUpdate>> ListAvailableUpdatesAsync(string accessToken, string applicationFamily, string environmentName, CancellationToken ct = default)
            => throw new NotSupportedException();

        /// <summary>Scheduled installs cancelled, as (app id, version, schedule).</summary>
        public List<(Guid AppId, string Version, string Schedule)> Removed { get; } = new();

        /// <summary>When set, cancelling a scheduled install is refused with this message.</summary>
        public string? RemoveRefusal { get; set; }

        /// <summary>When set, the cancel is lost on the way back (after Business Central acted on it when <see cref="RemoveTakesEffect"/>).</summary>
        public Exception? RemoveFault { get; set; }
        public bool RemoveTakesEffect { get; set; }

        public Task<BcAppOperation> RemoveScheduledPteVersionAsync(
            string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion,
            string scheduleKind, CancellationToken ct = default)
        {
            if (RemoveRefusal is { } refusal) throw new BcApiException(null, refusal);
            if (RemoveFault is { } fault)
            {
                if (RemoveTakesEffect) Scheduled.RemoveAll(s => s.AppId == appId && s.TargetAppVersion == targetVersion);
                throw fault;
            }
            Removed.Add((appId, targetVersion, scheduleKind));
            return Task.FromResult(Operation(appId, "canceled"));
        }

        private static BcAppOperation Operation(Guid appId, string status, Guid? operationId = null) => new(
            Id: operationId ?? Guid.NewGuid(),
            AppId: appId,
            Type: "install",
            Status: BcAppManagementClient.ParseStatus(status),
            RawStatus: status,
            SourceAppVersion: string.Empty,
            TargetAppVersion: string.Empty,
            ScheduleKind: null,
            // A real failure comes back in the environment's language; nothing may read it.
            ErrorMessage: status == "failed" ? "Installationen af udvidelsen mislykkedes." : string.Empty,
            ErrorCode: status == "failed" ? "ExtensionChangeFailed" : string.Empty,
            InnerErrorCode: status == "failed" ? "TenantSyncFailure" : string.Empty,
            CanBeCanceled: false,
            CreatorPrincipalType: "app",
            CreatedOn: DateTimeOffset.UtcNow,
            StartedOn: null,
            CompletedOn: null);
        public Task<BcAppOperation> UpdateAppAsync(string accessToken, string applicationFamily, string environmentName, Guid appId, string targetVersion, bool useEnvironmentUpdateWindow, bool installOrUpdateNeededDependencies, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
