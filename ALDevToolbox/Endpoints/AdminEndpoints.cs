using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using Microsoft.AspNetCore.Antiforgery;
using static ALDevToolbox.Endpoints.EndpointHelpers;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Templates;

namespace ALDevToolbox.Endpoints;

internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // /admin/configuration/logo/preview serves the current org's logo bytes
        // so the configuration page can display the existing logo. The page
        // itself commits its edits through Blazor service calls.
        app.MapGet("/admin/configuration/logo/preview", async (
            HttpContext ctx, OrganizationConfigService config, CancellationToken ct) =>
        {
            var snapshot = await config.GetCurrentAsync(ct);
            if (snapshot.Logo is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = snapshot.Logo.ContentType;
            ctx.Response.Headers.CacheControl = "no-store";
            // This URL can be navigated to directly, which renders an SVG as a
            // document in the app's own origin. Sandbox the response so markup
            // that ever slipped past the upload sanitiser has no script
            // execution and no origin to reach: nosniff alone would not stop
            // that, and an inline disposition still renders. App-wide security
            // headers are a separate piece of work (issue #677).
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            await ctx.Response.Body.WriteAsync(snapshot.Logo.Content, ct);
            // Editors reach the Defaults page that shows this logo and can change
            // it there, so they can see it too (#1615). It is only ever the
            // acting organisation's own logo.
        }).RequireAuthorization(policy => policy.RequireRole(
            HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole));

        // /admin/export/import — destructive restore from a TOML snapshot.
        // Lives on the Export page, kept as a server POST so the (large)
        // TOML body is parsed outside the interactive Blazor circuit.
        // Note: tenants never see database backup/restore; this endpoint only
        // replays the per-org configuration TOML.
        app.MapPost("/admin/export/import", async (
            HttpContext ctx, OrganizationConfigTomlImporter importer, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var toml = form["Toml"].ToString();
            var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
            if (!confirmed)
            {
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminExport}?{RouteConstants.ErrQuery}=Confirm&{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Tick the confirmation box before importing."));
                return;
            }
            try
            {
                await importer.ImportFromTomlAsync(toml, ct);
                ctx.Response.Redirect($"{RouteConstants.AdminExport}?{RouteConstants.OkQuery}=imported");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminExport}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/export/download", async (HttpContext ctx, ExportService export, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var archive = await export.ExportAllAsync(ct);
            WriteAttachmentHeaders(ctx, archive.FileName);
            archive.Stream.Position = 0;
            await archive.Stream.CopyToAsync(ctx.Response.Body, ct);
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // Marks a single runtime template as the per-organisation default. The
        // service enforces the "active and non-deprecated" rule and clears
        // the previous default in the same SaveChanges.
        app.MapPost("/admin/templates/{id:int}/default", async (
            int id, HttpContext ctx, TemplateService templates, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await templates.SetDefaultAsync(id, ct);
                ctx.Response.Redirect($"{RouteConstants.AdminTemplates}?{RouteConstants.OkQuery}=default-set");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect(
                    $"{RouteConstants.AdminTemplates}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        return app;
    }
}
