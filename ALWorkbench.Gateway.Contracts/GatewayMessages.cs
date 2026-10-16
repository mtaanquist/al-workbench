using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// Body of <c>POST /gateway/register</c>: the one-time code an Admin handed out,
/// and the public half of the key pair the gateway just generated. The private
/// half never leaves the machine.
/// </summary>
public sealed record GatewayRegistrationRequest
{
    /// <summary>The registration code shown on the Gateways page, as typed (dashes and spaces are ignored).</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// The gateway's ECDSA P-256 public key as a base64 DER <c>SubjectPublicKeyInfo</c>,
    /// which is what <c>ECDsa.ExportSubjectPublicKeyInfo()</c> produces.
    /// </summary>
    [JsonPropertyName("publicKey")]
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>The Windows computer name the gateway runs on, shown to the Admin who approves it.</summary>
    [JsonPropertyName("machineName")]
    public string MachineName { get; init; } = string.Empty;

    /// <summary>The gateway program's own version, for the Gateways page.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>The <see cref="GatewayProtocol.Version"/> the gateway speaks.</summary>
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }
}

/// <summary>Answer to a successful registration.</summary>
public sealed record GatewayRegistrationResponse
{
    /// <summary>The id the gateway puts in every token's <c>iss</c> from now on.</summary>
    [JsonPropertyName("gatewayId")]
    public int GatewayId { get; init; }

    /// <summary>The name the gateway is listed under on the Gateways page.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// <see cref="GatewayKeyFingerprint"/> of the key the workbench stored. The gateway
    /// prints it, and the Admin compares it with the one on the Gateways page before
    /// approving.
    /// </summary>
    [JsonPropertyName("keyFingerprint")]
    public string KeyFingerprint { get; init; } = string.Empty;

    /// <summary>Always false straight after registering: an Admin approves the gateway first.</summary>
    [JsonPropertyName("approved")]
    public bool Approved { get; init; }
}

/// <summary>Body of <c>POST /gateway/heartbeat</c>.</summary>
public sealed record GatewayHeartbeatRequest
{
    /// <summary>The gateway program's own version; it changes when customer IT updates it.</summary>
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    /// <summary>The Windows computer name, in case the gateway has moved.</summary>
    [JsonPropertyName("machineName")]
    public string MachineName { get; init; } = string.Empty;
}

/// <summary>Answer to a heartbeat.</summary>
public sealed record GatewayHeartbeatResponse
{
    /// <summary>
    /// False while the gateway waits for an Admin's approval (or after its key was
    /// replaced). A gateway that is not approved receives no commands and no files.
    /// </summary>
    [JsonPropertyName("approved")]
    public bool Approved { get; init; }
}

/// <summary>
/// Body of every refusal from a gateway route: field-keyed messages written for
/// the person running the gateway.
/// </summary>
public sealed record GatewayErrorResponse
{
    [JsonPropertyName("errors")]
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();
}
