using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// The login MFA challenge (TOTP / email-code / recovery-code) and the
/// authenticated 2FA self-service posts (TOTP confirm/disable, email-MFA
/// begin/confirm/disable, recovery-code regeneration). Split out of
/// <see cref="AccountEndpoints"/> along its original banner boundaries;
/// behaviour is unchanged.
/// </summary>
internal static class AccountMfaEndpoints
{
    public static IEndpointRouteBuilder MapAccountMfaEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Login MFA challenge -------------------------------------------

        app.MapPost("/auth/login/challenge/totp", async (
            HttpContext ctx, AuthService auth, TotpService totp,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            // Brute-force guard: the attacker already has the password (that's
            // how they reached the challenge), so cap wrong second-factor
            // guesses before verifying. See #477.
            if (await auth.IsMfaThrottledAsync(state.UserId, clock.GetUtcNow().UtcDateTime, ct))
            {
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=too-many-attempts");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await totp.VerifyAsync(state.UserId, code, ct))
            {
                await auth.RecordMfaFailureAsync(state.UserId, ResolveIp(ctx), ct);
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, SignInMethod.Totp, ct, logger);
        });

        app.MapPost("/auth/login/challenge/email/issue", async (
            HttpContext ctx, EmailMfaService mfa, IEmailService emailSvc, AppDbContext db,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }

            try
            {
                if (!await emailSvc.IsConfiguredAsync(ct))
                {
                    ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=not-configured");
                    return;
                }
                var code = await mfa.IssueChallengeAsync(state.UserId, ct);
                if (code is null)
                {
                    ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=rate-limited");
                    return;
                }
                // Fence category 1 (pre-auth routing): MFA challenge between the two login legs;
                // pinned to u.Id == state.UserId from the signed challenge state.
                var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == state.UserId)
                    .Select(u => new { u.Email, u.DisplayName })
                    .FirstAsync(ct);
                var (subject, body) = EmailTemplates.MfaEmailCode(user.DisplayName, code);
                await emailSvc.SendAsync(user.Email, subject, body, EmailPurpose.MfaCode, ct);
                ctx.Response.Redirect($"/login/challenge?method=email&{RouteConstants.OkQuery}=sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email MFA challenge failed.");
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=invalid");
            }
        });

        app.MapPost("/auth/login/challenge/email/verify", async (
            HttpContext ctx, AuthService auth, EmailMfaService mfa,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await mfa.VerifyAsync(state.UserId, code, ct))
            {
                ctx.Response.Redirect($"/login/challenge?method=email&{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, SignInMethod.EmailCode, ct, logger);
        });

        app.MapPost("/auth/login/challenge/recovery", async (
            HttpContext ctx, AuthService auth, RecoveryCodeService recovery,
            IDataProtectionProvider protection, TimeProvider clock,
            IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MfaChallenge");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadMfaPendingCookie(ctx, protection, clock);
            if (state is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            // Same brute-force guard as the TOTP path — recovery codes are a
            // longer but still finite space, and the counter is shared so an
            // attacker can't dodge it by alternating methods. See #477.
            if (await auth.IsMfaThrottledAsync(state.UserId, clock.GetUtcNow().UtcDateTime, ct))
            {
                ctx.Response.Redirect($"/login/challenge?method=recovery&{RouteConstants.ErrQuery}=too-many-attempts");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var code = form["Code"].ToString();
            if (!await recovery.ConsumeAsync(state.UserId, code, ct))
            {
                await auth.RecordMfaFailureAsync(state.UserId, ResolveIp(ctx), ct);
                ctx.Response.Redirect($"/login/challenge?method=recovery&{RouteConstants.ErrQuery}=invalid");
                return;
            }
            await CompleteMfaSignIn(ctx, auth, state, SignInMethod.RecoveryCode, ct, logger);
        });

        // --- TOTP self-service ----------------------------------------------

        app.MapPost("/auth/account/2fa/totp/confirm", async (
            HttpContext ctx, TotpService totp, IOrganizationContext org,
            IDataProtectionProvider protection, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var codes = await totp.ConfirmEnrollmentAsync(org.CurrentUserId!.Value, form["Code"].ToString(), ct);
                // Stash the plaintext codes in a 60-second protected cookie so the
                // recovery-codes page can render them once.
                SetOneShotRecoveryCodesCookie(ctx, protection, codes);
                ctx.Response.Redirect("/account/2fa/recovery-codes?show=1");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/account/2fa/totp/setup?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/totp/disable", async (
            HttpContext ctx, TotpService totp, AuthService auth, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await AccountEndpoints.LoadUserAndVerifyPasswordAsync(ctx, db, auth, org, form["Password"].ToString(), ct);
            if (user is null) return;
            await totp.DisableAsync(user.Id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=totp-disabled");
        }).RequireAuthorization();

        // --- Email-MFA self-service -----------------------------------------

        app.MapPost("/auth/account/2fa/email/begin", async (
            HttpContext ctx, EmailMfaService mfa, IEmailService emailSvc, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("EmailMfaSetup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await emailSvc.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("SMTP isn't configured. Ask a SiteAdmin.")}");
                return;
            }
            try
            {
                var code = await mfa.IssueChallengeAsync(org.CurrentUserId!.Value, ct);
                if (code is null)
                {
                    ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Try again in a few minutes.")}");
                    return;
                }
                // Fence category 4 (explicitly scoped user-id lookup): pinned to the signed-in
                // user's own id.
                var user = await db.Users.IgnoreQueryFilters().AsNoTracking()
                    .Where(u => u.Id == org.CurrentUserId!.Value)
                    .Select(u => new { u.Email, u.DisplayName })
                    .FirstAsync(ct);
                var (subject, body) = EmailTemplates.MfaEmailCode(user.DisplayName, code);
                await emailSvc.SendAsync(user.Email, subject, body, EmailPurpose.MfaCode, ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-sent");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Email-MFA setup failed.");
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Email&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Failed to send code.")}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/email/confirm", async (
            HttpContext ctx, EmailMfaService mfa, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            if (!await mfa.VerifyAsync(org.CurrentUserId!.Value, form["Code"].ToString(), ct))
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Code&{RouteConstants.MsgQuery}={Uri.EscapeDataString("That code didn't match. Request a new one.")}");
                return;
            }
            await mfa.EnableAsync(org.CurrentUserId!.Value, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-enabled");
        }).RequireAuthorization();

        app.MapPost("/auth/account/2fa/email/disable", async (
            HttpContext ctx, EmailMfaService mfa, AuthService auth, AppDbContext db,
            IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await AccountEndpoints.LoadUserAndVerifyPasswordAsync(ctx, db, auth, org, form["Password"].ToString(), ct);
            if (user is null) return;
            await mfa.DisableAsync(user.Id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=email-mfa-disabled");
        }).RequireAuthorization();

        // --- Recovery codes regen -------------------------------------------

        app.MapPost("/auth/account/2fa/recovery/regenerate", async (
            HttpContext ctx, RecoveryCodeService recovery, AuthService auth, AppDbContext db,
            IOrganizationContext org, IDataProtectionProvider protection,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var user = await AccountEndpoints.LoadUserAndVerifyPasswordAsync(ctx, db, auth, org, form["Password"].ToString(), ct);
            if (user is null) return;
            if (!user.TotpEnabled)
            {
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}=Totp&{RouteConstants.MsgQuery}={Uri.EscapeDataString("Enable TOTP first.")}");
                return;
            }
            var codes = await recovery.RegenerateAsync(user.Id, ct);
            SetOneShotRecoveryCodesCookie(ctx, protection, codes);
            ctx.Response.Redirect("/account/2fa/recovery-codes?show=1");
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Finalises an MFA-gated login: completes the second factor, issues the
    /// auth cookie, clears the pending-MFA cookie and redirects to the stashed
    /// return URL. Shared by the TOTP / email-code / recovery-code challenge
    /// handlers (was a local function inside the original registration method).
    /// </summary>
    private static async Task CompleteMfaSignIn(HttpContext ctx, AuthService auth, MfaPending state, SignInMethod method, CancellationToken ct, ILogger logger)
    {
        if (state.StepUp)
        {
            await StepUpEndpoints.CompleteStepUpAsync(ctx, state, method, ct, logger);
            return;
        }
        var user = await auth.CompleteMfaAsync(state.UserId, ResolveIp(ctx), ct);
        await SignInUserAsync(ctx, user, method);
        ClearMfaPendingCookie(ctx);
        logger.LogInformation("MFA-gated sign-in completed for {Email} (org {OrgId}).", user.Email, user.OrganizationId);
        ctx.Response.Redirect(string.IsNullOrEmpty(state.ReturnUrl) ? "/" : state.ReturnUrl);
    }
}
