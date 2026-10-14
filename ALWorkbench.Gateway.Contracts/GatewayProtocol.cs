namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// The constants both ends of the gateway protocol must agree on. A change to any
/// of them is a protocol change: bump <see cref="Version"/> with it, so an old
/// gateway is refused with a message rather than failing in a way nobody can read.
/// See <c>.design/on-prem-gateway.md</c>, "Registration and authentication".
/// </summary>
public static class GatewayProtocol
{
    /// <summary>
    /// The protocol this build speaks. A gateway sends it when it registers and
    /// AL Workbench refuses any other value, telling customer IT to update.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Every gateway route sits under this path, and a request token's audience
    /// is the workbench's public origin followed by it
    /// (<c>https://workbench.example.com/gateway</c>).
    /// </summary>
    public const string PathPrefix = "/gateway";

    /// <summary>Where a gateway exchanges its registration code for a pending registration.</summary>
    public const string RegisterPath = PathPrefix + "/register";

    /// <summary>Where a registered gateway says it is alive, every <see cref="HeartbeatInterval"/>.</summary>
    public const string HeartbeatPath = PathPrefix + "/heartbeat";

    /// <summary>
    /// Where a gateway long-polls for its next command: held open for up to
    /// <see cref="LongPollTimeout"/>, answered with a <c>GatewayCommandMessage</c> as
    /// soon as one is queued for it, or 204 No Content when none was.
    /// </summary>
    public const string NextCommandPath = PathPrefix + "/commands/next";

    /// <summary>The longest the workbench holds <see cref="NextCommandPath"/> open before answering 204.</summary>
    public static readonly TimeSpan LongPollTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Where a gateway posts progress lines for command <paramref name="commandId"/>.</summary>
    public static string CommandProgressPath(long commandId) => $"{PathPrefix}/commands/{commandId}/progress";

    /// <summary>Where a gateway posts how command <paramref name="commandId"/> ended.</summary>
    public static string CommandResultPath(long commandId) => $"{PathPrefix}/commands/{commandId}/result";

    /// <summary>Where a gateway downloads file <paramref name="fileId"/> of command <paramref name="commandId"/>.</summary>
    public static string CommandFilePath(long commandId, long fileId) => $"{PathPrefix}/commands/{commandId}/files/{fileId}";

    /// <summary>How often a gateway calls <see cref="HeartbeatPath"/>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>The longest a request token may live: <c>exp - iat</c> is at most this.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How far the two clocks may disagree before a token is refused for its times.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The largest request body a gateway sends. The body is hashed into the token
    /// (<see cref="GatewayTokenClaims.BodyHash"/>), so the workbench reads all of it
    /// before it knows who is calling; this caps what a stranger can make it read.
    /// </summary>
    public const int MaxRequestBodyBytes = 4 * 1024 * 1024;

    /// <summary>The audience a token must carry for a workbench at <paramref name="origin"/>.</summary>
    public static string AudienceFor(string origin) => origin.TrimEnd('/') + PathPrefix;
}

/// <summary>
/// Claim names in the per-request token. The standard ones are the JWT names; the
/// request binding (<c>htm</c>, <c>htu</c>) borrows DPoP's (RFC 9449), and
/// <c>bh</c> is ours.
/// </summary>
public static class GatewayTokenClaims
{
    /// <summary>The gateway id the workbench returned at registration, as a string.</summary>
    public const string Issuer = "iss";
    public const string Audience = "aud";
    public const string IssuedAt = "iat";
    public const string Expires = "exp";
    /// <summary>A value unique to this token. The workbench refuses one it has seen before.</summary>
    public const string TokenId = "jti";
    /// <summary>The HTTP method of the request the token is for, upper case.</summary>
    public const string Method = "htm";
    /// <summary>The path of the request the token is for, without query string.</summary>
    public const string Path = "htu";
    /// <summary>Base64url SHA-256 of the request body. Required when the request has one.</summary>
    public const string BodyHash = "bh";

    /// <summary>The only signing algorithm accepted: ECDSA P-256 with SHA-256.</summary>
    public const string Algorithm = "ES256";
}
