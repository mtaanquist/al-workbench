using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Tools;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Solutions, by the things a person says out loud about a customer: the name,
/// the short name the team uses, and - because this source is also the palette's
/// answer to "find me the customer" - a contact's name or company, the Voice
/// account number and the Business Central tenant id. Whatever matched, the row
/// is the Solution and Enter opens it. See <c>.design/command-palette.md</c>.
///
/// <para><b>The fence.</b> One <c>AsNoTracking()</c> read through the
/// organisation query filter and
/// <see cref="ProjectAccess.VisibleProjectPredicate"/>; no
/// <c>IgnoreQueryFilters()</c>. The customer-field matches are variants of the
/// same projected row, so they cannot reach a solution the caller could not
/// already see - there is no second query for them to escape through.</para>
///
/// <para><b>Personal data.</b> A contact may be matched on and named by name and
/// company. Their phone number and email address are never searched and never
/// projected, so they cannot be printed by mistake. The Voice account number and
/// the tenant id are matched on through
/// <see cref="PaletteCandidate.SearchOnly"/>, which never leaves the server: the
/// row names the field that matched, not the value.</para>
/// </summary>
public sealed class SolutionPaletteSource : IPaletteSource
{
    /// <summary>
    /// How many solutions are projected before ranking decides. Not a pre-filter:
    /// a few hundred human-typed names per organisation is one small indexed
    /// read, and filtering in SQL would be accent-sensitive (there is no
    /// <c>unaccent</c> here) where <see cref="PaletteRanking"/> is not. See
    /// <see cref="PaletteQuery.SqlTerms"/>. The cap is a ceiling on a pathological
    /// organisation, not a page size.
    /// </summary>
    public const int MaxSolutionsScanned = 2000;

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ToolEnablement _tools;

    public SolutionPaletteSource(AppDbContext db, ProjectAccess access, ToolEnablement tools)
    {
        _db = db;
        _access = access;
        _tools = tools;
    }

    public string Id => "solutions";

    public string Label => "Solutions";

    public int Order => PaletteGroupOrder.Solutions;

    /// <summary>
    /// Exactly the gate <c>/solutions</c> has: signed in, and the Solutions tool
    /// switched on for this organisation - the two lines
    /// <c>NavMenu.razor</c> runs before it draws the entry. The visibility rules
    /// in <see cref="SearchAsync"/> do the rest per row.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true && _tools.IsEnabled(ToolKey.Projects, user));

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var snapshot = await _access.GetSnapshotAsync(ct);

        // Contacts ride along as a correlated projection rather than a second
        // read: sources share the request's DbContext and run one at a time, so
        // a round trip saved here is a round trip off the palette's budget.
        // Email and phone are deliberately absent from the projection.
        var rows = await _db.OeProjects.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(ProjectAccess.VisibleProjectPredicate(snapshot))
            .OrderBy(p => p.Name)
            .Take(MaxSolutionsScanned)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.ShortName,
                p.HostingType,
                p.BcVersion,
                p.VoiceAccountNumber,
                p.BcTenantId,
                Contacts = _db.OeProjectContacts
                    .Where(c => c.ProjectId == p.Id)
                    .OrderBy(c => c.Name)
                    .Select(c => new { c.Name, c.Company })
                    .ToList(),
            })
            .ToListAsync(ct);

        // The version in the subtitle is the one the Solutions list shows: Business
        // Central's own, for a connected customer with a production environment. A second
        // small read rather than a correlated one, so the rule lives in one place; it is
        // the organisation's connected solutions only, and is only ever looked up for a
        // row the read above already let through.
        var production = rows.Count == 0
            ? new Dictionary<int, ProductionEnvironmentFacts>()
            : await ProjectCustomerInfoService.ReadProductionFactsAsync(_db, null, ct);

        var candidates = new List<PaletteCandidate>(rows.Count);
        foreach (var row in rows)
        {
            var variants = new List<PaletteCandidate>(2 + (row.Contacts.Count * 2))
            {
                // The plain row first, so a solution found by its own name is
                // described by its hosting rather than by whichever contact
                // happened to match too.
                new("solution", row.Name,
                    Describe(row.ShortName, row.HostingType, production.TryGetValue(row.Id, out var facts) ? facts.Version : row.BcVersion),
                    Href(row.Id), row.ShortName),
            };

            foreach (var contact in row.Contacts)
            {
                if (string.IsNullOrWhiteSpace(contact.Name)) continue;
                variants.Add(new PaletteCandidate(
                    "solution", row.Name, $"contact: {contact.Name.Trim()}", Href(row.Id), row.ShortName));
                if (!string.IsNullOrWhiteSpace(contact.Company))
                {
                    // A second variant rather than always naming the company:
                    // the company belongs on the row when it is why the row is
                    // there, and is noise when it is not.
                    variants.Add(new PaletteCandidate(
                        "solution", row.Name,
                        $"contact: {contact.Name.Trim()} at {contact.Company.Trim()}", Href(row.Id), row.ShortName));
                }
            }

            if (!string.IsNullOrWhiteSpace(row.VoiceAccountNumber))
            {
                variants.Add(new PaletteCandidate(
                    "solution", row.Name, "Voice account number", Href(row.Id), row.ShortName,
                    SearchOnly: row.VoiceAccountNumber));
            }

            if (row.BcTenantId is { } tenantId)
            {
                variants.Add(new PaletteCandidate(
                    "solution", row.Name, "Tenant ID", Href(row.Id), row.ShortName,
                    SearchOnly: tenantId.ToString("D")));
            }

            if (Best(query, variants) is { } candidate) candidates.Add(candidate);
        }

        // Ranked here, not just filtered: the service asks for more candidates
        // than it shows and truncating before ranking would be able to drop the
        // best match. See .design/command-palette.md, "Matching and ranking".
        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    private static string Href(int projectId) => $"/solutions/{projectId}";

    /// <summary>
    /// The one variant of a solution's row that gets shown: the best-matching
    /// one, and among equals the first offered - which is why the plain row is
    /// built first. A solution that matched at all matches exactly once, so a
    /// customer with six contacts is one result rather than seven.
    ///
    /// <para>This is the source deciding <em>which of its own fields explains a
    /// row</em>, not scoring it: <see cref="PaletteRanking"/> still decides what
    /// matched and how well, here and again in the service.</para>
    /// </summary>
    private static PaletteCandidate? Best(PaletteQuery query, List<PaletteCandidate> variants)
    {
        PaletteCandidate? best = null;
        PaletteMatchTier? bestTier = null;

        foreach (var variant in variants)
        {
            if (PaletteRanking.Match(query, variant) is not { } tier) continue;
            if (bestTier is null || tier < bestTier)
            {
                best = variant;
                bestTier = tier;
            }
            // Nothing beats the top hit, so there is no point costing a fold per
            // contact once one has been found.
            if (bestTier == PaletteMatchTier.ExactShortName) break;
        }

        return best;
    }

    /// <summary>
    /// The subtitle of a solution found by its name: the short name it is known
    /// by, then the Solutions list's own "Hosted by" and "BC version" columns.
    /// Null when the solution has none of the three, rather than an empty line.
    /// </summary>
    internal static string? Describe(string? shortName, ProjectHostingType? hosting, string? bcVersion)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(shortName)) parts.Add(shortName.Trim());
        if (hosting is { } h) parts.Add(ProjectHostingText.Short(h));
        if (!string.IsNullOrWhiteSpace(bcVersion)) parts.Add(bcVersion.Trim());
        return parts.Count == 0 ? null : string.Join(" - ", parts);
    }
}
