using System.Net;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Routing;

/// <summary>
/// The gateway setup guide is public (#1376): customer IT reads it before anyone
/// there has an account. <see cref="RouteAuthorizationTests"/> checks the attribute;
/// this fetches the page signed out through the real pipeline, so a layout, a
/// component or an injected service that needs a signed-in organisation would show
/// up here as a redirect or a failure rather than in production.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GatewaySetupDocsAnonymousTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public GatewaySetupDocsAnonymousTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_signed_out_reader_gets_the_whole_guide()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/docs/gateway-setup");
        var html = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the guide must not send customer IT to a sign-in page");
        html.Should().Contain("Setting up the on-premises gateway")
            .And.Contain("id=\"gs-example-group-toml\"")
            .And.Contain("id=\"gs-logs\"");
    }

    [Fact]
    public async Task The_signed_in_only_docs_page_still_asks_for_sign_in()
    {
        // The control: the same client is turned away from a docs page that is not
        // public, so the 200 above is the attribute's doing and not an open pipeline.
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/docs/search");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
