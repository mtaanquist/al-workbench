using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Account;

/// <summary>
/// Cross-organisation projection of a user. Returned by
/// <see cref="SiteAdminService.SearchUsersAsync"/> so the SiteAdmin
/// /site-admin/users page can render one row per (user, org) pairing
/// without joining in the page.
/// </summary>
public sealed record SiteAdminUserRow(
    int UserId,
    string Email,
    string DisplayName,
    UserRole Role,
    UserStatus Status,
    bool IsSiteAdmin,
    int OrganizationId,
    string OrganizationName,
    string OrganizationSlug,
    DateTime? LastLoginAt);

/// <summary>
/// One row per audit_log entry, with the organisation's name eagerly joined
/// so the SiteAdmin audit page can render cross-org rows in a single pass.
/// </summary>
public sealed record SiteAdminAuditRow(
    int Id,
    DateTime Timestamp,
    string ChangedBy,
    int? OrganizationId,
    string? OrganizationName,
    AuditEntityType EntityType,
    int EntityId,
    AuditAction Action,
    string? EntityName,
    string? SnapshotJson);

/// <summary>
/// One page of audit rows plus the total match count, so the SiteAdmin
/// audit page can render Previous/Next controls without re-running the
/// query to count.
/// </summary>
public sealed record SiteAdminAuditPage(IReadOnlyList<SiteAdminAuditRow> Rows, int Total);

/// <summary>
/// SiteAdmin (hosting-operator) operations that span organisations. Reads
/// bypass the per-org EF query filter via <c>IgnoreQueryFilters()</c>;
/// mutations call <see cref="RequireSiteAdmin"/> so an org-scoped admin
/// can't escalate by hitting these methods directly.
/// </summary>
public sealed class SiteAdminService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ILogger<SiteAdminService> _logger;

    public SiteAdminService(AppDbContext db, IOrganizationContext orgContext, ILogger<SiteAdminService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _logger = logger;
    }

    /// <summary>
    /// Searches users across every organisation. The query is a
    /// case-insensitive substring match against email and display name; an
    /// empty query returns the most recently signed-in users so the page
    /// has something to show on first load.
    /// </summary>
    public Task<List<SiteAdminUserRow>> SearchUsersAsync(string? query, int limit = 100, CancellationToken ct = default)
    {
        // Defence-in-depth alongside the page's [Authorize] attribute: every other
        // cross-org read in this class guards before escaping the tenant filter,
        // so a future caller wiring this without the attribute can't enumerate
        // every org's users. See issue #376.
        RequireSiteAdmin();
        var trimmed = (query ?? string.Empty).Trim();
        // ILike is Postgres-native and pg_trgm-indexable; ToLower().Contains
        // would force a sequential scan on every email column.
        var pattern = "%" + trimmed + "%";
        // Fence category 2 (SiteAdmin cross-org console): RequireSiteAdmin() above.
        var users = _db.Users.IgnoreQueryFilters().AsNoTracking();
        if (trimmed.Length > 0)
        {
            users = users.Where(u =>
                EF.Functions.ILike(u.Email, pattern)
                || EF.Functions.ILike(u.DisplayName, pattern));
        }

        return users
            .OrderByDescending(u => u.LastLoginAt ?? u.CreatedAt)
            .ThenBy(u => u.Email)
            .Take(limit)
            .Select(u => new SiteAdminUserRow(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role,
                u.Status,
                u.IsSiteAdmin,
                u.OrganizationId,
                u.Organization!.Name,
                u.Organization.Slug,
                u.LastLoginAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Returns the organisation the given email is a member of (at most one since
    /// emails became unique deployment-wide, #1207). Drives the
    /// per-user detail rendering on the SiteAdmin users page when an admin
    /// clicks through from the search results.
    /// </summary>
    public Task<List<SiteAdminUserRow>> GetMembershipsAsync(string email, CancellationToken ct = default)
    {
        // Guard before crossing the tenant fence — see SearchUsersAsync (#376).
        RequireSiteAdmin();
        var normalised = (email ?? string.Empty).Trim().ToLowerInvariant();
        if (normalised.Length == 0) return Task.FromResult(new List<SiteAdminUserRow>());

        // Fence category 2 (SiteAdmin cross-org console): RequireSiteAdmin() above.
        return _db.Users.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => u.Email == normalised)
            .OrderBy(u => u.Organization!.Name)
            .Select(u => new SiteAdminUserRow(
                u.Id,
                u.Email,
                u.DisplayName,
                u.Role,
                u.Status,
                u.IsSiteAdmin,
                u.OrganizationId,
                u.Organization!.Name,
                u.Organization.Slug,
                u.LastLoginAt))
            .ToListAsync(ct);
    }

    /// <summary>Promotes a user to SiteAdmin. The change is audited via the standard interceptor.</summary>
    public async Task PromoteAsync(int userId, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var user = await LoadUserAsync(userId, ct);
        if (!user.IsSiteAdmin)
        {
            user.IsSiteAdmin = true;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Promoted {Email} (user {UserId}) to SiteAdmin.", user.Email, user.Id);
        }
    }

    /// <summary>
    /// Demotes a user from SiteAdmin. Refuses to demote the last remaining
    /// SiteAdmin so the deployment never ends up with no hosting operator.
    /// </summary>
    public async Task DemoteAsync(int userId, CancellationToken ct = default)
    {
        RequireSiteAdmin();
        var user = await LoadUserAsync(userId, ct);
        if (!user.IsSiteAdmin) return;

        // Require at least one OTHER active SiteAdmin to remain. Counting all
        // non-disabled SiteAdmins (the previous shape) misbehaved at the
        // boundary: it includes the subject when they're active, and it
        // counts pending users (who can't actually act) toward the quorum.
        // Fence category 2 (SiteAdmin cross-org console): RequireSiteAdmin() above;
        // SiteAdmins span organisations, so the count has to as well.
        var otherActiveSiteAdmins = await _db.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Id != user.Id
                             && u.IsSiteAdmin
                             && u.Status == UserStatus.Active, ct);
        if (otherActiveSiteAdmins < 1)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["LastSiteAdmin"] = "You can't demote the last active SiteAdmin."
            });
        }

        user.IsSiteAdmin = false;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Demoted {Email} (user {UserId}) from SiteAdmin.", user.Email, user.Id);
    }

    /// <summary>
    /// Cross-organisation audit search, paginated. All filter arguments are
    /// optional; an unfiltered call returns the newest activity across every
    /// organisation. Returns the requested page of rows plus the total match
    /// count so the caller can render Previous/Next controls.
    /// </summary>
    public async Task<SiteAdminAuditPage> SearchAuditAsync(
        AuditEntityType? entityType,
        int? organizationId,
        string? actorContains,
        DateTime? fromUtc,
        DateTime? toUtc,
        int skip = 0,
        int take = 100,
        CancellationToken ct = default)
    {
        RequireSiteAdmin();
        // Fence category 2 (SiteAdmin cross-org console): audit_log gained a
        // tenant query filter in #678; this console exists to search audit
        // activity across every organisation, and access is gated by
        // RequireSiteAdmin() above.
        var q = _db.AuditLog.IgnoreQueryFilters().AsNoTracking().AsQueryable();

        if (entityType is { } et) q = q.Where(e => e.EntityType == et);
        if (organizationId is int oid) q = q.Where(e => e.OrganizationId == oid);
        if (!string.IsNullOrWhiteSpace(actorContains))
        {
            var needle = "%" + actorContains.Trim() + "%";
            q = q.Where(e => EF.Functions.ILike(e.ChangedBy, needle));
        }
        if (fromUtc is { } from) q = q.Where(e => e.Timestamp >= from);
        if (toUtc is { } to) q = q.Where(e => e.Timestamp <= to);

        var total = await q.CountAsync(ct);

        var rows = await q
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, skip))
            .Take(take)
            .Select(e => new SiteAdminAuditRow(
                e.Id,
                e.Timestamp,
                e.ChangedBy,
                e.OrganizationId,
                e.Organization == null ? null : e.Organization.Name,
                e.EntityType,
                e.EntityId,
                e.Action,
                e.EntityName,
                e.SnapshotJson))
            .ToListAsync(ct);

        return new SiteAdminAuditPage(rows, total);
    }

    /// <summary>List of all organisations for the audit search filter dropdown.</summary>
    public Task<List<Organization>> ListOrganizationsAsync(CancellationToken ct = default)
    {
        RequireSiteAdmin();
        return _db.Organizations
            .AsNoTracking()
            .OrderBy(o => o.Name)
            .ToListAsync(ct);
    }

    private async Task<User> LoadUserAsync(int userId, CancellationToken ct)
    {
        // Fence category 2 (SiteAdmin cross-org console): private helper; every public
        // caller calls RequireSiteAdmin() before reaching here.
        var user = await _db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["UserId"] = "User not found." });
        }
        return user;
    }

    private void RequireSiteAdmin() => _orgContext.RequireSiteAdmin();
}
