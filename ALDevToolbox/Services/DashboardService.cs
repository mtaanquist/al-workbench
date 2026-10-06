using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// A count and the timestamp that says how fresh it is. The dashboard
/// archetype's cue tile always pairs the two - see the design system's own
/// note, "every cue carries a 'last activity' line so a count always says how
/// fresh it is" - so a bare number never ships without one.
/// </summary>
/// <param name="Count">How many rows there are.</param>
/// <param name="At">Newest change among them; <c>null</c> when there are none.</param>
public sealed record CountWithStamp(int Count, DateTime? At)
{
    public static readonly CountWithStamp Empty = new(0, null);
}

/// <summary>
/// Work sitting in a queue waiting for an admin to act on it. Carries one
/// representative item as well as the count, because "3 waiting" and "3
/// waiting, the first for eleven days" are different situations.
/// </summary>
/// <param name="Count">How many are waiting.</param>
/// <param name="At">
/// When the item named by <paramref name="Label"/> arrived. Which item that is
/// depends on the queue and is the caller's decision: for an approval queue it
/// is the one that has waited longest, for failures it is the most recent,
/// because the useful thing about a failure is the one that just happened.
/// </param>
/// <param name="Label">Something identifying it - an email, a title.</param>
public sealed record PendingQueue(int Count, DateTime? At, string? Label)
{
    public static readonly PendingQueue Empty = new(0, null, null);

    public bool Any => Count > 0;
}

/// <summary>
/// Everything the <c>/admin</c> dashboard reads in one shot. Deliberately
/// numbers and timestamps only - the cue labels and attention-row wording live
/// in the page, where a copy edit doesn't mean touching a service.
/// </summary>
public sealed record AdminDashboardData(
    PendingQueue Signups,
    PendingQueue RecipeSuggestions,
    PendingQueue ExpiredInvites,
    PendingQueue FailedImports,
    CountWithStamp Users,
    CountWithStamp Templates,
    CountWithStamp Modules,
    CountWithStamp Recipes,
    CountWithStamp ApplicationVersions,
    CountWithStamp CatalogEntries);

/// <summary>
/// What a tool holds, for the meta line under its tile on the home launcher.
/// Only the tools that are useless while empty are here: a user who opens
/// Object Explorer with nothing imported has wasted the click, and the tile is
/// the last place we can say so.
/// </summary>
public sealed record ToolCounts(int Templates, int Recipes, int Releases, int Projects)
{
    public static readonly ToolCounts Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Read-side counts behind the two landing pages - <c>/admin</c> (dashboard
/// archetype 4) and <c>/</c> (launcher archetype 1). Every query rides the
/// standard EF tenant filter, so this reads one organisation's rows and
/// nothing else; nothing here calls <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class DashboardService
{
    /// <summary>
    /// <see cref="Domain.Entities.ObjectExplorer.OeRelease.Kind"/> for a pipeline
    /// build. Those rows share the releases table with real imports but are a
    /// different tool's business; see <see cref="FailedImportsAsync"/>.
    /// </summary>
    private const string ProjectBuildKind = "project";

    private readonly AppDbContext _db;

    public DashboardService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Everything <c>/admin</c> renders. Around a dozen small indexed counts;
    /// they run sequentially because the DbContext is not thread-safe, and the
    /// tables involved are all organisation-sized rather than log-sized.
    /// </summary>
    public async Task<AdminDashboardData> GetAdminDashboardAsync(CancellationToken ct = default)
    {
        var signups = await PendingSignupsAsync(ct);
        var suggestions = await PendingSuggestionsAsync(ct);
        var expiredInvites = await ExpiredInvitesAsync(ct);
        var failedImports = await FailedImportsAsync(ct);

        // Disabled accounts are still accounts an admin manages, so the Users
        // cue counts them; only the never-approved Pending rows are excluded,
        // since those are the signups queue above and would be counted twice.
        var users = await CountAndLatestAsync(
            _db.Users.AsNoTracking()
                .Where(u => u.Status != UserStatus.Pending)
                .Select(u => u.LastLoginAt),
            ct);

        // Admin lists show deprecated rows (only end-user pickers hide them),
        // so these counts match what the admin sees when they follow the cue.
        var templates = await CountAndLatestAsync(
            _db.RuntimeTemplates.AsNoTracking().Where(t => t.DeletedAt == null).Select(t => t.UpdatedAt), ct);
        var modules = await CountAndLatestAsync(
            _db.Modules.AsNoTracking().Where(m => m.DeletedAt == null).Select(m => m.UpdatedAt), ct);
        var recipes = await CountAndLatestAsync(
            _db.Recipes.AsNoTracking().Where(r => r.DeletedAt == null).Select(r => r.UpdatedAt), ct);
        var appVersions = await CountAndLatestAsync(
            _db.ApplicationVersions.AsNoTracking().Where(v => v.DeletedAt == null).Select(v => v.UpdatedAt), ct);
        var catalog = await CountAndLatestAsync(
            _db.WellKnownDependencies.AsNoTracking().Select(d => d.UpdatedAt), ct);

        return new AdminDashboardData(
            signups, suggestions, expiredInvites, failedImports,
            users, templates, modules, recipes, appVersions, catalog);
    }

    /// <summary>
    /// Counts for the home launcher's tile meta lines. Four small counts, run on
    /// every visit to <c>/</c> - keep it that cheap.
    /// </summary>
    public async Task<ToolCounts> GetToolCountsAsync(CancellationToken ct = default)
    {
        // Each count matches the page its tile links to, which is the only thing
        // that makes the number checkable. /templates lists deprecated rows with
        // a badge, so they count here; /cookbook hides them, so they do not.
        var templates = await _db.RuntimeTemplates.AsNoTracking()
            .CountAsync(t => t.DeletedAt == null, ct);
        var recipes = await _db.Recipes.AsNoTracking()
            .CountAsync(r => r.DeletedAt == null && !r.Deprecated, ct);
        // Only a "ready" release can actually be browsed - promising one that is
        // still importing or has failed is a lie the user finds out one click
        // later. Pipeline builds are excluded for the same reason: they are
        // oe_releases rows too, but /object-explorer does not list them (they
        // live in the Artifacts tool), and there is one per build, so counting
        // them would drift further from what the page shows every day. Feed symbol
        // packages with no source are left off the page, so off the count (#1092).
        var releases = await _db.OeReleases.AsNoTracking()
            .CountAsync(r => r.DeletedAt == null && r.Status == "ready" && r.Kind != ProjectBuildKind
                && !(r.DedupKey != null && r.DedupKey.StartsWith(OeRelease.SymbolFeedDedupPrefix) && r.SourceFileCount == 0), ct);
        var projects = await _db.OeProjects.AsNoTracking()
            .CountAsync(p => p.DeletedAt == null, ct);
        return new ToolCounts(templates, recipes, releases, projects);
    }

    private async Task<PendingQueue> PendingSignupsAsync(CancellationToken ct)
    {
        var q = _db.SignupRequests.AsNoTracking().Where(r => r.Decision == SignupDecision.Pending);
        var count = await q.CountAsync(ct);
        if (count == 0) return PendingQueue.Empty;
        var oldest = await q.OrderBy(r => r.RequestedAt)
            .Select(r => new { r.RequestedAt, r.Email })
            .FirstOrDefaultAsync(ct);
        return oldest is null ? PendingQueue.Empty : new PendingQueue(count, oldest.RequestedAt, oldest.Email);
    }

    private async Task<PendingQueue> PendingSuggestionsAsync(CancellationToken ct)
    {
        var q = _db.RecipeSuggestions.AsNoTracking()
            .Where(s => s.Decision == RecipeSuggestionDecision.Pending);
        var count = await q.CountAsync(ct);
        if (count == 0) return PendingQueue.Empty;
        var oldest = await q.OrderBy(s => s.RequestedAt)
            .Select(s => new { s.RequestedAt, s.Title })
            .FirstOrDefaultAsync(ct);
        return oldest is null ? PendingQueue.Empty : new PendingQueue(count, oldest.RequestedAt, oldest.Title);
    }

    /// <summary>
    /// Invitations that ran out before anyone accepted them. Someone was meant
    /// to get in and never did, and nothing else in the app ever says so -
    /// the invite list shows them as expired but nobody opens it unprompted.
    /// </summary>
    private async Task<PendingQueue> ExpiredInvitesAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var q = _db.Invites.AsNoTracking()
            .Where(i => i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt <= now);
        var count = await q.CountAsync(ct);
        if (count == 0) return PendingQueue.Empty;
        var oldest = await q.OrderBy(i => i.ExpiresAt)
            .Select(i => new { i.ExpiresAt, i.Email })
            .FirstOrDefaultAsync(ct);
        return oldest is null ? PendingQueue.Empty : new PendingQueue(count, oldest.ExpiresAt, oldest.Email);
    }

    /// <summary>
    /// Release imports that ended in failure and have not been cleared away.
    /// The Object Explorer admin page shows these, but only if you go looking;
    /// a failed import leaves the release list quietly incomplete.
    ///
    /// <para>
    /// <c>oe_releases</c> is not only imports: <c>ProjectBuildImporter</c> stamps
    /// a row with <c>Kind = "project"</c> for every pipeline build, through the
    /// same queue and the same kind-agnostic failure path. Those belong to
    /// whoever ran the build and already carry a status in Pipelines, so they
    /// are excluded here - otherwise an org with active pipelines would have a
    /// permanent red row on the dashboard reading "a release import failed".
    /// <c>ObjectExplorerService</c> draws the same line for the same reason.
    /// A failed C/AL import stays, because an admin is the one who started it.
    /// </para>
    /// </summary>
    private async Task<PendingQueue> FailedImportsAsync(CancellationToken ct)
    {
        var q = _db.OeReleases.AsNoTracking()
            .Where(r => r.DeletedAt == null && r.Status == "failed" && r.Kind != ProjectBuildKind);
        var count = await q.CountAsync(ct);
        if (count == 0) return PendingQueue.Empty;
        // Newest first, unlike the approval queues: what an admin wants from a
        // failure is the one that just happened.
        var newest = await q.OrderByDescending(r => r.UpdatedAt)
            .Select(r => new { r.UpdatedAt, r.Label })
            .FirstOrDefaultAsync(ct);
        return newest is null ? PendingQueue.Empty : new PendingQueue(count, newest.UpdatedAt, newest.Label);
    }

    /// <summary>
    /// Count plus newest timestamp over an already-projected column. Taking the
    /// projection rather than the entity keeps every call site one readable
    /// line and avoids hand-composing expression trees for the aggregate.
    /// </summary>
    private static async Task<CountWithStamp> CountAndLatestAsync(IQueryable<DateTime> stamps, CancellationToken ct)
    {
        var count = await stamps.CountAsync(ct);
        if (count == 0) return CountWithStamp.Empty;
        // Cast to DateTime? rather than relying on the count guard: the two
        // queries are separate round-trips, so the last matching row can be
        // deleted in between, and Max over an empty set would then throw
        // "Sequence contains no elements" and take the whole page with it.
        // A count that is one stale beats a 500.
        return new CountWithStamp(count, await stamps.Select(x => (DateTime?)x).MaxAsync(ct));
    }

    /// <summary>Nullable-column overload; <c>Max</c> over all-nulls is null, which is the honest answer.</summary>
    private static async Task<CountWithStamp> CountAndLatestAsync(IQueryable<DateTime?> stamps, CancellationToken ct)
    {
        var count = await stamps.CountAsync(ct);
        if (count == 0) return CountWithStamp.Empty;
        return new CountWithStamp(count, await stamps.MaxAsync(ct));
    }
}
