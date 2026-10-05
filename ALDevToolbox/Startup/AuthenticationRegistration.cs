using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ALDevToolbox.Startup;

/// <summary>
/// Sign-in schemes and authorisation policies: the auth cookie, the PAT bearer
/// scheme, Microsoft (Entra ID) sign-in, and the MCP bearer policy.
/// </summary>
public static class AuthenticationRegistration
{
    /// <summary>Registers every authentication scheme and authorisation policy.</summary>
    public static IServiceCollection AddAppAuthentication(this IServiceCollection services)
    {
        // Cookie auth — Milestone P3.13 replaces the single shared password with
        // real accounts. The cookie carries user_id, org_id and the user's role as
        // claims; <c>HttpOrganizationContext</c> reads them to scope EF queries.
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "alwb_auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromDays(30);
                options.SlidingExpiration = true;
                options.LoginPath = "/login";
                options.AccessDeniedPath = "/login";

                // /site-admin/* must return 404, never redirect-to-login or 403 —
                // a 403 would tell an org admin those routes exist. /palette/*
                // must return 401, because it is fetched by a script rather than
                // navigated to. Everything else redirects. Status-code re-execute
                // renders the NotFound page for the 404.
                options.Events.OnRedirectToLogin = StatusInsteadOfRedirect;
                options.Events.OnRedirectToAccessDenied = StatusInsteadOfRedirect;
                // Re-validate the cookie's role / Status / SiteAdmin snapshot against
                // the DB on a throttle so a disable or demotion applies within minutes
                // rather than riding the 30-day cookie to expiry. See issue #412.
                options.Events.OnValidatePrincipal = ALDevToolbox.Endpoints.CookieSessionRevalidation.ValidateAsync;

                static Task StatusInsteadOfRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> ctx)
                {
                    if (ctx.Request.Path.StartsWithSegments(HttpOrganizationContext.SiteAdminPathPrefix))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                        return Task.CompletedTask;
                    }
                    // The command palette fetches its results; it never navigates
                    // to them. A 302 to /login would arrive at the script as a
                    // 200 page of sign-in HTML, indistinguishable from results —
                    // 401 is what lets it say the session has expired instead.
                    if (ctx.Request.Path.StartsWithSegments(PaletteEndpoints.PathPrefix))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }
                    ctx.Response.Redirect(ctx.RedirectUri);
                    return Task.CompletedTask;
                }
            })
            // Bearer-token scheme for Personal Access Tokens. Sits alongside the
            // cookie scheme; only routes that opt in (currently /mcp) declare the
            // "McpBearer" authorisation policy. The handler mounts the same claim
            // set as the cookie path so IOrganizationContext resolves identically.
            .AddScheme<AuthenticationSchemeOptions, ALDevToolbox.Services.Account.PatAuthenticationHandler>(
                ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
                _ => { })
            // Microsoft (Entra ID) sign-in — issue #552 slice 2. One handler serves
            // every org: the app-registration credentials live in the database (per
            // org or deployment-wide), so the static options carry placeholders and
            // the events inject the real client id/secret per request from the
            // AuthenticationProperties stashed at challenge time. The handler never
            // signs anyone in by itself: OnTicketReceived always HandleResponse()s
            // and defers to EntraSignInService, which owns the security checks
            // (tenant allow-list, org routing, account status) and mints the same
            // BuildIdentity cookie as every other sign-in path.
            .AddOpenIdConnect(ALDevToolbox.Endpoints.EntraAuthEndpoints.AuthenticationScheme, options =>
            {
                // The /organizations endpoint serves metadata + signing keys valid
                // for every Entra work tenant; personal Microsoft accounts are
                // excluded by design.
                options.Authority = "https://login.microsoftonline.com/organizations/v2.0";
                // Placeholder — replaced per request in the events below. The
                // handler refuses to start without one.
                options.ClientId = "00000000-0000-0000-0000-000000000000";
                options.CallbackPath = ALDevToolbox.Endpoints.EntraAuthEndpoints.CallbackPath;
                options.ResponseType = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseType.Code;
                // Microsoft returns to the callback with a top-level GET, not
                // the handler's default form_post. A cross-site POST from
                // login.microsoftonline.com does not carry our SameSite=Lax
                // auth cookie, so the step-up and "connect account" callbacks,
                // which must see who is signed in, found nobody and refused
                // every attempt as a mismatch. A GET navigation carries it.
                // The code in the query is single-use and bound by PKCE and
                // the client secret, and the callback redirects straight away.
                options.ResponseMode = Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectResponseMode.Query;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.Scope.Add("email");
                options.GetClaimsFromUserInfoEndpoint = false;
                // Keep the raw JWT claim names (tid/oid/preferred_username) instead
                // of the legacy SOAP-era mappings.
                options.MapInboundClaims = false;
                options.TokenValidationParameters.NameClaimType = "name";
                // The /organizations metadata publishes a templated issuer, so the
                // stock issuer check can't work for a multi-tenant sign-in. We
                // compensate below in OnTokenValidated: the issuer must be exactly
                // https://login.microsoftonline.com/{tid}/v2.0 for the token's own
                // tid, the audience must be the client id we challenged with, and
                // EntraSignInService then enforces the per-org tenant allow-list —
                // which is the actual security boundary (issue #552).
                options.TokenValidationParameters.ValidateIssuer = false;
                options.TokenValidationParameters.ValidateAudience = false;
                options.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = ctx =>
                    {
                        ctx.ProtocolMessage.ClientId = ctx.Properties.Items[ALDevToolbox.Endpoints.EntraAuthEndpoints.ClientIdItem]!;
                        if (ctx.Properties.Items.TryGetValue(ALDevToolbox.Endpoints.EntraAuthEndpoints.LoginHintItem, out var hint)
                            && !string.IsNullOrEmpty(hint))
                        {
                            ctx.ProtocolMessage.LoginHint = hint;
                        }
                        // A step-up must not be satisfied by Microsoft's existing
                        // session: prompt=login forces a fresh interactive sign-in,
                        // and with it whatever MFA the tenant's own policy asks for.
                        // See Endpoints/StepUpEndpoints.cs.
                        // max_age=0 makes the token carry auth_time, which the
                        // callback requires; prompt=login is the belt to that
                        // brace for older tenant behaviour.
                        if (ctx.Properties.Items.ContainsKey(ALDevToolbox.Endpoints.StepUpEndpoints.EntraStepUpUserIdItem))
                        {
                            ctx.ProtocolMessage.Prompt = "login";
                            ctx.ProtocolMessage.MaxAge = "0";
                        }
                        return Task.CompletedTask;
                    },
                    OnAuthorizationCodeReceived = async ctx =>
                    {
                        var items = ctx.Properties!.Items;
                        ctx.TokenEndpointRequest!.ClientId = items[ALDevToolbox.Endpoints.EntraAuthEndpoints.ClientIdItem]!;
                        // The secret is resolved from the DB here rather than stashed
                        // in the (cookie-borne) AuthenticationProperties.
                        var entra = ctx.HttpContext.RequestServices
                            .GetRequiredService<ALDevToolbox.Services.Account.EntraSignInService>();
                        var secret = await entra.GetClientSecretAsync(
                            int.Parse(items[ALDevToolbox.Endpoints.EntraAuthEndpoints.OrgIdItem]!),
                            items[ALDevToolbox.Endpoints.EntraAuthEndpoints.ConfigSourceItem]!,
                            ctx.HttpContext.RequestAborted);
                        if (secret is not null) ctx.TokenEndpointRequest.ClientSecret = secret;
                    },
                    OnTokenValidated = ctx =>
                    {
                        var expectedAudience = ctx.Properties!.Items[ALDevToolbox.Endpoints.EntraAuthEndpoints.ClientIdItem];
                        var tid = ctx.Principal?.FindFirst("tid")?.Value;
                        var iss = ctx.Principal?.FindFirst("iss")?.Value;
                        var audMatches = ctx.Principal?.FindAll("aud")
                            .Any(a => string.Equals(a.Value, expectedAudience, StringComparison.OrdinalIgnoreCase)) == true;
                        if (!audMatches)
                        {
                            ctx.Fail("id_token audience does not match the challenged client id.");
                            return Task.CompletedTask;
                        }
                        if (string.IsNullOrEmpty(tid)
                            || !string.Equals(iss, $"https://login.microsoftonline.com/{tid}/v2.0", StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Fail("id_token issuer does not match its tenant id.");
                        }
                        return Task.CompletedTask;
                    },
                    OnTicketReceived = ALDevToolbox.Endpoints.EntraAuthEndpoints.OnTicketReceivedAsync,
                    OnRemoteFailure = ctx =>
                    {
                        ctx.HandleResponse();
                        ctx.Response.Redirect($"{RouteConstants.Login}?{RouteConstants.ErrQuery}=entra-failed");
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization(options =>
        {
            // McpBearer accepts EITHER a PAT (aldt_pat_…) OR an OAuth access token
            // issued by our own OpenIddict server. Same downstream claims, same
            // tenant scoping — the difference is invisible to the MCP tools.
            options.AddPolicy(McpBearerPolicy.Name, policy =>
            {
                policy.AuthenticationSchemes = new[]
                {
                    ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
                    OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                };
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(HttpOrganizationContext.UserIdClaim);
                policy.RequireClaim(HttpOrganizationContext.OrganizationIdClaim);
            });
            // Kept under the old name for one release so anything that hard-coded
            // "PAT" as the policy keeps working while it migrates.
            options.AddPolicy(ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme, policy =>
            {
                policy.AuthenticationSchemes = new[]
                {
                    ALDevToolbox.Services.Account.PatAuthenticationHandler.AuthenticationScheme,
                    OpenIddict.Validation.AspNetCore.OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme,
                };
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(HttpOrganizationContext.UserIdClaim);
                policy.RequireClaim(HttpOrganizationContext.OrganizationIdClaim);
            });
        });
        services.AddCascadingAuthenticationState();
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        return services;
    }
}
