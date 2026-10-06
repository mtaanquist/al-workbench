namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// The links a repository's stored clone URL turns into: its page on the host,
/// and VS Code's clone handler. Shared by the New Workspace result and a
/// solution's Repositories tab (issue #1076).
/// </summary>
public static class RepositoryLinks
{
    /// <summary>
    /// Whether <paramref name="url"/> is an https URL on <paramref name="provider"/>'s
    /// host. The solution service's validation rule, so a URL it accepts is one
    /// <see cref="WebUrl"/> can turn into a page link.
    /// </summary>
    public static bool IsValidUrl(RepositoryProvider provider, string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        var host = uri.Host.ToLowerInvariant();
        return provider switch
        {
            RepositoryProvider.AzureDevOps =>
                host == "dev.azure.com" || host.EndsWith(".visualstudio.com", StringComparison.Ordinal),
            RepositoryProvider.GitHub =>
                host == "github.com" || host == "www.github.com",
            _ => false,
        };
    }

    /// <summary>
    /// The repository's page on its host, or null when the URL is not one
    /// <see cref="IsValidUrl"/> accepts. A stored URL is a clone URL: Azure
    /// DevOps puts the organisation's user name in front of the host
    /// (<c>https://contoso@dev.azure.com/...</c>) and GitHub ends it in
    /// <c>.git</c>. Both are dropped so the link lands on the repository page
    /// rather than a sign-in prompt or a redirect; the query and fragment go too.
    /// </summary>
    public static string? WebUrl(RepositoryProvider provider, string? cloneUrl)
    {
        if (!IsValidUrl(provider, cloneUrl)) return null;
        var uri = new Uri(cloneUrl!.Trim());
        var path = uri.AbsolutePath.TrimEnd('/');
        // Only GitHub's clone URLs carry the suffix; an Azure DevOps repository
        // may genuinely be named "something.git".
        if (provider == RepositoryProvider.GitHub && path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^4];
        }
        if (path.Length == 0) return null;
        // Authority is host and port only; the user name is not part of it.
        return $"https://{uri.Authority}{path}";
    }

    /// <summary>
    /// VS Code's documented URL handler for cloning: it opens VS Code, asks
    /// where to put the repository, and clones it there. The clone URL is a
    /// query-string value, so it is escaped rather than pasted in - its own
    /// <c>://</c> and slashes would otherwise be read as part of the handler
    /// path.
    /// </summary>
    public static string VsCodeCloneUrl(string cloneUrl) =>
        $"vscode://vscode.git/clone?url={Uri.EscapeDataString(cloneUrl.Trim())}";
}
