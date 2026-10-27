using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The per-address rate limits on the routes that need one (#1435). Dropping a
/// <c>RequireRateLimiting</c> or loosening a limit changes no answer a single request
/// gets, so nothing else in the suite would notice. The Diff tool's burst is pinned in
/// <see cref="CompareEndpointTests"/>; the two anonymous routes that write or guess
/// something - client registration and the Sealed reveal - get a burst here, and every
/// limited route gets its policy name checked on the endpoint itself.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class RateLimitPolicyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public RateLimitPolicyTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    [Theory]
    [InlineData("POST", "/oauth/register", OAuthEndpoints.DcrRateLimitPolicy)]
    [InlineData("POST", GitHubWebhookEndpoints.WebhookPath, GitHubWebhookEndpoints.WebhookRateLimitPolicy)]
    [InlineData("POST", "/api/sealed/{token}/reveal", SealedEndpoints.RevealRateLimitPolicy)]
    [InlineData("GET", PaletteEndpoints.SearchPath, PaletteEndpoints.SearchRateLimitPolicy)]
    [InlineData("GET", PaletteEndpoints.ContextPath, PaletteEndpoints.SearchRateLimitPolicy)]
    [InlineData("POST", "/api/compare/diff", CompareEndpoints.DiffRateLimitPolicy)]
    public void A_limited_route_carries_its_policy(string method, string route, string policy)
    {
        var endpoint = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => string.Equals(e.RoutePattern.RawText?.TrimEnd('/'), route, StringComparison.OrdinalIgnoreCase)
                && e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true)
            .Should().ContainSingle()
            .Subject;

        var limit = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>();
        limit.Should().NotBeNull("{0} {1} is limited per address", method, route);
        limit!.PolicyName.Should().Be(policy);
    }

    [Fact]
    public async Task The_eleventh_client_registration_inside_the_window_is_refused_and_stores_nothing()
    {
        // Registration is anonymous and each one is a stored client, so the limit is
        // what bounds how many rows one address can create (#378).
        using var client = _factory.CreateClient();

        for (var i = 1; i <= 10; i++)
        {
            using var accepted = await RegisterAsync(client, i);
            accepted.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "registration {0} is inside the limit", i);
            accepted.IsSuccessStatusCode.Should().BeTrue();
        }
        var stored = await CountClientsAsync();

        using var refused = await RegisterAsync(client, 11);

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await CountClientsAsync()).Should().Be(stored, "a refused registration writes no client");
    }

    [Fact]
    public async Task The_twenty_first_sealed_reveal_inside_the_window_is_refused()
    {
        // Every reveal counts, found or not: the limit is against guessing, so a
        // run of misses is exactly what it has to catch.
        using var client = _factory.CreateClient();
        var check = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        for (var i = 1; i <= 20; i++)
        {
            using var answered = await client.PostAsJsonAsync("/api/sealed/AAAAAAAAAAAAAAAAAAAAAA/reveal", new { check });
            answered.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "reveal {0} is inside the limit", i);
        }

        using var refused = await client.PostAsJsonAsync("/api/sealed/AAAAAAAAAAAAAAAAAAAAAA/reveal", new { check });

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client, int n)
    {
        var body = JsonSerializer.Serialize(new
        {
            redirect_uris = new[] { $"https://client{n}.cronus.example/callback" },
            client_name = $"CRONUS test client {n}",
        });
        return client.PostAsync("/oauth/register", new StringContent(body, Encoding.UTF8, "application/json"));
    }

    private async Task<long> CountClientsAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CountAsync();
    }
}
