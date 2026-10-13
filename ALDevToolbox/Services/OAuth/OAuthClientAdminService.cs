using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Read + revoke surface for OAuth clients registered against the deployment.
/// Wraps OpenIddict's <see cref="IOpenIddictApplicationManager"/> so the
/// admin / site-admin / per-user list pages don't each reinvent the same
/// pagination and revocation logic. Consent rows
/// (<see cref="OAuthConsent"/>) live in our own table and are joined on
/// <see cref="ConnectedClient.ClientId"/>.
///
/// All reads use <c>IgnoreQueryFilters()</c> internally — OpenIddict's
/// tables are outside the multi-tenant query filter because pre-auth flows
/// must read them before any org context exists. Callers are responsible
/// for enforcing their own scope (per-user, per-org, or site-admin).
/// </summary>
public sealed class OAuthClientAdminService
{
    private readonly IOpenIddictApplicationManager _applications;
    private readonly IOpenIddictTokenManager _tokens;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<OAuthClientAdminService> _logger;

    public OAuthClientAdminService(
        IOpenIddictApplicationManager applications,
        IOpenIddictTokenManager tokens,
        AppDbContext db,
        TimeProvider clock,
        ILogger<OAuthClientAdminService> logger)
    {
        _applications = applications;
        _tokens = tokens;
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Lists every OAuth client this user has ever consented to. The user
    /// can revoke a consent here — the OAuth client itself stays registered
    /// (other users might have it too) but the user's own tokens are killed.
    /// </summary>
    public async Task<IReadOnlyList<ConnectedClient>> ListForUserAsync(int userId, CancellationToken cancellationToken = default)
    {
        var consents = await _db.OAuthConsents
            // Fence category 4 (explicitly scoped user-id lookup): pinned to c.UserId == userId,
            // which /account passes from the signed-in user.
            .IgnoreQueryFilters()
            .Where(c => c.UserId == userId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return await JoinConsentsWithApplicationsAsync(consents, cancellationToken);
    }

    /// <summary>
    /// Lists every consent in the organisation — what the org admin uses to
    /// see which assistants the team has connected. The view is read-only
    /// per user, with revoke acting only on that consent row.
    /// </summary>
    public async Task<IReadOnlyList<ConnectedClient>> ListForOrganizationAsync(int organizationId, CancellationToken cancellationToken = default)
    {
        var consents = await _db.OAuthConsents
            // Fence category 4 (explicitly scoped org-id lookup): pinned to
            // c.OrganizationId == organizationId, passed by the admin page from its own context.
            .IgnoreQueryFilters()
            .Where(c => c.OrganizationId == organizationId)
            .Include(c => c.User)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return await JoinConsentsWithApplicationsAsync(consents, cancellationToken);
    }

    /// <summary>
    /// Lists every consent in every org. SiteAdmin only — caller must
    /// confirm authorisation. The application column is filled in so the
    /// site-admin can see DCR / CIMD provenance per row.
    /// </summary>
    public async Task<IReadOnlyList<ConnectedClient>> ListAllAsync(CancellationToken cancellationToken = default)
    {
        var consents = await _db.OAuthConsents
            // Fence category 2 (SiteAdmin cross-org console): only caller is
            // /site-admin/oauth-clients, gated by [Authorize(Roles = SiteAdminRole)].
            .IgnoreQueryFilters()
            .Include(c => c.User)
            .Include(c => c.Organization)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        return await JoinConsentsWithApplicationsAsync(consents, cancellationToken);
    }

    /// <summary>
    /// Stamps <c>revoked_at</c> on the consent row and tells OpenIddict to
    /// revoke every active token issued under this (subject, application).
    /// The OAuth client itself stays registered — other users may have
    /// independent consents on it.
    ///
    /// <para>
    /// Three authorisation modes, since the consent table is read with
    /// <c>IgnoreQueryFilters()</c> and can't lean on the tenant filter:
    /// </para>
    /// <list type="bullet">
    /// <item>Self-service (<paramref name="ignoreOrgScope"/> = false): the
    /// consent must belong to <paramref name="actorUserId"/>.</item>
    /// <item>Org admin (<paramref name="ignoreOrgScope"/> = true and
    /// <paramref name="expectedOrganizationId"/> set): the consent must belong
    /// to that org, so an admin can only revoke inside their own tenant.</item>
    /// <item>SiteAdmin (<paramref name="ignoreOrgScope"/> = true and
    /// <paramref name="expectedOrganizationId"/> = null): no org restriction.</item>
    /// </list>
    /// The org-scope guard is defence in depth against a future caller passing a
    /// user-supplied consent id (today they come from row-bound handlers). See #488.
    /// </summary>
    public async Task RevokeConsentAsync(int consentId, int actorUserId, bool ignoreOrgScope = false, int? expectedOrganizationId = null, CancellationToken cancellationToken = default)
    {
        var query = _db.OAuthConsents.AsQueryable();
        // Fence category 2 (SiteAdmin cross-org console): the bypass is opt-in and only the
        // SiteAdmin page passes ignoreOrgScope; org-scoped callers stay inside the filter.
        if (ignoreOrgScope) query = query.IgnoreQueryFilters();
        var consent = await query.FirstOrDefaultAsync(c => c.Id == consentId, cancellationToken);
        if (consent is null || consent.RevokedAt is not null) return;
        if (!ignoreOrgScope && consent.UserId != actorUserId)
        {
            _logger.LogWarning(
                "User {Actor} tried to revoke consent {ConsentId} owned by user {Owner}; refusing.",
                actorUserId, consentId, consent.UserId);
            return;
        }
        if (expectedOrganizationId is int expectedOrg && consent.OrganizationId != expectedOrg)
        {
            _logger.LogWarning(
                "Org {ExpectedOrg} tried to revoke consent {ConsentId} belonging to org {Owner}; refusing.",
                expectedOrg, consentId, consent.OrganizationId);
            return;
        }

        consent.RevokedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(cancellationToken);

        // Kill any access / refresh tokens this user holds for the same
        // client. OpenIddict stores Subject = the user id we stamped during
        // sign-in (see OAuthEndpoints.MapAuthorizeComplete).
        var application = await _applications.FindByClientIdAsync(consent.ClientId, cancellationToken);
        if (application is not null)
        {
            var appId = await _applications.GetIdAsync(application, cancellationToken);
            // One set-based UPDATE over (subject, application), not a walk over
            // every token the user holds with one UPDATE each (#1259).
            if (appId is not null)
            {
                await _tokens.RevokeAsync(consent.UserId.ToString(), appId, status: null, type: null, cancellationToken);
            }
        }

        _logger.LogInformation(
            "Revoked OAuth consent {ConsentId} for user {UserId} on client {ClientId}.",
            consentId, consent.UserId, consent.ClientId);
    }

    /// <summary>
    /// Deletes the OAuth client itself, taking every issued token with it
    /// (OpenIddict cascades) and revoking every org's consent on it. Used by the
    /// SiteAdmin client-list page to wipe a registered Claude / Cursor / whatever
    /// connector completely.
    ///
    /// <para>
    /// <b>SiteAdmin only.</b> An OAuth client is deployment-wide — a single
    /// registration can be consented by users across many orgs — so deleting one
    /// is an inherently cross-tenant action with no org to scope it to. Do not
    /// wire this to an org-Admin surface; an org admin must revoke their org's
    /// consents (<see cref="RevokeConsentAsync"/>) instead, which leaves the
    /// client intact for the other tenants using it. See #488.
    /// </para>
    /// </summary>
    public async Task DeleteClientAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var application = await _applications.FindByClientIdAsync(clientId, cancellationToken);
        if (application is null) return;
        await _applications.DeleteAsync(application, cancellationToken);

        // The consent rows live in our table; mark every consent for this
        // client as revoked so the lists show a consistent picture.
        var now = _clock.GetUtcNow().UtcDateTime;
        var consents = await _db.OAuthConsents
            .IgnoreQueryFilters()
            .Where(c => c.ClientId == clientId && c.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var c in consents) c.RevokedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Deleted OAuth client {ClientId} and {Count} associated consent rows.", clientId, consents.Count);
    }

    private async Task<IReadOnlyList<ConnectedClient>> JoinConsentsWithApplicationsAsync(
        IList<OAuthConsent> consents,
        CancellationToken cancellationToken)
    {
        var result = new List<ConnectedClient>(consents.Count);
        // OpenIddict's manager doesn't accept a batched lookup — one call per
        // distinct client_id. Cache locally so the page render isn't N+1.
        var appCache = new Dictionary<string, ApplicationFacts>(StringComparer.Ordinal);
        foreach (var consent in consents)
        {
            if (!appCache.TryGetValue(consent.ClientId, out var facts))
            {
                var application = await _applications.FindByClientIdAsync(consent.ClientId, cancellationToken);
                if (application is null)
                {
                    facts = new ApplicationFacts(consent.ClientId, "Deleted client", Array.Empty<string>(), null);
                }
                else
                {
                    var display = await _applications.GetDisplayNameAsync(application, cancellationToken) ?? consent.ClientId;
                    var redirects = (await _applications.GetRedirectUrisAsync(application, cancellationToken))
                        .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var p) ? p.Host : u)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var source = await ReadSourcePropertyAsync(application, cancellationToken);
                    facts = new ApplicationFacts(consent.ClientId, display, redirects, source);
                }
                appCache[consent.ClientId] = facts;
            }
            result.Add(new ConnectedClient(
                ConsentId: consent.Id,
                ClientId: consent.ClientId,
                DisplayName: facts.DisplayName,
                RedirectHosts: facts.RedirectHosts,
                RegistrationSource: facts.RegistrationSource,
                UserId: consent.UserId,
                UserEmail: consent.User?.Email ?? "(unknown)",
                OrganizationId: consent.OrganizationId,
                OrganizationName: consent.Organization?.Name ?? string.Empty,
                Scopes: consent.ScopesGranted,
                GrantedAt: consent.GrantedAt,
                RevokedAt: consent.RevokedAt));
        }
        return result;
    }

    private async Task<string?> ReadSourcePropertyAsync(object application, CancellationToken cancellationToken)
    {
        var properties = await _applications.GetPropertiesAsync(application, cancellationToken);
        if (properties.TryGetValue(CimdClientResolver.RegistrationSourceProperty, out var element)
            && element.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return element.GetString();
        }
        return null;
    }

    private sealed record ApplicationFacts(string ClientId, string DisplayName, string[] RedirectHosts, string? RegistrationSource);
}

/// <summary>
/// One consent + the OAuth client it points at, rolled into a single row the
/// list pages can render directly. <see cref="RevokedAt"/> is non-null when
/// the consent has been pulled but the row is kept for audit.
/// </summary>
public sealed record ConnectedClient(
    int ConsentId,
    string ClientId,
    string DisplayName,
    IReadOnlyList<string> RedirectHosts,
    string? RegistrationSource,
    int UserId,
    string UserEmail,
    int OrganizationId,
    string OrganizationName,
    string Scopes,
    DateTime GrantedAt,
    DateTime? RevokedAt);
