using System.Text.Json;
using System.Text.RegularExpressions;
using ALDevToolbox.Data;

namespace ALDevToolbox.Services.Templates;

/// <summary>
/// Where a module's files come from when they are copied from a GitHub
/// repository (#1415), as the Editor typed it. <see cref="Address"/> is whatever
/// they pasted; <see cref="ModuleSourceAddress.TryParse"/> turns it into an
/// owner and a repository, and may pick a branch and folder out of a
/// <c>/tree/</c> link when the other two fields are blank.
/// </summary>
public record ModuleSourceInput(string Address, string? Ref, string? Path);

/// <summary>
/// The source columns a module row is saved with: what to copy and which commit
/// the copy came from. Built by <see cref="ModuleSourceService"/> after a
/// successful copy and handed to <see cref="ModuleService"/> on
/// <see cref="ModuleInput.Source"/>.
/// </summary>
public record ModuleSourceState(
    string Repository,
    string? Ref,
    string? Path,
    string CommitSha,
    DateTime FetchedAt,
    IReadOnlyList<ModuleSourceSkippedFile> Skipped,
    IReadOnlyList<ModuleDependencyInput> Dependencies);

/// <summary>A file a copy left out, and why, in words for the module page.</summary>
public record ModuleSourceSkippedFile(string Path, string Reason);

/// <summary>
/// What one copy of a repository produced: the folder tree to store, what was
/// left out, and what the repository's own <c>app.json</c> said.
/// </summary>
public record ModuleSourceSnapshot(
    IReadOnlyList<FolderAuthoring> Folders,
    IReadOnlyList<ModuleSourceSkippedFile> Skipped,
    IReadOnlyList<ModuleDependencyInput> Dependencies,
    int? IdRangeSize,
    int FileCount,
    long TotalBytes);

/// <summary>
/// The limits a copy is held to. The repository is untrusted content that ends
/// up in new customer workspaces, so these are deliberately small: a module is a
/// starting extension, not a product.
/// </summary>
public static class ModuleSourceLimits
{
    /// <summary>The most compressed bytes downloaded for one copy.</summary>
    public const long MaxDownloadBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The most bytes the whole archive may expand to, whether or not they are kept,
    /// so a small download cannot unpack into something enormous.
    /// </summary>
    public const long MaxUnpackedBytes = 200L * 1024 * 1024;

    /// <summary>The most bytes the copied files may add up to.</summary>
    public const long MaxTotalBytes = 5L * 1024 * 1024;

    /// <summary>The most files one copy may hold.</summary>
    public const int MaxFiles = 500;

    /// <summary>The largest single file copied; larger ones are left out and listed.</summary>
    public const int MaxFileBytes = 512 * 1024;
}

/// <summary>
/// Parses what an Editor pastes as a repository address. Only github.com is
/// accepted, so a module can never make the server call an arbitrary host.
/// </summary>
public static partial class ModuleSourceAddress
{
    // GitHub's own rules: an account name is up to 39 letters, digits and
    // single hyphens; a repository name is letters, digits, '.', '-' and '_'.
    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    private static partial Regex OwnerPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    /// <summary>
    /// Reads <paramref name="address"/> as <c>https://github.com/owner/repo</c>
    /// (with or without the scheme, a trailing <c>.git</c> or slash), as
    /// <c>owner/repo</c>, or as a <c>/tree/branch/folder</c> link, whose branch and
    /// folder come back in <paramref name="refFromLink"/> and
    /// <paramref name="pathFromLink"/>. A branch whose name contains a slash cannot
    /// be told apart from a folder in such a link, so it is read as the first
    /// segment only; the Editor types it in the branch field instead.
    /// </summary>
    public static bool TryParse(
        string? address,
        out string fullName,
        out string? refFromLink,
        out string? pathFromLink,
        out string error)
    {
        fullName = string.Empty;
        refFromLink = null;
        pathFromLink = null;
        error = string.Empty;

        var text = address?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Enter the repository's address.";
            return false;
        }

        if (text.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                error = "Enter a GitHub address, for example https://github.com/cronus-dk/document-capture.";
                return false;
            }
            if (!IsGitHubHost(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort)
            {
                error = "Only repositories on github.com can be used.";
                return false;
            }
            text = uri.AbsolutePath;
        }
        else
        {
            var slash = text.IndexOf('/');
            if (slash > 0 && text[..slash].Contains('.'))
            {
                if (!IsGitHubHost(text[..slash]))
                {
                    error = "Only repositories on github.com can be used.";
                    return false;
                }
                text = text[slash..];
            }
        }

        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            error = "Enter the owner and the repository, for example cronus-dk/document-capture.";
            return false;
        }

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1];
        if (!OwnerPattern().IsMatch(owner) || !RepositoryPattern().IsMatch(repo) || repo is "." or "..")
        {
            error = "That doesn't look like a GitHub repository address.";
            return false;
        }

        if (segments.Length > 2)
        {
            if (!string.Equals(segments[2], "tree", StringComparison.Ordinal) || segments.Length < 4)
            {
                error = "Paste the repository's address, or a link to a branch or folder in it.";
                return false;
            }
            refFromLink = Uri.UnescapeDataString(segments[3]);
            if (segments.Length > 4)
            {
                pathFromLink = string.Join('/', segments.Skip(4).Select(Uri.UnescapeDataString));
            }
        }

        fullName = $"{owner}/{repo}";
        return true;
    }

    /// <summary>
    /// Normalises the folder field: slashes trimmed, backslashes refused, no
    /// <c>.</c> or <c>..</c> segments. Null or blank means the repository root.
    /// </summary>
    public static bool TryNormalisePath(string? path, out string? normalised, out string error)
    {
        normalised = null;
        error = string.Empty;
        var text = path?.Trim().Trim('/') ?? string.Empty;
        if (text.Length == 0) return true;
        if (text.Contains('\\'))
        {
            error = "Use '/' between folder names.";
            return false;
        }
        var segments = text.Split('/');
        if (segments.Any(s => s.Length == 0 || s == "." || s == ".." || s.Trim() != s))
        {
            error = "Enter a folder path such as src/DocumentCapture, without '..'.";
            return false;
        }
        normalised = string.Join('/', segments);
        return true;
    }

    private static bool IsGitHubHost(string host) =>
        string.Equals(host, "github.com", StringComparison.OrdinalIgnoreCase)
        || string.Equals(host, "www.github.com", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the JSON source columns on <see cref="Domain.Entities.Module"/> back into
/// their shapes. A column that does not parse reads as empty: it only ever holds
/// what <see cref="ModuleService"/> wrote, and a display list is not worth a crash.
/// </summary>
public static class ModuleSourceColumns
{
    public static IReadOnlyList<ModuleSourceSkippedFile> ReadSkipped(string? json) =>
        Read<List<ModuleSourceSkippedFile>>(json) ?? [];

    public static IReadOnlyList<ModuleDependencyInput> ReadDependencies(string? json) =>
        Read<List<ModuleDependencyInput>>(json) ?? [];

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, PersistenceJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
