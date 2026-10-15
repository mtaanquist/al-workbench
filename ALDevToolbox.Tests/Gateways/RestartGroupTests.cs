using System.Security.Cryptography;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.PublishInstall;
using ALWorkbench.Gateway.Core.RestartGroup;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The restart group and customer IT's before and after scripts (#1368), against the fake
/// service manager and scripts behind the fake process runner: the order (install, then
/// restart), the two restart modes, a tier that does not come back, and what a failing
/// script does to the deployment. See <c>.design/on-prem-gateway.md</c>, "The restart
/// group, as built".
/// </summary>
public sealed class RestartGroupTests : IDisposable
{
    private const string Before = @"C:\ALWorkbench\before-deploy.ps1";
    private const string After = @"C:\ALWorkbench\after-restart.ps1";

    private static readonly Guid ThisBuild = Guid.Parse("7c1d2e3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f");

    private readonly ScratchFolder _scratch = new();
    private readonly FakeBusinessCentral _server = new();
    private readonly FakeFileAccess _files = new();
    private readonly List<string> _progress = [];

    public RestartGroupTests()
    {
        _files.Existing.Add(Before);
        _files.Existing.Add(After);
    }

    public void Dispose() => _scratch.Dispose();

    /// <summary>A BC 14 database with two tiers: CRONUS-BC01 (this computer) publishes, CRONUS-BC02 is reached through the service manager.</summary>
    private static string Policy(string mode = "stop-all-then-start-all", bool scripts = true, string bc02Method = "service-manager",
        string bc01Allow = """["inventory", "publish", "restart", "scripts", "service_control"]""") => $"""
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "test"
        allow = {bc01Allow}
        allow_unsigned = true
        powershell = "7"

        [[instance]]
        machine = "CRONUS-BC02"
        name = "BC140"
        environment = "test"
        allow = ["inventory", "restart", "service_control"]

        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140TEST"
        environment = "test"
        allow = ["inventory", "restart", "service_control"]

        [[database]]
        name = "CRONUS-SQL/BC140"
        publish_on = {"{"} machine = "CRONUS-BC01", instance = "BC140" {"}"}
        restart = [
          {"{"} machine = "CRONUS-BC01", instance = "BC140" {"}"},
          {"{"} machine = "CRONUS-BC02", instance = "BC140" {"}"},
        ]
        restart_mode = "{mode}"
        {(scripts ? $"before = '{Before}'\nafter = '{After}'" : string.Empty)}

        [[machine]]
        name = "CRONUS-BC02"
        method = "{bc02Method}"
        """;

    private TextPolicy _policy = new(Policy());

    private PublishInstallVerb Verb() => new(
        _policy,
        _server.Runner(),
        new FixedTierLocator(@"C:\Program Files\Microsoft Dynamics 365 Business Central\140\Service", new Version(14, 0, 29537, 0)),
        "CRONUS-BC01",
        _server.Restarts("CRONUS-BC01"),
        _server.ScriptRunner(_files));

    private async Task<GatewayCommandOutcome> DeployAsync()
    {
        var bytes = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0", packageId: ThisBuild);
        var path = Path.Combine(_scratch.Path, "coffee.app");
        await File.WriteAllBytesAsync(path, bytes);
        _server.AppIds["CRONUS Coffee"] = AppFiles.CoffeeId;
        var command = new GatewayCommandMessage
        {
            Id = 1368,
            Verb = GatewayVerb.PublishInstall,
            Parameters = new GatewayPublishInstallParameters
            {
                Machine = "CRONUS-BC01",
                Instance = "BC140",
                UnpublishPrevious = false,
                Apps = [new GatewayPublishInstallApp { File = "coffee.app", Id = AppFiles.CoffeeId, Name = "CRONUS Coffee", Publisher = "CRONUS Partner", Version = "1.4.0.0" }],
            }.ToParameters(),
            SecondsLeft = 900,
            Files = [new GatewayCommandFileMessage { Id = 7, Name = "coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length }],
        };
        return await Verb().ExecuteAsync(new GatewayCommandContext(command, new Dictionary<long, string> { [7] = path }, _progress.Add), CancellationToken.None);
    }

    private static GatewayPublishInstallResult Result(GatewayCommandOutcome outcome) =>
        GatewayPublishInstallResult.FromJson(outcome.Result) ?? throw new InvalidOperationException("No publish-install result.");

    // ---------------------------------------------------------------- order

    [Fact]
    public async Task The_before_script_runs_first_then_the_install_then_the_restart_then_the_after_script()
    {
        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal(
            $"script {Before}",
            "publish CRONUS Coffee 1.4.0.0 Tenant default skip=True",
            "sync CRONUS Coffee 1.4.0.0 default Add",
            "install CRONUS Coffee 1.4.0.0 default",
            "stop CRONUS-BC01 / BC140 local",
            "stop CRONUS-BC02 / BC140 service-manager",
            "start CRONUS-BC01 / BC140 local",
            "start CRONUS-BC02 / BC140 service-manager",
            $"script {After}");
        var result = Result(outcome);
        result.Restarted.Should().BeTrue();
        result.Restart!.Mode.Should().Be("stop-all-then-start-all");
        result.Restart.Tiers.Select(t => $"{t.Machine} / {t.Instance} {t.Outcome}").Should().Equal(
            "CRONUS-BC01 / BC140 restarted", "CRONUS-BC02 / BC140 restarted");
        result.Scripts.Select(s => $"{s.Stage} {s.Outcome}").Should().Equal("before succeeded", "after succeeded");
        _progress.Should().Contain("Restarted CRONUS-BC01 / BC140.").And.Contain("Restarted CRONUS-BC02 / BC140.");
    }

    [Fact]
    public async Task Only_the_tiers_the_policy_lists_for_the_database_are_restarted()
    {
        await DeployAsync();

        _server.Runs.Where(r => r.Operation == "service-control")
            .Select(r => r.Parameters["service"]!.GetValue<string>() + "@" + r.Parameters["machine"]!.GetValue<string>())
            .Distinct()
            .Should().BeEquivalentTo(["MicrosoftDynamicsNavServer$BC140@", "MicrosoftDynamicsNavServer$BC140@CRONUS-BC02"],
                "BC140TEST shares the computer but not the database, so it is left alone");
    }

    [Fact]
    public async Task Service_control_and_scripts_run_in_Windows_PowerShell_whatever_the_tier_uses()
    {
        await DeployAsync();

        _server.Programs.Should().NotBeEmpty().And.OnlyContain(p => p == "powershell.exe",
            "Get-Service -ComputerName only exists in Windows PowerShell 5.1");
    }

    [Fact]
    public async Task A_tier_reached_by_PowerShell_remoting_is_controlled_through_it()
    {
        _policy = new TextPolicy(Policy(bc02Method: "powershell-remoting"));

        await DeployAsync();

        _server.Timeline.Should().Contain("stop CRONUS-BC02 / BC140 powershell-remoting").And.Contain("start CRONUS-BC02 / BC140 powershell-remoting");
    }

    [Fact]
    public async Task Nothing_installed_restarts_nothing()
    {
        _server.Publish("CRONUS Coffee", "1.4.0.0", tenant: "default", packageId: ThisBuild);
        _server.Installed[("default", "CRONUS Coffee")] = new Version(1, 4, 0, 0);

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal($"script {Before}");
        var result = Result(outcome);
        result.Restarted.Should().BeFalse();
        result.Restart!.Message.Should().Contain("nothing changed");
        result.Scripts.Last().Outcome.Should().Be(GatewayScriptOutcomes.NotRun);
    }

    // ---------------------------------------------------------------- modes

    [Fact]
    public async Task One_by_one_brings_each_tier_back_before_the_next_goes_down()
    {
        _policy = new TextPolicy(Policy(mode: "one-by-one", scripts: false));

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Where(s => s.StartsWith("stop", StringComparison.Ordinal) || s.StartsWith("start", StringComparison.Ordinal))
            .Should().Equal(
                "stop CRONUS-BC01 / BC140 local",
                "start CRONUS-BC01 / BC140 local",
                "stop CRONUS-BC02 / BC140 service-manager",
                "start CRONUS-BC02 / BC140 service-manager");
        Result(outcome).Restart!.Mode.Should().Be("one-by-one");
    }

    // ---------------------------------------------------------------- a tier that does not come back

    [Fact]
    public async Task A_tier_that_does_not_report_running_in_time_fails_the_deployment_with_the_apps_installed()
    {
        _server.NeverStarts.Add("CRONUS-BC02 / BC140");

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().StartWith("All apps were installed, but the service tiers were not all restarted: CRONUS-BC02 / BC140 did not report running within 10 minutes");
        var result = Result(outcome);
        result.Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Installed);
        result.Restarted.Should().BeFalse();
        result.Restart!.Tiers.Select(t => t.Outcome).Should().Equal(GatewayRestartTierOutcomes.Restarted, GatewayRestartTierOutcomes.Failed);
        result.Restart.Tiers[1].State.Should().Be("StartPending");
        result.Scripts.Select(s => $"{s.Stage} {s.Outcome}").Should().Equal("before succeeded", "after not-run");
        _server.Timeline.Should().NotContain($"script {After}");
    }

    [Fact]
    public async Task A_tier_that_does_not_stop_ends_the_stopping_and_is_itself_started_again()
    {
        _server.NeverStops.Add("CRONUS-BC01 / BC140");

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.Timeline.Where(s => s.StartsWith("stop", StringComparison.Ordinal) || s.StartsWith("start", StringComparison.Ordinal))
            .Should().Equal("stop CRONUS-BC01 / BC140 local", "start CRONUS-BC01 / BC140 local");
        var tiers = Result(outcome).Restart!.Tiers;
        tiers.Select(t => t.Outcome).Should().Equal(GatewayRestartTierOutcomes.Failed, GatewayRestartTierOutcomes.NotRestarted);
        tiers[0].Message.Should().StartWith("CRONUS-BC01 / BC140 did not stop within 5 minutes")
            .And.EndWith("The gateway started it again, and it is running.", "the result says where the tier was left");
        tiers[1].Message.Should().Be("Not restarted, because CRONUS-BC01 / BC140 did not stop.");
        _server.ServiceStates["CRONUS-BC01 / BC140"].Should().Be("Running", "a stop that was sent is always followed by a start");
    }

    [Fact]
    public async Task A_tier_whose_PowerShell_dies_on_the_stop_is_still_sent_a_start()
    {
        _server.ServiceErrors["CRONUS-BC02 / BC140"] = "The RPC server is unavailable.";

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.Timeline.Should().NotContain(t => t.Contains("CRONUS-BC02", StringComparison.Ordinal), "the fake records only what reached the tier");
        _server.Runs.Where(r => r.Operation == "service-control" && r.Parameters["machine"]!.GetValue<string>() == "CRONUS-BC02")
            .Select(r => r.Parameters["action"]!.GetValue<string>())
            .Should().Equal(["stop", "start"], "the stop may have gone through before PowerShell died");
        var bc02 = Result(outcome).Restart!.Tiers[1];
        bc02.Outcome.Should().Be(GatewayRestartTierOutcomes.Failed);
        bc02.Message.Should().Contain("could not be told to stop").And.Contain("Then: CRONUS-BC02 / BC140 could not be told to start");
    }

    [Fact]
    public async Task One_by_one_starts_a_tier_that_did_not_stop_and_leaves_the_rest_alone()
    {
        _policy = new TextPolicy(Policy(mode: "one-by-one", scripts: false));
        _server.NeverStops.Add("CRONUS-BC01 / BC140");

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.Timeline.Where(s => s.StartsWith("stop", StringComparison.Ordinal) || s.StartsWith("start", StringComparison.Ordinal))
            .Should().Equal("stop CRONUS-BC01 / BC140 local", "start CRONUS-BC01 / BC140 local");
        var tiers = Result(outcome).Restart!.Tiers;
        tiers[0].Message.Should().EndWith("The gateway started it again, and it is running.");
        tiers[1].Message.Should().Be("Not restarted, because CRONUS-BC01 / BC140 did not stop.");
    }

    // ---------------------------------------------------------------- stopped part-way

    [Fact]
    public async Task A_command_stopped_with_a_tier_down_still_starts_it_and_reports_the_rest_as_not_restarted()
    {
        using var stop = new CancellationTokenSource();
        _server.AfterServiceControl = entry =>
        {
            // AL Workbench fails the command (or the service stops) right after the first stop.
            if (entry == "stop CRONUS-BC01 / BC140 local") stop.Cancel();
        };
        var verb = new RestartGroupVerb(_policy, _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC01", "BC140"), stop.Token);

        outcome.Succeeded.Should().BeFalse("the group was not restarted, but the verb returns its result rather than throwing it away");
        _server.Timeline.Should().Equal("stop CRONUS-BC01 / BC140 local", "start CRONUS-BC01 / BC140 local");
        _server.ServiceStates["CRONUS-BC01 / BC140"].Should().Be("Running");
        var result = GatewayRestartGroupResult.FromJson(outcome.Result)!;
        result.Restarted.Should().BeFalse();
        result.Tiers[0].Message.Should().Be("Stopped and started again, but the group was not restarted, because the command was stopped.");
        result.Tiers[1].Outcome.Should().Be(GatewayRestartTierOutcomes.NotRestarted);
        result.Tiers[1].Message.Should().Be("Not restarted, because the command was stopped before the gateway reached it.");
    }

    [Fact]
    public async Task A_deployment_stopped_during_the_restart_skips_the_after_script_and_keeps_its_result()
    {
        using var stop = new CancellationTokenSource();
        _server.AfterServiceControl = entry =>
        {
            if (entry == "start CRONUS-BC02 / BC140 service-manager") stop.Cancel();
        };

        var outcome = await Verb().ExecuteAsync(await CommandAsync(), stop.Token);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be($"All apps were installed and the service tiers restarted, but the after script {After} was not run, because the command was stopped.");
        _server.Timeline.Should().NotContain($"script {After}");
        var result = Result(outcome);
        result.Restarted.Should().BeTrue();
        result.Scripts.Last().Outcome.Should().Be(GatewayScriptOutcomes.NotRun);
    }

    [Fact]
    public async Task The_tiers_waiting_for_their_start_are_kept_on_disk_until_it_has_been_tried()
    {
        var store = new FileRunOnceStore(Path.Combine(_scratch.Path, "run-once.json"), new FakeGatewayClock());
        store.MarkStarted(1371);
        var seen = new List<string>();
        _server.AfterServiceControl = entry => seen.Add(entry + " -> " + string.Join(", ", store.StoppedTiers().GetValueOrDefault(1371) ?? []));
        var verb = new RestartGroupVerb(_policy, _server.Restarts(ledger: store));

        await verb.ExecuteAsync(RestartCommand("CRONUS-BC01", "BC140"), CancellationToken.None);

        seen.Should().Equal(
            "stop CRONUS-BC01 / BC140 local -> CRONUS-BC01 / BC140",
            "stop CRONUS-BC02 / BC140 service-manager -> CRONUS-BC01 / BC140, CRONUS-BC02 / BC140",
            "start CRONUS-BC01 / BC140 local -> CRONUS-BC01 / BC140, CRONUS-BC02 / BC140",
            "start CRONUS-BC02 / BC140 service-manager -> CRONUS-BC02 / BC140");
        store.StoppedTiers().Should().BeEmpty();
    }

    [Fact]
    public async Task On_the_next_start_the_gateway_starts_the_tiers_a_stop_left_down_and_reports_the_command_as_failed()
    {
        var path = Path.Combine(_scratch.Path, "run-once.json");
        var before = new FileRunOnceStore(path, new FakeGatewayClock());
        before.MarkStarted(1368);
        before.SetStoppedTiers(1368, [new StoppedTier { Machine = "CRONUS-BC01", Instance = "BC140" }, new StoppedTier { Machine = "CRONUS-BC02", Instance = "BC140" }]);
        _server.ServiceStates["CRONUS-BC01 / BC140"] = "Stopped";
        _server.ServiceStates["CRONUS-BC02 / BC140"] = "Stopped";
        var store = new FileRunOnceStore(path, new FakeGatewayClock());
        var log = new ListLogger();

        await new StoppedTierRecovery(_server.Restarts(), store, log).RecoverAsync(_policy.InForce);

        _server.Timeline.Should().Equal("start CRONUS-BC01 / BC140 local", "start CRONUS-BC02 / BC140 service-manager");
        _server.ServiceStates.Values.Should().AllBe("Running");
        store.StoppedTiers().Should().BeEmpty();
        store.Unfinished().Should().BeEmpty("it is reported with a result, not as merely interrupted");
        var reported = store.PendingResults()[1368];
        reported.Succeeded.Should().BeFalse();
        reported.FailureMessage.Should().Be("The gateway service stopped while it was restarting the service tiers, with CRONUS-BC01 / BC140, CRONUS-BC02 / BC140 stopped. "
            + "When it started again it started them: CRONUS-BC01 / BC140 is running again. CRONUS-BC02 / BC140 is running again.");
        log.Messages(GatewayLog.TiersLeftStoppedId).Should().ContainSingle().Which.Should().Contain("Command 1368 stopped these service tiers");
        log.Messages(GatewayLog.TierStartedAgainId).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_tier_the_gateway_cannot_start_again_is_logged_as_an_error_and_named_in_the_report()
    {
        var store = new FileRunOnceStore(Path.Combine(_scratch.Path, "run-once.json"), new FakeGatewayClock());
        store.MarkStarted(1368);
        store.SetStoppedTiers(1368, [new StoppedTier { Machine = "CRONUS-BC01", Instance = "BC140" }]);
        _server.ServiceStates["CRONUS-BC01 / BC140"] = "Stopped";
        _server.NeverStarts.Add("CRONUS-BC01 / BC140");
        var log = new ListLogger();

        await new StoppedTierRecovery(_server.Restarts(), store, log).RecoverAsync(_policy.InForce);

        log.Messages(GatewayLog.TierNotStartedAgainId).Should().ContainSingle().Which.Should().EndWith("Start it by hand.");
        store.PendingResults()[1368].FailureMessage.Should().Contain("CRONUS-BC01 / BC140 did not report running within 10 minutes");
        store.StoppedTiers().Should().BeEmpty("it is tried once; customer IT has the event");
    }

    [Fact]
    public async Task Recovery_keeps_a_result_that_was_already_saved()
    {
        var store = new FileRunOnceStore(Path.Combine(_scratch.Path, "run-once.json"), new FakeGatewayClock());
        store.MarkStarted(1368);
        store.SetStoppedTiers(1368, [new StoppedTier { Machine = "CRONUS-BC01", Instance = "BC140" }]);
        store.SaveResult(1368, new GatewayCommandResultRequest { Succeeded = false, FailureMessage = "The verb's own result." });

        await new StoppedTierRecovery(_server.Restarts(), store, new ListLogger()).RecoverAsync(_policy.InForce);

        store.PendingResults()[1368].FailureMessage.Should().Be("The verb's own result.");
        _server.Timeline.Should().Equal("start CRONUS-BC01 / BC140 local");
    }

    [Fact]
    public void The_stopped_tiers_survive_a_restart_of_the_gateway_and_go_when_the_command_is_finished()
    {
        var path = Path.Combine(_scratch.Path, "run-once.json");
        var store = new FileRunOnceStore(path, new FakeGatewayClock());
        store.MarkStarted(7);
        store.SetStoppedTiers(7, [new StoppedTier { Machine = "CRONUS-BC01", Instance = "BC140" }]);

        new FileRunOnceStore(path, new FakeGatewayClock()).StoppedTiers()[7].Should().Equal(new StoppedTier { Machine = "CRONUS-BC01", Instance = "BC140" });

        store.MarkFinished(7);
        new FileRunOnceStore(path, new FakeGatewayClock()).StoppedTiers().Should().BeEmpty();
    }

    [Fact]
    public async Task Stopping_all_then_starting_all_starts_every_stopped_tier_even_when_a_later_one_will_not_stop()
    {
        _server.NeverStops.Add("CRONUS-BC02 / BC140");

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.ServiceStates["CRONUS-BC01 / BC140"].Should().Be("Running", "a failure never leaves more tiers down than it found");
        outcome.FailureMessage.Should().Contain("CRONUS-BC02 / BC140 did not stop within 5 minutes");
    }

    [Fact]
    public async Task One_by_one_stops_at_the_first_tier_that_does_not_come_back()
    {
        _policy = new TextPolicy(Policy(mode: "one-by-one", scripts: false));
        _server.NeverStarts.Add("CRONUS-BC01 / BC140");

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.Timeline.Should().NotContain(s => s.Contains("CRONUS-BC02", StringComparison.Ordinal), "one bad restart must not take the other tier down too");
        Result(outcome).Restart!.Tiers[1].Message.Should().Be("Not restarted, because CRONUS-BC01 / BC140 did not come back.");
    }

    [Fact]
    public async Task A_service_manager_that_refuses_the_gateway_fails_that_tier_with_the_reason()
    {
        _server.ServiceErrors["CRONUS-BC02 / BC140"] = "Cannot open MicrosoftDynamicsNavServer$BC140 service on computer 'CRONUS-BC02'.";

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("CRONUS-BC02 / BC140 could not be told to stop: Cannot open");
    }

    [Fact]
    public async Task Settings_that_no_longer_allow_restart_when_the_apps_are_in_restart_nothing()
    {
        // The policy changes between the check at the start and the restart: simulated by
        // a policy whose file, read again before the restart, no longer allows it.
        var changed = new TextPolicy(Policy(bc01Allow: """["inventory", "publish", "scripts", "service_control"]"""));
        var flipping = new FlippingPolicy(_policy, changed);
        var outcome = await new PublishInstallVerb(flipping, _server.Runner(),
            new FixedTierLocator(@"C:\BC\Service", new Version(14, 0, 0, 0)), "CRONUS-BC01", _server.Restarts(), _server.ScriptRunner(_files))
            .ExecuteAsync(await CommandAsync(), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be("All apps were installed, but the customer's gateway settings no longer allow 'restart' on CRONUS-BC01 / BC140, so the service tiers were not restarted.");
        _server.Runs.Should().NotContain(r => r.Operation == "service-control");
    }

    [Fact]
    public async Task The_after_script_comes_from_the_settings_read_again_before_the_restart()
    {
        const string NewAfter = @"C:\ALWorkbench\after-restart-v2.ps1";
        _files.Existing.Add(NewAfter);
        var changed = new TextPolicy(Policy().Replace(After, NewAfter, StringComparison.Ordinal));
        var outcome = await new PublishInstallVerb(new FlippingPolicy(_policy, changed), _server.Runner(),
            new FixedTierLocator(@"C:\BC\Service", new Version(14, 0, 0, 0)), "CRONUS-BC01", _server.Restarts(), _server.ScriptRunner(_files))
            .ExecuteAsync(await CommandAsync(), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Contain($"script {NewAfter}").And.NotContain($"script {After}");
    }

    [Fact]
    public async Task Settings_that_no_longer_allow_scripts_when_the_apps_are_in_skip_the_after_script()
    {
        var changed = new TextPolicy(Policy(bc01Allow: """["inventory", "publish", "restart", "service_control"]"""));
        var outcome = await new PublishInstallVerb(new FlippingPolicy(_policy, changed), _server.Runner(),
            new FixedTierLocator(@"C:\BC\Service", new Version(14, 0, 0, 0)), "CRONUS-BC01", _server.Restarts(), _server.ScriptRunner(_files))
            .ExecuteAsync(await CommandAsync(), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be($"All apps were installed and the service tiers restarted, but the after script {After} was not run: the customer's gateway settings no longer allow 'scripts' on CRONUS-BC01 / BC140.");
        _server.Timeline.Should().NotContain($"script {After}");
        Result(outcome).Scripts.Last().Outcome.Should().Be(GatewayScriptOutcomes.NotRun);
    }

    [Fact]
    public async Task The_restart_group_verb_restarts_the_group_of_the_policy_it_judged()
    {
        // A file read after the check that drops BC02 from the group does not change what
        // was allowed: the verb uses the policy the decision was made on.
        var changed = new TextPolicy(Policy().Replace("""  { machine = "CRONUS-BC02", instance = "BC140" },""", "", StringComparison.Ordinal));
        var verb = new RestartGroupVerb(new FlippingPolicy(_policy, changed, flipOnInForce: true), _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC01", "BC140"), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Contain("stop CRONUS-BC02 / BC140 service-manager");
    }

    private async Task<GatewayCommandContext> CommandAsync()
    {
        var bytes = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0", packageId: ThisBuild);
        var path = Path.Combine(_scratch.Path, "coffee.app");
        await File.WriteAllBytesAsync(path, bytes);
        _server.AppIds["CRONUS Coffee"] = AppFiles.CoffeeId;
        var command = new GatewayCommandMessage
        {
            Id = 1368,
            Verb = GatewayVerb.PublishInstall,
            Parameters = new GatewayPublishInstallParameters
            {
                Machine = "CRONUS-BC01",
                Instance = "BC140",
                UnpublishPrevious = false,
                Apps = [new GatewayPublishInstallApp { File = "coffee.app", Id = AppFiles.CoffeeId, Name = "CRONUS Coffee", Publisher = "CRONUS Partner", Version = "1.4.0.0" }],
            }.ToParameters(),
            SecondsLeft = 900,
            Files = [new GatewayCommandFileMessage { Id = 7, Name = "coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length }],
        };
        return new GatewayCommandContext(command, new Dictionary<long, string> { [7] = path }, _progress.Add);
    }

    /// <summary>Checks commands against one file, then reports another as in force once the file is read again.</summary>
    private sealed class FlippingPolicy(TextPolicy first, TextPolicy then, bool flipOnInForce = false) : ALWorkbench.Gateway.Core.Policy.IGatewayPolicy
    {
        private bool _flipped;

        /// <summary>With <c>flipOnInForce</c>, any read of the policy in force after the check already sees the new file.</summary>
        public ALWorkbench.Gateway.Core.Policy.GatewayPolicy InForce => _flipped || flipOnInForce ? then.InForce : first.InForce;

        public IReadOnlyList<string> Start() => [];

        public ALWorkbench.Gateway.Core.Policy.PolicySnapshot Refresh()
        {
            _flipped = true;
            return then.Refresh();
        }

        public ALWorkbench.Gateway.Core.Policy.PolicyDecision Check(GatewayCommandMessage command) => first.Check(command);
    }

    // ---------------------------------------------------------------- scripts

    [Fact]
    public async Task A_failing_before_script_stops_the_deployment_before_anything_is_published()
    {
        _server.Scripts[Before] = (3, ["Backing up the database...", "The backup share is full."]);

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be($"The before script {Before} failed: it ended with exit code 3. No app was published.");
        _server.Timeline.Should().Equal($"script {Before}");
        _server.Steps.Should().BeEmpty();
        var result = Result(outcome);
        result.Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Skipped);
        result.Apps.Single().Message.Should().Be("Not started, because the before script failed.");
        result.Restart.Should().BeNull();
        result.Scripts.Select(s => $"{s.Stage} {s.Outcome} {s.ExitCode}").Should().Equal("before failed 3", "after not-run ");
        _progress.Should().Contain("before script: The backup share is full.", "the script's own output goes to the deployment log");
    }

    [Fact]
    public async Task A_before_script_that_throws_or_runs_too_long_also_stops_the_deployment()
    {
        _server.ScriptsTimeOut.Add(Before);

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("did not finish within 30 minutes and was stopped");
        _server.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failing_after_script_fails_the_deployment_with_the_install_and_the_restart_reported_as_done()
    {
        _server.Scripts[After] = (2, ["Warming up the web client...", "The web client did not answer."]);

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be($"All apps were installed and the service tiers restarted, but the after script {After} failed: it ended with exit code 2.");
        var result = Result(outcome);
        result.Apps.Single().Outcome.Should().Be(GatewayPublishInstallOutcomes.Installed);
        result.Restarted.Should().BeTrue();
        result.Scripts.Select(s => $"{s.Stage} {s.Outcome} {s.ExitCode}").Should().Equal("before succeeded 0", "after failed 2");
        _progress.Should().Contain("after script: The web client did not answer.");
    }

    [Fact]
    public async Task A_script_someone_other_than_administrators_can_change_is_not_run()
    {
        _files.Problems[Before] = @"BUILTIN\Users can change C:\ALWorkbench\before-deploy.ps1.";

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("someone other than administrators can change it, so it was not run").And.Contain(@"BUILTIN\Users");
        _server.Runs.Should().NotContain(r => r.Operation == "script");
        _server.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task A_script_folder_others_can_change_is_refused_too()
    {
        _files.Problems[@"C:\ALWorkbench"] = @"BUILTIN\Users can change C:\ALWorkbench.";

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        _server.Runs.Should().NotContain(r => r.Operation == "script");
    }

    [Fact]
    public async Task A_folder_above_the_script_others_can_rename_things_in_is_refused_too()
    {
        _files.AncestorProblems[@"C:\"] = @"BUILTIN\Users can rename or replace what is in C:\, which holds a script's folder.";

        var outcome = await DeployAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain(@"BUILTIN\Users can rename or replace what is in C:\");
        _server.Runs.Should().NotContain(r => r.Operation == "script");
        _files.AskedAncestors.Should().Contain(@"C:\", "every folder up to the drive root is checked");
    }

    [Fact]
    public void Every_folder_above_a_script_is_checked_up_to_the_drive_root()
    {
        var files = new FakeFileAccess();

        ALWorkbench.Gateway.Core.Policy.FileAccessChecks.ScriptProblems(files, @"C:\IT\Gateway\scripts\after.ps1");

        files.Asked.Should().Equal(@"C:\IT\Gateway\scripts\after.ps1", @"C:\IT\Gateway\scripts");
        files.AskedAncestors.Should().Equal(@"C:\IT\Gateway", @"C:\IT", @"C:\");
    }

    [Fact]
    public async Task A_script_that_writes_megabytes_of_errors_is_reported_in_a_short_message()
    {
        _server.ScriptErrors[Before] = new string('x', 500_000);

        var outcome = await DeployAsync();

        var before = Result(outcome).Scripts.First();
        before.Message!.Length.Should().BeLessThanOrEqualTo(GatewayStepText.MaxMessageChars);
        before.Message.Should().EndWith("...");
        outcome.FailureMessage!.Length.Should().BeLessThan(3_000, "the result must stay far inside the request limit");
    }

    [Fact]
    public async Task A_tier_failure_with_a_long_error_is_cut_short_too()
    {
        _server.ServiceErrors["CRONUS-BC02 / BC140"] = new string('y', 100_000);

        var outcome = await DeployAsync();

        Result(outcome).Restart!.Tiers[1].Message!.Length.Should().BeLessThanOrEqualTo(GatewayStepText.MaxMessageChars);
    }

    [Fact]
    public void A_script_fails_only_on_an_explicit_exit_or_an_error_that_stops_it()
    {
        // -File reports exit N's N, 1 for a terminating error, and otherwise 0: a trailing
        // robocopy that returns 1 is not the script's own exit code. Checked against a
        // real server in "To verify on real servers".
        CustomerScripts.Run.Script.Should().Contain("-File $path")
            .And.Contain("[int]$LASTEXITCODE")
            .And.NotContain("& $path", "running the script in the gateway's own session would pick up a program's leftover exit code");
    }

    [Fact]
    public async Task The_script_path_reaches_PowerShell_as_data_from_the_policy_file()
    {
        await DeployAsync();

        var script = _server.Runs.First(r => r.Operation == "script").Parameters;
        script["path"]!.GetValue<string>().Should().Be(Before);
        script.Count.Should().Be(1, "nothing from the command is passed to customer IT's script");
    }

    // ---------------------------------------------------------------- the restart-group verb on its own

    private GatewayCommandContext RestartCommand(string machine, string instance, bool agent = false) => new(
        new GatewayCommandMessage
        {
            Id = 1371,
            Verb = GatewayVerb.RestartGroup,
            Parameters = new GatewayRestartGroupParameters { Machine = machine, Instance = instance }.ToParameters(),
            SecondsLeft = 900,
            StartedByAgent = agent,
        },
        new Dictionary<long, string>(),
        _progress.Add);

    [Fact]
    public async Task The_restart_group_verb_restarts_the_whole_group_of_the_tier_it_names_and_runs_no_script()
    {
        var verb = new RestartGroupVerb(_policy, _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC02", "BC140"), CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal(
            "stop CRONUS-BC01 / BC140 local",
            "stop CRONUS-BC02 / BC140 service-manager",
            "start CRONUS-BC01 / BC140 local",
            "start CRONUS-BC02 / BC140 service-manager");
        GatewayRestartGroupResult.FromJson(outcome.Result)!.Restarted.Should().BeTrue();
    }

    [Fact]
    public async Task The_restart_group_verb_refuses_a_tier_the_policy_does_not_list()
    {
        var verb = new RestartGroupVerb(_policy, _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC09", "BC140"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("is not listed in the customer's gateway settings").And.EndWith("Nothing was changed.");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_restart_group_verb_needs_service_control_on_every_tier_of_the_group()
    {
        _policy = new TextPolicy(Policy(bc01Allow: """["inventory", "publish", "restart"]"""));
        var verb = new RestartGroupVerb(_policy, _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC02", "BC140"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("CRONUS-BC01 / BC140 does not allow 'service_control'");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task The_restart_group_verb_reports_a_tier_that_does_not_come_back()
    {
        _server.NeverStarts.Add("CRONUS-BC01 / BC140");
        var verb = new RestartGroupVerb(_policy, _server.Restarts());

        var outcome = await verb.ExecuteAsync(RestartCommand("CRONUS-BC01", "BC140"), CancellationToken.None);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().StartWith("CRONUS-BC01 / BC140 did not report running within 10 minutes");
        var result = GatewayRestartGroupResult.FromJson(outcome.Result)!;
        result.Tiers.Select(t => t.Outcome).Should().Equal(GatewayRestartTierOutcomes.Failed, GatewayRestartTierOutcomes.Restarted);
    }

    // ---------------------------------------------------------------- the script itself

    [Fact]
    public void The_service_control_parameters_name_the_service_from_the_instance_and_leave_this_computer_unnamed()
    {
        var local = ServiceControlScript.Parameters(new("CRONUS-BC01", "BC140"), null, ServiceControlScript.Stop, TimeSpan.FromMinutes(5));
        local["service"]!.GetValue<string>().Should().Be("MicrosoftDynamicsNavServer$BC140");
        local["machine"]!.GetValue<string>().Should().BeEmpty();
        local["method"]!.GetValue<string>().Should().Be("local");
        local["timeoutSeconds"]!.GetValue<int>().Should().Be(300);

        var act = () => ServiceControlScript.Parameters(new("CRONUS-BC01", "BC140"), null, "Stop-Computer", TimeSpan.FromMinutes(5));
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void The_operations_load_no_Business_Central_module()
    {
        ServiceControlScript.Control.NeedsAdminModule.Should().BeFalse();
        CustomerScripts.Run.NeedsAdminModule.Should().BeFalse();
    }
}
