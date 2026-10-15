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

    public void Dispose() => _limiter.Dispose();
}
