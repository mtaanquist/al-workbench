using ALDevToolbox.Services;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Every route under <c>/site-admin</c> carries the SiteAdmin role, read off
/// the real endpoint table rather than by calling each route. The services
/// behind those routes cross the tenant fence, and a handler mapped without
/// the role would otherwise ship green (#1272).
/// </summary>
public sealed class SiteAdminRoleMetadataTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public SiteAdminRoleMetadataTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Every_site_admin_route_requires_the_site_admin_role()
    {
        var routes = _factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => ("/" + (e.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
                .StartsWith("/site-admin", StringComparison.OrdinalIgnoreCase))
            .ToList();

        routes.Should().HaveCountGreaterThan(30, "the console's handlers and pages are all in the table");
        var unguarded = routes
            .Where(e => !RequiresSiteAdmin(e))
            .Select(e => $"{e.DisplayName} ({e.RoutePattern.RawText})")
            .ToList();
        unguarded.Should().BeEmpty("every /site-admin route must carry the {0} role", HttpOrganizationContext.SiteAdminRole);
    }

    private static bool RequiresSiteAdmin(Microsoft.AspNetCore.Http.Endpoint endpoint)
    {
        var viaAttribute = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(a => (a.Roles ?? string.Empty).Split(',', StringSplitOptions.TrimEntries)
                .Contains(HttpOrganizationContext.SiteAdminRole));
        var viaPolicy = endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>()
            .SelectMany(p => p.Requirements.OfType<RolesAuthorizationRequirement>())
            .Any(r => r.AllowedRoles.Contains(HttpOrganizationContext.SiteAdminRole));
        return viaAttribute || viaPolicy;
    }
}
