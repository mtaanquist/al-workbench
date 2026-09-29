using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Finds an imported Object Explorer release by its label, its Business Central
/// version, its country, or - for a release a Solution build produced - the
/// Solution's name. "26.0 dk" and "cronus 25.3" are the shapes this exists for.
/// See <c>.design/command-palette.md</c>, "Sources", and issue #883.
///
/// <para><b>A large table, so it pre-filters in SQL.</b> Unlike the sources over
/// solutions and environments, <c>oe_releases</c> grows one row per build and
/// reaches into the thousands, so this narrows with <c>ILIKE</c> per term before
/// <see cref="PaletteRanking"/> decides. The cost is the accent-sensitivity
/// <see cref="PaletteQuery.SqlTerms"/> documents: a release named for "Møller"
/// is found by <c>møller</c> and not by <c>moller</c>.</para>
///
/// <para><b>The fence.</b> One <c>AsNoTracking()</c> read through the
/// organisation query filter, narrowed by
/// <see cref="ProjectAccess.VisibleReleasePredicate"/> - a release a Private
/// solution's build produced is as private as the solution, and so is the
/// solution's name in its subtitle: the predicate is a NOT-EXISTS over the
/// locked solutions linked to the release, so a release that survives it is one
/// no locked solution produced or imported. That is what makes the subtitle's
/// join safe without re-stating the visibility rules a third time. No
/// <c>IgnoreQueryFilters()</c>.</para>
/// </summary>
public sealed class ReleasePaletteSource : IPaletteSource
{
    /// <summary>Status of a release that finished importing - the normal state, so the subtitle says nothing about it.</summary>
    private const string ReadyStatus = "ready";

    /// <summary>Status of a release still being imported. It is offered, and its subtitle says so.</summary>
    private const string IngestingStatus = "ingesting";

    /// <summary>Status of an import that died. A tombstone with no objects in it - never offered.</summary>
    private const string FailedStatus = "failed";

    /// <summary>The <see cref="Domain.Entities.ObjectExplorer.OeRelease.Kind"/> of a release a Solution build produced.</summary>
    private const string ProjectKind = "project";

    /// <summary>
    /// The prefixes on the dedup key of a Microsoft artifact import -
    /// <c>bc-onprem:{Major}.{Minor}:{cc}</c> for a shipped release,
    /// <c>bc-insider:{Major}.{Minor}:{cc}</c> for a preview - the only place a
    /// release's country is recorded as data rather than as part of its label.
    /// See <c>BcArtifactIndex.FormatDedupKey</c>.
    /// </summary>
    private static readonly string[] ArtifactDedupPrefixes =
    {
        ALDevToolbox.Services.ObjectExplorer.Import.BcArtifactIndex.ReleaseDedupPrefix + ":",
        ALDevToolbox.Services.ObjectExplorer.Import.BcArtifactIndex.PreviewDedupPrefix + ":",
    };

    /// <summary>
    /// How many rows to read per row the palette will show. Ranking drops every
    /// candidate that does not match every term, and the <c>ILIKE</c> pre-filter
    /// is looser than ranking is (it matches the dedup key, which nothing
    /// user-visible is built from), so reading exactly the limit would show
    /// fewer rows than the limit.
    /// </summary>
    private const int OverReturnFactor = 4;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;

    public ReleasePaletteSource(AppDbContext db, ProjectAccess access, ToolEnablement tools)
    {
        _db = db;
        _access = access;
        _tools = tools;
    }

    public string Id => "releases";

    public string Label => "Releases";

    public int Order => PaletteGroupOrder.Releases;

    /// <summary>
    /// Exactly the gate on <c>/object-explorer</c>: a signed-in member of the
    /// organisation, with the Object Explorer switched on site-wide and for their
    /// organisation. No role check - the content-authoring roles gate importing a
    /// release, not reading one.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(
            user?.Identity?.IsAuthenticated == true
            && _tools.IsEnabled(ToolKey.ObjectExplorer, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);

        // Deleted releases are gone; failed ones are tombstones holding no
        // objects, so opening one is a dead end. Ingesting ones are offered -
        // the subtitle says they are still coming.
        var rows = _db.OeReleases.AsNoTracking()
            .Where(r => r.DeletedAt == null && r.Status != FailedStatus)
            .Where(_access.VisibleReleasePredicate(snapshot));

        foreach (var term in query.SqlTerms)
        {
            // Declared inside the loop so each predicate captures its own
            // pattern. Escaped against the "\\" escape char so a term with a
            // literal % or _ in it matches literally (#385).
            var pattern = "%" + ObjectSearchService.EscapeLike(term) + "%";
            rows = rows.Where(r =>
                EF.Functions.ILike(r.Label, pattern, "\\")
                || (r.BcVersion != null && EF.Functions.ILike(r.BcVersion, pattern, "\\"))
                // The country lives inside the dedup key of a Microsoft OnPrem
                // import and nowhere else, which is what makes "26.0 dk" work.
                // Matching the whole key over-matches ("onprem" is in every one
                // of them); ranking discards whatever the subtitle does not say.
                || (r.DedupKey != null && EF.Functions.ILike(r.DedupKey, pattern, "\\"))
                // A release a Solution build produced is findable by the
                // Solution's name. Joined rather than read off the denormalised
                // project_name column: that column is stamped at build time and
                // goes stale on a rename, and legacy third-party rows carry one
                // without any solution behind it.
                || _db.OeProjectBuilds.Any(b =>
                    b.ReleaseId == r.Id
                    && b.Project!.DeletedAt == null
                    && EF.Functions.ILike(b.Project.Name, pattern, "\\")));
        }

        // Newest first is the truncation bias, not the display order: if more
        // releases match than we are willing to read, the recent ones are the
        // ones worth ranking. What the user sees is PaletteRanking's order,
        // which breaks ties by title.
        var candidates = await rows
            .OrderByDescending(r => r.Id)
            .Take(limit * OverReturnFactor)
            .Select(r => new ReleaseRow(
                r.Id,
                r.Label,
                r.BcVersion,
                r.DedupKey,
                r.Status,
                r.Kind,
                _db.OeProjectBuilds
                    .Where(b => b.ReleaseId == r.Id && b.Project!.DeletedAt == null)
                    .OrderBy(b => b.Project!.Name)
                    .Select(b => b.Project!.Name)
                    .FirstOrDefault()))
            .ToListAsync(ct).ConfigureAwait(false);

        return candidates
            .Select(row => new PaletteCandidate(
                "release",
                row.Label,
                Subtitle(row),
                $"/object-explorer/release/{row.Id}"))
            .ToList();
    }

    /// <summary>
    /// The line under the label: the Business Central version, the country, the
    /// Solution whose build produced it, and - only when it is not the ordinary
    /// finished state - what the release is still doing. A ready release says
    /// nothing about its status, because every release the user opens is one.
    /// </summary>
    private static string? Subtitle(ReleaseRow row)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(row.BcVersion)) parts.Add(row.BcVersion.Trim());
        if (Country(row.DedupKey) is { } country) parts.Add(country);
        if (!string.IsNullOrWhiteSpace(row.SolutionName)) parts.Add(row.SolutionName.Trim());
        if (StatusNote(row.Status, row.Kind) is { } status) parts.Add(status);

        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }

    /// <summary>
    /// The country code out of a Microsoft artifact import's dedup key
    /// (<c>bc-onprem:28.2:dk</c> or <c>bc-insider:29.0:dk</c> -> <c>DK</c>), or null for every other release -
    /// manual uploads, third-party bundles and Solution builds record no country
    /// at all. Upper-cased to match the label the importer writes,
    /// "Business Central 28.2 (DK)".
    /// </summary>
    private static string? Country(string? dedupKey)
    {
        if (dedupKey is null || !ArtifactDedupPrefixes.Any(p => dedupKey.StartsWith(p, StringComparison.Ordinal))) return null;

        var lastSeparator = dedupKey.LastIndexOf(':');
        if (lastSeparator < 0 || lastSeparator == dedupKey.Length - 1) return null;

        return dedupKey[(lastSeparator + 1)..].ToUpperInvariant();
    }

    /// <summary>
    /// What a release that is not ready is doing, in the words the Object
    /// Explorer's own release lists use. Null for a ready release, and for a
    /// status we do not recognise - a raw status token is a word out of the
    /// database, not something to show a user.
    /// </summary>
    private static string? StatusNote(string status, string kind) => status switch
    {
        ReadyStatus => null,
        // A Solution's release is mid-build (clone, compile, ingest), which is
        // what the person who pressed Build is waiting on.
        IngestingStatus when kind == ProjectKind => "Building...",
        IngestingStatus => "Ingesting...",
        _ => null,
    };

    /// <summary>
    /// The projected read. <paramref name="SolutionName"/> is the current name of
    /// a Solution whose build produced this release, or null when no build did -
    /// and it is safe to show because
    /// <see cref="ProjectAccess.VisibleReleasePredicate"/> has already excluded
    /// every release a solution the caller cannot see is linked to.
    /// </summary>
    private sealed record ReleaseRow(
        int Id,
        string Label,
        string? BcVersion,
        string? DedupKey,
        string Status,
        string Kind,
        string? SolutionName);
}
