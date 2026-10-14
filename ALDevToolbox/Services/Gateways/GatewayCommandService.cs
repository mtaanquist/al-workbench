using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>A file to send with a command: its name as the gateway will see it, and its content.</summary>
public sealed record GatewayCommandFileInput(string FileName, byte[] Content);

/// <summary>
/// What a caller asks a gateway to do. <paramref name="DueAt"/> defaults to now; the
/// claim deadline is 15 minutes after it, or <paramref name="WindowEndsAt"/> when
/// that is earlier (the end of the delivery window the command was scheduled in).
/// </summary>
public sealed record GatewayCommandRequest(
    int GatewayId,
    GatewayVerb Verb,
    JsonObject? Parameters = null,
    DateTime? DueAt = null,
    DateTime? WindowEndsAt = null,
    int? RequestedByUserId = null,
    bool StartedByAgent = false,
    IReadOnlyList<GatewayCommandFileInput>? Files = null);

/// <summary>How a request for the next command ended.</summary>
public enum GatewayClaimOutcome
{
    /// <summary>A command was claimed for the gateway; it is in <see cref="GatewayClaimResult.Command"/>.</summary>
    Claimed,
    /// <summary>Nothing is waiting for it.</summary>
    Nothing,
    /// <summary>The gateway is waiting for an Admin's approval and gets no commands.</summary>
    NotApproved,
    /// <summary>The gateway is gone or revoked.</summary>
    Revoked,
}

public sealed record GatewayClaimResult(GatewayClaimOutcome Outcome, GatewayCommandMessage? Command = null);

/// <summary>How a file request ended.</summary>
public enum GatewayFileOutcome
{
    Found,
    /// <summary>No such command for this gateway, or no such file on that command. Deliberately one answer for both.</summary>
    NotFound,
    /// <summary>The command is this gateway's, but it is not claimed or running any more.</summary>
    NotRunning,
}

public sealed record GatewayCommandFileDownload(string FileName, string Sha256, byte[] Content);

/// <summary>What one sweep of one organisation did.</summary>
public sealed record GatewayCommandSweepResult(int Expired, int FailedSilent, int TokensRemoved);

/// <summary>
/// The on-premises gateway command queue: queueing a command, handing it to its
/// gateway exactly once, recording progress and the result, and the sweep that
/// expires what was never collected and fails what went quiet. See
/// <c>.design/on-prem-gateway.md</c>, "Commands".
///
/// <para>Commands are rows and every state change is a compare-and-set
/// (<c>ExecuteUpdateAsync</c> with the expected state in its <c>WHERE</c>), so two
/// long-polls, or a long-poll and the sweep, can never both win a row. Everything
/// runs under the query filter: the gateway's requests carry its organisation claim
/// and the sweep visits one organisation at a time, so nothing here crosses the
/// tenant fence. The gateway-facing methods also check that the command belongs to
/// the calling gateway, beyond the organisation filter.</para>
///
/// <para>Nothing here is audited row by row: the command row is its own history,
/// and its state changes are written with <c>ExecuteUpdateAsync</c>.</para>
/// </summary>
public sealed class GatewayCommandService
{
    /// <summary>How long after its due time a command may still be collected.</summary>
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(15);

    /// <summary>A claimed or running command nobody has reported on for this long is failed.</summary>
    public static readonly TimeSpan SilenceLimit = TimeSpan.FromMinutes(10);

    /// <summary>Most progress lines in one post.</summary>
    public const int MaxProgressLines = 200;

    /// <summary>Longest progress line kept; longer ones are cut.</summary>
    public const int MaxLineLength = 4000;

    /// <summary>Once a command's log reaches this many characters, further lines are dropped.</summary>
    public const int MaxLogLength = 1_000_000;

    /// <summary>Longest failure message a gateway may report.</summary>
    public const int MaxFailureMessageLength = 4000;

    /// <summary>Most command ids in one heartbeat list.</summary>
    public const int MaxHeartbeatIds = 100;

    /// <summary>Longest file name a command may carry.</summary>
    public const int MaxFileNameLength = 255;

    public const string OfflineMessage = "The gateway was offline.";

    public const string InterruptedMessage = "The gateway restarted while this was running; check the environment.";

    public const string GatewaySilentMessage = "The gateway stopped responding while this was running; check the environment.";

    public const string CommandSilentMessage = "The gateway stopped reporting on this while it was running; check the environment.";

    // How often the claim retries after losing a race for a row before saying
    // "nothing": the long-poll asks again after its next wake anyway.
    private const int ClaimAttempts = 5;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly GatewayCommandSignal _signal;
    private readonly TimeProvider _clock;
    private readonly ILogger<GatewayCommandService> _logger;

    public GatewayCommandService(
        AppDbContext db,
        IOrganizationContext orgContext,
        GatewayCommandSignal signal,
        TimeProvider clock,
        ILogger<GatewayCommandService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _signal = signal;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; gateway command call made outside an authenticated request or organisation scope.");

    // ---------------------------------------------------------------- the workbench's side

    /// <summary>
    /// Queues a command for a gateway and wakes the gateway if it is waiting. For the
    /// features built on the queue (inventory, deployments, licences, tier actions):
    /// they decide who may ask, this decides whether the gateway can take it. Saves
    /// on its own, so call it outside any transaction of the caller's - the wake-up
    /// fires after this save, and a waiting gateway must be able to see the row.
    /// </summary>
    /// <returns>The new command's id.</returns>
    /// <exception cref="PlanValidationException">
    /// Keyed <c>gateway</c> when the gateway is gone, revoked or not approved;
    /// <c>dueAt</c> when the claim deadline has already passed; <c>files</c> for a bad file.
    /// </exception>
    public async Task<long> EnqueueAsync(GatewayCommandRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var orgId = RequireOrganizationId();
        var now = Now;
        var errors = new Dictionary<string, string>();

        if (!Enum.IsDefined(request.Verb))
        {
            errors["verb"] = "That is not something a gateway can do.";
        }

        var gateway = await _db.Gateways.AsNoTracking()
            .Where(g => g.Id == request.GatewayId)
            .Select(g => new { g.Name, g.State, g.RevokedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (gateway is null)
        {
            errors["gateway"] = "That gateway no longer exists.";
        }
        else if (gateway.RevokedAt is not null)
        {
            errors["gateway"] = $"The gateway {gateway.Name} has been revoked.";
        }
        else if (gateway.State != GatewayState.Approved)
        {
            errors["gateway"] = $"The gateway {gateway.Name} is waiting for an Admin to approve it under Administration → Gateways.";
        }

        var due = request.DueAt ?? now;
        var expires = ClaimDeadline(due, request.WindowEndsAt);
        if (expires <= now)
        {
            errors["dueAt"] = "This would expire before the gateway could collect it: the delivery window has already ended.";
        }

        var files = request.Files ?? [];
        foreach (var file in files)
        {
            var name = file.FileName?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > MaxFileNameLength || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            {
                errors["files"] = $"The file name '{name}' cannot be sent to a gateway.";
                break;
            }
            if (file.Content is null || file.Content.Length == 0)
            {
                errors["files"] = $"The file {name} is empty.";
                break;
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var command = new GatewayCommand
        {
            OrganizationId = orgId,
            GatewayId = request.GatewayId,
            Verb = request.Verb,
            ParametersJson = (request.Parameters ?? new JsonObject()).ToJsonString(),
            Status = GatewayCommandStatus.Queued,
            RequestedByUserId = request.RequestedByUserId,
            StartedByAgent = request.StartedByAgent,
            CreatedAt = now,
            DueAt = due,
            ExpiresAt = expires,
        };
        foreach (var file in files)
        {
            command.Files.Add(new GatewayCommandFile
            {
                OrganizationId = orgId,
                FileName = file.FileName.Trim(),
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(file.Content)),
                Size = file.Content.Length,
                Content = file.Content,
            });
        }
        _db.GatewayCommands.Add(command);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _signal.Notify(request.GatewayId);
        _logger.LogInformation(
            "Queued gateway command {CommandId} ({Verb}) for gateway {GatewayId} with {FileCount} file(s), due {DueAt}, expires {ExpiresAt}.",
            command.Id, GatewayVerbs.ToWire(request.Verb), request.GatewayId, files.Count, due, expires);
        return command.Id;
    }

    /// <summary>
    /// The claim deadline for a command due at <paramref name="due"/>: <see cref="ClaimGrace"/>
    /// after it, or the end of its delivery window when that is earlier.
    /// </summary>
    public static DateTime ClaimDeadline(DateTime due, DateTime? windowEndsAt)
    {
        var deadline = due + ClaimGrace;
        return windowEndsAt is { } end && end < deadline ? end : deadline;
    }

    // ---------------------------------------------------------------- the gateway's side

    /// <summary>
    /// Claims the gateway's next due command, if there is one. Atomic: the claim is
    /// an update that only succeeds while the row is still queued, so two polls that
    /// read the same row cannot both have it - the loser moves on to the next.
    /// </summary>
    public async Task<GatewayClaimResult> ClaimNextAsync(int gatewayId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var gateway = await _db.Gateways.AsNoTracking()
            .Where(g => g.Id == gatewayId)
            .Select(g => new { g.State, g.RevokedAt })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (gateway is null || gateway.RevokedAt is not null) return new(GatewayClaimOutcome.Revoked);
        if (gateway.State != GatewayState.Approved) return new(GatewayClaimOutcome.NotApproved);

        for (var attempt = 0; attempt < ClaimAttempts; attempt++)
        {
            var now = Now;
            var candidate = await _db.GatewayCommands.AsNoTracking()
                .Where(c => c.GatewayId == gatewayId && c.Status == GatewayCommandStatus.Queued
                            && c.DueAt <= now && c.ExpiresAt > now)
                .OrderBy(c => c.DueAt).ThenBy(c => c.Id)
                .Select(c => (long?)c.Id)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (candidate is not { } id) return new(GatewayClaimOutcome.Nothing);

            var won = await _db.GatewayCommands
                .Where(c => c.Id == id && c.GatewayId == gatewayId && c.Status == GatewayCommandStatus.Queued && c.ExpiresAt > now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, GatewayCommandStatus.Claimed)
                    .SetProperty(c => c.ClaimedAt, now)
                    .SetProperty(c => c.LastReportedAt, now), ct).ConfigureAwait(false);
            if (won == 0) continue;

            var message = await LoadMessageAsync(id, now, ct).ConfigureAwait(false);
            _logger.LogInformation("Gateway {GatewayId} claimed command {CommandId} ({Verb}).",
                gatewayId, id, GatewayVerbs.ToWire(message.Verb));
            return new(GatewayClaimOutcome.Claimed, message);
        }
        return new(GatewayClaimOutcome.Nothing);
    }

    /// <summary>
    /// The long-poll: claims the next command now if there is one, otherwise waits for
    /// a wake-up from <see cref="EnqueueAsync"/> and tries again, until
    /// <see cref="GatewayCommandSignal.LongPollTimeout"/> runs out.
    /// </summary>
    public async Task<GatewayClaimResult> WaitForNextAsync(int gatewayId, CancellationToken ct = default)
    {
        using var timeout = new CancellationTokenSource(_signal.LongPollTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        while (true)
        {
            // Taken before the read, so a command queued between the read and the
            // wait still wakes this poll.
            var wake = _signal.WaitHandle(gatewayId);
            var result = await ClaimNextAsync(gatewayId, ct).ConfigureAwait(false);
            if (result.Outcome != GatewayClaimOutcome.Nothing) return result;
            try
            {
                await wake.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return result;
            }
        }
    }

    private async Task<GatewayCommandMessage> LoadMessageAsync(long id, DateTime now, CancellationToken ct)
    {
        var row = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new
            {
                c.Id,
                c.Verb,
                c.ParametersJson,
                c.ExpiresAt,
                Files = c.Files.OrderBy(f => f.Id)
                    .Select(f => new GatewayCommandFileMessage { Id = f.Id, Name = f.FileName, Sha256 = f.Sha256, Size = f.Size })
                    .ToList(),
            })
            .FirstAsync(ct).ConfigureAwait(false);
        return new GatewayCommandMessage
        {
            Id = row.Id,
            Verb = row.Verb,
            Parameters = JsonNode.Parse(row.ParametersJson) as JsonObject ?? new JsonObject(),
            SecondsLeft = (int)Math.Max(0, Math.Floor((row.ExpiresAt - now).TotalSeconds)),
            Files = row.Files,
        };
    }

    /// <summary>
    /// Adds progress lines to one of the gateway's commands, each stamped with the
    /// workbench's clock. The first marks a claimed command running. False when the
    /// command is not this gateway's, or is no longer claimed or running (the sweep
    /// failed it, say): the gateway should stop reporting on it.
    /// </summary>
    public async Task<bool> AppendProgressAsync(int gatewayId, long commandId, IReadOnlyList<string> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        RequireOrganizationId();
        if (lines.Count > MaxProgressLines)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["lines"] = $"Send at most {MaxProgressLines} progress lines at a time.",
            });
        }

        var now = Now;
        var stamp = now.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            // One line in, one line out: a newline inside a line would let it pose as two.
            var clean = (line ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            if (clean.Length > MaxLineLength) clean = clean[..MaxLineLength] + "...";
            text.Append(stamp).Append(' ').Append(clean).Append('\n');
        }
        var append = text.ToString();

        var updated = await _db.GatewayCommands
            .Where(c => c.Id == commandId && c.GatewayId == gatewayId
                        && (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Running)
                .SetProperty(c => c.StartedAt, c => c.StartedAt ?? now)
                .SetProperty(c => c.LastReportedAt, now)
                .SetProperty(c => c.Log, c => c.Log.Length >= MaxLogLength ? c.Log : c.Log + append), ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// Records how one of the gateway's commands ended. False when the command is not
    /// this gateway's or is no longer claimed or running, so a late or repeated result
    /// never overwrites the first outcome.
    /// </summary>
    public async Task<bool> CompleteAsync(int gatewayId, long commandId, GatewayCommandResultRequest result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        RequireOrganizationId();
        var failure = result.FailureMessage?.Trim();
        if (!result.Succeeded && string.IsNullOrEmpty(failure))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["failureMessage"] = "Say why the command failed.",
            });
        }
        if (failure is { Length: > MaxFailureMessageLength })
        {
            failure = failure[..MaxFailureMessageLength] + "...";
        }

        var now = Now;
        var status = result.Succeeded ? GatewayCommandStatus.Succeeded : GatewayCommandStatus.Failed;
        var resultJson = result.Result?.ToJsonString();
        var message = result.Succeeded ? null : failure;
        var updated = await _db.GatewayCommands
            .Where(c => c.Id == commandId && c.GatewayId == gatewayId
                        && (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, status)
                .SetProperty(c => c.StartedAt, c => c.StartedAt ?? now)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.LastReportedAt, now)
                .SetProperty(c => c.ResultJson, resultJson)
                .SetProperty(c => c.FailureMessage, message), ct)
            .ConfigureAwait(false);
        if (updated == 1)
        {
            _logger.LogInformation("Gateway {GatewayId} finished command {CommandId}: {Status}.", gatewayId, commandId, status);
        }
        return updated == 1;
    }

    /// <summary>
    /// Applies what a heartbeat says about commands: the ones listed as running are
    /// kept alive, and the ones listed as interrupted are failed with
    /// <see cref="InterruptedMessage"/> and never sent again. Only the calling
    /// gateway's claimed or running commands are touched.
    /// </summary>
    /// <returns>How many commands were failed as interrupted.</returns>
    public async Task<int> ApplyHeartbeatAsync(
        int gatewayId, IReadOnlyList<long>? running, IReadOnlyList<long>? interrupted, CancellationToken ct = default)
    {
        RequireOrganizationId();
        running ??= [];
        interrupted ??= [];
        var errors = new Dictionary<string, string>();
        if (running.Count > MaxHeartbeatIds) errors["runningCommandIds"] = $"List at most {MaxHeartbeatIds} running commands.";
        if (interrupted.Count > MaxHeartbeatIds) errors["interruptedCommandIds"] = $"List at most {MaxHeartbeatIds} interrupted commands.";
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var now = Now;
        var failed = 0;
        if (interrupted.Count > 0)
        {
            var ids = interrupted.Distinct().ToList();
            failed = await _db.GatewayCommands
                .Where(c => ids.Contains(c.Id) && c.GatewayId == gatewayId
                            && (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                    .SetProperty(c => c.FinishedAt, now)
                    .SetProperty(c => c.FailureMessage, InterruptedMessage), ct)
                .ConfigureAwait(false);
            if (failed > 0)
            {
                _logger.LogWarning("Gateway {GatewayId} restarted in the middle of {Count} command(s); marked them failed.", gatewayId, failed);
            }
        }
        if (running.Count > 0)
        {
            var ids = running.Distinct().ToList();
            await _db.GatewayCommands
                .Where(c => ids.Contains(c.Id) && c.GatewayId == gatewayId
                            && (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastReportedAt, now), ct)
                .ConfigureAwait(false);
        }
        return failed;
    }

    /// <summary>
    /// A file of one of the gateway's commands, checked explicitly beyond the
    /// organisation filter: the command must be this gateway's and still claimed or
    /// running, and the file must be that command's.
    /// </summary>
    public async Task<(GatewayFileOutcome Outcome, GatewayCommandFileDownload? File)> OpenFileAsync(
        int gatewayId, long commandId, long fileId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var command = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.Id == commandId && c.GatewayId == gatewayId)
            .Select(c => new { c.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (command is null) return (GatewayFileOutcome.NotFound, null);
        if (command.Status is not (GatewayCommandStatus.Claimed or GatewayCommandStatus.Running))
        {
            return (GatewayFileOutcome.NotRunning, null);
        }

        var file = await _db.GatewayCommandFiles.AsNoTracking()
            .Where(f => f.Id == fileId && f.CommandId == commandId)
            .Select(f => new GatewayCommandFileDownload(f.FileName, f.Sha256, f.Content))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return file is null ? (GatewayFileOutcome.NotFound, null) : (GatewayFileOutcome.Found, file);
    }

    // ---------------------------------------------------------------- the sweep

    /// <summary>
    /// One pass over the current organisation: expires queued commands past their
    /// claim deadline, fails claimed or running ones nobody has reported on for
    /// <see cref="SilenceLimit"/>, and clears spent replay-protection rows. Called by
    /// <see cref="GatewayCommandSweep"/> inside the organisation's ambient scope.
    /// </summary>
    public async Task<GatewayCommandSweepResult> SweepAsync(CancellationToken ct = default)
    {
        RequireOrganizationId();
        var now = Now;

        var expired = await _db.GatewayCommands
            .Where(c => c.Status == GatewayCommandStatus.Queued && c.ExpiresAt <= now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Expired)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, OfflineMessage), ct)
            .ConfigureAwait(false);

        var quietSince = now - SilenceLimit;
        // The gateway itself went quiet: the likelier reason, and the clearer message.
        var gatewaySilent = await _db.GatewayCommands
            .Where(c => (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running)
                        && (c.LastReportedAt == null || c.LastReportedAt < quietSince)
                        && (c.Gateway!.LastSeenAt == null || c.Gateway.LastSeenAt < quietSince))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, GatewaySilentMessage), ct)
            .ConfigureAwait(false);
        // The gateway is alive but has stopped listing this one: it never received it
        // (the answer to the claim was lost), or lost track of it.
        var commandSilent = await _db.GatewayCommands
            .Where(c => (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running)
                        && (c.LastReportedAt == null || c.LastReportedAt < quietSince))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, CommandSilentMessage), ct)
            .ConfigureAwait(false);

        var tokens = await _db.GatewaySeenTokens
            .Where(t => t.ExpiresAt < now)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        if (expired + gatewaySilent + commandSilent > 0)
        {
            _logger.LogWarning(
                "Gateway command sweep for organisation {OrganizationId}: {Expired} expired uncollected, {Silent} failed after the gateway went quiet.",
                _orgContext.CurrentOrganizationId, expired, gatewaySilent + commandSilent);
        }
        return new GatewayCommandSweepResult(expired, gatewaySilent + commandSilent, tokens);
    }
}
