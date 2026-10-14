using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <see cref="GatewayPolicySource"/>: the policy file as the running gateway sees it.
/// No file allows nothing; an edit takes effect on the next look; a file with mistakes
/// keeps the last good tiers readable but refuses every change; and someone other than
/// administrators being able to change the files stops the gateway at start and
/// refuses every command later. See <c>.design/on-prem-gateway.md</c>, "The local
/// policy file".
/// </summary>
public sealed class GatewayPolicySourceTests : IDisposable
{
    private const string Good = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "test"
        allow = ["inventory", "service_control"]
        """;

    private readonly ScratchFolder _scratch = new();
    private readonly FakeFileAccess _access = new();
    private readonly FakeGatewayClock _clock = new();
    private readonly ListLogger _log = new();
    private readonly string _file;

    public GatewayPolicySourceTests()
    {
        _file = Path.Combine(_scratch.Path, GatewayPolicyParser.FileName);
    }

    public void Dispose() => _scratch.Dispose();

    private GatewayPolicySource Source() =>
        new(new GatewayPolicyOptions(_file, _scratch.Path, "CRONUS-BC01"), _access, _clock, TimeZoneInfo.Utc, _log);

    private static GatewayCommandMessage Command(GatewayVerb verb) => new()
    {
        Id = 1,
        Verb = verb,
        SecondsLeft = 600,
        Parameters = new JsonObject
        {
            [GatewayCommandParameters.Machine] = "CRONUS-BC01",
            [GatewayCommandParameters.Instance] = "BC140",
        },
    };

    [Fact]
    public void With_no_file_every_command_is_refused_and_the_gateway_still_starts()
    {
        var source = Source();

        source.Start().Should().BeEmpty();
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Missing);
        source.Check(Command(GatewayVerb.RefreshInventory)).Allowed.Should().BeFalse();
        _log.Messages(GatewayLog.PolicyMissingId).Should().ContainSingle();
    }

    [Fact]
    public void An_edit_takes_effect_at_the_next_look_and_moves_the_report_version()
    {
        File.WriteAllText(_file, Good);
        var source = Source();
        source.Start().Should().BeEmpty();
        var first = source.Refresh();
        first.Report.State.Should().Be(GatewayPolicyStates.Valid);
        source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeTrue();
        source.Refresh().Version.Should().Be(first.Version, "nothing changed");

        File.WriteAllText(_file, Good.Replace("\"service_control\"", "\"restart\"", StringComparison.Ordinal));

        source.Check(Command(GatewayVerb.RestartInstance)).Reason.Should().Contain("does not allow 'service_control'");
        source.Refresh().Version.Should().BeGreaterThan(first.Version);
        source.Refresh().Report.Instances.Single().Allow.Should().Equal("inventory", "restart");
        _log.Messages(GatewayLog.PolicyLoadedId).Should().HaveCount(2);
    }

    [Fact]
    public void A_file_with_mistakes_keeps_the_last_good_tiers_readable_and_refuses_every_change_until_fixed()
    {
        File.WriteAllText(_file, Good);
        var source = Source();
        source.Start().Should().BeEmpty();

        File.WriteAllText(_file, Good + "\nallow_unsinged = true\n");
        var snapshot = source.Refresh();

        snapshot.Report.State.Should().Be(GatewayPolicyStates.Invalid);
        snapshot.Report.Problems.Should().ContainSingle().Which.Should().StartWith("Line 6: 'allow_unsinged'");
        snapshot.Report.Instances.Single().Allow.Should().Equal("inventory");
        source.Check(Command(GatewayVerb.RefreshInventory)).Allowed.Should().BeTrue();
        source.Check(Command(GatewayVerb.RestartInstance)).Reason.Should()
            .EndWith("The customer's gateway.toml has mistakes, so only reading is allowed until it is corrected.");
        _log.Messages(GatewayLog.PolicyInvalidId).Should().ContainSingle("the same mistake is logged once, not every minute");
        source.Refresh();
        _log.Messages(GatewayLog.PolicyInvalidId).Should().ContainSingle();

        File.WriteAllText(_file, Good);
        source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void A_file_with_mistakes_from_the_start_allows_nothing()
    {
        File.WriteAllText(_file, "[[instance]\n");
        var source = Source();

        source.Start().Should().BeEmpty();
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Invalid);
        source.Check(Command(GatewayVerb.RefreshInventory)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void A_file_that_is_not_utf8_is_a_mistake_saying_how_to_save_it()
    {
        File.WriteAllBytes(_file, [0x23, 0x20, 0xE6, 0xF8, 0xE5, 0x0A]);

        Source().Refresh().Report.Problems.Should().ContainSingle().Which.Should().Contain("Save it as UTF-8");
    }

    [Fact]
    public void Loose_permissions_on_the_file_stop_the_gateway_at_start()
    {
        File.WriteAllText(_file, Good);
        _access.Problems[_file] = $"BUILTIN\\Users can change {_file}. Only Administrators and SYSTEM may.";

        var problems = Source().Start();

        problems.Should().ContainSingle().Which.Should().StartWith("BUILTIN\\Users can change");
        _log.Messages(GatewayLog.PolicyUnsafeId).Should().ContainSingle().Which.Should().Contain("BUILTIN\\Users can change");
    }

    [Fact]
    public void Loose_permissions_found_later_refuse_every_command_reads_included()
    {
        File.WriteAllText(_file, Good);
        var source = Source();
        source.Start().Should().BeEmpty();

        _access.Problems[_scratch.Path] = "CRONUS\\Developers can change the program folder.";

        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Unsafe);
        var decision = source.Check(Command(GatewayVerb.RefreshInventory));
        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("CRONUS\\Developers can change the program folder.");
    }

    [Fact]
    public void Each_named_script_and_its_folder_are_checked_and_a_missing_script_makes_the_file_read_only()
    {
        const string script = @"C:\ALWorkbench\before-deploy.ps1";
        File.WriteAllText(_file, Good + """

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            before = 'C:\ALWorkbench\before-deploy.ps1'
            """);
        var source = Source();

        var missing = source.Refresh();
        missing.Report.State.Should().Be(GatewayPolicyStates.Invalid);
        missing.Report.Problems.Should().Equal($"The script {script} does not exist.");

        _access.Existing.Add(script);
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Valid);
        _access.Asked.Should().Contain([script, @"C:\ALWorkbench", _scratch.Path, _file]);

        _access.Problems[@"C:\ALWorkbench"] = @"Everyone can change C:\ALWorkbench.";
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Unsafe);
    }

    [Theory]
    [InlineData(@"C:\ALWorkbench\before.ps1", @"C:\ALWorkbench")]
    [InlineData(@"C:\before.ps1", @"C:\")]
    [InlineData("before.ps1", null)]
    public void The_folder_of_a_windows_path_is_found_the_same_way_on_any_system(string path, string? folder)
    {
        GatewayPolicySource.WindowsFolderOf(path).Should().Be(folder);
    }
}
