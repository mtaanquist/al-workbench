using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Explore;

/// <summary>
/// The builds a pipeline build's Release can be compared with, shared by the
/// two pickers that offer them: the Compare picker on the build's release page
/// (<see cref="ObjectExplorerService.ListBuildCompareCandidatesAsync"/>, #1075)
/// and the "Compare with release..." picker on one of its files
/// (<see cref="ReleaseComparisonService.GetCompareTargetsAsync"/>, #1570). One
/// query so the two lists can't disagree on order, labels or cap. See
/// <c>.design/artifacts.md</c>.
/// </summary>
internal static class BuildCompareCandidates
{
    /// <summary>
    /// How many builds a compare picker offers. A busy solution has hundreds;
    /// the recent ones are the ones anyone compares against, and the picker is a
    /// plain dropdown.
    /// </summary>
    internal const int Cap = 50;

    /// <summary>
    /// The other ready builds of the same project as <paramref name="releaseId"/>,
    /// its own pipeline's first, newest first within each, capped at
    /// <see cref="Cap"/>. Every build of a project shares the label
    /// "{project} on BC {version}", so each row's <see cref="ReleaseListItem.Label"/>
    /// is rewritten to name the build and its pipeline. Empty when the release is
    /// not a tracked build or the caller cannot see it.
    /// <para>
    /// <paramref name="withFile"/> keeps only builds holding a file at that
    /// <c>(AppId, Path)</c>, applied before the cap: a pull-request build has no
    /// own pipeline to rank first, and pipelines can build different apps, so
    /// filtering after the cap could fill every slot with builds that lack the
    /// file (#1570). The release page passes none.
    /// </para>
    /// </summary>
    internal static async Task<List<ReleaseListItem>> ListAsync(
        AppDbContext db, ProjectAccess access, int releaseId, CancellationToken ct,
        (Guid AppId, string Path)? withFile = null)
    {
        if (!await access.IsReleaseVisibleAsync(releaseId, ct)) return [];
        var own = await db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .OrderBy(b => b.Id)
            .Select(b => new { b.ProjectId, b.PipelineId })
            .FirstOrDefaultAsync(ct);
        if (own is null) return [];

        var snapshot = await access.GetSnapshotAsync(ct);
        var visibleRelease = access.VisibleReleasePredicate(snapshot);
        var builds = db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ProjectId == own.ProjectId
                        && b.ReleaseId != null
                        && b.ReleaseId != releaseId
                        && b.Status == ProjectBuildStatus.Ready
                        && b.Release!.Status == "ready"
                        && b.Release!.DeletedAt == null
                        // A deleted pipeline's builds go with it, as on the Pipelines pages.
                        && (b.Pipeline == null || b.Pipeline.DeletedAt == null))
            .Where(OeProjectBuild.HasIndexedObjects)
            .Where(b => db.OeReleases.Where(visibleRelease).Any(r => r.Id == b.ReleaseId));
        if (withFile is { } file)
        {
            var (appId, path) = file;
            builds = builds.Where(b => db.OeModuleFiles.Any(f =>
                f.Path == path && f.Module!.AppId == appId && f.Module.ReleaseId == b.ReleaseId));
        }
        var rows = await builds
            .OrderBy(b => own.PipelineId != null && b.PipelineId == own.PipelineId ? 0 : 1)
            .ThenByDescending(b => b.StartedAt)
            .ThenByDescending(b => b.Id)
            .Take(Cap)
            .Select(b => new
            {
                BuildId = b.Id,
                b.BcTarget,
                PipelineName = b.Pipeline != null ? b.Pipeline.Name : null,
                Item = new ReleaseListItem(
                    b.Release!.Id, b.Release.Label, b.Release.Kind, b.Release.Status,
                    b.Release.BcVersion, b.Release.ParentReleaseId,
                    ParentLabel: b.Release.ParentRelease != null ? b.Release.ParentRelease.Label : null,
                    Publisher: b.Release.Publisher,
                    ProjectName: b.Project!.Name,
                    ImportedAt: b.Release.ImportedAt,
                    SourceFileCount: b.Release.SourceFileCount,
                    SourceContentLength: b.Release.SourceContentLength,
                    DeletedAt: b.Release.DeletedAt,
                    StatusMessage: b.Release.StatusMessage,
                    PipelineName: b.Pipeline != null ? b.Pipeline.Name : null,
                    IsPrerelease: b.Release.IsPrerelease),
            })
            .ToListAsync(ct);

        return rows
            .Select(r => r.Item with { Label = Label(r.BuildId, r.BcTarget, r.PipelineName, r.Item.BcVersion) })
            .ToList();
    }

    private static string Label(int buildId, string bcTarget, string? pipelineName, string? bcVersion)
    {
        var label = bcTarget == ProjectBuildTarget.Current
            ? $"Build #{buildId}"
            : $"{ProjectBuildTarget.Label(bcTarget)} preview build #{buildId}";
        if (!string.IsNullOrWhiteSpace(pipelineName)) label += $" of {pipelineName}";
        return string.IsNullOrWhiteSpace(bcVersion) ? label : $"{label} on BC {bcVersion}";
    }
}
