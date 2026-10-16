using System.Text.Json;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Restarting, stopping and starting an on-premises service tier from its environment page,
/// and restarting every tier on its database (#1371): who may, what the customer's gateway
/// settings must allow, the <c>shared_restart</c> refusal only AL Workbench can make, the
/// audit row, and how the history reads the command back. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".
/// </summary>
public sealed class OnPremisesTierActionServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(Start);
    private User _owner = null!;
    private User _stranger = null!;
    private int _projectId;
    private int _gatewayId;
    private int _bc01;
    private int _bc02;
    private int _environmentId;

    public OnPremisesTierActionServiceTests()
    {
        SetUpAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    // ---------------------------------------------------------------- what is sent

    [Theory]
    [InlineData(ServiceTierAction.Restart, GatewayVerb.RestartInstance, UpgradeActionKind.RestartServiceTier)]
    [InlineData(ServiceTierAction.RestartAll, GatewayVerb.RestartGroup, UpgradeActionKind.RestartServiceTiers)]
    [InlineData(ServiceTierAction.Stop, GatewayVerb.StopInstance, UpgradeActionKind.StopServiceTier)]
    [InlineData(ServiceTierAction.Start, GatewayVerb.StartInstance, UpgradeActionKind.StartServiceTier)]
    public async Task Each_action_queues_its_command_for_the_linked_tier_and_records_it_in_the_history(
        ServiceTierAction action, GatewayVerb verb, UpgradeActionKind kind)
    {
        await SetPolicyAsync();

        var id = await RequestAsync(action);

        await using var ctx = _db.NewContext();
        var command = await ctx.GatewayCommands.AsNoTracking().SingleAsync();
        command.GatewayId.Should().Be(_gatewayId);
        command.Verb.Should().Be(verb);
        command.RequestedByUserId.Should().Be(_owner.Id);
        command.StartedByAgent.Should().BeFalse();
        var parameters = JsonNode.Parse(command.ParametersJson)!.AsObject();
        parameters["machine"]!.GetValue<string>().Should().Be("CRONUS-BC01");
        parameters["instance"]!.GetValue<string>().Should().Be("BC250", "the group's other tiers come from the customer's settings, never the command");

        var row = await ctx.OeEnvironmentUpgradeActions.AsNoTracking().SingleAsync();
        row.Id.Should().Be(id);
        row.Kind.Should().Be(kind);
        row.EnvironmentId.Should().Be(_environmentId);
        row.ProjectId.Should().Be(_projectId);
        row.GatewayCommandId.Should().Be(command.Id);
        row.RequestedByUserId.Should().Be(_owner.Id);
        row.RequestedBy.Should().Contain("owner@cronus.example");
        row.RequestedAt.Should().Be(Start.UtcDateTime);
    }

    [Fact]
    public async Task An_action_is_audited_with_who_and_when()
    {
        await SetPolicyAsync();

        await RequestAsync(ServiceTierAction.Restart);

        await using var ctx = _db.NewContext();
        var audit = await ctx.AuditLog.AsNoTracking().SingleAsync(a => a.EntityType == AuditEntityType.ProjectEnvironment);
        audit.EntityId.Should().Be(_environmentId);
        audit.EntityName.Should().Be("Production");
        audit.ChangedByUserId.Should().Be(_owner.Id);
        audit.ChangedBy.Should().Contain("owner@cronus.example");
        audit.Timestamp.Should().Be(Start.UtcDateTime);
        var snapshot = JsonNode.Parse(audit.SnapshotJson!)!.AsObject();
        snapshot["Action"]!.GetValue<string>().Should().Be("Restart service tier");
        snapshot["ServiceTier"]!.GetValue<string>().Should().Be("CRONUS-BC01 / BC250");
        snapshot["GatewayCommandId"]!.GetValue<long>().Should().Be((await ctx.GatewayCommands.SingleAsync()).Id);
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_is_refused_and_nothing_is_sent()
    {
        await SetPolicyAsync();
        await SetVisibilityAsync(ProjectVisibility.ReadOnly);
        ActAs(_stranger);

        var act = () => RequestAsync(ServiceTierAction.Restart);

        await act.Should().ThrowAsync<ProjectAccessDeniedException>();
        await NothingSentAsync();
        await using var ctx = _db.NewContext();
        var view = await Service(ctx).GetViewAsync(_environmentId);
        view!.CanManage.Should().BeFalse("they still see the card, without the buttons");
    }

    // ---------------------------------------------------------------- the customer's settings

    [Fact]
    public async Task A_tier_whose_settings_do_not_allow_service_control_is_refused()
    {
        await SetPolicyAsync(bc01Allow: ["inventory", "restart"]);

        var act = () => RequestAsync(ServiceTierAction.Stop);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().StartWith(OnPremisesDeployFacts.NotAllowed)
            .And.Contain("CRONUS-BC01 / BC250 may not be stopped, started or restarted");
        await NothingSentAsync();
    }

    [Fact]
    public async Task Restarting_all_tiers_needs_service_control_on_every_one_but_a_single_restart_does_not()
    {
        await SetPolicyAsync(bc02Allow: ["inventory", "restart"]);

        var all = () => RequestAsync(ServiceTierAction.RestartAll);
        (await all.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"].Should().Contain("CRONUS-BC02 / BC250 may not be");
        await NothingSentAsync();

        await RequestAsync(ServiceTierAction.Restart);
        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_offline_gateway_is_refused()
    {
        await SetPolicyAsync();
        await using (var ctx = _db.NewContext())
        {
            await ctx.Gateways.Where(g => g.Id == _gatewayId)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastSeenAt, Start.UtcDateTime.AddHours(-2)));
        }

        var act = () => RequestAsync(ServiceTierAction.Restart);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"].Should().Contain("The gateway is offline");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_second_action_waits_for_the_first_to_finish()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.Restart);

        var act = () => RequestAsync(ServiceTierAction.Stop);

        (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"].Should().Contain("still waiting for the gateway");
        await FinishCommandAsync(GatewayCommandStatus.Succeeded, null, new GatewayServiceTierResult { Action = "restart", Done = true }.ToJson());
        await RequestAsync(ServiceTierAction.Stop);
    }

    // ---------------------------------------------------------------- shared_restart

    [Fact]
    public async Task Restarting_all_tiers_is_refused_when_another_solution_uses_one_of_them()
    {
        await SetPolicyAsync();
        await LinkAnotherSolutionAsync(_bc02);

        var act = () => RequestAsync(ServiceTierAction.RestartAll);

        var refused = await act.Should().ThrowAsync<PlanValidationException>();
        refused.Which.Errors["environment"].Should().Be(
            "Restarting CRONUS-BC01 / BC250, CRONUS-BC02 / BC250 would also sign out people using another Solution on the same database. "
            + "Ask the customer's IT to allow shared restarts for this database in the AL Workbench gateway's settings (the shared_restart setting), or to give each Solution its own service tiers. Nothing was sent.");
        await NothingSentAsync();
    }

    [Fact]
    public async Task A_single_tier_restart_or_stop_is_refused_when_another_solution_uses_that_tier_but_a_start_is_not()
    {
        await SetPolicyAsync();
        await LinkAnotherSolutionAsync(_bc01);

        foreach (var action in new[] { ServiceTierAction.Restart, ServiceTierAction.Stop })
        {
            var act = () => RequestAsync(action);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"]
                .Should().Contain("would also sign out people using another Solution on the same service tier").And.Contain("shared_restart");
        }
        await NothingSentAsync();

        // Starting a stopped tier signs nobody out.
        await RequestAsync(ServiceTierAction.Start);
        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.SingleAsync()).Verb.Should().Be(GatewayVerb.StartInstance);
    }

    [Fact]
    public async Task A_single_tier_restart_is_not_refused_because_another_solution_uses_a_different_tier_of_the_database()
    {
        await SetPolicyAsync();
        await LinkAnotherSolutionAsync(_bc02);

        await RequestAsync(ServiceTierAction.Restart);

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.SingleAsync()).Verb.Should().Be(GatewayVerb.RestartInstance);
    }

    [Fact]
    public async Task Shared_restarts_the_settings_allow_go_through()
    {
        await SetPolicyAsync(shared: true);
        await LinkAnotherSolutionAsync(_bc01);

        await RequestAsync(ServiceTierAction.RestartAll);

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.SingleAsync()).Verb.Should().Be(GatewayVerb.RestartGroup);
    }

    // ---------------------------------------------------------------- what the page and the dialog read

    [Fact]
    public async Task The_page_offers_restart_stop_and_restart_all_for_a_running_tier_and_start_for_a_stopped_one()
    {
        await SetPolicyAsync();

        await using (var ctx = _db.NewContext())
        {
            var view = (await Service(ctx).GetViewAsync(_environmentId))!;
            view.CanManage.Should().BeTrue();
            view.TierName.Should().Be("CRONUS-BC01 / BC250");
            view.GroupTierCount.Should().Be(2);
            view.Choices.Select(c => c.Action).Should().Equal(ServiceTierAction.Restart, ServiceTierAction.RestartAll, ServiceTierAction.Stop);
            view.Choices.Should().OnlyContain(c => c.Unavailable == null);
            view.Latest.Should().BeNull();
        }

        await using (var write = _db.NewContext())
        {
            await write.GatewayInstances.Where(i => i.Id == _bc01).ExecuteUpdateAsync(s => s.SetProperty(i => i.State, "Stopped"));
        }
        await using (var ctx = _db.NewContext())
        {
            (await Service(ctx).GetViewAsync(_environmentId))!.Choices.Select(c => c.Action)
                .Should().Equal(ServiceTierAction.RestartAll, ServiceTierAction.Start);
        }
    }

    [Fact]
    public async Task The_plan_counts_the_sessions_on_the_tiers_and_across_the_database()
    {
        await SetPolicyAsync();
        await using (var write = _db.NewContext())
        {
            await write.GatewayInstances.Where(i => i.Id == _bc01).ExecuteUpdateAsync(s => s.SetProperty(i => i.SessionCount, 7));
            await write.GatewayInstances.Where(i => i.Id == _bc02).ExecuteUpdateAsync(s => s.SetProperty(i => i.SessionCount, 5));
        }

        await using var ctx = _db.NewContext();
        var single = (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.Restart))!;
        single.Tiers.Should().Equal("CRONUS-BC01 / BC250");
        single.Sessions.Should().Be(new ServiceTierSessions(7, 12, 2, Start.UtcDateTime));
        single.IsProduction.Should().BeTrue("Production asks again before anyone is signed out");
        single.SolutionName.Should().Be("CRONUS Coffee");
        single.Refusal.Should().BeNull();

        var all = (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.RestartAll))!;
        all.Tiers.Should().Equal("CRONUS-BC01 / BC250", "CRONUS-BC02 / BC250");
        all.Sessions.OnTiers.Should().Be(12);

        (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.Start))!.SignsPeopleOut.Should().BeFalse();
    }

    [Fact]
    public async Task The_history_reads_the_action_from_its_command_while_it_runs_and_when_it_is_done()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.Restart);

        var waiting = await LatestAsync();
        waiting.Status.Should().Be(UpgradeActionStatus.Pending);
        waiting.TierProgress.Should().Be(ServiceTierProgress.Waiting);
        waiting.TierName.Should().Be("CRONUS-BC01 / BC250");

        await using (var write = _db.NewContext())
        {
            await write.GatewayCommands.ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, GatewayCommandStatus.Running));
        }
        (await LatestAsync()).TierProgress.Should().Be(ServiceTierProgress.Running);

        await FinishCommandAsync(GatewayCommandStatus.Succeeded, null,
            new GatewayServiceTierResult { Action = "restart", Machine = "CRONUS-BC01", Instance = "BC250", Done = true, Before = "Running", State = "Running" }.ToJson());
        var done = await LatestAsync();
        done.Status.Should().Be(UpgradeActionStatus.Sent);
        done.TierProgress.Should().BeNull();
        done.Outcome.Should().Be("CRONUS-BC01 / BC250 restarted and is running.");
    }

    [Fact]
    public async Task A_refusal_by_the_customers_settings_is_said_without_their_file_and_a_failed_restart_with_its_reason()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.Stop);
        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "Not allowed by the customer's gateway settings: CRONUS-BC01 / BC250 does not allow 'service_control'. Nothing was changed.", null);

        var refused = await LatestAsync();
        refused.Status.Should().Be(UpgradeActionStatus.Failed);
        refused.Outcome.Should().Be(ServiceTierActionOutcome.RefusedMessage);

        await FinishCommandAsync(GatewayCommandStatus.Failed,
            "CRONUS-BC01 / BC250 did not stop within 5 minutes; it was StopPending when the gateway stopped waiting.",
            new GatewayServiceTierResult { Action = "stop", Done = false, State = "StopPending" }.ToJson());
        (await LatestAsync()).Outcome.Should().StartWith("CRONUS-BC01 / BC250 did not stop within 5 minutes");
    }

    [Fact]
    public async Task The_history_of_the_whole_environment_reads_tier_actions_as_well()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.RestartAll);
        await FinishCommandAsync(GatewayCommandStatus.Succeeded, null, new GatewayRestartGroupResult
        {
            Mode = "stop-all-then-start-all",
            Restarted = true,
            Tiers =
            [
                new GatewayRestartTierResult { Machine = "CRONUS-BC01", Instance = "BC250", Outcome = GatewayRestartTierOutcomes.Restarted },
                new GatewayRestartTierResult { Machine = "CRONUS-BC02", Instance = "BC250", Outcome = GatewayRestartTierOutcomes.Restarted },
            ],
        }.ToJson());

        await using var ctx = _db.NewContext();
        var rows = await ctx.OeEnvironmentUpgradeActions.AsNoTracking()
            .Select(a => new UpgradeActionRow(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status, a.RequestedBy, a.RequestedAt,
                a.ExecuteAfter, a.SentAt, a.Outcome, a.CancelledBy, a.CancelledAt, null, null, null, null, null, null, null, null, null, a.GatewayCommandId, null, null))
            .ToListAsync();
        var read = await ServiceTierActionOutcome.ApplyAsync(ctx, rows, CancellationToken.None);
        read.Single().Outcome.Should().Be("Restarted 2 service tiers: CRONUS-BC01 / BC250, CRONUS-BC02 / BC250. They are all running.");
    }

    // ---------------------------------------------------------------- Production

    [Fact]
    public async Task Restarting_a_test_environments_tier_that_also_carries_the_solutions_production_asks_as_production_does()
    {
        await SetPolicyAsync(bc01Environment: "test", bc02Environment: "test");
        // The same tier carries the Solution's Production tenant and a Test tenant.
        int testId;
        await using (var write = _db.NewContext())
        {
            var test = new OeProjectEnvironment
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = _projectId, Name = "Test", Type = "Sandbox",
                Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = _bc01, TenantId = "test",
                Status = BcEnvironmentStatus.Active, FetchedAt = Start.UtcDateTime,
            };
            write.OeProjectEnvironments.Add(test);
            await write.SaveChangesAsync();
            testId = test.Id;
        }

        await using var ctx = _db.NewContext();
        var plan = (await Service(ctx).GetPlanAsync(testId, ServiceTierAction.Restart))!;

        plan.IsProduction.Should().BeTrue("restarting the tier signs out the Production tenant's people too");
        plan.IsOwnProduction.Should().BeFalse();
        plan.OtherProduction.Should().Equal("CRONUS Coffee Production");
    }

    [Fact]
    public async Task Restarting_all_tiers_from_a_test_environment_asks_as_production_when_the_settings_call_a_tier_in_the_group_production()
    {
        await SetPolicyAsync(bc01Environment: "test", bc02Environment: "production");
        await using (var write = _db.NewContext())
        {
            await write.OeProjectEnvironments.Where(e => e.Id == _environmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, "Sandbox").SetProperty(e => e.Name, "Test"));
        }

        await using var ctx = _db.NewContext();
        (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.Restart))!.IsProduction
            .Should().BeFalse("the environment's own tier is a test tier, and nothing Production uses it");
        var all = (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.RestartAll))!;
        all.IsProduction.Should().BeTrue("CRONUS-BC02 / BC250 is production in the customer's settings");
        all.OtherProduction.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- one thing at a time

    [Fact]
    public async Task Two_requests_at_once_send_one_and_tell_the_other_what_is_running()
    {
        await SetPolicyAsync();

        async Task<string?> Attempt(ServiceTierAction action)
        {
            await using var ctx = _db.NewContext();
            try
            {
                await Service(ctx).RequestAsync(_environmentId, action);
                return null;
            }
            catch (PlanValidationException ex)
            {
                return ex.Errors["environment"];
            }
        }

        var outcomes = await Task.WhenAll(Attempt(ServiceTierAction.Restart), Attempt(ServiceTierAction.Stop));

        outcomes.Count(o => o is null).Should().Be(1, "the gateway's lock lets one through, whichever arrives first");
        outcomes.Single(o => o is not null).Should().MatchRegex("^A (restart|stop) of CRONUS-BC01 / BC250 is still waiting for the gateway; try again when it has finished\\.$");
        await using var check = _db.NewContext();
        (await check.GatewayCommands.CountAsync()).Should().Be(1);
        (await check.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_deployment_running_on_the_database_holds_back_every_action_on_its_tiers()
    {
        await SetPolicyAsync();
        await AddCommandAsync(GatewayVerb.PublishInstall, GatewayCommandStatus.Running, "CRONUS-BC01", "BC250");

        foreach (var action in new[] { ServiceTierAction.Restart, ServiceTierAction.RestartAll, ServiceTierAction.Stop })
        {
            var act = () => RequestAsync(action);
            (await act.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"]
                .Should().Be("A deployment to this server is still running; try again when it has finished.");
        }

        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(1, "only the deployment");
        (await ctx.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(0);
        (await Service(ctx).GetViewAsync(_environmentId))!.Choices
            .Should().OnlyContain(c => c.Unavailable == "A deployment to this server is still running; try again when it has finished.");
    }

    [Fact]
    public async Task Work_on_another_tier_of_the_gateway_does_not_hold_this_one_back()
    {
        await SetPolicyAsync();
        // A stop of BC02 alone does not touch BC01, so restarting BC01 alone may go ahead.
        await AddCommandAsync(GatewayVerb.StopInstance, GatewayCommandStatus.Running, "CRONUS-BC02", "BC250");

        await RequestAsync(ServiceTierAction.Restart);

        var all = () => RequestAsync(ServiceTierAction.RestartAll);
        (await all.Should().ThrowAsync<PlanValidationException>()).Which.Errors["environment"]
            .Should().Be("A stop of CRONUS-BC02 / BC250 is still running; try again when it has finished.");
    }

    // ---------------------------------------------------------------- after the action

    [Fact]
    public async Task A_tier_just_stopped_offers_start_at_once_rather_than_after_the_next_inventory()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.Stop);
        await using (var write = _db.NewContext())
        {
            await write.GatewayCommands.ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Succeeded)
                .SetProperty(c => c.FinishedAt, Start.UtcDateTime.AddMinutes(2))
                .SetProperty(c => c.ResultJson, new GatewayServiceTierResult
                {
                    Action = "stop", Machine = "CRONUS-BC01", Instance = "BC250", Done = true, Before = "Running", State = "Stopped",
                }.ToJson().ToJsonString()));
        }

        await using (var ctx = _db.NewContext())
        {
            var view = (await Service(ctx).GetViewAsync(_environmentId))!;
            view.State.Should().Be("Stopped");
            view.StateFromAction.Should().BeTrue("the inventory still reads it Running from before the stop");
            view.Choices.Select(c => c.Action).Should().Equal(ServiceTierAction.RestartAll, ServiceTierAction.Start);
        }

        // The next inventory, after the stop, is believed again.
        await using (var write = _db.NewContext())
        {
            await write.GatewayInstances.Where(i => i.Id == _bc01).ExecuteUpdateAsync(s => s
                .SetProperty(i => i.ReadAt, Start.UtcDateTime.AddMinutes(10))
                .SetProperty(i => i.State, "Stopped"));
        }
        await using (var ctx = _db.NewContext())
        {
            var view = (await Service(ctx).GetViewAsync(_environmentId))!;
            view.StateFromAction.Should().BeFalse();
            view.Choices.Select(c => c.Action).Should().Contain(ServiceTierAction.Start);
        }
    }

    [Fact]
    public async Task A_tier_with_no_session_count_makes_the_number_a_floor()
    {
        await SetPolicyAsync();
        await using (var write = _db.NewContext())
        {
            await write.GatewayInstances.Where(i => i.Id == _bc01).ExecuteUpdateAsync(s => s.SetProperty(i => i.SessionCount, 7));
            await write.GatewayInstances.Where(i => i.Id == _bc02).ExecuteUpdateAsync(s => s.SetProperty(i => i.SessionCount, (int?)null));
        }

        await using var ctx = _db.NewContext();
        var all = (await Service(ctx).GetPlanAsync(_environmentId, ServiceTierAction.RestartAll))!;

        all.Sessions.OnTiers.Should().Be(7);
        all.Sessions.Uncounted.Should().Be(1);
    }

    [Fact]
    public async Task A_save_that_fails_after_the_command_was_queued_leaves_nothing_behind_for_the_next_request()
    {
        await SetPolicyAsync();
        var failing = new FailFirstSaveOf<OeEnvironmentUpgradeAction>();
        await using var ctx = _db.NewContext(failing);
        var service = Service(ctx);

        var first = () => service.RequestAsync(_environmentId, ServiceTierAction.Restart);
        await first.Should().ThrowAsync<InvalidOperationException>();
        failing.Failed.Should().BeTrue();
        await NothingSentAsync();

        // Same scoped context, as the page would use: the rolled-back rows must not ride along.
        await service.RequestAsync(_environmentId, ServiceTierAction.Restart);
        await using var check = _db.NewContext();
        var command = await check.GatewayCommands.SingleAsync();
        (await check.OeEnvironmentUpgradeActions.SingleAsync()).GatewayCommandId.Should().Be(command.Id);
        (await check.AuditLog.CountAsync(a => a.EntityType == AuditEntityType.ProjectEnvironment)).Should().Be(1);
    }

    [Fact]
    public async Task The_history_names_the_database_a_restart_of_every_tier_was_for()
    {
        await SetPolicyAsync();
        await RequestAsync(ServiceTierAction.RestartAll);

        (await LatestAsync()).TierDatabase.Should().Be("CRONUS-SQL01 / CRONUS");
    }

    // ---------------------------------------------------------------- building blocks

    private async Task AddCommandAsync(GatewayVerb verb, GatewayCommandStatus status, string machine, string instance)
    {
        await using var ctx = _db.NewContext();
        var now = Start.UtcDateTime;
        ctx.GatewayCommands.Add(new GatewayCommand
        {
            OrganizationId = TestDb.DefaultOrgId, GatewayId = _gatewayId, Verb = verb, Status = status,
            ParametersJson = new GatewayServiceTierParameters { Machine = machine, Instance = instance }.ToParameters().ToJsonString(),
            CreatedAt = now, DueAt = now, ExpiresAt = now.AddMinutes(15),
        });
        await ctx.SaveChangesAsync();
    }

    private OnPremisesTierActionService Service(AppDbContext ctx) => new(
        ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
        new GatewayCommandService(ctx, _db.OrgContext, new GatewayCommandSignal(), _clock, NullLogger<GatewayCommandService>.Instance),
        _clock, NullLogger<OnPremisesTierActionService>.Instance);

    private async Task<int> RequestAsync(ServiceTierAction action)
    {
        await using var ctx = _db.NewContext();
        return await Service(ctx).RequestAsync(_environmentId, action);
    }

    private async Task<UpgradeActionRow> LatestAsync()
    {
        await using var ctx = _db.NewContext();
        return (await Service(ctx).GetViewAsync(_environmentId))!.Latest!;
    }

    private async Task NothingSentAsync()
    {
        await using var ctx = _db.NewContext();
        (await ctx.GatewayCommands.CountAsync()).Should().Be(0, "nothing may reach the gateway");
        (await ctx.OeEnvironmentUpgradeActions.CountAsync()).Should().Be(0, "nothing is recorded either");
        (await ctx.AuditLog.CountAsync(a => a.EntityType == AuditEntityType.ProjectEnvironment)).Should().Be(0);
    }

    private void ActAs(User user)
    {
        _db.OrgContext.CurrentUserId = user.Id;
        _db.OrgContext.IsSiteAdmin = false;
    }

    private async Task SetUpAsync()
    {
        _owner = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Editor, "owner@cronus.example");
        _stranger = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.User, "stranger@cronus.example");
        ActAs(_owner);

        var now = Start.UtcDateTime;
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Coffee", Visibility = ProjectVisibility.Public,
            CreatedByUserId = _owner.Id, CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        var gateway = new Gateway
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS server room", MachineName = "CRONUS-BC01",
            KeyFingerprint = Guid.NewGuid().ToString("N"),
            State = GatewayState.Approved, RegisteredAt = now, KeyRegisteredAt = now, LastSeenAt = now,
        };
        ctx.Gateways.Add(gateway);
        await ctx.SaveChangesAsync();
        var bc01 = Tier(gateway.Id, "CRONUS-BC01");
        var bc02 = Tier(gateway.Id, "CRONUS-BC02");
        ctx.GatewayInstances.AddRange(bc01, bc02);
        await ctx.SaveChangesAsync();
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = bc01.Id, TenantId = "default",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();

        _projectId = project.Id;
        _gatewayId = gateway.Id;
        _bc01 = bc01.Id;
        _bc02 = bc02.Id;
        _environmentId = env.Id;
    }

    private static GatewayInstance Tier(int gatewayId, string machine) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, GatewayId = gatewayId, Machine = machine, Name = "BC250", State = "Running",
        Version = "25.4.29661.29927", DatabaseServer = "CRONUS-SQL01", DatabaseName = "CRONUS", Multitenant = false,
        FirstReportedAt = Start.UtcDateTime, ReportedAt = Start.UtcDateTime, ReadAt = Start.UtcDateTime,
    };

    /// <summary>The customer's settings: one database published on BC01 that restarts BC01 and BC02.</summary>
    private async Task SetPolicyAsync(string[]? bc01Allow = null, string[]? bc02Allow = null, bool shared = false,
        string bc01Environment = "production", string bc02Environment = "production")
    {
        var policy = new GatewayPolicyReport
        {
            State = GatewayPolicyStates.Valid,
            Instances =
            [
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC01", Name = "BC250", Environment = bc01Environment,
                    Allow = bc01Allow ?? ["inventory", "publish", "restart", "service_control"], Tenants = ["default"],
                },
                new GatewayPolicyInstance
                {
                    Machine = "CRONUS-BC02", Name = "BC250", Environment = bc02Environment,
                    Allow = bc02Allow ?? ["inventory", "restart", "service_control"], Tenants = ["default"],
                },
            ],
            Databases =
            [
                new GatewayPolicyDatabase
                {
                    Name = "CRONUS-SQL01/CRONUS",
                    PublishOn = new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" },
                    Restart =
                    [
                        new GatewayPolicyTier { Machine = "CRONUS-BC01", Instance = "BC250" },
                        new GatewayPolicyTier { Machine = "CRONUS-BC02", Instance = "BC250" },
                    ],
                    SharedRestart = shared,
                },
            ],
        };
        await using var ctx = _db.NewContext();
        await ctx.Gateways.Where(g => g.Id == _gatewayId).ExecuteUpdateAsync(s => s
            .SetProperty(g => g.EffectivePolicyJson, JsonSerializer.Serialize(policy)));
    }

    private async Task SetVisibilityAsync(ProjectVisibility visibility)
    {
        await using var ctx = _db.NewContext();
        await ctx.OeProjects.Where(p => p.Id == _projectId).ExecuteUpdateAsync(s => s.SetProperty(p => p.Visibility, visibility));
    }

    private async Task LinkAnotherSolutionAsync(int instanceId)
    {
        await using var ctx = _db.NewContext();
        var now = Start.UtcDateTime;
        var project = new OeProject { OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS Retail", CreatedAt = now, UpdatedAt = now };
        ctx.OeProjects.Add(project);
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, Project = project, Name = "CRONUS Retail", Type = "Production",
            Kind = OeEnvironmentKind.OnPremises, GatewayInstanceId = instanceId, TenantId = "retail",
            Status = BcEnvironmentStatus.Active, FetchedAt = now,
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Ends every command as the gateway would have.</summary>
    private async Task FinishCommandAsync(GatewayCommandStatus status, string? failure, JsonObject? result)
    {
        await using var ctx = _db.NewContext();
        await ctx.GatewayCommands.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, status)
            .SetProperty(c => c.FailureMessage, failure)
            .SetProperty(c => c.ResultJson, result == null ? null : result.ToJsonString())
            .SetProperty(c => c.FinishedAt, Start.UtcDateTime));
    }
}
