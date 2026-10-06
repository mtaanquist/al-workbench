using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Email;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Microsoft (Entra ID) sign-in endpoints — issue #552 slice 2. The
/// challenge endpoint routes the sign-in to an organisation and hands the
/// OIDC handler its per-request app-registration credentials via
/// <see cref="AuthenticationProperties"/>; the callback logic lives in
/// <see cref="OnTicketReceivedAsync"/> (wired from the handler options in
/// <c>Program.cs</c>) and defers every security decision to
/// <see cref="EntraSignInService"/>.
/// </summary>
internal static class EntraAuthEndpoints
{
    public const string AuthenticationScheme = "EntraId";

    /// <summary>
    /// The redirect URI path registered in Entra. Displayed to admins on
    /// both settings pages — changing it invalidates every registration,
    /// so don't. See .design/auth-and-audit.md.
    /// </summary>
    public const string CallbackPath = "/signin-microsoft";

    // AuthenticationProperties item keys carried through the handshake.
    public const string OrgIdItem = "entra_org_id";
    public const string ClientIdItem = "entra_client_id";
    public const string ConfigSourceItem = "entra_config_source";
    public const string LoginHintItem = "entra_login_hint";
    /// <summary>Present (as the acting user's id) when the handshake is a /account "connect" rather than a sign-in.</summary>
    public const string LinkUserIdItem = "entra_link_user_id";

    public static IEndpointRouteBuilder MapEntraAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Posted from the login form ("Sign in with Microsoft" submits the
        // same form via formaction, so the typed email rides along as the
        // routing + login hint).
        app.MapPost("/auth/entra/challenge", async (
            HttpContext ctx, EntraSignInService entra, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            var email = form["Email"].ToString();
            var safeReturn = ResolveSafeReturn(form["ReturnUrl"].ToString());

            var (config, errorCode) = await entra.ResolveChallengeAsync(email, ct);
            if (config is null)
            {
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={errorCode}&return={Uri.EscapeDataString(safeReturn)}");
                return;
            }

            var properties = new AuthenticationProperties { RedirectUri = safeReturn };
            properties.Items[OrgIdItem] = config.OrganizationId.ToString();
            properties.Items[ClientIdItem] = config.ClientId;
            properties.Items[ConfigSourceItem] = config.ConfigSource;
            if (!string.IsNullOrWhiteSpace(email)) properties.Items[LoginHintItem] = email.Trim();
            await ctx.ChallengeAsync(AuthenticationScheme, properties);
        });

        // "Connect Microsoft account" on /account — same handshake, but the
        // callback links the identity to the already-signed-in user instead
        // of signing anyone in.
        app.MapPost("/auth/entra/link", async (
            HttpContext ctx, EntraSignInService entra, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var userId = CurrentUserId(ctx);
            if (userId is null) { ctx.Response.Redirect(RouteConstants.Login); return; }

            // Authenticated request: the org comes from the caller's own
            // cookie claims, so the service reads stay inside the query filter.
            var config = await entra.ResolveChallengeForCurrentOrgAsync(ct);
            if (config is null)
            {
                ctx.Response.Redirect("/account/security?err=" + Uri.EscapeDataString("Microsoft sign-in") + "&msg="
                    + Uri.EscapeDataString("Microsoft sign-in isn't set up for your organisation yet. An admin can turn it on under Administration."));
                return;
            }

            var properties = new AuthenticationProperties { RedirectUri = "/account/security?ok=ms-linked" };
            properties.Items[OrgIdItem] = config.OrganizationId.ToString();
            properties.Items[ClientIdItem] = config.ClientId;
            properties.Items[ConfigSourceItem] = config.ConfigSource;
            properties.Items[LinkUserIdItem] = userId.Value.ToString();
            await ctx.ChallengeAsync(AuthenticationScheme, properties);
        }).RequireAuthorization();

        app.MapPost("/auth/entra/link/{id:int}/remove", async (
            int id, HttpContext ctx, EntraSignInService entra, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var userId = CurrentUserId(ctx);
            if (userId is null) { ctx.Response.Redirect(RouteConstants.Login); return; }
            try
            {
                await entra.UnlinkAsync(userId.Value, id, ct);
                ctx.Response.Redirect("/account/security?ok=ms-unlinked");
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect("/account/security?err=" + Uri.EscapeDataString("Microsoft sign-in") + "&msg="
                    + Uri.EscapeDataString(ex.Errors.First().Value));
            }
        }).RequireAuthorization();

        return app;
    }

    /// <summary>
    /// Whether a Microsoft step-up callback counts as a fresh interactive
    /// sign-in. The handshake sends <c>prompt=login</c> and <c>max_age=0</c>,
    /// but Entra only puts <c>auth_time</c> in an ID token when the app
    /// registration lists it as an optional claim; most registrations do not,
    /// and refusing every token without it locked Microsoft users out of
    /// step-up entirely (found the day this shipped). So: when the claim is
    /// there it is enforced, no older than the step-up started with a minute
    /// of skew; when it is absent the callback trusts <c>prompt=login</c>,
    /// and logs that the verification was client-side only.
    /// </summary>
    internal static bool StepUpReauthenticated(DateTime? authTime, DateTime issuedAt) =>
        authTime is not { } at || at >= issuedAt.AddMinutes(-1);

    /// <summary>The token's <c>auth_time</c> (seconds since the epoch) as UTC, or null when Microsoft did not include it.</summary>
    private static DateTime? AuthTimeOf(ClaimsPrincipal principal) =>
        long.TryParse(principal.FindFirst("auth_time")?.Value, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;

    private static int? CurrentUserId(HttpContext ctx) =>
        int.TryParse(ctx.User.FindFirst(ALDevToolbox.Services.HttpOrganizationContext.UserIdClaim)?.Value, out var id)
            ? id : null;

    /// <summary>
    /// The OIDC handler's terminal event. Always handles the response
    /// itself — the handler never signs its (external) principal into our
    /// cookie; a successful resolution mints the standard
    /// <see cref="EndpointHelpers.BuildIdentity"/> cookie instead, so the
    /// downstream (query filters, revalidation, role gates) can't tell a
    /// federated sign-in from a password one.
    /// </summary>
    public static async Task OnTicketReceivedAsync(TicketReceivedContext ctx)
    {
        ctx.HandleResponse();
        var ct = ctx.HttpContext.RequestAborted;
        var principal = ctx.Principal!;
        var token = new EntraTokenIdentity(
            TenantId: principal.FindFirst("tid")?.Value ?? string.Empty,
            ObjectId: principal.FindFirst("oid")?.Value ?? string.Empty,
            Email: principal.FindFirst("preferred_username")?.Value ?? principal.FindFirst("email")?.Value,
            DisplayName: principal.FindFirst("name")?.Value,
            // xms_edov: Microsoft's own "the tenant owns this email domain"
            // signal. Only a true value counts as evidence — the claim is
            // absent in plenty of tenants, and absence is not verification.
            EmailVerified: string.Equals(principal.FindFirst("xms_edov")?.Value, "true", StringComparison.OrdinalIgnoreCase)
                || principal.FindFirst("xms_edov")?.Value == "1");
        var safeReturn = ResolveSafeReturn(ctx.Properties?.RedirectUri ?? "/");

        if (token.TenantId.Length == 0 || token.ObjectId.Length == 0)
        {
            ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=entra-failed");
            return;
        }

        var entra = ctx.HttpContext.RequestServices.GetRequiredService<EntraSignInService>();

        // Step-up mode: a signed-in user confirming it is them for a sensitive
        // tool (StepUpEndpoints). Never a sign-in: the identity that came back
        // must already be linked to the user on this request's cookie, and the
        // sign-in must be newer than the step-up started when Microsoft says
        // when it happened (see StepUpReauthenticated for when it does not).
        var authTime = AuthTimeOf(principal);
        if (ctx.Properties?.Items.TryGetValue(StepUpEndpoints.EntraStepUpUserIdItem, out var stepUpUserRaw) == true
            && int.TryParse(stepUpUserRaw, out var stepUpUserId))
        {
            var cookie = await ctx.HttpContext.AuthenticateAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
            if (cookie.Principal is not null) ctx.HttpContext.User = cookie.Principal;
            var logger = ctx.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>().CreateLogger("StepUp");
            var issuedAt = ctx.Properties.Items.TryGetValue(StepUpEndpoints.EntraStepUpIssuedAtItem, out var issuedRaw)
                && DateTime.TryParse(issuedRaw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedIssuedAt)
                ? parsedIssuedAt : DateTime.MaxValue;
            var sameUser = CurrentUserId(ctx.HttpContext) == stepUpUserId;
            var reauthenticated = StepUpReauthenticated(authTime, issuedAt);
            var linked = sameUser && await entra.IsLinkedAsync(stepUpUserId, token, ct);
            if (!sameUser || !reauthenticated || !linked)
            {
                // Which check refused is what an operator needs when a person
                // reports the "didn't match your account" message.
                logger.LogWarning(
                    "Microsoft step-up refused for user {UserId}: sameUser={SameUser}, reauthenticated={Reauthenticated}, linked={Linked}, authTime={AuthTime}.",
                    stepUpUserId, sameUser, reauthenticated, linked, authTime);
                ctx.Response.Redirect($"/login/challenge?{RouteConstants.ErrQuery}=entra-mismatch");
                return;
            }
            if (authTime is null)
            {
                logger.LogInformation(
                    "Microsoft step-up for user {UserId} accepted on prompt=login alone: the token carried no auth_time. "
                    + "Add auth_time as an optional ID token claim on the app registration to have it verified server-side.",
                    stepUpUserId);
            }
            var protection = ctx.HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
            var clock = ctx.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            var pending = ReadMfaPendingCookie(ctx.HttpContext, protection, clock)
                ?? new MfaPending(stepUpUserId, false, false, clock.GetUtcNow().UtcDateTime, safeReturn, StepUp: true);
            var auth = ctx.HttpContext.RequestServices.GetRequiredService<AuthService>();
            await StepUpEndpoints.CompleteStepUpAsync(ctx.HttpContext, auth, pending with { ReturnUrl = safeReturn }, SignInMethod.Entra, ct, logger);
            return;
        }

        // Link mode: a signed-in user connecting a Microsoft account from
        // /account. The link target rides in the protected properties, and
        // the auth cookie on this request must belong to the same user — a
        // stolen callback URL replayed from another session must not link.
        if (ctx.Properties?.Items.TryGetValue(LinkUserIdItem, out var linkUserRaw) == true
            && int.TryParse(linkUserRaw, out var linkUserId))
        {
            // A remote-auth handler runs as an IAuthenticationRequestHandler,
            // which the authentication middleware invokes *before* it fills
            // HttpContext.User from the cookie scheme. Restore the principal
            // ourselves: the identity check below needs it, and so does the
            // org query filter that scopes the linking reads.
            var cookie = await ctx.HttpContext.AuthenticateAsync(
                CookieAuthenticationDefaults.AuthenticationScheme);
            if (cookie.Principal is not null) ctx.HttpContext.User = cookie.Principal;

            if (CurrentUserId(ctx.HttpContext) != linkUserId)
            {
                ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=entra-failed");
                return;
            }
            try
            {
                await entra.LinkAsync(linkUserId, token, ct);
                ctx.Response.Redirect("/account/security?ok=ms-linked");
            }
            catch (PlanValidationException ex)
            {
                ctx.Response.Redirect("/account/security?err=" + Uri.EscapeDataString("Microsoft sign-in") + "&msg="
                    + Uri.EscapeDataString(ex.Errors.First().Value));
            }
            return;
        }

        var result = await entra.CompleteAsync(token, ResolveIp(ctx.HttpContext), ct);

        if (result.Outcome == EntraCompletionOutcome.Success && result.User is not null)
        {
            // Reload with the Organization nav so BuildIdentity can stamp
            // the org-name / MCP / tool claims, mirroring TryLoginAsync.
            // Strong only when Microsoft says when the person authenticated:
            // without auth_time this may be a silently reused Microsoft
            // session, which proves nothing about who is at the keyboard.
            if (authTime is { } strongAt)
            {
                await SignInUserAsync(ctx.HttpContext, result.User, SignInMethod.Entra, strongAt);
            }
            else
            {
                await SignInUserAsync(ctx.HttpContext, result.User, SignInMethod.EntraSso);
            }
            ctx.Response.Redirect(safeReturn);
            return;
        }

        if (result.Outcome == EntraCompletionOutcome.PendingApproval
            && result.User is { Organization: not null } jitUser)
        {
            // Same admin heads-up the password signup flow sends; failures
            // log and never surface to the visitor.
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var email = ctx.HttpContext.RequestServices.GetRequiredService<IEmailService>();
            var emailRenderer = ctx.HttpContext.RequestServices.GetRequiredService<EmailRenderer>();
            var publicOrigin = ctx.HttpContext.RequestServices.GetRequiredService<PublicOrigin>();
            var logger = ctx.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>().CreateLogger("EntraSignIn");
            await AccountEndpoints.NotifyAdminsOfPendingSignupAsync(
                ctx.HttpContext, db, email, emailRenderer, publicOrigin, jitUser.Organization, jitUser, logger, ct);
        }

        var code = result.Outcome switch
        {
            EntraCompletionOutcome.PendingApproval => "entra-pending",
            EntraCompletionOutcome.AccountPending => "pending",
            EntraCompletionOutcome.AccountDisabled => "disabled",
            EntraCompletionOutcome.TenantNotAllowed => "entra-tenant",
            EntraCompletionOutcome.Ambiguous => "entra-ambiguous",
            EntraCompletionOutcome.EmailMissing => "entra-failed",
            EntraCompletionOutcome.EmailTakenElsewhere => "entra-email-taken",
            EntraCompletionOutcome.EmailNotVerified => "entra-email-unverified",
            EntraCompletionOutcome.IdentityTakenElsewhere => "entra-identity-taken",
            _ => "entra-failed",
        };
        ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}={code}");
    }
}
