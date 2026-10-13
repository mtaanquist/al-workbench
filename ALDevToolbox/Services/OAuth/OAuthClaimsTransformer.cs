using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Services.OAuth;

/// <summary>
/// Bridges OpenIddict's access-token claims (<c>sub</c>, <c>org</c>, scope)
/// onto the claim names the rest of the app reads
/// (<see cref="HttpOrganizationContext.UserIdClaim"/>,
/// <see cref="HttpOrganizationContext.OrganizationIdClaim"/>,
/// <see cref="HttpOrganizationContext.SiteAdminClaim"/>, role). Matching the
/// shape <see cref="ALDevToolbox.Services.Account.PatAuthenticationHandler"/>
/// already mounts means MCP tools see exactly the same principal regardless
/// of which credential authenticated the request.
///
/// Cookie and PAT principals already carry <c>UserIdClaim</c>, so the
/// idempotency guard short-circuits them. The substantive entry signal is
/// the presence of both <c>sub</c> and our custom <c>org</c> claim — that
/// combination is only ever stamped onto OpenIddict-issued access tokens
/// (see <c>OAuthEndpoints.MapAuthorizeComplete</c>). Earlier versions of
/// this class gated on
/// <see cref="OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme"/>
/// but the unwrapped JWT principal's <c>AuthenticationType</c> is
/// <c>TokenValidationParameters.DefaultAuthenticationType</c>
/// (<c>"AuthenticationTypes.Federation"</c>), not the scheme name — so the
/// check silently bailed and the bridge claims were never added, which
/// downstream surfaces as <c>insufficient_access</c> (OpenIddict <c>ID2095</c>)
/// from the MCP bearer policy.
/// </summary>
public sealed class OAuthClaimsTransformer : IClaimsTransformation
{
    /// <summary>
    /// Custom access-token claim carrying the tenant id, stamped by the consent
    /// flow (<c>OAuthEndpoints.MapAuthorizeComplete</c>) and read back here. Its
    /// presence is the signal a principal came from an OpenIddict token rather
    /// than a cookie or PAT; a rename on one side without the other surfaces as
    /// <c>insufficient_access</c>, so both ends reference this constant.
    /// </summary>
    public const string OrgClaim = "org";

    private readonly AppDbContext _db;

    public OAuthClaimsTransformer(AppDbContext db)
    {
        _db = db;
    }

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = principal.Identity as ClaimsIdentity;
        if (identity is null || !identity.IsAuthenticated)
        {
            return principal;
        }
        // Idempotent: once we've stamped the bridge claims, re-running is a no-op.
        // Cookie and PAT principals already carry UserIdClaim from their own
        // sign-in paths, so this also short-circuits them.
        if (identity.HasClaim(c => c.Type == HttpOrganizationContext.UserIdClaim))
        {
            return principal;
        }

        var subject = principal.FindFirstValue(OpenIddictConstants.Claims.Subject);
        if (string.IsNullOrEmpty(subject) || !int.TryParse(subject, out var userId))
        {
            return principal;
        }

        // The 'org' claim is stamped on the access token by the consent flow
        // (OAuthEndpoints.MapAuthorizeComplete). It's a hard requirement —
        // tokens minted without it can't be mapped to a tenant, and the
        // McpBearer policy will reject them. Its presence is also what tells
        // us this principal came from an OpenIddict access token rather than
        // a cookie or PAT sign-in.
        var orgClaim = principal.FindFirstValue(OrgClaim);
        if (string.IsNullOrEmpty(orgClaim) || !int.TryParse(orgClaim, out var orgId))
        {
            return principal;
        }

        var user = await _db.Users
            // Fence category 1 (pre-auth routing): runs while the principal is being built from
            // a bearer token; pinned to u.Id == userId from the token's subject claim.
            .IgnoreQueryFilters()
            .Include(u => u.Organization)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null || user.Status != UserStatus.Active)
        {
            return principal;
        }
        // Defensive: the user must still belong to the org the token claims.
        // Org membership today is scalar (User.OrganizationId), so this is a
        // direct equality check. When multi-org support lands the check
        // becomes a contains() on the user's org list — same place.
        if (user.OrganizationId != orgId)
        {
            return principal;
        }
        if (IssuedBeforeCredentialChange(principal, user.CredentialsChangedAt))
        {
            return principal;
        }

        identity.AddClaim(new Claim(HttpOrganizationContext.UserIdClaim, userId.ToString()));
        identity.AddClaim(new Claim(HttpOrganizationContext.OrganizationIdClaim, orgId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        identity.AddClaim(new Claim(ClaimTypes.Email, user.Email));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
        if (user.IsSiteAdmin)
        {
            identity.AddClaim(new Claim(HttpOrganizationContext.SiteAdminClaim, "true"));
            identity.AddClaim(new Claim(ClaimTypes.Role, HttpOrganizationContext.SiteAdminRole));
        }
        if (user.Organization?.IsSystem == true)
        {
            identity.AddClaim(new Claim(HttpOrganizationContext.SystemOrgClaim, "true"));
        }
        return principal;
    }

    /// <summary>
    /// Whether the access token was issued before the user's password last
    /// changed (<see cref="User.CredentialsChangedAt"/>). A change revokes the
    /// user's OAuth tokens, but access tokens are validated locally without a
    /// database lookup, so a revoked one would otherwise keep working until it
    /// expires. This is the OAuth counterpart of the cookie check in
    /// <c>CookieSessionRevalidation</c> (#675).
    /// </summary>
    /// <remarks>
    /// Compared at whole-second precision: a token's creation date can be
    /// serialised without fractions, so a token minted in the same second as
    /// the change, after it, would otherwise read as older and be refused. A
    /// token with no creation date at all is treated as older than any stamp.
    /// </remarks>
    private static bool IssuedBeforeCredentialChange(ClaimsPrincipal principal, DateTime? credentialsChangedAt)
    {
        if (credentialsChangedAt is not { } changedAt) return false;
        if (principal.GetCreationDate() is not { } created) return true;
        var changedAtSecond = new DateTimeOffset(changedAt.Ticks - changedAt.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return created < changedAtSecond;
    }
}

