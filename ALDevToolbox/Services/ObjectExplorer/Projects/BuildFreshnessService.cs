using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Whether a build pipeline's last successful build is still what its watched
/// branch holds, per repository: the head the <c>push</c> webhook last reported
/// against the commit the build pinned (<c>oe_project_build_repo_commits</c>).
///
/// <para>Read-only and passive. Nothing here asks GitHub anything and nothing is
/// built: a person reads the answer and decides whether to press Build. The
/// comparison is by commit SHA only - a force push makes any count of commits
/// meaningless, and the head row's <c>forced</c> flag is what says so. See
/// <c>.design/github-integration-phase2.md</c>, "Branch watching" (#963).</para>
/// </summary>
public sealed class BuildFreshnessService
{
    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;

    public BuildFreshnessService(AppDbContext db, ProjectAccess access)
    {
        _db = db;
        _access = access;
    }

    /// <summary>
    /// The freshness of <paramref name="pipelineId"/>, one entry per repository of
    /// its solution (a pipeline builds every repository), or <see langword="null"/>
    /// when the pipeline does not exist in this organisation. Throws
    /// <see cref="ProjectAccessDeniedException"/> when the pipeline's solution is
    /// Private and the caller may not see it - the same gate as the pipeline pages.
    ///
    /// <para>States, checked in this order: <see cref="BuildFreshnessState.BranchGone"/>
    /// (GitHub reported the watched branch deleted), <see cref="BuildFreshnessState.NeverBuilt"/>
    /// (no successful build of this pipeline pinned a commit in the repository),
    /// <see cref="BuildFreshnessState.Unknown"/> (no head stored for the branch: the
    /// App is not sending pushes, or nothing has been pushed since),
    /// <see cref="BuildFreshnessState.UpToDate"/> (same SHA), otherwise
    /// <see cref="BuildFreshnessState.Ahead"/>.</para>
    /// </summary>
    public async Task<PipelineFreshness?> GetAsync(int pipelineId, CancellationToken ct = default)
    {
        var pipeline = await _db.OePipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId && p.DeletedAt == null)
            .Select(p => new PipelineKey(p.Id, p.ProjectId, p.Branch, false))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null) return null;
        await _access.EnsureCanViewAsync(pipeline.ProjectId, ct).ConfigureAwait(false);
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == pipeline.ProjectId)
            .Select(p => p.CreatedByUserId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var canBuild = await _access.CanManageAsync(pipeline.ProjectId, ownerId, ct).ConfigureAwait(false);

        return (await ComputeAsync([pipeline with { CanBuild = canBuild }], ct).ConfigureAwait(false))[0];
    }

    /// <summary>
    /// <see cref="GetAsync"/> for every pipeline the caller can see, in a fixed number of
    /// queries rather than one round per pipeline: what the Builds list and the Pipelines
    /// dashboard read (#964). A pipeline of a solution the caller may not see is simply
    /// not in the answer, as it is not on those pages.
    /// </summary>
    public async Task<List<PipelineFreshness>> ListAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);
        var manageable = ProjectAccess.ManageProjectPredicate(snapshot);
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(p => _db.OeProjects.Where(visible).Any(v => v.Id == p.ProjectId))
            .Select(p => new PipelineKey(p.Id, p.ProjectId, p.Branch,
                _db.OeProjects.Where(manageable).Any(m => m.Id == p.ProjectId)))
            .ToListAsync(ct).ConfigureAwait(false);
        return pipelines.Count == 0 ? [] : await ComputeAsync(pipelines, ct).ConfigureAwait(false);
    }

    private sealed record PipelineKey(int Id, int ProjectId, string? Branch, bool CanBuild);

    /// <summary>The comparison itself, for any number of pipelines, in a fixed number of queries.</summary>
    private async Task<List<PipelineFreshness>> ComputeAsync(List<PipelineKey> pipelines, CancellationToken ct)
    {
        var projectIds = pipelines.Select(p => p.ProjectId).Distinct().ToList();
        var pipelineIds = pipelines.Select(p => p.Id).ToList();

        var repositories = await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId))
            .OrderBy(r => r.Id)
            .Select(r => new { r.Id, r.ProjectId, r.DisplayName, r.Provider, r.Url })
            .ToListAsync(ct).ConfigureAwait(false);
        var repositoryIds = repositories.Select(r => r.Id).ToList();

        // The last successful build of each pipeline, and the commit it pinned in
        // each repository. A repository added after that build, or whose clone
        // failed in it, has no commit there and reads as never built.
        var readyBuilds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId.Value) && b.Status == ProjectBuildStatus.Ready
                && b.BcTarget == ProjectBuildTarget.Current)
            .Select(b => new { b.Id, PipelineId = b.PipelineId!.Value, b.StartedAt, b.FinishedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var lastBuilds = readyBuilds
            .GroupBy(b => b.PipelineId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id).First());
        var lastBuildIds = lastBuilds.Values.Select(b => b.Id).ToList();
        var pinned = await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => lastBuildIds.Contains(c.ProjectBuildId) && c.ProjectRepositoryId != null && c.CommitHash != "")
            .Select(c => new { c.ProjectBuildId, RepositoryId = c.ProjectRepositoryId!.Value, c.CommitHash, c.CommittedAt })
            .ToListAsync(ct).ConfigureAwait(false);

        // Builds of these pipelines still queued or running against the current
        // version: one that already covers a repository's new head means the branch
        // is being built, not waiting for somebody to press Build (#1128).
        var activeBuilds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId.Value)
                && (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
                && b.BcTarget == ProjectBuildTarget.Current)
            .Select(b => new ActiveBuild(b.PipelineId!.Value, b.HeadSha, b.HeadRepositoryId, b.StartedAt))
            .ToListAsync(ct).ConfigureAwait(false);

        var heads = await _db.OeRepositoryBranchHeads.AsNoTracking()
            .Where(h => repositoryIds.Contains(h.ProjectRepositoryId))
            .ToListAsync(ct).ConfigureAwait(false);
        var merged = await _db.OeRepositoryMergedPullRequests.AsNoTracking()
            .Where(m => repositoryIds.Contains(m.ProjectRepositoryId))
            .ToListAsync(ct).ConfigureAwait(false);

        var answers = new List<PipelineFreshness>(pipelines.Count);
        foreach (var pipeline in pipelines)
        {
            var lastBuild = lastBuilds.GetValueOrDefault(pipeline.Id);
            var builtByRepository = new Dictionary<int, (string Sha, DateTime? CommittedAt)>();
            if (lastBuild is not null)
            {
                foreach (var c in pinned.Where(c => c.ProjectBuildId == lastBuild.Id))
                    builtByRepository[c.RepositoryId] = (c.CommitHash, c.CommittedAt);
            }

            var result = new List<RepositoryFreshness>();
            foreach (var repository in repositories.Where(r => r.ProjectId == pipeline.ProjectId))
            {
                // The branch this repository is watched on: the pipeline's own, or the
                // repository's default as the last push reported it.
                var head = pipeline.Branch is { } named
                    ? heads.FirstOrDefault(h => h.ProjectRepositoryId == repository.Id
                                                && string.Equals(h.Branch, named, StringComparison.Ordinal))
                    : heads.FirstOrDefault(h => h.ProjectRepositoryId == repository.Id
                                                && h.IsDefaultBranch && h.DeletedAt is null);
                var watchedBranch = pipeline.Branch ?? head?.Branch;
                (string Sha, DateTime? CommittedAt)? built = builtByRepository.TryGetValue(repository.Id, out var b) ? b : null;

                var state = head is { DeletedAt: not null } ? BuildFreshnessState.BranchGone
                    : built is null ? BuildFreshnessState.NeverBuilt
                    : head is null ? BuildFreshnessState.Unknown
                    : string.Equals(head.HeadSha, built.Value.Sha, StringComparison.OrdinalIgnoreCase) ? BuildFreshnessState.UpToDate
                    : BuildFreshnessState.Ahead;

                var beingBuilt = state == BuildFreshnessState.Ahead && head is not null
                    && activeBuilds.Any(a => a.PipelineId == pipeline.Id && Covers(a, repository.Id, head));

                IReadOnlyList<MergedPullRequestSummary> mergedSince = [];
                IReadOnlyList<CommitSummary> commitsSince = [];
                var commitsComplete = true;
                if (state == BuildFreshnessState.Ahead && head is not null && built is { } pin)
                {
                    // Pull requests that merged into the watched branch after the built
                    // commit was made. Best effort: time is what the record carries, and
                    // the built commit's own merge is left out by SHA.
                    var since = pin.CommittedAt ?? lastBuild!.StartedAt;
                    mergedSince = merged
                        .Where(m => m.ProjectRepositoryId == repository.Id
                                    && string.Equals(m.BaseBranch, watchedBranch, StringComparison.Ordinal)
                                    && m.MergedAt > since
                                    && !string.Equals(m.MergeSha, pin.Sha, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(m => m.MergedAt)
                        .Select(m => new MergedPullRequestSummary(m.Number, m.Title, m.AuthorLogin, m.MergedAt, m.MergeSha))
                        .ToList();

                    // The stored commits after the built one. When the built commit is
                    // not in the list the list is only the newest part of what changed.
                    var stored = GitHubBranchActivityService.ReadCommits(head.CommitsJson);
                    var at = stored.FindIndex(c => string.Equals(c.Sha, pin.Sha, StringComparison.OrdinalIgnoreCase));
                    commitsComplete = at >= 0;
                    commitsSince = stored.Skip(at + 1)
                        .Reverse()
                        .Select(c => new CommitSummary(c.Sha, c.Message))
                        .ToList();
                }

                result.Add(new RepositoryFreshness(
                    RepositoryId: repository.Id,
                    RepositoryName: repository.DisplayName,
                    Branch: watchedBranch,
                    State: state,
                    HeadSha: head?.HeadSha,
                    PushedAt: head?.PushedAt,
                    PusherLogin: head is { PusherLogin.Length: > 0 } ? head.PusherLogin : null,
                    Forced: head?.Forced ?? false,
                    BuiltSha: built?.Sha,
                    MergedPullRequests: mergedSince,
                    Commits: commitsSince,
                    CommitsComplete: commitsComplete)
                {
                    BeingBuilt = beingBuilt,
                    WebUrl = repository.Provider == RepositoryProvider.GitHub ? GitHubWebUrl(repository.Url) : null,
                });
            }

            answers.Add(new PipelineFreshness(
                PipelineId: pipeline.Id,
                Branch: pipeline.Branch,
                LastBuildId: lastBuild?.Id,
                LastBuiltAt: lastBuild is null ? null : lastBuild.FinishedAt ?? lastBuild.StartedAt,
                Repositories: result)
            {
                CanBuild = pipeline.CanBuild,
            });
        }
        return answers;
    }

    private sealed record ActiveBuild(int PipelineId, string? HeadSha, int? HeadRepositoryId, DateTime StartedAt);

    /// <summary>
    /// Whether a queued or running build will build <paramref name="head"/>: a build
    /// on push names its commit, and covers the head when that is the pushed
    /// repository's head now. A build that names no commit (Build pressed by a
    /// person) clones the branch as it is when it starts, so it covers every push
    /// recorded before it was queued.
    /// </summary>
    private static bool Covers(ActiveBuild build, int repositoryId, OeRepositoryBranchHead head) =>
        build.HeadSha is { Length: > 0 } sha
            ? build.HeadRepositoryId == repositoryId && string.Equals(sha, head.HeadSha, StringComparison.OrdinalIgnoreCase)
            : build.StartedAt >= head.PushedAt;

    /// <summary>
    /// The repository's page on GitHub, from its clone URL (https or scp-style), for
    /// the links to a commit or a pull request. Null when the URL does not read as
    /// host/owner/name.
    /// </summary>
    internal static string? GitHubWebUrl(string? cloneUrl)
    {
        var normalised = GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(cloneUrl);
        return normalised.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 3
            ? "https://" + normalised
            : null;
    }
}

/// <summary>Where one repository of a pipeline stands against the branch it watches.</summary>
public enum BuildFreshnessState
{
    /// <summary>The last successful build used the commit the branch points at now.</summary>
    UpToDate,

    /// <summary>The branch has moved past the commit the last successful build used.</summary>
    Ahead,

    /// <summary>No successful build of this pipeline has used this repository yet.</summary>
    NeverBuilt,

    /// <summary>GitHub reported the watched branch deleted.</summary>
    BranchGone,

    /// <summary>
    /// No head is stored for the watched branch: the GitHub App is not sending
    /// pushes, the repository is not on GitHub, or nothing has been pushed since
    /// branch watching arrived.
    /// </summary>
    Unknown,
}

/// <summary>A build pipeline's freshness: its branch (null = each repository's default), its last successful build, and each repository.</summary>
public sealed record PipelineFreshness(
    int PipelineId,
    string? Branch,
    int? LastBuildId,
    DateTime? LastBuiltAt,
    IReadOnlyList<RepositoryFreshness> Repositories)
{
    /// <summary>Whether the caller may press Build on this pipeline (manages its solution). Decides what is offered; the build re-checks.</summary>
    public bool CanBuild { get; init; }
}

/// <summary>
/// One repository's freshness.
/// </summary>
/// <param name="Branch">The branch watched in this repository; null when the pipeline follows the default branch and no push has said which that is.</param>
/// <param name="HeadSha">Where the branch points, as the last push said; null when nothing is stored.</param>
/// <param name="Forced">The last push rewrote history, so the commit list below may not be a linear "since" at all.</param>
/// <param name="BuiltSha">The commit the last successful build used; null when it never used this repository.</param>
/// <param name="MergedPullRequests">For <see cref="BuildFreshnessState.Ahead"/>: pull requests merged into the branch since the built commit, newest first.</param>
/// <param name="Commits">For <see cref="BuildFreshnessState.Ahead"/>: the stored commits after the built one, newest first.</param>
/// <param name="CommitsComplete">False when the built commit is not among the stored commits, so <paramref name="Commits"/> is only the newest part of what changed.</param>
public sealed record RepositoryFreshness(
    int RepositoryId,
    string RepositoryName,
    string? Branch,
    BuildFreshnessState State,
    string? HeadSha,
    DateTime? PushedAt,
    string? PusherLogin,
    bool Forced,
    string? BuiltSha,
    IReadOnlyList<MergedPullRequestSummary> MergedPullRequests,
    IReadOnlyList<CommitSummary> Commits,
    bool CommitsComplete)
{
    /// <summary>The repository's page on GitHub (https://github.com/owner/name), for links to commits and pull requests; null for any other host.</summary>
    public string? WebUrl { get; init; }

    /// <summary>
    /// For <see cref="BuildFreshnessState.Ahead"/>: a build of the pipeline that is
    /// queued or running already covers the branch's newest commit (#1128).
    /// </summary>
    public bool BeingBuilt { get; init; }
}

/// <summary>A pull request that merged into a watched branch.</summary>
public sealed record MergedPullRequestSummary(int Number, string Title, string AuthorLogin, DateTime MergedAt, string MergeSha);

/// <summary>One commit on a watched branch, as a push listed it.</summary>
public sealed record CommitSummary(string Sha, string Message);
