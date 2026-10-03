using System.Security.Claims;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Core login/logout/signup, the email-first verified signup flow,
/// password-reset / magic-link / invite, and the authenticated
/// <c>/auth/account/*</c> self-service posts (password, display name, delete).
/// Split out of <see cref="AccountEndpoints"/> along its original banner
/// boundaries; behaviour is unchanged.
/// </summary>
internal static class AccountAuthEndpoints
{
    public static IEndpointRouteBuilder MapAccountAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // /auth/login: validates the email + password, sets the auth cookie
        // with the user_id / org_id / role / email claims, and triggers
        // seeding for orgs being touched by their first admin login.
        app.MapPost("/auth/login", async (
            HttpContext ctx,
            AuthService auth,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Auth");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var form = await ctx.Request.ReadFormAsync(ct);
            var email = form["Email"].ToString();
            var password = form["Password"].ToString();
            var requestedReturn = form["ReturnUrl"].ToString();
            var safeReturn = ResolveSafeReturn(requestedReturn);
            var ip = ResolveIp(ctx);

            var (outcome, user) = await auth.TryLoginAsync(email, password, ip, ct);

            if (outcome == LoginOutcome.MfaRequired && user is not null)
            {
                // Password verified but a second factor is required. Stash the
                // user id + enrolled methods in a short-lived signed cookie and
                // redirect to /login/challenge.
                SetMfaPendingCookie(ctx, protection, new MfaPending(
                    user.Id, user.TotpEnabled, user.EmailMfaEnabled,
                    clock.GetUtcNow().UtcDateTime, safeReturn));
                ctx.Response.Redirect("/login/challenge");
                return;
            }

            if (outcome != LoginOutcome.Success || user is null)
            {
                var code = outcome switch
                {
                    LoginOutcome.Pending => "pending",
                    LoginOutcome.Disabled => "disabled",
                    LoginOutcome.LockedOut => "locked",
                    LoginOutcome.RateLimited => "rate-limited",
                    LoginOutcome.LocalLoginDisabled => "entra-only",
                    _ => "invalid",
                };
                logger.LogInformation("Login attempt for {Email} from {Ip} resolved {Outcome}.", email, ip, outcome);
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={code}&return={Uri.EscapeDataString(safeReturn)}");
                return;
            }

            await SignInUserAsync(ctx, user, SignInMethod.Password);
            logger.LogInformation("Signed in {Email} (org {OrgId}, role {Role}).", user.Email, user.OrganizationId, user.Role);
            ctx.Response.Redirect(safeReturn);
        });

        app.MapPost("/auth/logout", async (HttpContext ctx, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            ctx.Response.Redirect("/");
        });

        // /auth/signup — two paths:
        //  - Existing-org signup: creates a Pending user, emails the org's
        //    active admins, redirects with a "queued" message.
        //  - New-org signup: auto-approves (we have no superuser to do
        //    otherwise), signs the user in, and lands them on the home page
        //    as the new org's admin.
        // Email send failures log a warning but never roll back the signup.
        app.MapPost("/auth/signup", async (
            HttpContext ctx,
            AccountService accounts,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            PublicOrigin publicOrigin,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            // When SMTP is configured, the email-first verified flow is the only
            // signup path. Refuse this unverified single-form POST so a forged
            // request can't skip verification — the /signup page renders the
            // email-only form in that case and posts to /auth/signup/start.
            if (await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/signup");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var (outcome, user, org) = await accounts.SignupAsync(
                    email: form["Email"].ToString(),
                    displayName: form["DisplayName"].ToString(),
                    password: form["Password"].ToString(),
                    organizationSlug: form["OrganizationSlug"].ToString(),
                    organizationName: form["OrganizationName"].ToString(),
                    ct);

                if (outcome == SignupOutcome.EmailAlreadyTaken)
                {
                    // Don't leak account existence. Return the same response
                    // shape as a queued-pending signup so an attacker can't
                    // tell a registered email apart from a fresh one. The
                    // info-level log retains the discriminator for forensics.
                    logger.LogInformation("Signup attempt with already-registered email — returning generic pending response.");
                    ctx.Response.Redirect("/signup?ok=pending");
                    return;
                }

                if (outcome == SignupOutcome.OrganizationProvisioned && user is not null && org is not null)
                {
                    user.Organization = org;
                    await SignInUserAsync(ctx, user, SignInMethod.Password);
                    logger.LogInformation("Auto-approved new-org signup {Email} as admin of {OrgSlug}.", user.Email, org.Slug);
                    ctx.Response.Redirect("/");
                    return;
                }

                // Existing-org signup: notify the org's active admins so they
                // can approve via /admin/users. SMTP failures don't roll back
                // the signup.
                if (org is not null && await email.IsConfiguredAsync(ct))
                {
                    await AccountEndpoints.NotifyAdminsOfPendingSignupAsync(ctx, db, email, emailRenderer, publicOrigin, org, user!, logger, ct);
                }

                ctx.Response.Redirect("/signup?ok=pending");
            }
            catch (PlanValidationException ex)
            {
                var qs = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/signup?err=invalid&field={Uri.EscapeDataString(qs.Key)}&msg={Uri.EscapeDataString(qs.Value)}");
            }
        });

        // --- Email-first verified signup (when SMTP is configured) ----------
        //
        // Step 1: /auth/signup/start takes just an email and emails a one-time
        // verification link + 6-digit code. Step 2: the visitor verifies via
        // /auth/signup/verify (link) or /auth/signup/verify-code (code), which
        // sets the signed verified-email cookie. Step 3: /signup/details posts
        // the remaining fields to /auth/signup/complete. See
        // .design/auth-and-audit.md.

        app.MapPost("/auth/signup/start", async (
            HttpContext ctx,
            PendingSignupService pending,
            IEmailService email,
            EmailRenderer emailRenderer,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailInput = form["Email"].ToString();
            var ip = ResolveIp(ctx);
            try
            {
                var start = await pending.StartAsync(emailInput, ip, ct);
                if (start is not null && await email.IsConfiguredAsync(ct))
                {
                    var verifyUrl = $"{publicOrigin.For(ctx)}/auth/signup/verify?token={Uri.EscapeDataString(start.LinkToken)}";
                    var content = await SignupVerificationEmail.RenderAsync(emailRenderer, verifyUrl, start.Code, ct);
                    await email.SendAsync(AuthService.NormaliseEmail(emailInput), content, EmailPurpose.SignupVerification, ct);
                }
            }
            catch (Exception ex)
            {
                // A send failure must not betray whether the address exists —
                // log it and fall through to the same generic response.
                logger.LogWarning(ex, "Failed to send signup verification email.");
            }
            // Always identical, regardless of new / already-registered / rate-
            // limited / domain-disallowed: no account enumeration.
            ctx.Response.Redirect($"/signup?ok=check-email&email={Uri.EscapeDataString(emailInput.Trim())}");
        });

        app.MapGet("/auth/signup/verify", async (
            HttpContext ctx,
            PendingSignupService pending,
            IDataProtectionProvider protection,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var token = ctx.Request.Query["token"].ToString();
            var row = await pending.VerifyByTokenAsync(token, ct);
            if (row is null)
            {
                ctx.Response.Redirect("/signup?err=verify-invalid");
                return;
            }
            SetSignupVerifiedCookie(ctx, protection, new SignupVerified(row.Id, row.Email, clock.GetUtcNow().UtcDateTime));
            ctx.Response.Redirect("/signup/details");
        });

        app.MapPost("/auth/signup/verify-code", async (
            HttpContext ctx,
            PendingSignupService pending,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var emailInput = form["Email"].ToString();
            var row = await pending.VerifyByCodeAsync(emailInput, form["Code"].ToString(), ct);
            if (row is null)
            {
                ctx.Response.Redirect($"/signup?err=code-invalid&email={Uri.EscapeDataString(emailInput.Trim())}");
                return;
            }
            SetSignupVerifiedCookie(ctx, protection, new SignupVerified(row.Id, row.Email, clock.GetUtcNow().UtcDateTime));
            ctx.Response.Redirect("/signup/details");
        });

        app.MapPost("/auth/signup/complete", async (
            HttpContext ctx,
            PendingSignupService pending,
            AccountService accounts,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            PublicOrigin publicOrigin,
            IDataProtectionProvider protection,
            TimeProvider clock,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("Signup");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;

            var state = ReadSignupVerifiedCookie(ctx, protection, clock);
            if (state is null)
            {
                ctx.Response.Redirect("/signup");
                return;
            }

            // Authoritative re-check: the cookie is only a hint. The row must
            // still be verified, uncompleted and unexpired.
            var row = await pending.FindVerifiedAsync(state.Email, ct);
            if (row is null)
            {
                ClearSignupVerifiedCookie(ctx);
                ctx.Response.Redirect("/signup?err=verify-invalid");
                return;
            }

            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                var (outcome, user, org) = await accounts.CompleteVerifiedSignupAsync(
                    row,
                    displayName: form["DisplayName"].ToString(),
                    password: form["Password"].ToString(),
                    organizationName: form["OrganizationName"].ToString(),
                    organizationSlug: form["OrganizationSlug"].ToString(),
                    ct);

                ClearSignupVerifiedCookie(ctx);

                if (outcome == SignupOutcome.EmailAlreadyTaken)
                {
                    // Lost a race to an invite/another signup — the account now
                    // exists, so send them to sign in.
                    logger.LogInformation("Verified signup raced an existing account; redirecting to login.");
                    ctx.Response.Redirect(RouteConstants.Login);
                    return;
                }

                if ((outcome == SignupOutcome.OrganizationProvisioned || outcome == SignupOutcome.JoinedActive)
                    && user is not null && org is not null)
                {
                    user.Organization = org;
                    await SignInUserAsync(ctx, user, SignInMethod.Password);
                    logger.LogInformation("Verified signup signed in {Email} (org {OrgSlug}, newOrg={New}).",
                        user.Email, org.Slug, outcome == SignupOutcome.OrganizationProvisioned);
                    ctx.Response.Redirect("/");
                    return;
                }

                // PendingApproval — notify the org's active admins, then show
                // the queued message. SMTP failures don't roll back the signup.
                if (org is not null && await email.IsConfiguredAsync(ct))
                {
                    await AccountEndpoints.NotifyAdminsOfPendingSignupAsync(ctx, db, email, emailRenderer, publicOrigin, org, user!, logger, ct);
                }

                ctx.Response.Redirect("/signup?ok=pending");
            }
            catch (PlanValidationException ex)
            {
                var qs = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/signup/details?err=invalid&field={Uri.EscapeDataString(qs.Key)}&msg={Uri.EscapeDataString(qs.Value)}");
            }
        });

        app.MapPost("/auth/forgot-password", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("ForgotPassword");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/forgot-password?err=not-configured");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var addr = form["Email"].ToString();
            try
            {
                var token = await passwordReset.CreatePasswordResetTokenAsync(addr, ResolveIp(ctx), ct);
                if (token is not null)
                {
                    // Fence category 1 (pre-auth routing): forgot-password, before any cookie exists;
                    // pinned to the typed email.
                    var user = await db.Users.IgnoreQueryFilters().FirstAsync(u => u.Email == addr.Trim().ToLowerInvariant(), ct);
                    var url = $"{publicOrigin.For(ctx)}/reset-password?token={Uri.EscapeDataString(token)}";
                    var content = await PasswordResetEmail.RenderAsync(emailRenderer, user.DisplayName, url, ct);
                    await email.SendAsync(user.Email, content, EmailPurpose.PasswordReset, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Forgot-password flow failed for {Email}.", addr);
            }
            ctx.Response.Redirect("/forgot-password?ok=1");
        });

        // /auth/login/magic — issues a single-use magic-link sign-in token.
        // Always redirects to "ok=1" so the response is identical for known
        // and unknown emails. Per-email and per-IP rate limits applied in
        // PasswordResetService.CreateMagicLoginTokenAsync.
        app.MapPost("/auth/login/magic", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            AppDbContext db,
            IEmailService email,
            EmailRenderer emailRenderer,
            IAntiforgery antiforgery,
            PublicOrigin publicOrigin,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MagicLogin");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            if (!await email.IsConfiguredAsync(ct))
            {
                ctx.Response.Redirect("/login/magic?err=not-configured");
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var addr = form["Email"].ToString();
            try
            {
                var token = await passwordReset.CreateMagicLoginTokenAsync(addr, ResolveIp(ctx), ct);
                if (token is not null)
                {
                    // Fence category 1 (pre-auth routing): magic-link email, before any cookie exists;
                    // pinned to the typed email.
                    var user = await db.Users.IgnoreQueryFilters()
                        .FirstAsync(u => u.Email == addr.Trim().ToLowerInvariant(), ct);
                    var url = $"{publicOrigin.For(ctx)}/auth/login/magic/consume?token={Uri.EscapeDataString(token)}";
                    var content = await MagicLinkEmail.RenderAsync(emailRenderer, user.DisplayName, url, ct);
                    await email.SendAsync(user.Email, content, EmailPurpose.MagicLink, ct);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Magic-link flow failed for {Email}.", addr);
            }
            ctx.Response.Redirect("/login/magic?ok=1");
        });

        app.MapGet("/auth/login/magic/consume", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("MagicLogin");
            var token = ctx.Request.Query["token"].ToString();
            if (string.IsNullOrEmpty(token))
            {
                ctx.Response.Redirect("/login/magic");
                return;
            }
            try
            {
                var user = await passwordReset.ConsumeMagicLoginTokenAsync(token, ct);
                await SignInUserAsync(ctx, user, SignInMethod.MagicLink);
                logger.LogInformation("Magic-link sign-in for {Email} (org {OrgId}).", user.Email, user.OrganizationId);
                ctx.Response.Redirect("/");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"/login/magic?err=invalid&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/accept-invite — invitee submits display name + password.
        // Activates the user into the inviting organisation and signs them in.
        app.MapPost("/auth/accept-invite", async (
            HttpContext ctx,
            InviteService invites,
            IAntiforgery antiforgery,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("AcceptInvite");
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var token = form["Token"].ToString();
            try
            {
                var user = await invites.AcceptAsync(
                    token,
                    form["DisplayName"].ToString(),
                    form["Password"].ToString(),
                    ct);

                await SignInUserAsync(ctx, user, SignInMethod.Password);
                logger.LogInformation("Invite accepted; {Email} signed in to org {OrgId}.",
                    user.Email, user.OrganizationId);
                ctx.Response.Redirect("/");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect(
                    $"/accept-invite?token={Uri.EscapeDataString(token)}&err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/reset-password — consumes the token and applies the new password.
        app.MapPost("/auth/reset-password", async (
            HttpContext ctx,
            PasswordResetService passwordReset,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var token = form["Token"].ToString();
            var password = form["Password"].ToString();
            try
            {
                await passwordReset.ConsumePasswordResetTokenAsync(token, password, ct);
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.OkQuery}=password-reset");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect(
                    $"/reset-password?token={Uri.EscapeDataString(token)}&err={Uri.EscapeDataString(first.Key)}&msg={Uri.EscapeDataString(first.Value)}");
            }
        });

        // /auth/account/* — self-service. All require [Authorize].
        app.MapPost("/auth/account/password", async (
            HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await accounts.ChangePasswordAsync(org.CurrentUserId!.Value,
                    form["CurrentPassword"].ToString(), form["NewPassword"].ToString(), ct);
                // The change invalidates every cookie issued before it (#675),
                // including this one — re-issue it so the user who just changed
                // their own password isn't signed out along with everyone else.
                // Same session: keep its method and strong moment exactly as
                // they were (typing the current password proves nothing new),
                // but a fresh signed_in_at so this cookie survives the cut the
                // change just made. The stamps are mirrored on ctx.User as claims.
                var props = PersistentSignIn(ctx,
                    StepUpAuth.Method(ctx.User) ?? SignInMethod.Password,
                    StepUpAuth.StrongAuthAt(ctx.User),
                    signedInAt: clock.GetUtcNow().UtcDateTime);
                await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ctx.User, props);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=password");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/display-name", async (
            HttpContext ctx, AccountService accounts, IOrganizationContext org, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await accounts.ChangeDisplayNameAsync(org.CurrentUserId!.Value, form["DisplayName"].ToString(), ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=display-name");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        // --- Admin-initiated email change: user-side confirmation -----------

        app.MapGet("/auth/account/email-change/confirm", async (
            HttpContext ctx, UserAdministrationService users, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("EmailChange");
            var token = ctx.Request.Query["token"].ToString();
            var user = await users.ConfirmEmailChangeAsync(token, ct);
            if (user is null)
            {
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=email-change-invalid");
                return;
            }
            // Force the user to re-login from the new address so the cookie's
            // email claim is correct.
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            logger.LogInformation("Email change confirmed for user {UserId}; new email {Email}.", user.Id, user.Email);
            ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.OkQuery}=email-change-confirmed");
        });

        return app;
    }
}
