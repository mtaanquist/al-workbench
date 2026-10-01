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
    /// The longest a check goes without running, even when nothing it can see has
    /// changed. Nothing here can see a push to a repository the pipeline has not
    /// built since, so this is what catches one.
    /// </summary>
    internal static readonly TimeSpan MaxQuietPeriod = TimeSpan.FromDays(7);

    internal const string NoOwnerMessage =
        "the person who turned it on no longer has an active account.";

    internal const string NoAccessMessage =
        "the person who turned it on can no longer manage this solution.";

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
    /// Microsoft, a build of the pipeline since, or <see cref="MaxQuietPeriod"/>
    /// passing. A pipeline that cannot run at all comes back once, with
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
                p.PreviewCheckByUserId,
                OwnerActive = p.PreviewCheckByUser != null && p.PreviewCheckByUser.Status == UserStatus.Active,
                Country = p.Project!.DefaultArtifactCountry,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (pipelines.Count == 0) return [];

        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var builds = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId.Value))
            .Select(b => new { PipelineId = b.PipelineId!.Value, b.BcTarget, b.Status, b.StartedAt, b.BcArtifactVersion })
            .ToListAsync(ct).ConfigureAwait(false);
        var byPipeline = builds.ToLookup(b => b.PipelineId);

        var today = DateOnly.FromDateTime(nowUtc);
        var versions = new Dictionary<(string Country, string Target), string?>();
        var due = new List<PreviewCheckDue>();
        foreach (var pipeline in pipelines)
        {
            if (pipeline.PreviewCheckByUserId is not { } userId || !pipeline.OwnerActive)
            {
                due.Add(new PreviewCheckDue(pipeline.Id, null, null, NoOwnerMessage));
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
                if (checks.Any(b => b.Status is ProjectBuildStatus.Queued or ProjectBuildStatus.Building)) continue;

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

                var unchanged = last is not null
                    && last.BcArtifactVersion == version
                    && (lastCurrentBuild is null || lastCurrentBuild < last.StartedAt)
                    && nowUtc - last.StartedAt < MaxQuietPeriod;
                if (unchanged) continue;

                due.Add(new PreviewCheckDue(pipeline.Id, userId, target, null));
            }
        }
        return due;
    }

    /// <summary>
    /// Records why <paramref name="pipelineId"/>'s check could not start, or clears it
    /// with null. Writes only when the value changes.
    /// </summary>
    public async Task SetBlockedAsync(int pipelineId, string? reason, CancellationToken ct = default)
    {
        if (reason is { Length: > 500 }) reason = reason[..500];
        await _db.OePipelines
            .Where(p => p.Id == pipelineId && p.PreviewCheckBlocked != reason)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PreviewCheckBlocked, reason), ct)
            .ConfigureAwait(false);
    }

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
