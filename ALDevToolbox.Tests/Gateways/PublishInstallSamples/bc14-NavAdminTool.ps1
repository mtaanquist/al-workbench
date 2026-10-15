# SYNTHETIC stand-in for a Business Central 14 tier's NavAdminTool.ps1, written from the
# documented parameters and output of the cmdlets publish-install uses. A single-tenant
# server running CRONUS Coffee 1.3.0.0, published to its one tenant. What differs from a
# current version: Get-NAVAppInfo has no -TenantSpecificProperties (so no data version,
# and no IsInstalled: which tenants have a version installed comes from Get-NAVAppTenant),
# Unpublish-NAVApp has no -Tenant, and Get-AuthenticodeSignature is stood in because it
# only exists on Windows. Whether BC 14 really lacks -TenantSpecificProperties is to be
# verified (see the README); this stand-in plays the version without it, so the fallback
# is what runs. Each cmdlet writes one line to calls.txt beside this file.
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

# No -TenantSpecificProperties on this version, so no IsInstalled either: which tenants
# have a version installed is Get-NAVAppTenant's to say.
function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant, [string]$Name, [string]$Publisher, [version]$Version, [string]$Path) {
    if ($Path) {
        Write-Call ('Get-NAVAppInfo Path=' + (Split-Path -Leaf $Path))
        return Read-StubAppFile $Path
    }
    Write-Call ('Get-NAVAppInfo Tenant=' + $Tenant)
    [pscustomobject]@{ AppId = [guid]'63ca2fa4-4f03-4f2b-a480-172fef340d3f'; Name = 'System Application'; Publisher = 'Microsoft'; Version = [version]'14.0.47786.0'; Scope = 'Global'; PackageId = [guid]'0a1b2c3d-4e5f-4a6b-8c7d-8e9f0a1b2c3d' }
    [pscustomobject]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.3.0.0'; Scope = 'Tenant'; PackageId = [guid]'6c1e2a3b-4d5f-4a6b-8c7d-9e0f1a2b3c4d' }
}

# The tenants that have one version of an app installed.
function Get-NAVAppTenant([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Get-NAVAppTenant Name=' + $Name + ' Version=' + $Version)
    if ($Name -eq 'CRONUS Coffee' -and $Version -eq [version]'1.3.0.0') { [pscustomobject]@{ Id = 'default'; State = 'Operational' } }
}

function Get-NAVTenant([string]$ServerInstance) {
    Write-Call 'Get-NAVTenant'
    [pscustomobject]@{ Id = 'default'; State = 'Operational' }
}

function Publish-NAVApp([string]$ServerInstance, [string]$Path, [switch]$SkipVerification, [string]$Scope, [string]$Tenant, [string]$PackageType) {
    Write-Call ('Publish-NAVApp Scope=' + $Scope + ' Tenant=' + $Tenant + ' SkipVerification=' + [bool]$SkipVerification + ' File=' + (Split-Path -Leaf $Path))
}

function Sync-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant, [string]$Mode) {
    Write-Call ('Sync-NAVApp Name=' + $Name + ' Publisher=' + $Publisher + ' Version=' + $Version + ' Tenant=' + $Tenant + ' Mode=' + $Mode)
}

function Start-NAVAppDataUpgrade([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Start-NAVAppDataUpgrade Name=' + $Name + ' Version=' + $Version + ' Tenant=' + $Tenant)
}

function Install-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version, [string]$Tenant) {
    Write-Call ('Install-NAVApp Name=' + $Name + ' Version=' + $Version + ' Tenant=' + $Tenant)
}

function Unpublish-NAVApp([string]$ServerInstance, [string]$Name, [string]$Publisher, [version]$Version) {
    Write-Call ('Unpublish-NAVApp Name=' + $Name + ' Version=' + $Version)
}

function Get-AuthenticodeSignature([string]$LiteralPath) {
    Write-Call ('Get-AuthenticodeSignature File=' + (Split-Path -Leaf $LiteralPath))
    [pscustomobject]@{
        Status = 'Valid'
        StatusMessage = 'Signature verified.'
        SignerCertificate = [pscustomobject]@{ Subject = 'CN=CRONUS Partner, O=CRONUS Partner, C=DK'; Thumbprint = '3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E' }
    }
}
