using System.Security.Claims;
using OpenIddict.Abstractions;
using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Plain reads over the consent rows in our own <c>oauth_consents</c> table.
/// Separate from <see cref="OAuthClientAdminService"/> on purpose: that service
/// joins every consent against OpenIddict's application registry to get display
/// names, which pulls the OpenIddict managers in behind it. The MCP setup page
/// only needs a yes/no, so it takes this instead.
/// </summary>
/// <remarks>
/// Reads stay inside the organisation query filter — this org's consents for
/// this user is exactly the scope wanted, so there is no
/// <c>IgnoreQueryFilters()</c> call here.
/// </remarks>
public sealed class OAuthConsentService
{
    private readonly AppDbContext _db;

    public OAuthConsentService(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>Whether this user has consented to any OAuth client.</summary>
    public Task<bool> HasAnyConsentAsync(int userId, CancellationToken ct = default)
        => _db.OAuthConsents
            .AsNoTracking()
            .AnyAsync(c => c.UserId == userId, ct);

    /// <summary>
    /// Whether the consent an OAuth access token runs under was approved from
    /// a session with a recent second factor (<see cref="Domain.Entities.OAuthConsent.StrongAuthAt"/>).
    /// The MCP call filter asks this before a writing tool the organisation
    /// marked for step-up runs. The client comes from the token's own claims
    /// (<c>client_id</c>, or <c>azp</c>, or OpenIddict's presenter); a token
    /// naming none, or a consent that is revoked or unstamped, answers no,
    /// which fails closed. Filtered: the token's org is the request's org.
    /// </summary>
    public async Task<bool> WasApprovedWithStrongAuthAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (!int.TryParse(principal.FindFirstValue(HttpOrganizationContext.UserIdClaim), out var userId)) return false;
        var clientId = principal.FindFirstValue(OpenIddictConstants.Claims.ClientId)
            ?? principal.FindFirstValue(OpenIddictConstants.Claims.AuthorizedParty)
            ?? principal.GetPresenters().FirstOrDefault();
        if (string.IsNullOrEmpty(clientId)) return false;
        return await _db.OAuthConsents.AsNoTracking()
            .AnyAsync(c => c.UserId == userId && c.ClientId == clientId
                && c.RevokedAt == null && c.StrongAuthAt != null, ct);
    }
}
