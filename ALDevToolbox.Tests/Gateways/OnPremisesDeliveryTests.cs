using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Tests.Infrastructure;
using ALDevToolbox.Tests.ObjectExplorer;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Deploying to a linked on-premises service tier (#1367): the shared run in
/// <see cref="DeliveryService"/> against <see cref="GatewayDeliveryTarget"/>, with a fake
/// gateway on the other side of the real command queue - it claims the command, posts
/// progress and reports per-app outcomes the way the publish-install verb does - and the
/// follower that finishes the deployment once the run has handed it off. Also the
/// rules on-premises adds: no deferred schedules, Force sync only where the customer's
/// gateway settings allow it, no AI deployments to production, open sessions counted
/// across every tier on the database, and an offline gateway failing the run at once.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class OnPremisesDeliveryTests : IDisposable
{
    private const string CoreId = "c0ffee00-0000-0000-0000-000000000001";
    private const string SalesId = "c0ffee00-0000-0000-0000-000000000002";
    private const string ReportsId = "c0ffee00-0000-0000-0000-000000000003";

    private readonly TestDb _db = new();
    private readonly DeliveryQueue _queue = new();
    private readonly GatewayCommandSignal _signal = new();

    public OnPremisesDeliveryTests()
    {
        _db.OrgContext.IsSiteAdmin = true; // manage rights via the project owner
    }

    public void Dispose() => _db.Dispose();

    // ── The run, end to end against a fake gateway ─────────────────────────────

    [Fact]
    public async Task A_deployment_goes_to_the_gateway_as_one_command_and_each_app_gets_its_own_outcome()
    {
        var seed = await SeedAsync(installed: [(SalesId, "CRONUS Sales", "1.0.1.0")], previous: [(CoreId, "CRONUS Core", "0.9.0.0")]);
        var id = await DeployNowAsync(seed);

        GatewayCommandMessage? seen = null;
        byte[]? downloaded = null;
        await RunWithGatewayAsync(id, seed.GatewayId, async (gateway, message) =>
        {
            seen = message;
            downloaded = await gateway.DownloadAsync(message, 0);
            return (
                ["Publishing CRONUS Core 1.0.0.0 on BC250.", "Installing CRONUS Reports 1.0.2.0."],
                Succeeded(
                    App(CoreId, GatewayPublishInstallOutcomes.Upgraded, fromVersion: "0.9.0.0", signer: "CN=CRONUS A/S"),
                    App(ReportsId, GatewayPublishInstallOutcomes.Installed)));
        });

        // What the gateway was asked to do.
        var parameters = seen!.Parameters;
        parameters["machine"]!.GetValue<string>().Should().Be("CRONUS-BC01");
        parameters["instance"]!.GetValue<string>().Should().Be("BC250");
        parameters["syncMode"]!.GetValue<string>().Should().Be(GatewaySyncModes.Add);
        parameters["unpublishPrevious"]!.GetValue<bool>().Should().BeTrue("the pipeline's default");
        parameters.ContainsKey("tenant").Should().BeFalse("a single-tenant server is left to its default tenant");
        var apps = parameters["apps"]!.AsArray();
        apps.Select(a => a!["id"]!.GetValue<string>()).Should().Equal([CoreId, ReportsId],
            "in dependency order, without the app the tier already has at this version");
        apps.Select(a => a!["version"]!.GetValue<string>()).Should().Equal("1.0.0.0", "1.0.2.0");
        seen.Files.Should().HaveCount(2);
        downloaded.Should().Equal(seed.Packages[0], "the file is read from the build artifact it references");

        await using var read = _db.NewContext();
        var delivery = await read.OeProjectDeliveries.Include(d => d.Results).SingleAsync(d => d.Id == id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results.Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Completed);
        results[0].PreviousVersion.Should().Be("0.9.0.0");
        results[0].Message.Should().Contain("Signed by CN=CRONUS A/S.");
        results[0].OperationId.Should().BeNull("the gateway has no operation id of Business Central's own");
        results[1].Message.Should().Be("Already on 1.0.1.0.");
        delivery.TiersRestarted.Should().BeFalse();
        delivery.DiagnosticsLog.Should().Contain("through the gateway")
            .And.Contain("Waiting for the gateway to pick it up.")
            .And.Contain("Gateway: Publishing CRONUS Core 1.0.0.0 on BC250.")
            .And.Contain("Gateway: Installing CRONUS Reports 1.0.2.0.")
            .And.Contain(DeliveryService.TiersNotRestartedLine);
    }

    [Fact]
    public async Task An_app_the_gateway_finds_already_installed_is_skipped_and_the_run_goes_on()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            [],
            Succeeded(
                App(CoreId, GatewayPublishInstallOutcomes.AlreadyInstalled, message: "Already installed at 1.0.0.0."),
                App(SalesId, GatewayPublishInstallOutcomes.Installed),
                App(ReportsId, GatewayPublishInstallOutcomes.Installed)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Deployed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Completed);
        delivery.Results.Single(r => r.Ordering == 0).Message.Should().Be("Already installed at 1.0.0.0.");
    }

    [Fact]
    public async Task A_failed_app_fails_the_run_and_the_apps_after_it_are_skipped()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            ["Publishing CRONUS Sales 1.0.1.0 on BC250."],
            Failed("CRONUS Sales 1.0.1.0 failed to sync.",
                App(CoreId, GatewayPublishInstallOutcomes.Installed),
                App(SalesId, GatewayPublishInstallOutcomes.Failed, message: "The schema sync found a destructive change in table 50100."),
                App(ReportsId, GatewayPublishInstallOutcomes.Skipped)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed, ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().Be("CRONUS Sales 1.0.1.0 failed: The schema sync found a destructive change in table 50100.");
        delivery.DiagnosticsLog.Should().NotContain(DeliveryService.TiersNotRestartedLine, "only a deployment that went through says so");
    }

    [Fact]
    public async Task A_refused_app_is_recorded_as_refused_not_failed()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunWithGatewayAsync(id, seed.GatewayId, (_, _) => Task.FromResult<(string[], GatewayCommandResultRequest)>((
            [],
            Failed("The gateway refused CRONUS Core.",
                App(CoreId, GatewayPublishInstallOutcomes.Refused, message: "Not allowed by the customer's gateway settings: unsigned apps may not be published on CRONUS-BC01 / BC250."),
                App(SalesId, GatewayPublishInstallOutcomes.Skipped),
                App(ReportsId, GatewayPublishInstallOutcomes.Skipped)))));

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Refused, ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().StartWith("The gateway refused CRONUS Core 1.0.0.0: Not allowed by the customer's gateway settings");
        delivery.DiagnosticsLog.Should().Contain("REFUSED CRONUS Core 1.0.0.0");
    }

    [Fact]
    public async Task A_command_that_expires_uncollected_fails_the_first_app_with_the_queues_reason()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        // Nobody collects it: the queue's own sweep ends it as expired.
        await RunAsync(id);
        var commandId = await CommandIdAsync(seed.GatewayId);
        await using (var ctx = _db.NewContext())
        {
            await ctx.GatewayCommands.Where(c => c.Id == commandId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Expired)
                .SetProperty(c => c.FailureMessage, GatewayCommandService.OfflineMessage));
        }
        (await FollowAsync()).Should().Equal(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[0].Message.Should().Be(GatewayCommandService.OfflineMessage);
        results.Skip(1).Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
    }

    [Fact]
    public async Task A_due_deployment_whose_gateway_is_offline_fails_at_once_and_queues_nothing()
    {
        var seed = await SeedAsync(gatewayLastSeen: DateTime.UtcNow.AddHours(-2));
        var id = await DeployNowAsync(seed);

        await using (var ctx = _db.NewContext())
        {
            await NewService(ctx).RunDeliveryAsync(id);
        }

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(
            "The gateway was offline, so nothing was sent. Check that the gateway service is running on CRONUS-GW01, then deploy again.");
        await using var read = _db.NewContext();
        (await read.GatewayCommands.CountAsync()).Should().Be(0);
    }

    // ── What on-premises refuses ───────────────────────────────────────────────

    [Theory]
    [InlineData(BcDeploymentSchedule.NextMinorUpdate)]
    [InlineData(BcDeploymentSchedule.NextMajorUpdate)]
    public async Task A_deferred_schedule_is_refused_when_the_deployment_is_made(string schedule)
    {
        var seed = await SeedAsync();
        await using var ctx = _db.NewContext();
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DeploymentSchedule, schedule));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors.Values
            .Should().Contain(DeliveryService.WaitsForUpdateRefusal("CRONUS Test"));
        _queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task A_deferred_schedule_on_a_deployment_already_made_fails_when_it_runs()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectDeliveries.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.DeploymentSchedule, BcDeploymentSchedule.NextMinorUpdate));
            await NewService(ctx).RunDeliveryAsync(id);
        }

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(DeliveryService.WaitsForUpdateRefusal("CRONUS Test"));
    }

    [Fact]
    public async Task Force_sync_is_refused_where_the_customers_gateway_settings_do_not_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: false);
        await using var ctx = _db.NewContext();
        await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SchemaSyncMode, BcSyncMode.ForceSync));

        var act = () => NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["SchemaSyncMode"]
            .Should().StartWith(OnPremisesDeployFacts.NotAllowed).And.Contain("Force sync");
    }

    [Fact]
    public async Task Force_sync_goes_to_the_gateway_where_the_customers_gateway_settings_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: true);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SchemaSyncMode, BcSyncMode.ForceSync));
        }
        var id = await DeployNowAsync(seed);

        string? syncMode = null;
        await RunWithGatewayAsync(id, seed.GatewayId, (_, message) =>
        {
            syncMode = message.Parameters["syncMode"]!.GetValue<string>();
            return Task.FromResult<(string[], GatewayCommandResultRequest)>(([],
                Succeeded(App(CoreId, "installed"), App(SalesId, "installed"), App(ReportsId, "installed"))));
        });

        syncMode.Should().Be(GatewaySyncModes.ForceSync);
    }

    [Fact]
    public async Task A_one_time_force_sync_retry_is_refused_where_the_settings_do_not_allow_it()
    {
        var seed = await SeedAsync(allowForceSync: false);
        var id = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectDeliveries.Where(d => d.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, ProjectDeliveryStatus.Failed));
        }

        var act = () => NewService(_db.NewContext()).ReleaseAgainAsync(id, forceSyncOnce: true);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["SchemaSyncMode"]
            .Should().Be(OnPremisesDeployFacts.ForceSyncRefusal("CRONUS Test"));
    }

    [Fact]
    public async Task An_ai_assistant_cannot_deploy_to_on_premises_production_even_where_the_organisation_lets_it_deploy_to_production()
    {
        var seed = await SeedAsync(environmentType: "Production");
        await AllowAgentsInProductionAsync();

        var act = () => NewService(_db.NewContext()).DeployNowForAgentAsync(seed.ReleasePipelineId, seed.BuildId);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["ProjectEnvironment"]
            .Should().Be(DeliveryService.OnPremisesProductionRefusedForAgents("CRONUS Test"));
    }

    [Fact]
    public async Task An_ai_assistant_can_deploy_to_an_on_premises_test_tier()
    {
        var seed = await SeedAsync(environmentType: "Sandbox");

        var id = await NewService(_db.NewContext()).DeployNowForAgentAsync(seed.ReleasePipelineId, seed.BuildId);

        (await LoadAsync(id)).StartedByAgent.Should().BeTrue();
    }

    // ── Open sessions across the tiers on one database ─────────────────────────

    [Fact]
    public async Task Open_sessions_are_counted_on_every_tier_that_shares_the_database()
    {
        var seed = await SeedAsync(sessions: 1);
        await using (var ctx = _db.NewContext())
        {
            // A second tier on the same database, serving the web client, publishes nothing
            // but its sessions see the new version all the same.
            ctx.GatewayInstances.Add(Instance(seed.GatewayId, "CRONUS-BC02", "BC250", sessions: 4));
            // A tier on another database does not count.
            ctx.GatewayInstances.Add(Instance(seed.GatewayId, "CRONUS-BC03", "BC250", sessions: 9, database: "CRONUS Test 2"));
            await ctx.SaveChangesAsync();
        }

        var check = await NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Sessions.Should().Be(5);
        check.Tiers.Should().Be(2);
        check.NeedsConfirmation.Should().BeTrue();
    }

    [Fact]
    public async Task No_open_sessions_needs_no_confirmation()
    {
        var seed = await SeedAsync(sessions: 0);

        var check = await NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Sessions.Should().Be(0);
        check.NeedsConfirmation.Should().BeFalse();
    }

    // ── The result mapping ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(GatewayPublishInstallOutcomes.Installed, true, DeliveryAppOutcome.Installed)]
    [InlineData(GatewayPublishInstallOutcomes.Upgraded, true, DeliveryAppOutcome.Installed)]
    [InlineData(GatewayPublishInstallOutcomes.AlreadyInstalled, false, DeliveryAppOutcome.AlreadyInstalled)]
    [InlineData(GatewayPublishInstallOutcomes.Failed, false, DeliveryAppOutcome.Failed)]
    [InlineData(GatewayPublishInstallOutcomes.Refused, false, DeliveryAppOutcome.Refused)]
    [InlineData(GatewayPublishInstallOutcomes.Skipped, false, DeliveryAppOutcome.Skipped)]
    public void Each_gateway_outcome_maps_onto_the_runs_outcome(string outcome, bool completed, DeliveryAppOutcome expected)
    {
        var final = new GatewayCommandProgress(GatewayCommandStatus.Succeeded, null,
            Succeeded(App(CoreId, outcome, fromVersion: "0.9.0.0")).Result, string.Empty, 0);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, "1.0.0.0");

        mapped.Completed.Should().Be(completed);
        mapped.Outcome.Should().Be(completed ? DeliveryAppOutcome.Installed : expected);
        mapped.TiersRestarted.Should().BeFalse();
        if (outcome == GatewayPublishInstallOutcomes.Upgraded) mapped.FromVersion.Should().Be("0.9.0.0");
    }

    [Fact]
    public void A_command_that_never_left_the_queue_gives_its_own_reason_to_the_app()
    {
        var final = new GatewayCommandProgress(GatewayCommandStatus.Expired, GatewayCommandService.OfflineMessage, null, string.Empty, 0);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, "1.0.0.0");

        mapped.Completed.Should().BeFalse();
        mapped.Outcome.Should().Be(DeliveryAppOutcome.Failed);
        mapped.Message.Should().Be(GatewayCommandService.OfflineMessage);
        mapped.TiersRestarted.Should().BeNull("nothing was said about restarts");
    }

    [Fact]
    public void A_command_the_gateway_collected_and_lost_leaves_each_app_unconfirmed()
    {
        var final = new GatewayCommandProgress(GatewayCommandStatus.Failed, GatewayCommandService.InterruptedMessage, null, string.Empty, 0,
            LeftQueue: true);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 1, "1.0.1.0");

        mapped.Completed.Should().BeFalse();
        mapped.Outcome.Should().Be(DeliveryAppOutcome.Unconfirmed, "some apps may have gone in before the gateway restarted");
        mapped.Message.Should().StartWith(GatewayDeliveryTarget.UnconfirmedAppMessage).And.EndWith(GatewayCommandService.InterruptedMessage);
    }

    [Fact]
    public void A_settings_refusal_before_anything_ran_is_a_refusal_even_after_the_gateway_collected_it()
    {
        const string why = "Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 does not allow 'publish'. Nothing was changed.";
        var final = new GatewayCommandProgress(GatewayCommandStatus.Failed, why, null, string.Empty, 0, LeftQueue: true);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, "1.0.0.0");

        mapped.Outcome.Should().Be(DeliveryAppOutcome.Refused);
        mapped.Message.Should().Be(why);
    }

    [Fact]
    public void A_command_that_failed_without_a_result_but_changed_nothing_is_a_failure_not_unconfirmed()
    {
        const string why = "The command's time to start ran out before the gateway could start it. Nothing was changed.";
        var final = new GatewayCommandProgress(GatewayCommandStatus.Failed, why, null, string.Empty, 0, LeftQueue: true);

        var mapped = GatewayDeliveryTarget.OutcomeFor(final, 0, "1.0.0.0");

        mapped.Outcome.Should().Be(DeliveryAppOutcome.Failed);
        mapped.Message.Should().Be(why);
    }

    [Fact]
    public void Gateway_lines_lose_the_time_stamp_the_deployment_log_puts_back()
    {
        GatewayDeliveryTarget.GatewayLines("2026-10-15 09:12:01Z Publishing CRONUS Core.\n\n2026-10-15 09:12:04Z Synced.\n")
            .Should().Equal("Publishing CRONUS Core.", "Synced.");
    }

    [Fact]
    public void A_signature_is_read_from_the_package_length_in_the_header()
    {
        var unsigned = SyntheticApp.Build(CoreId, "CRONUS Core", "CRONUS", "1.0.0.0");
        GatewayDeliveryTarget.CarriesSignature(unsigned).Should().BeFalse("the synthetic header says nothing follows the package");
        GatewayDeliveryTarget.CarriesSignature([1, 2, 3]).Should().BeFalse();
    }

    // ── Handed off: the worker is freed, the deployment is followed ────────────

    [Fact]
    public async Task The_run_hands_the_deployment_to_the_gateway_and_returns_without_waiting_for_it()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);

        await RunAsync(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Installing, "the gateway has not collected it yet");
        delivery.GatewayCommandId.Should().Be(await CommandIdAsync(seed.GatewayId));
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Installing);
        delivery.DiagnosticsLog.Should().Contain("Waiting for the gateway to pick it up.").And.NotContain("command 1");
    }

    [Fact]
    public async Task The_gateways_progress_reaches_the_deployment_while_the_command_runs()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await RunAsync(id);
        var gateway = new FakeGateway(this, seed.GatewayId);
        var message = await gateway.ClaimAsync();
        await gateway.ProgressAsync(message!.Id, ["App 1 of 3: CRONUS Core 1.0.0.0 on tenant 'default'."]);

        (await FollowAsync()).Should().BeEmpty("the command is still running");
        await gateway.ProgressAsync(message.Id, ["CRONUS Core 1.0.0.0: installed."]);
        await FollowAsync();

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Installing);
        delivery.DiagnosticsLog.Should().Contain("Gateway: App 1 of 3: CRONUS Core 1.0.0.0 on tenant 'default'.");
        delivery.DiagnosticsLog!.Split("Gateway: App 1 of 3").Should().HaveCount(2, "each line is copied once");
        delivery.DiagnosticsLog.Should().Contain("Gateway: CRONUS Core 1.0.0.0: installed.");
    }

    [Fact]
    public async Task A_restart_neither_fails_a_deployment_the_gateway_has_nor_lets_a_second_one_start()
    {
        var seed = await SeedAsync();
        var first = await DeployNowAsync(seed);
        await RunAsync(first);
        var gateway = new FakeGateway(this, seed.GatewayId);
        var message = await gateway.ClaimAsync();

        // The restart's check, as the scheduler makes it once per organisation.
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).FailInterruptedDeliveriesAsync(DateTime.UtcNow.AddMinutes(1))).Should().BeEmpty();
        }
        (await LoadAsync(first)).Status.Should().Be(ProjectDeliveryStatus.Installing);

        // A second deployment to the same tier waits, scheduled, rather than queuing a second command.
        var second = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            (await NewService(ctx).RunDeliveryAsync(second)).Should().BeFalse();
        }
        (await LoadAsync(second)).Status.Should().Be(ProjectDeliveryStatus.Scheduled);
        await using (var ctx = _db.NewContext())
        {
            (await ctx.GatewayCommands.CountAsync()).Should().Be(1);
        }

        // The command finishes after the restart, and the deployment with it.
        await gateway.CompleteAsync(message!.Id, Succeeded(
            App(CoreId, GatewayPublishInstallOutcomes.Installed),
            App(SalesId, GatewayPublishInstallOutcomes.Installed),
            App(ReportsId, GatewayPublishInstallOutcomes.Installed)));
        (await FollowAsync()).Should().Equal(first);
        (await LoadAsync(first)).Status.Should().Be(ProjectDeliveryStatus.Deployed);

        // Now the tier is free.
        await RunAsync(second);
        (await LoadAsync(second)).Status.Should().Be(ProjectDeliveryStatus.Installing);
    }

    [Fact]
    public async Task Giving_up_on_a_command_nobody_collected_takes_it_back_and_fails_the_deployment()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await RunAsync(id);
        await StartedLongAgoAsync(id);

        (await FollowAsync()).Should().Equal(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[0].Message.Should().Be(DeliveryService.GaveUpUncollected);
        results.Skip(1).Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
        await using var read = _db.NewContext();
        var command = await read.GatewayCommands.AsNoTracking().SingleAsync();
        command.Status.Should().Be(GatewayCommandStatus.Failed, "taken back, so a gateway coming online later never runs it");
    }

    [Fact]
    public async Task Giving_up_on_a_command_the_gateway_has_says_the_outcome_is_not_known()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await RunAsync(id);
        var message = await new FakeGateway(this, seed.GatewayId).ClaimAsync();
        await StartedLongAgoAsync(id);

        (await FollowAsync()).Should().Equal(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(DeliveryService.UnknownOutcome("CRONUS Test"));
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Unconfirmed);
        delivery.Results.First().Message.Should().Contain(DeliveryService.GaveUpCollected);
        await using var read = _db.NewContext();
        (await read.GatewayCommands.AsNoTracking().SingleAsync(c => c.Id == message!.Id)).Status
            .Should().Be(GatewayCommandStatus.Claimed, "only the gateway can say what became of it");
    }

    [Fact]
    public async Task A_command_that_stops_after_the_gateway_started_it_leaves_its_apps_unconfirmed()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await RunAsync(id);
        var gateway = new FakeGateway(this, seed.GatewayId);
        var message = await gateway.ClaimAsync();
        await gateway.ProgressAsync(message!.Id, ["App 1 of 3: CRONUS Core 1.0.0.0 on tenant 'default'."]);
        // The gateway goes quiet; the queue's sweep fails the command without a result.
        await using (var ctx = _db.NewContext())
        {
            await ctx.GatewayCommands.Where(c => c.Id == message.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FailureMessage, GatewayCommandService.GatewaySilentMessage));
        }

        await FollowAsync();

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be(DeliveryService.UnknownOutcome("CRONUS Test"));
        delivery.Results.Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Unconfirmed,
            "the first app may have gone in, and the gateway never said");
        delivery.Results.First().Message.Should().Contain("after the gateway's next inventory");
    }

    [Fact]
    public async Task A_refusal_by_the_gateways_own_check_before_the_verb_ran_is_recorded_as_refused()
    {
        var seed = await SeedAsync();
        var id = await DeployNowAsync(seed);
        await RunAsync(id);
        var gateway = new FakeGateway(this, seed.GatewayId);
        var message = await gateway.ClaimAsync();
        await gateway.CompleteAsync(message!.Id, new GatewayCommandResultRequest
        {
            Succeeded = false,
            FailureMessage = "Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 does not allow 'publish'. Nothing was changed.",
        });

        await FollowAsync();

        var delivery = await LoadAsync(id);
        delivery.Results.OrderBy(r => r.Ordering).Select(r => r.Status).Should().Equal(
            ProjectDeliveryResultStatus.Refused, ProjectDeliveryResultStatus.Skipped, ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().StartWith("The gateway refused CRONUS Core 1.0.0.0: Not allowed by");
    }

    [Fact]
    public async Task Force_sync_turned_off_after_the_deployment_was_made_is_refused_when_it_runs_and_nothing_is_sent()
    {
        var seed = await SeedAsync(allowForceSync: true);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeReleasePipelines.Where(r => r.Id == seed.ReleasePipelineId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.SchemaSyncMode, BcSyncMode.ForceSync));
        }
        var id = await DeployNowAsync(seed);
        // The customer's IT turns Force sync off before the deployment's turn comes.
        await SetPolicyAsync(seed.GatewayId, allowForceSync: false, environment: "test");

        await RunAsync(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Refused);
        results[0].Message.Should().Be(GatewayDeliveryTarget.ForceSyncRefusedAtClaim("CRONUS-BC01 / BC250"));
        results.Skip(1).Should().OnlyContain(r => r.Status == ProjectDeliveryResultStatus.Skipped);
        delivery.FailureMessage.Should().StartWith("The gateway refused CRONUS Core 1.0.0.0: Not allowed by");
        await using var read = _db.NewContext();
        (await read.GatewayCommands.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_ai_assistants_deployment_is_refused_when_it_runs_if_the_tier_has_become_production()
    {
        var seed = await SeedAsync(environmentType: "Sandbox");
        var id = await NewService(_db.NewContext()).DeployNowForAgentAsync(seed.ReleasePipelineId, seed.BuildId);
        while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        // The customer's IT marks the tier as production before the deployment's turn comes.
        await SetPolicyAsync(seed.GatewayId, allowForceSync: false, environment: "production");
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, "Production"));
        }

        await RunAsync(id);

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        delivery.FailureMessage.Should().Be("Not deployed: " + DeliveryService.OnPremisesProductionRefusedForAgents("CRONUS Test"));
        await using var read = _db.NewContext();
        (await read.GatewayCommands.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_command_queued_in_the_delivery_window_expires_when_the_window_closes()
    {
        var seed = await SeedAsync();
        var now = DateTime.UtcNow;
        var closes = now.AddMinutes(5);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == seed.EnvironmentId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.UpdateWindowStart, TimeOnly.FromDateTime(now.AddHours(-1)))
                .SetProperty(e => e.UpdateWindowEnd, TimeOnly.FromDateTime(closes)));
            await ctx.OeProjects.Where(p => p.Id == seed.ProjectId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.BcTimeZone, "UTC"));
        }
        var id = await DeployNowAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectDeliveries.Where(d => d.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScheduledByDeliveryWindow, true)
                .SetProperty(d => d.ScheduledOutsideWindow, false));
        }

        await RunAsync(id);
        DateTime expiresAt;
        await using (var ctx = _db.NewContext())
        {
            expiresAt = await ctx.GatewayCommands.AsNoTracking().Select(c => c.ExpiresAt).SingleAsync();
        }
        expiresAt.Should().BeCloseTo(closes, TimeSpan.FromSeconds(2), "nothing may start after the window closes");

        // The gateway stays away; the sweep runs just after the window has closed.
        var later = new ALDevToolbox.Tests.Auth.FakeTimeProvider(new DateTimeOffset(closes.AddMinutes(1), TimeSpan.Zero));
        await using (var ctx = _db.NewContext())
        {
            (await Commands(ctx, later).SweepAsync()).Expired.Should().Be(1);
        }
        await FollowAsync();

        var delivery = await LoadAsync(id);
        delivery.Status.Should().Be(ProjectDeliveryStatus.Failed);
        var results = delivery.Results.OrderBy(r => r.Ordering).ToList();
        results[0].Status.Should().Be(ProjectDeliveryResultStatus.Failed);
        results[0].Message.Should().Be(GatewayCommandService.OfflineMessage);
    }

    [Fact]
    public async Task Tiers_on_one_database_are_matched_however_their_server_is_spelled_and_offline_ones_are_left_out()
    {
        var seed = await SeedAsync(sessions: 1, databaseServer: "CRONUS-SQL01.cronus.local", databaseInstance: null);
        await using (var ctx = _db.NewContext())
        {
            // A second gateway on the SQL Server's own computer, which names it as itself.
            var sqlGateway = NewGateway("CRONUS-SQL01", lastSeen: DateTime.UtcNow);
            var offlineGateway = NewGateway("CRONUS-BC09", lastSeen: DateTime.UtcNow.AddHours(-3));
            ctx.Gateways.AddRange(sqlGateway, offlineGateway);
            await ctx.SaveChangesAsync();
            ctx.GatewayInstances.AddRange(
                Instance(seed.GatewayId, "CRONUS-BC02", "BC250", sessions: 2, server: "cronus-sql01", instance: "MSSQLSERVER"),
                Instance(seed.GatewayId, "CRONUS-BC03", "BC250", sessions: 3, server: "tcp:CRONUS-SQL01,1433", instance: ""),
                Instance(sqlGateway.Id, "CRONUS-SQL01", "BC250", sessions: 4, server: "localhost", instance: null),
                Instance(sqlGateway.Id, "CRONUS-SQL01", "BC251", sessions: 5, server: "(local)", instance: "MSSQLSERVER"),
                Instance(sqlGateway.Id, "CRONUS-SQL01", "BC252", sessions: 6, server: ".", instance: null),
                // Same spellings, another database or instance: not counted.
                Instance(seed.GatewayId, "CRONUS-BC04", "BC250", sessions: 50, server: "CRONUS-SQL01", instance: "BCDEV"),
                Instance(sqlGateway.Id, "CRONUS-SQL01", "BC253", sessions: 60, server: "localhost", instance: null, database: "CRONUS Live"),
                // On the database, but its gateway is offline: left out, and said so.
                Instance(offlineGateway.Id, "CRONUS-BC09", "BC250", sessions: 70, server: "CRONUS-SQL01", instance: null));
            await ctx.SaveChangesAsync();
        }

        var check = await NewService(_db.NewContext()).CheckOpenSessionsAsync(seed.ReleasePipelineId);

        check.Sessions.Should().Be(1 + 2 + 3 + 4 + 5 + 6);
        check.Tiers.Should().Be(6);
        check.UncountedTiers.Should().Be(1);
        check.Question.Should().Contain("1 more service tier on the same database was not counted, because its gateway is offline.");
    }

    [Theory]
    [InlineData("CRONUS-SQL01", null, "sql01.cronus.local", "MSSQLSERVER", false)]
    [InlineData("CRONUS-SQL01", null, "cronus-sql01.cronus.local", "MSSQLSERVER", true)]
    [InlineData(".", "", "CRONUS-GW01", null, true)]
    [InlineData("(local)", "BC", "cronus-gw01\\BC", null, true)]
    [InlineData("127.0.0.1", null, "localhost", null, true)]
    [InlineData("10.0.0.5", null, "10.0.0.6", null, false)]
    [InlineData("CRONUS-SQL01", "BC", "CRONUS-SQL01", null, false)]
    public void A_database_key_spells_one_database_one_way(string server, string? instance, string otherServer, string? otherInstance, bool same)
    {
        var key = DatabaseKey.For(server, instance, "CRONUS", "CRONUS-GW01");
        var other = DatabaseKey.For(otherServer, otherInstance, "cronus", "CRONUS-GW01");

        (key == other).Should().Be(same);
        DatabaseKey.For(server, instance, " ", "CRONUS-GW01").Should().BeNull("without a database name nothing can be said to share it");
    }

    [Theory]
    [InlineData("Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 does not take apps from the publisher 'Fabrikam'.",
        "Ask the customer's IT to allow apps published by Fabrikam on CRONUS-BC01 / BC250, then deploy again.")]
    [InlineData("'Core.app' is not signed, and CRONUS-BC01 / BC250 does not take unsigned apps (allow_unsigned is off in the customer's gateway settings).",
        "Sign the app, or ask the customer's IT to allow unsigned apps on CRONUS-BC01 / BC250, then deploy again.")]
    [InlineData("'Core.app' is signed by CN=Fabrikam (thumbprint AB12), who is not on CRONUS-BC01 / BC250's list of signers in the customer's gateway settings.",
        "Ask the customer's IT to add CN=Fabrikam to the signers allowed on CRONUS-BC01 / BC250, then deploy again.")]
    [InlineData("Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 does not allow Force sync, so nothing was sent.",
        "Set this deployment pipeline's schema sync to Add, or ask the customer's IT to allow Force sync on CRONUS-BC01 / BC250, then deploy again.")]
    [InlineData("Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 may not act on tenant 'cronus'.",
        "Ask the customer's IT to allow this in the gateway settings for CRONUS-BC01 / BC250, then deploy again.")]
    [InlineData("version 2.0.0.0 is installed on tenant 'default', which is newer than 1.0.0.0. The gateway does not install an older version.", null)]
    public void A_settings_refusal_names_the_next_step(string refusal, string? next)
    {
        GatewayRefusalAdvice.NextStep(refusal, "CRONUS-BC01 / BC250").Should().Be(next);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private sealed record Seed(int ProjectId, int GatewayId, int InstanceId, int EnvironmentId, int ReleasePipelineId, int BuildId, List<byte[]> Packages);

    private static GatewayPublishInstallAppResult App(
        string id, string outcome, string? fromVersion = null, string? message = null, string? signer = null) => new()
    {
        Id = id,
        Outcome = outcome,
        FromVersion = fromVersion,
        Message = message,
        SignerSubject = signer,
    };

    private static GatewayCommandResultRequest Succeeded(params GatewayPublishInstallAppResult[] apps) => new()
    {
        Succeeded = true,
        Result = new GatewayPublishInstallResult { Apps = apps, Restarted = false }.ToJson(),
    };

    private static GatewayCommandResultRequest Failed(string why, params GatewayPublishInstallAppResult[] apps) => new()
    {
        Succeeded = false,
        FailureMessage = why,
        Result = new GatewayPublishInstallResult { Apps = apps, Restarted = false }.ToJson(),
    };

    private async Task<Seed> SeedAsync(
        IReadOnlyList<(string Id, string Name, string Version)>? installed = null,
        IReadOnlyList<(string Id, string Name, string Version)>? previous = null,
        bool allowForceSync = false,
        string environmentType = "Sandbox",
        int? sessions = 0,
        DateTime? gatewayLastSeen = null,
        string databaseServer = "CRONUS-SQL01",
        string? databaseInstance = null)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;

        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS " + Guid.NewGuid().ToString("N"), CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        var pipeline = new OePipeline { OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "Build", CreatedAt = now, UpdatedAt = now };
        ctx.OePipelines.Add(pipeline);

        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250", Environment = environmentType == "Production" ? "production" : "test",
                    Allow = ["inventory", "publish"], AllowForceSync = allowForceSync,
                },
            ],
        };
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS hosting", MachineName = "CRONUS-GW01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now,
            LastSeenAt = gatewayLastSeen ?? now,
            EffectivePolicyJson = System.Text.Json.JsonSerializer.Serialize(policy),
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();

        var instance = Instance(gateway.Id, "CRONUS-BC01", "BC250", sessions, server: databaseServer, instance: databaseInstance);
        var tenant = new GatewayInstanceTenant
        {
            OrganizationId = TestDb.DefaultOrgId, TenantId = "default", SessionCount = sessions, ReadAt = now, AppsReadAt = now,
        };
        foreach (var (appId, name, version) in (installed ?? []).Concat(previous ?? []))
        {
            tenant.Apps.Add(new GatewayInstanceApp
            {
                OrganizationId = TestDb.DefaultOrgId, AppId = appId, Name = name, Publisher = "CRONUS", Version = version,
                Scope = "Tenant", IsInstalled = true, IsPublished = true,
            });
        }
        instance.Tenants.Add(tenant);
        ctx.GatewayInstances.Add(instance);

        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "CRONUS Test", Type = environmentType,
            Kind = OeEnvironmentKind.OnPremises, GatewayInstance = instance, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        var releasePipeline = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Build → CRONUS Test",
            BuildPipelineId = pipeline.Id, ProjectEnvironmentId = env.Id,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(releasePipeline);

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Pipeline = pipeline,
            Status = ProjectBuildStatus.Ready, StartedAt = now, FinishedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();

        var packages = new List<byte[]>();
        string[] ids = [CoreId, SalesId, ReportsId];
        string[] names = ["CRONUS Core", "CRONUS Sales", "CRONUS Reports"];
        for (var i = 0; i < ids.Length; i++)
        {
            var version = $"1.0.{i}.0";
            var bytes = SyntheticApp.Build(ids[i], names[i], "CRONUS", version);
            packages.Add(bytes);
            // Saved one at a time: the id order is the build's dependency order.
            ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id, FileName = $"CRONUS_{names[i].Replace(" ", "")}_{version}.app",
                AppId = ids[i], AppName = names[i], AppVersion = version, SizeBytes = bytes.Length, Content = bytes, CreatedAt = now,
            });
            await ctx.SaveChangesAsync();
        }

        return new Seed(project.Id, gateway.Id, instance.Id, env.Id, releasePipeline.Id, build.Id, packages);
    }

    private static GatewayInstance Instance(int gatewayId, string machine, string name, int? sessions, string database = "CRONUS Test",
        string server = "CRONUS-SQL01", string? instance = null) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, GatewayId = gatewayId, Machine = machine, Name = name, State = "Running",
        DatabaseServer = server, DatabaseInstance = instance, DatabaseName = database, Multitenant = false, SessionCount = sessions,
        FirstReportedAt = DateTime.UtcNow, ReportedAt = DateTime.UtcNow, ReadAt = DateTime.UtcNow,
    };

    private static Gateway NewGateway(string machine, DateTime lastSeen) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, Name = machine, MachineName = machine,
        KeyFingerprint = Guid.NewGuid().ToString("N"),
        State = GatewayState.Approved, RegisteredAt = lastSeen, KeyRegisteredAt = lastSeen, LastSeenAt = lastSeen,
    };

    /// <summary>What the customer's gateway settings now say about the linked tier.</summary>
    private async Task SetPolicyAsync(int gatewayId, bool allowForceSync, string environment)
    {
        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250", Environment = environment,
                    Allow = ["inventory", "publish"], AllowForceSync = allowForceSync,
                },
            ],
        };
        await using var ctx = _db.NewContext();
        await ctx.Gateways.Where(g => g.Id == gatewayId).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.EffectivePolicyJson, System.Text.Json.JsonSerializer.Serialize(policy)));
    }

    /// <summary>Moves a handed-off deployment's start back past the longest it is followed.</summary>
    private async Task StartedLongAgoAsync(int deliveryId)
    {
        var then = DateTime.UtcNow - GatewayDeliveryTarget.HardWaitLimit - TimeSpan.FromHours(1);
        await using var ctx = _db.NewContext();
        await ctx.OeProjectDeliveries.Where(d => d.Id == deliveryId).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.InstallStartedAt, then)
            .SetProperty(d => d.StartedAt, then));
    }

    private async Task AllowAgentsInProductionAsync()
    {
        await using var ctx = _db.NewContext();
        var settings = await ctx.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId };
            ctx.OrganizationSettings.Add(settings);
        }
        settings.AgentsMayDeployToProduction = true;
        await ctx.SaveChangesAsync();
    }

    private async Task<int> DeployNowAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var id = await NewService(ctx).ReleaseBuildNowAsync(seed.ReleasePipelineId, seed.BuildId);
        while (_queue.Reader.TryRead(out var job)) _queue.Complete(job.DeliveryId);
        return id;
    }

    private async Task<OeProjectDelivery> LoadAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectDeliveries.AsNoTracking().Include(d => d.Results).SingleAsync(d => d.Id == id);
    }

    /// <summary>The one command the run queued for the gateway.</summary>
    private async Task<long> CommandIdAsync(int gatewayId)
    {
        await using var ctx = _db.NewContext();
        return await ctx.GatewayCommands.AsNoTracking().Where(c => c.GatewayId == gatewayId).Select(c => c.Id).SingleAsync();
    }

    /// <summary>The worker's turn: claims the deployment and runs it until it is handed to the gateway.</summary>
    private async Task RunAsync(int deliveryId)
    {
        await using var ctx = _db.NewContext();
        (await NewService(ctx).RunDeliveryAsync(deliveryId)).Should().BeTrue("the run claims the deployment");
    }

    /// <summary>One pass of <see cref="GatewayDeliveryFollower"/>; answers the deployments it finished.</summary>
    private async Task<List<int>> FollowAsync(TimeProvider? clock = null)
    {
        await using var ctx = _db.NewContext();
        return await NewService(ctx, clock).FollowGatewayDeliveriesAsync();
    }

    /// <summary>
    /// Runs the deployment until it is handed off, lets a fake gateway collect its command,
    /// post the lines <paramref name="answer"/> gives and report the result it gives, then
    /// follows the command once, which finishes the deployment.
    /// </summary>
    private async Task RunWithGatewayAsync(
        int deliveryId, int gatewayId,
        Func<FakeGateway, GatewayCommandMessage, Task<(string[] Lines, GatewayCommandResultRequest Result)>> answer)
    {
        await RunAsync(deliveryId);
        var gateway = new FakeGateway(this, gatewayId);
        var message = await gateway.ClaimAsync();
        message.Should().NotBeNull("the run queues a command for the gateway");
        var (lines, result) = await answer(gateway, message!);
        if (lines.Length > 0) await gateway.ProgressAsync(message!.Id, lines);
        await gateway.CompleteAsync(message!.Id, result);
        (await FollowAsync()).Should().Equal(deliveryId);
    }

    /// <summary>The gateway's side of the command queue, as the endpoints call it.</summary>
    private sealed class FakeGateway(OnPremisesDeliveryTests owner, int gatewayId)
    {
        private GatewayCommandService Service(AppDbContext ctx) => owner.Commands(ctx);

        public async Task<GatewayCommandMessage?> ClaimAsync()
        {
            await using var ctx = owner._db.NewContext();
            var claim = await Service(ctx).ClaimNextAsync(gatewayId);
            return claim.Outcome == GatewayClaimOutcome.Claimed ? claim.Command : null;
        }

        public async Task<byte[]> DownloadAsync(GatewayCommandMessage message, int index)
        {
            await using var ctx = owner._db.NewContext();
            var (outcome, file) = await Service(ctx).OpenFileAsync(gatewayId, message.Id, message.Files[index].Id);
            outcome.Should().Be(GatewayFileOutcome.Found);
            using var stream = new MemoryStream();
            (await Service(ctx).CopyFileContentAsync(file!.Id, stream)).Should().BeTrue();
            var bytes = stream.ToArray();
            file.Size.Should().Be(bytes.Length);
            file.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(bytes)), "the hash the database worked out is the content's");
            return bytes;
        }

        public async Task ProgressAsync(long commandId, IReadOnlyList<string> lines)
        {
            await using var ctx = owner._db.NewContext();
            (await Service(ctx).AppendProgressAsync(gatewayId, commandId, lines)).Should().BeTrue();
        }

        public async Task CompleteAsync(long commandId, GatewayCommandResultRequest result)
        {
            await using var ctx = owner._db.NewContext();
            (await Service(ctx).CompleteAsync(gatewayId, commandId, result)).Should().BeTrue();
        }
    }

    private GatewayCommandService Commands(AppDbContext ctx) =>
        new(ctx, _db.OrgContext, _signal, TimeProvider.System, NullLogger<GatewayCommandService>.Instance);

    private GatewayCommandService Commands(AppDbContext ctx, TimeProvider clock) =>
        new(ctx, _db.OrgContext, _signal, clock, NullLogger<GatewayCommandService>.Instance);

    private DeliveryService NewService(AppDbContext ctx, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var gateway = new GatewayTargetServices(ctx, Commands(ctx, clock), clock, NullLogger<GatewayDeliveryTarget>.Instance);
        return new DeliveryService(ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new DeliveryTargetFactory(new NoTokens(), Unreachable.Of<IBcAppManagementClient>(), Unreachable.Of<IBcAdminClient>(), gateway), _queue,
            new BcPanelCache(TimeProvider.System),
            _db.NewToolEnablement(ctx),
            clock,
            NullLogger<DeliveryService>.Instance)
        {
            PollDelay = TimeSpan.Zero,
            PollTimeoutPerApp = TimeSpan.FromSeconds(5),
        };
    }

    // Business Central online is never reached from these tests.
    private sealed class NoTokens : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, bool forceRefresh, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public class Unreachable : System.Reflection.DispatchProxy
    {
        public static T Of<T>() where T : class => Create<T, Unreachable>();

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} reaches Business Central online.");
    }
}
