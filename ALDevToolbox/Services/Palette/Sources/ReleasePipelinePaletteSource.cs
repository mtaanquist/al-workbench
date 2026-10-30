using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Deployment pipelines - the targets on <c>/pipelines/deployments</c> that install a
/// build into a Business Central environment - found by their own name, their
/// Solution's and their environment's. <c>cronus prod</c> finds the pipeline that
/// deploys to CRONUS's Production. See <c>.design/command-palette.md</c>, "Sources",
/// and issue #885.
///
/// <para>Its group is "Deployment pipelines", the page's own heading. It used to be
/// "Release pipelines", which sat one word from the Object Explorer "Releases" group;
/// the product vocabulary moved to Deployments so the two no longer share a word
/// (.design/saas-delivery.md, "Vocabulary"). The type is still named for the
/// entity, <c>OeReleasePipeline</c>.</para>
///
/// <para><b>A small table, so no pre-filter</b> - a few per Solution. See
/// <see cref="PaletteQuery.SqlTerms"/>.</para>
///
/// <para><b>The fence.</b> A deployment pipeline inherits its Solution's
/// visibility, exactly as <see cref="ReleasePipelineService.ListReleasePipelinesAsync"/>
/// reads it for <c>/pipelines/deployments</c>: through
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>, with the organisation
/// query filter underneath. No <c>IgnoreQueryFilters()</c>.</para>
///
/// <para>A pipeline aimed at an environment that has gone or failed is still
/// offered - the Deployment pipelines page lists it too - and its subtitle says what is
/// wrong, in that page's words, because a pipeline that cannot deploy is the
/// one somebody is most likely looking for.</para>
/// </summary>
public sealed class ReleasePipelinePaletteSource : IPaletteSource
{
    /// <summary>A ceiling on a pathological organisation, not a page size.</summary>
    public const int MaxReleasePipelinesScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;

    public ReleasePipelinePaletteSource(AppDbContext db, ProjectAccess access, ToolEnablement tools)
    {
        _db = db;
        _access = access;
        _tools = tools;
    }

    public string Id => "release-pipelines";

    public string Label => "Deployment pipelines";

    public int Order => PaletteGroupOrder.ReleasePipelines;

    /// <summary>
    /// Exactly the gate on the sidebar's Deployments entry: signed in, and the
    /// deployment pipelines tool (<see cref="ToolKey.Releases"/>) switched on
    /// site-wide and for this organisation.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true && _tools.IsEnabled(ToolKey.Releases, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var rows = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.DeletedAt == null && r.ArchivedAt == null)
            .Where(r => _db.OeProjects.Where(visible)
                .Any(v => v.Id == r.ProjectId && v.DeletedAt == null))
            .OrderBy(r => r.Project!.Name).ThenBy(r => r.Name)
            .Take(MaxReleasePipelinesScanned)
            .Select(r => new
            {
                r.Id,
                r.Name,
                ProjectName = r.Project!.Name,
                ProjectShortName = r.Project.ShortName,
                EnvironmentName = r.ProjectEnvironment!.Name,
                EnvironmentMissing = r.ProjectEnvironment.MissingSince != null,
                EnvironmentStatus = r.ProjectEnvironment.Status,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var candidates = rows.Select(r => new PaletteCandidate(
            "release-pipeline",
            r.Name,
            Describe(r.ProjectName, r.EnvironmentName,
                ReleasePipelineRow.DescribeEnvironmentProblem(r.EnvironmentMissing, r.EnvironmentStatus)),
            $"/pipelines/deployments/{r.Id}",
            ShortName: null,
            SearchOnly: r.ProjectShortName));

        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>
    /// The Solution, then the target environment, then - only when there is
    /// one - what is wrong with that environment ("no longer present", "failed
    /// in Business Central"), the words the Deployment pipelines list prints beside it.
    /// </summary>
    private static string? Describe(string projectName, string? environmentName, string? problem)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(projectName)) parts.Add(projectName.Trim());
        if (!string.IsNullOrWhiteSpace(environmentName)) parts.Add(environmentName.Trim());
        if (problem is not null) parts.Add(problem);
        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }
}
