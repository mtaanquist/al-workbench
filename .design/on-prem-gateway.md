# On-premises gateway - managing on-premises Business Central from AL Workbench

> **Status: designed, not built.** Milestone #8, issues #1359 to #1376. Built on the
> `staging/on-prem-gateway` branch and merged to `main` in one PR when the milestone is done.
> The feasibility study that led here is summarised in "Why a gateway" below.

Today everything AL Workbench does to a customer's Business Central goes through Microsoft's Admin
Center API (`saas-delivery.md`, `environment-updates.md`), which only exists online. A Solution whose
hosting is anything but Microsoft's cloud is *on-premises* and loses the Business Central tab,
deployment pipelines and its rows on Environments and Upgrades (`solution-customer-info.md`, "What
on-premises turns off"). Builds are unaffected: they compile in our container whatever the hosting.

The **on-premises gateway** closes that gap. It is a small Windows service installed in the network
where the service tiers run. It connects out to AL Workbench, picks up a fixed set of typed commands,
and runs them with Microsoft's Business Central administration PowerShell module. With it, an
on-premises Solution gets environments, deployments, licence upload and service tier restarts.

## Vocabulary

| A person reads | The code says |
| --- | --- |
| On-premises gateway, gateway | `Gateway` (entity, services, projects) |
| Service tier | Server instance (`gateway_instances`) |
| On-premises environment | `OeProjectEnvironment` with `Kind = OnPremises` |
| Restart group | `RestartGroup` in the policy file |
| Setup guide | The docs page for customer IT (#1376) |

**"Agent" is not used for the gateway.** In this product an agent is an AI assistant
(`started_by_agent`, "Let AI assistants deploy to production environments"). The gateway is never
called an agent in copy, code or docs, so the two cannot be confused.

## Goal and named users

- **A BC consultant** opens an on-premises customer's Solution and sees the same things as for an
  online one: which version each service tier runs, what is installed, when the licence expires.
- **A developer or release manager** deploys a successful build to the customer's test or
  production tiers through a deployment pipeline, in the delivery window, without remoting into a
  server, and the tiers are restarted afterwards so no session keeps old event subscribers.
- **An org Admin** sets up a gateway once per network and links its service tiers to Solutions.
- **Customer IT** decides what the gateway may touch, in a file on their own server that AL Workbench
  cannot change, and can read a log of everything it did.

## Why a gateway

Service tiers sit behind customer firewalls; our container cannot reach them, and asking every
customer for inbound access or a site-to-site VPN is not realistic. The production-grade management
surface is the Business Central administration PowerShell module (`Publish-NAVApp`, `Sync-NAVApp`,
...), which is Windows-only and version-matched to the server, so it cannot run in our Linux
container either. The HTTP surfaces that exist on-premises do not replace it: the developer endpoint
is usually off on production servers and is not a production publish path, and the automation API's
`extensionUpload` is the surface online delivery just moved away from, needs stored user credentials,
and only exists from BC 15.

So the work splits: AL Workbench keeps everything it does today (compile, schedule, approve, audit,
UI, MCP), and the gateway does only what has to happen on the customer's Windows machines.

| AL Workbench (unchanged container) | Gateway (Windows, customer network) |
| --- | --- |
| Compile, store artifacts, pipelines, approvals, delivery windows | Download one command's files and verify their hashes |
| Command queue, audit, UI, MCP tools | Publish, sync, data upgrade, install, unpublish |
| The on-premises mirror (from heartbeats) | Report instances, versions, tenants, apps, sessions, licence |
| Decide *when* and *whether*; refuse when the gateway is offline | Restart tiers; import licences; enforce the local policy |

## Connection model

- **Outbound HTTPS only.** The gateway calls AL Workbench; nothing calls the gateway. No inbound
  port, no VPN. It uses the machine's proxy settings and certificate store, so a corporate proxy and
  TLS inspection work.
- **Long-poll.** The gateway holds `GET /gateway/commands/next` open for up to 30 seconds; the
  endpoint answers as soon as a command is queued for that gateway (an in-memory wake-up signal per
  gateway, with the table as the source of truth), otherwise empty. A command starts within a second
  or two of being queued.
- **Commands are rows** (`gateway_commands`), not messages. A deployment survives a restart of
  either side, and the table is the audit trail.
- **Heartbeat carries inventory.** The gateway posts what it sees on a regular interval and after
  any command that changes it. The on-premises mirror is *pushed*, where the online one is *pulled*
  by `EnvironmentRefreshScheduler`.
- **Single app instance.** The wake-up signal and the replay cache are in memory, like the build
  queue and the schedulers; `deployment.md` already says to stay on one app instance.

Rejected: a SignalR or WebSocket channel (more connection state for the same result; it can be added
over the same table later if live log streaming needs it), a cloud relay such as Azure Relay (a new
external dependency, CLAUDE.md), inbound access or a VPN (customers will not open it).

## Registration and authentication

1. An org Admin opens **Administration -> Gateways** and presses **Add gateway**. AL Workbench shows
   a **registration code**: random, single use, valid for one hour, shown once, stored hashed
   (`gateway_registration_codes`).
2. On the server, customer IT installs the gateway and runs `ALWorkbench.Gateway register --url
   <workbench> --code <code>`. The gateway generates an **ECDSA P-256 key pair**, keeps the private
   key under the service account's own DPAPI scope (not machine scope, so other processes on the box
   cannot read it), and sends the public key with the code.
3. AL Workbench checks the code, creates the `gateways` row with the public key, marks the code
   used, and returns the gateway id. The exchange is rate-limited per address.
4. From then on every request carries a short-lived JWT the gateway signs itself:
   `iss` = gateway id, `aud` = the workbench origin + `/gateway`, `iat`, `exp` (at most 5 minutes
   after `iat`), `jti`. AL Workbench verifies the signature against the stored public key, the
   audience, the times (2 minutes of clock skew) and refuses a `jti` it has seen before (an in-memory
   cache kept until the token's expiry). No reusable secret ever crosses the wire, and a database
   leak exposes no credential.
5. The **authentication handler** builds a principal with the same organisation claim PAT validation
   sets (`HttpOrganizationContext.OrganizationIdClaim`), plus a gateway-id claim and **no user
   claim**. The EF query filter then scopes everything the gateway endpoints read, exactly as for a
   person. Services called from gateway endpoints must not need a user; their audit actor is the
   gateway ("Gateway CRONUS-BC01").

**Revoke, rotate, retire.** Revoking stamps `revoked_at` and refuses the next request. Rotating is a
new registration code for the same gateway; the new key replaces the old one on exchange. Retiring
revokes it, unlinks its environments and cancels scheduled deployments to them, after a
confirmation that says how many. All three are audited.

**Online and offline.** A gateway is offline after three missed heartbeats (heartbeat every
60 seconds). Admins are notified when it goes offline and when it comes back (a notification
category, `notifications.md`). Commands for an offline gateway expire with "The gateway was
offline" rather than waiting.

## Commands

A command is a row in `gateway_commands`: gateway, typed verb, parameters (JSON), status
`queued -> claimed -> running -> succeeded | failed | expired`, requested by (a user id, with
`started_by_agent` as for deliveries), times, expiry, result (JSON) and log lines. The verb is an
enum shared by both sides through `ALWorkbench.Gateway.Contracts`; an unknown verb is refused by both.
**No verb runs a script or a command line the server sends.**

| Verb | Cmdlets (each instance's own module) | Used by |
| --- | --- | --- |
| `refresh-inventory` | `Get-NAVServerInstance`, `Get-NAVServerConfiguration`, `Get-NAVTenant`, `Get-NAVAppInfo`, `Get-NAVServerSession`, `Export-NAVServerLicenseInformation`, service state | #1365 |
| `publish-install` | `Publish-NAVApp`, `Sync-NAVApp`, `Start-NAVAppDataUpgrade` / `Install-NAVApp`, `Unpublish-NAVApp`, then the restart group | #1375, #1367 |
| `restart-group` | Stop and start the tiers of one database (local service control or remote, below) | #1368, #1371 |
| `restart-instance`, `stop-instance`, `start-instance` | One tier | #1371 |
| `import-licence` | `Import-NAVServerLicense`, then the restart group | #1369 |

**Files.** A command that needs files (an `.app`, a licence) names them with their SHA-256. The
gateway downloads them from `GET /gateway/commands/{id}/files/{name}`, which serves only files that
command names, only to that gateway, and the gateway refuses a hash mismatch.

**Replay and expiry.** Every command has an id and an expiry (default: 15 minutes past its due
time). The gateway refuses an expired command or an id it has run before. A claimed command whose
gateway stops heartbeating is failed after 10 minutes.

**The sweep** that expires and fails commands is a `PolledScheduler` that loops organisations with
`AmbientOrganizationScope`, as `DeliveryScheduler` does, so it adds no `IgnoreQueryFilters()` call.
It and the wake-up signal register with `/healthz/workers`.

## Running the cmdlets

Each operation runs as a **child PowerShell process** that imports the administration module from
that instance's own install folder (read from the Windows service's image path). This is what lets
one gateway manage BC 14 and BC 26 tiers on one machine. Older modules need Windows PowerShell 5.1;
newer ones also load in PowerShell 7; the gateway picks per instance and uses 5.1 when unsure.
Parameters go to the child as data (JSON on standard input), never by building a command string, so
nothing in a command can inject script. The runner sits behind a seam so every operation is
unit-tested on the Linux CI against captured outputs.

## The local policy file

`gateway.toml`, beside the program. **Customer IT owns it; AL Workbench cannot change it.** The
gateway validates it at start and when it changes, and reports its effective content so the
workbench can show what is allowed. A command the file does not allow is refused by the gateway,
whatever the server sent.

```toml
# Service tiers this gateway may touch. Anything not listed is refused.
[[instance]]
machine = "CRONUS-BC01"        # this machine
name = "BC140"
install = true                 # may publish and install
tenant = "default"             # multitenant servers: the tenant to target
publishers = ["CRONUS Partner"] # optional allow-list of app publishers
allow_unsigned = true          # Publish-NAVApp -SkipVerification (our builds are unsigned)
scope = "tenant"               # tenant (default) or global

[[instance]]
machine = "CRONUS-BC02"
name = "BC140"
install = false                # restarted with the group, never published to

# Tiers that share a database: one publishes, all restart after an install.
[[database]]
name = "CRONUS-SQL/BC140"
publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
restart = [
  { machine = "CRONUS-BC01", instance = "BC140" },
  { machine = "CRONUS-BC02", instance = "BC140" },
]
restart_mode = "stop-all-then-start-all"   # or "one-by-one"
before = 'C:\ALWorkbench\before-deploy.ps1' # optional, customer IT's own scripts
after = 'C:\ALWorkbench\after-restart.ps1'

# How to reach other machines.
[[machine]]
name = "CRONUS-BC02"
method = "service-manager"     # or "powershell-remoting"
```

**File permissions are part of the policy.** At start the gateway checks that the policy file and
every script it names are writable only by Administrators and the gateway's service account, and
refuses to start otherwise: whoever can write them gets code running as a Business Central
administrator.

**Remote machines.** In Windows PowerShell 5.1, `Stop-Service` / `Start-Service` have no
`-ComputerName`, but `Get-Service -ComputerName <machine> -Name 'MicrosoftDynamicsNavServer$<instance>'
| Stop-Service` works over the remote service manager. PowerShell 7 removed `-ComputerName` from
`Get-Service`, so `service-manager` steps always run in 5.1. `powershell-remoting` uses
`Invoke-Command -ComputerName` where WinRM is enabled.

## Data model

New tables, all org-scoped with the usual query filter:

| Table | Holds |
| --- | --- |
| `gateways` | Name, public key, registered at, last seen, reported version, machine name, effective policy (JSON), revoked at |
| `gateway_registration_codes` | Code hash, created by, expires at, used at |
| `gateway_commands` | As in "Commands" above |
| `gateway_command_files` | Command id, file name, SHA-256, bytes (or a reference to the artifact row it came from) |
| `gateway_instances` | Gateway, machine, instance name, version, state, database server and name, multitenant, open sessions, licence (licensee, expiry, ranges as JSON with object types), read at, missing since |
| `gateway_instance_tenants` | Instance, tenant id, state |
| `gateway_instance_apps` | Instance, tenant, app id, name, publisher, version, data version, scope, installed, sync state |
| `oe_project_licences` | Solution, file name, bytes (at most 1 MB), SHA-256, uploaded by and at |

The inventory tables are a stable upsert keyed by gateway + machine + instance (+ tenant); an
instance that stops being reported is stamped `missing_since`, never deleted, as
`OeProjectEnvironment` treats environments that vanish.

### An on-premises environment is an `OeProjectEnvironment` row

Decision: linking a service tier to a Solution creates an **`OeProjectEnvironment` row with
`Kind = OnPremises`** that points at `gateway_instance_id` and `tenant_id`. Online rows get
`Kind = Online` (the default, so every existing row is online without a backfill). A separate table
was the alternative; it was rejected because the deployment pipeline's target
(`OeReleasePipeline.EnvironmentId`), `OeEnvironmentApp.EnvironmentId`, the palette and the
Environments list all key on `OeProjectEnvironment`, and a second table would mean two foreign keys
everywhere.

- `(gateway_instance_id, tenant_id)` is **unique** among on-premises rows: one service tier and
  tenant belongs to one Solution. Only an **org Admin** links and unlinks (audited), so the managers of
  one Solution can never deploy to, restart or relicense another customer's tier on a shared
  hosting gateway.
- Name and type (Production or Test) are chosen when linking; `Version`, `Status` and
  `WebClientLoginUrl` are written from the inventory, so pages that read them need no branching.
- For linked rows, every inventory upsert also rewrites the environment's `OeEnvironmentApp` rows
  from `gateway_instance_apps`, so third-party module versions (`CustomerModuleService`) come from
  what is installed, as for online.
- The Microsoft-only columns (update windows from Microsoft, next platform update, storage, soft
  delete, ring, geo) stay null for on-premises rows. Our own delivery window
  (`UpdateWindowStart/End`) applies to both.

### Readers of `OeProjectEnvironments`

Every reader decides whether on-premises rows belong:

| Reader | On-premises rows |
| --- | --- |
| `EnvironmentRefreshScheduler` / `ProjectConnectionService` refresh | **Excluded** - the gateway pushes instead |
| `UpgradeFleetService`, `EnvironmentUpgradeService`, `UpgradeActionService`, `UpgradeActionWorker` (Upgrades page) | **Excluded** - Microsoft's platform updates only |
| `EnvironmentUpdateNotifier` | **Excluded** - Microsoft's update notices only |
| `HeldInstalls` | **Excluded** - Microsoft's deferred schedules only |
| `DeliveryService`, `ReleasePipelineService` | Included, through the delivery seam (#1374, #1367) |
| `PipelinesDashboardService` | Included |
| `ProjectCustomerInfoService` (production facts) | Included - the linked Production instance's version and address |
| `CustomerModuleService` | Included - versions from installed apps |
| `DependencyDriftService` | Included - installed versions are what drift compares against |
| `EnvironmentPaletteSource`, `PaletteContextService` | Included |
| `ProjectBuildService` | Included (licence check, #1370) |
| Environments list and environment page | Included, with only the fields on-premises has |

## What on-premises turns off, revised

`solution-customer-info.md` keeps its rule that an on-premises Solution has no Business Central
**tab** (that tab is the online connection: tenant, Entra app, secret). What changes:

- An on-premises Solution with at least one linked service tier gets its **environments** back: rows
  on the Environments list, the environment page, deployment pipelines that target them, and
  production facts and module versions from the linked Production tier. The typed version, address
  and module versions are kept, not blanked, and come back when the last tier is unlinked (the same
  rule as #907).
- `ProjectCustomerInfoService` refuses an on-premises hosting type today while any environment row
  exists. It changes to: refused while any **online** row exists. The mirror rule is new: switching to
  Microsoft cloud is refused while any **on-premises** row is linked.
- Not coming back: the Upgrades page (Microsoft's platform updates), which stays online-only.

## Deploying to on-premises

The delivery orchestration stays one flow. #1374 moves the *install the apps on this environment*
step of `DeliveryService` behind one interface with two implementations: the Admin Center one
(today's code) and the gateway one. That clears CLAUDE.md's interface bar the way
`IOffsiteStorageProvider` did. Shared, unchanged: claiming, per-app result rows, dependency order,
skip-if-already-installed (#931), failure short-circuit, the delivery window, prepared deployments
and approvals, one deployment per environment at a time.

On-premises differences:

- **Schedules:** only *Now* and *In {environment}'s delivery window*. Microsoft's deferred schedules
  and `handed_off` do not exist on-premises; the service refuses them, not only the editor.
- **Sync mode:** `Add` or `ForceSync` (`Sync-NAVApp -Mode`).
- **Open sessions** are counted across every tier on the database, not only the publishing one.
- **Offline gateway:** a due deployment fails with "The gateway was offline", it does not wait.

The `publish-install` verb, per app in dependency order, on the database's publishing tier:

1. Download the `.app`; verify its SHA-256.
2. `Publish-NAVApp`, tenant scope by default (`-Scope Tenant -Tenant <id>`) so one customer's app is
   not visible to every tenant on a shared server; global only where the policy file says so.
   `-SkipVerification` only when the policy allows unsigned apps. The publisher must be on the
   instance's allow-list when it has one.
3. `Sync-NAVApp -Tenant <id> -Mode <mode>`.
4. Decided from `Get-NAVAppInfo -Tenant <id>` including the data version: an older version installed,
   or uninstalled with its data kept, gets `Start-NAVAppDataUpgrade`; one never installed gets
   `Install-NAVApp`; the same version already installed is skipped.
5. If the pipeline says so (default on), `Unpublish-NAVApp` the previous version, **only when no
   tenant on the server still has it installed**; otherwise it is left, and the result says why.

Then the **restart group** for that database runs, but only if every app succeeded. Nothing is
stopped before publishing: one tier has to be running to publish against. Restarting matters most on
BC 14 hybrid servers, where a session that stays open keeps the old event subscribers. The default
is stop all, then start all, so old and new subscribers never run side by side. The gateway waits for
each tier to report running, with a timeout. Each restart is its own line on the deployment
("Restarted CRONUS-BC02 / BC140"). The Deploy dialog says up front which tiers will be restarted and
that everyone's sessions will drop.

**Before and after scripts** are named in the policy file, never sent by the server. A failing
*before* script stops the deployment before anything is published; a failing *after* script fails
the deployment with the install reported as done. Output goes to the deployment log.

## Licences

**Upload and import (#1369).** On an on-premises environment a person who manages the Solution
uploads the licence file, picks database or tenant (as the policy allows), and confirms that the
listed tiers will restart. The `import-licence` verb runs `Import-NAVServerLicense` on the publishing
tier, then the restart group (tiers only read a new licence on restart), then a fresh inventory.
Format follows the version: `.flf` before BC 22, `.bclicense` from BC 22; a file the target cannot
import is refused with a message naming both. The file follows the Solution's visibility. AL
Workbench never parses licence files; what it shows comes from `Export-NAVServerLicenseInformation`.

**Expiry warning.** The environment page shows the expiry; people following the Solution get a
notification ahead of it (30 days).

**Licensed object ranges (#1370).** Every on-premises version released so far requires an
extension's objects to be covered by the licence; online never does for per-tenant extensions. The
rules:

- Only **full objects** are checked (table, page, report, codeunit, query, xmlport and the other
  full types), each against the range for its own type.
- **Never checked:** enums, interfaces, and extension objects (tableextension, pageextension,
  reportextension, enumextension and the like).
- **BC 14:** an enum's id must fall in a licensed **table** range, so on a BC 14 target enums are
  checked against the table ranges.

A build for a Solution with a linked Production tier gets a **warning** ("Table 50120 is outside the
licence (50000-50099)"); the customer can still buy the object pack. At deploy time the same check
against the target is a **refusal** before anything is uploaded. With no licence read yet, the build
and deployment say "Licence not checked".

## Service tier actions (#1371)

Restart, stop and start a tier, or restart the whole group, from the environment page. The
confirmation shows the open sessions across the database's tiers; Production asks again, as online
Production deliveries do. Manage rights only, audited, and the policy file still decides. Changing
server configuration (`Set-NAVServerConfiguration`) is out of scope.

## MCP parity

- **Reads:** `list_environments`, `get_environment`, `get_solution` and the deployment reads include
  on-premises environments, marked `onPremises: true`, with the gateway's online state.
- **Deploy:** `deploy_build` and `list_deployments` work for on-premises targets **except
  production**. `agents_may_deploy_to_production` does not cover on-premises targets: a deployment
  there also restarts the customer's tiers, and that starts as a person's decision in the web UI.
- **No write tools** for licences or tier actions in this milestone, as environment actions are
  web-only today.

## Security

**What the design protects against:**

- A stolen database row: it holds a public key, nothing usable.
- A replayed or intercepted request: short-lived signed tokens, audience and `jti` checks, TLS.
- A Solution manager reaching another customer: Admin-only linking, one Solution per tier.
- A command the customer did not agree to: the local policy file, which the server cannot change.
- Script injection: no verb carries script; parameters are data.
- Tampered or swapped files in transit: per-command download with hash check.

**What it does not protect against, stated plainly:** a **compromised AL Workbench** can still
install any app on any tier the policy allows. The hash is chosen by the server, and the publisher
allow-list checks what the app says about itself. The real limits are the policy file (which tiers,
install yes or no) and the customer's choice to install a gateway at all. If that is not enough for a
customer, two later options exist: local approval on the gateway before an install, or apps signed
with a certificate the customer trusts and `allow_unsigned = false`. The setup guide says this to
customer IT in plain words.

**Tenant isolation.** Two new `IgnoreQueryFilters()` call sites, both category 1 (pre-auth routing),
approved by the maintainer in #1359:

1. Gateway credential validation: the lookup by `gateways.id == <iss>` with `revoked_at == null`, to
   read the public key and the organisation before any claim exists.
2. The registration-code exchange: the lookup by code hash with `used_at == null` and not expired.

Nothing else on the gateway path crosses the fence: the expiry sweep uses `AmbientOrganizationScope`
per organisation, and the gateway endpoints run under the organisation claim from the credential.

**Signed binaries.** Releases are signed through Azure's managed code signing service (Trusted
Signing, now Artifact Signing). Until it is set up, builds ship unsigned and marked preview only.
The gateway does not update itself; the workbench shows "Out of date" and customer IT updates it.

## Projects, packaging and naming

| Project | Target | Holds |
| --- | --- | --- |
| `ALWorkbench.Gateway.Contracts` | `net10.0` | DTOs, the verb enum, protocol version; referenced by the app and the gateway |
| `ALWorkbench.Gateway.Core` | `net10.0` | All gateway logic behind seams (policy, runner, poll loop); tested on Linux CI |
| `ALWorkbench.Gateway` | `net10.0-windows` | Thin Windows Service host, DPAPI, event log; `EnableWindowsTargeting` so the solution still builds on Linux |

These projects use the product name, not the `ALDevToolbox` plumbing spelling. The
"AL Workbench in the product" rule in CLAUDE.md protects *existing* identifiers; new projects have
none to protect, and the gateway's exe, Windows service ("AL Workbench Gateway") and event log
source are read by customer IT. The app keeps `ALDevToolbox`. CLAUDE.md's naming table records this.

The Dockerfile's restore layer copies the contracts csproj alongside `ALDevToolbox.csproj`. The
release workflow gains a Windows job that publishes the gateway self-contained single-file for
`win-x64`, signs it and attaches it to the release; the Gateways page links the build matching the
running version with its SHA-256. The contracts carry a minimum supported gateway version; an older
gateway is refused with a message saying to update.

## Phasing

| Step | Issue |
| --- | --- |
| This document | #1359 |
| Gateways page, registration, authentication | #1360 |
| Command queue and endpoints | #1361 |
| Windows service frame | #1362 |
| Policy file and PowerShell runner | #1373 |
| Signed download with each release | #1363 |
| Inventory - the read-only spike, on our own hosting first | #1365 |
| On-premises environments on the Solution | #1366 |
| Delivery seam (no behaviour change) | #1374 |
| The `publish-install` verb | #1375 |
| Deployment pipelines to on-premises | #1367 |
| Restart group, before and after scripts | #1368 |
| Licence upload, import, expiry | #1369 |
| Licence range check | #1370 |
| Service tier actions | #1371 |
| Setup guide for customer IT | #1376 |

## Decisions (resolved)

- **Name:** "On-premises gateway", never "agent".
- **Connection:** outbound HTTPS long-poll; commands as rows; inventory pushed on heartbeat.
- **Credential:** gateway-generated key pair, signed short-lived tokens; no shared secret.
- **Scope:** a gateway belongs to the organisation and may serve many Solutions; each tier + tenant
  is linked to exactly one Solution, by an Admin.
- **Storage:** an on-premises environment is an `OeProjectEnvironment` row with `Kind = OnPremises`.
- **AI assistants:** may read on-premises environments and deploy to non-production ones; on-premises
  production is web-only.
- **Fences:** the two `IgnoreQueryFilters()` call sites above, the new credential, the Windows
  release artifact and the table-backed command queue are approved (#1359).
- **Signing:** Azure's managed code signing service.
- **Licence formats:** `.flf` before BC 22, `.bclicense` from BC 22.
- **Project names:** `ALWorkbench.Gateway`, `.Core`, `.Contracts`.

## Out of scope

- Platform (cumulative update) upgrades and database conversion. A different, riskier job.
- Any verb that runs a script or command line from the server.
- Changing server configuration keys; company management; user and permission management.
- C/AL object delivery. Deploy is for AL apps (BC 14 and later, including hybrid). Older NAV servers
  may report inventory only.
- An MSI installer and gateway self-update.
- One gateway managing servers in another network over the internet; remote machines are reached
  only inside the customer's network.
