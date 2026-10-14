using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The PowerShell runner: parameter values reach the script as data (the command line
/// is the same whatever they hold, and they arrive intact on standard input), the
/// result is read back from between its markers, the right PowerShell is picked per
/// tier, and a child that runs too long is killed with everything it started. A real
/// round trip runs when <c>pwsh</c> is installed and is skipped otherwise. See
/// <c>.design/on-prem-gateway.md</c>, "Running the cmdlets".
/// </summary>
public sealed class PowerShellRunnerTests
{
    private static readonly PowerShellExecutables Programs = new(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", @"C:\Program Files\PowerShell\7\pwsh.exe");

    private static readonly PowerShellOperation Echo = new("echo", "Write-GatewayLog ('got ' + $Parameters.name)\n$Parameters", NeedsAdminModule: false);

    /// <summary>Values that would break out of a string if they were ever put into script text.</summary>
    public static TheoryData<string> Hostile => new()
    {
        "O'Brien \"CRONUS\" A/S",
        "x'; Remove-Item C:\\ -Recurse; '",
        "$(Stop-Computer -Force)",
        "`$env:PATH` and `n backticks",
        "line one\nline two\r\n'; exit 0; '",
        "@{ a = 1 } | & { param($x) $x }",
        "\u00e6\u00f8\u00e5 \u20ac \U0001F600",
    };

    private sealed class CapturingRunner : IProcessRunner
    {
        public List<ProcessSpec> Specs { get; } = [];

        public Func<ProcessSpec, ProcessOutcome> Answer { get; set; } = _ => new ProcessOutcome(0, string.Empty, string.Empty, false);

        public Task<ProcessOutcome> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Specs.Add(spec);
            var outcome = Answer(spec);
            foreach (var line in outcome.StandardOutput.Split('\n')) spec.OnOutputLine?.Invoke(line);
            return Task.FromResult(outcome);
        }
    }

    private static JsonObject Envelope(ProcessSpec spec) =>
        JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(spec.StandardInput!)))!.AsObject();

    private static string Script(ProcessSpec spec) =>
        Encoding.Unicode.GetString(Convert.FromBase64String(spec.Arguments[^1]));

    // ---------------------------------------------------------------- parameters are data

    [Theory]
    [MemberData(nameof(Hostile))]
    public void A_parameter_value_never_reaches_the_command_line_and_arrives_intact_on_standard_input(string value)
    {
        var runner = new PowerShellRunner(new CapturingRunner(), Programs);
        var target = new PowerShellTarget(PowerShellEdition.WindowsPowerShell, @"C:\Program Files\Microsoft Dynamics 365 Business Central\140\Service");

        var plain = runner.BuildSpec(PowerShellOperations.SelfTest, target, new JsonObject { ["instance"] = "BC140" }, TimeSpan.FromMinutes(1), "M");
        var hostile = runner.BuildSpec(PowerShellOperations.SelfTest, target, new JsonObject { ["instance"] = value, ["nested"] = new JsonArray(value) }, TimeSpan.FromMinutes(1), "M");

        hostile.Arguments.Should().Equal(plain.Arguments, "the command line is built from fixed text only");
        hostile.FileName.Should().Be(Programs.WindowsPowerShell);
        Script(hostile).Should().NotContain(value);
        string.Join(" ", hostile.Arguments).Should().NotContain(value);

        var envelope = Envelope(hostile);
        envelope["parameters"]!["instance"]!.GetValue<string>().Should().Be(value);
        envelope["parameters"]!["nested"]![0]!.GetValue<string>().Should().Be(value);
        envelope["moduleFolder"]!.GetValue<string>().Should().Be(target.ServiceFolder);
        hostile.StandardInput.Should().MatchRegex("^[A-Za-z0-9+/=]+$", "base64 survives any console code page");
    }

    [Fact]
    public void The_child_is_started_with_fixed_switches_and_the_script_reads_its_input_with_ConvertFrom_Json()
    {
        var spec = new PowerShellRunner(new CapturingRunner(), Programs)
            .BuildSpec(Echo, new PowerShellTarget(PowerShellEdition.PowerShell7, null), new JsonObject(), TimeSpan.FromSeconds(5), "M");

        spec.FileName.Should().Be(Programs.PowerShell7);
        spec.Arguments.Take(6).Should().Equal("-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand");
        var script = Script(spec);
        script.Should().Contain("[Console]::In.ReadToEnd()").And.Contain("ConvertFrom-Json").And.Contain(Echo.Script);
        Envelope(spec)["moduleFolder"].Should().BeNull("an operation that needs no administration module loads none");
    }

    [Fact]
    public void An_operation_that_needs_the_module_refuses_to_run_without_the_tiers_folder()
    {
        var act = () => new PowerShellRunner(new CapturingRunner(), Programs)
            .BuildSpec(PowerShellOperations.SelfTest, new PowerShellTarget(PowerShellEdition.WindowsPowerShell, null), new JsonObject(), TimeSpan.FromSeconds(5), "M");

        act.Should().Throw<ArgumentException>();
    }

    // ---------------------------------------------------------------- reading the result

    [Fact]
    public async Task The_result_comes_from_between_the_markers_and_every_other_line_is_log()
    {
        var processes = new CapturingRunner();
        processes.Answer = spec =>
        {
            var marker = Envelope(spec)["marker"]!.GetValue<string>();
            var result = Convert.ToBase64String(Encoding.UTF8.GetBytes("[{\"state\":\"Running\",\"name\":\"\u00e6\u00f8\u00e5\"}]"));
            return new ProcessOutcome(0, $"Loading module\nALWB-RESULT-fake\n{marker}\n{result}\n{marker}\nafter\n", string.Empty, false);
        };
        var progress = new List<string>();

        var outcome = await new PowerShellRunner(processes, Programs)
            .RunAsync(Echo, new PowerShellTarget(PowerShellEdition.WindowsPowerShell, null), new JsonObject(), TimeSpan.FromMinutes(1), progress.Add, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        outcome.Output.Should().ContainSingle();
        outcome.Output[0]!["name"]!.GetValue<string>().Should().Be("\u00e6\u00f8\u00e5");
        outcome.Log.Should().Equal("Loading module", "ALWB-RESULT-fake", "after");
        progress.Where(l => l.Length > 0).Should().Equal("Loading module", "ALWB-RESULT-fake", "after");
        Envelope(processes.Specs.Single())["marker"]!.GetValue<string>().Should().MatchRegex("^ALWB-RESULT-[0-9a-f]{32}$", "a marker the script's own output cannot guess");
    }

    [Fact]
    public void A_failure_a_timeout_and_a_missing_result_are_each_reported()
    {
        var failed = PowerShellRunner.Interpret(new ProcessOutcome(1, "Loading\n", "Get-NAVServerInstance : The term is not recognized\n", false), "M");
        failed.Succeeded.Should().BeFalse();
        failed.Error.Should().Be("Get-NAVServerInstance : The term is not recognized");
        failed.Log.Should().Equal("Loading");

        PowerShellRunner.Interpret(new ProcessOutcome(-1, string.Empty, string.Empty, true), "M")
            .Should().BeEquivalentTo(new { Succeeded = false, TimedOut = true, Error = "PowerShell did not finish in time and was stopped." });
        PowerShellRunner.Interpret(new ProcessOutcome(0, "M\n", string.Empty, false), "M")
            .Error.Should().Be("PowerShell ended without returning a result.");
        PowerShellRunner.Interpret(new ProcessOutcome(0, "M\nnot base64!\nM\n", string.Empty, false), "M")
            .Error.Should().Be("PowerShell returned a result the gateway could not read.");
    }

    // ---------------------------------------------------------------- which PowerShell, which folder

    [Theory]
    [InlineData(null, null, PowerShellEdition.WindowsPowerShell)]
    [InlineData(null, "14.0.29530.0", PowerShellEdition.WindowsPowerShell)]
    [InlineData(null, "23.5.0.0", PowerShellEdition.WindowsPowerShell)]
    [InlineData(null, "24.0.0.0", PowerShellEdition.PowerShell7)]
    [InlineData(PowerShellEdition.WindowsPowerShell, "26.0.0.0", PowerShellEdition.WindowsPowerShell)]
    [InlineData(PowerShellEdition.PowerShell7, "14.0.0.0", PowerShellEdition.PowerShell7)]
    public void The_policy_choice_wins_and_otherwise_the_version_decides_with_5_1_when_unsure(
        PowerShellEdition? configured, string? version, PowerShellEdition expected)
    {
        PowerShellChoice.For(configured, version is null ? null : Version.Parse(version)).Should().Be(expected);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Microsoft Dynamics 365 Business Central\\140\\Service\\Microsoft.Dynamics.Nav.Server.exe\" $BC140",
        "C:\\Program Files\\Microsoft Dynamics 365 Business Central\\140\\Service")]
    [InlineData("C:\\BC\\260\\Service\\Microsoft.Dynamics.Nav.Server.EXE $BC260 config \"C:\\BC\\260\\Service\\x.config\"", "C:\\BC\\260\\Service")]
    [InlineData("", null)]
    [InlineData("\"unterminated", null)]
    public void The_install_folder_comes_from_the_services_image_path(string imagePath, string? folder)
    {
        ServiceTierInstall.FolderFromImagePath(imagePath).Should().Be(folder);
        ServiceTierInstall.ServiceName("BC140").Should().Be("MicrosoftDynamicsNavServer$BC140");
    }

    // ---------------------------------------------------------------- the real process runner

    [Fact]
    public async Task Arguments_and_standard_input_reach_a_real_child_unchanged()
    {
        if (!File.Exists("/bin/sh")) Assert.Skip("Needs /bin/sh.");
        const string nasty = "a \"quoted\" ; $(whoami) `tick` 'single'";

        var outcome = await new SystemProcessRunner().RunAsync(
            new ProcessSpec("/bin/sh", ["-c", "printf '%s|' \"$1\"; cat", "sh", nasty], "from stdin " + nasty, TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        outcome.ExitCode.Should().Be(0);
        outcome.StandardOutput.Should().Be(nasty + "|from stdin " + nasty + "\n");
    }

    [Fact]
    public async Task A_child_that_runs_past_its_timeout_is_killed_with_everything_it_started()
    {
        if (!File.Exists("/bin/sh") || !OperatingSystem.IsLinux()) Assert.Skip("Needs /bin/sh on Linux.");
        var pidFile = Path.Combine(Path.GetTempPath(), "aldt-gateway-" + Guid.NewGuid().ToString("N"));
        try
        {
            var watch = Stopwatch.StartNew();
            var outcome = await new SystemProcessRunner().RunAsync(
                new ProcessSpec("/bin/sh", ["-c", "sleep 60 & echo $! > \"$1\"; wait", "sh", pidFile], null, TimeSpan.FromSeconds(1)),
                CancellationToken.None);

            outcome.TimedOut.Should().BeTrue();
            outcome.ExitCode.Should().Be(-1);
            watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
            var grandchild = int.Parse((await File.ReadAllTextAsync(pidFile)).Trim());
            await WaitUntilGoneAsync(grandchild);
        }
        finally
        {
            File.Delete(pidFile);
        }
    }

    [Fact]
    public async Task Cancelling_kills_the_child_and_throws()
    {
        if (!File.Exists("/bin/sh")) Assert.Skip("Needs /bin/sh.");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var act = () => new SystemProcessRunner().RunAsync(new ProcessSpec("/bin/sh", ["-c", "sleep 60"], null, TimeSpan.FromMinutes(5)), cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task WaitUntilGoneAsync(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            // A killed child stays a zombie until its parent reaps it; either is gone for our purpose.
            var stat = $"/proc/{pid}/stat";
            if (!File.Exists(stat)) return;
            var state = (await File.ReadAllTextAsync(stat)).Split(") ").LastOrDefault()?.FirstOrDefault();
            if (state is 'Z' or 'X') return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException($"Process {pid} is still running after its parent was killed.");
    }

    // ---------------------------------------------------------------- a real PowerShell, when there is one

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task With_pwsh_installed_a_hostile_value_makes_the_round_trip_as_data(string value)
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null) Assert.Skip("PowerShell 7 (pwsh) is not installed here.");
        var runner = new PowerShellRunner(new SystemProcessRunner(), new PowerShellExecutables(pwsh, pwsh));
        var progress = new List<string>();

        var outcome = await runner.RunAsync(Echo, new PowerShellTarget(PowerShellEdition.PowerShell7, null),
            new JsonObject { ["name"] = value }, TimeSpan.FromMinutes(1), progress.Add, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue(outcome.Error);
        outcome.Output.Single()!["name"]!.GetValue<string>().Should().Be(value);
        outcome.Log.Should().Contain(l => l.StartsWith("got ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_pwsh_installed_the_self_test_loads_the_admin_tool_from_the_tiers_own_folder()
    {
        var pwsh = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (pwsh is null) Assert.Skip("PowerShell 7 (pwsh) is not installed here.");
        using var tier = new ScratchFolder();
        using var empty = new ScratchFolder();
        // Stands in for the tier's own NavAdminTool.ps1, which defines the cmdlets.
        await File.WriteAllTextAsync(Path.Combine(tier.Path, "NavAdminTool.ps1"), """
            Write-Host 'Business Central Administration Shell'
            function Get-NAVServerInstance([string]$ServerInstance) {
                [pscustomobject]@{ ServerInstance = 'MicrosoftDynamicsNavServer$' + $ServerInstance; State = 'Running'; Version = '14.0.29530.0' }
            }
            """);
        var runner = new PowerShellRunner(new SystemProcessRunner(), new PowerShellExecutables(pwsh, pwsh));

        var loaded = await runner.RunAsync(PowerShellOperations.SelfTest, new PowerShellTarget(PowerShellEdition.PowerShell7, tier.Path),
            new JsonObject { ["instance"] = "BC140" }, TimeSpan.FromMinutes(1), null, CancellationToken.None);
        var missing = await runner.RunAsync(PowerShellOperations.SelfTest, new PowerShellTarget(PowerShellEdition.PowerShell7, empty.Path),
            new JsonObject { ["instance"] = "BC140" }, TimeSpan.FromMinutes(1), null, CancellationToken.None);

        loaded.Succeeded.Should().BeTrue(loaded.Error);
        loaded.Output.Single()!["serverInstance"]!.GetValue<string>().Should().Be("MicrosoftDynamicsNavServer$BC140");
        loaded.Output.Single()!["state"]!.GetValue<string>().Should().Be("Running");
        missing.Succeeded.Should().BeFalse();
        missing.Error.Should().Contain("No Business Central administration module was found in " + empty.Path);
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder, program))
            .FirstOrDefault(File.Exists);
}
