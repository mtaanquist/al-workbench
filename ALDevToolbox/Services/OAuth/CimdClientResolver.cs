using System.Net;
using System.Text.Json;
using ALDevToolbox.Endpoints;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using Microsoft.AspNetCore;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Resolves a Client ID Metadata Document (CIMD) into a registered OAuth
/// application on the fly, so hosted MCP surfaces (Claude, ChatGPT, …) can
/// use an HTTPS URL as their <c>client_id</c> instead of pre-registering
/// via DCR.
///
/// <para>
/// Two client-authentication shapes are accepted:
/// <list type="bullet">
///   <item><c>token_endpoint_auth_method=none</c> — public PKCE client.
///   Claude's hosted surfaces use this with
///   <c>https://claude.ai/oauth/mcp-oauth-client-metadata</c>.</item>
///   <item><c>token_endpoint_auth_method=private_key_jwt</c> — confidential
///   client that signs a JWT assertion at the token endpoint. ChatGPT's
///   custom connectors use this with a per-connector
///   <c>https://chatgpt.com/oauth/&lt;id&gt;/client.json</c> URL and a
///   <c>jwks_uri</c> we fetch to seed OpenIddict's signature validator.</item>
/// </list>
/// </para>
///
/// <para>
/// The MCP client picks CIMD over DCR when our discovery metadata advertises
/// <c>client_id_metadata_document_supported: true</c> and the auth method
/// the client wants to use. Both <c>"none"</c> and <c>"private_key_jwt"</c>
/// are advertised in <c>token_endpoint_auth_methods_supported</c> from
/// <c>Program.cs</c>. Without this resolver in place OpenIddict's standard
/// validator fails the authorisation request with
/// <c>ID2052: The specified 'client_id' is invalid</c> because no row in
/// <c>oauth_applications</c> matches the URL — see
/// <see href="https://documentation.openiddict.com/errors/ID2052"/>.
/// </para>
///
/// <para>
/// The resolver runs at <see cref="OpenIddictServerHandlerType.Custom"/>
/// before OpenIddict's <c>ValidateClientId</c> handler. On every authorize
/// request whose <c>client_id</c> is an HTTPS URL it fetches the metadata
/// document, validates the basic shape per the
/// <see href="https://modelcontextprotocol.io/specification/2025-11-25/basic/authorization#client-id-metadata-documents">MCP CIMD spec</see>,
/// and either creates the application via
/// <see cref="IOpenIddictApplicationManager"/> or updates the existing row.
/// Re-fetching is what lets us survive the issuer rotating its JWKS or
/// amending its <c>redirect_uris</c> without an admin intervention, and a
/// stale JWKS would otherwise break every refresh attempt until someone
/// deleted the application row by hand.
/// </para>
///
/// <para>
/// The request is anonymous, so each fetch is also a way for anyone to make
/// this server dial a URL and write a row (#1336). Two things bound that: a
/// row refreshed within <see cref="RefreshInterval"/> is used as it is, with
/// no fetch (which also covers the consent form posting the same request
/// back), and every fetch spends a permit from <see cref="CimdFetchLimiter"/>,
/// the same per-IP budget <c>/oauth/register</c> has.
/// </para>
/// </summary>
public sealed class CimdClientResolver : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    /// <summary>
    /// Application-property key (and its value for CIMD rows) recording how a
    /// client was registered. This is a security boundary: only rows we stamped
    /// from a CIMD document may be overwritten from a freshly-fetched (and
    /// attacker-influenceable) metadata URL, so the write here and the
    /// overwrite guards in this class and <see cref="OAuthClientAdminService"/>
    /// must agree on the exact key/value — hence the shared constants.
    /// </summary>
    public const string RegistrationSourceProperty = "registration_source";
    public const string CimdRegistrationSource = "cimd";

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(5);
    private const int MaxBodyBytes = 64 * 1024;

    /// <summary>Application property stamped on every create or refresh from a document.</summary>
    public const string RefreshedAtProperty = "refreshed_at";

    /// <summary>How long a document-registered row is used without fetching its document again.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(10);

    private readonly IOpenIddictApplicationManager _applications;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CimdFetchLimiter _fetchLimiter;
    private readonly TimeProvider _clock;
    private readonly ILogger<CimdClientResolver> _logger;

    public CimdClientResolver(
        IOpenIddictApplicationManager applications,
        IHttpClientFactory httpClientFactory,
        CimdFetchLimiter fetchLimiter,
        TimeProvider clock,
        ILogger<CimdClientResolver> logger)
    {
        _applications = applications;
        _httpClientFactory = httpClientFactory;
        _fetchLimiter = fetchLimiter;
        _clock = clock;
        _logger = logger;
    }

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var clientId = context.ClientId;
        if (string.IsNullOrEmpty(clientId)) return;
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url)) return;
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return;

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _applications.FindByClientIdAsync(clientId, context.CancellationToken);

        // Only ever overwrite a row that we ourselves created from a CIMD
        // document. A DCR- or admin-registered client that happens to use a
        // URL-shaped client_id must NOT be silently rewritten from an
        // attacker-controllable metadata document — that would let whoever can
        // serve (or transiently spoof) that URL replace its redirect_uris,
        // JWKS, and client type. Checked before the fetch, so such a request
        // never makes us dial the URL at all.
        if (existing is not null)
        {
            var properties = await _applications.GetPropertiesAsync(existing, context.CancellationToken);
            var isCimdRow = properties.TryGetValue(RegistrationSourceProperty, out var source)
                            && source.ValueKind == JsonValueKind.String
                            && string.Equals(source.GetString(), CimdRegistrationSource, StringComparison.Ordinal);
            if (!isCimdRow)
            {
                _logger.LogWarning(
                    "Refusing to overwrite client {ClientId} from a CIMD document — it was registered through another channel.",
                    clientId);
                context.Reject(
                    error: OpenIddictConstants.Errors.InvalidClient,
                    description: "A client with this id is already registered through another channel.");
                return;
            }
            if (properties.TryGetValue(RefreshedAtProperty, out var refreshed)
                && refreshed.ValueKind == JsonValueKind.String
                && refreshed.TryGetDateTime(out var refreshedAt)
                && now - refreshedAt.ToUniversalTime() < RefreshInterval)
            {
                // Recently fetched: use the row as it stands.
                return;
            }
        }

        var ip = context.Transaction.GetHttpRequest()?.HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!_fetchLimiter.TryAcquire(ip))
        {
            _logger.LogWarning("CIMD fetch for {ClientId} refused: too many from {Ip}.", clientId, ip);
            context.Reject(
                error: OpenIddictConstants.Errors.TemporarilyUnavailable,
                description: "Too many client metadata requests. Try again in a few minutes.");
            return;
        }

        CimdMetadata? metadata;
        try
        {
            metadata = await FetchMetadataAsync(url, context.CancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CIMD fetch failed for {Url}", url);
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: $"Failed to fetch client metadata document from '{url}'.");
            return;
        }
        if (metadata is null)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: $"Client metadata document at '{url}' was empty or not valid JSON.");
            return;
        }

        // The document MUST self-identify with the same URL — otherwise
        // any document could claim any client_id.
        if (!string.Equals(metadata.ClientId, clientId, StringComparison.Ordinal))
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document's client_id does not match the requested URL.");
            return;
        }
        if (metadata.RedirectUris is null || metadata.RedirectUris.Length == 0)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document must declare at least one redirect_uri.");
            return;
        }
        // Default per RFC 7591 is client_secret_basic, but CIMD documents
        // SHOULD set this explicitly; we treat a missing value as "none"
        // for compatibility with documents that follow the spirit of CIMD
        // but skip the field.
        var authMethod = string.IsNullOrWhiteSpace(metadata.TokenEndpointAuthMethod)
            ? "none"
            : metadata.TokenEndpointAuthMethod!.Trim();

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
            DisplayName = string.IsNullOrWhiteSpace(metadata.ClientName) ? clientId : metadata.ClientName!.Trim(),
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Authorization,
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.Endpoints.Revocation,
                OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                OpenIddictConstants.Permissions.ResponseTypes.Code,
                "scp:mcp",
                "scp:" + OpenIddictConstants.Scopes.OfflineAccess,
            },
            Requirements =
            {
                OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
            },
        };

        switch (authMethod)
        {
            case "none":
                descriptor.ClientType = OpenIddictConstants.ClientTypes.Public;
                break;

            case "private_key_jwt":
                if (string.IsNullOrWhiteSpace(metadata.JwksUri))
                {
                    context.Reject(
                        error: OpenIddictConstants.Errors.InvalidClient,
                        description: "Client metadata document declares private_key_jwt but is missing jwks_uri.");
                    return;
                }
                if (!Uri.TryCreate(metadata.JwksUri, UriKind.Absolute, out var jwksUrl) ||
                    !string.Equals(jwksUrl.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal))
                {
                    context.Reject(
                        error: OpenIddictConstants.Errors.InvalidClient,
                        description: "Client metadata document's jwks_uri must be an HTTPS URL.");
                    return;
                }
                JsonWebKeySet keys;
                try
                {
                    keys = await FetchJwksAsync(jwksUrl, context.CancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "CIMD JWKS fetch failed for {ClientId} from {JwksUri}", clientId, jwksUrl);
                    context.Reject(
                        error: OpenIddictConstants.Errors.InvalidClient,
                        description: $"Failed to fetch JWKS document from '{jwksUrl}'.");
                    return;
                }
                if (keys.Keys.Count == 0)
                {
                    context.Reject(
                        error: OpenIddictConstants.Errors.InvalidClient,
                        description: $"JWKS document at '{jwksUrl}' contained no keys.");
                    return;
                }
                descriptor.ClientType = OpenIddictConstants.ClientTypes.Confidential;
                descriptor.JsonWebKeySet = keys;
                descriptor.Permissions.Add("cam:" + OpenIddictConstants.ClientAuthenticationMethods.PrivateKeyJwt);
                if (!string.IsNullOrWhiteSpace(metadata.TokenEndpointAuthSigningAlg))
                {
                    descriptor.Properties["token_endpoint_auth_signing_alg"] =
                        JsonSerializer.SerializeToElement(metadata.TokenEndpointAuthSigningAlg);
                }
                descriptor.Properties["jwks_uri"] = JsonSerializer.SerializeToElement(metadata.JwksUri);
                break;

            default:
                context.Reject(
                    error: OpenIddictConstants.Errors.InvalidClient,
                    description: $"Unsupported token_endpoint_auth_method '{authMethod}'. " +
                                 "Only 'none' (public PKCE) and 'private_key_jwt' are accepted.");
                return;
        }

        // Same rule as /oauth/register: an entry the policy refuses is
        // dropped (logged), and a document left with none is rejected below.
        foreach (var uri in metadata.RedirectUris)
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && OAuthRedirectUriPolicy.IsAllowed(parsed))
            {
                descriptor.RedirectUris.Add(parsed);
            }
            else
            {
                _logger.LogWarning(
                    "Ignoring redirect_uri {RedirectUri} from the client metadata document for {ClientId}: not an allowed redirect URI.",
                    uri, clientId);
            }
        }
        if (descriptor.RedirectUris.Count == 0)
        {
            context.Reject(
                error: OpenIddictConstants.Errors.InvalidClient,
                description: "Client metadata document's redirect_uris are all invalid.");
            return;
        }
        descriptor.Properties[RegistrationSourceProperty] = JsonSerializer.SerializeToElement(CimdRegistrationSource);
        descriptor.Properties["registered_at"] = JsonSerializer.SerializeToElement(now);
        descriptor.Properties[RefreshedAtProperty] = JsonSerializer.SerializeToElement(now);
        descriptor.Properties["token_endpoint_auth_method"] = JsonSerializer.SerializeToElement(authMethod);
        if (!string.IsNullOrWhiteSpace(metadata.ClientUri))
        {
            descriptor.Properties["client_uri"] = JsonSerializer.SerializeToElement(metadata.ClientUri);
        }

        try
        {
            if (existing is null)
            {
                await _applications.CreateAsync(descriptor, context.CancellationToken);
                _logger.LogInformation(
                    "Resolved CIMD client {ClientId} ({DisplayName}) with {RedirectCount} redirect_uri(s), auth_method={AuthMethod}.",
                    clientId, descriptor.DisplayName, descriptor.RedirectUris.Count, authMethod);
            }
            else
            {
                await _applications.UpdateAsync(existing, descriptor, context.CancellationToken);
                _logger.LogInformation(
                    "Refreshed CIMD client {ClientId} ({DisplayName}) with {RedirectCount} redirect_uri(s), auth_method={AuthMethod}.",
                    clientId, descriptor.DisplayName, descriptor.RedirectUris.Count, authMethod);
            }
        }
        catch (Exception ex)
        {
            // Race: a parallel request mutated the same row first. The
            // standard validator will pick up whichever row landed in the
            // DB, so log and continue rather than fail the request.
            _logger.LogInformation(ex, "CIMD client {ClientId} changed concurrently — using whichever row landed.", clientId);
        }
    }

    private Task<CimdMetadata?> FetchMetadataAsync(Uri url, CancellationToken cancellationToken) =>
        FetchJsonAsync<CimdMetadata>(url, cancellationToken);

    private async Task<JsonWebKeySet> FetchJwksAsync(Uri url, CancellationToken cancellationToken)
    {
        var body = await FetchBodyAsync(url, cancellationToken);
        // JsonWebKeySet's constructor parses the JWKS shape and skips keys
        // it doesn't understand, so the resolver doesn't have to know
        // anything about RSA vs EC key encodings.
        return new JsonWebKeySet(body);
    }

    private async Task<T?> FetchJsonAsync<T>(Uri url, CancellationToken cancellationToken) where T : class
    {
        var body = await FetchBodyAsync(url, cancellationToken);
        return JsonSerializer.Deserialize<T>(
            body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private async Task<string> FetchBodyAsync(Uri url, CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(nameof(CimdClientResolver));
        client.Timeout = FetchTimeout;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox-MCP/1.0");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"Document at '{url}' returned HTTP {(int)response.StatusCode}.");
        }
        // Cap the read so a hostile document can't exhaust memory.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxBodyBytes + 1];
        int total = 0;
        while (total <= MaxBodyBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        if (total > MaxBodyBytes)
        {
            throw new InvalidOperationException($"Document at '{url}' exceeded {MaxBodyBytes} bytes.");
        }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, total);
    }

    /// <summary>
    /// Subset of the RFC 7591 client metadata shape that CIMD reuses. We
    /// only read the fields we actually honour; unknown fields are dropped.
    /// </summary>
    private sealed record CimdMetadata
    {
        [System.Text.Json.Serialization.JsonPropertyName("client_id")]
        public string? ClientId { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("client_uri")]
        public string? ClientUri { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
        public string[]? RedirectUris { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_signing_alg")]
        public string? TokenEndpointAuthSigningAlg { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("jwks_uri")]
        public string? JwksUri { get; init; }
    }
}
