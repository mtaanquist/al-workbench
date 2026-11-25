using System.Text.RegularExpressions;

namespace ALDevToolbox.Services.Migration;

/// <summary>
/// An Azure DevOps project, named by its organisation and project name, that
/// Migrate Repositories reads repositories from (issue #1699).
///
/// <para>Parsed from whatever an Admin pastes: the project's address in the
/// browser (<c>https://dev.azure.com/cronus/Sales</c>, with or without a page
/// after it), the older <c>https://cronus.visualstudio.com/Sales</c> form, or
/// plain <c>cronus/Sales</c>. Every call it leads to goes to
/// <c>dev.azure.com</c>, whatever host was pasted, so the address can only ever
/// choose a path there, never a server.</para>
/// </summary>
public sealed partial record AzureDevOpsProject(string Organization, string Project)
{
    /// <summary>The host every Azure DevOps API call and clone goes to.</summary>
    public const string Host = "dev.azure.com";

    // Azure DevOps organisation names: letters, digits and hyphens, starting with
    // a letter or digit, at most 50 characters.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,49}$")]
    private static partial Regex OrganizationRegex();

    /// <summary>Longest project name Azure DevOps allows.</summary>
    private const int MaxProjectLength = 64;

    /// <summary>The project's address, for a link and for the history.</summary>
    public string WebUrl => $"https://{Host}/{Uri.EscapeDataString(Organization)}/{Uri.EscapeDataString(Project)}";

    /// <summary>"cronus/Sales", the way the page names the project.</summary>
    public string DisplayName => $"{Organization}/{Project}";

    /// <summary>
    /// Reads <paramref name="input"/> as a project address. False, with
    /// <paramref name="project"/> null, when it does not name one.
    /// </summary>
    public static bool TryParse(string? input, out AzureDevOpsProject? project)
    {
        project = null;
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return false;

        string? organization;
        IReadOnlyList<string> rest;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
            var segments = Segments(uri.AbsolutePath);
            var host = uri.Host.ToLowerInvariant();
            if (host == Host)
            {
                if (segments.Count < 2) return false;
                organization = segments[0];
                rest = segments.Skip(1).ToList();
            }
            else if (host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
            {
                organization = host[..^".visualstudio.com".Length];
                rest = segments;
                // The oldest addresses put the collection name in front of the project.
                if (rest.Count > 0 && rest[0].Equals("DefaultCollection", StringComparison.OrdinalIgnoreCase))
                {
                    rest = rest.Skip(1).ToList();
                }
            }
            else
            {
                return false;
            }
        }
        else
        {
            var segments = Segments(text);
            if (segments.Count != 2) return false;
            organization = segments[0];
            rest = segments.Skip(1).ToList();
        }

        if (rest.Count == 0) return false;
        var projectName = rest[0];
        // "_git" or "_apis" right after the organisation means no project was named.
        if (projectName.StartsWith('_')) return false;
        // "." and ".." would be collapsed out of the request path.
        if (projectName is "." or "..") return false;
        if (!OrganizationRegex().IsMatch(organization)) return false;
        if (projectName.Length > MaxProjectLength || projectName.Any(c => char.IsControl(c) || c is '/' or '\\')) return false;

        project = new AzureDevOpsProject(organization, projectName);
        return true;
    }

    private static List<string> Segments(string path) => path
        .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Uri.UnescapeDataString)
        .Where(s => s.Length > 0)
        .ToList();
}
