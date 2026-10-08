using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using ALDevToolbox.Services.SingleTenant;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;
using ALDevToolbox.Services.Backups;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// /site-admin/* — hosting-operator endpoints. The cookie events translate
/// auth failures on this prefix into 404 so non-SiteAdmins can't even confirm
/// the routes exist (see <c>Program.cs</c> cookie options).
/// </summary>
internal static class SiteAdminEndpoints
{
    public static IEndpointRouteBuilder MapSiteAdminEndpoints(this IEndpointRouteBuilder app)
    {
        // Revoke any Personal Access Token, regardless of organisation. Used
        // when a token has leaked or its owner has left the org. Calls into
        // PersonalAccessTokenService with ignoreOrgScope so the token row is
        // visible through the org query filter that would otherwise hide it.
        app.MapPost("/site-admin/access-tokens/{id:int}/revoke", async (
            int id, HttpContext ctx, PersonalAccessTokenService tokens, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await tokens.RevokeAsync(id, ignoreOrgScope: true, ct: ct);
            ctx.Response.Redirect("/site-admin/connections/access-tokens?ok=revoked");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/users/{id:int}/promote", async (
            int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await siteAdmin.PromoteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=promoted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/users/{id:int}/demote", async (
            int id, HttpContext ctx, SiteAdminService siteAdmin, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await siteAdmin.DemoteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=demoted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        // Per-section save endpoints. Each loads the current settings
        // view, overlays only its own fields from the form, and then
        // calls SystemSettingsService.SaveAsync — which still does the
        // single transactional write and validation. That way the
        // service contract stays minimal, the SMTP password encryption
        // path stays in one place, and tabs save independently from the
        // user's point of view.

        async Task SaveSectionAsync(
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct,
            Func<SystemSettingsView, IFormCollection, SystemSettingsInput> overlay,
            string tabPath)
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var current = await settings.GetViewAsync(ct);
            var input = overlay(current, form);
            try
            {
                await settings.SaveAsync(input, ct);
                ctx.Response.Redirect($"{tabPath}?{RouteConstants.OkQuery}=saved");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{tabPath}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(first.Value));
            }
        }

        // The pin/unpin/delete/restore backup endpoints all surface a single
        // validation error the same way: redirect back to their list page with
        // the first error message in the ?msg= query. Same shape as the
        // SaveSectionAsync catch above, factored out because it recurs ~10 times.
        static void RedirectFirstError(HttpContext ctx, string tabPath, PlanValidationException ex) =>
            ctx.Response.Redirect($"{tabPath}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Errors.First().Value));

        app.MapPost("/site-admin/settings/smtp/save", (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
                SaveSectionAsync(ctx, settings, antiforgery, ct,
                    (current, form) => SettingsInputBuilder.WithSmtp(current, form),
                    "/site-admin/settings/smtp"))
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/backups/save", async (
            HttpContext ctx, SystemSettingsService settings, DisplayTimeZone zone, TimeProvider clock,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            // The schedule is typed in the organisation's display zone (#970).
            await zone.EnsureLoadedAsync(ct);
            var offset = zone.OffsetAt(clock.GetUtcNow().UtcDateTime);
            await SaveSectionAsync(ctx, settings, antiforgery, ct,
                (current, form) => SettingsInputBuilder.WithBackups(current, form, offset),
                "/site-admin/settings/backups");
        })
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/quotas/save", (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
                SaveSectionAsync(ctx, settings, antiforgery, ct,
                    (current, form) => SettingsInputBuilder.WithQuotas(current, form),
                    "/site-admin/settings/quotas"))
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
            .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/settings/general/save", (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
                SaveSectionAsync(ctx, settings, antiforgery, ct,
                    (current, form) => SettingsInputBuilder.WithGeneral(current, form),
                    "/site-admin/settings/general"))
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/tools/save", (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
                SaveSectionAsync(ctx, settings, antiforgery, ct,
                    (current, form) => SettingsInputBuilder.WithTools(current, form),
                    "/site-admin/settings/tools"))
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/builds/save", (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
                SaveSectionAsync(ctx, settings, antiforgery, ct,
                    (current, form) => SettingsInputBuilder.WithBuilds(current, form),
                    "/site-admin/settings/builds"))
            .RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/entra/save", async (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var input = new EntraAppInput(
                ClientId: form["EntraClientId"].ToString(),
                ClientSecret: form["EntraClientSecret"].ToString(),
                ClearClientSecret: form["ClearEntraClientSecret"] == "true" || form["ClearEntraClientSecret"] == "on");
            try
            {
                await settings.SaveEntraAppAsync(input, ct);
                ctx.Response.Redirect($"/site-admin/settings/entra?{RouteConstants.OkQuery}=saved");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, "/site-admin/settings/entra", ex);
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/github/save", async (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var input = new GitHubAppInput(
                AppId: form["GitHubAppId"].ToString(),
                AppSlug: form["GitHubAppSlug"].ToString(),
                ClientId: form["GitHubClientId"].ToString(),
                ClientSecret: form["GitHubClientSecret"].ToString(),
                ClearClientSecret: IsChecked(form, "ClearGitHubClientSecret"),
                PrivateKeyPem: form["GitHubPrivateKey"].ToString(),
                ClearPrivateKey: IsChecked(form, "ClearGitHubPrivateKey"),
                WebhookSecret: form["GitHubWebhookSecret"].ToString(),
                ClearWebhookSecret: IsChecked(form, "ClearGitHubWebhookSecret"));
            try
            {
                await settings.SaveGitHubAppAsync(input, ct);
                ctx.Response.Redirect($"/site-admin/settings/github?{RouteConstants.OkQuery}=saved");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, "/site-admin/settings/github", ex);
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/offsite/save", async (
            HttpContext ctx, SystemSettingsService settings, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var input = new OffsiteSettingsInput(
                Enabled: IsChecked(form, "Enabled"),
                Provider: form["Provider"].ToString(),
                Endpoint: form["Endpoint"].ToString(),
                Region: form["Region"].ToString(),
                Bucket: form["Bucket"].ToString(),
                Prefix: form["Prefix"].ToString(),
                AccessKey: form["AccessKey"].ToString(),
                ClearAccessKey: IsChecked(form, "ClearAccessKey"),
                SecretKey: form["SecretKey"].ToString(),
                ClearSecretKey: IsChecked(form, "ClearSecretKey"),
                ForcePathStyle: IsChecked(form, "ForcePathStyle"),
                RetentionDays: int.TryParse(form["RetentionDays"], out var rd) ? rd : 90);
            try
            {
                await settings.SaveOffsiteAsync(input, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.OkQuery}=offsite-saved");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminSettings, ex);
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/offsite/test", async (
            HttpContext ctx, OffsiteBackupService offsite, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var result = await offsite.TestConnectionAsync(ct);
            var prefix = result.Success ? "OK: " : "FAIL: ";
            ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?test="
                + Uri.EscapeDataString(prefix + result.Message));
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/upload", async (
            int id, HttpContext ctx, OffsiteBackupService offsite, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                var key = await offsite.UploadAsync(id, ct);
                if (key is null)
                {
                    ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                        + Uri.EscapeDataString("Off-site backup not configured, or the local file is missing."));
                    return;
                }
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=uploaded");
            }
            catch (Exception ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Upload failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/offsite/download", async (
            HttpContext ctx, OffsiteBackupService offsite, OffsiteRestoreJobs jobs,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var key = form["ObjectKey"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Pick an object to download."));
                return;
            }
            // The form already lists the bucket's objects; cross-checking
            // membership here costs another S3 round-trip but stops a
            // tampered submit from triggering a download of an arbitrary
            // object outside the configured prefix. The download service
            // ALSO enforces the prefix and dump-suffix rule, so this is
            // belt-and-braces.
            var listed = await offsite.ListAsync(maxObjects: 1000, ct);
            var match = listed.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
            if (match is null)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("That object is no longer in the bucket or doesn't look like one of our backups."));
                return;
            }
            var id = jobs.Enqueue(OffsiteRestoreJobKind.WholeDb, match.Key, match.FileName);
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?job={Uri.EscapeDataString(id.ToString())}");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapGet("/site-admin/backups/offsite/jobs/{id:guid}", (
            Guid id, OffsiteRestoreJobs jobs) =>
        {
            var job = jobs.Get(id);
            if (job is null) return Results.NotFound();
            return Results.Json(new
            {
                id = job.Id,
                kind = job.Kind.ToString().ToLowerInvariant(),
                objectKey = job.ObjectKey,
                fileName = job.FileName,
                status = job.Status.ToString().ToLowerInvariant(),
                bytesDownloaded = job.BytesDownloaded,
                totalBytes = job.TotalBytes,
                error = job.Error,
                backupId = job.BackupId,
                startedAt = job.StartedAt,
                updatedAt = job.UpdatedAt,
            });
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/settings/test-email", async (
            HttpContext ctx, IEmailService email, EmailRenderer renderer, AppDbContext db, IOrganizationContext orgCtx,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SiteAdminTestEmail");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString("SMTP is not configured."));
                return;
            }
            var userId = orgCtx.CurrentUserId;
            if (userId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var recipient = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => new { u.Email, u.DisplayName })
                .FirstAsync(ct);
            try
            {
                var content = await SiteAdminTestEmail.RenderAsync(renderer, recipient.DisplayName, ct);
                await email.SendAsync(recipient.Email, content, EmailPurpose.SiteAdminTest, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.OkQuery}=test-sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Test email failed for SiteAdmin {Email}.", recipient.Email);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminSettings}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Test email failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        // Sends one of the email previews to the signed-in site admin, so they can
        // see it in a real mail client. Sample data only, see EmailPreviews.
        app.MapPost("/site-admin/email/previews/{key}/send", async (
            string key,
            HttpContext ctx,
            IEmailService email,
            EmailRenderer renderer,
            AppDbContext db,
            IOrganizationContext orgCtx,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SiteAdminEmailPreview");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var preview = EmailPreviews.Find(key);
            if (preview is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var back = $"/site-admin/email/previews?email={Uri.EscapeDataString(preview.Key)}";
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{back}&{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Email is not set up yet. Fill in Email settings first."));
                return;
            }
            var recipient = await db.Users.AsNoTracking()
                .Where(u => u.Id == orgCtx.CurrentUserId!.Value)
                .Select(u => u.Email)
                .FirstAsync(ct);
            try
            {
                var content = await preview.RenderAsync(renderer, ct);
                // Marked in the subject so it is not mistaken for the real thing.
                content = content with { Subject = $"Preview: {content.Subject}" };
                await email.SendAsync(recipient, content, EmailPurpose.SiteAdminTest, ct);
                ctx.Response.Redirect($"{back}&{RouteConstants.OkQuery}=sent");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Email preview {Preview} could not be sent to {Email}.", preview.Key, recipient);
                ctx.Response.Redirect($"{back}&{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("The preview could not be sent. The details are in the app log."));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/create", async (
            HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await backups.CreateAsync(BackupKind.AdHoc, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=created");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/pin", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, pinned: true, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=pinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/unpin", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, pinned: false, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=unpinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/delete", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.DeleteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=deleted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/backups/{id:int}/restore", async (
            int id, HttpContext ctx, BackupService backups, IAntiforgery antiforgery,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("RestoreEndpoint");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var confirmed = form["Confirm"] == "true" || form["Confirm"] == "on";
            if (!confirmed)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Tick the confirmation box before restoring."));
                return;
            }
            try
            {
                await backups.RestoreAsync(id, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.OkQuery}=restored");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminBackups, ex);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                logger.LogError(ex, "Restore failed for backup {Id}.", id);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminBackups}?{RouteConstants.MsgQuery}=" + Uri.EscapeDataString("Restore failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        // SiteAdmin break-glass: wipe a user's 2FA when they've lost access
        // to every factor (no recovery code, no TOTP device, no passkey, SMTP
        // down). Logs at info; the audit interceptor captures the row changes
        // for the audit log.
        app.MapPost("/site-admin/users/{id:int}/reset-mfa", async (
            int id, HttpContext ctx, UserAdministrationService userAdmin,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SiteAdminMfaReset");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await userAdmin.ResetMfaAsync(id, ct);
                logger.LogInformation("SiteAdmin reset MFA for user {UserId}.", id);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminUsers}?{RouteConstants.OkQuery}=mfa-reset");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminUsers, ex);
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapGet("/site-admin/backups/{id:int}/download", async (
            int id, HttpContext ctx, BackupService backups, CancellationToken ct) =>
        {
            var opened = await backups.OpenForDownloadAsync(id, ct);
            if (opened is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var (row, stream) = opened.Value;
            try
            {
                ctx.Response.ContentType = "application/octet-stream";
                var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                cd.SetHttpFileName(row.FileName);
                ctx.Response.Headers.ContentDisposition = cd.ToString();
                ctx.Response.ContentLength = row.FileSizeBytes;
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            finally
            {
                await stream.DisposeAsync();
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole));

        app.MapPost("/site-admin/storage/{orgId:int}/backup", async (
            int orgId, HttpContext ctx, PerTenantBackupService backups,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await backups.CreateAsync(orgId, BackupKind.AdHoc, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=created");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminTenantBackups, ex);
            }
            catch (Exception ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Snapshot failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapGet("/site-admin/tenant-backups/{id:int}/download", async (
            int id, HttpContext ctx, PerTenantBackupService backups, CancellationToken ct) =>
        {
            var opened = await backups.OpenForDownloadAsync(id, ct);
            if (opened is null) { ctx.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var (row, stream) = opened.Value;
            try
            {
                ctx.Response.ContentType = "application/zip";
                var cd = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
                cd.SetHttpFileName(row.FileName);
                ctx.Response.Headers.ContentDisposition = cd.ToString();
                ctx.Response.ContentLength = row.FileSizeBytes;
                await stream.CopyToAsync(ctx.Response.Body, ct);
            }
            finally { await stream.DisposeAsync(); }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/{id:int}/upload", async (
            int id, HttpContext ctx, OffsiteBackupService offsite, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                var key = await offsite.UploadPerTenantAsync(id, ct);
                if (key is null)
                {
                    ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                        + Uri.EscapeDataString("Off-site backup not configured, or the local snapshot is missing."));
                    return;
                }
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=uploaded");
            }
            catch (Exception ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Upload failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/offsite/download", async (
            HttpContext ctx, OffsiteBackupService offsite, OffsiteRestoreJobs jobs,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var key = form["ObjectKey"].ToString();
            if (string.IsNullOrWhiteSpace(key))
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Pick a per-tenant object to download."));
                return;
            }
            // Cross-check membership in the listed bucket (under the
            // tenants/ namespace) so a tampered submit can't trigger a
            // download of an arbitrary key. The download method enforces
            // the same shape rules internally — this is belt-and-braces.
            var listed = await offsite.ListPerTenantAsync(maxObjects: 1000, ct);
            var match = listed.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.Ordinal));
            if (match is null)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("That per-tenant object is no longer in the bucket or doesn't look like one of our snapshots."));
                return;
            }
            var id = jobs.Enqueue(OffsiteRestoreJobKind.PerTenant, match.Key, match.FileName);
            ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?job={Uri.EscapeDataString(id.ToString())}");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/{id:int}/pin", async (
            int id, HttpContext ctx, PerTenantBackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, true, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminTenantBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=pinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/{id:int}/unpin", async (
            int id, HttpContext ctx, PerTenantBackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.SetPinnedAsync(id, false, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminTenantBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=unpinned");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/{id:int}/delete", async (
            int id, HttpContext ctx, PerTenantBackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await backups.DeleteAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminTenantBackups, ex);
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=deleted");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/tenant-backups/{id:int}/restore", async (
            int id, HttpContext ctx, PerTenantBackupService backups, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try
            {
                await backups.RestoreAsync(id, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.OkQuery}=restored");
            }
            catch (PlanValidationException ex)
            {
                RedirectFirstError(ctx, RouteConstants.SiteAdminTenantBackups, ex);
            }
            catch (Exception ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminTenantBackups}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Restore failed: " + ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        app.MapPost("/site-admin/storage/{orgId:int}/quota", async (
            int orgId, HttpContext ctx, DatabaseUsageService usage, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var raw = form["QuotaMb"].ToString();
            int? quota = string.IsNullOrWhiteSpace(raw)
                ? null
                : int.TryParse(raw, out var parsed) ? parsed : -1;
            if (quota == -1)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString("Quota must be a non-negative whole number, or blank for the system default."));
                return;
            }
            try
            {
                await usage.SetOrgQuotaAsync(orgId, quota, ct);
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.OkQuery}=quota-saved");
            }
            catch (ArgumentOutOfRangeException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString(ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.SiteAdminStorage}?{RouteConstants.MsgQuery}="
                    + Uri.EscapeDataString(ex.Message));
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.SiteAdminRole))
          .AddEndpointFilter(BlockInSingleTenant);

        return app;
    }

    /// <summary>
    /// Endpoint filter that 404s storage-quota and tenant-snapshot routes when
    /// the deployment runs in single-tenant mode — those surfaces are hidden
    /// from the UI, so their POST/GET handlers must refuse too. Writes a
    /// plain-text body and disables status-code-pages re-execute for the same
    /// reason as <see cref="McpEndpoints"/>: a re-executed GET /not-found
    /// would mismatch the POST binding and surface a 400.
    /// </summary>
    private static async ValueTask<object?> BlockInSingleTenant(
        EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var single = ctx.HttpContext.RequestServices.GetRequiredService<ISingleTenantMode>();
        if (single.IsEnabled)
        {
            var http = ctx.HttpContext;
            var statusCodes = http.Features.Get<IStatusCodePagesFeature>();
            if (statusCodes is not null) statusCodes.Enabled = false;
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            http.Response.ContentType = "text/plain; charset=utf-8";
            await http.Response.WriteAsync("This feature is disabled on this deployment.");
            return null;
        }
        return await next(ctx);
    }
}
