using System.Data;
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

/// <summary>
/// A file to send with a command: its name as the gateway will see it, and either its
/// content or the build artifact it is (<see cref="FromArtifact"/>).
/// </summary>
public sealed record GatewayCommandFileInput(string FileName, byte[]? Content, int? ProjectBuildArtifactId = null)
{
    /// <summary>
    /// A build artifact sent by reference: the command keeps the artifact's id rather than
    /// a copy of its bytes, and the download reads the artifact (#1367). The hash the
    /// gateway checks is worked out from the artifact when the command is queued.
    /// </summary>
    public static GatewayCommandFileInput FromArtifact(string fileName, int projectBuildArtifactId) =>
        new(fileName, null, projectBuildArtifactId);
}

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

/// <summary>A file the gateway may download; its content is streamed by <see cref="GatewayCommandService.CopyFileContentAsync"/>.</summary>
public sealed record GatewayCommandFileDownload(long Id, string FileName, string Sha256, long Size);

/// <summary>
/// Where one command stands, as <see cref="GatewayCommandService.ReadProgressAsync"/> reads it.
/// </summary>
/// <param name="NewLog">The log written since the offset asked for, each line stamped with the workbench's clock.</param>
/// <param name="LogOffset">The offset to ask from next time.</param>
/// <param name="LeftQueue">Whether the gateway ever collected it: once it has, something may have run, whatever the command says after.</param>
public sealed record GatewayCommandProgress(
    GatewayCommandStatus Status, string? FailureMessage, JsonObject? Result, string NewLog, int LogOffset, bool LeftQueue = false)
{
    /// <summary>The command will not change again.</summary>
    public bool IsFinal => Status is GatewayCommandStatus.Succeeded or GatewayCommandStatus.Failed or GatewayCommandStatus.Expired;
}

/// <summary>What one sweep of one organisation did.</summary>
public sealed record GatewayCommandSweepResult(int Expired, int FailedRekeyed, int FailedSilent, int TokensRemoved);

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

    public const string RevokedMessage = "The gateway was revoked.";

    public const string RekeyedMessage =
        "The gateway was given a new key and is waiting for an Admin to approve it; check the environment before trying again.";

    /// <summary>
    /// The largest file a command may carry. Business Central online refuses an
    /// extension over 50 MB (<c>BcAppManagementClient.MaxAppBytes</c>); on-premises has
    /// no such ceiling, so this allows well over twice that for apps with embedded
    /// resources, while staying a size the app can hold once in memory when it queues
    /// the command (the download itself is streamed). A licence is a few kilobytes.
    /// </summary>
    public const int MaxFileBytes = 128 * 1024 * 1024;

    /// <summary>Appended once when a command's log reaches <see cref="MaxLogLength"/>; later lines are dropped.</summary>
    public const string LogTruncatedMarker = "\n[Log truncated: later lines were not kept.]\n";

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
        // What each file is as it will be sent: its hash and size, worked out here for a
        // referenced artifact as for a carried file, so the gateway always checks against
        // a hash the workbench computed from exactly the bytes it will serve.
        var sent = new List<(string Name, string Sha256, long Size, byte[]? Content, int? ArtifactId)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var name = file.FileName?.Trim() ?? string.Empty;
            if (name.Length == 0 || name.Length > MaxFileNameLength || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or "..")
            {
                errors["files"] = $"The file name '{name}' cannot be sent to a gateway.";
                break;
            }
            if (!names.Add(name))
            {
                errors["files"] = $"Two files of one command are called {name}.";
                break;
            }
            string sha;
            long size;
            if (file.ProjectBuildArtifactId is { } artifactId)
            {
                if (await ArtifactHashAsync(orgId, artifactId, ct).ConfigureAwait(false) is not { } artifact)
                {
                    errors["files"] = $"The build file {name} no longer exists.";
                    break;
                }
                (sha, size) = artifact;
            }
            else
            {
                if (file.Content is null || file.Content.Length == 0)
                {
                    errors["files"] = $"The file {name} is empty.";
                    break;
                }
                sha = Convert.ToHexStringLower(SHA256.HashData(file.Content));
                size = file.Content.Length;
            }
            if (size == 0)
            {
                errors["files"] = $"The file {name} is empty.";
                break;
            }
            if (size > MaxFileBytes)
            {
                errors["files"] = $"The file {name} is larger than {MaxFileBytes / (1024 * 1024)} MB, the most a gateway is sent.";
                break;
            }
            sent.Add((name, sha, size, file.ProjectBuildArtifactId is null ? file.Content : null, file.ProjectBuildArtifactId));
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
        foreach (var file in sent)
        {
            command.Files.Add(new GatewayCommandFile
            {
                OrganizationId = orgId,
                FileName = file.Name,
                Sha256 = file.Sha256,
                Size = file.Size,
                Content = file.Content,
                ProjectBuildArtifactId = file.ArtifactId,
            });
        }
        _db.GatewayCommands.Add(command);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Inside the caller's transaction (a deployment recording the command it queued,
        // #1367) the row is not visible to the gateway until the caller commits, so waking
        // it now would only send it back to sleep: the caller wakes it after the commit.
        if (_db.Database.CurrentTransaction is null) _signal.Notify(request.GatewayId);
        _logger.LogInformation(
            "Queued gateway command {CommandId} ({Verb}) for gateway {GatewayId} with {FileCount} file(s), due {DueAt}, expires {ExpiresAt}.",
            command.Id, GatewayVerbs.ToWire(request.Verb), request.GatewayId, files.Count, due, expires);
        return command.Id;
    }

    /// <summary>
    /// Wakes <paramref name="gatewayId"/>'s long poll, for a caller that queued a command
    /// inside its own transaction and has now committed it.
    /// </summary>
    public void Wake(int gatewayId) => _signal.Notify(gatewayId);

    /// <summary>
    /// Where a command stands, for the feature that queued it and is waiting on it (a
    /// deployment, #1367): its status, how it ended, and the log written since
    /// <paramref name="logOffset"/> characters, so a caller that polls only ever reads
    /// what is new. Null when the command is not there (another organisation's reads as
    /// absent, under the query filter).
    /// </summary>
    public async Task<GatewayCommandProgress?> ReadProgressAsync(long commandId, int logOffset, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var offset = Math.Max(0, logOffset);
        var row = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.Id == commandId)
            .Select(c => new
            {
                c.Status,
                c.FailureMessage,
                c.ResultJson,
                c.ClaimedAt,
                Length = c.Log.Length,
                Tail = c.Log.Length > offset ? c.Log.Substring(offset) : string.Empty,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null) return null;
        var result = string.IsNullOrWhiteSpace(row.ResultJson) ? null : JsonNode.Parse(row.ResultJson) as JsonObject;
        return new GatewayCommandProgress(row.Status, row.FailureMessage, result, row.Tail, Math.Max(offset, row.Length),
            LeftQueue: row.ClaimedAt is not null || row.Status is GatewayCommandStatus.Claimed or GatewayCommandStatus.Running);
    }

    /// <summary>
    /// Takes back a command nobody has collected yet: a queued one becomes failed with
    /// <paramref name="message"/>, compare-and-set on its status, so a gateway claiming it
    /// at the same moment wins. True when it was taken back. One already claimed cannot be:
    /// the gateway has it, and only the gateway can say what became of it.
    /// </summary>
    public async Task<bool> CancelQueuedAsync(long commandId, string message, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var now = Now;
        var cancelled = await _db.GatewayCommands
            .Where(c => c.Id == commandId && c.Status == GatewayCommandStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, message), ct)
            .ConfigureAwait(false);
        if (cancelled > 0)
        {
            _logger.LogInformation("Took back gateway command {CommandId} before the gateway collected it.", commandId);
        }
        return cancelled > 0;
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

        // Retried for as long as a candidate remains: losing the race for a row means
        // another poll took it, so the queue only shrinks and this always ends.
        while (true)
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

            // The row is ours now: read it whatever happens to the request, so a
            // gateway that hung up a moment ago does not leave a half-built answer.
            // If the answer still never arrives, the gateway never lists the command
            // and the sweep fails it after SilenceLimit; it is never handed out twice.
            var message = await LoadMessageAsync(id, now, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Gateway {GatewayId} claimed command {CommandId} ({Verb}).",
                gatewayId, id, GatewayVerbs.ToWire(message.Verb));
            return new(GatewayClaimOutcome.Claimed, message);
        }
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
                RequestedBy = c.RequestedByUser != null ? c.RequestedByUser.DisplayName : null,
                c.StartedByAgent,
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
            RequestedBy = string.IsNullOrWhiteSpace(row.RequestedBy) ? null : row.RequestedBy,
            StartedByAgent = row.StartedByAgent,
            Files = row.Files,
        };
    }

    /// <summary>
    /// Adds progress lines to one of the gateway's commands, each stamped with the
    /// workbench's clock. The first marks a claimed command running. False when the
    /// command is not this gateway's, is no longer claimed or running (the sweep
    /// failed it, say), or the gateway is not approved (re-keyed and waiting, say):
    /// the gateway should stop reporting on it.
    /// </summary>
    public async Task<bool> AppendProgressAsync(int gatewayId, long commandId, IReadOnlyList<string> lines, CancellationToken ct = default) =>
        await AppendProgressAsync(gatewayId, commandId, lines, sequence: 0, ct).ConfigureAwait(false);

    /// <summary>
    /// As <see cref="AppendProgressAsync(int, long, IReadOnlyList{string}, CancellationToken)"/>,
    /// for a numbered batch: a <paramref name="sequence"/> at or below the last one
    /// appended is a batch sent again after its answer was lost, so it is accepted
    /// (true) without appending its lines a second time. 0 is always appended.
    /// </summary>
    public async Task<bool> AppendProgressAsync(int gatewayId, long commandId, IReadOnlyList<string> lines, long sequence, CancellationToken ct = default)
    {
        if (sequence < 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["sequence"] = "A progress batch number cannot be negative.",
            });
        }
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

        // Bounded in the database itself, so two posts racing cannot overshoot: an
        // untruncated log is always shorter than MaxLogLength, the post that would
        // reach it is cut to fit and closed with the marker exactly once, and a log at
        // or past the cap is never touched again. Each post still rewrites the row
        // (Postgres has no in-place append), which the cap keeps to at most ~1 MB.
        var updated = await _db.GatewayCommands
            .Where(c => c.Id == commandId && c.GatewayId == gatewayId && c.Gateway!.State == GatewayState.Approved
                        && (c.Status == GatewayCommandStatus.Claimed || c.Status == GatewayCommandStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Running)
                .SetProperty(c => c.StartedAt, c => c.StartedAt ?? now)
                .SetProperty(c => c.LastReportedAt, now)
                .SetProperty(c => c.LastProgressSequence, c => sequence > c.LastProgressSequence ? sequence : c.LastProgressSequence)
                .SetProperty(c => c.Log, c =>
                    (sequence > 0 && sequence <= c.LastProgressSequence) || c.Log.Length >= MaxLogLength ? c.Log
                    : c.Log.Length + append.Length < MaxLogLength ? c.Log + append
                    : (c.Log + append).Substring(0, MaxLogLength) + LogTruncatedMarker), ct)
            .ConfigureAwait(false);
        return updated == 1;
    }

    /// <summary>
    /// Records how one of the gateway's commands ended. False when the command is not
    /// this gateway's, is no longer claimed or running, or the gateway is not
    /// approved, so a late or repeated result never overwrites the first outcome.
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
            .Where(c => c.Id == commandId && c.GatewayId == gatewayId && c.Gateway!.State == GatewayState.Approved
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
    /// gateway's claimed or running commands are touched, and only while it is
    /// approved: a gateway re-keyed by a rotation code proves nothing yet about the
    /// commands its predecessor was working on.
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
                .Where(c => ids.Contains(c.Id) && c.GatewayId == gatewayId && c.Gateway!.State == GatewayState.Approved
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
                .Where(c => ids.Contains(c.Id) && c.GatewayId == gatewayId && c.Gateway!.State == GatewayState.Approved
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
            .Select(f => new GatewayCommandFileDownload(f.Id, f.FileName, f.Sha256, f.Size))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return file is null ? (GatewayFileOutcome.NotFound, null) : (GatewayFileOutcome.Found, file);
    }

    /// <summary>
    /// Streams one file's content to <paramref name="destination"/> without holding it
    /// in memory: EF can only materialise a <c>bytea</c> whole, so this reads it with a
    /// sequential-access reader instead. Call it only for a file
    /// <see cref="OpenFileAsync"/> has just found. The SQL bypasses the query filter,
    /// so it names the organisation itself.
    /// </summary>
    /// <returns>False when the content is gone (the command became final in between).</returns>
    public async Task<bool> CopyFileContentAsync(long fileId, Stream destination, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var orgId = RequireOrganizationId();
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            // A file sent by reference (#1367) is read from its build artifact, in the same
            // organisation; one carried with the command, from its own content.
            command.CommandText =
                "SELECT COALESCE(f.content, a.content) FROM gateway_command_files f "
                + "LEFT JOIN oe_project_build_artifacts a ON a.id = f.project_build_artifact_id AND a.organization_id = f.organization_id "
                + "WHERE f.id = @id AND f.organization_id = @org";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = fileId;
            command.Parameters.Add(id);
            var org = command.CreateParameter();
            org.ParameterName = "org";
            org.Value = orgId;
            command.Parameters.Add(org);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || await reader.IsDBNullAsync(0, ct).ConfigureAwait(false))
            {
                return false;
            }
            await using var content = reader.GetStream(0);
            await content.CopyToAsync(destination, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The SHA-256 (lower-case hex) and size of a build artifact's content, worked out by
    /// the database so a large <c>.app</c> is not pulled into memory to be hashed. Null
    /// when the organisation has no such artifact. The SQL bypasses the query filter, so
    /// it names the organisation itself.
    /// </summary>
    private async Task<(string Sha256, long Size)?> ArtifactHashAsync(int orgId, int artifactId, CancellationToken ct)
    {
        var connection = _db.Database.GetDbConnection();
        await _db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT encode(sha256(content), 'hex'), octet_length(content) FROM oe_project_build_artifacts WHERE id = @id AND organization_id = @org";
            var id = command.CreateParameter();
            id.ParameterName = "id";
            id.Value = artifactId;
            command.Parameters.Add(id);
            var org = command.CreateParameter();
            org.ParameterName = "org";
            org.Value = orgId;
            command.Parameters.Add(org);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || await reader.IsDBNullAsync(0, ct).ConfigureAwait(false))
            {
                return null;
            }
            return (reader.GetString(0), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
        }
        finally
        {
            await _db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fails every queued, claimed or running command of one gateway with
    /// <paramref name="message"/>. Used when the gateway is revoked: nothing it was
    /// asked to do can be collected or reported any more. Runs under the caller's
    /// query filter, so it only reaches the caller's organisation.
    /// </summary>
    internal static Task<int> FailOpenCommandsAsync(AppDbContext db, int gatewayId, string message, DateTime now, CancellationToken ct) =>
        db.GatewayCommands
            .Where(c => c.GatewayId == gatewayId
                        && (c.Status == GatewayCommandStatus.Queued
                            || c.Status == GatewayCommandStatus.Claimed
                            || c.Status == GatewayCommandStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, message), ct);

    // ---------------------------------------------------------------- the sweep

    /// <summary>
    /// One pass over the current organisation: expires queued commands past their
    /// claim deadline, fails the open commands of a re-keyed (pending) gateway and the
    /// claimed or running ones nobody has reported on for <see cref="SilenceLimit"/>,
    /// drops the content of final commands' files, and clears spent replay-protection rows. Called by
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

        // A gateway re-keyed by a rotation code is pending again, and whatever its old
        // key was working on cannot be collected or reported until an Admin approves
        // the new one. The rotation exchange itself runs before anyone is signed in, so
        // it cannot reach the organisation's commands; this pass does it within 30
        // seconds instead. Enqueueing refuses a pending gateway, so only a rotated one
        // has open commands here.
        var rekeyed = await _db.GatewayCommands
            .Where(c => (c.Status == GatewayCommandStatus.Queued
                         || c.Status == GatewayCommandStatus.Claimed
                         || c.Status == GatewayCommandStatus.Running)
                        && c.Gateway!.State == GatewayState.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, GatewayCommandStatus.Failed)
                .SetProperty(c => c.FinishedAt, now)
                .SetProperty(c => c.FailureMessage, RekeyedMessage), ct)
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

        // A final command is never downloaded again; keep the record, drop the bytes.
        await _db.GatewayCommandFiles
            .Where(f => f.Content != null
                        && (f.Command!.Status == GatewayCommandStatus.Succeeded
                            || f.Command.Status == GatewayCommandStatus.Failed
                            || f.Command.Status == GatewayCommandStatus.Expired))
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.Content, (byte[]?)null), ct)
            .ConfigureAwait(false);

        var tokens = await _db.GatewaySeenTokens
            .Where(t => t.ExpiresAt < now)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        if (expired + rekeyed + gatewaySilent + commandSilent > 0)
        {
            _logger.LogWarning(
                "Gateway command sweep for organisation {OrganizationId}: {Expired} expired uncollected, {Rekeyed} failed after a gateway was re-keyed, {Silent} failed after the gateway went quiet.",
                _orgContext.CurrentOrganizationId, expired, rekeyed, gatewaySilent + commandSilent);
        }
        return new GatewayCommandSweepResult(expired, rekeyed, gatewaySilent + commandSilent, tokens);
    }
}
