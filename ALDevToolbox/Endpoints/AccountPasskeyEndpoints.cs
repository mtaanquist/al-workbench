using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using Fido2NetLib;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Passkey self-service + passkey login, and personal access tokens
/// (MCP / non-interactive callers). Split out of <see cref="AccountEndpoints"/>
/// along its original banner boundaries; behaviour is unchanged.
/// </summary>
internal static class AccountPasskeyEndpoints
{
    public static IEndpointRouteBuilder MapAccountPasskeyEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Passkey self-service + login ------------------------------------

        app.MapGet("/account/passkeys/registration-options", async (
            HttpContext ctx, PasskeyService passkeys, IOrganizationContext org, CancellationToken ct) =>
        {
            if (org.CurrentUserId is null) { ctx.Response.StatusCode = 401; return; }
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("Passkeys not configured."); return; }
            var (options, envelope) = await passkeys.BeginRegistrationAsync(org.CurrentUserId.Value, ct);
            ctx.Response.Cookies.Append(PasskeyService.RegistrationCookieName, envelope, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                MaxAge = PasskeyService.ChallengeLifetime,
            });
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(options.ToJson(), ct);
        }).RequireAuthorization();

        app.MapPost("/account/passkeys", async (
            HttpContext ctx, PasskeyService passkeys, IOrganizationContext org, CancellationToken ct) =>
        {
            if (org.CurrentUserId is null) { ctx.Response.StatusCode = 401; return; }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var nickname = doc.RootElement.TryGetProperty("nickname", out var n) ? n.GetString() ?? "Passkey" : "Passkey";
            var rawResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(
                doc.RootElement.GetProperty("response").GetRawText(),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (rawResponse is null) { ctx.Response.StatusCode = 400; return; }
            if (!ctx.Request.Cookies.TryGetValue(PasskeyService.RegistrationCookieName, out var envelope) || string.IsNullOrEmpty(envelope))
            {
                ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Registration session expired."); return;
            }
            ctx.Response.Cookies.Delete(PasskeyService.RegistrationCookieName);
            try
            {
                await passkeys.CompleteRegistrationAsync(org.CurrentUserId.Value, nickname, rawResponse, envelope, ct);
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        }).RequireAuthorization();

        app.MapPost("/auth/account/passkeys/{id:int}/delete", async (
            int id, HttpContext ctx, PasskeyService passkeys, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            await passkeys.DeleteAsync(org.CurrentUserId!.Value, id, ct);
            ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=passkey-deleted");
        }).RequireAuthorization();

        app.MapPost("/auth/account/passkeys/{id:int}/rename", async (
            int id, HttpContext ctx, PasskeyService passkeys, IOrganizationContext org,
            IAntiforgery antiforgery, CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var form = await ctx.Request.ReadFormAsync(ct);
            try
            {
                await passkeys.RenameAsync(org.CurrentUserId!.Value, id, form["Name"].ToString(), ct);
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.OkQuery}=passkey-renamed");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.First();
                ctx.Response.Redirect($"{RouteConstants.Account}?{RouteConstants.ErrQuery}={Uri.EscapeDataString(first.Key)}&{RouteConstants.MsgQuery}={Uri.EscapeDataString(first.Value)}");
            }
        }).RequireAuthorization();

        app.MapPost("/auth/passkey/login/options", async (
            HttpContext ctx, PasskeyService passkeys, CancellationToken ct) =>
        {
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("Passkeys not configured."); return; }
            string? emailHint = null;
            if (ctx.Request.HasFormContentType)
            {
                var form = await ctx.Request.ReadFormAsync(ct);
                emailHint = form["Email"].ToString();
            }
            else if (ctx.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(ctx.Request.Body);
                var body = await reader.ReadToEndAsync(ct);
                if (!string.IsNullOrWhiteSpace(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("email", out var e)) emailHint = e.GetString();
                }
            }
            var (options, envelope) = await passkeys.BeginLoginAsync(emailHint, ct);
            ctx.Response.Cookies.Append(PasskeyService.LoginCookieName, envelope, new CookieOptions
            {
                HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, Path = "/",
                MaxAge = PasskeyService.ChallengeLifetime,
            });
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(options.ToJson(), ct);
        });

        app.MapPost("/auth/passkey/login", async (
            HttpContext ctx, PasskeyService passkeys, ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("PasskeyLogin");
            if (!passkeys.IsConfigured) { ctx.Response.StatusCode = 503; return; }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var rawResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (rawResponse is null) { ctx.Response.StatusCode = 400; return; }
            if (!ctx.Request.Cookies.TryGetValue(PasskeyService.LoginCookieName, out var envelope) || string.IsNullOrEmpty(envelope))
            {
                ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("{\"error\":\"session-expired\"}"); return;
            }
            ctx.Response.Cookies.Delete(PasskeyService.LoginCookieName);
            try
            {
                var (user, userVerified) = await passkeys.CompleteLoginAsync(rawResponse, envelope, ct);
                await SignInUserAsync(ctx, user, userVerified ? SignInMethod.PasskeyVerified : SignInMethod.Passkey);
                logger.LogInformation("Passkey sign-in for {Email}.", user.Email);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"ok\":true}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Passkey login failed.");
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsync(JsonSerializer.Serialize(new { error = ex.Message }));
            }
        });

        // --- Personal access tokens (MCP / non-interactive callers) --------

        // Create: validates input, mints a token, stashes the plaintext in a
        // Data-Protected one-shot cookie, then redirects to the reveal screen.
        // The plaintext is shown exactly once — subsequent visits to the list
        // page show only the prefix.
        app.MapPost("/auth/account/access-tokens/create", async (
            HttpContext ctx,
            PersonalAccessTokenService tokens,
            IOrganizationContext org,
            IDataProtectionProvider protection,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            var userId = org.CurrentUserId;
            var orgId = org.CurrentOrganizationId;
            if (userId is null || orgId is null)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var form = await ctx.Request.ReadFormAsync(ct);
            var name = form["Name"].ToString();
            DateTime? expiresAt = null;
            var ttlDaysRaw = form["TtlDays"].ToString();
            if (int.TryParse(ttlDaysRaw, out var ttlDays) && ttlDays > 0)
            {
                expiresAt = DateTime.UtcNow.AddDays(ttlDays);
            }

            try
            {
                var issued = await tokens.IssueAsync(userId.Value, orgId.Value, name, expiresAt, ct);
                SetOneShotPatCookie(ctx, protection, new OneShotPat(
                    issued.Id, issued.Plaintext, name.Trim(), issued.CreatedAt, issued.ExpiresAt));
                ctx.Response.Redirect("/account/access-tokens/created");
            }
            catch (PlanValidationException ex)
            {
                var first = ex.Errors.FirstOrDefault();
                var msg = string.IsNullOrEmpty(first.Value) ? "Invalid token request." : first.Value;
                ctx.Response.Redirect($"/account?section=ai&err={Uri.EscapeDataString(msg)}");
            }
        }).RequireAuthorization();

        // Revoke: stamps RevokedAt. No-op when the token doesn't belong to
        // the caller's org (the query filter hides it). Redirects back to
        // the list page either way.
        app.MapPost("/auth/account/access-tokens/{id:int}/revoke", async (
            int id,
            HttpContext ctx,
            PersonalAccessTokenService tokens,
            IOrganizationContext org,
            IAntiforgery antiforgery,
            CancellationToken ct) =>
        {
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, ct)) return;
            // Scope to the caller's own tokens: PATs are visible org-wide, so the
            // org filter alone would let any member revoke another's by id (#375).
            await tokens.RevokeAsync(id, ignoreOrgScope: false, forUserId: org.CurrentUserId, ct: ct);
            ctx.Response.Redirect("/account?section=ai&ok=token-revoked");
        }).RequireAuthorization();

        return app;
    }
}
