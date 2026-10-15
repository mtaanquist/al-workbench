# SYNTHETIC stand-in for a Business Central 25 tier's NavAdminTool.ps1, written from the
# documented parameters and output of the cmdlets publish-install uses. A multitenant
# server: tenants cronus-dk and cronus-se both run CRONUS Coffee 1.3.0.0, published for
# every tenant, and 1.2.0.0 is still published but installed nowhere. Each cmdlet writes
# one line to calls.txt beside this file so the test can see what the gateway ran.
$global:StubLog = Join-Path $PSScriptRoot 'calls.txt'

function Write-Call([string]$Line) { Add-Content -LiteralPath $global:StubLog -Value $Line }

function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant, [switch]$TenantSpecificProperties, [string]$Name, [string]$Id) {
    Write-Call ('Get-NAVAppInfo Tenant=' + $Tenant + ' TenantSpecificProperties=' + [bool]$TenantSpecificProperties)
    $base = [ordered]@{ AppId = [guid]'437dbf0e-84ff-417a-965d-ed2bb9650972'; Name = 'Base Application'; Publisher = 'Microsoft'; Version = [version]'25.4.29661.29927'; Scope = 'Global'; IsInstalled = $true; IsPublished = $true }
    $old = [ordered]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.2.0.0'; Scope = 'Global'; IsInstalled = $false; IsPublished = $true }
    $current = [ordered]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.3.0.0'; Scope = 'Global'; IsInstalled = $true; IsPublished = $true }
    foreach ($row in @($base, $old, $current)) {
        if ($TenantSpecificProperties) {
            $row['ExtensionDataVersion'] = if ($row.Name -eq 'CRONUS Coffee') { [version]'1.3.0.0' } else { $row.Version }
            $row['SyncState'] = 'Synced'
            $row['NeedsUpgrade'] = $false
        }
        [pscustomobject]$row
    }
}

function Get-NAVTenant([string]$ServerInstance) {
    Write-Call 'Get-NAVTenant'
    [pscustomobject]@{ Id = 'cronus-dk'; State = 'Operational'; DatabaseName = 'BC250-cronus-dk' }
    [pscustomobject]@{ Id = 'cronus-se'; State = 'Operational'; DatabaseName = 'BC250-cronus-se' }
}

function Publish-NAVApp([string]$ServerInstance, [string]$Path, [switch]$SkipVerification, [string]$Scope, [string]$Tenant, [string]$PackageType, [switch]$Force) {
    Write-Call ('Publish-NAVApp Scope=' + $Scope + ' Tenant=' + $Tenant + ' SkipVerification=' + [bool]$SkipVerification + ' File=' + (Split-Path -Leaf $Path))
}

function Sync-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant, [string]$Mode, [switch]$Force) {
    Write-Call ('Sync-NAVApp Name=' + $Name + ' Publisher=' + $Publisher + ' Version=' + $Version + ' Tenant=' + $Tenant + ' Mode=' + $Mode)
}

function Start-NAVAppDataUpgrade([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Start-NAVAppDataUpgrade Name=' + $Name + ' Version=' + $Version + ' Tenant=' + $Tenant)
    'Upgrading CRONUS Coffee from 1.3.0.0 to 1.4.0.0.'
}

function Install-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Install-NAVApp Name=' + $Name + ' Version=' + $Version + ' Tenant=' + $Tenant)
}

function Unpublish-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Unpublish-NAVApp Name=' + $Name + ' Version=' + $Version + ' Tenant=' + $Tenant)
}
