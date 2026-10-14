using System.Net;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The gateway's side of registering (<see cref="GatewayRegistration"/>): the key is
/// made in the key store, only its public half is sent, the fingerprint the workbench
/// stored is checked against the key, and a failed attempt leaves the gateway as it
/// was. See <c>.design/on-prem-gateway.md</c>, "Registration and authentication".
/// </summary>
public sealed class GatewayRegistrationClientTests
{
    private readonly FakeWorkbench _workbench = new();
    private readonly FakeKeyStore _keys = new();
    private readonly InMemorySettingsStore _settings = new();
    private readonly FakeGatewayClock _clock = new();
    private readonly ListLogger _log = new();

    private GatewayRegistration Registration() => new(_workbench.Client(), _keys, _settings, _clock, _log);

    private Task<GatewaySettings> RegisterAsync(string url = FakeWorkbench.Url, string code = "ABCD-EFGH-JKLM") =>
        Registration().RegisterAsync(url, code, "CRONUS-BC01", "1.2.3", CancellationToken.None);

    /// <summary>A workbench that accepts the code and stores the key it was sent.</summary>
    private void Accept(int gatewayId = 7, Func<string, string>? fingerprint = null) =>
        _workbench.Respond = request =>
        {
            var body = request.Json<GatewayRegistrationRequest>();
            var stored = GatewayKeyFingerprint.Compute(Convert.FromBase64String(body.PublicKey));
            return FakeWorkbench.Json(new GatewayRegistrationResponse
            {
                GatewayId = gatewayId,
                Name = "CRONUS-BC01",
                KeyFingerprint = fingerprint?.Invoke(stored) ?? stored,
                Approved = false,
            });
        };

    [Fact]
    public async Task Registering_sends_the_code_and_public_key_and_records_the_gateway()
    {
        Accept();

        var settings = await RegisterAsync(FakeWorkbench.Url + "/");

        var sent = _workbench.Requests.Single();
        sent.Method.Should().Be("POST");
        sent.Path.Should().Be(GatewayProtocol.RegisterPath);
        sent.Authorization.Should().BeNull();
        var body = sent.Json<GatewayRegistrationRequest>();
        body.Code.Should().Be("ABCD-EFGH-JKLM");
        body.MachineName.Should().Be("CRONUS-BC01");
        body.Version.Should().Be("1.2.3");
        body.ProtocolVersion.Should().Be(GatewayProtocol.Version);
        Convert.FromBase64String(body.PublicKey).Should().Equal(_keys.ExportPublicKey(settings.KeyName));
        sent.Body.Should().NotContain("PRIVATE");

        settings.WorkbenchUrl.Should().Be(FakeWorkbench.Url);
        settings.GatewayId.Should().Be(7);
        settings.KeyFingerprint.Should().Be(GatewayKeyFingerprint.Compute(_keys.ExportPublicKey(settings.KeyName)));
        settings.KeyName.Should().StartWith(GatewayRegistration.KeyNamePrefix);
        _settings.Value.Should().Be(settings);
        _log.Messages(GatewayLog.RegisteredId).Should().ContainSingle().Which.Should().Contain(settings.KeyFingerprint);
    }

    [Fact]
    public async Task The_new_key_is_granted_to_the_service_account_the_install_step_recorded()
    {
        _settings.Value = new GatewaySettings { ServiceAccount = @"CRONUS\svc-bcgateway$" };
        Accept();

        var settings = await RegisterAsync();

        _keys.Granted[settings.KeyName].Should().Be(@"CRONUS\svc-bcgateway$");
        settings.ServiceAccount.Should().Be(@"CRONUS\svc-bcgateway$");
    }

    [Fact]
    public async Task A_refused_code_leaves_no_new_key_and_keeps_the_previous_registration()
    {
        var previous = await RegisterOnceAsync();
        _workbench.Respond = _ => FakeWorkbench.Json(
            new GatewayErrorResponse { Errors = new Dictionary<string, string> { ["code"] = "This registration code has expired. Ask an Admin for a new one." } },
            HttpStatusCode.BadRequest);

        var act = () => RegisterAsync();

        (await act.Should().ThrowAsync<GatewayRegistrationException>())
            .Which.Message.Should().Contain("refused the registration").And.Contain("has expired");
        _keys.Names.Should().Equal(previous.KeyName);
        _settings.Value.Should().Be(previous);
    }

    [Fact]
    public async Task Registering_again_with_a_rotation_code_replaces_the_key_and_deletes_the_old_one()
    {
        var previous = await RegisterOnceAsync();
        _clock.Advance(TimeSpan.FromDays(1));
        Accept();

        var rotated = await RegisterAsync();

        rotated.GatewayId.Should().Be(previous.GatewayId);
        rotated.KeyName.Should().NotBe(previous.KeyName);
        rotated.KeyFingerprint.Should().NotBe(previous.KeyFingerprint);
        _keys.Names.Should().Equal(rotated.KeyName);
    }

    [Fact]
    public async Task A_fingerprint_that_does_not_match_the_key_is_refused_and_nothing_is_kept()
    {
        Accept(fingerprint: _ => "0000-0000-0000-0000-0000");

        var act = () => RegisterAsync();

        (await act.Should().ThrowAsync<GatewayRegistrationException>())
            .Which.Message.Should().Contain("different key").And.Contain("Do not approve");
        _keys.Names.Should().BeEmpty();
        _settings.Value.Should().BeNull();
    }

    [Fact]
    public async Task An_unreachable_workbench_says_to_check_the_address_and_the_proxy()
    {
        _workbench.Respond = _ => throw new HttpRequestException("No such host is known.");

        var act = () => RegisterAsync();

        (await act.Should().ThrowAsync<GatewayRegistrationException>())
            .Which.Message.Should().Contain("No such host is known").And.Contain("proxy");
        _keys.Names.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://workbench.cronus.example", "must start with https://")]
    [InlineData("workbench.cronus.example", "not a web address")]
    [InlineData("https://workbench.cronus.example/?x=1", "without anything after")]
    [InlineData("", "--url")]
    public async Task A_bad_address_is_refused_before_anything_is_made(string url, string message)
    {
        var act = () => RegisterAsync(url);

        (await act.Should().ThrowAsync<GatewayRegistrationException>()).Which.Message.Should().Contain(message);
        _workbench.Requests.Should().BeEmpty();
        _keys.Names.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_code_says_where_an_Admin_gets_one()
    {
        var act = () => RegisterAsync(code: " ");

        (await act.Should().ThrowAsync<GatewayRegistrationException>()).Which.Message.Should().Contain("Add gateway");
    }

    [Theory]
    [InlineData("https://workbench.cronus.example/", "https://workbench.cronus.example", "/gateway/heartbeat")]
    [InlineData("https://cronus.example/al/", "https://cronus.example/al", "/al/gateway/heartbeat")]
    [InlineData("http://localhost:5000", "http://localhost:5000", "/gateway/heartbeat")]
    public void The_address_is_normalised_and_keeps_a_path_the_workbench_is_served_under(string raw, string normalised, string heartbeatPath)
    {
        var url = WorkbenchAddress.TryNormalise(raw, out var error);

        error.Should().BeEmpty();
        url.Should().Be(normalised);
        WorkbenchAddress.PathFor(url!, GatewayProtocol.HeartbeatPath).Should().Be(heartbeatPath);
        WorkbenchAddress.AudienceFor(url!).Should().Be(normalised + "/gateway");
    }

    private async Task<GatewaySettings> RegisterOnceAsync()
    {
        Accept();
        return await RegisterAsync();
    }
}
