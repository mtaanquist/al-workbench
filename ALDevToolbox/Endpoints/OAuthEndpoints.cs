using System.Net.Mime;
using System.Security.Claims;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Services.OAuth;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static ALDevToolbox.Endpoints.EndpointHelpers;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// HTTP-layer surface of the OAuth 2.1 authorisation server. The OpenIddict
/// middleware in <c>Program.cs</c> handles <c>/oauth/token</c>,
/// <c>/oauth/revoke</c>, <c>/oauth/introspect</c> and the standard discovery
/// metadata document at <c>/.well-known/oauth-authorization-server</c>; this
/// class owns the pieces OpenIddict 7.5.0 doesn't ship out of the box:
///
/// <list type="bullet">
///   <item><c>POST /oauth/authorize</c> — the consent-form submission handler
///         that, after re-checking the OAuth request, mints the
///         <see cref="ClaimsPrincipal"/> OpenIddict turns into the
///         authorisation-code redirect.</item>
///   <item><c>POST /oauth/register</c> — Dynamic Client Registration (RFC 7591).
///         OpenIddict 7.5.0's server builder has no first-class registration
///         endpoint, so we hand-roll one against
///         <see cref="IOpenIddictApplicationManager"/>.</item>
///   <item><c>GET /.well-known/oauth-protected-resource</c> — resource metadata
///         per RFC 9728. Claude follows the <c>WWW-Authenticate</c> pointer
///         on <c>/mcp</c>'s <c>401</c> to this document to discover the
///         authorisation server.</item>
/// </list>
///
/// The Razor consent page at <c>GET /oauth/authorize</c> lives in
/// <c>Components/Pages/AccountSecurity/OAuthConsent.razor</c> and renders the
/// form that posts here. See <c>.design/mcp-oauth.md</c>.
/// </summary>
internal static class OAuthEndpoints
{
    /// <summary>
    /// Single source of truth for the scopes we let clients ask for. Extending
    /// this list also requires updating the consent screen's scope-description
    /// table and the protected-resource metadata's <c>scopes_supported</c>.
    /// </summary>
    public static readonly string[] SupportedScopes =
    {
        "mcp",
        OpenIddictConstants.Scopes.OfflineAccess,
    };

    /// <summary>
    /// Rate-limit policy name for the anonymous DCR endpoint, configured in
    /// Program.cs (per-IP fixed window). See issue #378.
    /// </summary>
    public const string DcrRateLimitPolicy = "oauth-register";

    public static IEndpointRouteBuilder MapOAuthEndpoints(this IEndpointRouteBuilder app)
    {
        MapProtectedResourceMetadata(app);
        MapAuthorizeGet(app);
        MapAuthorizeComplete(app);
        MapDynamicClientRegistration(app);
        return app;
    }

    // ────────────────────────────────────────────────────────────────────
    // GET /oauth/authorize — forward to the Razor consent page
    // ────────────────────────────────────────────────────────────────────

    private static void MapAuthorizeGet(IEndpointRouteBuilder app)
    {
        // OpenIddict's middleware validates the OAuth request on /oauth/authorize
        // and passes through to this endpoint (EnableAuthorizationEndpointPassthrough).
        // We immediately redirect the browser to /oauth/consent, carrying every
        // OAuth query parameter verbatim — the consent Razor page re-emits them
        // as hidden inputs on a form that POSTs back to /oauth/authorize, which
        // is where OpenIddict's middleware reconstructs the request and emits
        // the auth-code redirect via our SignIn.
        //
        // The redirect (rather than rendering the consent UI inline on
        // /oauth/authorize) is what keeps GET /oauth/authorize and POST
        // /oauth/authorize from sharing a route with the Razor @page — the
        // EndpointAmbiguityTests safety net trips otherwise.
        app.MapGet("/oauth/authorize", (HttpContext ctx) =>
        {
            var request = ctx.GetOpenIddictServerRequest();
            if (request is null)
            {
                // Reached without an OAuth request in scope (direct hit). Send
                // the user to the docs hub rather than the consent page —
                // the consent page can't read its own params.
                return Results.Redirect("/docs/mcp");
            }
            return Results.Redirect("/oauth/consent" + ctx.Request.QueryString.Value);
        });
    }

    // ────────────────────────────────────────────────────────────────────
    // RFC 9728 — Protected Resource Metadata
    // ────────────────────────────────────────────────────────────────────

    private static void MapProtectedResourceMetadata(IEndpointRouteBuilder app)
    {
        // Anonymous, cacheable, side-effect free. Returns the document Claude
        // fetches after following the WWW-Authenticate pointer on /mcp's 401.
        app.MapGet("/.well-known/oauth-protected-resource", (HttpContext ctx, PublicOrigin publicOrigin) =>
        {
            // Computed per request and never persisted, so PUBLIC_BASE_URL can
            // simply take over when set (issue #670). OpenIddict's own
            // discovery document still derives its issuer per request; the two
            // agree because a client that fetched this document reaches the
            // authorization server through the origin it names.
            var issuer = publicOrigin.For(ctx);
            var doc = new
            {
                resource = $"{issuer}/mcp",
                authorization_servers = new[] { issuer },
                scopes_supported = SupportedScopes,
                bearer_methods_supported = new[] { "header" },
                resource_documentation = $"{issuer}/docs/mcp",
            };
            return Results.Json(doc, contentType: MediaTypeNames.Application.Json);
        }).AllowAnonymous();
    }

    // ────────────────────────────────────────────────────────────────────
    // POST /oauth/authorize — consent form handler
    // ────────────────────────────────────────────────────────────────────

    private static void MapAuthorizeComplete(IEndpointRouteBuilder app)
    {
        // Posted by Components/Pages/AccountSecurity/OAuthConsent.razor. The
        // form re-includes every OAuth query parameter as a hidden input so
        // OpenIddict's middleware can reconstruct the original
        // authorisation request via GetOpenIddictServerRequest() before
        // intercepting our SignIn call.
        app.MapPost("/oauth/authorize", async (
            HttpContext ctx,
            IAntiforgery antiforgery,
            AppDbContext db,
            TimeProvider clock,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            // The handler binds HttpContext and reads the form by hand, so the
            // endpoint carries no IAntiforgeryMetadata and UseAntiforgery()
            // does not cover it — validate explicitly, first, like every other
            // cookie-authenticated POST under Endpoints/. Without this a
            // cross-site auto-submitted form could mint an authorisation code
            // for an attacker-registered client (issue #671).
            if (!await ValidateAntiforgeryAsync(ctx, antiforgery, cancellationToken)) return;

            var logger = loggerFactory.CreateLogger("OAuth.Authorize");
            var request = ctx.GetOpenIddictServerRequest()
                ?? throw new InvalidOperationException(
                    "OAuth authorize POST hit without an OpenIddict request — has EnableAuthorizationEndpointPassthrough() been turned off?");

            // The user must already be signed in via the cookie scheme; the
            // GET handler (Razor page) gates the consent screen with
            // [Authorize], so reaching POST without an authenticated principal
            // means someone is replaying the form. Reject.
            var cookieResult = await ctx.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            if (!cookieResult.Succeeded || cookieResult.Principal?.Identity?.IsAuthenticated != true)
            {
                await ctx.ChallengeAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var form = await ctx.Request.ReadFormAsync(cancellationToken);
            var decision = form["decision"].ToString();

            // Step-up: authorising an assistant hands it this user's writing
            // tools for as long as its refresh token lives, so when the org
            // asks for a second factor on any tool, the consent itself needs a
            // recent one. The consent page checks the same thing before
            // rendering; this is the backstop for a form that sat open past
            // the window. Denying needs no confirmation. The form carries the
            // OAuth request as hidden fields, so the page URL to come back to
            // is rebuilt from those, not from this POST's (empty) query.
            var tools = ctx.RequestServices.GetRequiredService<ToolEnablement>();
            if (decision == "allow"
                && await tools.AnyStepUpAsync(cancellationToken)
                && !tools.IsFresh(cookieResult.Principal))
            {
                var consentQuery = QueryString.Create(form
                    .Where(f => f.Key != "decision" && !f.Key.StartsWith("__", StringComparison.Ordinal))
                    .Select(f => new KeyValuePair<string, string?>(f.Key, f.Value.ToString())));
                ctx.Response.Redirect(StepUpEndpoints.Url("/oauth/consent" + consentQuery));
                return;
            }

            if (decision != "allow")
            {
                // Deny — round-trip an OAuth error back to Claude per RFC 6749.
                var properties = new AuthenticationProperties(new Dictionary<string, string?>
                {
                    [OpenIddictServerAspNetCoreConstants.Properties.Error] = OpenIddictConstants.Errors.AccessDenied,
                    [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = "The user declined the authorisation request.",
                });
                await ctx.ForbidAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, properties);
                return;
            }

            var userIdValue = cookieResult.Principal.FindFirstValue(HttpOrganizationContext.UserIdClaim);
            var orgIdValue = cookieResult.Principal.FindFirstValue(HttpOrganizationContext.OrganizationIdClaim);
            if (!int.TryParse(userIdValue, out var userId) || !int.TryParse(orgIdValue, out var orgId))
            {
                logger.LogWarning("OAuth consent POST without user_id/org_id claims; refusing.");
                await ctx.ForbidAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
                return;
            }

            var clientId = request.ClientId ?? string.Empty;
            var requestedScopes = (request.Scope ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsScopeSupported)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();
            var canonicalScopes = string.Join(' ', requestedScopes);

            var now = clock.GetUtcNow().UtcDateTime;
            // The consent remembers whether the person confirming it had a
            // recent second factor; the MCP filter requires that before a
            // writing tool the org marked for step-up runs under this consent.
            var strongAuthAt = StepUpAuth.IsFresh(cookieResult.Principal, now)
                ? StepUpAuth.StrongAuthAt(cookieResult.Principal) : null;
            await UpsertConsentAsync(db, userId, orgId, clientId, canonicalScopes, now, strongAuthAt, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            // Stamp the org id and granted scopes onto the principal. The
            // OAuth claims transformer turns sub/org into the ALDevToolbox
            // claim names downstream; here we just emit what OpenIddict
            // needs to sign the access + refresh tokens.
            var identity = new ClaimsIdentity(
                authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
                nameType: OpenIddictConstants.Claims.Name,
                roleType: OpenIddictConstants.Claims.Role);
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, userId.ToString())
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken));
            identity.AddClaim(new Claim(OAuthClaimsTransformer.OrgClaim, orgId.ToString())
                .SetDestinations(OpenIddictConstants.Destinations.AccessToken));
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Email,
                    cookieResult.Principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty)
                .SetDestinations(OpenIddictConstants.Destinations.IdentityToken));
            identity.AddClaim(new Claim(OpenIddictConstants.Claims.Name,
                    cookieResult.Principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty)
                .SetDestinations(OpenIddictConstants.Destinations.IdentityToken));

            var principal = new ClaimsPrincipal(identity);
            principal.SetScopes(requestedScopes);

            logger.LogInformation(
                "OAuth authorise: user {UserId} granted scopes [{Scopes}] to client {ClientId} in org {OrgId}.",
                userId, canonicalScopes, clientId, orgId);

            await ctx.SignInAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, principal);
        });
    }

    private static bool IsScopeSupported(string scope)
        => Array.Exists(SupportedScopes, s => s.Equals(scope, StringComparison.Ordinal));

    private static async Task UpsertConsentAsync(
        AppDbContext db,
        int userId,
        int orgId,
        string clientId,
        string canonicalScopes,
        DateTime now,
        DateTime? strongAuthAt,
        CancellationToken cancellationToken)
    {
        // Use IgnoreQueryFilters so the lookup hits the unique index across
        // the unfiltered table — preserves the "one row per (user, client, org)"
        // shape even when an admin revoked the consent earlier.
        var existing = await db.OAuthConsents
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c =>
                c.UserId == userId
                && c.OrganizationId == orgId
                && c.ClientId == clientId,
                cancellationToken);
        if (existing is null)
        {
            db.OAuthConsents.Add(new OAuthConsent
            {
                UserId = userId,
                OrganizationId = orgId,
                ClientId = clientId,
                ScopesGranted = canonicalScopes,
                GrantedAt = now,
                StrongAuthAt = strongAuthAt,
            });
        }
        else
        {
            existing.ScopesGranted = canonicalScopes;
            existing.GrantedAt = now;
            existing.RevokedAt = null;
            existing.StrongAuthAt = strongAuthAt;
        }
    }

    // ────────────────────────────────────────────────────────────────────
    // POST /oauth/register — Dynamic Client Registration (RFC 7591)
    // ────────────────────────────────────────────────────────────────────

    private static void MapDynamicClientRegistration(IEndpointRouteBuilder app)
    {
        // RFC 7591 says the registration endpoint MAY require authentication.
        // Claude's DCR flow registers anonymously, so we accept anonymous
        // POSTs and don't stamp (user_id, org_id) on the application row
        // until the user later consents — at which point the consent handler
        // updates the application's Properties JSON.
        app.MapPost("/oauth/register", async (
            HttpContext ctx,
            IOpenIddictApplicationManager applications,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            var logger = loggerFactory.CreateLogger("OAuth.Register");

            DcrRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<DcrRequest>(
                    ctx.Request.Body,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web),
                    cancellationToken);
            }
            catch (JsonException ex)
            {
                return DcrError("invalid_client_metadata", $"Request body is not valid JSON: {ex.Message}");
            }

            if (body is null)
            {
                return DcrError("invalid_client_metadata", "Request body is required.");
            }

            var redirectUris = body.RedirectUris ?? Array.Empty<string>();
            if (redirectUris.Length == 0)
            {
                return DcrError("invalid_redirect_uri", "At least one redirect_uri is required.");
            }
            foreach (var uri in redirectUris)
            {
                if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
                {
                    return DcrError("invalid_redirect_uri", $"redirect_uri must be an absolute URI: {uri}");
                }
                // Loopback is allowed for native clients (Claude Code); HTTPS
                // for hosted clients. Reject plain http:// on non-loopback.
                if (parsed.Scheme == Uri.UriSchemeHttp
                    && !IsLoopback(parsed))
                {
                    return DcrError("invalid_redirect_uri", $"redirect_uri must be https except for loopback: {uri}");
                }
            }

            var displayName = string.IsNullOrWhiteSpace(body.ClientName) ? "Dynamic OAuth client" : body.ClientName.Trim();

            // Public client by default — DCR-registered clients are PKCE-only
            // public clients per the MCP spec. No client_secret is issued.
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = Guid.NewGuid().ToString("N"),
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = displayName,
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.Endpoints.Revocation,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.GrantTypes.RefreshToken,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    "scp:mcp",
                    "scp:" + OpenIddictConstants.Scopes.OfflineAccess,
                },
                Requirements =
                {
                    OpenIddictConstants.Requirements.Features.ProofKeyForCodeExchange,
                },
            };
            foreach (var uri in redirectUris)
            {
                descriptor.RedirectUris.Add(new Uri(uri));
            }
            descriptor.Properties["registered_at"] = JsonSerializer.SerializeToElement(DateTime.UtcNow);
            descriptor.Properties["registration_source"] = JsonSerializer.SerializeToElement("dcr");

            await applications.CreateAsync(descriptor, cancellationToken);

            logger.LogInformation(
                "DCR registered client {ClientId} ({DisplayName}) with {RedirectCount} redirect URI(s).",
                descriptor.ClientId, displayName, redirectUris.Length);

            var response = new
            {
                client_id = descriptor.ClientId,
                client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                client_name = displayName,
                redirect_uris = redirectUris,
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                token_endpoint_auth_method = "none",
                scope = string.Join(' ', SupportedScopes),
            };
            return Results.Json(response, statusCode: StatusCodes.Status201Created);
        }).AllowAnonymous().RequireRateLimiting(DcrRateLimitPolicy);
    }

    private static IResult DcrError(string code, string description) =>
        Results.Json(new { error = code, error_description = description }, statusCode: StatusCodes.Status400BadRequest);

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
        || string.Equals(uri.Host, "::1", StringComparison.Ordinal);

    /// <summary>
    /// RFC 7591 §2 client metadata. We only read the fields ALDevToolbox
    /// actually honours; unknown fields are dropped silently per the spec.
    /// </summary>
    private sealed record DcrRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("client_name")]
        public string? ClientName { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("redirect_uris")]
        public string[]? RedirectUris { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token_endpoint_auth_method")]
        public string? TokenEndpointAuthMethod { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("grant_types")]
        public string[]? GrantTypes { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string? Scope { get; init; }
    }
}

/// <summary>
/// <see cref="Claim"/> extension helpers for OpenIddict's destination model —
/// each claim needs an explicit declaration of which token(s) it appears in.
/// </summary>
internal static class OpenIddictClaimExtensions
{
    public static Claim SetDestinations(this Claim claim, params string[] destinations)
    {
        claim.SetDestinations((IEnumerable<string>)destinations);
        return claim;
    }
}
