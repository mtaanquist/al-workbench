using System.Globalization;
using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;
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

        // A restore empties or drops the users table while it runs, and this runs
        // inside UseAuthentication, ahead of the maintenance 503. Re-checking now would
        // sign the person out (no row) or fail the request (no table) instead of letting
        // the gate answer it, so the cookie is left as it is until the restore is over
        // (#1269). A demotion still bites on the first request after it.
        if (context.HttpContext.RequestServices.GetService<MaintenanceModeState>()?.IsActive == true)
        {
            return;
        }

        // Fence category 4 (explicitly scoped user-id lookup): pinned to u.Id == userId,
        // the cookie's own user id - the same posture as StrongAuthGate's re-check.
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
    /// Endpoint filter for the endpoints that hand out a credential that
    /// outlives the cookie: an access token, a passkey, an authenticator app,
    /// recovery codes, a Microsoft link, an assistant's consent. The check
    /// above runs at most once per <see cref="ValidationInterval"/>, so a
    /// cookie validated just before the owner reset their password could
    /// otherwise still mint one of these for up to five minutes, and since it
    /// would be dated after the reset nothing would refuse it later (#1336).
    /// This applies the same cut-off on the request itself, with no throttle.
    /// A refused session is signed out: a form post goes to the sign-in page,
    /// a script call gets a 401.
    /// </summary>
    public static RouteHandlerBuilder RequireCurrentCredentials(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (filterContext, next) =>
        {
            var ctx = filterContext.HttpContext;
            if (!await SessionPredatesCredentialChangeAsync(ctx)) return await next(filterContext);

            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return ctx.Request.HasFormContentType
                ? Results.Redirect(RouteConstants.Login)
                : Results.Unauthorized();
        });

    /// <summary>
    /// True when this request's cookie session started before the user's
    /// last credential change, or the user is no longer there to ask. A
    /// request with no cookie session is left to the endpoint's own
    /// authorisation.
    /// </summary>
    internal static async Task<bool> SessionPredatesCredentialChangeAsync(HttpContext ctx)
    {
        var cookie = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (!cookie.Succeeded || cookie.Properties is null) return false;
        var userId = ctx.RequestServices.GetRequiredService<IOrganizationContext>().CurrentUserId;
        if (userId is null) return false;

        // Filtered: the cookie's own user, inside the organisation its claims name.
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        var row = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId.Value)
            .Select(u => new { u.CredentialsChangedAt })
            .FirstOrDefaultAsync(ctx.RequestAborted);
        if (row is null) return true;
        return row.CredentialsChangedAt is { } changedAt && SignedInAt(cookie.Properties) < changedAt;
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
