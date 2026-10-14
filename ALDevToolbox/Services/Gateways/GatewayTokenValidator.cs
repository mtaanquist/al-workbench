using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALWorkbench.Gateway.Contracts;

namespace ALDevToolbox.Services.Gateways;

/// <summary>What the request a token arrived on actually was, to compare the token's binding against.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The request path, without query string.</param>
/// <param name="Audience">The audience this workbench expects: <see cref="GatewayProtocol.AudienceFor"/> of its public origin.</param>
/// <param name="BodyHash">Base64url SHA-256 of the body as received (of no bytes when there was none).</param>
/// <param name="HasBody">True when the request carried at least one byte of body.</param>
/// <param name="Now">The workbench's clock.</param>
public sealed record GatewayTokenRequest(
    string Method, string Path, string Audience, string BodyHash, bool HasBody, DateTimeOffset Now);

/// <summary>A token that passed every check but the replay check, which needs the database.</summary>
/// <param name="TokenId">The <c>jti</c>, to remember.</param>
/// <param name="ExpiresAt">The token's <c>exp</c>.</param>
public sealed record ValidatedGatewayToken(string TokenId, DateTimeOffset ExpiresAt);

/// <summary>
/// Checks a gateway's per-request token (<see cref="GatewayRequestToken"/>) against
/// the gateway's stored public key and the request it arrived on. Pure: no database,
/// no clock of its own, so every refusal has a unit test. The replay check (the
/// <c>jti</c>) is the one thing it cannot do, and <see cref="GatewayService"/> does
/// it after this passes. See <c>.design/on-prem-gateway.md</c>, "Registration and
/// authentication".
///
/// <para>Hand-rolled over <c>System.Security.Cryptography</c> rather than a JWT
/// library on purpose: the format is fixed by our own contract (one algorithm, one
/// key per issuer, a handful of claims), and spelling each check out keeps the
/// library's own defaults - accepted algorithms, lifetime rules, claim mapping -
/// from quietly deciding what a gateway may send.</para>
/// </summary>
public static class GatewayTokenValidator
{
    /// <summary>Longest <c>jti</c> accepted; the column holds it.</summary>
    public const int MaxTokenIdLength = 128;

    /// <summary>
    /// Reads the token's <c>iss</c> without verifying anything, to know whose key to
    /// verify it with. Nothing else may be trusted before <see cref="Validate"/> passes.
    /// </summary>
    public static bool TryReadIssuer(string token, out int gatewayId)
    {
        gatewayId = 0;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;
        try
        {
            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty(GatewayTokenClaims.Issuer, out var iss)
                && iss.ValueKind == JsonValueKind.String
                && int.TryParse(iss.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out gatewayId)
                && gatewayId > 0;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the signature and every claim. Returns the token on success, or the
    /// reason it was refused - worded for the person reading the gateway's log, since
    /// the commonest real cause is a server clock that has drifted.
    /// </summary>
    public static (ValidatedGatewayToken? Token, string? Error) Validate(
        string token, byte[] publicKey, int gatewayId, GatewayTokenRequest request)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return Refuse("The token is not in the expected format.");

        JsonElement header, payload;
        byte[] signature;
        try
        {
            header = ParseJson(parts[0]);
            payload = ParseJson(parts[1]);
            signature = Base64Url.DecodeFromChars(parts[2]);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return Refuse("The token is not in the expected format.");
        }
        if (header.ValueKind != JsonValueKind.Object || payload.ValueKind != JsonValueKind.Object)
        {
            return Refuse("The token is not in the expected format.");
        }

        // One algorithm, named exactly. "none", HS256 against the public key and
        // every other confusion attack ends here.
        if (!header.TryGetProperty("alg", out var alg) || alg.ValueKind != JsonValueKind.String
            || !string.Equals(alg.GetString(), GatewayTokenClaims.Algorithm, StringComparison.Ordinal))
        {
            return Refuse("The token must be signed with " + GatewayTokenClaims.Algorithm + ".");
        }

        if (!VerifySignature(parts[0] + "." + parts[1], signature, publicKey))
        {
            return Refuse("The token's signature does not match this gateway's registered key.");
        }

        // From here on the claims are the gateway's own words.
        if (ReadString(payload, GatewayTokenClaims.Issuer) != gatewayId.ToString(CultureInfo.InvariantCulture))
        {
            return Refuse("The token was issued for a different gateway.");
        }
        if (ReadString(payload, GatewayTokenClaims.Audience) is not { } aud
            || !string.Equals(aud, request.Audience, StringComparison.Ordinal))
        {
            return Refuse("The token was made for a different AL Workbench address. Register the gateway again with the address shown on the Gateways page.");
        }

        if (ReadLong(payload, GatewayTokenClaims.IssuedAt) is not { } iat
            || ReadLong(payload, GatewayTokenClaims.Expires) is not { } exp)
        {
            return Refuse("The token has no issue or expiry time.");
        }
        var lifetime = exp - iat;
        if (lifetime <= 0 || lifetime > (long)GatewayProtocol.MaxTokenLifetime.TotalSeconds)
        {
            return Refuse($"The token may live at most {(long)GatewayProtocol.MaxTokenLifetime.TotalSeconds} seconds.");
        }
        var now = request.Now.ToUnixTimeSeconds();
        var skew = (long)GatewayProtocol.ClockSkew.TotalSeconds;
        if (iat > now + skew)
        {
            return Refuse("The token was issued in the future. Check the clock on the gateway's server.");
        }
        if (exp < now - skew)
        {
            return Refuse("The token has expired. Check the clock on the gateway's server.");
        }

        if (ReadString(payload, GatewayTokenClaims.TokenId) is not { Length: > 0 and <= MaxTokenIdLength } jti)
        {
            return Refuse("The token has no usable id.");
        }

        if (!string.Equals(ReadString(payload, GatewayTokenClaims.Method), request.Method, StringComparison.Ordinal)
            || !string.Equals(ReadString(payload, GatewayTokenClaims.Path), request.Path, StringComparison.Ordinal))
        {
            return Refuse("The token was made for a different request.");
        }

        // A body must be vouched for; a request without one may omit the claim, and
        // if it carries one anyway it must be the hash of nothing.
        var bh = ReadString(payload, GatewayTokenClaims.BodyHash);
        if (bh is null ? request.HasBody : !string.Equals(bh, request.BodyHash, StringComparison.Ordinal))
        {
            return Refuse("The token does not match the request body.");
        }

        return (new ValidatedGatewayToken(jti, DateTimeOffset.FromUnixTimeSeconds(exp)), null);
    }

    private static bool VerifySignature(string signingInput, byte[] signature, byte[] publicKey)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(
                Encoding.ASCII.GetBytes(signingInput),
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static JsonElement ParseJson(string base64Url)
    {
        using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(base64Url));
        return doc.RootElement.Clone();
    }

    private static string? ReadString(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadLong(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static (ValidatedGatewayToken?, string?) Refuse(string reason) => (null, reason);
}
