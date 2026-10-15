using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.Licence;

/// <summary>
/// The fixed piece of PowerShell <c>import-licence</c> runs (#1369): one
/// <c>Import-NAVServerLicense</c> on the publishing tier, from the tier's own administration
/// module. Like the other scripts it is a constant in the gateway; the command reaches it
/// only as data in <c>$Parameters</c> (the instance, the file's path, the database and the
/// tenant), and the database is one of four fixed words checked before the script runs.
/// See <c>.design/on-prem-gateway.md</c>, "Licences".
///
/// <para>Optional parameters are looked up with <c>Get-Command</c> first, as the other
/// scripts do: <c>-Tenant</c> (a tenant import refuses without it rather than importing
/// into the whole server) and <c>-Force</c> (passed where the cmdlet has it, so it never
/// stops to ask under <c>-NonInteractive</c>). The cmdlet's own failure comes back as
/// <c>{ ok: false, error }</c>; anything that stops the script is PowerShell's error.</para>
/// </summary>
public static class ImportLicenceScript
{
    /// <summary>Importing writes a few kilobytes into the database; ten minutes is far beyond that.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    /// <summary>The parameters of <see cref="Import"/>.</summary>
    public static JsonObject Parameters(string instance, string path, string database, string tenant) => new()
    {
        [GatewayCommandParameters.Instance] = instance,
        ["path"] = path,
        ["database"] = database,
        [GatewayCommandParameters.Tenant] = tenant,
    };

    /// <summary>Returns one object: <c>{ ok, error }</c>.</summary>
    public static readonly PowerShellOperation Import = new("import-licence", """
        $instance = [string]$Parameters.instance
        $path = [string]$Parameters.path
        $database = [string]$Parameters.database
        $tenant = [string]$Parameters.tenant

        function Get-Reason($record) {
            $e = $record.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            $text = ([string]$e.Message).Trim()
            if ($text.Length -gt 900) { $text = $text.Substring(0, 900) + '...' }
            if ($text.Length -eq 0) { $text = 'It failed without saying why.' }
            return $text
        }

        $result = [ordered]@{ ok = $false; error = $null }
        try {
            $command = Get-Command -Name 'Import-NAVServerLicense' -ErrorAction SilentlyContinue
            if ($null -eq $command) { throw 'This Business Central version has no Import-NAVServerLicense, so the gateway cannot import a licence on it.' }
            if (-not $command.Parameters.ContainsKey('Database')) { throw 'Import-NAVServerLicense on this Business Central version does not say which database to import into, so the gateway did not run it.' }
            $arguments = @{ ServerInstance = $instance; LicenseFile = $path; Database = $database }
            if ($database -eq 'Tenant') {
                if (-not $command.Parameters.ContainsKey('Tenant')) {
                    throw ('Import-NAVServerLicense on this Business Central version cannot import into one tenant, so the gateway did not run it for tenant ' + $tenant + '.')
                }
                $arguments['Tenant'] = $tenant
            }
            if ($command.Parameters.ContainsKey('Force')) { $arguments['Force'] = $true }
            Write-GatewayLog ('Import-NAVServerLicense, database ' + $database + $(if ($database -eq 'Tenant') { ', tenant ' + $tenant } else { '' }) + '.')
            $output = Import-NAVServerLicense @arguments -ErrorAction Stop
            foreach ($line in @(($output | Out-String) -split "`r?`n")) {
                if ($line.Trim().Length -gt 0) { Write-GatewayLog ('  ' + $line.TrimEnd()) }
            }
            $result.ok = $true
        } catch {
            $result.error = Get-Reason $_
        }
        [pscustomobject]$result
        """);
}
