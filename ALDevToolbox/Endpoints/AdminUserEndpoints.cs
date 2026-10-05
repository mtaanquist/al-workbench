using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// /admin/users/* — approve / reject / disable / enable / invite. Admin-only.
/// The page UI moved to /admin/administration/users (AdminAdministrationUsers.razor)
/// but these POST endpoints keep their original URLs so legacy redirects, email
/// templates, and external links continue to work. Role changes run inside the
/// Blazor circuit (interactive role dropdown on the users page) and call
/// UserAdministrationService.ChangeRoleAsync directly.
/// </summary>
internal static class AdminUserEndpoints
{
    public static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/users/{id:int}/approve", async (
            int id, HttpContext ctx, UserAdministrationService users, AppDbContext db,
            IEmailService email, EmailRenderer emailRenderer, PublicOrigin publicOrigin,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminUsers");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await users.ApproveSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);
            if (await email.IsConfiguredAsync(ct))
            {
                try
                {
                    // Fence category 4 (explicitly scoped lookup): the approval above already refused
                    // any request outside the admin's own org, so this re-read is in-org.
                    var req = await db.SignupRequests.IgnoreQueryFilters()
                        .Include(r => r.User).Include(r => r.Organization)
                        .FirstAsync(r => r.Id == id, ct);
                    if (req.User is not null && req.Organization is not null)
                    {
                        var loginUrl = $"{publicOrigin.For(ctx)}{RouteConstants.Login}";
                        var content = await SignupApprovedEmail.RenderAsync(
                            emailRenderer, req.User.DisplayName, req.Organization.Name, loginUrl, ct);
                        await email.SendAsync(req.User.Email, content, EmailPurpose.SignupDecision, ct);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Approval email failed for signup {Id}.", id);
                }
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/users/{id:int}/reject", async (
            int id, HttpContext ctx, UserAdministrationService users, AppDbContext db,
            IEmailService email, EmailRenderer emailRenderer,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminUsers");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            // Fence category 4 (explicitly scoped lookup): only the email address is read here;
            // the reject call below refuses any request outside the admin's own org.
            var req = await db.SignupRequests.IgnoreQueryFilters()
                .Include(r => r.User).Include(r => r.Organization)
                .FirstOrDefaultAsync(r => r.Id == id, ct);
            var requesterEmail = req?.User?.Email;
            var requesterDisplay = req?.User?.DisplayName ?? "User";
            var orgName = req?.Organization?.Name ?? "Unknown organisation";

            await users.RejectSignupAsync(id, org.CurrentUserId!.Value, org.CurrentOrganizationId!.Value, ct);

            if (await email.IsConfiguredAsync(ct) && !string.IsNullOrEmpty(requesterEmail))
            {
                try
                {
                    var content = await SignupDeclinedEmail.RenderAsync(emailRenderer, requesterDisplay, orgName, ct);
                    await email.SendAsync(requesterEmail, content, EmailPurpose.SignupDecision, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Rejection email failed for signup {Id}.", id);
                }
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/users/{id:int}/disable", async (
            int id, HttpContext ctx, UserAdministrationService users, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await users.DisableUserAsync(id, org.CurrentOrganizationId!.Value, ct); }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
                return;
            }
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/users/{id:int}/enable", async (
            int id, HttpContext ctx, UserAdministrationService users, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await users.EnableUserAsync(id, org.CurrentOrganizationId!.Value, ct);
            ctx.Response.Redirect(RouteConstants.AdminUsers);
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/users/invite", async (
            HttpContext ctx,
            InviteService invites,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            IOrganizationContext orgCtx,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminInvite");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}="
                    + Uri.EscapeDataString("Email isn't set up on this site. Ask a site administrator to configure it, or use the link-only flow above."));
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailAddr = form["Email"].ToString();
            var role = ParseInviteRole(form["Role"].ToString());
            var message = form["WelcomeMessage"].ToString();
            try
            {
                var (token, inviteId) = await invites.CreateAsync(emailAddr, role, message, ct);
                var url = $"{publicOrigin.For(ctx)}/accept-invite?token={Uri.EscapeDataString(token)}";
                // Fence category 4 (explicitly scoped user-id lookup): pinned to the signed-in
                // admin's own id.
                var inviter = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Include(u => u.Organization)
                    .FirstAsync(u => u.Id == orgCtx.CurrentUserId!.Value, ct);
                var orgName = inviter.Organization?.Name ?? "your organisation";
                var roleLabel = FormatRoleLabel(role);
                try
                {
                    var content = await InviteEmail.RenderAsync(
                        emailRenderer, inviter.DisplayName, orgName, roleLabel, message, url, ct);
                    await email.SendAsync(emailAddr.Trim(), content, EmailPurpose.Invite, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Invite email failed for invite {InviteId} to {Email}.", inviteId, emailAddr);
                    // Not ex.Message any more: a send failure is now a failure to
                    // *queue*, so what lands here is database text (constraint and
                    // column names) rather than an SMTP reply, and this reader is an
                    // org Admin. The exception stays in the log.
                    ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}="
                        + Uri.EscapeDataString(
                            "The invite was created, but the email could not be sent. "
                            + "Ask your site administrator to check the email settings."));
                    return;
                }
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.OkQuery}=invited");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        app.MapPost("/admin/users/invites/{id:int}/revoke", async (
            int id, HttpContext ctx, InviteService invites, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            try { await invites.RevokeAsync(id, ct); }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(ex.Errors.First().Value)}");
                return;
            }
            ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.OkQuery}=invite-revoked");
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // Admin-initiated user creation without requiring email. Issues an
        // invite the same way /admin/users/invite does, but surfaces the
        // accept URL in the admin UI (one-shot 60s protected cookie) instead
        // of relying on SMTP. If SMTP is configured AND the admin ticked
        // "SendEmail", also fires the existing invite email template.
        app.MapPost("/admin/users/create", async (
            HttpContext ctx,
            InviteService invites,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            IOrganizationContext orgCtx,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminCreateUser");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailAddr = form["Email"].ToString();
            var role = ParseInviteRole(form["Role"].ToString());
            var message = form["WelcomeMessage"].ToString();
            var sendEmail = form["SendEmail"] == "true" || form["SendEmail"] == "on";
            try
            {
                var (token, inviteId) = await invites.CreateAsync(emailAddr, role, message, ct);
                var url = $"{publicOrigin.For(ctx)}/accept-invite?token={Uri.EscapeDataString(token)}";
                var emailFailed = false;
                if (sendEmail && await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        // Fence category 4 (explicitly scoped user-id lookup): pinned to the signed-in
                        // admin's own id.
                        var inviter = await db.Users.IgnoreQueryFilters().AsNoTracking()
                            .Include(u => u.Organization)
                            .FirstAsync(u => u.Id == orgCtx.CurrentUserId!.Value, ct);
                        var orgName = inviter.Organization?.Name ?? "your organisation";
                        var roleLabel = FormatRoleLabel(role);
                        var content = await InviteEmail.RenderAsync(emailRenderer, inviter.DisplayName, orgName, roleLabel, message, url, ct);
                        await email.SendAsync(emailAddr.Trim(), content, EmailPurpose.Invite, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Invite email failed for invite {InviteId}; the link is still surfaced inline.", inviteId);
                        emailFailed = true;
                    }
                }
                SetOneShotInviteCookie(ctx, protection, url);
                // Delivery is now an explicit choice on the form (#543), so a
                // failed send is a broken promise rather than a background
                // detail: say so. The user still exists and the link is still
                // shown, which is what makes it recoverable.
                var query = emailFailed
                    ? $"{RouteConstants.ErrQuery}=" + Uri.EscapeDataString(
                        "The user was created, but the invite email couldn't be sent. Share the link below instead.")
                      + $"&{RouteConstants.OkQuery}=created"
                    : $"{RouteConstants.OkQuery}=created";
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{query}&inviteId={inviteId}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsersNew}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        // Admin-initiated email change. Stamps users.pending_email, issues a
        // 24h EmailChangeConfirm token, sends a link to the NEW address (or
        // surfaces it inline via the one-shot cookie when SMTP is off).
        app.MapPost("/admin/users/{id:int}/email", async (
            int id, HttpContext ctx,
            UserAdministrationService userAdmin,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            IOrganizationContext orgCtx,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AdminEmailChange");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var newEmail = form["NewEmail"].ToString();
            try
            {
                var token = await userAdmin.RequestEmailChangeAsync(id, newEmail,
                    orgCtx.CurrentOrganizationId!.Value, orgCtx.CurrentUserId!.Value, ct);
                var url = $"{publicOrigin.For(ctx)}/auth/account/email-change/confirm?token={Uri.EscapeDataString(token)}";
                if (await email.IsConfiguredAsync(ct))
                {
                    try
                    {
                        // Fence category 4 (explicitly scoped user-id lookup): the email-change request above
                        // already refused any user outside the admin's own org.
                        var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                            .FirstAsync(u => u.Id == id, ct);
                        var content = await EmailChangeConfirmEmail.RenderAsync(emailRenderer, user.DisplayName, url, ct);
                        await email.SendAsync(newEmail.Trim().ToLowerInvariant(), content, EmailPurpose.EmailChangeConfirmation, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Email-change confirmation email failed for user {Id}; link surfaced inline.", id);
                        SetOneShotInviteCookie(ctx, protection, url);
                    }
                }
                else
                {
                    SetOneShotInviteCookie(ctx, protection, url);
                }
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.OkQuery}=email-pending&userId={id}");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.AdminUsers}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization(policy => policy.RequireRole(HttpOrganizationContext.AdminRole));

        return app;
    }

    private static UserRole ParseInviteRole(string raw) =>
        Enum.TryParse<UserRole>(raw, ignoreCase: true, out var r) ? r : UserRole.User;

    private static string FormatRoleLabel(UserRole role) => role switch
    {
        UserRole.Admin => "Administrator",
        UserRole.Editor => "Editor",
        _ => "User",
    };
}
