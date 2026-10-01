using System.Security.Claims;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Build pipelines, found by their own name and by their Solution's: a
/// pipeline's subtitle leads with the Solution, so <c>cronus prod</c> finds
/// CRONUS's Production pipeline the same way it finds CRONUS's Production
/// environment. Enter opens the pipeline's build history. See
/// <c>.design/command-palette.md</c>, "Sources", and issue #885.
///
/// <para><b>A small table, so no pre-filter.</b> An organisation has a handful
/// of pipelines per Solution, so this projects the visible ones and lets
/// <see cref="PaletteRanking"/> decide, which folds accents where an
/// <c>ILIKE</c> would not. See <see cref="PaletteQuery.SqlTerms"/>.</para>
///
/// <para><b>The fence.</b> A pipeline has no visibility of its own - it
/// inherits its Solution's, exactly as <c>ArtifactService.ListPipelinesAsync</c>
/// reads it for <c>/pipelines</c> - so the read goes through
/// <see cref="ProjectAccess.VisibleProjectPredicate"/> with the organisation
/// query filter underneath. No <c>IgnoreQueryFilters()</c>.</para>
///
/// <para>Builds are not rows of their own. A build found by its number or its
/// branch would need a second shape of row and a second landing page, which is
/// the next slice of #885, not this one; the pipeline's latest build is what
/// its subtitle reports.</para>
/// </summary>
public sealed class PipelinePaletteSource : IPaletteSource
{
    /// <summary>
    /// How many pipelines are projected before ranking decides. A ceiling on a
    /// pathological organisation, not a page size - see
    /// <see cref="SolutionPaletteSource.MaxSolutionsScanned"/> for the reasoning.
    /// </summary>
    public const int MaxPipelinesScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;

    public PipelinePaletteSource(AppDbContext db, ProjectAccess access, ToolEnablement tools)
    {
        _db = db;
        _access = access;
        _tools = tools;
    }

    public string Id => "pipelines";

    public string Label => "Build pipelines";

    public int Order => PaletteGroupOrder.Pipelines;

    /// <summary>
    /// Exactly the gate on the sidebar's Pipelines entry: signed in, and the
    /// Pipelines tool switched on site-wide and for this organisation. No role
    /// check - any signed-in member may browse pipelines and download builds.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true && _tools.IsEnabled(ToolKey.Pipelines, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        // The latest build rides along as a correlated projection rather than a
        // second read: sources share the request's DbContext and run one at a
        // time, so a round trip saved here is one off the palette's budget.
        var rows = await _db.OePipelines.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(p => _db.OeProjects.Where(visible)
                .Any(v => v.Id == p.ProjectId && v.DeletedAt == null))
            .OrderBy(p => p.Project!.Name).ThenBy(p => p.Name)
            .Take(MaxPipelinesScanned)
            .Select(p => new
            {
                p.Id,
                p.Name,
                ProjectName = p.Project!.Name,
                ProjectShortName = p.Project.ShortName,
                Latest = _db.OeProjectBuilds
                    .Where(b => b.PipelineId == p.Id && b.BcTarget == ProjectBuildTarget.Current)
                    .OrderByDescending(b => b.StartedAt)
                    .Select(b => new { b.Status, b.StartedAt, b.FinishedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var candidates = rows.Select(r => new PaletteCandidate(
            "pipeline",
            r.Name,
            Describe(r.ProjectName, r.Latest?.Status, r.Latest?.FinishedAt ?? r.Latest?.StartedAt),
            $"/pipelines/{r.Id}",
            // The Solution's abbreviation is searched, not claimed: a pipeline
            // must never take the top hit an exact short name lifts out of its
            // group - that belongs to the Solution itself.
            ShortName: null,
            SearchOnly: r.ProjectShortName));

        // Ranked here, not just filtered: truncating an unranked projection could
        // drop the best match. See .design/command-palette.md, "Matching and ranking".
        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>
    /// The Solution, then the latest build in the Pipelines list's own words -
    /// its "When" column ("Built 2 days ago", "Failed 1 hour ago") or its
    /// "No builds yet".
    /// </summary>
    private static string Describe(string projectName, string? status, DateTime? when)
    {
        var latest = status is null || when is null
            ? "No builds yet"
            : $"{Verb(status)} {RelativeTime.Ago(when.Value)}";
        return string.IsNullOrWhiteSpace(projectName) ? latest : $"{projectName.Trim()} - {latest}";
    }

    /// <summary>The verbs <c>PipelinesBrowser.razor</c> puts in front of a build's time.</summary>
    private static string Verb(string status) => status switch
    {
        ProjectBuildStatus.Ready => "Built",
        ProjectBuildStatus.Failed => "Failed",
        ProjectBuildStatus.Building => "Started",
        _ => "Queued",
    };
}
