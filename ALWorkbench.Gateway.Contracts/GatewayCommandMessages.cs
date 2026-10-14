using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// One command, as <c>GET /gateway/commands/next</c> hands it to the gateway. By the
/// time the gateway reads this the command is already claimed for it, and will not
/// be handed out again. See <c>.design/on-prem-gateway.md</c>, "Commands".
/// </summary>
public sealed record GatewayCommandMessage
{
    /// <summary>
    /// The command's id. The gateway keeps the ids it has started, so it never runs
    /// one twice - including after it restarts.
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("verb")]
    public GatewayVerb Verb { get; init; }

    /// <summary>The verb's parameters. Always a JSON object, possibly empty; data, never script.</summary>
    [JsonPropertyName("parameters")]
    public JsonObject Parameters { get; init; } = new();

    /// <summary>
    /// How many seconds the command had left before its claim deadline, by the
    /// workbench's clock, when it was handed out. The gateway counts down from when it
    /// received the response and refuses to start a command that has run out by its own
    /// count; it never compares the two clocks.
    /// </summary>
    [JsonPropertyName("secondsLeft")]
    public int SecondsLeft { get; init; }

    /// <summary>
    /// The display name of the person who asked for the command, for the gateway's own
    /// log, so customer IT can see who started each change. Null for a command nobody
    /// asked for (a scheduled one, or one whose requester was since deleted). Added
    /// after protocol version 1 shipped to no one; an older gateway ignores it.
    /// </summary>
    [JsonPropertyName("requestedBy")]
    public string? RequestedBy { get; init; }

    /// <summary>True when the person asked through an AI assistant rather than the web UI.</summary>
    [JsonPropertyName("startedByAgent")]
    public bool StartedByAgent { get; init; }

    /// <summary>The files this command needs, to fetch from <see cref="GatewayProtocol.CommandFilePath"/>.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<GatewayCommandFileMessage> Files { get; init; } = [];
}

/// <summary>A file a command needs: an <c>.app</c>, a licence.</summary>
public sealed record GatewayCommandFileMessage
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    /// <summary>The file's name, e.g. <c>CRONUS_Coffee_1.4.0.0.app</c>.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the content. The gateway refuses a download that does not match.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Length of the content in bytes.</summary>
    [JsonPropertyName("size")]
    public long Size { get; init; }
}

/// <summary>
/// Body of <c>POST /gateway/commands/{id}/progress</c>: lines to add to the command's
/// log. The first one also marks a claimed command as running. The workbench stamps
/// each line with its own clock.
/// </summary>
public sealed record GatewayCommandProgressRequest
{
    [JsonPropertyName("lines")]
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>
    /// This batch's number within the command, from 1 up. A batch whose answer was
    /// lost is sent again with the same number, and the workbench appends a number it
    /// has already appended only once. 0 (a gateway that does not send it) is always
    /// appended. Additive: no protocol version bump.
    /// </summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }
}

/// <summary>Body of <c>POST /gateway/commands/{id}/result</c>: how the command ended.</summary>
public sealed record GatewayCommandResultRequest
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; init; }

    /// <summary>What the verb reports back (per-app outcomes, an inventory), as a JSON object. Optional.</summary>
    [JsonPropertyName("result")]
    public JsonObject? Result { get; init; }

    /// <summary>Why it failed, in a sentence a consultant can act on. Required when <see cref="Succeeded"/> is false.</summary>
    [JsonPropertyName("failureMessage")]
    public string? FailureMessage { get; init; }
}
