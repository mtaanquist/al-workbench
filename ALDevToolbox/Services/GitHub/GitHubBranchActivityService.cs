using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Writes what GitHub's <c>push</c> and merged <c>pull_request</c> deliveries say
/// about a solution repository's branches: one head row per branch, one row per
/// merged pull request. Called by <see cref="GitHubPullRequestBuildWorker"/> inside
/// the organisation it resolved from the installation id, so every read and write
/// here runs under that organisation's ordinary query filter.
///
/// <para>Nothing is built because of anything written here; which pipelines build on a
/// push is decided separately by <see cref="ALDevToolbox.Services.ObjectExplorer.Projects.PushBuildService"/>
/// and started by <see cref="GitHubPullRequestBuildWorker"/> (#1079). The rows are what <c>BuildFreshnessService</c> compares a pipeline's last
/// build against. See <c>.design/github-integration-phase2.md</c>, "Branch watching" (#963).</para>
/// </summary>
public sealed class GitHubBranchActivityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<GitHubBranchActivityService> _logger;

    public GitHubBranchActivityService(AppDbContext db, IOrganizationContext orgContext, ILogger<GitHubBranchActivityService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// Records a push against every solution repository in the current
    /// organisation that tracks the pushed repository, and returns how many it
    /// matched. Zero is ordinary (a repository no solution tracks) and writes
    /// nothing.
    ///
    /// <para>A deleted branch keeps its row with <c>deleted_at</c> set, so a
    /// pipeline watching it can say the branch is gone rather than that nothing is
    /// known. A push that fast-forwards from the stored head appends its commits to
    /// the stored list; anything else (a forced push, a recreated branch, a push we
    /// missed the one before) replaces it, because the old commits may no longer be
    /// on the branch. A push older than the one already recorded (late or
    /// redelivered) changes nothing.</para>
    /// </summary>
    public async Task<int> RecordPushAsync(GitHubPushJob push, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var repositoryIds = await GitHubRepositoryMatch.TrackingRepositoryIdsAsync(_db, push.CloneUrl, ct).ConfigureAwait(false);
        if (repositoryIds.Count == 0) return 0;

        var existing = await _db.OeRepositoryBranchHeads
            .Where(h => repositoryIds.Contains(h.ProjectRepositoryId))
            .ToListAsync(ct).ConfigureAwait(false);

        var now = DateTime.UtcNow;
        foreach (var repositoryId in repositoryIds)
        {
            var rows = existing.Where(h => h.ProjectRepositoryId == repositoryId).ToList();

            // GitHub names the default branch on every push, so the flag follows
            // a default-branch rename the next time anything is pushed.
            if (push.DefaultBranch.Length > 0)
            {
                foreach (var row in rows)
                {
                    row.IsDefaultBranch = string.Equals(row.Branch, push.DefaultBranch, StringComparison.Ordinal);
                }
            }

            var head = rows.FirstOrDefault(h => string.Equals(h.Branch, push.Branch, StringComparison.Ordinal));
            if (head is null)
            {
                head = new OeRepositoryBranchHead
                {
                    OrganizationId = orgId,
                    ProjectRepositoryId = repositoryId,
                    Branch = push.Branch,
                    IsDefaultBranch = string.Equals(push.Branch, push.DefaultBranch, StringComparison.Ordinal),
                };
                _db.OeRepositoryBranchHeads.Add(head);
            }
            else if (push.PushedAt < head.PushedAt)
            {
                // GitHub does not promise delivery order, and a redelivery carries the
                // original push. Either is older than what the row already says, so
                // it leaves the branch where the newer push put it. GitHub's time is in
                // whole seconds, so two pushes within one second still land in arrival order.
                continue;
            }

            if (push.Deleted)
            {
                head.DeletedAt = push.PushedAt;
                head.PushedAt = push.PushedAt;
                head.PusherLogin = push.PusherLogin;
                if (head.HeadSha.Length == 0) head.HeadSha = push.HeadSha;
                head.UpdatedAt = now;
                continue;
            }

            var fastForward = !push.Forced
                              && head.DeletedAt is null
                              && head.HeadSha.Length > 0
                              && string.Equals(head.HeadSha, push.BeforeSha, StringComparison.OrdinalIgnoreCase);
            var commits = fastForward ? ReadCommits(head.CommitsJson) : [];
            commits.AddRange(push.Commits);

            head.HeadSha = push.HeadSha;
            head.PushedAt = push.PushedAt;
            head.PusherLogin = push.PusherLogin;
            head.Forced = push.Forced;
            head.CommitCount = push.CommitCount;
            head.CommitsJson = JsonSerializer.Serialize(
                commits.TakeLast(MaxStoredCommits).ToList(), JsonOptions);
            head.DeletedAt = null;
            head.UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Recorded a push to {Repository} ({Branch} at {HeadSha}, forced {Forced}, deleted {Deleted}) for {Count} solution repositories.",
            push.RepositoryFullName, push.Branch, push.HeadSha, push.Forced, push.Deleted, repositoryIds.Count);
        return repositoryIds.Count;
    }

    /// <summary>
    /// Records a merged pull request against every solution repository in the
    /// current organisation that tracks the repository, and returns how many it
    /// matched. A redelivery updates the existing row instead of adding a second.
    /// </summary>
    public async Task<int> RecordMergedPullRequestAsync(GitHubMergedPullRequestJob merged, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var repositoryIds = await GitHubRepositoryMatch.TrackingRepositoryIdsAsync(_db, merged.CloneUrl, ct).ConfigureAwait(false);
        if (repositoryIds.Count == 0) return 0;

        var existing = await _db.OeRepositoryMergedPullRequests
            .Where(m => repositoryIds.Contains(m.ProjectRepositoryId) && m.Number == merged.Number)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var repositoryId in repositoryIds)
        {
            var row = existing.FirstOrDefault(m => m.ProjectRepositoryId == repositoryId);
            if (row is null)
            {
                row = new OeRepositoryMergedPullRequest
                {
                    OrganizationId = orgId,
                    ProjectRepositoryId = repositoryId,
                    Number = merged.Number,
                };
                _db.OeRepositoryMergedPullRequests.Add(row);
            }
            row.Title = merged.Title;
            row.BaseBranch = merged.BaseBranch;
            row.MergeSha = merged.MergeSha;
            row.MergedAt = merged.MergedAt;
            row.AuthorLogin = merged.AuthorLogin;
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Recorded merged pull request {Repository}#{Number} into {Branch} for {Count} solution repositories.",
            merged.RepositoryFullName, merged.Number, merged.BaseBranch, repositoryIds.Count);
        return repositoryIds.Count;
    }

    /// <summary>The stored commit list of a head row, oldest first. An unreadable column reads as empty.</summary>
    public static List<GitHubPushCommit> ReadCommits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<GitHubPushCommit>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>How many commits a head row keeps. Ten is what a "what changed" line can use.</summary>
    public const int MaxStoredCommits = 10;

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; branch activity recorded outside an organisation.");
}

/// <summary>
/// Which solution repositories in the current organisation are the GitHub
/// repository a webhook delivery named, by normalised clone URL. Shared by the
/// pull-request build routing and the branch-activity recorder, which have to agree
/// on what "tracked" means.
/// </summary>
internal static class GitHubRepositoryMatch
{
    /// <summary>
    /// Every non-deleted solution's repository row whose URL normalises to the same
    /// thing as <paramref name="cloneUrl"/>, with its solution. Runs under the
    /// caller's organisation filter; nothing here crosses it.
    /// </summary>
    public static async Task<List<(int RepositoryId, int ProjectId, string ProjectName)>> TrackingRepositoriesAsync(
        AppDbContext db, string cloneUrl, CancellationToken ct)
    {
        var candidates = await db.OeProjectRepositories.AsNoTracking()
            .Where(r => r.Provider == Domain.ValueObjects.RepositoryProvider.GitHub && r.Project!.DeletedAt == null)
            .Select(r => new { r.Id, r.Url, r.ProjectId, ProjectName = r.Project!.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(cloneUrl);
        return candidates
            .Where(r => GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(r.Url) == wanted)
            .Select(r => (r.Id, r.ProjectId, r.ProjectName))
            .ToList();
    }

    public static async Task<List<int>> TrackingRepositoryIdsAsync(AppDbContext db, string cloneUrl, CancellationToken ct) =>
        (await TrackingRepositoriesAsync(db, cloneUrl, ct).ConfigureAwait(false))
            .Select(r => r.RepositoryId)
            .Distinct()
            .ToList();
}
