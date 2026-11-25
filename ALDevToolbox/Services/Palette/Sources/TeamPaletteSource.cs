using System.Security.Claims;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Palette.Sources;

/// <summary>
/// Teams, by name - for "who else is on the CRONUS team" and for the manager
/// about to add somebody to it. Enter opens the team's roster. See
/// <c>.design/command-palette.md</c>, "Sources", and issue #885.
///
/// <para><b>Every team in the organisation, not only the caller's.</b> The
/// issue asked for admins and team managers; the pages decide otherwise, and the
/// palette follows the pages. <c>/teams</c> lists the teams you are on, but
/// <c>/teams/{id}</c> opens any team's roster for anyone signed in -
/// membership is not a secret inside an organisation (see
/// <c>TeamDetail.razor</c> and <c>.design/teams-and-visibility.md</c>). So a
/// row here is always one its caller can open, which is the palette's only rule
/// for what may be returned. The subtitle says which teams are the caller's.</para>
///
/// <para><b>The fence.</b> Teams belong to the organisation, not to a Solution,
/// so the organisation query filter is the whole of it. No
/// <c>IgnoreQueryFilters()</c>. A small table, so no pre-filter: the visible
/// rows are projected and <see cref="PaletteRanking"/> decides.</para>
/// </summary>
public sealed class TeamPaletteSource : IPaletteSource
{
    /// <summary>A ceiling on a pathological organisation, not a page size.</summary>
    public const int MaxTeamsScanned = 2000;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;

    public TeamPaletteSource(AppDbContext db, IOrganizationContext orgContext)
    {
        _db = db;
        _orgContext = orgContext;
    }

    public string Id => "teams";

    public string Label => "Teams";

    public int Order => PaletteGroupOrder.Teams;

    /// <summary>
    /// The sidebar's Teams entry and both team pages ask for a signed-in
    /// person and nothing else - no role, no tool toggle - so neither does this.
    /// </summary>
    public Task<bool> IsAvailableAsync(ClaimsPrincipal user, CancellationToken ct) =>
        Task.FromResult(user?.Identity?.IsAuthenticated == true);

    public async Task<IReadOnlyList<PaletteCandidate>> SearchAsync(
        PaletteQuery query, int limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!query.IsUsable || limit <= 0) return [];

        // Null for a request with no signed-in user: no team is "yours" then, and
        // the gate has already refused such a caller anyway.
        var userId = _orgContext.CurrentUserId;

        var rows = await _db.Teams.AsNoTracking()
            .OrderBy(t => t.Name)
            .Take(MaxTeamsScanned)
            .Select(t => new
            {
                t.Id,
                t.Name,
                MemberCount = t.Members.Count,
                IsMine = userId != null && t.Members.Any(m => m.UserId == userId),
            })
            .ToListAsync(ct);

        var candidates = rows.Select(r => new PaletteCandidate(
            "team", r.Name, Describe(r.MemberCount, r.IsMine), $"/teams/{r.Id}"));

        return PaletteRanking.Rank(query, candidates).Take(limit).Select(m => m.Candidate).ToList();
    }

    /// <summary>
    /// The member count in the team page's own words ("3 members"), led by
    /// "Your team" for one the caller is on - the <c>/teams</c> page is
    /// "Your teams", so that is the phrase they already know.
    /// </summary>
    private static string Describe(int memberCount, bool isMine)
    {
        var members = memberCount == 1 ? "1 member" : $"{memberCount} members";
        return isMine ? $"Your team - {members}" : members;
    }
}
