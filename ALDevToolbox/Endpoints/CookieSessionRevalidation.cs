using System.Globalization;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Re-validates a cookie principal against the database on a throttle, so an
/// account <em>disable</em>, a role demotion (Admin→User), or a SiteAdmin
/// demotion takes effect within <see cref="ValidationInterval"/> instead of
/// riding the 30-day sliding cookie to expiry. Without this, role / Status /
/// IsSiteAdmin are baked into the cookie at sign-in and only refresh on
/// re-login. PAT and OAuth requests re-check <c>Status == Active</c> on every
/// request already, so this is the cookie-only counterpart. See issue #412.
///
/// <para>
/// Wired as <c>CookieAuthenticationOptions.Events.OnValidatePrincipal</c>. The
/// throttle stamps a timestamp in the auth properties and only hits the DB once
/// per interval per session, so the steady-state cost is one extra query per
/// signed-in user per <see cref="ValidationInterval"/>, not per request.
/// </para>
/// </summary>
internal static class CookieSessionRevalidation
{
    /// <summary>How stale a cookie's role/Status/SiteAdmin snapshot may get before a re-check.</summary>
    public static readonly TimeSpan ValidationInterval = TimeSpan.FromMinutes(5);

    private const string LastValidatedKey = "revalidated_at";

    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true) return;

        var idClaim = principal.FindFirstValue(HttpOrganizationContext.UserIdClaim)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var userId)) return;

        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;

        // Throttle: skip the DB round-trip if we re-validated within the window.
        if (context.Properties.Items.TryGetValue(LastValidatedKey, out var lastRaw)
            && DateTime.TryParse(lastRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            && now - last < ValidationInterval)
        {
            return;
        }

        // Read across the tenant fence by the cookie's user id — the same trust
        // posture StrongAuthGate uses for its per-request enrolment re-check.
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var user = await db.Users.IgnoreQueryFilters()
            .Include(u => u.Organization)
            .FirstOrDefaultAsync(u => u.Id == userId, context.HttpContext.RequestAborted);

        if (user is null || user.Status != UserStatus.Active)
        {
            // Deleted or disabled since sign-in: drop the cookie immediately.
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Password changed (or reset) after this cookie was handed out: the
        // session belongs to the old credential, so drop it. A cookie with no
        // sign-in stamp predates issue #675 and is treated as older than any
        // stamp. The user who made the change keeps working because the
        // change-password endpoint re-issues their own cookie.
        if (user.CredentialsChangedAt is { } changedAt && SignedInAt(context.Properties) < changedAt)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Rebuild the claims from the current row so a role / SiteAdmin / org
        // change applies on this request, then renew the cookie with a fresh
        // re-validation stamp.
        var rebuilt = EndpointHelpers.BuildIdentity(user);
        // The sign-in method and step-up moment live in the properties, which
        // survive this rebuild; mirror them back onto the fresh claims.
        ALDevToolbox.Services.Account.StepUpAuth.ApplyClaims(rebuilt, context.Properties);
        context.ReplacePrincipal(new ClaimsPrincipal(rebuilt));
        context.Properties.Items[LastValidatedKey] = now.ToString("o", CultureInfo.InvariantCulture);
        context.ShouldRenew = true;
    }

    /// <summary>
    /// The moment the cookie's session started, as stamped by
    /// <see cref="EndpointHelpers.PersistentSignIn"/>. Returns
    /// <see cref="DateTime.MinValue"/> when the stamp is missing or unparsable
    /// so an old cookie fails the credential-change comparison rather than
    /// passing it.
    /// </summary>
    private static DateTime SignedInAt(AuthenticationProperties properties) =>
        properties.Items.TryGetValue(EndpointHelpers.SignedInAtKey, out var raw)
        && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var issued)
            ? issued
            : DateTime.MinValue;
}
