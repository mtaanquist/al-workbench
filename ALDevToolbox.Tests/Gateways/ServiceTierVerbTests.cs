using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.RestartGroup;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// Restarting, stopping and starting one service tier from the environment page (#1371),
/// and restarting the whole group, against the fake service manager behind the fake process
/// runner. Nothing here waits on a clock: the fake answers each step at once. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".
/// </summary>
public sealed class ServiceTierVerbTests : IDisposable
{
    private readonly ScratchFolder _scratch = new();
    private readonly FakeBusinessCentral _server = new();
    private readonly List<string> _progress = [];

    public void Dispose() => _scratch.Dispose();

    /// <summary>
    /// Two tiers on one database (CRONUS-BC01 here, CRONUS-BC02 through the service manager),
    /// and a third on this computer whose settings do not allow service control.
    /// </summary>
    private const string Policy = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "production"
        allow = ["inventory", "restart", "service_control"]

        [[instance]]
        machine = "CRONUS-BC02"
        name = "BC140"
        environment = "production"
        allow = ["inventory", "restart", "service_control"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140TEST"
        environment = "test"
        allow = ["inventory", "restart"]

        [[database]]
        name = "CRONUS-SQL/BC140"
        publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
        restart = [
          { machine = "CRONUS-BC01", instance = "BC140" },
          { machine = "CRONUS-BC02", instance = "BC140" },
        ]

        [[machine]]
        name = "CRONUS-BC02"
        method = "service-manager"
        """;

    private readonly TextPolicy _policy = new(Policy);

    private GatewayCommandContext Command(GatewayVerb verb, string machine = "CRONUS-BC01", string instance = "BC140", bool agent = false) => new(
        new GatewayCommandMessage
        {
            Id = 1371,
            Verb = verb,
            Parameters = new GatewayServiceTierParameters { Machine = machine, Instance = instance }.ToParameters(),
            SecondsLeft = 900,
            StartedByAgent = agent,
        },
        new Dictionary<long, string>(),
        _progress.Add);

    private ServiceTierVerb Verb(IRunOnceStore? ledger = null) => new(_policy, _server.Restarts(ledger: ledger));

    private static GatewayServiceTierResult Result(GatewayCommandOutcome outcome) =>
        GatewayServiceTierResult.FromJson(outcome.Result) ?? throw new InvalidOperationException("No service tier result.");

    // ---------------------------------------------------------------- single tier

    [Fact]
    public async Task Restart_stops_and_starts_only_the_named_tier()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.RestartInstance), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal("stop CRONUS-BC01 / BC140 local", "start CRONUS-BC01 / BC140 local");
        var result = Result(outcome);
        result.Action.Should().Be(GatewayServiceTierActions.Restart);
        result.Done.Should().BeTrue();
        result.Before.Should().Be("Running");
        result.State.Should().Be("Running");
        _server.Timeline.Should().NotContain(e => e.Contains("CRONUS-BC02"), "the other tier of the database is not touched");
    }

    [Fact]
    public async Task Stop_stops_the_tier_and_leaves_it_stopped()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.StopInstance), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal("stop CRONUS-BC01 / BC140 local");
        _server.ServiceStates["CRONUS-BC01 / BC140"].Should().Be("Stopped");
        Result(outcome).Should().BeEquivalentTo(new { Action = "stop", Done = true, Before = "Running", State = "Stopped" });
    }

    [Fact]
    public async Task Start_starts_a_stopped_tier()
    {
        _server.ServiceStates["CRONUS-BC01 / BC140"] = "Stopped";

        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.StartInstance), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal("start CRONUS-BC01 / BC140 local");
        Result(outcome).Should().BeEquivalentTo(new { Action = "start", Done = true, Before = "Stopped", State = "Running" });
    }

    [Fact]
    public async Task A_tier_on_another_computer_is_reached_the_way_the_settings_say()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.StopInstance, "CRONUS-BC02"), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal("stop CRONUS-BC02 / BC140 service-manager");
        _server.Programs.Should().OnlyContain(p => p == "powershell.exe");
    }

    [Fact]
    public async Task A_tier_that_does_not_come_back_fails_the_restart_with_where_it_was_left()
    {
        _server.NeverStarts.Add("CRONUS-BC01 / BC140");

        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.RestartInstance), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().StartWith("CRONUS-BC01 / BC140 did not report running within 10 minutes");
        var result = Result(outcome);
        result.Done.Should().BeFalse();
        result.State.Should().Be("StartPending");
    }

    [Fact]
    public async Task A_stop_that_does_not_finish_is_reported_and_the_tier_is_not_started_again()
    {
        _server.NeverStops.Add("CRONUS-BC01 / BC140");

        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.StopInstance), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("did not stop within 5 minutes");
        _server.Timeline.Should().Equal(["stop CRONUS-BC01 / BC140 local"], "the person asked for it to stop; starting it again would undo that");
    }

    // ---------------------------------------------------------------- the deliberate stop and recovery

    [Fact]
    public async Task A_stop_that_was_asked_for_is_never_recorded_for_the_gateway_to_start_again()
    {
        var path = Path.Combine(_scratch.Path, "run-once.json");
        var store = new FileRunOnceStore(path, new FakeGatewayClock());
        store.MarkStarted(1371);
        var recorded = new List<int>();
        _server.AfterServiceControl = _ => recorded.Add(store.StoppedTiers().Count);

        await Verb(store).ExecuteAsync(Command(GatewayVerb.StopInstance), CancellationToken.None);

        recorded.Should().Equal([0], "a deliberate stop is not a restart cut short");
        // The gateway stops and starts again before the command is finished: nothing is started.
        var restarted = new FileRunOnceStore(path, new FakeGatewayClock());
        await new StoppedTierRecovery(_server.Restarts(), restarted, new ListLogger()).RecoverAsync(_policy.InForce);
        _server.Timeline.Should().Equal("stop CRONUS-BC01 / BC140 local");
        _server.ServiceStates["CRONUS-BC01 / BC140"].Should().Be("Stopped");
    }

    [Fact]
    public async Task A_restart_cut_short_by_the_gateway_stopping_is_still_put_right_on_its_next_start()
    {
        var path = Path.Combine(_scratch.Path, "run-once.json");
        var store = new FileRunOnceStore(path, new FakeGatewayClock());
        store.MarkStarted(1371);
        var recorded = new List<string>();
        _server.AfterServiceControl = entry => recorded.Add(entry + " -> " + string.Join(", ", store.StoppedTiers().GetValueOrDefault(1371) ?? []));

        await Verb(store).ExecuteAsync(Command(GatewayVerb.RestartInstance), CancellationToken.None);

        // Recorded from the stop until the start has answered, then cleared.
        recorded.Should().Equal(
            "stop CRONUS-BC01 / BC140 local -> CRONUS-BC01 / BC140",
            "start CRONUS-BC01 / BC140 local -> CRONUS-BC01 / BC140");
        store.StoppedTiers().GetValueOrDefault(1371).Should().BeNullOrEmpty("the tier is running again, so there is nothing to put right");
    }

    // ---------------------------------------------------------------- the policy file decides

    [Fact]
    public async Task A_tier_without_service_control_is_refused_before_anything_runs()
    {
        foreach (var verb in ServiceTierVerb.Verbs)
        {
            var outcome = await Verb().ExecuteAsync(Command(verb, instance: "BC140TEST"), CancellationToken.None);

            outcome.Succeeded.Should().BeFalse();
            outcome.FailureMessage.Should().Contain("CRONUS-BC01 / BC140TEST does not allow 'service_control'").And.EndWith("Nothing was changed.");
        }
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tier_the_settings_do_not_list_is_refused_before_anything_runs()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.RestartInstance, "CRONUS-BC09"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("is not listed in the customer's gateway settings").And.EndWith("Nothing was changed.");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_AI_assistant_cannot_stop_a_production_tier()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.StopInstance, agent: true), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("AI assistants cannot change on-premises production");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_verb_refuses_a_command_meant_for_another_verb()
    {
        var outcome = await Verb().ExecuteAsync(Command(GatewayVerb.RestartGroup), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().EndWith("Nothing was changed.");
        _server.Runs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the group, from the same page

    [Fact]
    public async Task Restart_all_tiers_restarts_the_database_group_of_the_linked_tier()
    {
        var command = new GatewayCommandContext(
            new GatewayCommandMessage
            {
                Id = 1371,
                Verb = GatewayVerb.RestartGroup,
                Parameters = new GatewayRestartGroupParameters { Machine = "CRONUS-BC02", Instance = "BC140" }.ToParameters(),
                SecondsLeft = 900,
            },
            new Dictionary<long, string>(),
            _progress.Add);

        var outcome = await new RestartGroupVerb(_policy, _server.Restarts()).ExecuteAsync(command, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal(
            "stop CRONUS-BC01 / BC140 local",
            "stop CRONUS-BC02 / BC140 service-manager",
            "start CRONUS-BC01 / BC140 local",
            "start CRONUS-BC02 / BC140 service-manager");
        var result = GatewayRestartGroupResult.FromJson(outcome.Result)!;
        result.Tiers.Select(t => t.Before).Should().Equal("Running", "Running");
    }

    [Fact]
    public void The_result_round_trips_and_a_restart_group_result_is_not_mistaken_for_one()
    {
        var result = new GatewayServiceTierResult { Action = "stop", Machine = "CRONUS-BC01", Instance = "BC140", Done = true, State = "Stopped" };

        GatewayServiceTierResult.FromJson(result.ToJson()).Should().Be(result);
        GatewayServiceTierActions.For(GatewayVerb.RestartGroup).Should().BeNull();
        GatewayServiceTierActions.For(GatewayVerb.StopInstance).Should().Be("stop");
    }
}
