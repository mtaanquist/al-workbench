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
    /// A request that arrives while maintenance mode is on is
    /// <see cref="UseMaintenanceMode"/>'s to decide: it answers 503 or lets
    /// it through, and the stages ahead of it (authentication, the tool and
    /// strong-auth gates) read the database to get there, as they did before
    /// the database guard existed. So such a request may use the database.
    /// Registered first, so the allowance covers the whole request. A request
    /// already running when the restore began gets no allowance: its later
    /// commands are refused unless it acts for a SiteAdmin (#1571, see
    /// <see cref="Data.MaintenanceDatabaseGuard"/>).
    /// </summary>
    public static IApplicationBuilder UseMaintenanceDatabaseAllowance(this IApplicationBuilder app)
    {
        app.Use(async (ctx, next) =>
        {
            var maintenance = ctx.RequestServices.GetRequiredService<MaintenanceModeState>();
            if (!maintenance.IsActive)
            {
                await next();
                return;
            }
            using (maintenance.AllowDatabase()) await next();
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
                await next();
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
                await next();
                return;
            }
            if (ctx.User?.FindFirst(HttpOrganizationContext.SiteAdminClaim)?.Value == "true")
            {
                await next();
                return;
            }

            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.Headers.RetryAfter = "30";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(BuildBody(maintenance));
        });
        return app;
    }

    /// <summary>
    /// The page everyone but a SiteAdmin sees. It does not show
    /// <see cref="MaintenanceModeState.Reason"/>, which names the backup file and
    /// is meant for the SiteAdmin; and it only promises a quick return while a
    /// restore is running, not while a failed one holds the site closed (#1321).
    /// </summary>
    private static string BuildBody(MaintenanceModeState maintenance)
    {
        var message = maintenance.IsHeldAfterFailedRestore
            ? "AL Workbench is closed while a site administrator restores it from a backup. Please try again later."
            : "AL Workbench is being restored from a backup. It will be back shortly.";
        return "<!doctype html><html><head><title>Maintenance · AL Workbench</title></head>"
            + "<body style=\"font-family: system-ui, sans-serif; padding: 2rem;\">"
            + "<h1>Maintenance in progress</h1>"
            + $"<p>{message}</p>"
            + $"<p>Started: {maintenance.StartedAtUtc:yyyy-MM-dd HH:mm 'UTC'}.</p>"
            + "</body></html>";
    }
}
