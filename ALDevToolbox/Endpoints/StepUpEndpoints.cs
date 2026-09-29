using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Step-up authentication: a signed-in member confirms it is them with a
/// second factor they already have. The flow reuses the login challenge
/// (the same pending cookie, page and code endpoints) because the
/// verification and its throttle are the same; passkeys and Microsoft get
/// their own endpoints here because their ceremonies differ from a typed
/// code. See ".design/auth-and-audit.md", "Step-up for sensitive tools".
/// </summary>
internal static class StepUpEndpoints
{
    public const string Path = "/auth/step-up";

    /// <summary>Auth-properties item: the user a Microsoft step-up is for (never a sign-in when present).</summary>
    public const string EntraStepUpUserIdItem = "entra_step_up_user_id";

    /// <summary>Auth-properties item: when the Microsoft step-up started, so a stale <c>auth_time</c> is refused.</summary>
    public const string EntraStepUpIssuedAtItem = "entra_step_up_issued_at";

    /// <summary>The URL that starts a step-up and returns to <paramref name="returnUrl"/> afterwards.</summary>
    public static string Url(string returnUrl) => $"{Path}?return={Uri.EscapeDataString(returnUrl)}";

    public static IEndpointRouteBuilder MapStepUpEndpoints(this IEndpointRouteBuilder app)
    {
        // Entry: work out which factors this user can confirm with, stash the
        // pending state, and show the challenge page in its step-up dress.
        app.MapGet(Path, async (
            HttpContext ctx, AppDbContext db, IOrganizationContext org, PasskeyService passkeys,
            EntraSignInService entra, IDataProtectionProvider protection, TimeProvider clock,
            CancellationToken ct) =>
        {
            var safeReturn = ResolveSafeReturn(ctx.Request.Query["return"].ToString());
            var now = clock.GetUtcNow().UtcDateTime;
            if (StepUpAuth.IsFresh(ctx.User, now))
            {
                ctx.Response.Redirect(safeReturn);
                return;
            }
            var userId = org.CurrentUserId;
            if (userId is null || ctx.User.HasClaim(c => c.Type == "pat_id"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            // Filtered: the signed-in user's own row, inside their org.
            var user = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId.Value)
                .Select(u => new { u.TotpEnabled, u.EmailMfaEnabled })
                .FirstOrDefaultAsync(ct);
            if (user is null) { ctx.Response.Redirect(RouteConstants.Login); return; }

            // A session that began with a magic link already rests on the
            // mailbox, so an emailed code would be the same factor twice.
            var emailUsable = user.EmailMfaEnabled && StepUpAuth.Method(ctx.User) != SignInMethod.MagicLink;
            var passkeyAvailable = passkeys.IsConfigured
                && await db.UserPasskeys.AnyAsync(p => p.UserId == userId.Value, ct);
            var entraAvailable = await db.UserExternalLogins.AnyAsync(
                    l => l.UserId == userId.Value && l.Provider == EntraSignInService.ProviderName, ct)
                && await entra.ResolveChallengeForCurrentOrgAsync(ct) is not null;

            SetMfaPendingCookie(ctx, protection, new MfaPending(
                userId.Value, user.TotpEnabled, emailUsable, now, safeReturn,
                StepUp: true, PasskeyAvailable: passkeyAvailable, EntraAvailable: entraAvailable));
            ctx.Response.Redirect("/login/challenge");
        }).RequireAuthorization();

        // Passkey, first leg: an allow-list pinned to this user with user
        // verification required. Fetched by wwwroot/js/webauthn.js.
        app.MapPost($"{Path}/passkey/options", async (
            HttpContext ctx, PasskeyService passkeys, IOrganizationContext org,
            IDataProtectionProvider protection, TimeProvider clock, CancellationToken ct) =>
        {
            var state = ReadStepUpState(ctx, org, protection, clock);
            if (state is null) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            try
            {
                var (options, envelope) = await passkeys.BeginStepUpAsync(state.UserId, ct);
                ctx.Response.Cookies.Append(PasskeyService.LoginCookieName, envelope, new CookieOptions
                {
                    HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                    MaxAge = PasskeyService.ChallengeLifetime,
                });
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(options.ToJson(), ct);
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Errors.First().Value }), ct);
            }
        }).RequireAuthorization();

        // Passkey, second leg: verify, then re-stamp the session. Answers JSON
        // with where to go next, because the caller is a fetch, not a form.
        app.MapPost($"{Path}/passkey", async (
            HttpContext ctx, PasskeyService passkeys, AuthService auth, IOrganizationContext org,
            IDataProtectionProvider protection, TimeProvider clock, ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("StepUp");
            var state = ReadStepUpState(ctx, org, protection, clock);
            if (state is null) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var rawResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (rawResponse is null) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!ctx.Request.Cookies.TryGetValue(PasskeyService.LoginCookieName, out var envelope) || string.IsNullOrEmpty(envelope))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("{\"error\":\"session-expired\"}", ct);
                return;
            }
            ctx.Response.Cookies.Delete(PasskeyService.LoginCookieName);
            try
            {
                // Filtered and pinned to the signed-in user; refuses without
                // user verification. Nothing here crosses the tenant fence.
                await passkeys.CompleteStepUpAsync(state.UserId, rawResponse, envelope, ct);
                await CompleteStepUpAsync(ctx, auth, state, SignInMethod.PasskeyVerified, ct, logger, redirect: false);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { ok = true, redirect = ReturnUrlOf(state) }), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Passkey step-up failed.");
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = "That passkey didn't work. Try again." }), ct);
            }
        }).RequireAuthorization();

        // Microsoft: same handshake as sign-in, but with prompt=login (set in
        // AuthenticationRegistration when the step-up item is present) so
        // Microsoft re-runs the tenant's sign-in policy instead of reusing its
        // session. The callback half is EntraAuthEndpoints.OnTicketReceivedAsync.
        app.MapPost($"{Path}/entra", async (
            HttpContext ctx, EntraSignInService entra, IOrganizationContext org, AppDbContext db,
            IDataProtectionProvider protection, TimeProvider clock, IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var state = ReadStepUpState(ctx, org, protection, clock);
            if (state is null) { ctx.Response.Redirect(Url("/")); return; }
            var config = await entra.ResolveChallengeForCurrentOrgAsync(ct);
            if (config is null)
            {
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=entra-unavailable");
                return;
            }
            // Filtered: the user's own linked Microsoft identity, as the login
            // hint (DisplayIdentity is the email the link was made with).
            var hint = await db.UserExternalLogins.AsNoTracking()
                .Where(l => l.UserId == state.UserId && l.Provider == EntraSignInService.ProviderName)
                .Select(l => l.DisplayIdentity)
                .FirstOrDefaultAsync(ct);

            var properties = new AuthenticationProperties { RedirectUri = ReturnUrlOf(state) };
            properties.Items[EntraAuthEndpoints.OrgIdItem] = config.OrganizationId.ToString();
            properties.Items[EntraAuthEndpoints.ClientIdItem] = config.ClientId;
            properties.Items[EntraAuthEndpoints.ConfigSourceItem] = config.ConfigSource;
            properties.Items[EntraStepUpUserIdItem] = state.UserId.ToString();
            properties.Items[EntraStepUpIssuedAtItem] = clock.GetUtcNow().UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(hint)) properties.Items[EntraAuthEndpoints.LoginHintItem] = hint;
            await ctx.ChallengeAsync(EntraAuthEndpoints.AuthenticationScheme, properties);
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Re-issues the session cookie for the signed-in user with the method
    /// that just succeeded, which stamps a fresh strong moment, keeps the
    /// session's original start so a reset that should end it still does,
    /// records the success so the MFA throttle's run of failures is broken,
    /// and clears the pending state. Refuses unless the cookie on this
    /// request belongs to the user the step-up was started for: a pending
    /// state replayed from another session must not upgrade it.
    /// </summary>
    public static async Task CompleteStepUpAsync(HttpContext ctx, AuthService auth, MfaPending state, SignInMethod method, CancellationToken ct, ILogger logger, bool redirect = true)
    {
        var org = ctx.RequestServices.GetRequiredService<IOrganizationContext>();
        if (org.CurrentUserId != state.UserId)
        {
            ClearMfaPendingCookie(ctx);
            ctx.Response.Redirect(RouteConstants.Login);
            return;
        }
        var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
        var clock = ctx.RequestServices.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow().UtcDateTime;
        // Filtered: the user's own row; the Organization nav feeds the claims.
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Organization)
            .FirstAsync(u => u.Id == state.UserId, ct);
        await ReissueUserAsync(ctx, user, method, strongAuthAt: now);
        await auth.RecordAttemptAsync(user.Email, ResolveIp(ctx), succeeded: true, now, ct);
        ClearMfaPendingCookie(ctx);
        logger.LogInformation("Step-up completed with {Method} for {Email} (org {OrgId}).", method, user.Email, user.OrganizationId);
        if (redirect) ctx.Response.Redirect(ReturnUrlOf(state));
    }

    /// <summary>The pending state, only when it is a step-up for the user on this request's cookie.</summary>
    private static MfaPending? ReadStepUpState(HttpContext ctx, IOrganizationContext org, IDataProtectionProvider protection, TimeProvider clock)
    {
        var state = ReadMfaPendingCookie(ctx, protection, clock);
        return state is { StepUp: true } && org.CurrentUserId == state.UserId ? state : null;
    }

    private static string ReturnUrlOf(MfaPending state) =>
        string.IsNullOrEmpty(state.ReturnUrl) ? "/" : ResolveSafeReturn(state.ReturnUrl);
}
