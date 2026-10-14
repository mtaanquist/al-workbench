using System.Security.Cryptography;
using ALWorkbench.Gateway.Contracts;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>How <see cref="GatewayRunner.RunAsync"/> ended.</summary>
public enum GatewayRunOutcome
{
    /// <summary>Asked to stop (the service is stopping).</summary>
    Stopped,
    /// <summary>There are no registration settings; nothing to run.</summary>
    NotRegistered,
}

/// <summary>Where the runner keeps per-command files and how it describes itself.</summary>
/// <param name="WorkDirectory">A folder only the service account and Administrators can write; each command's files go in a subfolder, removed when it ends.</param>
/// <param name="MachineName">This computer's name, sent with every heartbeat.</param>
/// <param name="Version">The gateway program's version.</param>
public sealed record GatewayRunnerOptions(string WorkDirectory, string MachineName, string Version);

/// <summary>
/// The service's main loop: heartbeat every 60 seconds, long-poll for the next
/// command while approved, run one command at a time, report its progress and
/// result. One loop rather than several, so the order is plain: the first heartbeat
/// (which reports commands a restart cut short) always goes before the first poll,
/// and heartbeats keep going while a command runs, because a data upgrade can take
/// a long time and a command is only kept alive while heartbeats list it. See
/// <c>.design/on-prem-gateway.md</c>, "Connection model" and "Commands".
///
/// <para>When AL Workbench cannot be reached it backs off (<see cref="Backoff"/>).
/// While the gateway waits for approval it does not poll at all, only heartbeats,
/// and polls again after the first heartbeat that says it is approved. A 401 (revoked,
/// or this computer's clock far off) does not stop it: it logs an error with what to
/// check and keeps trying on the same back-off, so a drifted clock never leaves the
/// customer with a dead service.</para>
/// </summary>
public sealed class GatewayRunner
{
    /// <summary>Attempts at downloading one file before the command fails.</summary>
    public const int DownloadAttempts = 3;

    /// <summary>An empty long-poll that answered sooner than this is followed by a wait, so polls are never back to back.</summary>
    public static readonly TimeSpan MinimumEmptyPoll = TimeSpan.FromSeconds(2);

    private readonly HttpClient _http;
    private readonly IGatewaySettingsStore _settings;
    private readonly ISigningKeyStore _keys;
    private readonly IRunOnceStore _runOnce;
    private readonly ICommandExecutor _executor;
    private readonly IGatewayClock _clock;
    private readonly ILogger _logger;
    private readonly GatewayRunnerOptions _options;
    private readonly Random? _random;

    private readonly Lock _lock = new();
    private readonly HashSet<long> _running = [];
    private readonly List<long> _interrupted = [];
    private bool? _approved;
    private string _fingerprint = string.Empty;

    public GatewayRunner(
        HttpClient http,
        IGatewaySettingsStore settings,
        ISigningKeyStore keys,
        IRunOnceStore runOnce,
        ICommandExecutor executor,
        IGatewayClock clock,
        ILogger logger,
        GatewayRunnerOptions options,
        Random? random = null)
    {
        _http = http;
        _settings = settings;
        _keys = keys;
        _runOnce = runOnce;
        _executor = executor;
        _clock = clock;
        _logger = logger;
        _options = options;
        _random = random;
    }

    /// <summary>Runs until <paramref name="ct"/> is cancelled, or returns at once when there is nothing registered.</summary>
    public async Task<GatewayRunOutcome> RunAsync(CancellationToken ct)
    {
        var settings = _settings.Load();
        if (settings is null || settings.GatewayId == 0 || string.IsNullOrEmpty(settings.KeyName))
        {
            _logger.NotRegistered();
            return GatewayRunOutcome.NotRegistered;
        }
        _fingerprint = settings.KeyFingerprint;
        lock (_lock)
        {
            _interrupted.Clear();
            _interrupted.AddRange(_runOnce.Unfinished());
        }

        using var client = new WorkbenchClient(_http, settings, _keys, _clock);
        using var commandStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var backoff = new Backoff(_random);
        long? lastHeartbeat = null;
        Task? running = null;

        TimeSpan UntilHeartbeat() => lastHeartbeat is null
            ? TimeSpan.Zero
            : GatewayProtocol.HeartbeatInterval - _clock.Elapsed(lastHeartbeat.Value);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (UntilHeartbeat() <= TimeSpan.Zero)
                    {
                        var answer = await HeartbeatAsync(client, ct).ConfigureAwait(false);
                        lastHeartbeat = _clock.Timestamp();
                        Reached(backoff);
                        SetApproved(answer.Approved, settings);
                    }

                    if (running is not null)
                    {
                        if (!running.IsCompleted) await WaitForAsync(running, UntilHeartbeat(), ct).ConfigureAwait(false);
                        if (running.IsCompleted)
                        {
                            var finished = running;
                            running = null;
                            try
                            {
                                await finished.ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // The command stays "started, not finished": AL Workbench fails it
                                // once heartbeats stop listing it, and it is never run again.
                                _logger.LogError(ex, "A command ended with an unexpected error in the gateway.");
                            }
                        }
                        continue;
                    }

                    if (_approved != true)
                    {
                        // Waiting for an Admin: no polling, only the heartbeat, which
                        // says when approval has come.
                        await _clock.Delay(UntilHeartbeat(), ct).ConfigureAwait(false);
                        continue;
                    }

                    var polledAt = _clock.Timestamp();
                    var command = await client.NextCommandAsync(ct).ConfigureAwait(false);
                    var receivedAt = _clock.Timestamp();
                    Reached(backoff);
                    if (command is not null)
                    {
                        running = HandleCommandAsync(client, command, receivedAt, commandStop.Token);
                    }
                    else if (_clock.Elapsed(polledAt) < MinimumEmptyPoll)
                    {
                        // The workbench holds an empty poll for 30 seconds. One that comes
                        // back at once (a proxy that does not wait, say) must not turn the
                        // loop into a stream of requests.
                        await _clock.Delay(MinimumEmptyPoll - _clock.Elapsed(polledAt), ct).ConfigureAwait(false);
                    }
                }
                catch (GatewayNotApprovedException)
                {
                    SetApproved(false, settings);
                }
                catch (GatewayCredentialRefusedException ex)
                {
                    // One error line per attempt, and the attempts follow the back-off, so a
                    // gateway left refused for days writes a line every five minutes, not a flood.
                    var wait = backoff.Next();
                    _logger.CredentialRefused(ex.Message, (int)Math.Ceiling(wait.TotalSeconds));
                    await _clock.Delay(wait, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WorkbenchUnreachableException or WorkbenchRefusedException)
                {
                    var wait = backoff.Next();
                    _logger.Unreachable(ex.Message, (int)Math.Ceiling(wait.TotalSeconds));
                    await _clock.Delay(wait, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return GatewayRunOutcome.Stopped;
        }
        finally
        {
            await commandStop.CancelAsync().ConfigureAwait(false);
            if (running is not null)
            {
                // A command cut short stays "started, not finished" in the run-once
                // store, and the next start reports it as interrupted.
                try
                {
                    await running.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected while stopping.
                }
            }
        }
    }

    private async Task<GatewayHeartbeatResponse> HeartbeatAsync(WorkbenchClient client, CancellationToken ct)
    {
        List<long> running;
        List<long> interrupted;
        lock (_lock)
        {
            running = _running.Order().ToList();
            interrupted = _interrupted.ToList();
        }
        var answer = await client.HeartbeatAsync(new GatewayHeartbeatRequest
        {
            Version = _options.Version,
            MachineName = _options.MachineName,
            RunningCommandIds = running,
            InterruptedCommandIds = interrupted,
        }, ct).ConfigureAwait(false);

        // AL Workbench only applies the lists for an approved gateway, so a pending one
        // keeps reporting them until a heartbeat is answered as approved. Then AL
        // Workbench has them: it fails each one and never sends it again.
        if (!answer.Approved) return answer;
        foreach (var id in interrupted)
        {
            _runOnce.MarkFinished(id);
            _logger.CommandInterrupted(id);
        }
        lock (_lock) _interrupted.RemoveAll(interrupted.Contains);
        return answer;
    }

    private void SetApproved(bool approved, GatewaySettings settings)
    {
        if (_approved == approved) return;
        _approved = approved;
        if (approved) _logger.Approved(settings.WorkbenchUrl, settings.GatewayId);
        else _logger.WaitingForApproval(_fingerprint);
    }

    private void Reached(Backoff backoff)
    {
        if (backoff.Failures > 0) _logger.Reconnected(backoff.Failures);
        backoff.Reset();
    }

    /// <summary>Waits for the command to finish, or until the next heartbeat is due, whichever is first.</summary>
    private async Task WaitForAsync(Task running, TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = _clock.Delay(timeout, wait.Token);
        await Task.WhenAny(running, delay).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private static string Requester(GatewayCommandMessage command)
    {
        var who = string.IsNullOrWhiteSpace(command.RequestedBy) ? "AL Workbench (no person named)" : command.RequestedBy;
        return command.StartedByAgent ? who + " through an AI assistant" : who;
    }

    private async Task HandleCommandAsync(WorkbenchClient client, GatewayCommandMessage command, long receivedAt, CancellationToken ct)
    {
        // Leave the loop's turn before doing any work, so the loop sees a running task.
        await Task.Yield();
        var verb = GatewayVerbs.ToWire(command.Verb);
        var who = Requester(command);

        if (_runOnce.HasStarted(command.Id))
        {
            const string replayed = "This gateway already started this command once, and never runs a command twice.";
            _logger.CommandRefused(command.Id, verb, who, replayed);
            await ReportAsync(client, command.Id, GatewayCommandOutcome.Failure(replayed), ct).ConfigureAwait(false);
            return;
        }

        // Counted from when the answer arrived, by this computer's own monotonic
        // clock: the two clocks are never compared.
        var left = TimeSpan.FromSeconds(command.SecondsLeft) - _clock.Elapsed(receivedAt);
        if (command.SecondsLeft <= 0 || left <= TimeSpan.Zero)
        {
            const string expired = "The command's time to start ran out before the gateway could start it. Nothing was changed.";
            _runOnce.MarkStarted(command.Id);
            _logger.CommandRefused(command.Id, verb, who, expired);
            await ReportAsync(client, command.Id, GatewayCommandOutcome.Failure(expired), ct).ConfigureAwait(false);
            _runOnce.MarkFinished(command.Id);
            return;
        }

        // On disk before anything is done for the command.
        _runOnce.MarkStarted(command.Id);
        lock (_lock) _running.Add(command.Id);
        _logger.CommandStarted(command.Id, verb, who);
        var folder = Path.Combine(_options.WorkDirectory, command.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            using var batcher = new ProgressBatcher((lines, t) => client.PostProgressAsync(command.Id, lines, t), _clock, _logger);
            using var verbStop = CancellationTokenSource.CreateLinkedTokenSource(ct, batcher.Closed);
            using var pumpStop = new CancellationTokenSource();
            var pump = batcher.RunAsync(pumpStop.Token, ct);
            GatewayCommandOutcome outcome;
            try
            {
                var (files, refusal) = await DownloadFilesAsync(client, command, folder, verbStop.Token).ConfigureAwait(false);
                outcome = refusal ?? await ExecuteAsync(new GatewayCommandContext(command, files, batcher.Add), verbStop.Token, ct).ConfigureAwait(false);
            }
            finally
            {
                await pumpStop.CancelAsync().ConfigureAwait(false);
                await pump.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            await batcher.DrainAsync(ct).ConfigureAwait(false);

            if (batcher.Closed.IsCancellationRequested)
            {
                _logger.CommandFailed(command.Id, verb, who, "AL Workbench closed the command while it was running (it expired or was failed there).");
            }
            else
            {
                await ReportAsync(client, command.Id, outcome, ct).ConfigureAwait(false);
                if (outcome.Succeeded) _logger.CommandSucceeded(command.Id, verb, who);
                else _logger.CommandFailed(command.Id, verb, who, outcome.FailureMessage ?? "No reason given.");
            }
            _runOnce.MarkFinished(command.Id);
        }
        finally
        {
            lock (_lock) _running.Remove(command.Id);
            TryDelete(folder);
        }
    }

    private async Task<GatewayCommandOutcome> ExecuteAsync(GatewayCommandContext context, CancellationToken verbStop, CancellationToken ct)
    {
        try
        {
            return await _executor.ExecuteAsync(context, verbStop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (verbStop.IsCancellationRequested)
        {
            return GatewayCommandOutcome.Failure("Stopped because AL Workbench closed the command.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {CommandId} hit an unexpected error.", context.Command.Id);
            return GatewayCommandOutcome.Failure($"The gateway hit an unexpected error: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads each file and checks its SHA-256. Any mismatch fails the command
    /// before the verb sees a single file: a swapped or damaged file is never run.
    /// </summary>
    private async Task<(Dictionary<long, string> Files, GatewayCommandOutcome? Refusal)> DownloadFilesAsync(
        WorkbenchClient client, GatewayCommandMessage command, string folder, CancellationToken ct)
    {
        var files = new Dictionary<long, string>();
        if (command.Files.Count == 0) return (files, null);
        Directory.CreateDirectory(folder);
        foreach (var file in command.Files)
        {
            var path = Path.Combine(folder, $"{file.Id}-{SafeFileName(file.Name)}");
            var downloaded = await DownloadOneAsync(client, command.Id, file, path, ct).ConfigureAwait(false);
            if (downloaded is not null) return (files, downloaded);

            string actual;
            await using (var stream = File.OpenRead(path))
            {
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            }
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
                return (files, GatewayCommandOutcome.Failure(
                    $"The file '{file.Name}' that arrived is not the one AL Workbench sent: its SHA-256 does not match. Nothing was run."));
            }
            files[file.Id] = path;
        }
        return (files, null);
    }

    /// <summary>Null when the file is on disk at its declared size; otherwise the outcome to fail the command with.</summary>
    private async Task<GatewayCommandOutcome?> DownloadOneAsync(WorkbenchClient client, long commandId, GatewayCommandFileMessage file, string path, CancellationToken ct)
    {
        var backoff = new Backoff(_random);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                long size;
                await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    size = await client.DownloadFileAsync(commandId, file.Id, target, file.Size, ct).ConfigureAwait(false);
                }
                if (size != file.Size)
                {
                    File.Delete(path);
                    return GatewayCommandOutcome.Failure(
                        $"The file '{file.Name}' that arrived is not the one AL Workbench sent: it is {(size < 0 ? "larger" : "smaller")} than expected. Nothing was run.");
                }
                return null;
            }
            catch (Exception ex) when ((ex is WorkbenchUnreachableException or GatewayCredentialRefusedException) && attempt < DownloadAttempts)
            {
                _logger.LogWarning("Downloading '{File}' for command {CommandId} failed ({Reason}); trying again.", file.Name, commandId, ex.Message);
                await _clock.Delay(backoff.Next(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WorkbenchUnreachableException or WorkbenchRefusedException or GatewayNotApprovedException
                                       or GatewayCredentialRefusedException)
            {
                return GatewayCommandOutcome.Failure($"Could not download '{file.Name}': {ex.Message} Nothing was run.");
            }
        }
    }

    /// <summary>
    /// Posts the outcome, retrying while AL Workbench cannot be reached: the result is
    /// the one report that must arrive. A 409 means AL Workbench already closed the
    /// command, which is logged and accepted.
    /// </summary>
    private async Task ReportAsync(WorkbenchClient client, long commandId, GatewayCommandOutcome outcome, CancellationToken ct)
    {
        var request = new GatewayCommandResultRequest
        {
            Succeeded = outcome.Succeeded,
            Result = outcome.Result,
            FailureMessage = outcome.Succeeded ? null : outcome.FailureMessage ?? "The command failed.",
        };
        var backoff = new Backoff(_random);
        while (true)
        {
            try
            {
                await client.PostResultAsync(commandId, request, ct).ConfigureAwait(false);
                return;
            }
            catch (CommandClosedException ex)
            {
                _logger.LogWarning("AL Workbench had already closed command {CommandId}: {Reason}", commandId, ex.Message);
                return;
            }
            catch (WorkbenchRefusedException ex)
            {
                _logger.LogError("AL Workbench refused the result of command {CommandId}: {Reason}", commandId, ex.Message);
                return;
            }
            catch (Exception ex) when (ex is WorkbenchUnreachableException or GatewayCredentialRefusedException
                                       or GatewayNotApprovedException)
            {
                // Not approved: the key was replaced and an Admin has not approved the new
                // one yet. The result waits for that rather than being lost.
                var wait = backoff.Next();
                _logger.LogWarning("Could not report the result of command {CommandId} ({Reason}); trying again in {Seconds} seconds.",
                    commandId, ex.Message, (int)Math.Ceiling(wait.TotalSeconds));
                await _clock.Delay(wait, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The file's own name with anything Windows would not accept replaced, and never a path.</summary>
    internal static string SafeFileName(string name)
    {
        var leaf = Path.GetFileName(name.Replace('\\', '/'));
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '|', '?', '*', '/', '\\']).ToHashSet();
        var safe = new string(leaf.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim('.', ' ');
        if (safe.Length > 100) safe = safe[^100..];
        return safe.Length == 0 ? "file" : safe;
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not remove the working folder {Folder}: {Reason}", folder, ex.Message);
        }
    }
}
