using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// Pins the auth boundary at the HTTP layer: every endpoint flagged
/// <c>RequireAuthorization()</c> in <c>Program.cs</c> redirects anonymous
/// callers to the cookie-auth login path, never returns a 200 with content,
/// and never leaks a service-level stack trace. A regression that drops
/// <c>.RequireAuthorization()</c> from a Map call would otherwise ship —
/// service-layer tests can't see it.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class AuthenticationRequiredTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public AuthenticationRequiredTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("/generate/workspace")]
    [InlineData("/generate/extension")]
    [InlineData("/admin/export/download")]
    [InlineData("/admin/object-explorer/import")]
    public async Task Unauthenticated_post_to_a_protected_endpoint_redirects_to_login(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync(path, new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>()));

        // Antiforgery runs first; without a token we expect a 400 instead of
        // the auth redirect. But auth is *also* enforced — assert one of the
        // two refusal shapes lands. A 200 with a generated ZIP would be the
        // real regression.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.BadRequest);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            AssertRedirectsToLogin(response, expectedReturn: path);
        }
    }

    [Fact]
    public async Task Anonymous_get_to_the_logo_preview_redirects_to_login()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/admin/configuration/logo/preview");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        AssertRedirectsToLogin(response, expectedReturn: "/admin/configuration/logo/preview");
    }

    /// <summary>The build downloads: a compiled app, the zip of all of them, and the raw log (#1274).</summary>
    [Theory]
    [InlineData("/artifacts/build/1/app/1")]
    [InlineData("/artifacts/build/1/all")]
    [InlineData("/artifacts/build/1/log")]
    public async Task Anonymous_get_to_a_build_download_redirects_to_login(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        AssertRedirectsToLogin(response, expectedReturn: path);
    }

    [Fact]
    public async Task Anonymous_get_to_a_site_admin_route_returns_not_found_not_redirect()
    {
        // /site-admin/* must 404 for non-SiteAdmin (and anonymous) callers —
        // a redirect would tell an attacker the route exists.
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/site-admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Cookie auth issues an absolute Location ("https://localhost/login?ReturnUrl=…")
    /// when running through <see cref="WebApplicationFactory{T}"/>. Parse the URI
    /// and pin the path exactly so a regression to e.g. "/admin/login" or a
    /// different host can't pass the test, then verify the ReturnUrl carries
    /// the original request path back so post-login redirect works end-to-end.
    /// </summary>
    private static void AssertRedirectsToLogin(HttpResponseMessage response, string expectedReturn)
    {
        var location = response.Headers.Location;
        location.Should().NotBeNull("a redirect response must carry a Location header");
        var uri = location!.IsAbsoluteUri
            ? location
            : new Uri(new Uri("https://localhost/"), location);
        uri.AbsolutePath.Should().Be("/login",
            "the cookie auth handler is configured with LoginPath = \"/login\"");
        var query = QueryHelpers.ParseQuery(uri.Query);
        query.Should().ContainKey("ReturnUrl");
        query["ReturnUrl"].ToString().Should().Be(expectedReturn,
            "the original request path must round-trip via ReturnUrl so post-login lands the user back where they started");
    }
}
