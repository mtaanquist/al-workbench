using System.Net;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// Which redirect URIs an OAuth client may register and use, through every
/// door a client comes in by: anonymous registration at <c>/oauth/register</c>,
/// a client metadata document (URL-shaped <c>client_id</c>), and the
/// authorize request itself for a row stored before the rule existed. Also
/// pins that the <c>form_post</c> response mode is switched off.
///
/// <para>The cases share one host (#1536). Registration and metadata fetches are
/// rate limited per caller address, ten in ten minutes, so each test calls from an
/// address of its own (<see cref="Host"/> stands in for the connection address the
/// test server leaves empty); the limits themselves are untouched. The one test
/// that asserts the metadata client was never stored boots a host of its own,
/// because another test stores it.</para>
/// </summary>
public sealed class OAuthRedirectUriTests : IClassFixture<OAuthRedirectUriTests.Host>
{
    private const string CimdClientId = "https://client.cronus.example/oauth/metadata.json";
    private const string CimdGoodRedirect = "https://client.cronus.example/oauth/callback";
    private const string Loopback = "http://127.0.0.1:41999/callback";
    // RFC 7636 appendix B test vector - any well-formed S256 challenge will do.
    private const string CodeChallenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    private static int _lastAddress;

    private readonly EndpointFactory _factory;

    /// <summary>This test's own caller address, from the 198.18.0.0/15 benchmarking range.</summary>
    private readonly string _address;

    public OAuthRedirectUriTests(Host host)
    {
        _factory = host.Factory;
        var n = Interlocked.Increment(ref _lastAddress);
        _address = $"198.18.{n / 250}.{n % 250 + 1}";
    }

    /// <summary>A client whose requests arrive from this test's address.</summary>
    private HttpClient NewClient() => NewClient(_factory, _address);

    private static HttpClient NewClient(EndpointFactory factory, string address)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(Host.AddressHeader, address);
        return client;
    }

    /// <summary>
    /// The shared host: metadata documents come from <see cref="MetadataDocumentHandler"/>,
    /// and each request arrives from the address named in <see cref="AddressHeader"/>.
    /// </summary>
    public sealed class Host : SharedEndpointHost
    {
        public const string AddressHeader = "X-Test-Connection-Address";

        public Host()
            : base(services =>
            {
                services.AddHttpClient(nameof(CimdClientResolver))
                    .ConfigurePrimaryHttpMessageHandler(() => new MetadataDocumentHandler());
                services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, AddressFilter>();
            })
        {
        }

        /// <summary>Runs ahead of the app's pipeline, so the rate limiters partition on the named address.</summary>
        private sealed class AddressFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
        {
            public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
                Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
            {
                app.Use(nextMiddleware => ctx =>
                {
                    var named = ctx.Request.Headers[AddressHeader].ToString();
                    if (named.Length > 0) ctx.Connection.RemoteIpAddress = IPAddress.Parse(named);
                    return nextMiddleware(ctx);
                });
                next(app);
            };
        }
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript://cronus.example/%0Aalert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("data:text/html,hello")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("blob:https://cronus.example/1")]
    [InlineData("http://cronus.example/callback")]
    [InlineData("https://cronus.example/callback#frag")]
    [InlineData("myapp:/callback")]
    public async Task Registration_refuses_redirect_uris_outside_the_policy(string redirectUri)
    {
        using var client = NewClient();

        using var response = await RegisterAsync(client, redirectUri);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("error").GetString().Should().Be("invalid_redirect_uri");
    }

    [Fact]
    public async Task Registration_refuses_the_whole_request_when_one_redirect_uri_is_refused()
    {
        using var client = NewClient();

        using var response = await RegisterAsync(client, "https://cronus.example/callback", "javascript:alert(1)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("https://cronus.example/callback")]
    [InlineData("http://127.0.0.1:41999/callback")]
    [InlineData("http://localhost:41999/callback")]
    [InlineData("http://[::1]:41999/callback")]
    [InlineData("com.cronus.workbench:/oauth/callback")]
    public async Task Registration_accepts_https_loopback_and_reverse_domain_schemes(string redirectUri)
    {
        using var client = NewClient();

        using var response = await RegisterAsync(client, redirectUri);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Client_metadata_document_keeps_only_allowed_redirect_uris()
    {
        using var client = NewClient();

        // Any authorize request naming the URL-shaped client_id makes the
        // server fetch the document and store (or refresh) the client.
        using var response = await client.GetAsync(AuthorizeUrl(CimdClientId, CimdGoodRedirect));
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "the document still lists one allowed redirect URI, so the request goes ahead.");

        using var scope = _factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var application = await applications.FindByClientIdAsync(CimdClientId, CancellationToken.None);
        application.Should().NotBeNull();
        var stored = await applications.GetRedirectUrisAsync(application!, CancellationToken.None);
        stored.Should().BeEquivalentTo([CimdGoodRedirect]);
    }

    [Theory]
    [InlineData(true)]
    // Omitted: OpenIddict would fall back to the client's single stored URI.
    [InlineData(false)]
    public async Task Authorize_refuses_a_stored_redirect_uri_outside_the_policy(bool sendRedirectUri)
    {
        // A client registered before the rule existed: written straight
        // through the application manager, which does not apply the policy.
        const string legacyRedirect = "javascript:alert(1)";
        var clientId = Guid.NewGuid().ToString("N");
        using (var scope = _factory.Services.CreateScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = clientId,
                ClientType = OpenIddictConstants.ClientTypes.Public,
                ConsentType = OpenIddictConstants.ConsentTypes.Explicit,
                DisplayName = "CRONUS legacy client",
                Permissions =
                {
                    OpenIddictConstants.Permissions.Endpoints.Authorization,
                    OpenIddictConstants.Permissions.Endpoints.Token,
                    OpenIddictConstants.Permissions.GrantTypes.AuthorizationCode,
                    OpenIddictConstants.Permissions.ResponseTypes.Code,
                    "scp:mcp",
                },
            };
            descriptor.RedirectUris.Add(new Uri(legacyRedirect));
            await applications.CreateAsync(descriptor, CancellationToken.None);
        }

        using var client = NewClient();
        var url = sendRedirectUri
            ? AuthorizeUrl(clientId, legacyRedirect)
            : AuthorizeUrl(clientId, redirectUri: null);
        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Authorize_with_a_refused_redirect_uri_does_not_store_a_metadata_client()
    {
        using var host = new Host();
        using var client = NewClient(host.Factory, _address);

        using var response = await client.GetAsync(AuthorizeUrl(CimdClientId, "javascript:alert(1)"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var scope = host.Factory.Services.CreateScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        (await applications.FindByClientIdAsync(CimdClientId, CancellationToken.None)).Should().BeNull(
            "the request is refused before the metadata document is fetched and stored.");
    }

    [Fact]
    public async Task Authorize_refuses_the_form_post_response_mode()
    {
        using var client = NewClient();
        using var registration = await RegisterAsync(client, Loopback);
        registration.StatusCode.Should().Be(HttpStatusCode.Created);
        using var registered = JsonDocument.Parse(await registration.Content.ReadAsStringAsync());
        var clientId = registered.RootElement.GetProperty("client_id").GetString()!;

        // The same request without response_mode goes through to sign-in,
        // so the 400 below is about form_post alone.
        using var plain = await client.GetAsync(AuthorizeUrl(clientId, Loopback));
        plain.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);

        using var formPost = await client.GetAsync(AuthorizeUrl(clientId, Loopback) + "&response_mode=form_post");
        formPost.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Discovery_does_not_advertise_form_post()
    {
        using var client = NewClient();
        using var response = await client.GetAsync("/.well-known/oauth-authorization-server");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var modes = doc.RootElement.GetProperty("response_modes_supported").EnumerateArray()
            .Select(e => e.GetString())
            .ToArray();
        modes.Should().Contain("query");
        modes.Should().NotContain("form_post");
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, params string[] redirectUris)
    {
        var body = JsonSerializer.Serialize(new { redirect_uris = redirectUris, client_name = "CRONUS test client" });
        return client.PostAsync("/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private static string AuthorizeUrl(string clientId, string? redirectUri) =>
        "/oauth/authorize"
        + "?client_id=" + Uri.EscapeDataString(clientId)
        + (redirectUri is null ? string.Empty : "&redirect_uri=" + Uri.EscapeDataString(redirectUri))
        + "&response_type=code&scope=mcp"
        + "&code_challenge=" + CodeChallenge
        + "&code_challenge_method=S256";

    /// <summary>
    /// Serves the client metadata document for <see cref="CimdClientId"/>:
    /// one allowed redirect URI and several the policy refuses.
    /// </summary>
    private sealed class MetadataDocumentHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var document = JsonSerializer.Serialize(new
            {
                client_id = CimdClientId,
                client_name = "CRONUS hosted client",
                token_endpoint_auth_method = "none",
                redirect_uris = new[]
                {
                    "javascript:alert(1)",
                    "data:text/html,hello",
                    "http://client.cronus.example/oauth/callback",
                    CimdGoodRedirect,
                },
            });
            var status = request.RequestUri?.AbsoluteUri == CimdClientId ? HttpStatusCode.OK : HttpStatusCode.NotFound;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(document, Encoding.UTF8, "application/json"),
            });
        }
    }
}
