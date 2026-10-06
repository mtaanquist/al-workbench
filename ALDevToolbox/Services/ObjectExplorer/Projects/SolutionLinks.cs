using System.Globalization;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// The addresses of a solution's page and its environments' pages. The readable
/// form - <c>/solutions/cronus</c>, <c>/environments/cronus/Production</c> - when
/// the solution's slug is known, else the numeric one, which still resolves and
/// forwards to the readable form. One home so a link cannot disagree with the
/// route that serves it.
/// </summary>
public static class SolutionLinks
{
    /// <summary>
    /// A solution's page, optionally on one of its tabs (<c>general</c>,
    /// <c>repositories</c>, <c>bc</c>, <c>pipelines</c>, <c>symbols</c>, <c>access</c>).
    /// The Customer tab is the page's own address, so it takes no tab.
    /// </summary>
    public static string Solution(string? slug, int id, string? tab = null)
    {
        var root = string.IsNullOrEmpty(slug)
            ? $"/solutions/{id.ToString(CultureInfo.InvariantCulture)}"
            : $"/solutions/{slug}";
        return string.IsNullOrEmpty(tab) ? root : $"{root}/{tab}";
    }

    /// <summary>
    /// An environment's page, optionally on one of its tabs (<c>apps</c>,
    /// <c>operations</c>...). The environment name is escaped: Business Central only
    /// hands out URL-safe names today, but the address should not depend on that.
    /// </summary>
    public static string Environment(string? solutionSlug, string? environmentName, int environmentId, string? tab = null)
    {
        var root = string.IsNullOrEmpty(solutionSlug) || string.IsNullOrEmpty(environmentName)
            ? $"/environments/{environmentId.ToString(CultureInfo.InvariantCulture)}"
            : $"/environments/{solutionSlug}/{Uri.EscapeDataString(environmentName)}";
        return string.IsNullOrEmpty(tab) ? root : $"{root}/{tab}";
    }
}
