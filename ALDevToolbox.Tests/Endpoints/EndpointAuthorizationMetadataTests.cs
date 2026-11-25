using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Drift guard over every minimal-API endpoint, read off the real endpoint
/// table. There is no fallback authorization policy, so a <c>Map*</c> call that
/// forgets <c>RequireAuthorization()</c> is anonymous, and an <c>/admin</c>
/// handler that loses its role is open to every signed-in user. Neither shows
/// up in a test that names routes by hand (#1425, GEM-03).
///
/// Razor pages are out of scope here: <c>RouteAuthorizationTests</c> covers
/// them by their <c>[Authorize]</c> attribute, and <c>/site-admin</c> has its
/// own role guard in <see cref="SiteAdminRoleMetadataTests"/>.
/// </summary>
public sealed class EndpointAuthorizationMetadataTests : IDisposable
{
    private const string LegacyRedirect = "legacy redirect to the renamed route, which carries its own guard; reads nothing";
    private const string PreLogin = "pre-login: this is how a visitor gets a cookie in the first place";

    /// <summary>
    /// Endpoints that are anonymous on purpose, keyed "METHOD route" as the
    /// endpoint table spells them ("*" when the endpoint takes every verb).
    /// Adding an entry is the deliberate choice this test exists to force.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> IntentionallyAnonymous =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Operator probes and framework plumbing.
            ["* /healthz"] = "operator liveness probe; reports no data",
            ["* /healthz/workers"] = "operator worker probe; reports no data",
            ["* /readyz"] = "reverse-proxy readiness probe; reports no data",
            ["GET,HEAD {**path:file}"] = "framework static-file fallback",
            ["GET /_framework/opaque-redirect"] = "Blazor's own redirect plumbing for enhanced navigation",

            // Sign-in, signup, recovery and invites.
            ["POST /auth/login"] = PreLogin,
            ["POST /auth/login/challenge/email/issue"] = PreLogin + " (second factor, still pre-cookie)",
            ["POST /auth/login/challenge/email/verify"] = PreLogin + " (second factor, still pre-cookie)",
            ["POST /auth/login/challenge/recovery"] = PreLogin + " (second factor, still pre-cookie)",
            ["POST /auth/login/challenge/totp"] = PreLogin + " (second factor, still pre-cookie)",
            ["POST /auth/login/magic"] = PreLogin + " (requests a sign-in link)",
            ["GET /auth/login/magic/consume"] = PreLogin + " (links sent before #1500; only forwards to the confirmation page)",
            ["POST /auth/login/magic/consume"] = PreLogin + " (the confirmation page's Sign in button; token in the form)",
            ["POST /auth/passkey/login"] = PreLogin + " (passkey assertion)",
            ["POST /auth/passkey/login/options"] = PreLogin + " (passkey challenge)",
            ["POST /auth/entra/challenge"] = PreLogin + " (starts Microsoft sign-in)",
            ["POST /auth/forgot-password"] = "password-reset request; the visitor cannot sign in",
            ["POST /auth/reset-password"] = "password-reset completion; token in the form",
            ["POST /auth/signup"] = "self-service signup request",
            ["POST /auth/signup/start"] = "email-first signup, first step",
            ["GET /auth/signup/verify"] = "email-first signup, link from the verification email",
            ["POST /auth/signup/verify-code"] = "email-first signup, code from the verification email",
            ["POST /auth/signup/complete"] = "email-first signup, last step",
            ["POST /auth/accept-invite"] = "invited user sets a password before they have an account; token in the form",
            ["POST /auth/accept-invite/microsoft"] = "invited user accepts with Microsoft before they have an account; token in the form",
            ["GET /auth/account/email-change/confirm"] = "forwards an older confirmation link to the confirm page; spends nothing",
            ["POST /auth/account/email-change/confirm"] = "confirms an email change from the page the new address's link opens; token in the form",
            ["POST /auth/account/email-change/cancel"] = "stops an admin-made email change from the notice sent to the old address; token in the form",
            ["POST /auth/logout"] = "signing out without a cookie does nothing",

            // Machine callers that bring their own credential.
            ["GET /.well-known/oauth-protected-resource"] = "MCP resource metadata a client reads before it has a token",
            ["POST /oauth/register"] = "OAuth dynamic client registration happens before any token exists; rate-limited",
            ["GET /oauth/authorize"] = "OAuth consent; the handler challenges the cookie itself when nobody is signed in",
            ["POST /oauth/authorize"] = "OAuth consent; the handler challenges the cookie itself when nobody is signed in",
            ["POST /github/webhook"] = "GitHub calls it; authenticated by the HMAC signature, not a user",
            ["POST /api/sealed/{token}/reveal"] = "a Sealed link's recipient has no account; pinned to the link's token (.design/sealed.md)",
            ["POST /api/compare/diff"] = "diff for the in-browser compare utility; stateless and reads no data; rate-limited",

            // Renamed routes kept as redirects (LegacyRedirectEndpoints).
            ["GET /projects"] = LegacyRedirect,
            ["GET /projects/new"] = LegacyRedirect,
            ["GET /projects/{id:int}"] = LegacyRedirect,
            ["GET /projects/extension"] = LegacyRedirect,
            ["GET /releases"] = LegacyRedirect,
            ["GET /releases/{id:int}"] = LegacyRedirect,
            ["GET /snippets"] = LegacyRedirect,
            ["GET /snippets/suggest"] = LegacyRedirect,
            ["GET /snippets/{id:int}"] = LegacyRedirect,
            ["GET /admin/snippets"] = LegacyRedirect,
            ["GET /admin/snippets/new"] = LegacyRedirect,
            ["GET /admin/snippets/{id:int}"] = LegacyRedirect,
            ["GET /admin/snippets/suggestions"] = LegacyRedirect,
            ["GET /api/snippets/{id:int}/download"] = LegacyRedirect,
        };

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public EndpointAuthorizationMetadataTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Every_endpoint_requires_sign_in_or_is_listed_as_anonymous()
    {
        var endpoints = ApiEndpoints();
        endpoints.Should().HaveCountGreaterThan(100, "the walk must actually reach the mapped handlers");

        var unlisted = endpoints
            .Where(e => !RequiresAuthorization(e) && !IntentionallyAnonymous.ContainsKey(Key(e)))
            .Select(Key)
            .ToList();

        string.Join(Environment.NewLine, unlisted).Should().BeEmpty(
            "there is no fallback policy, so an endpoint without RequireAuthorization() is open to anyone; "
            + "add the guard, or add the route to IntentionallyAnonymous with the reason");
    }

    [Fact]
    public void Every_anonymous_list_entry_is_a_live_anonymous_endpoint()
    {
        // Keeps the list honest: an entry for a route that is gone, or that has
        // since gained a guard, would silently pre-approve whatever lands there next.
        var anonymous = ApiEndpoints()
            .Where(e => !RequiresAuthorization(e))
            .Select(Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        IntentionallyAnonymous.Keys.Where(k => !anonymous.Contains(k)).Should().BeEmpty(
            "each entry must name an endpoint that exists and is anonymous today");
    }

    [Fact]
    public void Every_admin_endpoint_requires_admin_or_admin_and_editor()
    {
        var admin = ApiEndpoints()
            .Where(e => Route(e).StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
            .Where(e => !IntentionallyAnonymous.ContainsKey(Key(e)))
            .ToList();
        admin.Should().HaveCountGreaterThan(20, "the walk must actually reach the /admin handlers");

        var wrong = admin
            .Select(e => (Key: Key(e), Roles: AllowedRoles(e)))
            .Where(x => !x.Roles.Contains(HttpOrganizationContext.AdminRole)
                || !x.Roles.IsSubsetOf([HttpOrganizationContext.AdminRole, HttpOrganizationContext.EditorRole]))
            .Select(x => $"{x.Key} allows [{string.Join(",", x.Roles)}]")
            .ToList();

        string.Join(Environment.NewLine, wrong).Should().BeEmpty("an /admin endpoint is for Admin, or Admin and Editor, never every signed-in user");
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// Every routed endpoint except Razor pages (guarded by
    /// <c>RouteAuthorizationTests</c>), the Blazor circuit hub that serves them,
    /// and the static assets.
    /// </summary>
    private List<RouteEndpoint> ApiEndpoints()
    {
        using var _ = _factory.CreateClient();
        return _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ComponentTypeMetadata>() is null)
            .Where(e => !Route(e).StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase))
            .Where(e => !IsStaticAsset(e))
            .ToList();
    }

    private static string Route(RouteEndpoint e) => e.RoutePattern.RawText ?? string.Empty;

    private static string Key(RouteEndpoint e)
    {
        var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var verb = methods is null || methods.Count == 0 ? "*" : string.Join(",", methods);
        return $"{verb} {Route(e)}";
    }

    private static bool RequiresAuthorization(RouteEndpoint e) =>
        e.Metadata.GetMetadata<IAllowAnonymous>() is null
        && (e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any()
            || e.Metadata.GetOrderedMetadata<AuthorizationPolicy>().Any());

    /// <summary>
    /// The roles an endpoint names, read deliberately conservatively: the union over
    /// every role requirement, though ASP.NET Core makes each requirement pass in turn,
    /// so the roles that really get in are the intersection. The union is never
    /// narrower than the truth, so a check that refuses a role outside the allowed set
    /// can raise a false alarm on an endpoint that stacks requirements, but cannot pass
    /// one that lets in more than it reports.
    /// </summary>
    private static HashSet<string> AllowedRoles(RouteEndpoint endpoint)
    {
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var policy in endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
        {
            foreach (var requirement in policy.Requirements.OfType<RolesAuthorizationRequirement>())
            {
                roles.UnionWith(requirement.AllowedRoles);
            }
        }
        foreach (var data in endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
        {
            if (string.IsNullOrWhiteSpace(data.Roles)) continue;
            roles.UnionWith(data.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return roles;
    }

    // Same identification as EndpointAmbiguityTests: by the static-assets
    // metadata type, not by the shape of the path.
    private static bool IsStaticAsset(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(m => m?.GetType().FullName?.StartsWith(
            "Microsoft.AspNetCore.StaticAssets", StringComparison.Ordinal) == true);
}
