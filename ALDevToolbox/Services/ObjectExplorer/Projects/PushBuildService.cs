using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Which pipelines a GitHub push should build, and the pause a pipeline shows when
/// one could not start. The builds themselves are started by
/// <see cref="GitHubPullRequestBuildWorker"/>, one per pipeline, as the person who
/// turned building on push on, through <see cref="ProjectBuildImporter.StartPushBuildAsync"/>.
/// Runs under the organisation the worker resolved from the installation, so every
/// read here goes through that organisation's ordinary query filter. See
/// <c>.design/github-integration-phase2.md</c>, "Building on push" (#1079).
/// </summary>
public sealed class PushBuildService
{
    private readonly AppDbContext _db;

    public PushBuildService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Whether a push is one that builds at all. A deleted branch has nothing to
    /// build, and a forced push rewrote the branch's history, so building it would
    /// record a "change" that may be the removal of others' work; both leave the
    /// pipeline to a person.
    /// </summary>
    public static bool IsBuildable(GitHubPushJob push) =>
        !push.Deleted && !push.Forced && push.HeadSha.Length > 0 && push.HeadSha.Any(c => c != '0');

    /// <summary>
    /// Whether a push changed only documentation and so is not built: building it
    /// would compile the same apps again and prepare a deployment of nothing new.
    /// Only when the push carried straight on from the head already recorded
    /// (<paramref name="followsRecordedHead"/>): otherwise an earlier push may have
    /// been missed, or may still arrive and be dropped as older than this one, and
    /// this push's build is the only one that would carry its code.
    /// </summary>
    public static bool IsDocumentationOnly(GitHubPushJob push, bool followsRecordedHead) =>
        push.DocumentationOnly && followsRecordedHead;

    /// <summary>
    /// The pipelines in the current organisation that build on push and watch the
    /// branch <paramref name="push"/> moved, each with the solution repository that
    /// was pushed to and the person the build runs as. A pipeline whose person is gone
    /// comes back with <see cref="PushBuildDue.Blocked"/> saying so. A pipeline that
    /// already has a build on push of this exact commit is left out, so GitHub
    /// redelivering a push does not build it twice, and so is every pipeline when a
    /// newer push to the branch has already been recorded.
    /// </summary>
    public async Task<List<PushBuildDue>> ListDueAsync(GitHubPushJob push, CancellationToken ct = default)
    {
        if (!IsBuildable(push)) return [];

        var repositories = await GitHubRepositoryMatch.TrackingRepositoriesAsync(_db, push.CloneUrl, ct).ConfigureAwait(false);
        if (repositories.Count == 0) return [];
        var repositoryByProject = repositories
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.First().RepositoryId);

        // A push that arrives after a newer one to the same branch (GitHub does not
        // promise order, and a redelivery carries the original push) is not built: the
        // newer push already queued its build, or moved a waiting one onto its commit,
        // and building the older commit after it would leave the branch's newest build
        // holding older code.
        var repositoryIds = repositoryByProject.Values.ToList();
        var superseded = await _db.OeRepositoryBranchHeads.AsNoTracking()
            .Where(h => repositoryIds.Contains(h.ProjectRepositoryId)
                        && h.Branch == push.Branch
                        && h.PushedAt > push.PushedAt)
            .Select(h => h.ProjectRepositoryId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var project in repositoryByProject.Where(r => superseded.Contains(r.Value)).Select(r => r.Key).ToList())
        {
            repositoryByProject.Remove(project);
        }
        if (repositoryByProject.Count == 0) return [];
        var projectIds = repositoryByProject.Keys.ToList();

        // A pipeline with no branch builds each repository's default branch, which
        // GitHub names on every push.
        var isDefault = push.DefaultBranch.Length > 0
                        && string.Equals(push.Branch, push.DefaultBranch, StringComparison.Ordinal);
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.BuildOnPush && p.DeletedAt == null && p.DisabledAt == null && p.Project!.DeletedAt == null
                        && projectIds.Contains(p.ProjectId)
                        && (p.Branch == push.Branch || (isDefault && p.Branch == null)))
            .Select(p => new
            {
                p.Id,
                p.ProjectId,
                p.BuildOnPushByUserId,
                OwnerActive = p.BuildOnPushByUser != null && p.BuildOnPushByUser.Status == UserStatus.Active,
                AlreadyBuilt = p.Builds.Any(b => b.Trigger == ProjectBuildTrigger.Push && b.HeadSha == push.HeadSha),
            })
            .OrderBy(p => p.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        return pipelines
            .Where(p => !p.AlreadyBuilt)
            .Select(p => p.BuildOnPushByUserId is { } userId && p.OwnerActive
                ? new PushBuildDue(p.Id, repositoryByProject[p.ProjectId], userId, null)
                : new PushBuildDue(p.Id, repositoryByProject[p.ProjectId], null, AutomatedBuilds.NoOwnerMessage))
            .ToList();
    }

    /// <summary>
    /// Records why <paramref name="pipelineId"/>'s last push could not start a build,
    /// or clears it with null. Writes only when the value changes.
    /// </summary>
    public Task SetBlockedAsync(int pipelineId, string? reason, CancellationToken ct = default) =>
        AutomatedBuilds.SetBlockedAsync(_db, pipelineId, PipelineAutomation.BuildOnPush, reason, ct);
}

/// <summary>
/// One pipeline a push should build: the solution repository pushed to, and the
/// person the build runs as, or why it cannot start.
/// </summary>
public sealed record PushBuildDue(int PipelineId, int RepositoryId, int? UserId, string? Blocked);
