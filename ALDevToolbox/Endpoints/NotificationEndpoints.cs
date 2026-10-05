using ALDevToolbox.Services.Notifications;
using Microsoft.AspNetCore.Antiforgery;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The two actions on the Notifications page (issue #1043). Plain endpoints
/// rather than component events because the page and the header count are
/// static: a redirect afterwards is what brings the count up to date.
/// </summary>
internal static class NotificationEndpoints
{
    public const string PagePath = "/notifications";

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        // Opening a notification marks it read and goes to the page it is
        // about. One that is gone (pruned, or never the caller's) lands back on
        // the list rather than on an error.
        app.MapGet("/notifications/{id:int}/open", async (
            int id, InAppNotificationService notifications, CancellationToken ct) =>
        {
            var path = await notifications.OpenForCurrentUserAsync(id, ct);
            return Results.LocalRedirect(IsAppPath(path) ? path! : PagePath);
        }).RequireAuthorization();

        app.MapPost("/notifications/read-all", async (
            HttpContext ctx, InAppNotificationService notifications, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            if (int.TryParse(form["upToId"], out var upToId))
            {
                await notifications.MarkAllReadForCurrentUserAsync(upToId, ct);
            }
            // The bell's flyout posts from whatever page it is open on and goes back there.
            string? returnUrl = form["returnUrl"];
            ctx.Response.Redirect(IsAppPath(returnUrl) ? returnUrl! : PagePath);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// A path within this app, never another site: the stored paths are written
    /// by the notifiers, but the flyout's return path comes from the form, so
    /// both are checked before a redirect follows them.
    /// </summary>
    internal static bool IsAppPath(string? path) =>
        path is { Length: > 0 } && path[0] == '/'
        && (path.Length == 1 || (path[1] != '/' && path[1] != '\\'))
        && !path.Any(char.IsControl);
}
