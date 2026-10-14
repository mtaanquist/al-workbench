using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ALWorkbench.Gateway.Contracts;

/// <summary>
/// Writes the token a gateway signs for each request: a compact JWT (ES256) whose
/// claims are listed in <see cref="GatewayTokenClaims"/>. It lives here rather than
/// in the gateway so the encoding of the request binding - the method, the path and
/// the body hash - is defined once, next to the workbench code that checks it.
///
/// <para>No reusable secret is in it: the token is good for one request, for at
/// most <see cref="GatewayProtocol.MaxTokenLifetime"/>, and the workbench remembers
/// its <c>jti</c> until it expires. See <c>.design/on-prem-gateway.md</c>,
/// "Registration and authentication".</para>
/// </summary>
public static class GatewayRequestToken
{
    /// <summary>The scheme word in front of the token in the <c>Authorization</c> header.</summary>
    public const string AuthorizationScheme = "Bearer";

    private static readonly byte[] HeaderJson = Encoding.UTF8.GetBytes(
        "{\"alg\":\"" + GatewayTokenClaims.Algorithm + "\",\"typ\":\"JWT\"}");

    /// <summary>
    /// Signs a token for one request.
    /// </summary>
    /// <param name="key">The gateway's private key (ECDSA P-256).</param>
    /// <param name="gatewayId">The id returned at registration.</param>
    /// <param name="audience">
    /// <see cref="GatewayProtocol.AudienceFor"/> of the workbench's public origin.
    /// </param>
    /// <param name="method">The HTTP method, e.g. <c>POST</c>.</param>
    /// <param name="path">The request path without query string, e.g. <c>/gateway/heartbeat</c>.</param>
    /// <param name="body">The exact request body bytes, as they will be sent.</param>
    /// <param name="hasBody">False for a request with no body (a GET): no <c>bh</c> claim is written.</param>
    /// <param name="now">The gateway's clock.</param>
    /// <param name="lifetime">How long the token lives. Defaults to <see cref="GatewayProtocol.MaxTokenLifetime"/>; the workbench refuses a longer one.</param>
    /// <param name="tokenId">The <c>jti</c>; a fresh random one when null.</param>
    public static string Create(
        ECDsa key,
        int gatewayId,
        string audience,
        string method,
        string path,
        ReadOnlySpan<byte> body,
        bool hasBody,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        string? tokenId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        var life = lifetime ?? GatewayProtocol.MaxTokenLifetime;
        var iat = now.ToUnixTimeSeconds();
        var exp = iat + (long)life.TotalSeconds;

        using var payload = new MemoryStream();
        using (var json = new Utf8JsonWriter(payload))
        {
            json.WriteStartObject();
            json.WriteString(GatewayTokenClaims.Issuer, gatewayId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            json.WriteString(GatewayTokenClaims.Audience, audience);
            json.WriteNumber(GatewayTokenClaims.IssuedAt, iat);
            json.WriteNumber(GatewayTokenClaims.Expires, exp);
            json.WriteString(GatewayTokenClaims.TokenId, tokenId ?? NewTokenId());
            json.WriteString(GatewayTokenClaims.Method, method.ToUpperInvariant());
            json.WriteString(GatewayTokenClaims.Path, path);
            if (hasBody)
            {
                json.WriteString(GatewayTokenClaims.BodyHash, HashBody(body));
            }
            json.WriteEndObject();
        }

        var signingInput = Base64Url.EncodeToString(HeaderJson) + "." + Base64Url.EncodeToString(payload.ToArray());
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return signingInput + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>The <c>bh</c> claim for a body: base64url SHA-256 of its bytes.</summary>
    public static string HashBody(ReadOnlySpan<byte> body)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(body, hash);
        return Base64Url.EncodeToString(hash);
    }

    /// <summary>128 random bits, base64url: unique per token without coordination.</summary>
    public static string NewTokenId() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
