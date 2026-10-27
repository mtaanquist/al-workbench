using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Routing;

/// <summary>
/// Boots the full app via <see cref="WebApplicationFactory{TEntryPoint}"/> and
/// walks <see cref="EndpointDataSource"/> to assert no two endpoints share the
/// same (HTTP method, route pattern). Catches the kind of bug where an explicit
/// <c>MapPost</c> overlaps a Razor Components <c>@page</c> route — the symptom
/// in production is <c>AmbiguousMatchException</c> at request time, which is
/// the exact failure mode this test surfaces at build time instead.
/// </summary>
// Boots the app and sets ConnectionStrings__DefaultConnection, a process-wide
// variable; serialise with every other app-booting class or two hosts race it.
[Collection(EndpointFactoryCollection.Name)]
public class EndpointAmbiguityTests : IClassFixture<TestDb>
{
    private readonly TestDb _db;

    public EndpointAmbiguityTests(TestDb db) => _db = db;

    [Fact]
    public void No_two_endpoints_match_the_same_method_and_route()
    {
        // ConnectionStrings:DefaultConnection is read inside
        // WebApplication.CreateBuilder(args), which runs *before* any
        // WithWebHostBuilder hook gets a chance to inject configuration —
        // minimal hosting's DeferredHostBuilder doesn't propagate
        // ConfigureAppConfiguration back into the WebApplicationBuilder.
        // Setting the env var up front is the supported workaround; it
        // also flows into the EnvironmentVariablesConfigurationProvider that
        // Program.cs already relies on in production.
        var previousConnection = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection");
        var previousScheduler = Environment.GetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER");
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", _db.ConnectionString);
        // BackupScheduler chases pg_dump on a 1-minute timer; suppress it so a
        // throwaway test DB doesn't trigger a background restore loop.
        Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", "1");
        try
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    // The project's content root carries wwwroot, which
                    // MapStaticAssets reads from. The SDK normally injects this
                    // via WebApplicationFactoryContentRoot, but pinning it
                    // explicitly keeps the test resilient to the test bin
                    // folder drifting from the project layout.
                    builder.UseContentRoot(LocateProjectFolder());
                    builder.UseEnvironment("Test");
                });

            // CreateClient triggers the host build, which is when endpoints get
            // registered. factory.Services isn't usable until that happens.
            using var _ = factory.CreateClient();

            var endpoints = factory.Services
                .GetRequiredService<EndpointDataSource>()
                .Endpoints
                .OfType<RouteEndpoint>()
                .Where(e => !IsStaticAssetEndpoint(e))
                .ToList();

            var conflicts = new List<string>();
            foreach (var group in endpoints.GroupBy(e => MatchKey(e.RoutePattern)))
            {
                if (group.Count() < 2) continue;
                var entries = group.ToList();
                for (var i = 0; i < entries.Count; i++)
                {
                    for (var j = i + 1; j < entries.Count; j++)
                    {
                        var shared = MethodsOf(entries[i])
                            .Intersect(MethodsOf(entries[j]), StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        foreach (var method in shared)
                        {
                            conflicts.Add(
                                $"{method} {group.Key}: '{entries[i].DisplayName}' vs '{entries[j].DisplayName}'");
                        }
                    }
                }
            }

            conflicts.Should().BeEmpty(
                "every (method, route) tuple must map to a single endpoint — overlapping endpoints raise AmbiguousMatchException when a matching request arrives");
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", previousConnection);
            Environment.SetEnvironmentVariable("DISABLE_BACKUP_SCHEDULER", previousScheduler);
        }
    }

    // Routing matches literals case-insensitively and does not care what a
    // parameter is called, so "{releaseId:int}/modules" on a MapPost and
    // "{ReleaseId:int}/modules" on an @page are the same address. Grouping by
    // RawText kept them apart and let exactly that collision through (#1450).
    // The key keeps each parameter's constraints and optional/catch-all shape,
    // which do change what a segment matches.
    internal static string MatchKey(RoutePattern pattern) =>
        "/" + string.Join("/", pattern.PathSegments.Select(segment =>
            string.Concat(segment.Parts.Select(part => part switch
            {
                RoutePatternLiteralPart literal => literal.Content.ToLowerInvariant(),
                RoutePatternSeparatorPart separator => separator.Content,
                RoutePatternParameterPart parameter =>
                    "{" + (parameter.IsCatchAll ? "*" : "")
                    + string.Join(":", parameter.ParameterPolicies.Select(p => p.Content?.ToLowerInvariant()))
                    + (parameter.IsOptional ? "?" : "") + "}",
                _ => part.ToString(),
            }))));

    // Endpoints without HttpMethodMetadata accept every verb — expand them to
    // the full set so the intersection check below catches conflicts between a
    // verb-restricted endpoint and a verb-agnostic one (the original
    // /site-admin/settings collision was exactly this shape).
    private static readonly string[] CanonicalMethods =
        ["GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS"];

    private static IEnumerable<string> MethodsOf(RouteEndpoint endpoint)
    {
        var meta = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        return meta is null || meta.HttpMethods.Count == 0
            ? CanonicalMethods
            : meta.HttpMethods;
    }

    // MapStaticAssets registers multiple endpoints per asset (fingerprinted +
    // unfingerprinted, compressed + uncompressed) that share a route pattern.
    // Its matcher disambiguates them via content negotiation, not by URL, so
    // they never raise AmbiguousMatchException — they'd only produce false
    // positives here. Identify them by their static-assets metadata rather
    // than by pattern shape so future framework changes don't slip past.
    private static bool IsStaticAssetEndpoint(RouteEndpoint endpoint)
    {
        foreach (var metadata in endpoint.Metadata)
        {
            if (metadata?.GetType().FullName is { } name
                && name.StartsWith("Microsoft.AspNetCore.StaticAssets", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [Theory]
    [InlineData("/admin/release/{releaseId:int}/modules", "/Admin/Release/{ReleaseId:int}/Modules", true)]
    [InlineData("/a/{id}", "/a/{slug}", true)]
    [InlineData("/a/{id:int}", "/a/{id}", false)]
    [InlineData("/a/{id?}", "/a/{id}", false)]
    [InlineData("/a/{*rest}", "/a/{rest}", false)]
    [InlineData("/a/b", "/a/c", false)]
    public void Routes_that_differ_only_in_case_or_parameter_name_share_a_key(string left, string right, bool same)
    {
        var equal = MatchKey(RoutePatternFactory.Parse(left)) == MatchKey(RoutePatternFactory.Parse(right));

        equal.Should().Be(same);
    }

    private static string LocateProjectFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "ALDevToolbox", "ALDevToolbox.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "ALDevToolbox");
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the ALDevToolbox project folder from " + AppContext.BaseDirectory);
    }
}
