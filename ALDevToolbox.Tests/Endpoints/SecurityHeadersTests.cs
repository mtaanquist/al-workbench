using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The baseline security headers (issue #677) are pipeline-wide, not
/// per-endpoint: they have to survive on a Razor page, on a static asset
/// served by MapStaticAssets, and on the health probe that short-circuits
/// before routing. A regression that moves the middleware below
/// <c>MapStaticAssets</c> would silently drop two of those three.
/// </summary>
public sealed class SecurityHeadersTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public SecurityHeadersTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/favicon.svg")]
    [InlineData("/healthz")]
    public async Task Every_response_carries_the_baseline_security_headers(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the path under test has to actually be served");

        Single(response, "X-Content-Type-Options").Should().Be("nosniff");
        Single(response, "Referrer-Policy").Should().Be("strict-origin-when-cross-origin");
        Single(response, "X-Frame-Options").Should().Be("DENY");

        // Report-only while the policy beds in; the enforce flip is a single
        // constant in SecurityHeaders. Accept either header name so flipping it
        // doesn't turn this test red, but pin the directives that matter.
        var csp = response.Headers.TryGetValues("Content-Security-Policy-Report-Only", out var reportOnly)
            ? reportOnly.Single()
            : Single(response, "Content-Security-Policy");
        csp.Should().Contain("default-src 'self'")
            .And.Contain("frame-ancestors 'none'")
            .And.Contain("object-src 'none'");
    }

    private static string Single(HttpResponseMessage response, string name)
    {
        response.Headers.TryGetValues(name, out var values).Should().BeTrue($"{name} should be present");
        return values!.Single();
    }
}
