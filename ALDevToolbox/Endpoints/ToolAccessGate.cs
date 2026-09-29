using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.Tools;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Route-access gate for the toggleable tools. When a tool is switched off —
/// site-wide by a SiteAdmin, or per-org by an org Admin — a direct navigation
/// to any of that tool's end-user routes (typed URL, reload, enhanced-nav GET)
/// gets a real 404 instead of the page. The sidebar already hides disabled
/// tools, so this is the "URL hacking" backstop: a hidden tool isn't reachable
/// by guessing its address.
///
/// <para>
/// Site state comes from the in-memory <see cref="IToolAvailability"/> singleton;
/// the per-org opt-out is read from the <c>org_disabled_tools</c> cookie claim
/// (no DB hit), so an org change propagates on the next cookie revalidation
/// (~5 min) — matching the existing MCP nav behaviour. Only the tools'
/// end-user route prefixes are gated (see <see cref="ToolCatalog"/>); their
/// <c>/admin/*</c> authoring pages stay reachable. Runs after authentication so
/// the claim is available, and ahead of routing so the 404 re-executes
/// <c>/not-found</c> via <c>UseStatusCodePagesWithReExecute</c>.
/// </para>
/// </summary>
internal static class ToolAccessGate
{
    public static IApplicationBuilder UseToolAccessGate(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var tools = MatchTools(ctx.Request.Path);
            if (tools.Count == 0)
            {
                await next();
                return;
            }

            var availability = ctx.RequestServices.GetRequiredService<IToolAvailability>();
            var orgDisabled = EndpointHelpers.ReadDisabledTools(ctx.User);
            // Org opt-out only narrows a site-enabled tool; a site-disabled tool
            // is gone for everyone regardless of the claim. A route shared by two
            // tools (the Pipelines dashboard) answers while either is on.
            var enabled = tools.Any(t => availability.IsSiteEnabled(t) && !orgDisabled.Contains(t));

            if (!enabled)
            {
                // A plain 404 — UseStatusCodePagesWithReExecute("/not-found")
                // turns it into the NotFound page. Same idiom as the SiteAdmin
                // path guard in Program.cs.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // Step-up: a tool the org marked on Administration → Tools opens
            // only for a session whose last second factor is recent. The set
            // rides the cookie like the disabled set; the freshness stamp is a
            // claim mirrored from the cookie's properties (StepUpAuth). Anonymous
            // requests fall through to authorization's own login redirect, and
            // PATs never reach page routes. GETs go to the step-up page and
            // come back; anything else gets a 403.
            var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
            if (NeedsStepUp(tools, ctx.User, clock.GetUtcNow().UtcDateTime))
            {
                if (HttpMethods.IsGet(ctx.Request.Method))
                {
                    ctx.Response.Redirect(StepUpEndpoints.Url(ctx.Request.Path + ctx.Request.QueryString));
                    return;
                }
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "Confirm it's you before using this tool: open it again in the browser.",
                    ctx.RequestAborted);
                return;
            }

            await next();
        });
        return app;
    }

    /// <summary>
    /// True when the signed-in cookie user's organisation wants a second factor
    /// for any of <paramref name="tools"/> and the session's last one is older
    /// than the organisation's window (both ride the cookie as claims). The
    /// shared Pipelines dashboard is gated when either of its tools is.
    /// </summary>
    internal static bool NeedsStepUp(IReadOnlyList<ToolKey> tools, System.Security.Claims.ClaimsPrincipal? user, DateTime now)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (user.HasClaim(c => c.Type == "pat_id")) return false;
        var stepUp = EndpointHelpers.ReadStepUpTools(user);
        if (stepUp.Count == 0 || !tools.Any(stepUp.Contains)) return false;
        return !Services.Account.StepUpAuth.IsFresh(user, now, EndpointHelpers.ReadStepUpWindow(user));
    }

    /// <summary>
    /// The page that belongs to two tools: the Pipelines dashboard sits above both
    /// build and deployment pipelines (#955), so it goes only when both do. Exact
    /// path only - everything under it still belongs to one tool by longest prefix.
    /// </summary>
    private const string SharedPipelinesRoute = "/pipelines";

    /// <summary>
    /// The tools a request needs at least one of: the two pipeline tools for the
    /// Pipelines dashboard, otherwise the one <see cref="MatchTool"/> finds, or none
    /// for a path that is not a gated tool route.
    /// </summary>
    internal static IReadOnlyList<ToolKey> MatchTools(PathString path)
    {
        if (path.StartsWithSegments(SharedPipelinesRoute, StringComparison.OrdinalIgnoreCase, out var rest)
            && (!rest.HasValue || rest.Value == "/"))
        {
            return [ToolKey.Pipelines, ToolKey.Releases];
        }
        return MatchTool(path) is { } tool ? [tool] : [];
    }

    /// <summary>
    /// Returns the tool whose end-user route prefix the path falls under, or
    /// <see langword="null"/> when the path isn't a gated tool route. The longest
    /// matching prefix wins, because one tool's routes can sit inside another's:
    /// deployment pipelines (<c>/pipelines/deployments</c>) under build pipelines
    /// (<c>/pipelines</c>).
    /// </summary>
    internal static ToolKey? MatchTool(PathString path)
    {
        if (!path.HasValue) return null;
        ToolKey? match = null;
        var matchLength = -1;
        foreach (var tool in ToolCatalog.All)
        {
            foreach (var prefix in tool.RoutePrefixes)
            {
                if (prefix.Length > matchLength && path.StartsWithSegments(prefix))
                {
                    match = tool.Key;
                    matchLength = prefix.Length;
                }
            }
        }
        return match;
    }
}
