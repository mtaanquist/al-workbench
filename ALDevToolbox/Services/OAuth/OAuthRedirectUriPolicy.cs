namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Which <c>redirect_uri</c> values an OAuth client may register and use.
/// Shared by the anonymous registration endpoint, the CIMD resolver and the
/// authorize-time check, so a client that slipped in before the rule existed
/// still cannot use a refused URI.
///
/// <para>
/// Allowed: <c>https</c> with a host; <c>http</c> on loopback only (native
/// clients such as Claude Code listen on <c>http://127.0.0.1:&lt;port&gt;</c>,
/// RFC 8252 §7.3); and private-use schemes in reverse-domain form such as
/// <c>com.example.app:/callback</c> (RFC 8252 §7.1). Everything else is
/// refused - in particular schemes the browser interprets itself
/// (<c>javascript:</c>, <c>data:</c>, <c>vbscript:</c>, <c>file:</c>,
/// <c>blob:</c>), because the authorization response lands on that URI from
/// this app's origin.
/// </para>
/// </summary>
public static class OAuthRedirectUriPolicy
{
    /// <summary>True when <paramref name="value"/> parses as an absolute URI the policy accepts.</summary>
    public static bool IsAllowed(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsAllowed(uri);

    /// <summary>True when the absolute <paramref name="uri"/> is one the policy accepts.</summary>
    public static bool IsAllowed(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri) return false;
        // RFC 6749 §3.1.2: the redirection endpoint MUST NOT include a fragment.
        if (!string.IsNullOrEmpty(uri.Fragment)) return false;

        var scheme = uri.Scheme;
        if (string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrEmpty(uri.Host);
        }
        if (string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            return IsLoopback(uri);
        }
        return IsPrivateUseScheme(scheme);
    }

    /// <summary>
    /// Loopback host check for <c>http</c> redirects (RFC 8252 §7.3).
    /// </summary>
    public static bool IsLoopback(Uri uri) =>
        !string.IsNullOrEmpty(uri.Host)
        && (uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// RFC 8252 §7.1 private-use scheme: a reverse domain name, so it always
    /// contains a dot (<c>com.example.app</c>). No scheme a browser handles
    /// itself has one.
    /// </summary>
    private static bool IsPrivateUseScheme(string scheme)
    {
        var parts = scheme.Split('.');
        return parts.Length >= 2 && parts.All(p => p.Length > 0);
    }
}
