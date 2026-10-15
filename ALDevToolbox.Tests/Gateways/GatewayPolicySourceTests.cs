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
            .EndWith("The customer's gateway.toml has mistakes or could not be read, so only reading is allowed until that is fixed.");
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

    [Fact]
    public void A_folder_above_a_script_others_can_replace_things_in_makes_the_file_unsafe()
    {
        const string script = @"C:\IT\Gateway\before-deploy.ps1";
        File.WriteAllText(_file, Good + """

            [[database]]
            name = "CRONUS-SQL/BC140"
            publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
            before = 'C:\IT\Gateway\before-deploy.ps1'
            """);
        _access.Existing.Add(script);
        var source = Source();
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Valid);
        _access.AskedAncestors.Should().Contain([@"C:\IT", @"C:\"]);

        _access.AncestorProblems[@"C:\IT"] = @"BUILTIN\Users can rename or replace what is in C:\IT, which holds a script's folder.";
        var unsafeNow = source.Refresh().Report;
        unsafeNow.State.Should().Be(GatewayPolicyStates.Unsafe, "renaming C:\\IT\\Gateway aside and putting another folder in its place swaps the script");
        unsafeNow.Problems.Should().Contain(@"BUILTIN\Users can rename or replace what is in C:\IT, which holds a script's folder.");
    }

    [Fact]
    public void A_file_that_cannot_be_read_allows_only_reading_until_a_read_succeeds()
    {
        File.WriteAllText(_file, Good);
        var source = Source();
        source.Start().Should().BeEmpty();
        source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeTrue();

        using (new FileStream(_file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Held open without sharing, as an editor saving it might, and possibly
            // with a change that takes a permission away.
            var snapshot = source.Refresh();
            snapshot.Report.State.Should().Be(GatewayPolicyStates.Invalid);
            snapshot.Report.Problems.Should().ContainSingle().Which.Should().Contain("could not read");
            snapshot.Report.Instances.Single().Allow.Should().Equal("inventory");
            source.Check(Command(GatewayVerb.RefreshInventory)).Allowed.Should().BeTrue();
            source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeFalse();
            source.Refresh();
            source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeFalse("still unreadable on the next round");
        }

        source.Check(Command(GatewayVerb.RestartInstance)).Allowed.Should().BeTrue("the file read again");
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Valid);
    }

    [Fact]
    public void Every_file_in_the_program_folder_is_checked_and_a_loose_one_makes_the_gateway_unsafe()
    {
        File.WriteAllText(_file, Good);
        var dll = Path.Combine(_scratch.Path, "ALWorkbench.Gateway.Core.dll");
        _access.FolderContents[_scratch.Path] = [Path.Combine(_scratch.Path, "ALWorkbench.Gateway.exe"), dll, _file];
        var source = Source();
        source.Refresh().Report.State.Should().Be(GatewayPolicyStates.Valid);
        _access.Asked.Should().Contain([_scratch.Path, Path.Combine(_scratch.Path, "ALWorkbench.Gateway.exe"), dll, _file]);

        _access.Problems[dll] = $"CRONUS\\Developers can change {dll}. Only Administrators and SYSTEM may.";

        var snapshot = source.Refresh();
        snapshot.Report.State.Should().Be(GatewayPolicyStates.Unsafe);
        snapshot.Report.Problems.Should().ContainSingle().Which.Should().Contain("ALWorkbench.Gateway.Core.dll");
    }

    [Fact]
    public void A_program_folder_open_to_everyone_names_a_few_files_and_counts_the_rest()
    {
        File.WriteAllText(_file, Good);
        var files = Enumerable.Range(1, 12).Select(i => Path.Combine(_scratch.Path, $"Library{i}.dll")).ToList();
        _access.FolderContents[_scratch.Path] = files;
        foreach (var file in files) _access.Problems[file] = $"Everyone can change {file}.";

        var problems = Source().Start();

        problems.Should().HaveCount(FileAccessChecks.NamedPerFolder + 1);
        problems[^1].Should().Be($"7 more files or folders in {_scratch.Path} can be changed by someone other than administrators.");
    }

    [Fact]
    public void The_report_lists_fifty_problems_and_counts_the_rest()
    {
        var problems = Enumerable.Range(1, 80).Select(i => $"Line {i}: oops").ToList();

        var capped = GatewayPolicySource.Capped(problems);

        capped.Should().HaveCount(GatewayPolicyReport.MaxProblems + 1);
        capped[^1].Should().StartWith("And 30 more;");
        GatewayPolicySource.Capped(problems.Take(50).ToList()).Should().HaveCount(50);
    }

    [Fact]
    public void A_report_too_large_to_send_becomes_a_short_one_that_says_so_and_changes_are_still_noticed()
    {
        // 200 tiers with ten long tenant names each: inside the file's own limits, far
        // past what AL Workbench should be sent with a heartbeat.
        var tenants = string.Join(", ", Enumerable.Range(1, 10).Select(t => $"\"tenant{t}{new string('x', 200)}\""));
        var tiers = string.Concat(Enumerable.Range(1, GatewayPolicyParser.MaxInstances).Select(i => $"""

            [[instance]]
            machine = "CRONUS-BC01"
            name = "BC{i}"
            allow = ["inventory"]
            tenants = [{tenants}]

            """));
        File.WriteAllText(_file, tiers);
        var source = Source();

        var snapshot = source.Refresh();

        snapshot.Report.TooLarge.Should().BeTrue();
        snapshot.Report.State.Should().Be(GatewayPolicyStates.Valid);
        snapshot.Report.Instances.Should().BeEmpty();
        snapshot.Report.Problems.Should().ContainSingle().Which.Should().Contain("too large to show in AL Workbench");
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(snapshot.Report).Length.Should().BeLessThan(GatewayPolicyReport.MaxJsonBytes);
        source.FullReport.Instances.Should().HaveCount(GatewayPolicyParser.MaxInstances, "the check command shows everything");

        File.WriteAllText(_file, tiers.Replace("\"inventory\"", "\"inventory\", \"restart\"", StringComparison.Ordinal));
        source.Refresh().Version.Should().BeGreaterThan(snapshot.Version, "a change is noticed although the short report looks the same");
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
