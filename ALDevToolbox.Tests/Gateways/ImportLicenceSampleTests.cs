using System.Security.Cryptography;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core;
using ALWorkbench.Gateway.Core.Licence;
using ALWorkbench.Gateway.Core.PowerShell;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Gateways;

/// <summary>
/// The real <c>import-licence</c> script (#1369) run under <c>pwsh</c> against a stand-in
/// <c>Import-NAVServerLicense</c>, when <c>pwsh</c> is installed; skipped otherwise. The
/// stand-in is SYNTHETIC, written from the cmdlet's documented parameters: what a real
/// server takes is in <c>.design/on-prem-gateway.md</c>, "To verify on real servers". The
/// restart after it needs Windows' service manager, so that part is the fake's.
/// </summary>
public sealed class ImportLicenceSampleTests : IDisposable
{
    private const string Policy = """
        [[instance]]
        machine = "CRONUS-BC01"
        name = "BC250"
        environment = "test"
        allow = ["inventory", "restart", "licence"]
        tenants = ["default", "cronus-dk"]

        [[database]]
        name = "CRONUS-SQL/BC250"
        publish_on = { machine = "CRONUS-BC01", instance = "BC250" }
        licence_target = "TARGET"
        """;

    /// <summary>A current version's cmdlet: <c>-Database</c>, <c>-Tenant</c> and <c>-Force</c>.</summary>
    private const string Current = """
        $global:StubLog = Join-Path $PSScriptRoot 'calls.txt'
        function Import-NAVServerLicense {
            [CmdletBinding()]
            param([string]$ServerInstance, [string]$LicenseFile, [string]$Database, [string]$Tenant, [switch]$Force)
            Add-Content -LiteralPath $global:StubLog -Value ('Import-NAVServerLicense ServerInstance=' + $ServerInstance + ' Database=' + $Database + ' Tenant=' + $Tenant + ' Force=' + [bool]$Force + ' File=' + (Split-Path -Leaf $LicenseFile))
            if ((Get-Content -LiteralPath $LicenseFile -Raw) -like '*broken*') { throw 'The license file is not valid.' }
            'License imported.'
        }
        """;

    /// <summary>A version whose cmdlet has no <c>-Tenant</c> (to be checked for BC 14 on a real server).</summary>
    private const string WithoutTenant = """
        $global:StubLog = Join-Path $PSScriptRoot 'calls.txt'
        function Import-NAVServerLicense {
            [CmdletBinding()]
            param([string]$ServerInstance, [string]$LicenseFile, [string]$Database)
            Add-Content -LiteralPath $global:StubLog -Value ('Import-NAVServerLicense Database=' + $Database)
        }
        """;

    private readonly ScratchFolder _scratch = new();

    public void Dispose() => _scratch.Dispose();

    private static string? FindPwsh() => (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"))
        .FirstOrDefault(File.Exists);

    private async Task<(GatewayCommandOutcome Outcome, List<string> Calls, List<string> Progress)> RunAsync(
        string standIn, string target, string? tenant, string content = "CRONUS licence")
    {
        var pwsh = FindPwsh();
        if (pwsh is null) Assert.Skip("PowerShell 7 (pwsh) is not installed here.");
        var tier = Path.Combine(_scratch.Path, "tier");
        Directory.CreateDirectory(tier);
        await File.WriteAllTextAsync(Path.Combine(tier, "NavAdminTool.ps1"), standIn);
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var path = Path.Combine(_scratch.Path, "9-CRONUS.bclicense");
        await File.WriteAllBytesAsync(path, bytes);
        var command = new GatewayCommandMessage
        {
            Id = 1369,
            Verb = GatewayVerb.ImportLicence,
            Parameters = new GatewayImportLicenceParameters { Machine = "CRONUS-BC01", Instance = "BC250", Tenant = tenant, LicenceTarget = target }.ToParameters(),
            Files = [new GatewayCommandFileMessage { Id = 9, Name = "CRONUS.bclicense", Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Size = bytes.Length }],
        };
        var services = new FakeBusinessCentral();
        var verb = new ImportLicenceVerb(new TextPolicy(Policy.Replace("TARGET", target, StringComparison.Ordinal)),
            new PowerShellRunner(new SystemProcessRunner(), new PowerShellExecutables(pwsh, pwsh), new FakeFileAccess()),
            new FixedTierLocator(tier), "CRONUS-BC01", services.Restarts());
        var progress = new List<string>();

        var outcome = await verb.ExecuteAsync(new GatewayCommandContext(command, new Dictionary<long, string> { [9] = path }, progress.Add), CancellationToken.None);

        var log = Path.Combine(tier, "calls.txt");
        return (outcome, File.Exists(log) ? [.. await File.ReadAllLinesAsync(log)] : [], progress);
    }

    [Fact]
    public async Task With_pwsh_installed_the_script_imports_into_the_database_the_settings_name()
    {
        var (outcome, calls, progress) = await RunAsync(Current, "NavDatabase", tenant: null);

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        calls.Should().Equal("Import-NAVServerLicense ServerInstance=BC250 Database=NavDatabase Tenant= Force=True File=9-CRONUS.bclicense");
        progress.Should().Contain("Import-NAVServerLicense, database NavDatabase.").And.Contain("  License imported.");
    }

    [Fact]
    public async Task With_pwsh_installed_a_tenant_licence_is_imported_with_the_tenant()
    {
        var (outcome, calls, _) = await RunAsync(Current, "Tenant", tenant: "cronus-dk");

        outcome.Succeeded.Should().BeTrue(outcome.FailureMessage);
        calls.Should().Equal("Import-NAVServerLicense ServerInstance=BC250 Database=Tenant Tenant=cronus-dk Force=True File=9-CRONUS.bclicense");
    }

    [Fact]
    public async Task With_pwsh_installed_the_cmdlets_own_error_comes_back_and_nothing_restarts()
    {
        var (outcome, _, _) = await RunAsync(Current, "NavDatabase", tenant: null, content: "a broken licence");

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("The license file is not valid.").And.Contain("Nothing was restarted");
    }

    [Fact]
    public async Task With_pwsh_installed_a_tenant_licence_on_a_cmdlet_without_tenant_is_refused_rather_than_imported_server_wide()
    {
        var (outcome, calls, _) = await RunAsync(WithoutTenant, "Tenant", tenant: "cronus-dk");

        outcome.Succeeded.Should().BeFalse();
        outcome.FailureMessage.Should().Contain("cannot import into one tenant");
        calls.Should().BeEmpty();
    }
}
