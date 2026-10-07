using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// Decides which pipelines' nightly preview checks are due, for the organisation in
/// scope, and records why one could not start. The builds themselves are started by
/// <see cref="PreviewCheckScheduler"/> through
/// <see cref="ProjectBuildImporter.StartPreviewCheckAsync"/>, as the person who turned
/// the check on. Everything here reads through the organisation's query filter.
/// See <c>.design/object-explorer-project-builds.md</c>, "The nightly preview check".
/// </summary>
public sealed class PreviewCheckService
{
    /// <summary>
    /// The longest a check goes without running when it cannot tell whether the code
    /// changed (a repository with no head stored from a push), or when its last run
    /// failed against the same code. A clean check on unchanged code waits for a change.
    /// </summary>
    internal static readonly TimeSpan MaxQuietPeriod = TimeSpan.FromDays(7);

    internal const string NoCountryMessage =
        "this solution has no country set, so there is no Business Central version to check against. Set one in the solution's settings.";

    private readonly AppDbContext _db;
    private readonly BcArtifactService _artifacts;
    private readonly ILogger<PreviewCheckService> _logger;

    public PreviewCheckService(AppDbContext db, BcArtifactService artifacts, ILogger<PreviewCheckService> logger)
    {
        _db = db;
        _artifacts = artifacts;
        _logger = logger;
    }

    /// <summary>
    /// The preview builds due tonight. A target is due when nothing is in flight for
    /// it, it has not run on this UTC day, Microsoft has published a preview for it,
    /// and something has changed since its last run: a new preview build from
    /// Microsoft, or new commits on the pipeline's branch. A check that ran clean on
    /// the same commits and preview build is not run again (#1140). Where no push has
    /// reported a branch's head, a build of the pipeline since, or
    /// <see cref="MaxQuietPeriod"/> passing, stands in for new commits. A pipeline that cannot run at all comes back once, with
    /// <see cref="PreviewCheckDue.Blocked"/> saying why; one that can comes back once
    /// with neither a target nor a reason, so a pause whose cause has gone is lifted
    /// even on a night with nothing to build.
    /// </summary>
    public async Task<List<PreviewCheckDue>> ListDueAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.PreviewCheck && p.DeletedAt == null && p.Project!.DeletedAt == null)
            .Select(p => new
            {
                p.Id,
                p.ProjectId,
                p.Branch,
                p.PreviewCheckByUserId,
                OwnerActive = p.PreviewCheckByUser != null && p.PreviewCheckByUser.Status == UserStatus.Active,
                Country = p.Project!.DefaultArtifactCountry,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (pipelines.Count == 0) return [];

        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId.Value))
            .Select(b => new
            {
                b.Id,
                PipelineId = b.PipelineId!.Value,
                b.BcTarget,
                b.Status,
                b.StartedAt,
                b.BcArtifactVersion,
                b.ReleaseId,
                // In flight only while its release is still ingesting, as in
                // ProjectBuildImporter.BlocksManualBuild: nothing resets a row whose job
                // was lost, and trusting the row alone would stop the check for good (#1111).
                InFlight = (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
                    && b.Release != null && b.Release.Status == "ingesting",
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var byPipeline = builds.ToLookup(b => b.PipelineId);

        // What each pipeline's last check of each target compiled, and what its
        // branches hold now as the push webhook last reported them: together they
        // say whether the code changed since that check (#1140).
        var lastChecks = builds
            .Where(b => ProjectBuildTarget.IsPreview(b.BcTarget))
            .GroupBy(b => (b.PipelineId, b.BcTarget))
            .Select(g => g.MaxBy(b => b.StartedAt)!)
            .ToList();
        var lastCheckIds = lastChecks.Select(b => b.Id).ToList();
        // A check that went ready with an extension that did not compile counts as
        // failed, as it does on the pipeline pages.
        var lastCheckReleaseIds = lastChecks.Where(b => b.ReleaseId != null).Select(b => b.ReleaseId!.Value).ToList();
        var withFailedApps = (await _db.OeProjectBuildResults.AsNoTracking()
                .Where(r => lastCheckReleaseIds.Contains(r.ReleaseId) && r.Status == ProjectBuildResultStatus.Failed)
                .Select(r => r.ReleaseId)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();
        var pinned = (await _db.OeProjectBuildRepoCommits.AsNoTracking()
            .Where(c => lastCheckIds.Contains(c.ProjectBuildId) && c.ProjectRepositoryId != null && c.CommitHash != "")
            .Select(c => new { c.ProjectBuildId, RepositoryId = c.ProjectRepositoryId!.Value, c.CommitHash })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(c => c.ProjectBuildId);
        var projectIds = pipelines.Select(p => p.ProjectId).Distinct().ToList();
        var repositories = (await _db.OeProjectRepositories.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId))
            .Select(r => new { r.Id, r.ProjectId })
            .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(r => r.ProjectId, r => r.Id);
        var repositoryIds = repositories.SelectMany(g => g).ToList();
        var heads = await _db.OeRepositoryBranchHeads.AsNoTracking()
            .Where(h => repositoryIds.Contains(h.ProjectRepositoryId) && h.DeletedAt == null)
            .Select(h => new BranchHead(h.ProjectRepositoryId, h.Branch, h.IsDefaultBranch, h.HeadSha, h.PushedAt))
            .ToListAsync(ct).ConfigureAwait(false);

        var today = DateOnly.FromDateTime(nowUtc);
        var versions = new Dictionary<(string Country, string Target), string?>();
        var due = new List<PreviewCheckDue>();
        foreach (var pipeline in pipelines)
        {
            if (pipeline.PreviewCheckByUserId is not { } userId || !pipeline.OwnerActive)
            {
                due.Add(new PreviewCheckDue(pipeline.Id, null, null, AutomatedBuilds.NoOwnerMessage));
                continue;
            }

            string country;
            try
            {
                country = ProjectBuildService.ResolveCountry(pipeline.Country);
            }
            catch (InvalidOperationException)
            {
                due.Add(new PreviewCheckDue(pipeline.Id, userId, null, NoCountryMessage));
                continue;
            }

            due.Add(new PreviewCheckDue(pipeline.Id, userId, null, null));
            var history = byPipeline[pipeline.Id].ToList();
            var lastCurrentBuild = history
                .Where(b => b.BcTarget == ProjectBuildTarget.Current)
                .Select(b => (DateTime?)b.StartedAt)
                .Max();

            foreach (var target in ProjectBuildTarget.Previews)
            {
                var checks = history.Where(b => b.BcTarget == target).ToList();
                if (checks.Any(b => b.InFlight)) continue;

                var last = checks.MaxBy(b => b.StartedAt);
                if (last is not null && DateOnly.FromDateTime(last.StartedAt) == today) continue;

                var key = (country, target);
                if (!versions.TryGetValue(key, out var version))
                {
                    version = await ResolveVersionAsync(country, target, ct).ConfigureAwait(false);
                    versions[key] = version;
                }
                // Microsoft has not published a preview of that version yet, or the
                // index could not be read tonight. Neither is the pipeline's problem.
                if (version is null) continue;

                if (last is not null && last.BcArtifactVersion == version)
                {
                    var sameCode = SameCode(
                        repositories[pipeline.ProjectId].ToList(),
                        pinned[last.Id].ToDictionary(c => c.RepositoryId, c => c.CommitHash),
                        heads, pipeline.Branch, last.StartedAt);
                    var clean = last.Status == ProjectBuildStatus.Ready
                                && !(last.ReleaseId is { } checkedRelease && withFailedApps.Contains(checkedRelease));
                    // A check that ran clean against the same code and the same preview
                    // build would only say the same again, however long ago it ran. One
                    // that failed is tried again once the quiet period passes, in case
                    // what failed was not the code.
                    if (sameCode == true && clean) continue;
                    // Without a stored head for every repository the code may have moved
                    // unseen, so a build of the pipeline since and the quiet period stand in.
                    var quiet = sameCode != false
                        && (sameCode == true || lastCurrentBuild is null || lastCurrentBuild < last.StartedAt)
                        && nowUtc - last.StartedAt < MaxQuietPeriod;
                    if (quiet) continue;
                }

                due.Add(new PreviewCheckDue(pipeline.Id, userId, target, null));
            }
        }
        return due;
    }

    /// <summary>
    /// Whether every repository's watched branch still holds the commit the last
    /// check compiled. True when every stored head matches; false when a push the
    /// webhook reported after the check moved one; null when nothing can tell: a
    /// repository with no stored head or no commit in the check, or a head that
    /// differs but was stored before the check ran (a missed push left it behind
    /// the branch the check cloned, so the difference says nothing).
    /// </summary>
    internal static bool? SameCode(
        IReadOnlyList<int> repositoryIds,
        IReadOnlyDictionary<int, string> checkedCommits,
        IReadOnlyList<BranchHead> heads,
        string? branch,
        DateTime checkStartedAt)
    {
        if (repositoryIds.Count == 0) return null;
        var unknown = false;
        foreach (var repositoryId in repositoryIds)
        {
            var head = branch is { } named
                ? heads.FirstOrDefault(h => h.RepositoryId == repositoryId && string.Equals(h.Branch, named, StringComparison.Ordinal))
                : heads.FirstOrDefault(h => h.RepositoryId == repositoryId && h.IsDefaultBranch);
            if (head is null || !checkedCommits.TryGetValue(repositoryId, out var sha))
            {
                unknown = true;
                continue;
            }
            if (string.Equals(head.HeadSha, sha, StringComparison.OrdinalIgnoreCase)) continue;
            if (head.PushedAt > checkStartedAt) return false;
            unknown = true;
        }
        return unknown ? null : true;
    }

    /// <summary>A branch head as the push webhook last reported it.</summary>
    internal sealed record BranchHead(int RepositoryId, string Branch, bool IsDefaultBranch, string HeadSha, DateTime PushedAt);

    /// <summary>
    /// Records why <paramref name="pipelineId"/>'s check could not start, or clears it
    /// with null. Writes only when the value changes.
    /// </summary>
    public Task SetBlockedAsync(int pipelineId, string? reason, CancellationToken ct = default) =>
        AutomatedBuilds.SetBlockedAsync(_db, pipelineId, PipelineAutomation.PreviewCheck, reason, ct);

    private async Task<string?> ResolveVersionAsync(string country, string target, CancellationToken ct)
    {
        try
        {
            var resolved = await _artifacts.ResolveTargetAsync(country, ProjectBuildTarget.ToBuildTarget(target), ct).ConfigureAwait(false);
            return resolved?.Version;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read Microsoft's preview builds for {Country} ({Target}); skipping tonight's check.", country, target);
            return null;
        }
    }
}

/// <summary>
/// One preview build due tonight, or (with <see cref="Blocked"/> set) a pipeline whose
/// check cannot run and why.
/// </summary>
/// <param name="UserId">Who the build runs as: the person who turned the check on.</param>
/// <param name="BcTarget"><c>next_minor</c> or <c>next_major</c>; null when blocked.</param>
public sealed record PreviewCheckDue(int PipelineId, int? UserId, string? BcTarget, string? Blocked);
