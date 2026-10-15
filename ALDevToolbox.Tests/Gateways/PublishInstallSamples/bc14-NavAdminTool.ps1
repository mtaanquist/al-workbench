# SYNTHETIC stand-in for a Business Central 14 tier's NavAdminTool.ps1, written from the
# documented parameters and output of the cmdlets publish-install uses. A single-tenant
# server running CRONUS Coffee 1.3.0.0, published to its one tenant. What differs from a
# current version: Get-NAVAppInfo has no -TenantSpecificProperties (so no data version),
# Unpublish-NAVApp has no -Tenant, and Get-AuthenticodeSignature is stood in because it
# only exists on Windows. Each cmdlet writes one line to calls.txt beside this file.
$global:StubLog = Join-Path $PSScriptRoot 'calls.txt'

function Write-Call([string]$Line) { Add-Content -LiteralPath $global:StubLog -Value $Line }

function Get-NAVAppInfo([string]$ServerInstance, [string]$Tenant, [string]$Name, [string]$Publisher, [version]$Version) {
    Write-Call ('Get-NAVAppInfo Tenant=' + $Tenant)
    [pscustomobject]@{ AppId = [guid]'63ca2fa4-4f03-4f2b-a480-172fef340d3f'; Name = 'System Application'; Publisher = 'Microsoft'; Version = [version]'14.0.47786.0'; Scope = 'Global'; IsInstalled = $true }
    [pscustomobject]@{ AppId = [guid]'5b7d3f4e-6a1c-4c2b-9d8e-1f0a2b3c4d5e'; Name = 'CRONUS Coffee'; Publisher = 'CRONUS Partner'; Version = [version]'1.3.0.0'; Scope = 'Tenant'; IsInstalled = $true }
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
    [pscustomobject]@{ Status = 'Valid'; StatusMessage = 'Signature verified.' }
}
