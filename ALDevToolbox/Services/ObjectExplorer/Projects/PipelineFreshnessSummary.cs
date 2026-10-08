namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// What a surface says about a whole pipeline, folded from the per-repository answer of
/// <see cref="BuildFreshnessService"/>: the Builds list's line, the Pipelines dashboard's
/// tile and attention rows, and the lead sentence of the pipeline page's card (#964).
/// </summary>
public enum PipelineFreshnessHeadline
{
    /// <summary>Nothing worth saying: no heads are stored (the App is not sending pushes) or the pipeline builds no repository.</summary>
    None,

    /// <summary>Every repository with a known head is at the commit the last successful build used.</summary>
    UpToDate,

    /// <summary>At least one repository's branch moved past the last successful build.</summary>
    Ahead,

    /// <summary>
    /// The branch moved past the last successful build, and a build that is queued or
    /// running already covers every new head: nothing for a person to start (#1128).
    /// </summary>
    Building,

    /// <summary>GitHub reported the watched branch deleted in a repository.</summary>
    BranchGone,

    /// <summary>The pipeline has no successful build.</summary>
    NeverBuilt,

    /// <summary>The pipeline has a successful build, but a repository is not in it (added since, or its clone failed).</summary>
    NotInLastBuild,
}

/// <summary>
/// One pipeline's freshness, as a sentence's facts. <see cref="CommitCount"/> is null
/// whenever a count would be a guess: after a force push, or when the stored commits do
/// not reach back to the built one.
/// </summary>
/// <param name="Branch">The watched branch to name; null when the pipeline follows each repository's default and no push has named it.</param>
/// <param name="LastBuildId">The last successful build, for "since build #118".</param>
/// <param name="MergedPullRequests">Pull requests merged into the branch since that build, across the pipeline's repositories.</param>
/// <param name="PushedAt">The newest push behind an <see cref="PipelineFreshnessHeadline.Ahead"/> answer.</param>
/// <param name="RepositoryName">The repository a <see cref="PipelineFreshnessHeadline.NotInLastBuild"/> answer is about.</param>
public sealed record PipelineFreshnessSummary(
    PipelineFreshnessHeadline Headline,
    string? Branch,
    int? LastBuildId,
    int MergedPullRequests,
    int? CommitCount,
    bool Forced,
    DateTime? PushedAt,
    string? RepositoryName = null)
{
    /// <summary>The branch has moved past the last build: the "Ready to build" filter and tile count these.</summary>
    public bool IsAhead => Headline == PipelineFreshnessHeadline.Ahead;

    /// <summary>
    /// Folds the repositories into one answer, strongest first: any repository ahead
    /// makes the pipeline ahead, or building when a queued or running build already
    /// covers every repository that is ahead; then a deleted branch; then no successful build; then a
    /// repository missing from the last build; then up to date. A repository whose head is
    /// unknown (not on GitHub, or no push yet) says nothing, so it neither blocks nor
    /// makes the "up to date" claim.
    /// </summary>
    public static PipelineFreshnessSummary From(PipelineFreshness freshness)
    {
        ArgumentNullException.ThrowIfNull(freshness);
        var repos = freshness.Repositories;
        var ahead = repos.Where(r => r.State == BuildFreshnessState.Ahead).ToList();
        string? BranchOf(IEnumerable<RepositoryFreshness> some) =>
            freshness.Branch ?? some.Select(r => r.Branch).FirstOrDefault(b => !string.IsNullOrEmpty(b))
                             ?? repos.Select(r => r.Branch).FirstOrDefault(b => !string.IsNullOrEmpty(b));

        if (ahead.Count > 0 && ahead.All(r => r.BeingBuilt))
        {
            return Plain(PipelineFreshnessHeadline.Building, BranchOf(ahead), freshness.LastBuildId) with
            {
                PushedAt = ahead.Max(r => r.PushedAt),
            };
        }

        if (ahead.Count > 0)
        {
            var forced = ahead.Any(r => r.Forced);
            var countable = !forced && ahead.All(r => r.CommitsComplete);
            return new PipelineFreshnessSummary(
                PipelineFreshnessHeadline.Ahead,
                BranchOf(ahead),
                freshness.LastBuildId,
                ahead.Sum(r => r.MergedPullRequests.Count),
                countable ? ahead.Sum(r => r.Commits.Count) : null,
                forced,
                ahead.Max(r => r.PushedAt));
        }

        var gone = repos.Where(r => r.State == BuildFreshnessState.BranchGone).ToList();
        if (gone.Count > 0)
            return Plain(PipelineFreshnessHeadline.BranchGone, BranchOf(gone), freshness.LastBuildId);

        if (freshness.LastBuildId is null && repos.Count > 0)
            return Plain(PipelineFreshnessHeadline.NeverBuilt, BranchOf(repos), null);

        if (repos.FirstOrDefault(r => r.State == BuildFreshnessState.NeverBuilt) is { } missing)
            return Plain(PipelineFreshnessHeadline.NotInLastBuild, BranchOf(repos), freshness.LastBuildId) with
            {
                RepositoryName = missing.RepositoryName,
            };

        var current = repos.Where(r => r.State == BuildFreshnessState.UpToDate).ToList();
        if (current.Count > 0)
            return Plain(PipelineFreshnessHeadline.UpToDate, BranchOf(current), freshness.LastBuildId);

        return Plain(PipelineFreshnessHeadline.None, null, freshness.LastBuildId);
    }

    private static PipelineFreshnessSummary Plain(PipelineFreshnessHeadline headline, string? branch, int? lastBuildId) =>
        new(headline, branch, lastBuildId, 0, null, false, null);
}
