using System.Net;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Services.OAuth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

namespace ALDevToolbox.Tests.OAuth;

/// <summary>
/// <see cref="CimdClientResolver"/> against a stub HTTP handler and the real
/// OpenIddict application manager (#1271). A metadata document creates its
/// client, a later fetch refreshes a client that a document created, and a
/// document can never rewrite a client that was registered another way: that
/// guard is what stops whoever can serve a URL from replacing a registered
/// client's redirect addresses.
/// </summary>
public sealed class CimdClientResolverTests : IDisposable
{
    private const string ClientId = "https://assistant.cronus.example/oauth/client.json";
    private const string FirstRedirect = "https://assistant.cronus.example/callback";
    private const string SecondRedirect = "https://assistant.cronus.example/callback-v2";

    private readonly TestDb _db = new();
    private readonly ServiceProvider _services;
    private readonly StubHandler _http = new();

    public CimdClientResolverTests()
    {
        var db = _db;
        _services = new ServiceCollection()
            .AddLogging()
            .AddScoped(_ => db.NewContext())
            .AddOpenIddict()
            .AddCore(o => o.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
            .Services
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _http.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task A_valid_document_creates_the_client()
    {
        _http.Serve(ClientId, Document(FirstRedirect));

        var context = await ResolveAsync(ClientId);

        context.IsRejected.Should().BeFalse();
        (await RedirectsAsync()).Should().Equal(FirstRedirect);
        (await SourceAsync()).Should().Be(CimdClientResolver.CimdRegistrationSource);
    }

    [Fact]
    public async Task A_later_fetch_refreshes_a_client_a_document_created()
    {
        _http.Serve(ClientId, Document(FirstRedirect));
        await ResolveAsync(ClientId);

        _http.Serve(ClientId, Document(SecondRedirect));
        var context = await ResolveAsync(ClientId);

        context.IsRejected.Should().BeFalse();
        (await RedirectsAsync()).Should().Equal(SecondRedirect);
    }

    [Theory]
    [InlineData("dcr")]
    [InlineData(null)]
    public async Task A_document_cannot_rewrite_a_client_registered_another_way(string? source)
    {
        // A client registered at /oauth/register ("dcr") or by an admin (no
        // marker) that happens to use a URL as its id.
        await using (var scope = _services.CreateAsyncScope())
        {
            var descriptor = new OpenIddictApplicationDescriptor
            {
                ClientId = ClientId,
                ClientType = OpenIddictConstants.ClientTypes.Public,
                DisplayName = "Registered assistant",
                RedirectUris = { new Uri(FirstRedirect) },
            };
            if (source is not null)
            {
                descriptor.Properties[CimdClientResolver.RegistrationSourceProperty] = JsonSerializer.SerializeToElement(source);
            }
            await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor);
        }
        _http.Serve(ClientId, Document("https://attacker.example/steal"));

        var context = await ResolveAsync(ClientId);

        context.IsRejected.Should().BeTrue();
        context.Error.Should().Be(OpenIddictConstants.Errors.InvalidClient);
        (await RedirectsAsync()).Should().Equal(FirstRedirect);
        (await SourceAsync()).Should().Be(source);
    }

    [Fact]
    public async Task A_document_that_names_a_different_client_is_refused()
    {
        _http.Serve(ClientId, Document(FirstRedirect, clientId: "https://elsewhere.example/client.json"));

        var context = await ResolveAsync(ClientId);

        context.IsRejected.Should().BeTrue();
        (await FindAsync()).Should().BeNull();
    }

    private async Task<ValidateAuthorizationRequestContext> ResolveAsync(string clientId)
    {
        await using var scope = _services.CreateAsyncScope();
        var resolver = new CimdClientResolver(
            scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>(),
            new StubFactory(_http),
            NullLogger<CimdClientResolver>.Instance);
        var context = new ValidateAuthorizationRequestContext(new OpenIddictServerTransaction
        {
            Options = new OpenIddictServerOptions(),
            Logger = NullLogger.Instance,
            Request = new OpenIddictRequest { ClientId = clientId },
        });
        await resolver.HandleAsync(context);
        return context;
    }

    private async Task<object?> FindAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().FindByClientIdAsync(ClientId);
    }

    private async Task<IReadOnlyList<string>> RedirectsAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var app = await apps.FindByClientIdAsync(ClientId);
        app.Should().NotBeNull();
        return (await apps.GetRedirectUrisAsync(app!)).Select(u => u.ToString()).ToList();
    }

    private async Task<string?> SourceAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var apps = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var properties = await apps.GetPropertiesAsync((await apps.FindByClientIdAsync(ClientId))!);
        return properties.TryGetValue(CimdClientResolver.RegistrationSourceProperty, out var value) ? value.GetString() : null;
    }

    private static string Document(string redirect, string clientId = ClientId) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["client_id"] = clientId,
            ["client_name"] = "CRONUS assistant",
            ["redirect_uris"] = new[] { redirect },
            ["token_endpoint_auth_method"] = "none",
        });

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers each registered URL with its JSON body, and 404 otherwise.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _bodies = new(StringComparer.Ordinal);

        public void Serve(string url, string body) => _bodies[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
