using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.Inventory;

/// <summary>
/// The fixed PowerShell that reads one service tier for the inventory, and the
/// parameters it takes. Read only: it calls <c>Get-</c> and <c>Export-</c> cmdlets
/// and nothing else. See <c>.design/on-prem-gateway.md</c>, "Inventory, as built".
///
/// <para><b>One part at a time, each with its own time limit.</b> The instance, its
/// Windows service, its configuration, its tenants, each tenant's apps, its sessions
/// and its licence are each read by their own call, run in a second runspace inside
/// the same process with the administration module loaded there too. A call that
/// does not answer in time is stopped and that runspace abandoned (the next part gets
/// a fresh one), and the part is reported as unavailable with the reason; the rest
/// of the tier is still read. The whole read also has a budget, after which the
/// parts not reached yet are reported as skipped, so a server with many tenants
/// still reports something within the runner's own time-out.</para>
///
/// <para><b>Older servers.</b> Before calling a cmdlet, or passing it a parameter
/// that not every version has (<c>-AsXml</c>, <c>-TenantSpecificProperties</c>,
/// <c>-Tenant</c> on <c>Get-NAVServerSession</c>), the script asks
/// <c>Get-Command</c> whether it exists, and reports the part (or the missing
/// properties) as unavailable when it does not. Nothing here assumes a version.</para>
///
/// <para><b>The output is already normalised</b>: one object with lower-case names,
/// plain strings for every value the cmdlets returned, and the configuration reduced
/// to a fixed list of keys (where the database is, the ports, whether it is
/// multitenant) so no secret in the configuration ever leaves the server.
/// <see cref="InventoryNormaliser"/> reads exactly this shape, and
/// <c>ALWorkbench.Gateway check --inventory</c> prints it, which is how real samples
/// are captured. Every difference between Business Central versions is dealt with
/// here, in the script, and nowhere else.</para>
/// </summary>
public static class InventoryScript
{
    /// <summary>The shape of the object the script returns. Bumped when it changes.</summary>
    public const int OutputSchema = 1;

    /// <summary>A part that does not answer within this is stopped and reported as unavailable.</summary>
    public static readonly TimeSpan SectionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>One tenant's apps: <c>Get-NAVAppInfo</c> can be slow on a large tenant.</summary>
    public static readonly TimeSpan AppsTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Loading the administration module into the second runspace.</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(3);

    /// <summary>After this the parts not reached yet are skipped.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(20);

    /// <summary>The runner's own limit for the whole process: the budget plus room to finish the part in hand and load the module.</summary>
    public static readonly TimeSpan ProcessTimeout = Budget + AppsTimeout + LoadTimeout + LoadTimeout;

    /// <summary>The parameters for one tier. Data only; the script reads them with <c>ConvertFrom-Json</c>.</summary>
    public static JsonObject Parameters(string instance, string moduleFolder, bool includeSessionUsers) => new()
    {
        [GatewayCommandParameters.Instance] = instance,
        ["moduleFolder"] = moduleFolder,
        ["includeSessionUsers"] = includeSessionUsers,
        ["sectionSeconds"] = (int)SectionTimeout.TotalSeconds,
        ["appsSeconds"] = (int)AppsTimeout.TotalSeconds,
        ["loadSeconds"] = (int)LoadTimeout.TotalSeconds,
        ["budgetSeconds"] = (int)Budget.TotalSeconds,
        ["maxTenants"] = GatewayInventoryLimits.MaxTenantsPerInstance,
        ["maxLicenceChars"] = GatewayInventoryLimits.MaxLicenceTextChars,
    };

    /// <summary>The operation the runner runs, once per tier.</summary>
    public static readonly PowerShellOperation Operation = new("inventory", """
        $name = [string]$Parameters.instance
        $folder = [string]$Parameters.moduleFolder
        $includeUsers = [bool]$Parameters.includeSessionUsers
        $sectionSeconds = [int]$Parameters.sectionSeconds
        $appsSeconds = [int]$Parameters.appsSeconds
        $loadSeconds = [int]$Parameters.loadSeconds
        $maxTenants = [int]$Parameters.maxTenants
        $maxLicenceChars = [int]$Parameters.maxLicenceChars
        $deadline = [DateTime]::UtcNow.AddSeconds([int]$Parameters.budgetSeconds)
        $unavailable = [ordered]@{}
        $script:sectionRunspace = $null

        function Get-Reason($record) {
            $e = $record.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            $text = ([string]$e.Message).Trim()
            if ($text.Length -gt 900) { $text = $text.Substring(0, 900) + '...' }
            if ($text.Length -eq 0) { $text = 'It failed without saying why.' }
            return $text
        }

        function Test-Cmdlet([string]$Command, [string]$Parameter) {
            $found = Get-Command -Name $Command -ErrorAction SilentlyContinue
            if ($null -eq $found) { return $false }
            if ($Parameter) { return $found.Parameters.ContainsKey($Parameter) }
            return $true
        }

        # Loads the tier's administration module in a fresh runspace, as the bootstrap
        # did in this one, so a part that hangs can be stopped without losing the rest.
        $loader = {
            param($Folder)
            $ErrorActionPreference = 'Stop'
            $tool = Join-Path $Folder 'NavAdminTool.ps1'
            if (Test-Path -LiteralPath $tool -PathType Leaf) {
                . $tool | Out-Null
            } else {
                foreach ($module in @('Microsoft.Dynamics.Nav.Management', 'Microsoft.Dynamics.Nav.Apps.Management', 'Microsoft.BusinessCentral.Management', 'Microsoft.BusinessCentral.Apps.Management')) {
                    foreach ($extension in @('.psd1', '.psm1', '.dll')) {
                        $candidate = Join-Path $Folder ($module + $extension)
                        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                            Import-Module -Name $candidate -DisableNameChecking -Force | Out-Null
                            break
                        }
                    }
                }
            }
        }

        function New-SectionRunspace {
            $runspace = [runspacefactory]::CreateRunspace()
            $runspace.Open()
            $shell = [powershell]::Create()
            $shell.Runspace = $runspace
            [void]$shell.AddScript($loader.ToString()).AddArgument($folder)
            $handle = $shell.BeginInvoke()
            if (-not $handle.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($loadSeconds))) {
                throw ('The Business Central administration module did not load within ' + $loadSeconds + ' seconds.')
            }
            [void]$shell.EndInvoke($handle)
            $shell.Dispose()
            return $runspace
        }

        # Runs one part with its own time limit. Returns its output, or $null with the
        # reason recorded under $Section.
        function Invoke-Section([string]$Section, [scriptblock]$Body, [object[]]$Arguments, [int]$Seconds) {
            if ([DateTime]::UtcNow -gt $deadline) {
                $unavailable[$Section] = 'Skipped: reading this service tier took longer than the gateway allows.'
                return $null
            }
            if ($null -eq $script:sectionRunspace) { $script:sectionRunspace = New-SectionRunspace }
            $shell = [powershell]::Create()
            $shell.Runspace = $script:sectionRunspace
            [void]$shell.AddScript($Body.ToString())
            foreach ($argument in $Arguments) { [void]$shell.AddArgument($argument) }
            $handle = $shell.BeginInvoke()
            if (-not $handle.AsyncWaitHandle.WaitOne([TimeSpan]::FromSeconds($Seconds))) {
                try { [void]$shell.BeginStop($null, $null) } catch { }
                # Left to finish or not on its own; the next part gets a fresh runspace.
                $script:sectionRunspace = $null
                $unavailable[$Section] = ('Did not answer within ' + $Seconds + ' seconds.')
                return $null
            }
            try {
                # The comma keeps an empty or one-item answer an array: $null means failed.
                return ,@($shell.EndInvoke($handle))
            } catch {
                $unavailable[$Section] = Get-Reason $_
                return $null
            } finally {
                $shell.Dispose()
            }
        }

        $result = [ordered]@{
            schema = 1
            instance = $name
            powerShell = [string]$PSVersionTable.PSVersion
            state = $null
            version = $null
            service = $null
            configuration = $null
            tenants = $null
            tenantSpecificProperties = $false
            sessions = $null
            licence = $null
            unavailable = $unavailable
        }

        # The instance: state and version.
        if (Test-Cmdlet 'Get-NAVServerInstance') {
            $out = Invoke-Section 'instance' {
                param($Name)
                $ErrorActionPreference = 'Stop'
                $found = @(Get-NAVServerInstance -ServerInstance $Name)
                if ($found.Count -eq 0) { throw ('Business Central does not know a server instance called ' + $Name + '.') }
                [pscustomobject]@{ state = [string]$found[0].State; version = [string]$found[0].Version }
            } @($name) $sectionSeconds
            if ($null -ne $out -and $out.Count -gt 0) {
                $result.state = $out[0].state
                $result.version = $out[0].version
            }
        } else {
            $unavailable['instance'] = 'Get-NAVServerInstance is not available in this administration module.'
        }

        # The Windows service, read from Windows itself.
        $out = Invoke-Section 'service' {
            param($Name)
            $ErrorActionPreference = 'Stop'
            $serviceName = 'MicrosoftDynamicsNavServer$' + $Name
            $service = @(Get-CimInstance -ClassName Win32_Service | Where-Object { $_.Name -eq $serviceName })
            if ($service.Count -eq 0) { throw ('Windows has no service called ' + $serviceName + '.') }
            [pscustomobject]@{ state = [string]$service[0].State; startMode = [string]$service[0].StartMode; account = [string]$service[0].StartName }
        } @($name) $sectionSeconds
        if ($null -ne $out -and $out.Count -gt 0) { $result.service = $out[0] }

        # The configuration: a fixed list of keys, never the whole file.
        if (Test-Cmdlet 'Get-NAVServerConfiguration') {
            $asXml = Test-Cmdlet 'Get-NAVServerConfiguration' 'AsXml'
            $out = Invoke-Section 'configuration' {
                param($Name, $AsXml)
                $ErrorActionPreference = 'Stop'
                $values = @{}
                if ($AsXml) {
                    $xml = Get-NAVServerConfiguration -ServerInstance $Name -AsXml
                    foreach ($add in @($xml.configuration.appSettings.add)) {
                        if ($null -ne $add) { $values[[string]$add.key] = [string]$add.value }
                    }
                } else {
                    foreach ($entry in @(Get-NAVServerConfiguration -ServerInstance $Name)) {
                        if ($null -ne $entry) { $values[[string]$entry.key] = [string]$entry.value }
                    }
                }
                $kept = [ordered]@{}
                foreach ($key in @('DatabaseServer', 'DatabaseInstance', 'DatabaseName', 'Multitenant', 'ClientServicesPort', 'SOAPServicesPort', 'ODataServicesPort', 'ManagementServicesPort', 'DeveloperServicesPort', 'PublicWebBaseUrl')) {
                    if ($values.ContainsKey($key)) { $kept[$key] = $values[$key] } else { $kept[$key] = $null }
                }
                [pscustomobject]$kept
            } @($name, $asXml) $sectionSeconds
            if ($null -ne $out -and $out.Count -gt 0) { $result.configuration = $out[0] }
        } else {
            $unavailable['configuration'] = 'Get-NAVServerConfiguration is not available in this administration module.'
        }

        # Everything below asks the running server; a stopped tier cannot answer.
        $running = [string]$result.state -eq 'Running'
        if (-not $running) {
            $why = if ($null -eq $result.state) { 'The state of the service tier could not be read, so the gateway did not ask it for more.' } else { 'The service tier is not running.' }
            foreach ($section in @('tenants', 'apps', 'sessions', 'licence')) { $unavailable[$section] = $why }
        } else {
            $multitenant = $null -ne $result.configuration -and [string]$result.configuration.Multitenant -eq 'true'

            # Tenants. A single-tenant server has one, called default.
            $tenantRows = $null
            if (Test-Cmdlet 'Get-NAVTenant') {
                $tenantRows = Invoke-Section 'tenants' {
                    param($Name)
                    $ErrorActionPreference = 'Stop'
                    foreach ($tenant in @(Get-NAVTenant -ServerInstance $Name)) {
                        [pscustomobject]@{ id = [string]$tenant.Id; state = [string]$tenant.State; databaseName = [string]$tenant.DatabaseName }
                    }
                } @($name) $sectionSeconds
            } else {
                $unavailable['tenants'] = 'Get-NAVTenant is not available in this administration module.'
            }
            if ($null -eq $tenantRows -and -not $multitenant) {
                # Single-tenant: the one tenant needs no cmdlet to be known.
                $unavailable.Remove('tenants')
                $tenantRows = @([pscustomobject]@{ id = 'default'; state = $null; databaseName = $null })
            }

            $tenants = @()
            if ($null -ne $tenantRows) {
                $tenantSpecific = Test-Cmdlet 'Get-NAVAppInfo' 'TenantSpecificProperties'
                $result.tenantSpecificProperties = $tenantSpecific
                $hasApps = Test-Cmdlet 'Get-NAVAppInfo'
                if (-not $hasApps) {
                    $unavailable['apps'] = 'Get-NAVAppInfo is not available on this Business Central version.'
                } elseif (-not $tenantSpecific) {
                    $unavailable['apps.tenantSpecificProperties'] = 'This Business Central version has no -TenantSpecificProperties, so data versions and sync states are not shown.'
                }
                $index = 0
                foreach ($row in @($tenantRows)) {
                    if ($index -ge $maxTenants) { break }
                    $index++
                    $tenant = [ordered]@{ id = [string]$row.id; state = $row.state; databaseName = $row.databaseName; sessionCount = $null; apps = $null }
                    if ($hasApps) {
                        $apps = Invoke-Section ('apps:' + $tenant.id) {
                            param($Name, $Tenant, $TenantSpecific)
                            $ErrorActionPreference = 'Stop'
                            function Text($item, [string]$property) {
                                $p = $item.PSObject.Properties[$property]
                                if ($null -eq $p -or $null -eq $p.Value) { return $null }
                                return [string]$p.Value
                            }
                            $arguments = @{ ServerInstance = $Name; Tenant = $Tenant }
                            if ($TenantSpecific) { $arguments['TenantSpecificProperties'] = $true }
                            foreach ($app in @(Get-NAVAppInfo @arguments)) {
                                [pscustomobject]@{
                                    appId = Text $app 'AppId'
                                    name = Text $app 'Name'
                                    publisher = Text $app 'Publisher'
                                    version = Text $app 'Version'
                                    dataVersion = Text $app 'ExtensionDataVersion'
                                    scope = Text $app 'Scope'
                                    syncState = Text $app 'SyncState'
                                    isInstalled = Text $app 'IsInstalled'
                                    isPublished = Text $app 'IsPublished'
                                }
                            }
                        } @($name, $tenant.id, $tenantSpecific) $appsSeconds
                        if ($null -ne $apps) { $tenant.apps = @($apps) }
                    }
                    $tenants += [pscustomobject]$tenant
                }
                $result.tenants = $tenants
            }

            # Sessions: how many per tenant; who, only when the policy file allows it.
            if (Test-Cmdlet 'Get-NAVServerSession') {
                $perTenant = Test-Cmdlet 'Get-NAVServerSession' 'Tenant'
                $tenantIds = @($tenants | ForEach-Object { $_.id })
                if ($tenantIds.Count -eq 0) { $tenantIds = @('default') }
                $out = Invoke-Section 'sessions' {
                    param($Name, $TenantIds, $PerTenant, $IncludeUsers)
                    $ErrorActionPreference = 'Stop'
                    $counts = @()
                    $users = @{}
                    $total = 0
                    if ($PerTenant) {
                        foreach ($id in $TenantIds) {
                            $sessions = @(Get-NAVServerSession -ServerInstance $Name -Tenant $id)
                            $counts += [pscustomobject]@{ tenant = $id; count = $sessions.Count }
                            $total += $sessions.Count
                            if ($IncludeUsers) { foreach ($s in $sessions) { if ($s.UserID) { $users[[string]$s.UserID] = $true } } }
                        }
                    } else {
                        $sessions = @(Get-NAVServerSession -ServerInstance $Name)
                        $total = $sessions.Count
                        if ($IncludeUsers) { foreach ($s in $sessions) { if ($s.UserID) { $users[[string]$s.UserID] = $true } } }
                    }
                    $names = $null
                    if ($IncludeUsers) { $names = @($users.Keys | Sort-Object) }
                    [pscustomobject]@{ total = $total; perTenant = $counts; users = $names }
                } @($name, $tenantIds, $perTenant, $includeUsers) $sectionSeconds
                if ($null -ne $out -and $out.Count -gt 0) {
                    $result.sessions = $out[0]
                    foreach ($count in @($out[0].perTenant)) {
                        foreach ($tenant in $tenants) { if ($tenant.id -eq $count.tenant) { $tenant.sessionCount = $count.count } }
                    }
                }
            } else {
                $unavailable['sessions'] = 'Get-NAVServerSession is not available in this administration module.'
            }

            # The licence, as text. AL Workbench reads it; the gateway only cuts it to size.
            if (Test-Cmdlet 'Export-NAVServerLicenseInformation') {
                $out = Invoke-Section 'licence' {
                    param($Name)
                    $ErrorActionPreference = 'Stop'
                    (Export-NAVServerLicenseInformation -ServerInstance $Name | Out-String)
                } @($name) $sectionSeconds
                if ($null -ne $out) {
                    $text = (@($out) -join "`n")
                    $cut = $text.Length -gt $maxLicenceChars
                    if ($cut) { $text = $text.Substring(0, $maxLicenceChars) }
                    $result.licence = [pscustomobject]@{ text = $text; truncated = $cut }
                }
            } else {
                $unavailable['licence'] = 'Export-NAVServerLicenseInformation is not available in this administration module.'
            }
        }

        [pscustomobject]$result
        """);
}
