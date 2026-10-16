# Inventory samples - SYNTHETIC, replace with real captures

Each file here is what the gateway's inventory script (`ALWorkbench.Gateway.Core/Inventory/InventoryScript.cs`)
prints for one service tier, before `InventoryNormaliser` reads it. The tests in
`GatewayInventoryParsingTests` read these files.

**None of them came from a real server.** They were written by hand from the documented shapes of
`Get-NAVServerInstance`, `Get-NAVServerConfiguration`, `Get-NAVTenant`, `Get-NAVAppInfo`,
`Get-NAVServerSession` and `Export-NAVServerLicenseInformation`, so they prove the code reads the
script's shape, not that the script reads real servers correctly. The licence texts in particular
are guesses at the layout of the `.flf` (BC 14) and `.bclicense` (BC 22 and later) information.

| File | Stands in for |
| --- | --- |
| `bc14-hybrid.json` | A BC 14 tier read with Windows PowerShell 5.1: no `-TenantSpecificProperties`, an array wrapped the way 5.1 sometimes wraps one, an `.flf`-style licence without an expiry date |
| `bc25-multitenant.json` | A BC 25 multitenant tier read with PowerShell 7: two tenants, data versions and sync states, a `.bclicense`-style licence with an expiry date |
| `bc14-stopped.json` | A stopped BC 14 tier: only the instance, the Windows service and the configuration are read |

## Replacing them with real captures

On one of our own servers, from a command prompt opened as administrator, with `gateway.toml`
listing the tiers to read (`allow = ["inventory"]` is enough):

```
ALWorkbench.Gateway check --inventory C:\captures
```

It writes one `inventory-<machine>-<instance>.json` per tier on that computer and changes nothing.
Capture at least a BC 14 hybrid tier and a current version, ideally on the same machine, and a
stopped one. Before committing a capture:

- replace the licensee, address and serial number with CRONUS values (keep the layout, the dates and
  the object ranges exactly as printed);
- replace user names in `sessions.users` (only there when `session_users` is allowed);
- keep everything else byte for byte, including anything that looks odd: that is what the tests are for.

Then point the tests at the new files, fix whatever they show in `InventoryScript` (differences
between versions belong in the script) or in `GatewayLicenceParser` (licence layouts), and delete
this paragraph's "SYNTHETIC" from the title.
