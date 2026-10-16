using System.Text.Json;
using ALWorkbench.Gateway.Contracts;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The verb set both sides share: its wire words, and that anything else fails to
/// deserialise rather than falling back to a default. See
/// <c>.design/on-prem-gateway.md</c>, "Commands".
/// </summary>
public sealed class GatewayVerbTests
{
    [Fact]
    public void Every_verb_has_its_kebab_case_wire_word()
    {
        GatewayVerbs.All.Should().Equal(
            "refresh-inventory", "publish-install", "restart-group", "restart-instance",
            "stop-instance", "start-instance", "import-licence");
        foreach (var verb in Enum.GetValues<GatewayVerb>())
        {
            GatewayVerbs.Parse(GatewayVerbs.ToWire(verb)).Should().Be(verb);
        }
    }

    [Fact]
    public void A_command_round_trips_with_its_verb_as_the_wire_word()
    {
        var json = JsonSerializer.Serialize(new GatewayCommandMessage { Id = 7, Verb = GatewayVerb.PublishInstall, SecondsLeft = 60 });

        json.Should().Contain("\"verb\":\"publish-install\"");
        JsonSerializer.Deserialize<GatewayCommandMessage>(json)!.Verb.Should().Be(GatewayVerb.PublishInstall);
    }

    [Theory]
    [InlineData("\"run-script\"")]
    [InlineData("\"PublishInstall\"")]
    [InlineData("\"Publish-Install\"")]
    [InlineData("1")]
    [InlineData("null")]
    public void An_unknown_verb_fails_to_deserialise(string verb)
    {
        var json = $$"""{"id":7,"verb":{{verb}},"parameters":{},"secondsLeft":60,"files":[]}""";

        var act = () => JsonSerializer.Deserialize<GatewayCommandMessage>(json);

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Parameters_must_be_an_object()
    {
        var act = () => JsonSerializer.Deserialize<GatewayCommandMessage>(
            """{"id":7,"verb":"restart-group","parameters":["x"],"secondsLeft":60,"files":[]}""");

        act.Should().Throw<JsonException>();
    }
}
