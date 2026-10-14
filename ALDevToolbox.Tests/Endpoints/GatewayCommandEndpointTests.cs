using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Endpoints;

/// <summary>
/// The command routes booted end to end: the long-poll, progress, result, the file
/// download and the heartbeat's command lists, each behind the gateway scheme. The
/// long-poll timeout is shortened through the <see cref="GatewayCommandSignal"/>
/// singleton so the empty case does not take 30 seconds.
/// </summary>
[Collection(EndpointFactoryCollection.Name)]
public sealed class GatewayCommandEndpointTests : IDisposable
{
    // No PUBLIC_BASE_URL in the test host, so the audience is the request's own origin.
    private const string Origin = "https://localhost";
    private static readonly string Audience = GatewayProtocol.AudienceFor(Origin);

    private readonly TestDb _db = new();
    private readonly GatewayCommandSignal _signal = new() { LongPollTimeout = TimeSpan.FromSeconds(1) };
    private readonly EndpointFactory _factory;
    private User? _admin;

    public GatewayCommandEndpointTests()
    {
        _factory = new EndpointFactory(_db, services => services.AddSingleton(_signal));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _db.Dispose();
    }

    private async Task<int> GatewayAsync(ECDsa key, bool approve = true, int organizationId = TestDb.DefaultOrgId, string machine = "CRONUS-BC01")
    {
        var admin = organizationId == TestDb.DefaultOrgId
            ? _admin ??= await GatewayTestKit.SeedUserAsync(_db, organizationId, UserRole.Admin, "admin@cronus.example")
            : await GatewayTestKit.SeedUserAsync(_db, organizationId, UserRole.Admin, $"admin{Guid.NewGuid():N}@other.example");
        return (await GatewayTestKit.RegisterAsync(_db, TimeProvider.System, organizationId, admin.Id, key, approve, machine)).GatewayId;
    }

    private async Task<long> EnqueueAsync(int gatewayId, IReadOnlyList<GatewayCommandFileInput>? files = null, int organizationId = TestDb.DefaultOrgId)
    {
        var scope = new AmbientOrganizationContext { CurrentOrganizationId = organizationId };
        await using var ctx = _db.NewContext(scope);
        return await new GatewayCommandService(ctx, scope, _signal, TimeProvider.System, NullLogger<GatewayCommandService>.Instance)
            .EnqueueAsync(new GatewayCommandRequest(gatewayId, GatewayVerb.PublishInstall, Files: files));
    }

    private static HttpRequestMessage Signed(ECDsa key, int gatewayId, HttpMethod method, string path, byte[]? body = null)
    {
        var token = GatewayRequestToken.Create(key, gatewayId, Audience, method.Method, path, body ?? [], hasBody: body is not null, DateTimeOffset.UtcNow);
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static HttpRequestMessage SignedJson<T>(ECDsa key, int gatewayId, string path, T value) =>
        Signed(key, gatewayId, HttpMethod.Post, path, JsonSerializer.SerializeToUtf8Bytes(value));

    private static async Task<IReadOnlyDictionary<string, string>> ErrorsOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<GatewayErrorResponse>())!.Errors;

    private async Task<GatewayCommandStatus> StatusAsync(long id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.GatewayCommands.Where(c => c.Id == id).Select(c => c.Status).SingleAsync();
    }

    [Fact]
    public async Task A_pending_gateway_is_refused_commands_with_a_reason()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key, approve: false);

        using var response = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorsOf(response))["authorization"].Should().Contain("waiting for an Admin to approve");
    }

    [Fact]
    public async Task A_revoked_gateway_is_refused_commands()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        _db.OrgContext.CurrentUserId = _admin!.Id;
        await using (var ctx = _db.NewContext())
        {
            await GatewayTestKit.NewService(ctx, _db.OrgContext, TimeProvider.System).RevokeAsync(gateway);
        }

        using var response = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_long_poll_answers_no_content_when_nothing_is_queued()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);

        using var response = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_long_poll_answers_as_soon_as_a_command_is_queued()
    {
        _signal.LongPollTimeout = TimeSpan.FromSeconds(20);
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var watch = Stopwatch.StartNew();

        var polling = client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));
        await Task.Delay(500);
        var id = await EnqueueAsync(gateway);
        using var response = await polling;

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var command = (await response.Content.ReadFromJsonAsync<GatewayCommandMessage>())!;
        command.Id.Should().Be(id);
        command.Verb.Should().Be(GatewayVerb.PublishInstall);
        command.SecondsLeft.Should().BeInRange(890, 900);
        (await StatusAsync(id)).Should().Be(GatewayCommandStatus.Claimed);
    }

    [Fact]
    public async Task A_gateway_runs_a_command_through_to_its_result_and_fetches_its_file()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var content = RandomNumberGenerator.GetBytes(256);
        var id = await EnqueueAsync(gateway, [new GatewayCommandFileInput("CRONUS_Coffee_1.4.0.0.app", content)]);

        using var next = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));
        var command = (await next.Content.ReadFromJsonAsync<GatewayCommandMessage>())!;
        var file = command.Files.Single();

        using var download = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.CommandFilePath(id, file.Id)));
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Convert.ToHexStringLower(SHA256.HashData(bytes)).Should().Be(file.Sha256);

        using var progress = await client.SendAsync(SignedJson(key, gateway, GatewayProtocol.CommandProgressPath(id),
            new GatewayCommandProgressRequest { Lines = ["Published CRONUS Coffee 1.4.0.0"] }));
        progress.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StatusAsync(id)).Should().Be(GatewayCommandStatus.Running);

        using var result = await client.SendAsync(SignedJson(key, gateway, GatewayProtocol.CommandResultPath(id),
            new GatewayCommandResultRequest { Succeeded = true }));
        result.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await StatusAsync(id)).Should().Be(GatewayCommandStatus.Succeeded);

        // Finished: no more files, no more reports.
        using var late = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.CommandFilePath(id, file.Id)));
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var lateResult = await client.SendAsync(SignedJson(key, gateway, GatewayProtocol.CommandResultPath(id),
            new GatewayCommandResultRequest { Succeeded = false, FailureMessage = "again" }));
        lateResult.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorsOf(lateResult)).Should().ContainKey("command");
    }

    [Fact]
    public async Task Another_gateways_command_file_is_not_found()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        using var otherKey = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var other = await GatewayAsync(otherKey, machine: "CRONUS-BC02");
        var id = await EnqueueAsync(gateway, [new GatewayCommandFileInput("CRONUS.bclicense", [1, 2, 3])]);
        using var next = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));
        var fileId = (await next.Content.ReadFromJsonAsync<GatewayCommandMessage>())!.Files.Single().Id;

        using var response = await client.SendAsync(Signed(otherKey, other, HttpMethod.Get, GatewayProtocol.CommandFilePath(id, fileId)));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorsOf(response)).Should().ContainKey("file");
    }

    [Fact]
    public async Task Another_organisations_command_file_is_not_found()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        using var foreignKey = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var foreign = await GatewayAsync(foreignKey, organizationId: TestDb.OtherOrgId);
        var id = await EnqueueAsync(foreign, [new GatewayCommandFileInput("CRONUS.bclicense", [1, 2, 3])], TestDb.OtherOrgId);
        using var next = await client.SendAsync(Signed(foreignKey, foreign, HttpMethod.Get, GatewayProtocol.NextCommandPath));
        var fileId = (await next.Content.ReadFromJsonAsync<GatewayCommandMessage>())!.Files.Single().Id;

        using var response = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.CommandFilePath(id, fileId)));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_heartbeat_reporting_an_interrupted_command_fails_it_for_good()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var id = await EnqueueAsync(gateway);
        using (await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath))) { }

        using var heartbeat = await client.SendAsync(SignedJson(key, gateway, GatewayProtocol.HeartbeatPath,
            new GatewayHeartbeatRequest { Version = "1.0.0", MachineName = "CRONUS-BC01", InterruptedCommandIds = [id] }));
        heartbeat.StatusCode.Should().Be(HttpStatusCode.OK);

        await using (var ctx = _db.NewContext())
        {
            var row = await ctx.GatewayCommands.SingleAsync(c => c.Id == id);
            row.Status.Should().Be(GatewayCommandStatus.Failed);
            row.FailureMessage.Should().Be(GatewayCommandService.InterruptedMessage);
        }
        using var again = await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath));
        again.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_result_with_an_unknown_shape_is_refused_with_a_reason()
    {
        using var client = _factory.CreateClient();
        using var key = GatewayTestKit.NewKey();
        var gateway = await GatewayAsync(key);
        var id = await EnqueueAsync(gateway);
        using (await client.SendAsync(Signed(key, gateway, HttpMethod.Get, GatewayProtocol.NextCommandPath))) { }

        using var response = await client.SendAsync(Signed(key, gateway, HttpMethod.Post, GatewayProtocol.CommandResultPath(id),
            Encoding.UTF8.GetBytes("""{"succeeded":true,"result":["not","an","object"]}""")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorsOf(response)).Should().ContainKey("body");
    }
}
