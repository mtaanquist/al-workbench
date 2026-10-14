using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALWorkbench.Gateway.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// Authenticates an on-premises gateway from the token it signs for each request
/// (<see cref="GatewayRequestToken"/>). Its own scheme, opted into only by the
/// gateway routes through <see cref="PolicyName"/>; no cookie or MCP policy lists
/// it, and the gateway policy lists nothing else.
///
/// <para>The principal it builds carries the gateway's organisation in
/// <see cref="HttpOrganizationContext.OrganizationIdClaim"/>, exactly as PAT
/// validation sets it, so the EF query filter scopes everything the gateway routes
/// read. It carries <b>no user claim</b>: a service that needs a user cannot be
/// reached by a gateway by accident. Its name is "Gateway {name}", which is what the
/// audit interceptor records as the actor. See <c>.design/on-prem-gateway.md</c>,
/// "Registration and authentication".</para>
/// </summary>
public sealed class GatewayAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string AuthenticationScheme = "Gateway";

    /// <summary>The policy every gateway route requires.</summary>
    public const string PolicyName = "Gateway";

    /// <summary>The gateway's id on the principal.</summary>
    public const string GatewayIdClaim = "gateway_id";

    /// <summary>
    /// <c>"true"</c> once an Admin approved the gateway's current key, else <c>"false"</c>.
    /// A pending gateway authenticates - it may heartbeat - and the routes that hand
    /// out commands or files refuse it on this claim.
    /// </summary>
    public const string GatewayApprovedClaim = "gateway_approved";

    /// <summary>
    /// What an unknown or revoked gateway and a token whose signature does not verify
    /// are both told. One message for all of them on purpose: telling them apart would
    /// let a stranger learn which gateway ids exist. The log says which it was.
    /// </summary>
    internal const string UnrecognisedMessage =
        "AL Workbench does not recognise this gateway or its key. It may have been revoked or registered again; check Administration → Gateways.";

    private readonly GatewayService _gateways;
    private readonly PublicOrigin _origin;

    public GatewayAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        GatewayService gateways,
        PublicOrigin origin)
        : base(options, logger, encoder)
    {
        _gateways = gateways;
        _origin = origin;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)) return AuthenticateResult.NoResult();
        var raw = header.ToString();
        var prefix = GatewayRequestToken.AuthorizationScheme + " ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = raw[prefix.Length..].Trim();

        if (!GatewayTokenValidator.TryReadIssuer(token, out var gatewayId))
        {
            return AuthenticateResult.Fail("The request does not carry a gateway token.");
        }

        var ct = Context.RequestAborted;
        var credential = await _gateways.FindCredentialAsync(gatewayId, ct);
        if (credential is null)
        {
            Logger.LogInformation("Refused a gateway request for unknown or revoked gateway {GatewayId}.", gatewayId);
            return AuthenticateResult.Fail(UnrecognisedMessage);
        }

        // Signature and every claim but the body hash first, so nothing is read from
        // the body until the caller has proved it holds this gateway's key.
        var request = new GatewayTokenRequest(
            Method: Request.Method.ToUpperInvariant(),
            Path: (Request.PathBase + Request.Path).Value ?? string.Empty,
            Audience: GatewayProtocol.AudienceFor(_origin.For(Context)),
            Now: TimeProvider.GetUtcNow());
        var (validated, refusal) = GatewayTokenValidator.Validate(token, credential.PublicKey, credential.GatewayId, request);
        if (validated is null)
        {
            Logger.LogWarning("Refused a request from gateway {GatewayId}: {Reason}", gatewayId, refusal!.Reason);
            // Before the signature is verified, the caller is a stranger: answer as for a
            // gateway that does not exist, so the refusal says nothing about which ids do.
            return AuthenticateResult.Fail(refusal.BeforeSignature ? UnrecognisedMessage : refusal.Reason);
        }

        var body = await ReadBodyAsync(ct);
        if (body is null)
        {
            Logger.LogWarning("Refused a request from gateway {GatewayId}: the body is over {Limit} bytes.", gatewayId, GatewayProtocol.MaxRequestBodyBytes);
            return AuthenticateResult.Fail($"The request body is larger than {GatewayProtocol.MaxRequestBodyBytes} bytes.");
        }
        if (GatewayTokenValidator.CheckBody(validated, GatewayRequestToken.HashBody(body), body.Length > 0) is { } bodyError)
        {
            Logger.LogWarning("Refused a request from gateway {GatewayId}: {Reason}", gatewayId, bodyError);
            return AuthenticateResult.Fail(bodyError);
        }

        // The jti is kept until the token could not pass the time check anyway.
        var keepUntil = (validated.ExpiresAt + GatewayProtocol.ClockSkew).UtcDateTime;
        if (!await _gateways.TryRecordTokenAsync(credential, validated.TokenId, keepUntil, ct))
        {
            Logger.LogWarning("Refused a replayed request from gateway {GatewayId} (token {TokenId}).", gatewayId, validated.TokenId);
            return AuthenticateResult.Fail("This request was already received once. Every request needs a new token.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "Gateway " + credential.Name),
            new(HttpOrganizationContext.OrganizationIdClaim, credential.OrganizationId.ToString(CultureInfo.InvariantCulture)),
            new(GatewayIdClaim, credential.GatewayId.ToString(CultureInfo.InvariantCulture)),
            new(GatewayApprovedClaim, credential.State == GatewayState.Approved ? "true" : "false"),
        };
        var identity = new ClaimsIdentity(claims, AuthenticationScheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme));
    }

    /// <summary>
    /// Reads the whole body so its hash can be checked, then rewinds it for the
    /// endpoint. Only called once the token's signature has verified. Null when it is
    /// over <see cref="GatewayProtocol.MaxRequestBodyBytes"/>.
    /// </summary>
    private async Task<byte[]?> ReadBodyAsync(CancellationToken ct)
    {
        if (Request.ContentLength > GatewayProtocol.MaxRequestBodyBytes) return null;
        Request.EnableBuffering(bufferThreshold: 64 * 1024, bufferLimit: GatewayProtocol.MaxRequestBodyBytes + 1L);
        using var copy = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await Request.Body.ReadAsync(buffer, ct)) > 0)
        {
            copy.Write(buffer, 0, read);
            if (copy.Length > GatewayProtocol.MaxRequestBodyBytes) return null;
        }
        Request.Body.Position = 0;
        return copy.ToArray();
    }

    /// <summary>
    /// 401 with the reason in the body. The reason is safe to show - it names which
    /// check failed, never a key - and the commonest one, a drifted clock, is
    /// something customer IT can fix from it. A body also keeps
    /// <c>UseStatusCodePagesWithReExecute</c> from turning the 401 into a page.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var result = await HandleAuthenticateOnceSafeAsync();
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append("WWW-Authenticate", GatewayRequestToken.AuthorizationScheme);
        await Response.WriteAsJsonAsync(new GatewayErrorResponse
        {
            Errors = new Dictionary<string, string>
            {
                ["authorization"] = result.Failure?.Message ?? "This route is for registered AL Workbench gateways only.",
            },
        }, Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new GatewayErrorResponse
        {
            Errors = new Dictionary<string, string> { ["authorization"] = "This gateway may not do that." },
        }, Context.RequestAborted);
    }
}
