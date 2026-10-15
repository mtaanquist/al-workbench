using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Policy;
using ALWorkbench.Gateway.Core.PowerShell;
using ALWorkbench.Gateway.Core.PublishInstall;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// <c>publish-install</c> against what Business Central 14 and a current version
/// return: the inspect script's output as the planner reads it (the samples in
/// <c>PublishInstallSamples/</c>, synthetic until replaced with captures), and the real
/// scripts run under <c>pwsh</c> against stand-in administration tools, when <c>pwsh</c>
/// is installed. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </summary>
public sealed class PublishInstallSampleTests : IDisposable
{
    private const string Tier = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC250"
        environment = "test"
        allow = ["inventory", "publish", "unpublish", "restart"]
        tenants = ["default", "cronus-dk", "cronus-se"]
        allow_unsigned = true
        """;

    private readonly ScratchFolder _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static AppInspection Sample(string file) => AppInspection.FromScriptOutput(
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gateways", "PublishInstallSamples", file))));

    private static EffectiveInstance TierOf(string toml) => new TextPolicy(toml).InForce.Instances[0];

    private static IEnumerable<string> Words(AppPlan plan) =>
        plan.Steps.Select(s => s.Run is null ? $"{s.Step}:skipped" : $"{s.Step}" + (s.Version is null ? string.Empty : $" {s.Version}"));

    // ---------------------------------------------------------------- the samples

    [Fact]
    public void Business_central_14_without_data_versions_upgrades_the_installed_version()
    {
        var inspection = Sample("bc14-inspect.json");

        inspection.TenantSpecificProperties.Should().BeFalse();
        inspection.Versions.Should().Equal(new AppVersionRow(new Version(1, 3, 0, 0), "Global", true, null));
        inspection.OtherTenants.Should().BeEmpty();
        inspection.Parameters["unpublishTenant"].Should().BeFalse();

        var plan = AppInstallPlanner.Plan(inspection, new Version(1, 4, 0, 0), TierOf(Tier), "default", GatewaySyncModes.Add, unpublishPrevious: true);

        plan.Outcome.Should().Be(GatewayPublishInstallOutcomes.Upgraded);
        plan.From.Should().Be(new Version(1, 3, 0, 0));
        Words(plan).Should().Equal("publish", "sync", "upgrade", "unpublish 1.3.0.0");
        plan.Steps[^1].Run!.TenantScoped.Should().BeFalse();
    }

    [Fact]
    public void A_current_multitenant_server_keeps_the_version_another_tenant_still_runs()
    {
        var plan = AppInstallPlanner.Plan(Sample("bc25-inspect-multitenant.json"), new Version(1, 4, 0, 0), TierOf(Tier), "cronus-dk", GatewaySyncModes.Add, unpublishPrevious: true);

        plan.Outcome.Should().Be(GatewayPublishInstallOutcomes.Upgraded);
        plan.From.Should().Be(new Version(1, 3, 0, 0));
        Words(plan).Should().Equal("publish", "sync", "upgrade", "unpublish 1.2.0.0", "unpublish:skipped");
        plan.Steps[^1].SkipReason.Should().Contain("tenant 'cronus-se' still has it installed");
        plan.Steps[0].Run!.Scope.Should().Be("Tenant");
    }

    [Fact]
    public void An_app_uninstalled_with_its_data_kept_is_upgraded_and_its_old_tenant_version_removed()
    {
        var inspection = Sample("bc25-inspect-uninstalled-with-data.json");

        inspection.Versions.Should().ContainSingle().Which.DataVersion.Should().Be(new Version(1, 3, 0, 0));
        var plan = AppInstallPlanner.Plan(inspection, new Version(1, 4, 0, 0), TierOf(Tier), "default", GatewaySyncModes.ForceSync, unpublishPrevious: true);

        plan.Outcome.Should().Be(GatewayPublishInstallOutcomes.Upgraded);
        Words(plan).Should().Equal("publish", "sync", "upgrade", "unpublish 1.3.0.0");
        plan.Steps[1].Run!.Mode.Should().Be("ForceSync");
        plan.Steps[^1].Run!.TenantScoped.Should().BeTrue();
    }

    [Fact]
    public void Data_kept_at_the_same_version_is_installed_again_rather_than_upgraded()
    {
        var inspection = new AppInspection(true, [new AppVersionRow(new Version(1, 4, 0, 0), "Tenant", false, new Version(1, 4, 0, 0))], null, null,
            new Dictionary<string, bool>());

        var plan = AppInstallPlanner.Plan(inspection, new Version(1, 4, 0, 0), TierOf(Tier), "default", GatewaySyncModes.Add, unpublishPrevious: false);

        plan.Outcome.Should().Be(GatewayPublishInstallOutcomes.Installed);
        Words(plan).Should().Equal("publish:skipped", "sync", "install");
    }

    [Fact]
    public void Data_kept_from_a_newer_version_is_refused()
    {
        var inspection = new AppInspection(true, [new AppVersionRow(new Version(1, 5, 0, 0), "Tenant", false, new Version(1, 5, 0, 0))], null, null,
            new Dictionary<string, bool>());

        var plan = AppInstallPlanner.Plan(inspection, new Version(1, 4, 0, 0), TierOf(Tier), "default", GatewaySyncModes.Add, unpublishPrevious: false);

        plan.Refusal.Should().Contain("keeps data from version 1.5.0.0");
        plan.Steps.Should().BeEmpty();
    }

    [Fact]
    public void Output_the_gateway_cannot_read_is_said_so()
    {
        var act = () => AppInspection.FromScriptOutput(JsonNode.Parse("[\"not an object\"]"));

        act.Should().Throw<FormatException>();
    }

    // ---------------------------------------------------------------- the real scripts, when pwsh is installed

    private static string? FindPwsh() => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"))
        .FirstOrDefault(File.Exists);

    /// <summary>
    /// Runs the verb with the real runner and scripts against the stand-in tool, for one
    /// app, and returns the outcome and the cmdlet calls the stand-in recorded.
    /// </summary>
    private async Task<(GatewayCommandOutcome Outcome, List<string> Calls, List<string> Progress)> RunRealAsync(
        string standIn, string toml, string? tenant, bool signed, string appName = "CRONUS Coffee", string? extraTool = null)
    {
        var pwsh = FindPwsh();
        if (pwsh is null) Assert.Skip("PowerShell 7 (pwsh) is not installed here.");
        var tier = Path.Combine(_scratch.Path, "tier");
        Directory.CreateDirectory(tier);
        var tool = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Gateways", "PublishInstallSamples", standIn));
        await File.WriteAllTextAsync(Path.Combine(tier, "NavAdminTool.ps1"), tool + Environment.NewLine + extraTool);

        var bytes = AppFiles.Build(AppFiles.CoffeeId, System.Security.SecurityElement.Escape(appName), "CRONUS Partner", "1.4.0.0", signed);
        var path = Path.Combine(_scratch.Path, "7-coffee.app");
        await File.WriteAllBytesAsync(path, bytes);
        var file = new GatewayCommandFileMessage { Id = 7, Name = "coffee.app", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length };
        var command = new GatewayCommandMessage
        {
            Id = 1375,
            Verb = GatewayVerb.PublishInstall,
            Parameters = new GatewayPublishInstallParameters
            {
                Machine = "CRONUS-BC01",
                Instance = "BC250",
                Tenant = tenant,
                Apps = [new GatewayPublishInstallApp { File = "coffee.app", Id = AppFiles.CoffeeId, Name = appName, Publisher = "CRONUS Partner", Version = "1.4.0.0", Signed = signed }],
            }.ToParameters(),
            Files = [file],
        };
        var verb = new PublishInstallVerb(new TextPolicy(toml),
            new PowerShellRunner(new SystemProcessRunner(), new PowerShellExecutables(pwsh, pwsh), new FakeFileAccess()),
            new FixedTierLocator(tier), "CRONUS-BC01");
        var progress = new List<string>();

        var outcome = await verb.ExecuteAsync(new GatewayCommandContext(command, new Dictionary<long, string> { [7] = path }, progress.Add), CancellationToken.None);

        var log = Path.Combine(tier, "calls.txt");
        return (outcome, File.Exists(log) ? [.. await File.ReadAllLinesAsync(log)] : [], progress);
    }

    [Fact]
    public async Task With_pwsh_installed_the_scripts_upgrade_one_tenant_of_a_current_multitenant_tier()
    {
        var (outcome, calls, progress) = await RunRealAsync("bc25-NavAdminTool.ps1", Tier, "cronus-dk", signed: false);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        calls.Should().Equal(
            "Get-NAVAppInfo Tenant=cronus-dk TenantSpecificProperties=True",
            "Get-NAVTenant",
            "Get-NAVAppInfo Tenant=cronus-se TenantSpecificProperties=False",
            "Publish-NAVApp Scope=Tenant Tenant=cronus-dk SkipVerification=True File=7-coffee.app",
            "Sync-NAVApp Name=CRONUS Coffee Publisher=CRONUS Partner Version=1.4.0.0 Tenant=cronus-dk Mode=Add",
            "Start-NAVAppDataUpgrade Name=CRONUS Coffee Version=1.4.0.0 Tenant=cronus-dk",
            "Unpublish-NAVApp Name=CRONUS Coffee Version=1.2.0.0 Tenant=");
        var app = GatewayPublishInstallResult.FromJson(outcome.Result)!.Apps.Single();
        app.Outcome.Should().Be(GatewayPublishInstallOutcomes.Upgraded);
        app.FromVersion.Should().Be("1.3.0.0");
        app.Steps.Last().Message.Should().Contain("tenant 'cronus-se' still has it installed");
        progress.Should().Contain("  Upgrading CRONUS Coffee from 1.3.0.0 to 1.4.0.0.", "what a cmdlet prints goes to the command's log");
    }

    [Fact]
    public async Task With_pwsh_installed_the_scripts_check_the_signature_and_leave_out_what_business_central_14_lacks()
    {
        var signedOnly = Tier.Replace("allow_unsigned = true", "allow_unsigned = false", StringComparison.Ordinal);

        var (outcome, calls, _) = await RunRealAsync("bc14-NavAdminTool.ps1", signedOnly, tenant: null, signed: true);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        calls.Should().Equal(
            "Get-NAVAppInfo Tenant=default",
            "Get-NAVTenant",
            "Get-AuthenticodeSignature File=7-coffee.app",
            "Publish-NAVApp Scope=Tenant Tenant=default SkipVerification=False File=7-coffee.app",
            "Sync-NAVApp Name=CRONUS Coffee Publisher=CRONUS Partner Version=1.4.0.0 Tenant=default Mode=Add",
            "Start-NAVAppDataUpgrade Name=CRONUS Coffee Version=1.4.0.0 Tenant=default",
            "Unpublish-NAVApp Name=CRONUS Coffee Version=1.3.0.0");
    }

    [Fact]
    public async Task With_pwsh_installed_a_signature_that_cannot_be_checked_refuses_to_publish()
    {
        // Off Windows there is no Get-AuthenticodeSignature: the check fails closed.
        var signedOnly = Tier.Replace("allow_unsigned = true", "allow_unsigned = false", StringComparison.Ordinal);

        var (outcome, calls, _) = await RunRealAsync("bc25-NavAdminTool.ps1", signedOnly, "cronus-dk", signed: true,
            extraTool: OperatingSystem.IsWindows() ? "function Get-AuthenticodeSignature([string]$LiteralPath) { [pscustomobject]@{ Status = 'NotSupportedFileFormat'; StatusMessage = 'Unknown file type.' } }" : null);

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("the signature check failed");
        calls.Should().NotContain(c => c.StartsWith("Publish-NAVApp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_pwsh_installed_a_failing_cmdlet_stops_the_app_at_that_step()
    {
        var (outcome, calls, _) = await RunRealAsync("bc25-NavAdminTool.ps1", Tier, "cronus-dk", signed: false,
            extraTool: "function Sync-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant, [string]$Mode) { throw 'The table CRONUS Coffee Setup has a destructive change.' }");

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Be("CRONUS Coffee 1.4.0.0: synchronising failed. The table CRONUS Coffee Setup has a destructive change.");
        calls.Should().NotContain(c => c.StartsWith("Start-NAVAppDataUpgrade", StringComparison.Ordinal));
        var steps = GatewayPublishInstallResult.FromJson(outcome.Result)!.Apps.Single().Steps.Select(s => $"{s.Step}:{s.Outcome}");
        steps.Should().Equal("verify:done", "publish:done", "sync:failed");
    }

    [Fact]
    public async Task With_pwsh_installed_an_app_name_full_of_script_reaches_the_cmdlets_as_text()
    {
        const string hostile = "CRONUS '; Write-Call pwned; $(Write-Call pwned2) `\" Coffee";

        var (outcome, calls, _) = await RunRealAsync("bc25-NavAdminTool.ps1", Tier, "cronus-dk", signed: false,
            appName: hostile);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        calls.Should().Contain($"Sync-NAVApp Name={hostile} Publisher=CRONUS Partner Version=1.4.0.0 Tenant=cronus-dk Mode=Add");
        calls.Should().NotContain(c => c.StartsWith("pwned", StringComparison.Ordinal));
    }
}
