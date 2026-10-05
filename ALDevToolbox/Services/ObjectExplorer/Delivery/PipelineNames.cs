using ALDevToolbox.Services.ObjectExplorer.Projects;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// The names build and deployment pipelines are given from what they are set up with,
/// so every solution's pipelines read the same way. A name holds only what tells two
/// pipelines in one solution apart: settings that can be switched on and off (the
/// preview check, version numbering, the schedule, approval) stay out, so changing one
/// never renames anything. A person types a name only when the generated one is
/// already taken. See <c>.design/artifacts.md</c>, "Pipeline names".
/// </summary>
/// <remarks>
/// Migration <c>GeneratePipelineNames</c> renames the pipelines that existed before
/// this with the same rules written in SQL; keep the two in step.
/// </remarks>
public static class PipelineNames
{
    /// <summary>The longest name a pipeline can carry (both tables' <c>name</c> column).</summary>
    public const int MaxLength = 200;

    /// <summary>What a build pipeline with no branch set is called: it builds each repository's default branch.</summary>
    public const string DefaultBranch = "Default branch";

    /// <summary>
    /// <c>{branch}</c>, plus the extensions in brackets when the pipeline builds only
    /// some of them: the extension's name when it is one, otherwise how many.
    /// <paramref name="selectedAppIds"/> null or empty means every extension.
    /// <paramref name="extensionNames"/> maps a normalised app id to its name; an id
    /// it doesn't know is still counted.
    /// </summary>
    public static string ForBuildPipeline(
        string? branch,
        IReadOnlyCollection<string>? selectedAppIds,
        IReadOnlyDictionary<string, string> extensionNames)
    {
        var name = string.IsNullOrWhiteSpace(branch) ? DefaultBranch : branch.Trim();
        if (selectedAppIds is { Count: > 0 })
        {
            var extensions = selectedAppIds.Count == 1
                && extensionNames.TryGetValue(ProjectBuildService.NormalizeAppId(selectedAppIds.First()), out var single)
                && !string.IsNullOrWhiteSpace(single)
                    ? single.Trim()
                    : selectedAppIds.Count == 1 ? "1 extension" : $"{selectedAppIds.Count} extensions";
            name = $"{name} ({extensions})";
        }
        return Fit(name);
    }

    /// <summary><c>{build pipeline} to {environment}</c>.</summary>
    public static string ForDeploymentFromBuild(string buildPipelineName, string environmentName) =>
        SourceTo(buildPipelineName.Trim(), environmentName);

    /// <summary><c>{repository} releases to {environment}</c>, for a pipeline that installs a repository's GitHub releases.</summary>
    public static string ForDeploymentFromReleases(string repositoryName, string environmentName) =>
        SourceTo($"{repositoryName.Trim()} releases", environmentName);

    /// <summary>
    /// Shortens the source rather than the environment when the whole is too long, so
    /// two deployment pipelines from one long-named build pipeline still differ.
    /// </summary>
    private static string SourceTo(string source, string environmentName)
    {
        var to = $" to {environmentName.Trim()}";
        if (source.Length + to.Length > MaxLength && to.Length < MaxLength)
        {
            source = source[..(MaxLength - to.Length)].TrimEnd();
        }
        return Fit(source + to);
    }

    private static string Fit(string name) =>
        name.Length > MaxLength ? name[..MaxLength].TrimEnd() : name;
}
