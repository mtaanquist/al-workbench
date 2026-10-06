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
    internal const string NoOwnerMessage =
        "the person its builds run as no longer has an active account.";

    internal const string NoAccessMessage =
        "the person its builds run as can no longer manage this solution.";

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
    /// The pipelines in the current organisation that build on push and watch the
    /// branch <paramref name="push"/> moved, each with the solution repository that
    /// was pushed to and the person the build runs as. A pipeline whose person is gone
    /// comes back with <see cref="PushBuildDue.Blocked"/> saying so. A pipeline that
    /// already has a build on push of this exact commit is left out, so GitHub
    /// redelivering a push does not build it twice.
    /// </summary>
    public async Task<List<PushBuildDue>> ListDueAsync(GitHubPushJob push, CancellationToken ct = default)
    {
        if (!IsBuildable(push)) return [];

        var repositories = await GitHubRepositoryMatch.TrackingRepositoriesAsync(_db, push.CloneUrl, ct).ConfigureAwait(false);
        if (repositories.Count == 0) return [];
        var repositoryByProject = repositories
            .GroupBy(r => r.ProjectId)
            .ToDictionary(g => g.Key, g => g.First().RepositoryId);
        var projectIds = repositoryByProject.Keys.ToList();

        // A pipeline with no branch builds each repository's default branch, which
        // GitHub names on every push.
        var isDefault = push.DefaultBranch.Length > 0
                        && string.Equals(push.Branch, push.DefaultBranch, StringComparison.Ordinal);
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.BuildOnPush && p.DeletedAt == null && p.Project!.DeletedAt == null
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
                : new PushBuildDue(p.Id, repositoryByProject[p.ProjectId], null, NoOwnerMessage))
            .ToList();
    }

    /// <summary>
    /// Records why <paramref name="pipelineId"/>'s last push could not start a build,
    /// or clears it with null. Writes only when the value changes.
    /// </summary>
    public async Task SetBlockedAsync(int pipelineId, string? reason, CancellationToken ct = default)
    {
        if (reason is { Length: > 500 }) reason = reason[..500];
        await _db.OePipelines
            .Where(p => p.Id == pipelineId && p.BuildOnPushBlocked != reason)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.BuildOnPushBlocked, reason), ct)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// One pipeline a push should build: the solution repository pushed to, and the
/// person the build runs as, or why it cannot start.
/// </summary>
public sealed record PushBuildDue(int PipelineId, int RepositoryId, int? UserId, string? Blocked);
