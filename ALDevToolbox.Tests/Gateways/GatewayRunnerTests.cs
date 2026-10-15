using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Services.Gateways;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="GatewayRunner"/>, the service's loop, against a fake AL Workbench and
/// a clock whose waits return at once: back-off and recovery, waiting for approval,
/// stopping on a refused credential, the run-once and deadline refusals, the file
/// hash check and the interrupted-command report after a restart. See
/// <c>.design/on-prem-gateway.md</c>, "Connection model" and "Commands".
/// </summary>
public sealed class GatewayRunnerTests : IDisposable
{
    private const int GatewayId = 7;

    private readonly FakeWorkbench _workbench = new();
    private readonly FakeGatewayClock _clock = new();
    private readonly FakeKeyStore _keys = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly ScratchFolder _scratch = new();
    private readonly RecordingExecutor _executor = new();
    private readonly AllowEveryCommandPolicy _policy = new();
    private readonly ListLogger _log = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly GatewayDataFolder _folder;
    private FileRunOnceStore _runOnce;

    public GatewayRunnerTests()
    {
        _folder = new GatewayDataFolder(_scratch.Path);
        _runOnce = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        var publicKey = _keys.Create("key-1", "NT SERVICE\\ALWorkbenchGateway");
        _settings.Value = new GatewaySettings
        {
            WorkbenchUrl = FakeWorkbench.Url,
            GatewayId = GatewayId,
            Name = "CRONUS-BC01",
            KeyName = "key-1",
            KeyFingerprint = GatewayKeyFingerprint.Compute(publicKey),
        };
    }

    public void Dispose()
    {
        _stop.Dispose();
        _scratch.Dispose();
    }

    private GatewayRunner Runner(TimeSpan? resultGrace = null, IGatewaySettingsStore? settings = null,
        ALWorkbench.Gateway.Core.Inventory.GatewayInventory? inventory = null, ICommandExecutor? executor = null) =>
        new(_workbench.Client(), settings ?? _settings, _keys, _runOnce, executor ?? _executor, _policy, _clock, _log,
            new GatewayRunnerOptions(_folder.WorkFolder, "CRONUS-BC01", "1.0.0")
            {
                ResultGrace = resultGrace ?? GatewayRunnerOptions.DefaultResultGrace,
            },
            new Random(1362), inventory);

    private async Task<GatewayRunOutcome> RunAsync(ALWorkbench.Gateway.Core.Inventory.GatewayInventory? inventory = null, ICommandExecutor? executor = null)
    {
        try
        {
            return await Runner(inventory: inventory, executor: executor).RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("The loop did not stop within 30 seconds. Waits: " + _clock.Delays.Count + "; last requests: " + string.Join(",", _workbench.Requests.Select(r => r.Path).TakeLast(15))
                + "; last log lines: " + string.Join(" | ", _log.Lines.Select(l => l.Message).TakeLast(10)));
        }
    }

    /// <summary>
    /// A command. Its default time to start is long because the fake clock moves a
    /// minute on every heartbeat wait, however quickly the test runs.
    /// </summary>
    private static GatewayCommandMessage Command(long id, int secondsLeft = 10_000_000, params GatewayCommandFileMessage[] files) => new()
    {
        Id = id,
        Verb = GatewayVerb.RefreshInventory,
        Parameters = new JsonObject(),
        SecondsLeft = secondsLeft,
        RequestedBy = "Ida Consultant",
        Files = files,
    };

    /// <summary>
    /// An approved workbench that hands out <paramref name="commands"/> one per poll,
    /// serves <paramref name="files"/>, accepts progress and results, and stops the
    /// loop at the first poll after the last result arrived.
    /// </summary>
    private void Serve(IEnumerable<GatewayCommandMessage> commands, Dictionary<long, byte[]>? files = null)
    {
        var queue = new Queue<GatewayCommandMessage>(commands);
        var expected = queue.Count;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath) return FakeWorkbench.Heartbeat(approved: true);
            if (request.Path == GatewayProtocol.NextCommandPath)
            {
                if (queue.Count > 0) return FakeWorkbench.Json(queue.Dequeue());
                if (Results().Count >= expected) _stop.Cancel();
                return FakeWorkbench.NoContent();
            }
            if (request.Path.EndsWith("/progress", StringComparison.Ordinal) || request.Path.EndsWith("/result", StringComparison.Ordinal)
                || request.Path == GatewayProtocol.InventoryPath)
            {
                return FakeWorkbench.NoContent();
            }
            var fileId = long.Parse(request.Path[(request.Path.LastIndexOf('/') + 1)..]);
            return FakeWorkbench.Bytes(files![fileId]);
        };
    }

    private List<(long Id, GatewayCommandResultRequest Result)> Results() => _workbench.Requests
        .Where(r => r.Path.EndsWith("/result", StringComparison.Ordinal))
        .Select(r => (long.Parse(r.Path.Split('/')[3]), r.Json<GatewayCommandResultRequest>()))
        .ToList();

    // ---------------------------------------------------------------- connection

    [Fact]
    public async Task Every_request_carries_a_token_the_workbench_accepts_for_that_request()
    {
        var heartbeats = 0;
        _workbench.Respond = _ =>
        {
            if (++heartbeats == 2) _stop.Cancel();
            return FakeWorkbench.Heartbeat(approved: false);
        };

        await RunAsync();

        var first = _workbench.Requests.First();
        var token = first.Authorization!["Bearer ".Length..];
        var (validated, refusal) = GatewayTokenValidator.Validate(token, _keys.ExportPublicKey("key-1"), GatewayId,
            new GatewayTokenRequest("POST", GatewayProtocol.HeartbeatPath, GatewayProtocol.AudienceFor(FakeWorkbench.Url), _clock.Start));
        refusal.Should().BeNull();
        GatewayTokenValidator.CheckBody(validated!, GatewayRequestToken.HashBody(System.Text.Encoding.UTF8.GetBytes(first.Body!)), true)
            .Should().BeNull();
        first.Json<GatewayHeartbeatRequest>().MachineName.Should().Be("CRONUS-BC01");
    }

    [Fact]
    public async Task An_unreachable_workbench_is_retried_with_a_growing_back_off_and_the_loop_recovers()
    {
        var attempts = 0;
        _workbench.Respond = request =>
        {
            attempts++;
            if (attempts <= 4) throw new HttpRequestException("No such host is known.");
            if (request.Path == GatewayProtocol.NextCommandPath) _stop.Cancel();
            return request.Path == GatewayProtocol.HeartbeatPath ? FakeWorkbench.Heartbeat(approved: true) : FakeWorkbench.NoContent();
        };

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.Stopped);
        var waits = _clock.Delays.Take(4).ToList();
        for (var i = 0; i < 4; i++)
        {
            var ceiling = Backoff.Ceiling(i + 1);
            waits[i].Should().BeGreaterThanOrEqualTo(ceiling / 2).And.BeLessThanOrEqualTo(ceiling);
        }
        waits[3].Should().BeGreaterThan(waits[0]);
        _log.Messages(GatewayLog.UnreachableId).Should().HaveCount(4);
        _log.Messages(GatewayLog.ReconnectedId).Should().ContainSingle().Which.Should().Contain("4 failed attempts");
        // Recovered: heartbeat first, then the long-poll.
        _workbench.Requests.Skip(4).Select(r => r.Path).Should().Equal(GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath);
    }

    [Fact]
    public async Task The_back_off_stops_growing_at_five_minutes()
    {
        var attempts = 0;
        _workbench.Respond = _ =>
        {
            if (++attempts == 15) _stop.Cancel();
            return FakeWorkbench.Refused(HttpStatusCode.BadGateway, "502 Bad Gateway");
        };

        await RunAsync();

        _clock.Delays.Should().OnlyContain(d => d <= Backoff.Maximum);
        _clock.Delays.Skip(10).Should().OnlyContain(d => d >= Backoff.Maximum / 2);
    }

    [Fact]
    public async Task A_pending_gateway_only_heartbeats_and_polls_once_approved()
    {
        var heartbeats = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.NextCommandPath)
            {
                _stop.Cancel();
                return FakeWorkbench.NoContent();
            }
            heartbeats++;
            return FakeWorkbench.Heartbeat(approved: heartbeats >= 4);
        };

        await RunAsync();

        _workbench.Requests.Select(r => r.Path).Should().Equal(
            GatewayProtocol.HeartbeatPath, GatewayProtocol.HeartbeatPath, GatewayProtocol.HeartbeatPath,
            GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath);
        _clock.Delays.Where(d => d > TimeSpan.Zero).Should().HaveCount(3).And.OnlyContain(d => d == GatewayProtocol.HeartbeatInterval);
        _log.Messages(GatewayLog.WaitingForApprovalId).Should().ContainSingle().Which.Should().Contain(_settings.Value!.KeyFingerprint);
        _log.Messages(GatewayLog.ApprovedId).Should().ContainSingle();
    }

    [Fact]
    public async Task A_403_on_the_long_poll_stops_polling_until_a_heartbeat_says_approved_again()
    {
        var polls = 0;
        var heartbeats = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath) return FakeWorkbench.Heartbeat(approved: ++heartbeats != 2);
            if (++polls == 2) _stop.Cancel();
            return polls == 1
                ? FakeWorkbench.Refused(HttpStatusCode.Forbidden, "This gateway is waiting for an Admin to approve it.")
                : FakeWorkbench.NoContent();
        };

        await RunAsync();

        _workbench.Requests.Select(r => r.Path).Should().Equal(
            GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath, // approved, then 403
            GatewayProtocol.HeartbeatPath,                                  // a minute later: still pending
            GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath); // approved again
    }

    [Fact]
    public async Task A_401_does_not_stop_the_gateway_it_logs_each_attempt_backs_off_and_recovers()
    {
        // A drifted clock answers 401 like a revoked gateway does; once the clock is
        // corrected the gateway must carry on by itself.
        var attempts = 0;
        _workbench.Respond = request =>
        {
            if (++attempts <= 3) return FakeWorkbench.Refused(HttpStatusCode.Unauthorized, "The token was issued in the future; check the gateway computer's clock.");
            if (request.Path == GatewayProtocol.NextCommandPath) _stop.Cancel();
            return request.Path == GatewayProtocol.HeartbeatPath ? FakeWorkbench.Heartbeat(approved: true) : FakeWorkbench.NoContent();
        };

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.Stopped);
        var refusals = _log.Lines.Where(l => l.EventId == GatewayLog.CredentialRefusedId).ToList();
        refusals.Should().HaveCount(3).And.OnlyContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        refusals[0].Message.Should().Contain("check the gateway computer's clock").And.Contain("correct this computer's clock").And.Contain("register");
        var waits = _clock.Delays.Take(3).ToList();
        for (var i = 0; i < 3; i++)
        {
            waits[i].Should().BeGreaterThanOrEqualTo(Backoff.Ceiling(i + 1) / 2).And.BeLessThanOrEqualTo(Backoff.Ceiling(i + 1));
        }
        _workbench.Requests.Skip(3).Select(r => r.Path).Should().Equal(GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath);
    }

    [Fact]
    public async Task A_gateway_refused_for_a_long_time_retries_every_five_minutes_with_one_error_per_attempt()
    {
        var attempts = 0;
        _workbench.Respond = _ =>
        {
            if (++attempts == 30) _stop.Cancel();
            return FakeWorkbench.Refused(HttpStatusCode.Unauthorized, "This gateway has been revoked.");
        };

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.Stopped);
        _log.Messages(GatewayLog.CredentialRefusedId).Should().HaveCount(_clock.Delays.Count + 1);
        _clock.Delays.Should().OnlyContain(d => d <= Backoff.Maximum);
        _clock.Delays.Skip(10).Should().OnlyContain(d => d >= Backoff.Maximum / 2);
    }

    [Fact]
    public async Task A_426_does_not_stop_the_gateway_it_logs_how_to_update_backs_off_and_recovers()
    {
        // AL Workbench was updated past this gateway's version: customer IT replaces the
        // program, and until then the service keeps running and keeps saying why.
        const string reason = "This gateway is version 11.18.0, and this AL Workbench needs version 11.19.0 or later.";
        var attempts = 0;
        _workbench.Respond = request =>
        {
            if (++attempts <= 3) return FakeWorkbench.Refused((HttpStatusCode)GatewayProtocol.OutdatedStatusCode, reason);
            if (request.Path == GatewayProtocol.NextCommandPath) _stop.Cancel();
            return request.Path == GatewayProtocol.HeartbeatPath ? FakeWorkbench.Heartbeat(approved: true) : FakeWorkbench.NoContent();
        };

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.Stopped);
        var refusals = _log.Lines.Where(l => l.EventId == GatewayLog.OutdatedId).ToList();
        refusals.Should().HaveCount(3).And.OnlyContain(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Error);
        refusals[0].Message.Should().Contain(reason).And.Contain("1.0.0")
            .And.Contain("stop the AL Workbench Gateway service").And.Contain("replace ALWorkbench.Gateway.exe");
        _log.Messages(GatewayLog.UnreachableId).Should().BeEmpty("an old gateway is not an unreachable workbench");
        var waits = _clock.Delays.Take(3).ToList();
        for (var i = 0; i < 3; i++)
        {
            waits[i].Should().BeGreaterThanOrEqualTo(Backoff.Ceiling(i + 1) / 2).And.BeLessThanOrEqualTo(Backoff.Ceiling(i + 1));
        }
        _workbench.Requests.Skip(3).Select(r => r.Path).Should().Equal(GatewayProtocol.HeartbeatPath, GatewayProtocol.NextCommandPath);
    }

    [Fact]
    public async Task A_result_refused_as_outdated_is_kept_and_retried()
    {
        Serve([Command(94)]);
        var inner = _workbench.Respond;
        var resultAttempts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.CommandResultPath(94) && ++resultAttempts < 3)
            {
                return FakeWorkbench.Refused((HttpStatusCode)GatewayProtocol.OutdatedStatusCode, "This gateway is too old.");
            }
            return inner(request);
        };

        await RunAsync();

        resultAttempts.Should().Be(3);
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task Interrupted_ids_are_not_reported_again_after_an_approved_heartbeat()
    {
        _runOnce.MarkStarted(72);
        _runOnce = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        var heartbeats = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath)
            {
                if (++heartbeats == 2) _stop.Cancel();
                return FakeWorkbench.Heartbeat(approved: true);
            }
            return FakeWorkbench.NoContent();
        };

        await RunAsync();

        var beats = _workbench.To(GatewayProtocol.HeartbeatPath).Select(r => r.Json<GatewayHeartbeatRequest>()).ToList();
        beats[0].InterruptedCommandIds.Should().Equal(72);
        beats[1].InterruptedCommandIds.Should().BeEmpty();
        new FileRunOnceStore(_folder.RunOnceFile, _clock).Unfinished().Should().BeEmpty();
        _log.Messages(GatewayLog.CommandInterruptedId).Should().ContainSingle().Which.Should().Contain("72");
    }

    [Fact]
    public async Task A_result_refused_while_waiting_for_approval_is_kept_and_retried()
    {
        Serve([Command(93)]);
        var inner = _workbench.Respond;
        var resultAttempts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.CommandResultPath(93) && ++resultAttempts < 3)
            {
                return FakeWorkbench.Refused(HttpStatusCode.Forbidden, "This gateway is waiting for an Admin to approve it.");
            }
            return inner(request);
        };

        await RunAsync();

        resultAttempts.Should().Be(3);
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_refused_with_401_is_kept_and_retried_until_it_arrives()
    {
        Serve([Command(92)]);
        var inner = _workbench.Respond;
        var resultAttempts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.CommandResultPath(92) && ++resultAttempts < 3)
            {
                return FakeWorkbench.Refused(HttpStatusCode.Unauthorized, "The token was issued in the future.");
            }
            return inner(request);
        };

        await RunAsync();

        resultAttempts.Should().Be(3);
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task Without_registration_the_runner_says_so_and_sends_nothing()
    {
        _settings.Value = null;

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.NotRegistered);
        _workbench.Requests.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- commands

    [Fact]
    public async Task A_command_runs_once_and_its_outcome_is_reported_and_logged_with_who_asked()
    {
        _executor.Run = (context, _) =>
        {
            context.Progress("Reading the service tiers");
            return Task.FromResult(GatewayCommandOutcome.Success(new JsonObject { ["instances"] = 2 }));
        };
        Serve([Command(11)]);

        await RunAsync();

        _executor.Calls.Should().ContainSingle().Which.Command.Id.Should().Be(11);
        var (id, result) = Results().Should().ContainSingle().Subject;
        id.Should().Be(11);
        result.Succeeded.Should().BeTrue();
        result.Result!["instances"]!.GetValue<int>().Should().Be(2);
        _workbench.To(GatewayProtocol.CommandProgressPath(11)).Single().Json<GatewayCommandProgressRequest>().Lines
            .Should().Equal("Reading the service tiers");
        _runOnce.HasStarted(11).Should().BeTrue();
        _runOnce.Unfinished().Should().BeEmpty();
        _log.Messages(GatewayLog.CommandStartedId).Should().ContainSingle().Which.Should().Contain("Ida Consultant").And.Contain("refresh-inventory");
        _log.Messages(GatewayLog.CommandSucceededId).Should().ContainSingle().Which.Should().Contain("Ida Consultant");
    }

    [Fact]
    public async Task The_default_executor_fails_every_command_as_not_supported_yet()
    {
        var runner = new GatewayRunner(_workbench.Client(), _settings, _keys, _runOnce, new NotSupportedCommandExecutor(), _policy, _clock, _log,
            new GatewayRunnerOptions(_folder.WorkFolder, "CRONUS-BC01", "1.0.0"), new Random(1));
        Serve([Command(12)]);

        await runner.RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        var result = Results().Single().Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("cannot run 'refresh-inventory' commands yet").And.Contain("Nothing was changed");
        _log.Messages(GatewayLog.CommandFailedId).Should().ContainSingle();
    }

    [Fact]
    public async Task A_command_already_started_once_is_refused_and_never_run_again()
    {
        _runOnce.MarkStarted(21);
        _runOnce.MarkFinished(21);
        Serve([Command(21)]);

        await RunAsync();

        _executor.Calls.Should().BeEmpty();
        var result = Results().Single().Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("never runs a command twice");
        _log.Messages(GatewayLog.CommandRefusedId).Should().ContainSingle().Which.Should().Contain("Ida Consultant");
    }

    [Fact]
    public async Task A_command_with_no_seconds_left_is_refused_and_recorded_so_it_never_runs()
    {
        Serve([Command(31, secondsLeft: 0)]);

        await RunAsync();

        _executor.Calls.Should().BeEmpty();
        Results().Single().Result.FailureMessage.Should().Contain("ran out before the gateway could start it");
        _runOnce.HasStarted(31).Should().BeTrue();
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task Files_are_downloaded_and_checked_before_the_verb_sees_them_and_removed_afterwards()
    {
        var app = "CRONUS Coffee 1.4"u8.ToArray();
        var file = new GatewayCommandFileMessage { Id = 5, Name = @"..\..\CRONUS_Coffee_1.4.0.0.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(app)), Size = app.Length };
        string? seenPath = null;
        _executor.Run = (context, _) =>
        {
            seenPath = context.Files[5];
            return Task.FromResult(GatewayCommandOutcome.Success());
        };
        Serve([Command(41, files: file)], new Dictionary<long, byte[]> { [5] = app });

        await RunAsync();

        _executor.FileContents[5].Should().Equal(app);
        Path.GetDirectoryName(seenPath).Should().Be(Path.Combine(_folder.WorkFolder, "41"));
        Path.GetFileName(seenPath).Should().Be("5-CRONUS_Coffee_1.4.0.0.app");
        Directory.Exists(Path.Combine(_folder.WorkFolder, "41")).Should().BeFalse();
        Results().Single().Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task A_file_whose_hash_does_not_match_fails_the_command_and_is_never_run()
    {
        var sent = "the real app"u8.ToArray();
        var arrived = "a swapped app"u8.ToArray()[..sent.Length];
        var file = new GatewayCommandFileMessage { Id = 6, Name = "CRONUS_Coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(sent)), Size = sent.Length };
        Serve([Command(51, files: file)], new Dictionary<long, byte[]> { [6] = arrived });

        await RunAsync();

        _executor.Calls.Should().BeEmpty();
        var result = Results().Single().Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("CRONUS_Coffee.app").And.Contain("SHA-256 does not match").And.Contain("Nothing was run");
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task A_publish_install_whose_app_arrives_with_another_hash_never_reaches_PowerShell()
    {
        var server = new FakeBusinessCentral();
        var verb = new ALWorkbench.Gateway.Core.PublishInstall.PublishInstallVerb(
            new TextPolicy("""
                [[instance]]
                machine = "CRONUS-BC01"
                name = "BC250"
                environment = "test"
                allow = ["inventory", "publish", "restart"]
                allow_unsigned = true
                """),
            new ALWorkbench.Gateway.Core.PowerShell.PowerShellRunner(server, new("powershell.exe", "pwsh.exe"), new FakeFileAccess()),
            new FixedTierLocator(@"C:\BC\Service"), "CRONUS-BC01");
        var sent = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.4.0.0");
        var arrived = AppFiles.Build(AppFiles.CoffeeId, "CRONUS Coffee", "CRONUS Partner", "1.5.0.0");
        var file = new GatewayCommandFileMessage { Id = 9, Name = "CRONUS_Coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(sent)), Size = arrived.Length };
        var command = Command(53, files: file) with
        {
            Verb = GatewayVerb.PublishInstall,
            Parameters = new GatewayPublishInstallParameters
            {
                Machine = "CRONUS-BC01",
                Instance = "BC250",
                Apps = [new GatewayPublishInstallApp { File = "CRONUS_Coffee.app", Id = AppFiles.CoffeeId, Name = "CRONUS Coffee", Publisher = "CRONUS Partner", Version = "1.4.0.0" }],
            }.ToParameters(),
        };
        Serve([command], new Dictionary<long, byte[]> { [9] = arrived });

        await RunAsync(executor: new VerbExecutors(new Dictionary<GatewayVerb, ICommandExecutor> { [GatewayVerb.PublishInstall] = verb }));

        server.Runs.Should().BeEmpty();
        var result = Results().Single().Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("SHA-256 does not match").And.Contain("Nothing was run");
    }

    [Fact]
    public async Task A_file_larger_than_announced_is_cut_off_and_fails_the_command()
    {
        var sent = "small"u8.ToArray();
        var file = new GatewayCommandFileMessage { Id = 8, Name = "CRONUS.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(sent)), Size = sent.Length };
        Serve([Command(52, files: file)], new Dictionary<long, byte[]> { [8] = new byte[1024] });

        await RunAsync();

        _executor.Calls.Should().BeEmpty();
        Results().Single().Result.FailureMessage.Should().Contain("larger than expected");
    }

    [Fact]
    public async Task Heartbeats_keep_going_while_a_command_runs_and_list_it_as_running()
    {
        var release = new TaskCompletionSource();
        _executor.Run = async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return GatewayCommandOutcome.Success();
        };
        Serve([Command(61)]);
        var inner = _workbench.Respond;
        var beatsWhileRunning = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath
                && request.Json<GatewayHeartbeatRequest>().RunningCommandIds.Contains(61)
                && ++beatsWhileRunning == 3)
            {
                release.SetResult();
            }
            return inner(request);
        };

        await RunAsync();

        beatsWhileRunning.Should().BeGreaterThanOrEqualTo(3);
        Results().Single().Result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task After_a_restart_mid_command_heartbeats_report_it_interrupted_until_an_approved_answer()
    {
        // The previous run started command 71 and stopped before finishing it.
        _runOnce.MarkStarted(71);
        _runOnce = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        var heartbeats = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath)
            {
                // The workbench ignores the lists of a gateway that is not approved, so the
                // first (pending) answer must not count as delivered.
                heartbeats++;
                return FakeWorkbench.Heartbeat(approved: heartbeats >= 2);
            }
            _stop.Cancel();
            return FakeWorkbench.NoContent();
        };

        await RunAsync();

        var beats = _workbench.To(GatewayProtocol.HeartbeatPath).Select(r => r.Json<GatewayHeartbeatRequest>()).ToList();
        beats.Should().HaveCount(2);
        beats[0].InterruptedCommandIds.Should().Equal(71);
        beats[1].InterruptedCommandIds.Should().Equal(71);
        _workbench.Requests.Last().Path.Should().Be(GatewayProtocol.NextCommandPath);
        new FileRunOnceStore(_folder.RunOnceFile, _clock).Unfinished().Should().BeEmpty();
        new FileRunOnceStore(_folder.RunOnceFile, _clock).HasStarted(71).Should().BeTrue();
        _log.Messages(GatewayLog.CommandInterruptedId).Should().ContainSingle().Which.Should().Contain("71");
    }

    [Fact]
    public async Task A_command_cut_short_by_a_stop_stays_unfinished_for_the_next_start_to_report()
    {
        var started = new TaskCompletionSource();
        _executor.Run = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return GatewayCommandOutcome.Success();
        };
        Serve([Command(81)]);
        // Heartbeats go on while the command runs; pace them so the loop does not
        // spin through the clock's limit before the test stops it.
        _clock.Pause = TimeSpan.FromMilliseconds(20);
        var run = Runner().RunAsync(_stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await _stop.CancelAsync();
        (await run.WaitAsync(TimeSpan.FromSeconds(30))).Should().Be(GatewayRunOutcome.Stopped);

        Results().Should().BeEmpty();
        new FileRunOnceStore(_folder.RunOnceFile, _clock).Unfinished().Should().Equal(81);
    }

    [Fact]
    public async Task A_result_that_cannot_be_posted_is_retried_until_it_arrives()
    {
        Serve([Command(91)]);
        var inner = _workbench.Respond;
        var resultAttempts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.CommandResultPath(91) && ++resultAttempts < 3)
            {
                throw new HttpRequestException("The connection was reset.");
            }
            return inner(request);
        };

        await RunAsync();

        resultAttempts.Should().Be(3);
        _runOnce.Unfinished().Should().BeEmpty();
    }

    // ---------------------------------------------------------------- the local policy

    [Fact]
    public async Task A_command_the_policy_refuses_is_reported_failed_with_the_reason_and_nothing_is_downloaded_or_run()
    {
        var app = "CRONUS Coffee 1.4"u8.ToArray();
        var file = new GatewayCommandFileMessage { Id = 5, Name = "CRONUS_Coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(app)), Size = app.Length };
        _policy.Decide = _ => ALWorkbench.Gateway.Core.Policy.PolicyDecision.Refuse(
            "CRONUS-BC09 / BC140 is not listed in the customer's gateway settings (gateway.toml), so the gateway does not touch it.");
        Serve([Command(61, files: file)], new Dictionary<long, byte[]> { [5] = app });

        await RunAsync();

        _policy.Checked.Should().ContainSingle().Which.Id.Should().Be(61);
        _executor.Calls.Should().BeEmpty();
        _workbench.Requests.Should().NotContain(r => r.Path.Contains("/files/", StringComparison.Ordinal));
        var result = Results().Single().Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Be("CRONUS-BC09 / BC140 is not listed in the customer's gateway settings (gateway.toml), so the gateway does not touch it. Nothing was changed.");
        _log.Messages(GatewayLog.CommandRefusedId).Should().ContainSingle().Which.Should().Contain("not listed");
        _runOnce.HasStarted(61).Should().BeTrue();
        _runOnce.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task Loose_permissions_at_start_stop_the_gateway_before_it_sends_anything()
    {
        _policy.StartProblems = ["BUILTIN\\Users can change C:\\Program Files\\AL Workbench Gateway\\gateway.toml."];

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.PolicyUnsafe);
        _workbench.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task The_policy_goes_with_the_first_heartbeat_again_when_it_changes_and_every_fifteen_minutes()
    {
        var heartbeats = 0;
        _workbench.Respond = request =>
        {
            if (request.Path != GatewayProtocol.HeartbeatPath) return FakeWorkbench.NoContent();
            heartbeats++;
            if (heartbeats == 3) _policy.Snapshot = new(new GatewayPolicyReport { State = GatewayPolicyStates.Invalid, Problems = ["Line 4: oops"] }, 2);
            if (heartbeats == 20) _stop.Cancel();
            return FakeWorkbench.Heartbeat(approved: false);
        };

        await RunAsync();

        var sent = _workbench.To(GatewayProtocol.HeartbeatPath)
            .Select((r, i) => (Beat: i + 1, Policy: r.Json<GatewayHeartbeatRequest>().Policy))
            .Where(b => b.Policy is not null)
            .ToList();
        sent.Select(b => b.Beat).Should().Equal(1, 4, 19);
        sent[1].Policy!.State.Should().Be(GatewayPolicyStates.Invalid);
        sent[1].Policy!.Problems.Should().Equal("Line 4: oops");
    }

    // ---------------------------------------------------------------- review fixes

    [Fact]
    public async Task A_downloaded_file_is_held_open_against_writes_from_its_hash_check_until_the_verb_ends()
    {
        var app = "CRONUS Coffee 1.5"u8.ToArray();
        var file = new GatewayCommandFileMessage { Id = 9, Name = "CRONUS_Coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(app)), Size = app.Length };
        string? path = null;
        Exception? write = null;
        var heldOpen = false;
        _executor.Run = (context, _) =>
        {
            path = context.Files[9];
            if (OperatingSystem.IsWindows())
            {
                // Windows enforces the share mode: nobody may write or delete it now.
                write = Record.Exception(() => File.WriteAllBytes(path, "swapped"u8.ToArray()));
                heldOpen = Record.Exception(() => File.Delete(path)) is IOException or UnauthorizedAccessException;
            }
            else
            {
                // Linux does not enforce share modes; check the handle the gateway holds instead.
                heldOpen = OpenedByThisProcess(path);
            }
            return Task.FromResult(GatewayCommandOutcome.Success());
        };
        Serve([Command(42, files: file)], new Dictionary<long, byte[]> { [9] = app });

        await RunAsync();

        heldOpen.Should().BeTrue();
        if (OperatingSystem.IsWindows()) write.Should().BeOfType<IOException>();
        _executor.FileContents[9].Should().Equal(app);
        Results().Single().Result.Succeeded.Should().BeTrue();
        // Released once the verb is done: the working folder could be removed.
        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    // ---------------------------------------------------------------- inventory

    private readonly FakeInventoryReader _reader = new();

    private List<GatewayInventoryReport> InventoryPosts() => _workbench.Requests
        .Where(r => r.Path == GatewayProtocol.InventoryPath)
        .Select(r => r.Json<GatewayInventoryReport>())
        .ToList();

    [Fact]
    public async Task Once_approved_the_gateway_reads_the_readable_tiers_and_sends_each_with_the_roster()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        Serve([]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _reader.Read.Should().Equal("CRONUS-BC01 / BC140", "CRONUS-BC01 / BC250", "CRONUS-BC01 / BC140TEST");
        var posts = InventoryPosts();
        posts.Should().HaveCount(3);
        posts.Should().OnlyContain(p => p.SchemaVersion == GatewayInventoryReport.CurrentSchemaVersion && p.Roster!.Count == 3);
        _executor.Calls.Should().BeEmpty("the scheduled round is not a command");
        _log.Messages(GatewayLog.InventorySentId).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refresh_inventory_command_reads_the_named_tier_itself_without_the_executor()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        var command = Command(21) with
        {
            Parameters = new JsonObject
            {
                [GatewayCommandParameters.Machine] = "CRONUS-BC01",
                [GatewayCommandParameters.Instance] = "BC250",
            },
        };
        Serve([command]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _executor.Calls.Should().BeEmpty();
        var result = Results().Should().ContainSingle().Subject.Result;
        result.Succeeded.Should().BeTrue();
        result.Result!["read"]!.GetValue<int>().Should().Be(1);
        var posts = InventoryPosts();
        posts.Should().HaveCount(4, "three from the round at start, one for the command");
        posts[^1].Roster.Should().BeNull();
        posts[^1].Instances.Should().ContainSingle().Which.Name.Should().Be("BC250");
    }

    [Fact]
    public async Task A_refresh_of_a_tier_the_policy_does_not_let_the_gateway_read_fails()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        var command = Command(22) with
        {
            Parameters = new JsonObject
            {
                [GatewayCommandParameters.Machine] = "CRONUS-BC01",
                [GatewayCommandParameters.Instance] = "BC260",
            },
        };
        Serve([command]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        var result = Results().Should().ContainSingle().Subject.Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("BC260");
    }

    [Fact]
    public async Task After_a_command_that_changes_something_the_tiers_are_read_again()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        Serve([Command(23) with { Verb = GatewayVerb.RestartInstance }]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _executor.Calls.Should().ContainSingle();
        InventoryPosts().Should().HaveCount(6, "one round at start and one after the restart");
    }

    [Fact]
    public async Task Nothing_is_read_while_the_gateways_files_are_not_safe()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        _policy.Snapshot = new(new GatewayPolicyReport { State = GatewayPolicyStates.Unsafe }, 1);
        Serve([]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _reader.Read.Should().BeEmpty();
        InventoryPosts().Should().BeEmpty();
    }

    [Fact]
    public async Task With_no_settings_in_force_nothing_is_read_or_reported_missing()
    {
        // No gateway.toml, or a broken one with no earlier good version: the policy in
        // force is the empty stand-in, and an empty roster would mark every tier missing.
        _policy.InForce = ALWorkbench.Gateway.Core.Policy.GatewayPolicy.Empty;
        Serve([]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _reader.Read.Should().BeEmpty();
        InventoryPosts().Should().BeEmpty();
        _log.Messages(GatewayLog.InventorySkippedId).Should().ContainSingle();
    }

    [Fact]
    public async Task A_refresh_with_no_settings_in_force_fails_without_reporting_anything()
    {
        _policy.InForce = ALWorkbench.Gateway.Core.Policy.GatewayPolicy.Empty;
        Serve([Command(24)]);

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        var result = Results().Should().ContainSingle().Subject.Result;
        result.Succeeded.Should().BeFalse();
        result.FailureMessage.Should().Contain("gateway.toml");
        InventoryPosts().Should().BeEmpty();
    }

    [Fact]
    public async Task A_tier_AL_Workbench_refuses_is_logged_and_does_not_hold_back_the_others()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        var inventoryPosts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath) return FakeWorkbench.Heartbeat(approved: true);
            if (request.Path == GatewayProtocol.InventoryPath)
            {
                var report = request.Json<GatewayInventoryReport>();
                inventoryPosts++;
                return report.Instances.Any(i => i.Name == "BC140")
                    ? FakeWorkbench.Refused(HttpStatusCode.BadRequest, "Too large.")
                    : FakeWorkbench.NoContent();
            }
            _stop.Cancel();
            return FakeWorkbench.NoContent();
        };

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        inventoryPosts.Should().Be(3, "the refused tier did not stop the other two");
        _log.Messages(GatewayLog.InventoryRefusedId).Should().ContainSingle().Which.Should().Contain("BC140");
        _log.Messages(GatewayLog.InventoryNotSentId).Should().BeEmpty("a refused tier is not retried in two minutes");
        _log.Messages(GatewayLog.InventorySentId).Should().ContainSingle();
    }

    [Fact]
    public async Task A_round_that_cannot_be_sent_is_tried_again_two_minutes_later()
    {
        _policy.InForce = GatewayInventoryParsingTests.Policy;
        var attempts = 0;
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath) return FakeWorkbench.Heartbeat(approved: true);
            if (request.Path == GatewayProtocol.InventoryPath)
            {
                return ++attempts == 1 ? FakeWorkbench.Refused(HttpStatusCode.ServiceUnavailable, "Restarting.") : FakeWorkbench.NoContent();
            }
            if (attempts >= 4) _stop.Cancel();
            return FakeWorkbench.NoContent();
        };

        await RunAsync(new ALWorkbench.Gateway.Core.Inventory.GatewayInventory(_reader));

        _log.Messages(GatewayLog.InventoryNotSentId).Should().ContainSingle().Which.Should().Contain("2 minutes");
        attempts.Should().Be(4, "the failed first post, then the three of the retry");
    }

    private static bool OpenedByThisProcess(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var fd in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                if (new FileInfo(fd).LinkTarget == full) return true;
            }
            catch (IOException)
            {
                // A descriptor closed while listing.
            }
        }
        return false;
    }

    [Fact]
    public async Task A_result_ready_as_the_service_stops_is_still_posted_within_the_grace_period()
    {
        _executor.Run = async (_, _) =>
        {
            // The stop arrives just as the verb finishes successfully.
            await _stop.CancelAsync();
            return GatewayCommandOutcome.Success(new JsonObject { ["upgraded"] = 3 });
        };
        Serve([Command(83)]);

        var outcome = await Runner(resultGrace: TimeSpan.FromSeconds(20)).RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        outcome.Should().Be(GatewayRunOutcome.Stopped);
        var (id, result) = Results().Single();
        id.Should().Be(83);
        result.Succeeded.Should().BeTrue();
        var reopened = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        reopened.PendingResults().Should().BeEmpty();
        reopened.Unfinished().Should().BeEmpty();
    }

    [Fact]
    public async Task A_result_that_misses_the_grace_period_is_kept_and_sent_on_the_next_start_not_reported_interrupted()
    {
        _executor.Run = async (_, _) =>
        {
            await _stop.CancelAsync();
            return GatewayCommandOutcome.Success(new JsonObject { ["upgraded"] = 3 });
        };
        Serve([Command(84)]);
        var inner = _workbench.Respond;
        _workbench.Respond = request => request.Path == GatewayProtocol.CommandResultPath(84)
            ? throw new HttpRequestException("The connection was reset.")
            : inner(request);

        var first = await Runner(resultGrace: TimeSpan.FromMilliseconds(300)).RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        first.Should().Be(GatewayRunOutcome.Stopped);
        _runOnce = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        _runOnce.PendingResults().Keys.Should().Equal(84);
        _runOnce.Unfinished().Should().BeEmpty();

        // The next start: AL Workbench is reachable again.
        _workbench.Requests.Clear();
        using var secondStop = new CancellationTokenSource();
        _workbench.Respond = request =>
        {
            if (request.Path == GatewayProtocol.HeartbeatPath) return FakeWorkbench.Heartbeat(approved: true);
            if (request.Path == GatewayProtocol.NextCommandPath) secondStop.Cancel();
            return FakeWorkbench.NoContent();
        };

        var second = await Runner().RunAsync(secondStop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        second.Should().Be(GatewayRunOutcome.Stopped);
        var beat = _workbench.To(GatewayProtocol.HeartbeatPath).First().Json<GatewayHeartbeatRequest>();
        beat.RunningCommandIds.Should().Equal(84);
        beat.InterruptedCommandIds.Should().BeEmpty();
        var (id, result) = Results().Single();
        id.Should().Be(84);
        result.Succeeded.Should().BeTrue();
        result.Result!["upgraded"]!.GetValue<int>().Should().Be(3);
        // Sent before the gateway took anything new.
        _workbench.Requests.Select(r => r.Path).Should().ContainInOrder(GatewayProtocol.CommandResultPath(84), GatewayProtocol.NextCommandPath);
        var reopened = new FileRunOnceStore(_folder.RunOnceFile, _clock);
        reopened.PendingResults().Should().BeEmpty();
        reopened.Unfinished().Should().BeEmpty();
        _log.Messages(GatewayLog.ResultReportedLateId).Should().ContainSingle().Which.Should().Contain("84");
        _log.Messages(GatewayLog.CommandInterruptedId).Should().BeEmpty();
    }

    [Fact]
    public async Task A_damaged_settings_file_ends_the_loop_as_failed_with_a_logged_reason()
    {
        var outcome = await Runner(settings: new ThrowingSettingsStore()).RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        outcome.Should().Be(GatewayRunOutcome.Failed);
        var line = _log.Lines.Single(l => l.EventId == GatewayLog.FailedId);
        line.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Critical);
        line.Message.Should().Contain("gateway.json is damaged");
        _workbench.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task A_key_that_cannot_be_opened_ends_the_loop_as_failed()
    {
        _settings.Value = _settings.Value! with { KeyName = "gone" };

        var outcome = await RunAsync();

        outcome.Should().Be(GatewayRunOutcome.Failed);
        _log.Messages(GatewayLog.FailedId).Should().ContainSingle();
    }

    private sealed class ThrowingSettingsStore : IGatewaySettingsStore
    {
        public GatewaySettings? Load() => throw new InvalidOperationException("gateway.json is damaged.");

        public void Save(GatewaySettings settings) => throw new InvalidOperationException("Not expected.");

        public void Delete() => throw new InvalidOperationException("Not expected.");
    }
}
