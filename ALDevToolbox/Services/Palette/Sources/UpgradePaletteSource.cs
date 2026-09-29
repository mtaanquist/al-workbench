using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Planned upgrades (#984), found by their name and their target release: <c>november</c>
/// or <c>28.5</c> finds "28.5 in November 2026". The subtitle is the target and the
/// upgrade's status in the page's words ("to 28.5 - In progress"). Enter opens
/// <c>/upgrades/{id}</c>. Open upgrades and the archive are both offered, open first. See
/// <c>.design/command-palette.md</c>, "Sources", and
/// <c>.design/environment-updates.md</c>, "Planned upgrades: a header with lines".
///
/// <para><b>The fence.</b> An upgrade's header belongs to the organisation, not to a
/// solution - the Upgrades page lists every one of them to anyone with the
/// environment-updates grant, as <see cref="EnvironmentUpgradeService.ListOpenAsync"/>
/// does - so the header read needs nothing beyond the organisation query filter. What
/// hangs off a solution is the lines, and nothing about a line reaches a row except the
/// status, which <see cref="EnvironmentUpgradeService"/> derives from the lines the caller
/// can see. No <c>IgnoreQueryFilters()</c>.</para>
///
/// <para><b>One query, and a second only when it pays.</b> The status of an open upgrade
/// is derived from its lines and the fleet, which is more than a palette keystroke should
/// read for rows nobody asked about. So the headers are projected and ranked on their name
/// and target first; only when an open upgrade survives into the rows returned is the
/// open list read for its status. A done upgrade is "Done" whatever its lines say, so
/// the archive never costs the second read. The consequence: "done" is searchable, the
/// open statuses are not - typing "in progress" does not find the open waves. The
/// Upgrades page's Open view is the place for that question.</para>
/// </summary>
public sealed class UpgradePaletteSource : IPaletteSource
{
    /// <summary>
    /// How many upgrades are projected before ranking decides. A ceiling on a
    /// pathological organisation, not a page size: a team makes a few a release.
    /// </summary>
    public const int MaxUpgradesScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly EnvironmentUpgradeService _upgrades;

    public UpgradePaletteSource(AppDbContext db, ProjectAccess access, EnvironmentUpgradeService upgrades)
    {
        _db = db;
        _access = access;
        _upgrades = upgrades;
    }

    public string Id => "upgrades";

    public string Label => "Upgrades";

    public int Order => PaletteGroupOrder.Upgrades;

    /// <summary>
    /// Exactly the gate on the sidebar's Upgrades entry: signed in, and the
    /// environment-updates grant on at least one team
    /// (<see cref="ProjectAccess.AccessSnapshot.CanUseEnvironmentOps"/>). No tool toggle -
    /// Upgrades is its own grant and stays reachable when Solutions is switched off.
    /// </summary>
    public async Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        return snapshot.CanUseEnvironmentOps;
    }

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        var rows = await _db.OeEnvironmentUpgrades.AsNoTracking()
            .OrderBy(u => u.ClosedAt != null)
            .ThenByDescending(u => u.ClosedAt)
            .ThenByDescending(u => u.CreatedAt)
            .Take(MaxUpgradesScanned)
            .Select(u => new { u.Id, u.Name, u.TargetVersion, IsClosed = u.ClosedAt != null })
            .ToListAsync(ct).ConfigureAwait(false);

        var kept = PaletteRanking.Rank(query, rows.Select(r => new PaletteCandidate(
                "upgrade",
                r.Name,
                Describe(r.TargetVersion, r.IsClosed ? UpgradeStatus.Done : null),
                $"/upgrades/{r.Id}")))
            .Take(limit)
            .Select(m => m.Candidate)
            .ToList();

        var openIds = rows.Where(r => !r.IsClosed).Select(r => $"/upgrades/{r.Id}").ToHashSet();
        if (!kept.Any(c => openIds.Contains(c.Href))) return kept;

        var open = (await _upgrades.ListOpenAsync(ct).ConfigureAwait(false))
            .ToDictionary(u => $"/upgrades/{u.Id}");
        return kept
            .Select(c => open.TryGetValue(c.Href, out var u)
                ? c with { Subtitle = Describe(u.TargetVersion, u.Status) }
                : c)
            .ToList();
    }

    /// <summary>
    /// "to 28.5 - Planned": the target release, then the status as the Upgrades page words
    /// it. The status is left off when it is not known yet (an open row before the second
    /// read), so a row never claims a status it has not been told.
    /// </summary>
    internal static string Describe(string targetVersion, UpgradeStatus? status)
    {
        var target = $"to {targetVersion}";
        return status is { } s ? $"{target} - {StatusWord(s)}" : target;
    }

    /// <summary>The status as a person reads it; the enum's own names run words together.</summary>
    internal static string StatusWord(UpgradeStatus status) => status switch
    {
        UpgradeStatus.Planned => "Planned",
        UpgradeStatus.InProgress => "In progress",
        UpgradeStatus.Updated => "Updated",
        UpgradeStatus.Done => "Done",
        _ => status.ToString(),
    };
}
