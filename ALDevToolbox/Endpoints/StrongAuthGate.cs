using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Per-organisation strong-auth gate. When an org has
/// <c>OrganizationSettings.RequireStrongAuth = true</c>, every authenticated
/// request from a member of that org must be made by a user who has at
/// least one of: confirmed TOTP, confirmed email-MFA, or a registered
/// passkey. Users without one get redirected to <c>/account/security?required=1</c>,
/// which renders an inline banner explaining what to do next.
///
/// <para>
/// The gate runs after authentication and authorization. It re-checks on
/// every request rather than stamping a sign-in-time claim so that flipping
/// the toggle on (or having an admin disable a user's last method)
/// evicts that user from privileged paths on their next click — without
/// waiting for cookie sliding renewal.
/// </para>
/// </summary>
internal static class StrongAuthGate
{
    public static IApplicationBuilder UseStrongAuthGate(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.User?.Identity?.IsAuthenticated != true)
            {
                await next();
                return;
            }

            var path = ctx.Request.Path;
            if (IsAllowed(path))
            {
                await next();
                return;
            }

            // A PAT-authenticated request is a programmatic call with its own
            // strong credential, and the strong-auth enrolment UI (TOTP / passkey)
            // is browser-only — gating PATs would 403 the entire MCP/API surface
            // for any strong-auth org. The OAuth/MCP HTTP surfaces are allow-listed
            // by path in IsAllowed. See issue #372.
            if (ctx.User.HasClaim(c => c.Type == "pat_id"))
            {
                await next();
                return;
            }

            var userId = ctx.RequestServices
                .GetRequiredService<IOrganizationContext>()
                .CurrentUserId;
            if (userId is null)
            {
                // Authenticated cookie without a user_id claim — odd but not
                // ours to fix here. Let it through and the downstream
                // authorisation will handle it.
                await next();
                return;
            }

            var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
            // One DB round-trip per gated request. Reads bypass the EF query
            // filter because we're joining users → organization_settings
            // across the tenant fence using the cookie's user id directly.
            var status = await db.Users
                .IgnoreQueryFilters()
                .Where(u => u.Id == userId.Value)
                .Select(u => new
                {
                    u.OrganizationId,
                    u.TotpEnabled,
                    u.EmailMfaEnabled,
                })
                .FirstOrDefaultAsync(ctx.RequestAborted);
            if (status is null)
            {
                await next();
                return;
            }

            var requireStrongAuth = await db.OrganizationSettings
                // Fence category 4 (explicitly scoped org-id lookup): pinned to the org id read from
                // the user row above.
                .IgnoreQueryFilters()
                .Where(s => s.OrganizationId == status.OrganizationId)
                .Select(s => (bool?)s.RequireStrongAuth)
                .FirstOrDefaultAsync(ctx.RequestAborted) ?? false;
            if (!requireStrongAuth)
            {
                await next();
                return;
            }

            // The external-login check is filtered: user_external_logins is
            // org-scoped through its User principal and the request carries
            // the signed-in user's own org, so no fence crossing is needed.
            // A linked Microsoft account satisfies the requirement because
            // MFA is the Entra tenant's job there — same rule as
            // AuthService.HasStrongAuthAsync. See issue #552.
            var hasStrongAuth = status.TotpEnabled || status.EmailMfaEnabled
                // Fence category 4 (explicitly scoped user-id lookup): pinned to p.UserId == userId,
                // the id on the request's own auth cookie.
                || await db.UserPasskeys.IgnoreQueryFilters()
                    .AnyAsync(p => p.UserId == userId.Value, ctx.RequestAborted)
                // Entra only: a GitHub account link (issue #621) shares this
                // table but is authorisation, not a sign-in method.
                || await db.UserExternalLogins
                    .AnyAsync(l => l.UserId == userId.Value
                        && l.Provider == ALDevToolbox.Services.Account.EntraSignInService.ProviderName,
                        ctx.RequestAborted);
            if (hasStrongAuth)
            {
                await next();
                return;
            }

            // GETs get a redirect — the page renders a banner explaining the
            // situation. Anything else gets 403 with a short text body; the
            // browser typically follows up with a GET to /account on the
            // user's next click.
            if (HttpMethods.IsGet(ctx.Request.Method))
            {
                ctx.Response.Redirect("/account/security?required=1");
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(
                "Your organisation requires two-factor authentication or a passkey. Set one up at /account/security before retrying.",
                ctx.RequestAborted);
        });
        return app;
    }

    /// <summary>
    /// Paths that must remain reachable so the user can satisfy the
    /// requirement (or sign out). Anything under <c>/auth/</c> is allowed
    /// so passkey/TOTP/email-MFA enrolment flows keep working; the
    /// <c>/account</c> tree carries the UI for it. Framework / health
    /// paths are allowed so Blazor circuits and probes don't break.
    /// </summary>
    internal static bool IsAllowed(PathString path)
    {
        if (!path.HasValue) return true;
        foreach (var prefix in AllowedPathPrefixes)
        {
            if (path.StartsWithSegments(prefix)) return true;
        }
        return false;
    }

    /// <summary>
    /// The allow-list itself, as data so a test can assert the exact set
    /// rather than re-deriving it. Matching is by path <em>segment</em>
    /// prefix, so <c>/accounts-payable</c> is not covered by <c>/account</c>.
    /// </summary>
    internal static readonly string[] AllowedPathPrefixes =
    [
        "/account",
        "/auth",
        // Programmatic surfaces: the MCP server, the OAuth authorization /
        // token / registration flow, and the discovery metadata. These are
        // reached by API clients (or the OAuth consent flow) and must not be
        // gated behind the browser-only strong-auth UI. See issue #372.
        "/mcp",
        "/oauth",
        "/.well-known",
        "/login",
        "/signup",
        "/_blazor",
        "/_framework",
        "/_content",
        "/healthz",
        "/readyz",
        "/not-found",
        "/Error",
        "/css",
        "/js",
        "/favicon.ico",
    ];
}
