using ALDevToolbox.Data;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using ALDevToolbox.Services.OAuth;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Mounts the Model Context Protocol HTTP transport at <c>/mcp</c> and
/// guards it with the <see cref="McpBearerPolicy"/> — which accepts either a
/// Personal Access Token (<see cref="PatAuthenticationHandler"/>, the
/// desktop/CLI path) or an OAuth access token issued by our OpenIddict
/// server (the Claude.ai directory / custom-connector path). Both schemes
/// mount the same downstream claim shape via
/// <see cref="OAuth.OAuthClaimsTransformer"/>.
///
/// Two layers of off-switch:
/// <list type="bullet">
///   <item><c>Mcp:Enabled</c> in <c>appsettings.json</c> — deployment-level.
///         When false the route isn't mapped at all.</item>
///   <item><c>system_settings.mcp_enabled</c> — SiteAdmin runtime toggle.
///         When false the route is mapped but the runtime middleware
///         installed by <see cref="UseMcpKillSwitch"/> short-circuits every
///         request to 404 before the MCP transport sees it.</item>
/// </list>
///
/// /mcp is intentionally <strong>not</strong> tagged for /healthz or
/// /readyz — these probes must keep returning quickly regardless of MCP
/// traffic. It is also outside the maintenance-mode allow-list, so MCP
/// requests pause during restores like every other org-scoped call.
/// </summary>
internal static class McpEndpoints
{
    /// <summary>
    /// Inserts the runtime kill-switch middleware ahead of routing. Must be
    /// called from <c>Program.cs</c> before <c>MapMcpEndpoints</c> so the
    /// 404 short-circuit beats the MCP transport's own request validation
    /// (which would otherwise return 400 for empty POSTs and mask the
    /// off-state).
    /// </summary>
    public static IApplicationBuilder UseMcpKillSwitch(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }
            // (Below kill-switch is intentionally synchronous now that
            //  IMcpAvailability is in-memory.)
            // Opt /mcp out of UseStatusCodePagesWithReExecute. The status-pages
            // middleware re-runs the pipeline at GET /not-found whenever a
            // downstream handler returns a bare 4xx; for POST /mcp that
            // re-execute mismatches the @page binding and the response goes
            // back to the client as 400 instead of the original status (401 /
            // 405 / 415). MCP clients want clean status codes, so we disable
            // the rewrite for this prefix.
            var statusCodes = ctx.Features.Get<IStatusCodePagesFeature>();
            if (statusCodes is not null) statusCodes.Enabled = false;

            // Read the in-memory toggle — no DB hit per request. Singleton is
            // primed at startup and refreshed by SystemSettingsService.SaveAsync.
            var availability = ctx.RequestServices.GetRequiredService<IMcpAvailability>();
            if (!availability.IsEnabled)
            {
                // Plain-text response body keeps UseStatusCodePagesWithReExecute
                // from re-running the pipeline as a GET /not-found — that
                // re-execute renders the NotFound Razor page (fine) but the
                // outer response writer ends up returning 400 because the
                // request method (POST) doesn't match the page's GET binding.
                // Writing a body ourselves makes the status-pages middleware
                // skip re-execute. Agents see a tidy plain-text 404 either way.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync("MCP is disabled on this deployment.");
                return;
            }
            await next();
        });
        return app;
    }

    public static IEndpointRouteBuilder MapMcpEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<IOptions<McpOptions>>().Value;
        if (!options.Enabled)
        {
            // Deployment-level kill-switch: don't mount the routes at all.
            // /mcp returns 404 in that case, which is the same shape the
            // SDK would produce for a path the agent has mis-typed.
            return app;
        }

        app.MapMcp("/mcp")
            .RequireAuthorization(McpBearerPolicy.Name)
            // Per-org opt-out (Issue: per-org MCP toggle). Authoritative
            // check at request time — the `org_mcp_enabled` claim on the
            // cookie/PAT principal feeds the nav-link visibility but can be
            // stale; this lookup against the live row is what actually
            // refuses a request from an opted-out org.
            .AddEndpointFilter(async (ctx, next) =>
            {
                var orgCtx = ctx.HttpContext.RequestServices.GetRequiredService<IOrganizationContext>();
                var orgId = orgCtx.CurrentOrganizationId;
                if (orgId is null)
                {
                    return Results.Unauthorized();
                }
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                var enabled = await db.Organizations.AsNoTracking()
                    .Where(o => o.Id == orgId.Value)
                    .Select(o => (bool?)o.McpEnabled)
                    .FirstOrDefaultAsync(ctx.HttpContext.RequestAborted);
                if (enabled != true)
                {
                    ctx.HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
                    ctx.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
                    await ctx.HttpContext.Response.WriteAsync(
                        "MCP is disabled for this organisation.",
                        ctx.HttpContext.RequestAborted);
                    return Results.Empty;
                }
                return await next(ctx);
            });
        return app;
    }
}
