# Publish-install samples - SYNTHETIC, replace with real captures

What the `publish-install` verb (`ALWorkbench.Gateway.Core/PublishInstall/`) reads from a service
tier, for one Business Central 14 tier and one current version. The tests in
`PublishInstallSampleTests` read these files.

**None of them came from a real server.** They were written by hand from the documented
parameters and output of `Get-NAVAppInfo` (with `-Tenant` and with `-Path`), `Get-NAVAppTenant`, `Get-NAVTenant`, `Publish-NAVApp`, `Sync-NAVApp`,
`Start-NAVAppDataUpgrade`, `Install-NAVApp`, `Unpublish-NAVApp` and `Get-AuthenticodeSignature`,
so they prove the gateway reads the shapes it expects, not that real servers produce them.

| File | Stands in for |
| --- | --- |
| `bc14-inspect.json` | What the inspect script prints on a BC 14 tier under Windows PowerShell 5.1: no `-TenantSpecificProperties` (so no data versions or sync states, and the installed state from `Get-NAVAppTenant`), no `-Tenant` on `Unpublish-NAVApp`, a braced `PackageId`, arrays wrapped the way 5.1 sometimes wraps them |
| `bc25-inspect-multitenant.json` | The same on a BC 25 multitenant tier under PowerShell 7: an older version installed here and on another tenant, an even older one installed nowhere |
| `bc25-inspect-uninstalled-with-data.json` | A BC 25 tenant where the app was uninstalled and its data kept; a one-item list written as the item |
| `bc14-NavAdminTool.ps1` | A BC 14 tier's administration tool: the cmdlets as PowerShell functions with BC 14's parameters, returning BC 14's object shapes, recording each call. Run by the real scripts under `pwsh`. It plays a version without `-TenantSpecificProperties`, so `Get-NAVAppInfo` returns no `IsInstalled` and `Get-NAVAppTenant` says which tenants have a version installed |
| `bc25-NavAdminTool.ps1` | The same for a BC 25 multitenant tier, where `Get-NAVAppInfo` returns `IsInstalled` only with `-TenantSpecificProperties` |

Both stand-ins answer `Get-NAVAppInfo -Path` by reading the `.app` the test built (its NAVX header
and manifest), the way the gateway's own reader does.

## Replacing them with real captures

To be done on our own servers before the verb is trusted, ideally a BC 14 hybrid tier and a
current version on the same machine:

- run the inspect script against a tier with an app in each state (never installed, an older
  version installed, uninstalled with data kept, the same version installed), and save what it
  prints in place of the `*-inspect*.json` files;
- check each cmdlet's parameters on that version (`(Get-Command Publish-NAVApp).Parameters.Keys`
  and so on) against the stand-in functions, and correct the functions to match;
- check what `Get-AuthenticodeSignature` says about a signed `.app`, an unsigned one and a
  tampered one, on a server with Business Central installed and on one without, under both
  PowerShell 5.1 and 7 (the component that reads `.app` signatures is registered per bitness), and
  what its `SignerCertificate` holds;
- check whether BC 14's `Get-NAVAppInfo` has `-TenantSpecificProperties`, what `Get-NAVAppTenant`
  returns there, and whether `PackageId` is reported, and correct the BC 14 stand-in to match;
- save what `Get-NAVAppInfo -Path` returns for a normal, a signed and a Ready2Run `.app`.

There is no `check` option that captures these yet (the inventory has `check --inventory`); one
is a small follow-up. Replace user and company names with CRONUS values, keep everything else
byte for byte, and delete "SYNTHETIC" from the title.
