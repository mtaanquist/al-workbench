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
    /// The oldest gateway program this AL Workbench accepts. A gateway that names an
    /// older release in <see cref="GatewayVersionHeader"/> is refused on every route
    /// with <see cref="OutdatedStatusCode"/> and a message telling customer IT to
    /// install the current build. Raise it when a change makes older gateways unsafe or
    /// unable to work, never above the release that carries the change; the workbench's
    /// own version (the gateway it offers for download) is the expected one, and older
    /// gateways at or above this minimum are only listed as out of date.
    /// No gateway was released before this version, so it refuses nothing real yet.
    /// See <c>.design/on-prem-gateway.md</c>, "Versions and updates".
    /// </summary>
    public const string MinimumGatewayVersion = "11.19.0";

    /// <summary>
    /// The header every gateway request carries its program version in
    /// (<see cref="GatewayVersion"/>). A header rather than a body field so the long-poll
    /// and downloads, which have no body, are checked too, and so the check runs before
    /// the token is read: a gateway too old to speak today's protocol still gets a
    /// message it can log. Not part of the signed request: a gateway that lies about its
    /// version only refuses itself work.
    /// </summary>
    public const string GatewayVersionHeader = "ALWorkbench-Gateway-Version";

    /// <summary>
    /// 426 Upgrade Required: "the server refuses to perform the request using the current
    /// protocol but might be willing to do so after the client upgrades" (RFC 9110, 15.5.22),
    /// which is exactly this refusal. Distinct from 401 (revoked or clock), 403 (waiting for
    /// approval) and 400, so the gateway can tell it apart and say what to do. The RFC's
    /// <c>Upgrade</c> response header is not sent: it names connection protocols, is
    /// hop-by-hop (a reverse proxy drops it) and is forbidden in HTTP/2; the body says
    /// which version is needed.
    /// </summary>
    public const int OutdatedStatusCode = 426;

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

    /// <summary>
    /// Where a gateway posts what it read from its service tiers (<c>GatewayInventoryReport</c>):
    /// every <c>GatewayInventoryLimits.Interval</c>, after a command, and when asked
    /// with <c>refresh-inventory</c>. A route added after protocol version 1 shipped to
    /// no one; an older gateway never calls it.
    /// </summary>
    public const string InventoryPath = PathPrefix + "/inventory";

    /// <summary>How often a gateway calls <see cref="HeartbeatPath"/>.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);

    /// <summary>The longest a request token may live: <c>exp - iat</c> is at most this.</summary>
    public static readonly TimeSpan MaxTokenLifetime = TimeSpan.FromSeconds(60);

    /// <summary>How far the two clocks may disagree before a token is refused for its times.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The largest request body a gateway sends. The body is hashed into the token
    /// (<see cref="GatewayTokenClaims.BodyHash"/>), so the workbench reads all of it
    /// to check the hash - only after the token's signature has verified, so a
    /// stranger cannot make it read any; this caps what a registered gateway can.
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
