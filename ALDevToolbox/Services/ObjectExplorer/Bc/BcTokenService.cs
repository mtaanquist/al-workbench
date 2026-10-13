using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Acquires and caches Business Central S2S (client-credentials) access tokens, one
/// per project. Registered as a <strong>singleton</strong> so the in-memory token
/// cache is shared (like the compiler-provisioning gate). It holds no scoped
/// <c>DbContext</c> or Data Protection state: the scoped
/// <see cref="ProjectConnectionService"/> decrypts the customer's secret and passes
/// <c>(tenantId, clientId, clientSecret)</c> in — this service only does the OAuth
/// round-trip and caches the bearer token. Tokens are never persisted. See
/// <c>.design/saas-delivery.md</c> ("Authentication").
/// </summary>
public sealed class BcTokenService
{
    // A safety margin so we refresh slightly before the real expiry rather than
    // racing a token that lapses mid-request.
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<BcTokenService> _logger;
    private readonly ConcurrentDictionary<int, CachedToken> _cache = new();

    public BcTokenService(IHttpClientFactory httpFactory, ILogger<BcTokenService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private sealed record CachedToken(string AccessToken, DateTime ExpiresAtUtc);

    /// <summary>
    /// Returns a valid bearer token for the project, acquiring a fresh one when the
    /// cache is empty/expired or <paramref name="forceRefresh"/> is set (used after a
    /// 401). Throws <see cref="BcApiException"/> when Entra rejects the credentials.
    /// </summary>
    public async Task<string> GetTokenAsync(
        int projectId, Guid tenantId, string clientId, string clientSecret,
        bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh
            && _cache.TryGetValue(projectId, out var cached)
            && cached.ExpiresAtUtc - ExpiryMargin > DateTime.UtcNow)
        {
            return cached.AccessToken;
        }

        var token = await RequestTokenAsync(tenantId, clientId, clientSecret, ct).ConfigureAwait(false);
        _cache[projectId] = token;
        return token.AccessToken;
    }

    /// <summary>Drops any cached token for a project — call when its credentials change.</summary>
    public void Invalidate(int projectId) => _cache.TryRemove(projectId, out _);

    private async Task<CachedToken> RequestTokenAsync(
        Guid tenantId, string clientId, string clientSecret, CancellationToken ct)
    {
        var url = $"{BcConstants.LoginBaseUrl}/{tenantId}/oauth2/v2.0/token";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = BcConstants.TokenScope,
            }),
        };

        var client = _httpFactory.CreateClient(BcConstants.HttpClientName);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        // HttpClient's own timeout surfaces as a cancellation too; only the caller's
        // token stopping is a real cancel, anything else is BC not answering (#1233).
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new BcApiException(null, "Couldn't reach the Microsoft sign-in endpoint.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // The login endpoint returns a JSON error; surface its short code, never
                // the secret. The most common cause is a wrong/expired client secret.
                _logger.LogWarning("BC token request failed for tenant {Tenant}: {Status}.", tenantId, response.StatusCode);
                throw new BcApiException(response.StatusCode, "The Business Central credentials were rejected.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                throw new BcApiException(response.StatusCode, "The Microsoft sign-in response didn't contain a token.");
            }
            var expiresIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var secs) ? secs : 3600;
            return new CachedToken(accessToken, DateTime.UtcNow.AddSeconds(expiresIn));
        }
    }
}

/// <summary>Helper to set the bearer header on an outgoing BC request.</summary>
internal static class BcHttpExtensions
{
    public static void UseBearer(this HttpRequestMessage request, string accessToken)
        => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
}
