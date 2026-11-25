using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Per-IP cap on the metadata fetches <see cref="CimdClientResolver"/> makes
/// for an anonymous <c>/oauth/authorize</c> request whose <c>client_id</c> is
/// a URL. Each fetch can create or rewrite an <c>oauth_applications</c> row,
/// which is what <c>/oauth/register</c>'s rate-limit policy guards against
/// (#378); this path had no such cap (#1336). The numbers match that policy.
/// It is a singleton of its own rather than an ASP.NET rate-limit policy
/// because the resolver runs inside OpenIddict's request handling, ahead of
/// endpoint routing, and only a fetch should spend a permit: a request served
/// from a recently refreshed row costs nothing.
/// </summary>
public sealed class CimdFetchLimiter : IDisposable
{
    public const int PermitLimit = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        ip => RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitLimit,
            Window = Window,
            QueueLimit = 0,
        }));

    /// <summary>True when the caller at <paramref name="ip"/> may make another fetch now.</summary>
    public bool TryAcquire(string? ip)
    {
        using var lease = _limiter.AttemptAcquire(ip ?? "unknown");
        return lease.IsAcquired;
    }

    // Client id -> when the token endpoint last tried to re-read that client's key
    // set. Only CIMD rows with an https jwks_uri ever get an entry, so it is bounded
    // by the registered clients, not by what anonymous callers send.
    private readonly ConcurrentDictionary<string, DateTime> _keySetAttempts = new(StringComparer.Ordinal);

    /// <summary>
    /// Claims the one key-set re-read a client gets per <paramref name="interval"/>,
    /// whether or not it then succeeds. A failed or empty read does not stamp the row,
    /// so without this a client whose <c>jwks_uri</c> is down would cost an outbound
    /// fetch on every token request naming it. In memory because the app runs as one
    /// container; a restart only allows one extra attempt.
    /// </summary>
    public bool TryClaimKeySetRefresh(string clientId, DateTime now, TimeSpan interval)
    {
        while (true)
        {
            if (_keySetAttempts.TryGetValue(clientId, out var last))
            {
                if (now - last < interval) return false;
                if (_keySetAttempts.TryUpdate(clientId, now, last)) return true;
            }
            else if (_keySetAttempts.TryAdd(clientId, now))
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Gives back a claim from <see cref="TryClaimKeySetRefresh"/> that made no fetch,
    /// so a caller over its own budget cannot hold the client's slot for the interval.
    /// </summary>
    public void ReleaseKeySetRefresh(string clientId, DateTime claimedAt) =>
        _keySetAttempts.TryRemove(KeyValuePair.Create(clientId, claimedAt));

    public void Dispose() => _limiter.Dispose();
}
