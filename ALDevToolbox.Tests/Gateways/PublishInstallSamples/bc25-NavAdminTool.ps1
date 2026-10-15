# SYNTHETIC stand-in for a Business Central 25 tier's NavAdminTool.ps1, written from the
# documented parameters and output of the cmdlets publish-install uses. A multitenant
# server: tenants cronus-dk and cronus-se both run CRONUS Coffee 1.3.0.0, published for
# every tenant, and 1.2.0.0 is still published but installed nowhere. As the real cmdlet
# does, Get-NAVAppInfo fills IsInstalled only with -TenantSpecificProperties. Each cmdlet
# writes one line to calls.txt beside this file so the test can see what the gateway ran.
$global:StubLog = Join-Path $PSScriptRoot 'calls.txt'

function Write-Call([string]$Line) { Add-Content -LiteralPath $global:StubLog -Value $Line }

# What Get-NAVAppInfo -Path reads from an .app: the NAVX header's package id and the
# manifest's App element, read the way the gateway's own reader does.
function Read-StubAppFile([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $headerLength = [BitConverter]::ToUInt32($bytes, 4)
    $length = [BitConverter]::ToInt64($bytes, 28)
    $packageId = New-Object System.Guid (,[byte[]]$bytes[12..27])
    Add-Type -AssemblyName System.IO.Compression
    $zip = New-Object System.IO.Compression.ZipArchive ((New-Object System.IO.MemoryStream ($bytes, [int]$headerLength, [int]$length)), [System.IO.Compression.ZipArchiveMode]::Read)
    $entry = $zip.Entries | Where-Object { $_.FullName -eq 'NavxManifest.xml' } | Select-Object -First 1
    $reader = New-Object System.IO.StreamReader ($entry.Open())
    [xml]$manifest = $reader.ReadToEnd()
    $reader.Dispose()
    $app = $manifest.Package.App
    [pscustomobject]@{ AppId = [guid]$app.Id; Name = [string]$app.Name; Publisher = [string]$app.Publisher; Version = [version]$app.Version; PackageId = $packageId }
}

function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant, [switch]$TenantSpecificProperties, [string]$Name, [string]$Id, [string]$Path) {
    if ($Path) {
        Write-Call ('Get-NAVAppInfo Path=' + (Split-Path -Leaf $Path))
        return Read-StubAppFile $Path
    }
    Write-Call ('Get-NAVAppInfo Tenant=' + $Tenant + ' TenantSpecificProperties=' + [bool]$TenantSpecificProperties)
    $base = [ordered]@{ AppId = [guid]'437dbf0e-84ff-417a-965d-ed2bb9650972'; Name = 'Base Application'; Publisher = 'Microsoft'; Version = [version]'25.4.29661.29927'; Scope = 'Global'; IsPublished = $true; PackageId = [guid]'0a1b2c3d-4e5f-4a6b-8c7d-8e9f0a1b2c3d' }
    $old = [ordered]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.2.0.0'; Scope = 'Global'; IsPublished = $true; PackageId = [guid]'1d2c3b4a-5f6e-4d7c-8b9a-0f1e2d3c4b5a' }
    $current = [ordered]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.3.0.0'; Scope = 'Global'; IsPublished = $true; PackageId = [guid]'6c1e2a3b-4d5f-4a6b-8c7d-9e0f1a2b3c4d' }
    foreach ($row in @($base, $old, $current)) {
        # Every tenant runs 1.3.0.0 and the Base Application; nobody runs 1.2.0.0. IsInstalled
        # is there only with -TenantSpecificProperties.
        if ($TenantSpecificProperties) {
            $row['IsInstalled'] = $row.Version -ne [version]'1.2.0.0'
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
