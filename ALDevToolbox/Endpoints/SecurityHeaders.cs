namespace ALDevToolbox.Endpoints;

/// <summary>
/// Baseline security response headers for every response the app serves
/// (issue #677). Registered early in the pipeline — ahead of
/// <c>MapStaticAssets</c> — so static assets, health probes, error pages and
/// short-circuiting middleware (maintenance mode, the MCP kill-switch) all
/// carry them too.
///
/// The CSP is *defence in depth*, not the app's primary control: it is what
/// keeps a future markup slip from becoming script execution in the app
/// origin, and <c>frame-ancestors 'none'</c> (with the legacy
/// <c>X-Frame-Options</c> alongside it for old proxies) keeps the app out of
/// other people's frames.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// While true the CSP ships as <c>Content-Security-Policy-Report-Only</c>:
    /// browsers report violations to the console but render the page anyway.
    /// Flip this to <c>false</c> to enforce the same policy — nothing else
    /// needs to change. Do that only after a pass over the app with the
    /// browser console open shows no violations, because an enforced policy
    /// breaks the offending page rather than logging it.
    ///
    /// Known blocker before enforcing: Chrome applies <c>form-action</c> to the
    /// redirect that follows a form post, so <c>form-action 'self'</c> would
    /// break the Microsoft sign-in hop (POST here, 302 to login.microsoftonline.com)
    /// and the OAuth consent redirect to a client's redirect_uri. Those pages
    /// were not exercised in the report-only pass either (they need a configured
    /// tenant and a registered client). Widen or drop <c>form-action</c> first.
    /// </summary>
    private const bool ReportOnly = true;

    /// <summary>
    /// Grounded in what the app actually loads:
    /// <list type="bullet">
    /// <item><c>script-src 'unsafe-inline'</c> — Blazor Server's boot script
    /// and the inline theme script in <c>App.razor</c>. Removing it means
    /// wiring per-response nonces through the Blazor host page first.</item>
    /// <item><c>style-src 'unsafe-inline'</c> — CodeMirror injects its theme
    /// as inline &lt;style&gt; elements, and several components set inline
    /// <c>style=</c> attributes.</item>
    /// <item><c>img-src data:</c> — the TOTP enrolment QR code is a data URI,
    /// and organisation logos render inline.</item>
    /// <item><c>connect-src ws: wss:</c> — the Blazor Server circuit's
    /// WebSocket (and its long-polling fallback over <c>'self'</c>).</item>
    /// </list>
    /// </summary>
    private const string Policy =
        "default-src 'self'; "
        + "base-uri 'self'; "
        + "object-src 'none'; "
        + "frame-ancestors 'none'; "
        + "frame-src 'none'; "
        + "form-action 'self'; "
        + "script-src 'self' 'unsafe-inline'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob:; "
        + "font-src 'self'; "
        + "connect-src 'self' ws: wss:";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            Apply(ctx.Response.Headers);
            await next();
        });
        return app;
    }

    /// <summary>
    /// Sets the headers on a response. Also for a middleware that clears a
    /// half-built response and writes its own (the maintenance page, #1599).
    /// </summary>
    internal static void Apply(IHeaderDictionary headers)
    {
        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["X-Frame-Options"] = "DENY";
        headers[ReportOnly ? "Content-Security-Policy-Report-Only" : "Content-Security-Policy"] = Policy;
    }
}
