using System.Security.Cryptography;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Licence;
using ALWorkbench.Gateway.Core.Policy;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The <c>import-licence</c> verb (#1369) against the fake Business Central behind the fake
/// process runner: the order (import, restart, read again), what stops it before anything
/// runs, and what a failure at each step leaves behind. See <c>.design/on-prem-gateway.md</c>,
/// "Licences".
/// </summary>
public sealed class ImportLicenceVerbTests : IDisposable
{
    private readonly ScratchFolder _scratch = new();
    private readonly FakeBusinessCentral _server = new();
    private readonly List<string> _progress = [];

    public void Dispose() => _scratch.Dispose();

    /// <summary>A BC 14 database with two tiers: CRONUS-BC01 (this computer) publishes and imports, CRONUS-BC02 restarts with it.</summary>
    private static string Policy(
        string target = "NavDatabase",
        string bc01Allow = """["inventory", "publish", "restart", "licence"]""",
        string bc02Allow = """["inventory", "restart"]""",
        string tenants = "") => $"""
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC140"
        environment = "production"
        allow = {bc01Allow}
        {tenants}

        [[instance]]
        machine = "CRONUS-BC02"
        name = "BC140"
        environment = "production"
        allow = {bc02Allow}

        [[database]]
        name = "CRONUS-SQL/BC140"
        publish_on = {"{"} machine = "CRONUS-BC01", instance = "BC140" {"}"}
        restart = [
          {"{"} machine = "CRONUS-BC01", instance = "BC140" {"}"},
          {"{"} machine = "CRONUS-BC02", instance = "BC140" {"}"},
        ]
        {(target.Length == 0 ? string.Empty : $"licence_target = \"{target}\"")}

        [[machine]]
        name = "CRONUS-BC02"
        method = "service-manager"
        """;

    private TextPolicy _policy = new(Policy());

    /// <summary>The tiers read again after the restart, in order, through the loop's refresh.</summary>
    private readonly List<string> _refreshed = [];

    private string? _refreshProblem;

    private ImportLicenceVerb Verb(Version? version = null) => new(
        _policy,
        _server.Runner(),
        new FixedTierLocator(@"C:\Program Files\Microsoft Dynamics 365 Business Central\140\Service", version ?? new Version(14, 0, 47486, 0)),
        "CRONUS-BC01",
        _server.Restarts("CRONUS-BC01"));

    private async Task<GatewayCommandOutcome> ImportAsync(
        string fileName = "CRONUS.flf", string asked = "NavDatabase", string? tenant = null, Version? version = null,
        byte[]? content = null, string? sha = null, string machine = "CRONUS-BC01")
    {
        var bytes = content ?? "CRONUS licence"u8.ToArray();
        var path = Path.Combine(_scratch.Path, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        var command = new GatewayCommandMessage
        {
            Id = 1369,
            Verb = GatewayVerb.ImportLicence,
            Parameters = new GatewayImportLicenceParameters { Machine = machine, Instance = "BC140", Tenant = tenant, LicenceTarget = asked }.ToParameters(),
            SecondsLeft = 900,
            Files = [new GatewayCommandFileMessage { Id = 9, Name = fileName, Sha256 = sha ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length }],
        };
        return await Verb(version).ExecuteAsync(new GatewayCommandContext(command, new Dictionary<long, string> { [9] = path }, _progress.Add, RefreshAsync), CancellationToken.None);
    }

    private Task<string?> RefreshAsync(TierRef tier, CancellationToken ct)
    {
        _server.Timeline.Add($"refresh {tier}");
        _refreshed.Add(tier.ToString());
        return Task.FromResult(_refreshProblem);
    }

    private static GatewayImportLicenceResult Result(GatewayCommandOutcome outcome) =>
        GatewayImportLicenceResult.FromJson(outcome.Result) ?? throw new InvalidOperationException("No import-licence result.");

    // ---------------------------------------------------------------- order

    [Fact]
    public async Task It_imports_then_restarts_the_group_then_reads_the_tiers_again()
    {
        var outcome = await ImportAsync();

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.Should().Equal(
            "import-licence NavDatabase default CRONUS.flf",
            "stop CRONUS-BC01 / BC140 local",
            "stop CRONUS-BC02 / BC140 service-manager",
            "start CRONUS-BC01 / BC140 local",
            "start CRONUS-BC02 / BC140 service-manager",
            "refresh CRONUS-BC01 / BC140",
            "refresh CRONUS-BC02 / BC140");
        var result = Result(outcome);
        result.Target.Should().Be("NavDatabase");
        result.Imported.Should().BeTrue();
        result.Restart!.Restarted.Should().BeTrue();
        result.Refreshed.Should().BeTrue();
        _progress.Should().Contain("Restarted CRONUS-BC01 / BC140.").And.Contain("Read the service tiers again, so AL Workbench shows the new licence.");
    }

    [Fact]
    public async Task The_import_runs_on_the_publishing_tier_with_the_file_and_database_as_data()
    {
        await ImportAsync();

        var (_, parameters) = _server.Runs.Single(r => r.Operation == "import-licence");
        parameters["instance"]!.GetValue<string>().Should().Be("BC140");
        parameters["database"]!.GetValue<string>().Should().Be("NavDatabase");
        parameters["path"]!.GetValue<string>().Should().EndWith("CRONUS.flf");
        _server.Programs.First().Should().Be("powershell.exe", "BC 14's module needs Windows PowerShell 5.1");
    }

    [Fact]
    public async Task A_tenant_licence_names_the_tenant()
    {
        _policy = new TextPolicy(Policy(target: "Tenant", tenants: """tenants = ["default", "cronus-dk"]"""));

        var outcome = await ImportAsync(asked: "Tenant", tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Timeline.First().Should().Be("import-licence Tenant cronus-dk CRONUS.flf");
        _progress.Should().Contain(l => l.Contains("tenant 'cronus-dk'"));
    }

    [Fact]
    public async Task A_bclicense_is_imported_on_business_central_22_and_later()
    {
        var outcome = await ImportAsync(fileName: "CRONUS.bclicense", version: new Version(25, 4, 29661, 29927));

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        _server.Programs.First().Should().Be("pwsh.exe", "BC 24 and later load in PowerShell 7");
    }

    // ---------------------------------------------------------------- refused before anything runs

    [Fact]
    public async Task A_database_without_a_licence_target_is_refused_and_nothing_runs()
    {
        _policy = new TextPolicy(Policy(target: string.Empty));

        var outcome = await ImportAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("licence_target").And.EndWith("Nothing was changed.");
        _server.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task A_publishing_tier_without_licence_in_its_allow_list_is_refused()
    {
        _policy = new TextPolicy(Policy(bc01Allow: """["inventory", "publish", "restart"]"""));

        var outcome = await ImportAsync();

        outcome.FailureMessage.Should().Contain("does not allow 'licence'").And.EndWith("Nothing was changed.");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_group_tier_without_restart_is_refused_before_the_import()
    {
        _policy = new TextPolicy(Policy(bc02Allow: """["inventory"]"""));

        var outcome = await ImportAsync();

        outcome.FailureMessage.Should().Contain("CRONUS-BC02 / BC140 does not allow 'restart'");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_target_other_than_the_one_confirmed_in_AL_Workbench_is_refused()
    {
        _policy = new TextPolicy(Policy(target: "Master"));

        var outcome = await ImportAsync(asked: "NavDatabase");

        outcome.FailureMessage.Should().Contain("now import licences into the master database").And.EndWith("Nothing was changed.");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_sent_to_a_tier_that_does_not_publish_for_the_database_is_refused()
    {
        _policy = new TextPolicy(Policy(bc02Allow: """["inventory", "restart", "licence"]"""));

        var outcome = await ImportAsync(machine: "CRONUS-BC02");

        outcome.FailureMessage.Should().Contain("Licences for the database").And.Contain("go through CRONUS-BC01 / BC140");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task An_flf_for_business_central_22_or_later_is_refused_naming_both_formats()
    {
        var outcome = await ImportAsync(fileName: "CRONUS.flf", version: new Version(23, 0, 12034, 0));

        outcome.FailureMessage.Should().Contain(".flf").And.Contain(".bclicense").And.Contain("Business Central 23");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bclicense_for_business_central_14_is_refused()
    {
        var outcome = await ImportAsync(fileName: "CRONUS.bclicense");

        outcome.FailureMessage.Should().Contain("Business Central 14").And.Contain("imports .flf licences");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_whose_hash_does_not_match_is_refused()
    {
        var outcome = await ImportAsync(sha: new string('0', 64));

        outcome.FailureMessage.Should().Contain("SHA-256 does not match");
        _server.Runs.Should().BeEmpty();
    }

    [Fact]
    public async Task A_file_over_the_size_limit_is_refused()
    {
        var outcome = await ImportAsync(content: new byte[GatewayLicenceFormats.MaxBytes + 1]);

        outcome.FailureMessage.Should().Contain("larger than 1 MB");
        _server.Runs.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- failures part-way

    [Fact]
    public async Task A_failed_import_restarts_nothing_and_reads_nothing_again()
    {
        _server.LicenceImportError = "The license file is not valid.";

        var outcome = await ImportAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("The license file is not valid.").And.Contain("Nothing was restarted");
        _server.Timeline.Should().BeEmpty();
        _refreshed.Should().BeEmpty();
        var result = Result(outcome);
        result.Imported.Should().BeFalse();
        result.Restart.Should().BeNull();
    }

    [Fact]
    public async Task PowerShell_failing_before_it_reports_counts_as_not_imported()
    {
        _server.LicenceImportCrashes = true;

        var outcome = await ImportAsync();

        outcome.FailureMessage.Should().Contain("PowerShell could not load the module.");
        _server.Timeline.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tier_that_does_not_come_back_fails_the_command_with_the_licence_imported_and_the_tiers_still_read()
    {
        _server.NeverStarts.Add("CRONUS-BC02 / BC140");

        var outcome = await ImportAsync();

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().StartWith("The licence was imported, but the service tiers were not all restarted");
        var result = Result(outcome);
        result.Imported.Should().BeTrue();
        result.Restart!.Restarted.Should().BeFalse();
        _refreshed.Should().Equal("CRONUS-BC01 / BC140", "CRONUS-BC02 / BC140");
    }

    [Fact]
    public async Task Settings_that_stop_allowing_restart_during_the_import_leave_the_tiers_running()
    {
        _server.AfterLicenceImport = () => _policy.InForce = new TextPolicy(Policy(bc02Allow: """["inventory"]""")).InForce;

        var outcome = await ImportAsync();

        outcome.FailureMessage.Should().Contain("The licence was imported, but the customer's gateway settings no longer allow 'restart' on CRONUS-BC02 / BC140");
        _server.Timeline.Should().Equal(
            "import-licence NavDatabase default CRONUS.flf",
            "refresh CRONUS-BC01 / BC140",
            "refresh CRONUS-BC02 / BC140");
    }

    [Fact]
    public async Task A_reading_that_does_not_go_through_is_reported_without_failing_the_import()
    {
        _refreshProblem = "AL Workbench could not be reached.";

        var outcome = await ImportAsync();

        outcome.Succeeded.Should().BeTrue();
        var result = Result(outcome);
        result.Refreshed.Should().BeFalse();
        result.RefreshMessage.Should().Be("AL Workbench could not be reached.");
        _progress.Should().Contain(l => l.StartsWith("Could not read CRONUS-BC01 / BC140 again", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_runner_policy_check_refuses_an_import_into_a_tenant_the_tier_may_not_touch()
    {
        _policy = new TextPolicy(Policy(target: "Tenant"));

        var outcome = await ImportAsync(asked: "Tenant", tenant: "cronus-se");

        outcome.FailureMessage.Should().Contain("may not act on tenant 'cronus-se'");
        _server.Runs.Should().BeEmpty();
    }
}
