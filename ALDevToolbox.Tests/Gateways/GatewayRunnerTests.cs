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

    private GatewayRunner Runner() => new(_workbench.Client(), _settings, _keys, _runOnce, _executor, _clock, _log,
        new GatewayRunnerOptions(_folder.WorkFolder, "CRONUS-BC01", "1.0.0"), new Random(1362));

    private async Task<GatewayRunOutcome> RunAsync()
    {
        try
        {
            return await Runner().RunAsync(_stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
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
            if (request.Path.EndsWith("/progress", StringComparison.Ordinal) || request.Path.EndsWith("/result", StringComparison.Ordinal))
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
        var runner = new GatewayRunner(_workbench.Client(), _settings, _keys, _runOnce, new NotSupportedCommandExecutor(), _clock, _log,
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
}
