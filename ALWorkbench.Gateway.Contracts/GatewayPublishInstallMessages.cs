using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The parameters of a <c>publish-install</c> command, typed, for AL Workbench to build
/// the command with (#1367) and for the tests to build it the same way. On the wire they
/// are the plain object <see cref="GatewayCommandMessage.Parameters"/> with the names in
/// <see cref="GatewayCommandParameters"/>; <see cref="ToParameters"/> writes exactly that.
/// See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed record GatewayPublishInstallParameters
{
    /// <summary>The database's publishing tier: its computer.</summary>
    public string Machine { get; init; } = string.Empty;

    /// <summary>The database's publishing tier: its server instance.</summary>
    public string Instance { get; init; } = string.Empty;

    /// <summary>The tenant to install on; null on a single-tenant server (tenant <c>default</c>).</summary>
    public string? Tenant { get; init; }

    /// <summary>One of <see cref="GatewaySyncModes"/>.</summary>
    public string SyncMode { get; init; } = GatewaySyncModes.Add;

    /// <summary>Whether to unpublish each app's previous version once it is replaced.</summary>
    public bool UnpublishPrevious { get; init; } = true;

    /// <summary>
    /// The apps, in the order they are to be installed: the build's dependency order,
    /// dependencies first. One per file of the command.
    /// </summary>
    public IReadOnlyList<GatewayPublishInstallApp> Apps { get; init; } = [];

    /// <summary>The command's parameters object.</summary>
    public JsonObject ToParameters()
    {
        var apps = new JsonArray();
        foreach (var app in Apps)
        {
            var item = new JsonObject
            {
                [GatewayCommandParameters.AppFile] = app.File,
                [GatewayCommandParameters.AppId] = app.Id.ToString("D"),
                [GatewayCommandParameters.AppName] = app.Name,
                [GatewayCommandParameters.AppPublisher] = app.Publisher,
                [GatewayCommandParameters.AppVersion] = app.Version,
                [GatewayCommandParameters.AppSigned] = app.Signed,
            };
            apps.Add(item);
        }
        var parameters = new JsonObject
        {
            [GatewayCommandParameters.Machine] = Machine,
            [GatewayCommandParameters.Instance] = Instance,
            [GatewayCommandParameters.SyncMode] = SyncMode,
            [GatewayCommandParameters.UnpublishPrevious] = UnpublishPrevious,
            [GatewayCommandParameters.Apps] = apps,
        };
        if (Tenant is not null) parameters[GatewayCommandParameters.Tenant] = Tenant;
        return parameters;
    }
}

/// <summary>One app of a <c>publish-install</c>, as AL Workbench declares it from the build.</summary>
public sealed record GatewayPublishInstallApp
{
    /// <summary>The name of the command's file that holds it (<see cref="GatewayCommandFileMessage.Name"/>).</summary>
    public string File { get; init; } = string.Empty;

    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Publisher { get; init; } = string.Empty;

    /// <summary>Four-part version, e.g. <c>1.4.0.0</c>.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether AL Workbench believes it is signed. A claim; the gateway checks the file.</summary>
    public bool Signed { get; init; }
}

/// <summary>
/// What a <c>publish-install</c> reports in <see cref="GatewayCommandResultRequest.Result"/>:
/// one entry per app, in the order they were declared, whether the command succeeded or
/// not. AL Workbench (#1367) turns it into the deployment's per-app rows.
/// </summary>
public sealed record GatewayPublishInstallResult
{
    /// <summary>Bumped when the shape changes.</summary>
    public const int CurrentSchema = 1;

    [JsonPropertyName("schema")]
    public int Schema { get; init; } = CurrentSchema;

    [JsonPropertyName("apps")]
    public IReadOnlyList<GatewayPublishInstallAppResult> Apps { get; init; } = [];

    /// <summary>
    /// Whether the restart group ran after the apps. False until the gateway restarts
    /// tiers (#1368), and false whenever an app failed.
    /// </summary>
    [JsonPropertyName("restarted")]
    public bool Restarted { get; init; }

    private static readonly JsonSerializerOptions Options = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public JsonObject ToJson() => JsonSerializer.SerializeToNode(this, Options)!.AsObject();

    /// <summary>Reads a result back; null when it is not one.</summary>
    public static GatewayPublishInstallResult? FromJson(JsonObject? result)
    {
        if (result is null) return null;
        try
        {
            return result.Deserialize<GatewayPublishInstallResult>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>How one app of a <c>publish-install</c> ended.</summary>
public sealed record GatewayPublishInstallAppResult
{
    [JsonPropertyName("file")]
    public string File { get; init; } = string.Empty;

    /// <summary>The app's id as read from the file; the declared one when the file could not be read.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>One of <see cref="GatewayPublishInstallOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = GatewayPublishInstallOutcomes.Skipped;

    /// <summary>The version installed on the tenant before, for an upgrade.</summary>
    [JsonPropertyName("fromVersion")]
    public string? FromVersion { get; init; }

    /// <summary>A sentence for the person: why it failed, was refused or skipped.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<GatewayPublishInstallStep> Steps { get; init; } = [];
}

/// <summary>One step of one app, in the order they ran.</summary>
public sealed record GatewayPublishInstallStep
{
    /// <summary>One of <see cref="GatewayPublishInstallSteps"/>.</summary>
    [JsonPropertyName("step")]
    public string Step { get; init; } = string.Empty;

    /// <summary>One of <see cref="GatewayPublishInstallStepOutcomes"/>.</summary>
    [JsonPropertyName("outcome")]
    public string Outcome { get; init; } = string.Empty;

    /// <summary>The version the step acted on, where it is not the app's own (an unpublished previous version).</summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>How an app ended. Words on the wire.</summary>
public static class GatewayPublishInstallOutcomes
{
    /// <summary>Not installed before; now installed.</summary>
    public const string Installed = "installed";

    /// <summary>An older version was installed, or its data kept; now upgraded.</summary>
    public const string Upgraded = "upgraded";

    /// <summary>This version was already installed on the tenant; nothing was done (#931).</summary>
    public const string AlreadyInstalled = "already-installed";

    /// <summary>A step failed. Steps before it may have changed the tier (see the steps).</summary>
    public const string Failed = "failed";

    /// <summary>The file was not the app declared, or the customer's settings do not allow it. Nothing was run.</summary>
    public const string Refused = "refused";

    /// <summary>Not started, because an app before it failed or was refused.</summary>
    public const string Skipped = "skipped";
}

/// <summary>The steps of one app. Words on the wire.</summary>
public static class GatewayPublishInstallSteps
{
    /// <summary>The file's SHA-256, its id, name, publisher and version, and whether it carries a signature.</summary>
    public const string Verify = "verify";

    /// <summary>The file's signature, checked by Windows on the gateway's computer.</summary>
    public const string Signature = "signature";

    public const string Publish = "publish";

    public const string Sync = "sync";

    public const string Install = "install";

    public const string Upgrade = "upgrade";

    public const string Unpublish = "unpublish";
}

/// <summary>How a step ended. Words on the wire.</summary>
public static class GatewayPublishInstallStepOutcomes
{
    public const string Done = "done";

    public const string Failed = "failed";

    /// <summary>Deliberately not done (already published; a previous version still in use elsewhere).</summary>
    public const string Skipped = "skipped";
}
