using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The parameters of a <c>restart-instance</c>, <c>stop-instance</c> or <c>start-instance</c>
/// command (#1371): the one tier, by <see cref="GatewayCommandParameters.Machine"/> and
/// <see cref="GatewayCommandParameters.Instance"/>. The gateway acts on that tier only, and
/// only when its policy file lists it with <c>service_control</c>. See
/// <c>.design/on-prem-gateway.md</c>, "Service tier actions, as built".
/// </summary>
public sealed record GatewayServiceTierParameters
{
    public string Machine { get; init; } = string.Empty;

    public string Instance { get; init; } = string.Empty;

    public JsonObject ToParameters() => new()
    {
        [GatewayCommandParameters.Machine] = Machine,
        [GatewayCommandParameters.Instance] = Instance,
    };
}

/// <summary>What a single-tier action asks for. Words on the wire, in <see cref="GatewayServiceTierResult.Action"/>.</summary>
public static class GatewayServiceTierActions
{
    public const string Restart = "restart";

    public const string Stop = "stop";

    public const string Start = "start";

    /// <summary>The action a single-tier verb performs; null for any other verb.</summary>
    public static string? For(GatewayVerb verb) => verb switch
    {
        GatewayVerb.RestartInstance => Restart,
        GatewayVerb.StopInstance => Stop,
        GatewayVerb.StartInstance => Start,
        _ => null,
    };
}

/// <summary>How a <c>restart-instance</c>, <c>stop-instance</c> or <c>start-instance</c> command went (#1371).</summary>
public sealed record GatewayServiceTierResult
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>One of <see cref="GatewayServiceTierActions"/>.</summary>
    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    [JsonPropertyName("instance")]
    public string Instance { get; init; } = string.Empty;

    /// <summary>True when the tier reached the state asked for: running after a restart or start, stopped after a stop.</summary>
    [JsonPropertyName("done")]
    public bool Done { get; init; }

    /// <summary>The Windows service's state before the gateway sent it anything, when it was read.</summary>
    [JsonPropertyName("before")]
    public string? Before { get; init; }

    /// <summary>The Windows service's state as last read, e.g. <c>Running</c> or <c>Stopped</c>.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>Why it did not get there, as a sentence; null when it did.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Options)!.AsObject();

    /// <summary>Reads a result back; null when it is not one.</summary>
    public static GatewayServiceTierResult? FromJson(JsonObject? result)
    {
        if (result is null) return null;
        try
        {
            return result.Deserialize<GatewayServiceTierResult>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
