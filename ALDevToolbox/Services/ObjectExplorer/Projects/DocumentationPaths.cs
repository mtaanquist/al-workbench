namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Which repository paths cannot change what a build compiles: documentation and
/// repository housekeeping. A push that touches nothing else is not built on push.
/// The list is deliberately short and errs towards building: an unknown file type
/// may be a resource the compiler packages, a logo <c>app.json</c> points at, or a
/// translation, so anything not named here counts. See
/// <c>.design/github-integration-phase2.md</c>, "Building on push".
/// </summary>
public static class DocumentationPaths
{
    private static readonly string[] Extensions = [".md", ".markdown"];

    private static readonly HashSet<string> FileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gitignore", ".gitattributes", ".editorconfig", "CODEOWNERS", "LICENSE", "LICENSE.txt",
    };

    /// <summary>The folder at the repository root that holds only GitHub's own settings and workflows.</summary>
    private const string GitHubFolder = ".github/";

    /// <summary>True when <paramref name="path"/> (repository-relative, forward slashes) is documentation or housekeeping.</summary>
    public static bool IsDocumentation(string path)
    {
        if (path.StartsWith(GitHubFolder, StringComparison.OrdinalIgnoreCase)) return true;
        var name = path[(path.LastIndexOf('/') + 1)..];
        return FileNames.Contains(name)
               || Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when <paramref name="paths"/> is not empty and every one is documentation.</summary>
    public static bool AreAllDocumentation(IReadOnlyCollection<string> paths) =>
        paths.Count > 0 && paths.All(IsDocumentation);
}
