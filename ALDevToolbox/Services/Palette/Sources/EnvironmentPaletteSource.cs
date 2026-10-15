using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Business Central environments, found by their own name and by their
/// customer's: an environment's subtitle leads with its Solution, so
/// <c>cronus prod</c> finds CRONUS's Production without the user having to think
/// about which half of that is which. Enter opens the environment page. See
/// <c>.design/command-palette.md</c>.
///
/// <para><b>The fence.</b> An environment has no visibility of its own - it
/// inherits its solution's - so this reads the environments table <em>through</em>
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>, the way
/// <see cref="UpgradeFleetService"/> does, with the organisation query filter
/// underneath. No <c>IgnoreQueryFilters()</c>.</para>
///
/// <para>Left out, because the page a row lands on would not have them either:
/// environments belonging to a deleted solution, environments Business Central no
/// longer reports (<c>MissingSince</c>), and environments the customer has
/// deleted (<see cref="EnvironmentQueries.NotSoftDeleted"/>). A deleted one is
/// still worth a page - the Environments list has a view of its own for them -
/// but the palette is for going somewhere, and it is not somewhere anyone is
/// heading by typing a name.</para>
/// </summary>
public sealed class EnvironmentPaletteSource : IPaletteSource
{
    /// <summary>
    /// How many environments are projected before ranking decides. As with
    /// solutions this is a ceiling rather than a page size, and for the same
    /// reason there is no <c>ILIKE</c> pre-filter: it would be accent-sensitive
    /// where <see cref="PaletteRanking"/> is not. See
    /// <see cref="PaletteQuery.SqlTerms"/>.
    /// </summary>
    public const int MaxEnvironmentsScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;

    public EnvironmentPaletteSource(AppDbContext db, ProjectAccess access, ToolEnablement tools)
    {
        _db = db;
        _access = access;
        _tools = tools;
    }

    public string Id => "environments";

    public string Label => "Environments";

    public int Order => PaletteGroupOrder.Environments;

    /// <summary>
    /// Exactly the gate <c>/environments</c> has: signed in, and the Solutions
    /// tool switched on for this organisation. Deliberately <em>not</em>
    /// <see cref="ProjectAccess.AccessSnapshot.CanUseEnvironmentOps"/> - that
    /// grant gates Upgrades, where update dates are moved, and the Environments
    /// entry beside it in <c>NavMenu.razor</c> does not ask for it.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true && _tools.IsEnabled(ToolKey.Projects, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        var rows = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.MissingSince == null)
            .Where(EnvironmentQueries.NotSoftDeleted)
            .Where(e => _db.OeProjects.Where(visible)
                .Any(p => p.Id == e.ProjectId && p.DeletedAt == null))
            .OrderBy(e => e.Project!.Name).ThenBy(e => e.Name)
            .Take(MaxEnvironmentsScanned)
            .Select(e => new
            {
                e.Id,
                e.Name,
                e.Type,
                e.Kind,
                e.Version,
                e.Status,
                ProjectName = e.Project!.Name,
                ProjectShortName = e.Project.ShortName,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var candidates = rows.Select(r => new PaletteCandidate(
            "environment",
            r.Name,
            Describe(r.ProjectName, TypeWord(r.Kind, r.Type), r.Version, r.Status),
            $"/environments/{r.Id}",
            // No short name of its own: an environment is not abbreviated, its
            // customer is. The customer's abbreviation is searched instead, so
            // "crocof prod" works, without an environment ever claiming the top
            // hit that an exact short name lifts out of its group.
            ShortName: null,
            SearchOnly: r.ProjectShortName));

        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>
    /// The Solution first - it is what makes <c>cronus prod</c> work and the
    /// thing a reader needs to tell two environments called Production apart -
    /// then the Environments list's own type, version and state.
    ///
    /// <para>A plainly running environment says nothing about its state, as the
    /// list's own rows do not: writing "Running" on almost every row would
    /// bury the one that says something else.</para>
    /// </summary>
    /// <summary>
    /// An on-premises service tier says so: its Sandbox is the customer's Test or
    /// Development server, which "Sandbox" would misname.
    /// </summary>
    internal static string? TypeWord(OeEnvironmentKind kind, string? type) =>
        kind != OeEnvironmentKind.OnPremises ? type
        : string.Equals(type, "Production", StringComparison.OrdinalIgnoreCase) ? "On-premises Production" : "On-premises";

    internal static string? Describe(string? projectName, string? type, string? version, string? status)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(projectName)) parts.Add(projectName.Trim());
        if (!string.IsNullOrWhiteSpace(type)) parts.Add(type.Trim());
        if (!string.IsNullOrWhiteSpace(version)) parts.Add($"BC version {version.Trim()}");
        if (!BcEnvironmentStatus.IsRunning(status)) parts.Add(BcEnvironmentStatus.StatusWord(status));
        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }
}
