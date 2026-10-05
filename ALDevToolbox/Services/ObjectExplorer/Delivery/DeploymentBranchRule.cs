namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Which builds a deployment pipeline with a branch rule accepts. A branch is the one
/// the build pipeline checked out, as recorded on the build; null on either side means
/// the repositories' default branch, which is what a build pipeline with no branch
/// builds. Branch names are compared exactly, the way git does. See
/// <c>.design/saas-delivery.md</c>, "Which branch may reach an environment".
/// </summary>
public static class DeploymentBranchRule
{
    /// <summary>True when a build from <paramref name="buildBranch"/> may go through a pipeline that only allows <paramref name="allowedBranch"/>.</summary>
    public static bool Allows(string? allowedBranch, string? buildBranch) =>
        string.Equals(Normalize(allowedBranch), Normalize(buildBranch), StringComparison.Ordinal);

    /// <summary>The branch as a person reads it: "branch release/25.0", or "the repositories' default branch".</summary>
    public static string Describe(string? branch) =>
        Normalize(branch) is { } name ? $"branch {name}" : "the repositories' default branch";

    /// <summary>Blank reads as the default branch.</summary>
    public static string? Normalize(string? branch) =>
        string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
}
