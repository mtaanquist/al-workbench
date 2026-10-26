using System.Reflection;
using ALDevToolbox.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace ALDevToolbox.Tests.Routing;

/// <summary>
/// Drift guard over every routable Blazor page. Access to the admin surfaces is
/// carried entirely by a per-page <c>[Authorize(Roles = ...)]</c> attribute and
/// there is no fallback policy, so a page that forgets the attribute is
/// <em>anonymous</em>, not 403. Nothing else in the suite notices: the
/// authentication tests cover a handful of routes by name.
///
/// So reflect over the app assembly the way <c>McpToolCatalogTests</c> does and
/// assert the three rules that hold today:
/// (a) every <c>/site-admin</c> route requires the SiteAdmin role,
/// (b) every <c>/admin</c> route requires <c>Admin</c> or <c>Admin,Editor</c>,
/// (c) every other route carries <c>[Authorize]</c> or is on the explicit
/// <see cref="IntentionallyAnonymous"/> list below.
///
/// Attribute <em>types</em> are read, never source text — some pages use the
/// fully-qualified <c>[Microsoft.AspNetCore.Authorization.Authorize]</c> form
/// that a grep misses. Adding a page now forces a deliberate choice: give it a
/// role, or add it to the list with a reason.
/// </summary>
public sealed class RouteAuthorizationTests
{
    /// <summary>
    /// Routes that are anonymous on purpose, each with the reason. Anything not
    /// listed here has to be <c>[Authorize]</c>d — adding an entry is the
    /// deliberate choice the test exists to force.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> IntentionallyAnonymous =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // The tool launcher renders for a signed-out visitor on purpose: each card
            // points at /login?returnUrl=<tool> with a lock badge instead of the tool.
            ["/"] = "tools home; cards link signed-out visitors to sign-in rather than the tool",
            // Pre-login surfaces: they are how a user gets a cookie in the first place.
            ["/login"] = "sign-in page",
            ["/login/challenge"] = "second-factor / verification step of sign-in, still pre-cookie",
            ["/login/magic"] = "passwordless sign-in request page",
            ["/signup"] = "self-service signup request",
            ["/signup/details"] = "second step of a signup request, still pre-cookie",
            ["/forgot-password"] = "password-reset request form",
            ["/reset-password"] = "password-reset completion form (token in the URL)",
            ["/account/email-change/cancel"] = "stops an admin-made email change from the notice sent to the current address (token in the URL)",
            ["/accept-invite"] = "invited user sets a password before they have an account",
            ["/s/{Token}"] = "a Sealed link's recipient has no account; shows only the sending organisation's name and spends nothing (.design/sealed.md)",
            // Public, data-free pages.
            ["/docs/mcp"] = "public connect-an-assistant docs; carries an explicit [AllowAnonymous]",
            ["/not-found"] = "404 landing page",
            ["/Error"] = "unhandled-error page",
            // Standalone client-side utilities: they inject no services and read no data.
            ["/diff"] = "in-browser text diff; no services injected, no data touched",
            ["/compare"] = "in-browser comparison utility; no services injected, no data touched",
            ["/piper"] = "in-browser Piper helper; no services injected, no data touched",
        };

    private static IReadOnlyList<(Type Page, string Route)> RoutableComponents() =>
        typeof(HttpOrganizationContext).Assembly
            .GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => (Page: t, r.Template)))
            .OrderBy(x => x.Template, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The most specific <c>[Authorize]</c> on the page (including any inherited
    /// from a base component), or null when the page carries none.
    /// </summary>
    private static AuthorizeAttribute? Authorize(Type page) =>
        page.GetCustomAttributes<AuthorizeAttribute>(inherit: true).FirstOrDefault();

    [Fact]
    public void The_assembly_actually_exposes_routable_pages()
    {
        // Guards the reflection itself: a silently empty set would make every
        // other assertion below pass vacuously.
        RoutableComponents().Should().HaveCountGreaterThan(50);
    }

    [Fact]
    public void Every_site_admin_route_requires_the_site_admin_role()
    {
        var offenders = RoutableComponents()
            .Where(x => x.Route.StartsWith("/site-admin", StringComparison.OrdinalIgnoreCase))
            .Where(x => Authorize(x.Page)?.Roles is not HttpOrganizationContext.SiteAdminRole)
            .Select(x => $"{x.Route} ({x.Page.Name}) -> {Describe(Authorize(x.Page))}")
            .ToList();

        offenders.Should().BeEmpty(
            "the SiteAdmin console is cross-organisation; every page under it must require the '{0}' role",
            HttpOrganizationContext.SiteAdminRole);
    }

    [Fact]
    public void Every_admin_route_requires_admin_or_admin_editor()
    {
        var allowed = new[] { "Admin", "Admin,Editor" };

        var offenders = RoutableComponents()
            .Where(x => x.Route.StartsWith("/admin", StringComparison.OrdinalIgnoreCase))
            .Where(x => !allowed.Contains(Authorize(x.Page)?.Roles, StringComparer.Ordinal))
            .Select(x => $"{x.Route} ({x.Page.Name}) -> {Describe(Authorize(x.Page))}")
            .ToList();

        offenders.Should().BeEmpty(
            "an /admin page is either Admin-only or open to the content-authoring Editor role; "
            + "no attribute at all means anonymous, because there is no fallback policy");
    }

    [Fact]
    public void Every_other_route_is_authorized_or_deliberately_anonymous()
    {
        var offenders = RoutableComponents()
            .Where(x => !x.Route.StartsWith("/admin", StringComparison.OrdinalIgnoreCase)
                     && !x.Route.StartsWith("/site-admin", StringComparison.OrdinalIgnoreCase))
            .Where(x => Authorize(x.Page) is null && !IntentionallyAnonymous.ContainsKey(x.Route))
            .Select(x => $"{x.Route} ({x.Page.Name})")
            .ToList();

        offenders.Should().BeEmpty(
            "a page without [Authorize] is reachable anonymously; either authorize it or add it to "
            + "IntentionallyAnonymous with the reason");
    }

    [Fact]
    public void The_anonymous_allow_list_has_no_stale_entries()
    {
        var routes = RoutableComponents().Select(x => x.Route).ToHashSet(StringComparer.OrdinalIgnoreCase);

        IntentionallyAnonymous.Keys.Where(r => !routes.Contains(r)).Should().BeEmpty(
            "the allow-list should shrink when a page is deleted or its route renamed");

        IntentionallyAnonymous.Keys
            .Where(r => routes.Contains(r))
            .Where(r => RoutableComponents().Any(x =>
                string.Equals(x.Route, r, StringComparison.OrdinalIgnoreCase) && Authorize(x.Page) is not null))
            .Should().BeEmpty("a page that has since been authorized should come off the allow-list");
    }

    private static string Describe(AuthorizeAttribute? attr) =>
        attr is null ? "no [Authorize] (anonymous)" : $"[Authorize(Roles = \"{attr.Roles}\")]";
}
