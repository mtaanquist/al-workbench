using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The parameters of a <c>restart-group</c> command (#1368): one tier of the database, by
/// <see cref="GatewayCommandParameters.Machine"/> and <see cref="GatewayCommandParameters.Instance"/>.
/// The gateway restarts the restart group its own policy file lists for that tier's
/// database, never a tier the command names beyond that one. Served by the manual restart
/// (#1371); the licence import (#1369) runs the same step inside its own verb. See
/// <c>.design/on-prem-gateway.md</c>, "The restart group, as built".
/// </summary>
public sealed record GatewayRestartGroupParameters
{
    public string Machine { get; init; } = string.Empty;

    public string Instance { get; init; } = string.Empty;

    public JsonObject ToParameters() => new()
    {
        [GatewayCommandParameters.Machine] = Machine,
        [GatewayCommandParameters.Instance] = Instance,
    };
}

/// <summary>
/// How a restart group went: one entry per tier, in the order the policy file lists them.
/// The result of a <c>restart-group</c> command, and <see cref="GatewayPublishInstallResult.Restart"/>
/// after a <c>publish-install</c>.
/// </summary>
public sealed record GatewayRestartGroupResult
{
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    /// <summary>The policy's <c>restart_mode</c>: <c>stop-all-then-start-all</c> or <c>one-by-one</c>.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; init; } = string.Empty;

    /// <summary>True when every tier of the group is running again after its restart.</summary>
    [JsonPropertyName("restarted")]
    public bool Restarted { get; init; }

    /// <summary>Why the group was not restarted at all, when it was not (nothing was installed, the settings changed).</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("tiers")]
    public IReadOnlyList<GatewayRestartTierResult> Tiers { get; init; } = [];

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Options)!.AsObject();

    /// <summary>Reads a result back; null when it is not one.</summary>
    public static GatewayRestartGroupResult? FromJson(JsonObject? result)
    {
        if (result is null) return null;
        try
        {
            return result.Deserialize<GatewayRestartGroupResult>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>One tier of a restart group.</summary>
public sealed record GatewayRestartTierResult
{
    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    [JsonPropertyName("instance")]
    public string Instance { get; init; } = string.Empty;

    /// <summary>One of <see cref="GatewayRestartTierOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = GatewayRestartTierOutcomes.NotRestarted;

    /// <summary>A sentence for the person: why it failed, or why it was not restarted.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    /// <summary>The Windows service's state as last read, e.g. <c>Running</c> or <c>Stopped</c>, when it was read.</summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    /// <summary>The Windows service's state before the gateway sent it anything, when it was read (#1371). Additive.</summary>
    [JsonPropertyName("before")]
    public string? Before { get; init; }
}

/// <summary>How one tier of a restart group ended. Words on the wire.</summary>
public static class GatewayRestartTierOutcomes
{
    /// <summary>Stopped and started again, and running.</summary>
    public const string Restarted = "restarted";

    /// <summary>It did not stop, did not start, or did not report running in time.</summary>
    public const string Failed = "failed";

    /// <summary>Left alone, because another tier of the group failed first.</summary>
    public const string NotRestarted = "not-restarted";
}

/// <summary>Customer IT's before or after script for a database, as it ran.</summary>
public sealed record GatewayScriptResult
{
    /// <summary>One of <see cref="GatewayScriptStages"/>.</summary>
    [JsonPropertyName("stage")]
    public string Stage { get; init; } = string.Empty;

    /// <summary>The script's full path, as the policy file names it.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    /// <summary>One of <see cref="GatewayScriptOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = GatewayScriptOutcomes.NotRun;

    /// <summary>The script's exit code, when it ended by itself.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>A sentence for the person: why it failed, or why it was not run.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>When a script runs. Words on the wire.</summary>
public static class GatewayScriptStages
{
    /// <summary>After every file is checked, before anything is published.</summary>
    public const string Before = "before";

    /// <summary>After the restart group restarted every tier.</summary>
    public const string After = "after";
}

/// <summary>How a script ended. Words on the wire.</summary>
public static class GatewayScriptOutcomes
{
    public const string Succeeded = "succeeded";

    /// <summary>A non-zero exit code, an error it threw, a timeout, or the gateway refused to run it.</summary>
    public const string Failed = "failed";

    /// <summary>Not run, because something before it did not go through.</summary>
    public const string NotRun = "not-run";
}

/// <summary>
/// The length a step's message is cut to (a script's error, a tier's failure), on the
/// gateway before the result is sent and in AL Workbench before it is stored: a script
/// can write megabytes to its error stream, and a result must stay well inside
/// <see cref="GatewayProtocol.MaxRequestBodyBytes"/>. The whole output is in the command's log.
/// </summary>
public static class GatewayStepText
{
    public const int MaxMessageChars = 2_000;

    /// <summary><paramref name="text"/>, or its first <see cref="MaxMessageChars"/> characters ending in "...".</summary>
    public static string? Cap(string? text) => Cap(text, MaxMessageChars);

    /// <summary><paramref name="text"/>, or its first <paramref name="max"/> characters ending in "...".</summary>
    public static string? Cap(string? text, int max)
    {
        if (text is null || text.Length <= max) return text;
        var cut = max - 3;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut] + "...";
    }
}
