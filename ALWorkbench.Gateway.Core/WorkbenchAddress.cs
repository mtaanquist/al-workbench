namespace ALWorkbench.Gateway.Core;

/// <summary>
/// The AL Workbench address customer IT types into <c>register --url</c>. It must be
/// the workbench's public address exactly as AL Workbench knows itself
/// (<c>PUBLIC_BASE_URL</c>), because a request token's audience is that address
/// followed by <c>/gateway</c>; a workbench served under a path (<c>https://host/al</c>)
/// keeps the path.
/// </summary>
public static class WorkbenchAddress
{
    /// <summary>
    /// The normalised address (no trailing slash, no query or fragment), or null with
    /// <paramref name="error"/> saying what is wrong. Plain http is allowed only for
    /// this computer itself, for testing: anything else goes over the network and must
    /// be https.
    /// </summary>
    public static string? TryNormalise(string? raw, out string error)
    {
        error = string.Empty;
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "Give the AL Workbench address with --url, for example --url https://workbench.cronus.example.";
            return null;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host))
        {
            error = $"'{trimmed}' is not a web address. Use the address you open AL Workbench at, for example https://workbench.cronus.example.";
            return null;
        }
        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            error = "The AL Workbench address must start with https://. The gateway does not talk to AL Workbench over an unencrypted connection.";
            return null;
        }
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            error = "Use the AL Workbench address on its own, without anything after a ? or #.";
            return null;
        }
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>The audience every request token carries for this workbench.</summary>
    public static string AudienceFor(string workbenchUrl) =>
        ALWorkbench.Gateway.Contracts.GatewayProtocol.AudienceFor(workbenchUrl);

    /// <summary>
    /// The request path for a protocol route (<c>/gateway/heartbeat</c>) on a workbench
    /// that may be served under a path, as the server reads it: unescaped, so
    /// <c>/al%20work</c> is <c>/al work</c>. This is the path the token's <c>htu</c>
    /// must name; <see cref="RequestUriFor"/> is the address the request goes to.
    /// </summary>
    public static string PathFor(string workbenchUrl, string route) =>
        Uri.UnescapeDataString(new Uri(workbenchUrl).AbsolutePath).TrimEnd('/') + route;

    /// <summary>The address a protocol route is sent to, escaped as on the wire.</summary>
    public static Uri RequestUriFor(string workbenchUrl, string route)
    {
        var baseUri = new Uri(workbenchUrl);
        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + baseUri.AbsolutePath.TrimEnd('/') + route);
    }
}
