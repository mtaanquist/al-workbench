using ALDevToolbox.Services.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Data;

namespace ALDevToolbox.Startup;

/// <summary>
/// The OAuth 2.1 server (OpenIddict) that gives /mcp its second accepted
/// credential alongside PATs.
/// </summary>
public static class OAuthServerRegistration
{
    /// <summary>Registers the OpenIddict server, its validation half and the CIMD resolver.</summary>
    public static IServiceCollection AddOAuthServer(this IServiceCollection services, IHostEnvironment environment)
    {
        // OAuth 2.1 server (OpenIddict) — adds the second accepted credential for
        // /mcp so Claude.ai's directory and custom-connector flows can connect.
        // Both schemes (PAT + OAuth) feed identical claims via OAuthClaimsTransformer,
        // so MCP tools see the same principal whichever path authenticated the call.
        // Discovery metadata is customised in OAuthEndpoints to advertise CIMD; the
        // hand-rolled resource-metadata endpoint (RFC 9728) is registered there too.
        //
        // Persistent signing + encryption keys live on the same app-keys volume as
        // the Data Protection ring (separate env var so operators who want to
        // isolate them can). Loaded before AddOpenIddict because OpenIddict's
        // builder needs them at config time, not at first use. Falls back to
        // in-memory keys with a warning when the directory isn't writable —
        // previously the prod default was always-ephemeral, so the fallback path
        // is a strict superset of what shipped before.
        var oauthKeyDir = BootKeyPaths.KeyDirectory();
        var oauthKeyLogger = BootKeyPaths.KeyMaterialLogger();
        var (oauthSigningKey, oauthEncryptionKey) = ALDevToolbox.Services.OAuth.OAuthKeyMaterial.LoadOrCreate(oauthKeyDir, oauthKeyLogger);

        services.AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .AddServer(o =>
            {
                o.SetAuthorizationEndpointUris("/oauth/authorize")
                    .SetTokenEndpointUris("/oauth/token")
                    .SetRevocationEndpointUris("/oauth/revoke")
                    .SetIntrospectionEndpointUris("/oauth/introspect")
                    .SetEndSessionEndpointUris("/oauth/logout");
                // DCR (RFC 7591) is hand-rolled at /oauth/register in OAuthEndpoints.cs
                // — OpenIddict 7.5.0's server builder doesn't expose a first-class
                // SetClientRegistrationEndpointUris(), so we write through
                // IOpenIddictApplicationManager from a minimal API instead, and
                // surface registration_endpoint via the discovery customisation.

                o.AllowAuthorizationCodeFlow()
                    .AllowRefreshTokenFlow();
                // Claude requires S256 PKCE on every authorisation request; plain
                // is rejected by OpenIddict automatically once this is set.
                o.RequireProofKeyForCodeExchange();

                // Single resource scope today. offline_access enables refresh
                // tokens — Claude appends it when our discovery metadata
                // advertises it in scopes_supported.
                o.RegisterScopes("mcp", OpenIddict.Abstractions.OpenIddictConstants.Scopes.OfflineAccess);

                // MCP clients (Claude web's custom connector, Claude Code) include
                // a `resource` parameter on every authorise + token request per the
                // MCP 2025-11-25 spec (RFC 8707 — Resource Indicators). OpenIddict
                // gates such requests in two places, both of which we opt out of:
                //
                //   * DisableResourceValidation removes the stock ValidateResources
                //     handler that compares the request value against the in-memory
                //     OpenIddictServerOptions.Resources allowlist (populated at
                //     startup via RegisterResources()).
                //   * IgnoreResourcePermissions removes ValidateResourcePermissions,
                //     the per-client check that requires the client's Permissions
                //     collection to carry "rsrc:" + <resource_url>. CIMD- and
                //     DCR-registered clients are created without that permission
                //     and we don't know the canonical URL when CimdClientResolver
                //     runs in a way that survives existing rows.
                //
                // We don't know the public host when the host builds (no PublicUrl
                // config; deployments use the request's Forwarded-* headers as the
                // source of truth), and attempts to mutate state dynamically from
                // pre-validator event handlers didn't take effect — see the
                // PR #191 / #192 retrospectives. Both checks are defence-in-depth
                // for servers fronting multiple resources; ALDevToolbox exposes a
                // single protected resource (/mcp), so disabling them only removes a
                // cross-resource confused-deputy guard that doesn't apply here.
                // NB: McpBearerPolicy gates /mcp on authentication + the user/org
                // claims — it does NOT separately assert the token audience, so don't
                // rely on it as an audience check. If a second protected resource is
                // ever added, re-enable resource validation (or add an explicit
                // audience requirement) before doing so.
                //
                // TODO: Revisit once OpenIddict ships native DCR / CIMD support
                // (tracked in openiddict/openiddict-core#2404, targeted at 7.6.0)
                // — that release will likely introduce a more idiomatic way to
                // register resources dynamically from a CIMD application descriptor,
                // at which point both opt-outs can come back on.
                o.DisableResourceValidation();
                o.IgnoreResourcePermissions();

                // Reuse the existing Data Protection key ring (mounted on the
                // app-keys volume) for token format wrapping. Losing the key ring
                // already invalidates auth cookies and the system_settings SMTP
                // ciphertext, so OAuth tokens sharing its fate isn't a new failure
                // mode.
                o.UseDataProtection();

                // OpenIddict additionally requires signing + encryption keys for
                // the JWKS endpoint and its token-format fallback. UseDataProtection
                // alone doesn't supply these. Loaded once at startup from the
                // app-keys volume via OAuthKeyMaterial — same trust boundary as
                // the Data Protection ring (anyone who can read app-keys can
                // already steal cookies + the SMTP password). Persisting them
                // means a container restart no longer invalidates every issued
                // access + refresh token, so Claude doesn't have to re-consent
                // on every redeploy.
                o.AddSigningKey(oauthSigningKey)
                    .AddEncryptionKey(oauthEncryptionKey);

                // Lifetimes — proactive refresh kicks in five minutes before
                // expiry, so 60-minute access tokens turn over comfortably.
                o.SetAccessTokenLifetime(TimeSpan.FromMinutes(60))
                    .SetRefreshTokenLifetime(TimeSpan.FromDays(30));

                // Only /oauth/authorize is passed through — the consent UI lives in
                // a Razor page and we craft the principal ourselves in
                // OAuthEndpoints.MapAuthorizeComplete. /oauth/token has no
                // customisation: OpenIddict already has the principal stored against
                // the auth code and refresh token, so it can issue the response on
                // its own. Enabling token-endpoint passthrough without registering a
                // matching route handler silently drops the response after validation
                // and Claude surfaces "Authorization with the MCP server failed".
                o.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough();

                // Dev: HTTPS isn't terminated in front of us, so OpenIddict's
                // built-in transport-security check would refuse to start. Prod
                // runs behind a TLS-terminating proxy (UseForwardedHeaders is
                // installed in the pipeline), so the check is satisfied by X-Forwarded-Proto.
                if (environment.IsDevelopment())
                {
                    o.UseAspNetCore().DisableTransportSecurityRequirement();
                }

                // Discovery customisation. Three additions MCP clients need:
                //   (1) Advertise the hand-rolled DCR endpoint (OpenIddict 7.5.0
                //       doesn't surface registration_endpoint itself).
                //   (2) Declare CIMD support — MCP clients pick the CIMD path
                //       (URL-as-client_id) when client_id_metadata_document_supported
                //       is true AND token_endpoint_auth_methods_supported lists the
                //       method they want to use.
                //   (3) Advertise both "none" (Claude's public PKCE clients) and
                //       "private_key_jwt" (ChatGPT's signed-assertion clients), plus
                //       the RS256 signing algorithm ChatGPT's CIMD documents declare.
                //       Missing either silently demotes that vendor to DCR-only or
                //       refuses outright.
                o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.HandleConfigurationRequestContext>(b =>
                    b.UseInlineHandler(context =>
                    {
                        context.Metadata["client_id_metadata_document_supported"] = true;
                        var issuer = context.Issuer ?? context.BaseUri;
                        if (issuer is not null)
                        {
                            context.Metadata["registration_endpoint"] = new Uri(issuer, "/oauth/register").AbsoluteUri;
                        }
                        context.TokenEndpointAuthenticationMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.ClientAuthenticationMethods.None);
                        context.TokenEndpointAuthenticationMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.ClientAuthenticationMethods.PrivateKeyJwt);
                        context.Metadata["token_endpoint_auth_signing_alg_values_supported"] = new OpenIddict.Abstractions.OpenIddictParameter(
                            System.Text.Json.JsonSerializer.SerializeToElement(new[] { "RS256" }));
                        context.CodeChallengeMethods.Add(OpenIddict.Abstractions.OpenIddictConstants.CodeChallengeMethods.Sha256);
                        return default;
                    }));

                // CIMD resolver — Claude's hosted surfaces identify themselves with
                // an HTTPS URL as their client_id (e.g.
                // https://claude.ai/oauth/mcp-oauth-client-metadata). Without this
                // handler OpenIddict's standard ValidateClientId rejects the request
                // with ID2052 because no oauth_applications row matches. Runs ahead
                // of every built-in validator so the row exists by the time
                // OpenIddict's own lookup fires.
                o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ValidateAuthorizationRequestContext>(b =>
                    b.UseScopedHandler<ALDevToolbox.Services.OAuth.CimdClientResolver>()
                     .SetOrder(int.MinValue + 100_000));

                // Registration (DCR and CIMD) only stores redirect URIs that
                // OAuthRedirectUriPolicy accepts, but rows written before that
                // rule existed may still hold a refused one. Checking the
                // requested redirect_uri on every authorize request keeps such
                // a row unusable; OpenIddict's own handlers then require it to
                // match a registered URI exactly. The parameter is required:
                // without it OpenIddict falls back to a client's single stored
                // URI, which would skip this check (OAuth 2.1 and MCP clients
                // always send it). Ordered ahead of the CIMD resolver so a
                // refused request fetches and stores nothing, and ahead of the
                // redirect URI validation, so the error is shown here, never
                // sent there.
                o.AddEventHandler<OpenIddict.Server.OpenIddictServerEvents.ValidateAuthorizationRequestContext>(b =>
                    b.UseInlineHandler(context =>
                    {
                        var redirectUri = context.Request.RedirectUri;
                        if (string.IsNullOrEmpty(redirectUri))
                        {
                            context.Reject(
                                error: OpenIddict.Abstractions.OpenIddictConstants.Errors.InvalidRequest,
                                description: "The redirect_uri parameter is required.");
                        }
                        else if (!OAuthRedirectUriPolicy.IsAllowed(redirectUri))
                        {
                            context.Reject(
                                error: OpenIddict.Abstractions.OpenIddictConstants.Errors.InvalidRequest,
                                description: "The redirect_uri must be https, http on loopback, or a reverse-domain app scheme.");
                        }
                        return default;
                    })
                    .SetOrder(int.MinValue + 50_000));

                // MCP clients use the code flow's default query response mode.
                // form_post would render an auto-submitting form whose action is
                // the redirect URI on this app's origin, so it is switched off.
                o.Configure(options => options.ResponseModes.Remove(
                    OpenIddict.Abstractions.OpenIddictConstants.ResponseModes.FormPost));
            })
            .AddValidation(o =>
            {
                // Local issuer — no remote /introspect round-trip per request.
                o.UseLocalServer();
                o.UseAspNetCore();
                o.UseDataProtection();
                // An access token is self-contained, so on its own it stays
                // valid for its whole lifetime after the user disconnects the
                // assistant or a SiteAdmin deletes the client. Checking its
                // stored entry on every request makes a revoked (or, with its
                // client, deleted) token fail at once. One indexed lookup per
                // bearer request. See issue #1246.
                o.EnableTokenEntryValidation();
            });
        // Stamps the ALDevToolbox claim names (user_id, org_id, role, site_admin)
        // on principals authenticated by OpenIddict's validation scheme. PAT and
        // cookie principals already carry these claims; this only fires for
        // OAuth access tokens.
        services.AddScoped<IClaimsTransformation, ALDevToolbox.Services.OAuth.OAuthClaimsTransformer>();
        services.AddScoped<ALDevToolbox.Services.OAuth.OAuthClientAdminService>();
        services.AddScoped<ALDevToolbox.Services.OAuth.OAuthConsentService>();
        // The CIMD resolver fetches a client metadata document over HTTPS. Named
        // HttpClient gives us per-call timeout/UA control without leaking the
        // configuration to every other caller.
        // SSRF guard: the resolver fetches attacker-supplied URLs, so dial only
        // publicly routable IPs (defeats DNS rebinding because the check runs on the
        // address we actually connect to) and refuse redirects (an HTTPS URL must not
        // be able to 302 us onto an internal http:// target).
        services.AddHttpClient(nameof(ALDevToolbox.Services.OAuth.CimdClientResolver))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = ALDevToolbox.Services.OAuth.SsrfGuard.ConnectAsync,
            });
        services.AddScoped<ALDevToolbox.Services.OAuth.CimdClientResolver>();
        return services;
    }
}
