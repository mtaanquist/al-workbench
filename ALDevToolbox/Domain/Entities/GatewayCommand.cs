using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Where a <see cref="GatewayCommand"/> is in its life:
/// <c>queued -> claimed -> running -> succeeded | failed</c>, or <c>queued -> expired</c>.
/// Every move out of a state is a compare-and-set on the row, so a claim and the
/// sweep, or two claims, can never both win.
/// </summary>
public enum GatewayCommandStatus
{
    /// <summary>Waiting for the gateway to collect it, from its due time until its claim deadline.</summary>
    Queued,

    /// <summary>Handed to the gateway by the long-poll. It will not be handed out again.</summary>
    Claimed,

    /// <summary>The gateway has reported progress on it.</summary>
    Running,

    Succeeded,

    /// <summary>The gateway reported a failure, restarted while running it, or went silent.</summary>
    Failed,

    /// <summary>Nobody collected it before its claim deadline. Never sent.</summary>
    Expired,
}

/// <summary>
/// One piece of work for an on-premises gateway. Commands are rows, not messages:
/// a command survives a restart of either side, and the table is the record of what
/// was asked and what came back. Written by <c>GatewayCommandService</c>; nothing
/// here is audited row by row, since the row is its own history. See
/// <c>.design/on-prem-gateway.md</c>, "Commands".
/// </summary>
public class GatewayCommand
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }

    public int GatewayId { get; set; }
    public Gateway? Gateway { get; set; }

    public GatewayVerb Verb { get; set; }

    /// <summary>The verb's parameters, a JSON object (jsonb). Data, never script.</summary>
    public string ParametersJson { get; set; } = "{}";

    public GatewayCommandStatus Status { get; set; } = GatewayCommandStatus.Queued;

    /// <summary>The person who asked for it; null for the workbench's own work (a scheduled inventory).</summary>
    public int? RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    /// <summary>True when an AI assistant asked for it, as <c>OeProjectDelivery.StartedByAgent</c>.</summary>
    public bool StartedByAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The earliest the gateway may collect it.</summary>
    public DateTime DueAt { get; set; }

    /// <summary>
    /// The claim deadline: 15 minutes past <see cref="DueAt"/>, or the end of the
    /// delivery window it was scheduled in, whichever is earlier. Not collected by
    /// then, it expires. Only the workbench's clock judges it.
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime? ClaimedAt { get; set; }

    /// <summary>When the gateway first reported progress on it.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// The last time the gateway showed it was still working on it: the claim, a
    /// progress post, or a heartbeat that listed it. The sweep fails a claimed or
    /// running command when this is ten minutes old.
    /// </summary>
    public DateTime? LastReportedAt { get; set; }

    /// <summary>
    /// The highest progress batch number appended to <see cref="Log"/>, so a batch the
    /// gateway sends again after a lost answer is not appended twice. 0 until a
    /// numbered batch arrives.
    /// </summary>
    public long LastProgressSequence { get; set; }

    public DateTime? FinishedAt { get; set; }

    /// <summary>What the verb reported back, a JSON object (jsonb); null until it finishes.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Why it failed or expired, in a sentence a person can act on.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// The progress lines the gateway posted, each prefixed with the workbench's
    /// UTC time. Appended in the database, never rewritten.
    /// </summary>
    public string Log { get; set; } = string.Empty;

    public ICollection<GatewayCommandFile> Files { get; set; } = new List<GatewayCommandFile>();
}

/// <summary>
/// A file one command needs (an <c>.app</c>, a licence), downloadable only by the
/// gateway the command is for, only while that command is claimed or running.
/// </summary>
public class GatewayCommandFile
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }

    public long CommandId { get; set; }
    public GatewayCommand? Command { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of <see cref="Content"/>, sent with the command so the gateway can check the download.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>
    /// The file itself, until its command is final: the sweep then clears it, since a
    /// finished command is never downloaded again and an .app or licence file kept
    /// forever is pure weight. The name, hash and size stay as the record of what was sent.
    /// </summary>
    public byte[]? Content { get; set; }
}
