using System.Text.Json.Nodes;
using ALWorkbench.Gateway.Contracts;
using ALWorkbench.Gateway.Core.PowerShell;

namespace ALWorkbench.Gateway.Core.PublishInstall;

/// <summary>
/// The two fixed pieces of PowerShell <c>publish-install</c> runs for each app, and
/// their parameters. Like <c>InventoryScript</c>, they are constants in the gateway;
/// the command reaches them only as data in <c>$Parameters</c>, and which cmdlets run is
/// chosen from a fixed list of step kinds (<see cref="Kinds"/>), never from text the
/// server sent. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
///
/// <para><b>One read of the files, then two processes per app, and the decisions in C#.</b>
/// <see cref="Examine"/> runs once, before anything changes, and reads every file as
/// Business Central and Windows read it (its identity and its signature).
/// <see cref="Inspect"/> reads what the tenant has of the app (every version
/// <c>Get-NAVAppInfo -Tenant</c> lists, with <c>-TenantSpecificProperties</c> where the
/// cmdlet has it, which adds whether it is installed and the data version; where it does
/// not, which tenants have each version installed comes from <c>Get-NAVAppTenant</c>, and
/// with neither it stays unknown), which other tenants still have it installed, and which
/// parameters this version's cmdlets take. <see cref="AppInstallPlanner"/>
/// decides from that, in C#, where it is tested without Business Central. <see cref="Apply"/>
/// then runs the planned steps in order and stops at the first that fails. Publishing
/// and syncing change neither what is installed nor the data version, so a decision made
/// before them still holds after. One process per step would load the administration
/// module five times per app; one for everything would put the decision in script.</para>
///
/// <para><b>Business Central 14 and later.</b> Every parameter not all versions have
/// (<c>-TenantSpecificProperties</c>, <c>-Scope</c> and <c>-Tenant</c> on
/// <c>Publish-NAVApp</c>, <c>-Tenant</c> on <c>Unpublish-NAVApp</c> and the others) is
/// looked up with <c>Get-Command</c> first, as the inventory does. A step that would need
/// a parameter the version lacks fails with a sentence saying so rather than doing
/// something else: an app meant for one tenant is never published to all of them because
/// <c>-Scope</c> is missing.</para>
/// </summary>
public static class PublishInstallScript
{
    /// <summary>Reading the tenant: a few cmdlet calls, longer on a server with many tenants.</summary>
    public static readonly TimeSpan InspectTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// One app's steps. A data upgrade can legitimately take hours; the command stays
    /// alive in AL Workbench while the gateway keeps heartbeating it.
    /// </summary>
    public static readonly TimeSpan ApplyTimeout = TimeSpan.FromHours(6);

    /// <summary>The step kinds <see cref="Apply"/> knows. Anything else fails the step.</summary>
    public static class Kinds
    {
        public const string Publish = "publish";
        public const string Sync = "sync";
        public const string Install = "install";
        public const string Upgrade = "upgrade";
        public const string Unpublish = "unpublish";
    }

    /// <summary>The parameters of <see cref="Inspect"/>.</summary>
    public static JsonObject InspectParameters(string instance, string tenant, Guid appId, bool otherTenants) => new()
    {
        [GatewayCommandParameters.Instance] = instance,
        [GatewayCommandParameters.Tenant] = tenant,
        ["appId"] = appId.ToString("D"),
        ["otherTenants"] = otherTenants,
    };

    /// <summary>The parameters of <see cref="Apply"/>: the app as read from its file, where the file is, and the steps.</summary>
    public static JsonObject ApplyParameters(string instance, string tenant, AppFileInfo app, string path, IEnumerable<AppStep> steps)
    {
        var list = new JsonArray();
        foreach (var step in steps) list.Add(step.ToJson());
        return new JsonObject
        {
            [GatewayCommandParameters.Instance] = instance,
            [GatewayCommandParameters.Tenant] = tenant,
            ["name"] = app.Name,
            ["publisher"] = app.Publisher,
            ["version"] = app.Version.ToString(),
            ["path"] = path,
            ["steps"] = list,
        };
    }

    /// <summary>Reading the files: one quick read of each, plus a signature check.</summary>
    public static readonly TimeSpan ExamineTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The parameters of <see cref="Examine"/>: each file, and whether to read its signature.</summary>
    public static JsonObject ExamineParameters(IEnumerable<(string Path, bool Signature)> files)
    {
        var list = new JsonArray();
        foreach (var (path, signature) in files) list.Add(new JsonObject { ["path"] = path, ["signature"] = signature });
        return new JsonObject { ["files"] = list };
    }

    /// <summary>
    /// Read only, and run once for all of a command's files before anything is changed:
    /// what Business Central itself reads from each <c>.app</c> (<c>Get-NAVAppInfo -Path</c>),
    /// and, where asked, what Windows says about its signature
    /// (<c>Get-AuthenticodeSignature</c>: status and signing certificate). The gateway's own
    /// reading (<see cref="AppFile"/>) decides the publisher check; this one makes sure
    /// Business Central reads the same app from the same bytes. Returns one entry per file:
    /// <c>{ path, app { id, name, publisher, version, packageId }, appError, signature
    /// { status, statusMessage, subject, thumbprint }, signatureError }</c>.
    /// </summary>
    public static readonly PowerShellOperation Examine = new("publish-install-examine", """
        function Text($item, [string]$property) {
            if ($null -eq $item) { return $null }
            $p = $item.PSObject.Properties[$property]
            if ($null -eq $p -or $null -eq $p.Value) { return $null }
            return [string]$p.Value
        }
        function Get-Reason($record) {
            $e = $record.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            $text = ([string]$e.Message).Trim()
            if ($text.Length -gt 900) { $text = $text.Substring(0, 900) + '...' }
            if ($text.Length -eq 0) { $text = 'It failed without saying why.' }
            return $text
        }

        $info = Get-Command -Name 'Get-NAVAppInfo' -ErrorAction SilentlyContinue
        $readsFiles = ($null -ne $info) -and $info.Parameters.ContainsKey('Path')
        $signatures = $null -ne (Get-Command -Name 'Get-AuthenticodeSignature' -ErrorAction SilentlyContinue)
        $results = @()
        foreach ($file in @($Parameters.files)) {
            $path = [string]$file.path
            $entry = [ordered]@{ path = $path; app = $null; appError = $null; signature = $null; signatureError = $null }
            if (-not $readsFiles) {
                $entry.appError = 'Get-NAVAppInfo on this Business Central version cannot read an .app file'
            } else {
                try {
                    $app = @(Get-NAVAppInfo -Path $path)
                    if ($app.Count -ne 1) { throw ('Get-NAVAppInfo returned ' + $app.Count + ' apps for the file.') }
                    $id = Text $app[0] 'AppId'
                    if ($null -eq $id) { $id = Text $app[0] 'Id' }
                    $entry.app = [pscustomobject]@{
                        id = $id
                        name = Text $app[0] 'Name'
                        publisher = Text $app[0] 'Publisher'
                        version = Text $app[0] 'Version'
                        packageId = Text $app[0] 'PackageId'
                    }
                } catch {
                    $entry.appError = Get-Reason $_
                }
            }
            if ([bool]$file.signature) {
                if (-not $signatures) {
                    $entry.signatureError = 'Windows signature checking (Get-AuthenticodeSignature) is not available here'
                } else {
                    try {
                        $signature = Get-AuthenticodeSignature -LiteralPath $path
                        $certificate = $signature.SignerCertificate
                        $entry.signature = [pscustomobject]@{
                            status = [string]$signature.Status
                            statusMessage = [string]$signature.StatusMessage
                            subject = Text $certificate 'Subject'
                            thumbprint = Text $certificate 'Thumbprint'
                        }
                    } catch {
                        $entry.signatureError = Get-Reason $_
                    }
                }
            }
            $results += [pscustomobject]$entry
        }
        $results
        """);

    /// <summary>Read only: <c>Get-Command</c>, <c>Get-NAVAppInfo</c>, <c>Get-NAVAppTenant</c> and <c>Get-NAVTenant</c>.</summary>
    public static readonly PowerShellOperation Inspect = new("publish-install-inspect", """
        $instance = [string]$Parameters.instance
        $tenant = [string]$Parameters.tenant
        $appId = [string]$Parameters.appId
        $askOthers = [bool]$Parameters.otherTenants

        function Test-Parameter([string]$Command, [string]$Parameter) {
            $found = Get-Command -Name $Command -ErrorAction SilentlyContinue
            if ($null -eq $found) { return $false }
            return $found.Parameters.ContainsKey($Parameter)
        }
        function Text($item, [string]$property) {
            $p = $item.PSObject.Properties[$property]
            if ($null -eq $p -or $null -eq $p.Value) { return $null }
            return [string]$p.Value
        }
        function Get-Reason($record) {
            $e = $record.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            $text = ([string]$e.Message).Trim()
            if ($text.Length -gt 900) { $text = $text.Substring(0, 900) + '...' }
            if ($text.Length -eq 0) { $text = 'It failed without saying why.' }
            return $text
        }
        # The rows of one tenant's Get-NAVAppInfo that are this app. IsInstalled is read only
        # with -TenantSpecificProperties: without it Get-NAVAppInfo leaves it unset, which
        # would read as "not installed" when it means "not asked".
        function Get-Rows([string]$Tenant) {
            $arguments = @{ ServerInstance = $instance; Tenant = $Tenant }
            if ($tenantSpecific) { $arguments['TenantSpecificProperties'] = $true }
            foreach ($app in @(Get-NAVAppInfo @arguments)) {
                $id = Text $app 'AppId'
                if ($null -eq $id) { $id = Text $app 'Id' }
                if ($null -ne $id -and $id.Trim('{', '}') -eq $appId) {
                    [pscustomobject]@{
                        version = Text $app 'Version'
                        name = Text $app 'Name'
                        publisher = Text $app 'Publisher'
                        scope = Text $app 'Scope'
                        isInstalled = $(if ($tenantSpecific) { Text $app 'IsInstalled' } else { $null })
                        isPublished = Text $app 'IsPublished'
                        dataVersion = Text $app 'ExtensionDataVersion'
                        syncState = Text $app 'SyncState'
                        packageId = Text $app 'PackageId'
                    }
                }
            }
        }
        # Without -TenantSpecificProperties: the tenants that have one version installed.
        function Get-InstalledTenants($Row) {
            $ids = New-Object System.Collections.Generic.List[string]
            foreach ($t in @(Get-NAVAppTenant -ServerInstance $instance -Name $Row.name -Publisher $Row.publisher -Version ([version]$Row.version))) {
                $id = Text $t 'Id'
                if ($null -eq $id) { $id = Text $t 'TenantId' }
                if ($null -eq $id) { throw 'Get-NAVAppTenant returned a tenant without an id.' }
                $ids.Add($id)
            }
            return ,$ids
        }

        if ($null -eq (Get-Command -Name 'Get-NAVAppInfo' -ErrorAction SilentlyContinue)) {
            throw 'Get-NAVAppInfo is not available in this administration module.'
        }
        $tenantSpecific = Test-Parameter 'Get-NAVAppInfo' 'TenantSpecificProperties'
        $appTenant = (-not $tenantSpecific) -and ($null -ne (Get-Command -Name 'Get-NAVAppTenant' -ErrorAction SilentlyContinue))
        $installedFrom = $null
        if ($tenantSpecific) { $installedFrom = 'TenantSpecificProperties' } elseif ($appTenant) { $installedFrom = 'Get-NAVAppTenant' }
        $rows = @(Get-Rows $tenant)
        $installedOn = @{}
        if ($appTenant) {
            foreach ($row in $rows) {
                $ids = Get-InstalledTenants $row
                $installedOn[[string]$row.version] = $ids
                $row.isInstalled = [string](@($ids | Where-Object { $_ -eq $tenant }).Count -gt 0)
            }
        }
        $result = [ordered]@{
            schema = 2
            tenantSpecificProperties = $tenantSpecific
            installedFrom = $installedFrom
            parameters = [ordered]@{
                publishScope = Test-Parameter 'Publish-NAVApp' 'Scope'
                publishTenant = Test-Parameter 'Publish-NAVApp' 'Tenant'
                syncTenant = Test-Parameter 'Sync-NAVApp' 'Tenant'
                installTenant = Test-Parameter 'Install-NAVApp' 'Tenant'
                upgradeTenant = Test-Parameter 'Start-NAVAppDataUpgrade' 'Tenant'
                unpublishTenant = Test-Parameter 'Unpublish-NAVApp' 'Tenant'
            }
            versions = $rows
            otherTenants = $null
            otherTenantsUnknown = $null
        }

        # Whether another tenant still has a version installed decides whether a
        # globally published previous version may go. Read the same way as this tenant's;
        # anything unknown leaves the list out, so the version is kept.
        if ($askOthers) {
            if ($null -eq (Get-Command -Name 'Get-NAVTenant' -ErrorAction SilentlyContinue)) {
                $result.otherTenantsUnknown = 'Get-NAVTenant is not available in this administration module.'
            } elseif ($null -eq $installedFrom) {
                $result.otherTenantsUnknown = 'this Business Central version does not say which versions a tenant has installed'
            } else {
                try {
                    $others = @()
                    foreach ($row in @(Get-NAVTenant -ServerInstance $instance)) {
                        $id = [string]$row.Id
                        if ($id -eq $tenant) { continue }
                        if ($tenantSpecific) {
                            $theirs = @(Get-Rows $id)
                            if (@($theirs | Where-Object { $null -eq $_.isInstalled }).Count -gt 0) {
                                throw ('Get-NAVAppInfo did not say what tenant ' + $id + ' has installed.')
                            }
                            $installed = @($theirs | Where-Object { [string]$_.isInstalled -eq 'True' } | ForEach-Object { $_.version })
                        } else {
                            $installed = @($installedOn.Keys | Where-Object { @($installedOn[$_] | Where-Object { $_ -eq $id }).Count -gt 0 })
                        }
                        $others += [pscustomobject]@{ id = $id; installed = $installed }
                    }
                    $result.otherTenants = $others
                } catch {
                    $result.otherTenantsUnknown = Get-Reason $_
                }
            }
        }
        [pscustomobject]$result
        """);

    /// <summary>
    /// Runs the planned steps for one app, in order, and stops at the first that fails.
    /// Returns one entry per step it ran: <c>{ kind, version, ok, error }</c>. Each step
    /// writes a line before it starts, so a long data upgrade shows in the log while it
    /// runs; whatever the cmdlets print goes to the log too.
    /// </summary>
    public static readonly PowerShellOperation Apply = new("publish-install-apply", """
        $instance = [string]$Parameters.instance
        $tenant = [string]$Parameters.tenant
        $name = [string]$Parameters.name
        $publisher = [string]$Parameters.publisher
        $version = [version][string]$Parameters.version
        $path = [string]$Parameters.path

        function Test-Parameter([string]$Command, [string]$Parameter) {
            $found = Get-Command -Name $Command -ErrorAction SilentlyContinue
            if ($null -eq $found) { return $false }
            return $found.Parameters.ContainsKey($Parameter)
        }
        function Get-Reason($record) {
            $e = $record.Exception
            while ($null -ne $e.InnerException) { $e = $e.InnerException }
            $text = ([string]$e.Message).Trim()
            if ($text.Length -gt 900) { $text = $text.Substring(0, 900) + '...' }
            if ($text.Length -eq 0) { $text = 'It failed without saying why.' }
            return $text
        }
        function Write-CmdletOutput($output) {
            foreach ($line in @(($output | Out-String) -split "`r?`n")) {
                if ($line.Trim().Length -gt 0) { Write-GatewayLog ('  ' + $line.TrimEnd()) }
            }
        }
        # The app's name, publisher and version, and the tenant where the cmdlet takes one.
        function Get-AppArguments([string]$Command, [version]$Version, [bool]$WithTenant) {
            $arguments = @{ ServerInstance = $instance; Name = $name; Publisher = $publisher; Version = $Version }
            if ($WithTenant) {
                if (Test-Parameter $Command 'Tenant') {
                    $arguments['Tenant'] = $tenant
                } elseif ($tenant -ne 'default') {
                    throw ($Command + ' on this Business Central version cannot act on one tenant, so the gateway did not run it for tenant ' + $tenant + '.')
                }
            }
            return $arguments
        }

        $ran = @()
        foreach ($step in @($Parameters.steps)) {
            $kind = [string]$step.kind
            $entry = [ordered]@{ kind = $kind; version = $null; ok = $false; error = $null }
            try {
                switch ($kind) {
                    'publish' {
                        $arguments = @{ ServerInstance = $instance; Path = $path }
                        if ([bool]$step.skipVerification) { $arguments['SkipVerification'] = $true }
                        if ([string]$step.scope -eq 'Tenant') {
                            if (-not (Test-Parameter 'Publish-NAVApp' 'Scope') -or -not (Test-Parameter 'Publish-NAVApp' 'Tenant')) {
                                throw 'Publish-NAVApp on this Business Central version cannot publish to one tenant. If the app should go to every tenant, customer IT can set scope = "global" for this service tier in gateway.toml.'
                            }
                            $arguments['Scope'] = 'Tenant'
                            $arguments['Tenant'] = $tenant
                        } elseif (Test-Parameter 'Publish-NAVApp' 'Scope') {
                            $arguments['Scope'] = 'Global'
                        }
                        Write-GatewayLog ('Publish-NAVApp, scope ' + [string]$step.scope + '.')
                        Write-CmdletOutput (Publish-NAVApp @arguments)
                    }
                    'sync' {
                        $arguments = Get-AppArguments 'Sync-NAVApp' $version $true
                        $arguments['Mode'] = [string]$step.mode
                        Write-GatewayLog ('Sync-NAVApp, mode ' + [string]$step.mode + '.')
                        Write-CmdletOutput (Sync-NAVApp @arguments)
                    }
                    'install' {
                        $arguments = Get-AppArguments 'Install-NAVApp' $version $true
                        Write-GatewayLog 'Install-NAVApp.'
                        Write-CmdletOutput (Install-NAVApp @arguments)
                    }
                    'upgrade' {
                        $arguments = Get-AppArguments 'Start-NAVAppDataUpgrade' $version $true
                        Write-GatewayLog 'Start-NAVAppDataUpgrade.'
                        Write-CmdletOutput (Start-NAVAppDataUpgrade @arguments)
                    }
                    'unpublish' {
                        $previous = [version][string]$step.version
                        $entry.version = [string]$previous
                        $arguments = Get-AppArguments 'Unpublish-NAVApp' $previous ([bool]$step.tenantScoped)
                        Write-GatewayLog ('Unpublish-NAVApp, version ' + [string]$previous + '.')
                        Write-CmdletOutput (Unpublish-NAVApp @arguments)
                    }
                    default {
                        throw ('The gateway has no step called ' + $kind + '.')
                    }
                }
                $entry.ok = $true
            } catch {
                $entry.error = Get-Reason $_
            }
            $ran += [pscustomobject]$entry
            if (-not $entry.ok) { break }
        }
        $ran
        """);
}

/// <summary>One step <see cref="PublishInstallScript.Apply"/> is to run.</summary>
/// <param name="Kind">One of <see cref="PublishInstallScript.Kinds"/>.</param>
/// <param name="Scope"><c>publish</c>: <c>Tenant</c> or <c>Global</c>.</param>
/// <param name="SkipVerification"><c>publish</c>: pass <c>-SkipVerification</c>.</param>
/// <param name="Mode"><c>sync</c>: the schema sync mode.</param>
/// <param name="Version"><c>unpublish</c>: the previous version to remove.</param>
/// <param name="TenantScoped"><c>unpublish</c>: the previous version was published to this tenant only, so it is removed with <c>-Tenant</c>.</param>
public sealed record AppStep(
    string Kind,
    string? Scope = null,
    bool SkipVerification = false,
    string? Mode = null,
    string? Version = null,
    bool TenantScoped = false)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind };
        if (Scope is not null) json["scope"] = Scope;
        if (Kind == PublishInstallScript.Kinds.Publish) json["skipVerification"] = SkipVerification;
        if (Mode is not null) json["mode"] = Mode;
        if (Version is not null) json["version"] = Version;
        if (Kind == PublishInstallScript.Kinds.Unpublish) json["tenantScoped"] = TenantScoped;
        return json;
    }
}
