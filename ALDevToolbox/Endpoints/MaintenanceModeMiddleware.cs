using ALDevToolbox.Services;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// While <see cref="BackupService.RestoreAsync"/> is mid-flight,
/// <see cref="MaintenanceModeState"/> is active and every non-SiteAdmin
/// request gets a <c>503</c> with a tiny static body. SiteAdmin requests and
/// the <c>/site-admin</c> area still pass through so the operator can watch the
/// restore; <c>/healthz</c> and <c>/readyz</c> stay reachable so reverse
/// proxies don't flap the container during a long restore.
/// </summary>
internal static class MaintenanceModeMiddleware
{
    /// <summary>
    /// The paths a restore closes before authentication runs. The OAuth
    /// token, revocation and introspection endpoints (a refresh grant writes
    /// token rows) and the Microsoft sign-in callback (a first sign-in creates
    /// the user and the link) are handled inside authentication and would
    /// write. <c>/mcp</c> is here because its bearer policy is checked by
    /// authorization, ahead of the gate, and an access token is left
    /// unvalidated during maintenance, so the caller would get a 401 rather
    /// than the 503 that tells it to retry. Nobody can be recognised as a
    /// SiteAdmin before authentication, and an assistant or a person retries
    /// these after the 503 (#1571). The authorization and end-session endpoints
    /// are validated inside authentication too, and the authorization request's
    /// client-metadata lookup creates or refreshes the client row there; without
    /// the 503 an assistant connecting during a restore got "invalid client" or an
    /// error page and no hint that trying again later would work (#1598).
    /// </summary>
    internal static readonly string[] ClosedBeforeAuthentication =
    [
        "/oauth/authorize",
        "/oauth/logout",
        "/oauth/token",
        "/oauth/revoke",
        "/oauth/introspect",
        EntraAuthEndpoints.CallbackPath,
        "/mcp",
    ];

    /// <summary>
    /// A request that arrives while maintenance mode is on is
    /// <see cref="UseMaintenanceMode"/>'s to decide, but the stages ahead of
    /// it (authentication, the tool and strong-auth gates) read the database
    /// to get there. So such a request may read the database, and not write
    /// it. The paths in <see cref="ClosedBeforeAuthentication"/> get the 503
    /// here instead, since they are handled inside authentication. Registered
    /// first, so the allowance covers the whole request. A request already
    /// running when the restore began gets no allowance at all (#1571, see
    /// <see cref="Data.MaintenanceDatabaseGuard"/>).
    /// </summary>
    public static IApplicationBuilder UseMaintenanceDatabaseAllowance(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var maintenance = ctx.RequestServices.GetRequiredService<MaintenanceModeState>();
            if (!maintenance.IsActive)
            {
                await NextOrMaintenancePageAsync(ctx, next, maintenance);
                return;
            }
            if (ClosedBeforeAuthentication.Any(p => ctx.Request.Path.StartsWithSegments(p)))
            {
                await WriteMaintenancePageAsync(ctx, maintenance);
                return;
            }
            using (maintenance.AllowDatabaseReads()) await NextOrMaintenancePageAsync(ctx, next, maintenance);
        });
        return app;
    }

    public static IApplicationBuilder UseMaintenanceMode(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var maintenance = ctx.RequestServices.GetRequiredService<MaintenanceModeState>();
            if (!maintenance.IsActive)
            {
                await NextOrMaintenancePageAsync(ctx, next, maintenance);
                return;
            }

            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/healthz")
                || path.StartsWithSegments("/readyz")
                || path.StartsWithSegments("/site-admin")
                // Accepting a delivery is enqueueing, not work: it costs one HMAC
                // and one channel write, and the worker holds the job back rather
                // than touching the database while a restore is in flight. GitHub
                // disables a webhook whose deliveries keep failing, so a 503 here
                // would cost the organisation its compile gate for reasons it
                // cannot see.
                || path.StartsWithSegments(GitHubWebhookEndpoints.WebhookPath))
            {
                await NextOrMaintenancePageAsync(ctx, next, maintenance);
                return;
            }
            if (ctx.User?.FindFirst(HttpOrganizationContext.SiteAdminClaim)?.Value == "true")
            {
                await NextOrMaintenancePageAsync(ctx, next, maintenance);
                return;
            }

            await WriteMaintenancePageAsync(ctx, maintenance);
        });
        return app;
    }

    /// <summary>
    /// Runs the rest of the pipeline, and answers with the maintenance page when the
    /// database guard refused a command on the way (#1599): a request that was already
    /// running when the restore began, or one that tried to write during it, would
    /// otherwise end as a 500. Applied here, inside the exception handler, so it sees
    /// the refusal before that handler turns it into the error page, and again in the
    /// outermost middleware for what fails ahead of this gate.
    /// </summary>
    private static async Task NextOrMaintenancePageAsync(HttpContext ctx, Func<Task> next, MaintenanceModeState maintenance)
    {
        try
        {
            await next();
        }
        catch (Exception ex) when (IsGuardRefusal(ex) && !ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            // Clearing drops the security headers set further out; put them back.
            SecurityHeaders.Apply(ctx.Response.Headers);
            await WriteMaintenancePageAsync(ctx, maintenance);
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the guard's refusal, bare (a query) or
    /// wrapped (EF reports a refused write in <c>SaveChanges</c> as a
    /// <c>DbUpdateException</c> around it).
    /// </summary>
    internal static bool IsGuardRefusal(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is DatabaseClosedForMaintenanceException) return true;
        }
        return false;
    }

    private static Task WriteMaintenancePageAsync(HttpContext ctx, MaintenanceModeState maintenance)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        ctx.Response.Headers.RetryAfter = "30";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync(BuildBody(maintenance));
    }

    /// <summary>
    /// The page everyone but a SiteAdmin sees. It does not show
    /// <see cref="MaintenanceModeState.Reason"/>, which names the backup file and
    /// is meant for the SiteAdmin; and it only promises a quick return while a
    /// restore is running, not while a failed one holds the site closed (#1321).
    /// </summary>
    private static string BuildBody(MaintenanceModeState maintenance)
    {
        // A request refused by the guard can answer after the restore already ended
        // (a refused restore reopens the site at once): there is no start time then,
        // and the site is open again.
        var message = maintenance.IsHeldAfterFailedRestore
            ? "AL Workbench is closed while a site administrator restores it from a backup. Please try again later."
            : maintenance.IsActive
                ? "AL Workbench is being restored from a backup. It will be back shortly."
                : "AL Workbench was briefly closed for maintenance. Please try again.";
        var started = maintenance.IsActive
            ? $"<p>Started: {maintenance.StartedAtUtc:yyyy-MM-dd HH:mm 'UTC'}.</p>"
            : string.Empty;
        return "<!doctype html><html><head><title>Maintenance · AL Workbench</title></head>"
            + "<body style=\"font-family: system-ui, sans-serif; padding: 2rem;\">"
            + "<h1>Maintenance in progress</h1>"
            + $"<p>{message}</p>"
            + started
            + "</body></html>";
    }
}
