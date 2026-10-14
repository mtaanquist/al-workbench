using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The gateway routes booted end to end, so the real pipeline is in the picture:
/// the anonymous registration exchange, the heartbeat behind the gateway scheme,
/// and the fence between that scheme and every other route. Refusals are asserted
/// for their status and their JSON body, because <c>UseStatusCodePagesWithReExecute</c>
/// would otherwise turn a bare 4xx into an HTML page the gateway cannot read.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GatewayEndpointTests : IDisposable
{
    // No PUBLIC_BASE_URL in the test host, so the audience is the request's own origin.
    private const string Origin = "https://localhost";

    private readonly TestDb _db = new();
    private readonly EndpointFactory _factory;

    public GatewayEndpointTests()
    {
        _factory = new EndpointFactory(_db);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    private async Task<string> NewCodeAsync()
    {
        var admin = await GatewayTestKit.SeedUserAsync(_db, TestDb.DefaultOrgId, UserRole.Admin, $"admin{Guid.NewGuid():N}@cronus.example");
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        _db.OrgContext.CurrentUserId = admin.Id;
        await using var ctx = _db.NewContext();
        return (await GatewayTestKit.NewService(ctx, _db.OrgContext, TimeProvider.System).CreateRegistrationCodeAsync()).Code;
    }

    private async Task<GatewayRegistrationResponse> RegisterAsync(HttpClient client, System.Security.Cryptography.ECDsa key)
    {
        var code = await NewCodeAsync();
        using var response = await client.PostAsJsonAsync(GatewayProtocol.RegisterPath, GatewayTestKit.Request(code, key));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<GatewayRegistrationResponse>())!;
    }

    private static HttpRequestMessage Heartbeat(System.Security.Cryptography.ECDsa key, int gatewayId, string? token = null)
    {
        var (body, signed) = GatewayTestKit.SignedHeartbeat(key, gatewayId, DateTimeOffset.UtcNow, GatewayProtocol.AudienceFor(Origin));
        var request = new HttpRequestMessage(HttpMethod.Post, GatewayProtocol.HeartbeatPath)
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? signed);
        return request;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ErrorsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<GatewayErrorResponse>())!.Errors;

    [Fact]
    public async Task Register_then_heartbeat_reports_waiting_for_approval()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();

        var registered = await RegisterAsync(client, key);
        registered.Approved.Should().BeFalse();
        registered.KeyFingerprint.Should().Be(GatewayKeyFingerprint.Compute(key.ExportSubjectPublicKeyInfo()));

        using var response = await client.SendAsync(Heartbeat(key, registered.GatewayId));

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadFromJsonAsync<GatewayHeartbeatResponse>())!.Approved.Should().BeFalse();
        await using var ctx = _db.NewContext();
        var gateway = await ctx.Gateways.SingleAsync();
        gateway.LastSeenAt.Should().NotBeNull();
        gateway.ReportedVersion.Should().Be("1.0.1");
    }

    [Fact]
    public async Task An_unknown_code_is_refused_with_a_reason()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();

        using var response = await client.PostAsJsonAsync(GatewayProtocol.RegisterPath, GatewayTestKit.Request("AAAA-BBBB", key));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().ContainKey("code");
    }

    [Fact]
    public async Task A_malformed_registration_body_is_refused_with_a_reason()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(GatewayProtocol.RegisterPath,
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().ContainKey("body");
    }

    [Fact]
    public async Task Explicit_null_fields_in_a_registration_are_a_400_with_the_field_named()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var code = await NewCodeAsync();
        var json = $$"""
            {"code":"{{code}}","publicKey":"{{Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}}",
             "machineName":null,"version":null,"protocolVersion":{{GatewayProtocol.Version}}}
            """;

        using var response = await client.PostAsync(GatewayProtocol.RegisterPath,
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await ErrorsOf(response)).Should().ContainKey("machineName");
    }

    [Fact]
    public void Every_gateway_route_is_rate_limited_and_register_keeps_its_stricter_policy()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith(GatewayProtocol.PathPrefix, StringComparison.Ordinal) == true)
            .ToList();

        endpoints.Should().NotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            var expected = endpoint.RoutePattern.RawText == GatewayProtocol.RegisterPath
                ? GatewayEndpoints.RegisterRateLimitPolicy
                : GatewayEndpoints.GatewayRateLimitPolicy;
            endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                .Should().Be(expected, endpoint.RoutePattern.RawText);
        }
    }

    [Fact]
    public async Task A_heartbeat_without_a_token_is_refused()
    {
        using var client = _factory.CreateClient();

        using var response = await client.PostAsJsonAsync(GatewayProtocol.HeartbeatPath, new GatewayHeartbeatRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorsOf(response)).Should().ContainKey("authorization");
    }

    [Fact]
    public async Task A_replayed_heartbeat_is_refused()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var registered = await RegisterAsync(client, key);
        var (_, token) = GatewayTestKit.SignedHeartbeat(key, registered.GatewayId, DateTimeOffset.UtcNow, GatewayProtocol.AudienceFor(Origin));

        using var first = await client.SendAsync(Heartbeat(key, registered.GatewayId, token));
        using var second = await client.SendAsync(Heartbeat(key, registered.GatewayId, token));

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ErrorsOf(second))["authorization"].Should().Contain("already received");
    }

    [Fact]
    public async Task A_gateway_token_does_not_open_the_mcp_endpoint()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var registered = await RegisterAsync(client, key);
        var token = GatewayRequestToken.Create(key, registered.GatewayId, GatewayProtocol.AudienceFor(Origin), "POST", "/mcp",
            "{}"u8, hasBody: true, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        // 401 where MCP is on, 404 where the deployment switched it off; never served.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_gateway_token_does_not_open_an_admin_page()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var registered = await RegisterAsync(client, key);
        var token = GatewayRequestToken.Create(key, registered.GatewayId, GatewayProtocol.AudienceFor(Origin), "GET",
            "/admin/administration/gateways", [], hasBody: false, DateTimeOffset.UtcNow);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/administration/gateways");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request);

        // The cookie scheme answers: a redirect to sign in, never the page.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/login");
    }

    // ---------------------------------------------------------------- minimum version (#1363)

    private const string TooOld = "11.18.0";

    private static async Task ShouldBeOutdatedAsync(HttpResponseMessage response)
    {
        ((int)response.StatusCode).Should().Be(GatewayProtocol.OutdatedStatusCode, await response.Content.ReadAsStringAsync());
        (await ErrorsOf(response)).Should().ContainKey("version").WhoseValue.Should()
            .Contain($"version {TooOld}").And.Contain($"needs version {GatewayProtocol.MinimumGatewayVersion} or later")
            .And.Contain("Administration → Gateways");
    }

    [Fact]
    public async Task A_gateway_below_the_minimum_cannot_register_and_its_code_stays_unused()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var code = await NewCodeAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, GatewayProtocol.RegisterPath)
        {
            Content = JsonContent.Create(GatewayTestKit.Request(code, key)),
        };
        request.Headers.Add(GatewayProtocol.GatewayVersionHeader, TooOld);

        using var response = await client.SendAsync(request);

        await ShouldBeOutdatedAsync(response);
        await using var ctx = _db.NewContext();
        (await ctx.Gateways.AnyAsync()).Should().BeFalse();
        (await ctx.GatewayRegistrationCodes.SingleAsync()).UsedAt.Should().BeNull("the Admin's code still works once the gateway is updated");
    }

    [Fact]
    public async Task A_gateway_below_the_minimum_is_refused_its_heartbeat_and_is_not_marked_seen()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var registered = await RegisterAsync(client, key);
        using var request = Heartbeat(key, registered.GatewayId);
        request.Headers.Add(GatewayProtocol.GatewayVersionHeader, TooOld);

        using var response = await client.SendAsync(request);

        await ShouldBeOutdatedAsync(response);
        await using var ctx = _db.NewContext();
        (await ctx.Gateways.SingleAsync()).LastSeenAt.Should().BeNull();
    }

    [Theory]
    [InlineData("GET", GatewayProtocol.NextCommandPath)]
    [InlineData("POST", "/gateway/commands/1/progress")]
    [InlineData("POST", "/gateway/commands/1/result")]
    [InlineData("GET", "/gateway/commands/1/files/1")]
    public async Task A_gateway_below_the_minimum_is_refused_every_command_route_before_its_token_is_read(string method, string path)
    {
        // No token at all: a gateway too old for today's protocol is told to update,
        // not given a 401 for a token it built the old way.
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add(GatewayProtocol.GatewayVersionHeader, TooOld);

        using var response = await client.SendAsync(request);

        await ShouldBeOutdatedAsync(response);
    }

    [Theory]
    [InlineData(GatewayProtocol.MinimumGatewayVersion)]
    [InlineData("99.0.0")]
    [InlineData("0.0.0-dev")]
    public async Task A_gateway_at_or_above_the_minimum_or_a_preview_build_registers_as_usual(string version)
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var code = await NewCodeAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, GatewayProtocol.RegisterPath)
        {
            Content = JsonContent.Create(GatewayTestKit.Request(code, key)),
        };
        request.Headers.Add(GatewayProtocol.GatewayVersionHeader, version);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_version_check_leaves_other_routes_alone()
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        request.Headers.Add(GatewayProtocol.GatewayVersionHeader, TooOld);

        using var response = await client.SendAsync(request);

        ((int)response.StatusCode).Should().NotBe(GatewayProtocol.OutdatedStatusCode);
    }
}
