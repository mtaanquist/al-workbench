namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Which builds a deployment pipeline with a branch rule accepts. A branch is the one
/// the build pipeline checked out, as recorded on the build; null on either side means
/// the repositories' default branch, which is what a build pipeline with no branch
/// builds. Branch names are compared exactly, the way git does, after "the default
/// branch" is read as the branch it was where that is known (#1129). See
/// <c>.design/saas-delivery.md</c>, "Which branch may reach an environment".
/// </summary>
public static class DeploymentBranchRule
{
    /// <summary>True when a build from <paramref name="buildBranch"/> may go through a pipeline that only allows <paramref name="allowedBranch"/>.</summary>
    public static bool Allows(string? allowedBranch, string? buildBranch) =>
        string.Equals(Normalize(allowedBranch), Normalize(buildBranch), StringComparison.Ordinal);

    /// <summary>
    /// <see cref="Allows(string?, string?)"/>, with "the default branch" read as the branch
    /// it is, so a build pipeline that names <c>main</c> and a deployment pipeline that
    /// allows the default branch agree when <c>main</c> is the default, and the other way
    /// round (#1129). <paramref name="buildDefaultBranches"/> is what the build found the
    /// repositories on when it named no branch: only what the build itself recorded
    /// counts on its side, since the default may have changed since it was made.
    /// <paramref name="solutionDefaultBranches"/> is each repository's default branch as
    /// GitHub last reported it, empty unless every repository has reported. A default
    /// branch counts only when the names agree on one; when nothing says which it is,
    /// the names are compared as written.
    /// </summary>
    public static bool Allows(string? allowedBranch, string? buildBranch,
        IReadOnlyCollection<string> buildDefaultBranches, IReadOnlyCollection<string> solutionDefaultBranches)
    {
        if (Allows(allowedBranch, buildBranch)) return true;
        var built = Resolve(buildBranch, buildDefaultBranches);
        var allowed = Resolve(allowedBranch, solutionDefaultBranches);
        return built is not null && string.Equals(built, allowed, StringComparison.Ordinal);
    }

    /// <summary>The build's recorded default branches as a list: they are stored comma-separated.</summary>
    public static IReadOnlyList<string> SplitRecorded(string? recorded) =>
        (recorded ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // A named branch is itself; the default branch is the one name every repository has
    // as its default, or unknown.
    private static string? Resolve(string? branch, IReadOnlyCollection<string> defaults)
    {
        if (Normalize(branch) is { } named) return named;
        var distinct = defaults.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 1 ? distinct[0] : null;
    }

    /// <summary>
    /// <see cref="Describe(string?)"/> for a build, naming the default branch it was on
    /// when it recorded one: "the repositories' default branch (main)".
    /// </summary>
    public static string DescribeBuild(string? branch, IReadOnlyCollection<string> recordedDefaults) =>
        Normalize(branch) is null && Resolve(null, recordedDefaults) is { } name
            ? $"the repositories' default branch ({name})"
            : Describe(branch);

    /// <summary>The branch as a person reads it: "branch release/25.0", or "the repositories' default branch".</summary>
    public static string Describe(string? branch) =>
        Normalize(branch) is { } name ? $"branch {name}" : "the repositories' default branch";

    /// <summary>Blank reads as the default branch.</summary>
    public static string? Normalize(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
}
