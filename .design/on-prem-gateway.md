# On-premises gateway - managing on-premises Business Central from AL Workbench

> **Status: being built.** Contracts, registration, the command queue (#1360, #1361) and the Windows service frame (#1362) exist; the policy file, the runner and the verbs do not yet. Milestone #8, issues #1359 to #1376. Built on the
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
- **Inventory is pushed.** The gateway posts what it sees every 15 minutes and after any command
  that changes it; the 60-second heartbeat only says it is alive. The workbench writes only what
  changed. The on-premises mirror is *pushed*, where the online one is *pulled*
  by `EnvironmentRefreshScheduler`.
- **Single app instance.** The wake-up signal is in memory, like the build queue and the
  schedulers; `deployment.md` already says to stay on one app instance.

Rejected: a SignalR or WebSocket channel (more connection state for the same result; it can be added
over the same table later if live log streaming needs it), a cloud relay such as Azure Relay (a new
external dependency, CLAUDE.md), inbound access or a VPN (customers will not open it).

## Registration and authentication

1. An org Admin opens **Administration → Gateways** and presses **Add gateway**. AL Workbench shows
   a **registration code**: random, single use, valid for one hour, shown once, stored hashed
   (`gateway_registration_codes`).
2. On the server, customer IT installs the gateway and runs `ALWorkbench.Gateway register --url
   <workbench> --code <code>`. The gateway generates an **ECDSA P-256 key pair**, keeps the private
   key non-exportable in the Windows key store (the TPM where the machine has one), readable only by
   the gateway's service account. Local administrators and SYSTEM can still reach it, as they can
   reach anything on the box; nothing else can. It sends the public key with the code.
3. AL Workbench checks the code, creates the `gateways` row with the public key in a **pending**
   state, marks the code used, and returns the gateway id. The exchange is rate-limited per client
   address (the forwarded address behind Caddy, not the proxy's).
4. **An Admin approves it.** The Gateways page shows the pending gateway's machine name and a short
   fingerprint of its key; the gateway prints the same fingerprint when it registers. Until an Admin
   compares them and presses Approve, the gateway can heartbeat but receives no commands and no
   files. A leaked registration code therefore cannot quietly enrol a rogue machine. Approval is
   bound to the fingerprint the Admin compared: if the gateway re-keyed in the meantime, Approve is
   refused and the Admin compares again.
5. From then on every request carries a JWT the gateway signs itself, one per request:
   `iss` = gateway id, `aud` = the workbench origin + `/gateway`, `iat`, `exp` (at most 60 seconds
   after `iat`), `jti`, and the request it is for: `htm` (method), `htu` (path) and, for a request
   with a body, `bh` (SHA-256 of the body). AL Workbench verifies the signature against the stored
   public key and every claim before it reads the body, then compares the body hash, and refuses
   a `jti` it has seen before. An unknown gateway and a bad signature get the same refusal, and
   every gateway route carries a per-address rate limit. Seen `jti`s are kept in a small table until they expire
   (`gateway_seen_tokens`, swept with the commands), so a restart of the app does not open a replay
   window. No reusable secret ever crosses the wire, and a database leak exposes no credential.
6. The **authentication handler** is its own scheme, registered like the PAT scheme and opted into
   only by the gateway routes through a policy that requires the gateway-id claim. No cookie or MCP
   policy accepts it, and the gateway routes accept nothing else. It builds a principal with the
   same organisation claim PAT validation sets (`HttpOrganizationContext.OrganizationIdClaim`), plus a gateway-id claim and **no user
   claim**. The EF query filter then scopes everything the gateway endpoints read, exactly as for a
   person. Services called from gateway endpoints must not need a user; the principal's name is the
   gateway's ("Gateway CRONUS-BC01"), which is what the audit interceptor records as the actor, with
   no user id.

**Revoke, rotate, retire.** Revoking stamps `revoked_at`, refuses the next request and fails the
gateway's unfinished commands. Rotating is a
new registration code bound to that gateway's id; on exchange the new key replaces the old one, which
is refused from then on, and the gateway goes back to pending until an Admin approves the new
fingerprint. Only the newest rotation code for a gateway works: issuing or using one makes the older
ones useless. Retiring
revokes it, unlinks its environments (below) and cancels scheduled deployments to them, after a
confirmation that says how many. All three are audited.

**Online and offline.** The heartbeat is light: every 60 seconds the gateway says it is alive and
which command ids it is running. A gateway is offline after three missed heartbeats. The command
sweep detects the transitions and notifies the organisation's Admins when a gateway goes offline and
when it comes back, under a new notification category **Gateways** (`NotificationCategory.Gateways`,
labelled "Gateways", `notifications.md`). Commands for an offline gateway expire with "The gateway
was offline" rather than waiting.

Each change is told once: the sweep claims `gateways.notified_online` with a compare-and-set before
it sends, as `UpgradeCheckNotifier` does. A gateway's first connection is recorded without a notice,
and one that was never heard from is not "offline" (it is still being set up). The category goes to
Admins only, so the account page offers its row to Admins only.

## Commands

A command is a row in `gateway_commands`: gateway, typed verb, parameters (JSON), status
`queued -> claimed -> running -> succeeded | failed | expired`, requested by (a user id, with
`started_by_agent` as for deliveries), times, expiry, result (JSON) and log lines. The verb is an
enum shared by both sides through `ALWorkbench.Gateway.Contracts`; an unknown verb is refused by both.
**No verb runs a script or a command line the server sends.**

As built (#1361), in `Services/Gateways/GatewayCommandService.cs`:

- The verb travels, and is stored, as its kebab-case word (`publish-install`). The contracts' own
  JSON converter refuses any other string, a number or a different case, so an unknown verb fails
  deserialisation of the whole message on either side.
- `GET /gateway/commands/next` answers 403 to a gateway that is not approved, 204 when nothing came
  within 30 seconds, and otherwise the claimed command. Claiming is a compare-and-set update on a
  `queued` row (the loser of a race moves on to the next row), all under the query filter.
- `POST /gateway/commands/{id}/progress` appends lines to one `log` text column, each stamped with
  the workbench's clock; the first post moves `claimed` to `running`. `POST
  /gateway/commands/{id}/result` records `succeeded` or `failed` with the result object and a reason.
  Both answer 409 once the command is no longer the caller's to report on (finished, expired, or
  failed by the sweep), so a late report never overwrites the first outcome, and 403 to a gateway
  that is not approved. The log stops at 1,000,000 characters with one "[Log truncated...]" line.
- A heartbeat's running and interrupted lists are applied only for an approved gateway.
- Files are stored with the command (`gateway_command_files.content`), not referenced from the
  artifact row. #1375 decides whether command files reference the build's artifacts instead. A file
  is at most 128 MB (an .app the BC admin API accepts is at most 50 MB), the download is streamed
  rather than buffered, and the sweep drops the content once the command is final, keeping its
  name, hash and size.
- Revoking a gateway fails its queued, claimed and running commands at once with "The gateway was
  revoked.", rather than leaving them to wait out a claim deadline that may be days away. Rotating
  one does the same with its own message, but from the next sweep (within 30 seconds) rather than
  in the exchange: the exchange runs before anyone is signed in and cannot reach the organisation's
  commands.
- Every route but `/register` shares the per-address limit on the `/gateway` group (600 burst,
  300 a minute). One gateway's steady traffic is a heartbeat a minute and a long-poll every 30
  seconds; the gateway batches progress lines (a post every few seconds at most, up to 200 lines
  each), so even a deployment stays far below it, with room for several gateways behind one address.

| Verb | Cmdlets (each instance's own module) | Used by |
| --- | --- | --- |
| `refresh-inventory` | `Get-NAVServerInstance`, `Get-NAVServerConfiguration`, `Get-NAVTenant`, `Get-NAVAppInfo`, `Get-NAVServerSession`, `Export-NAVServerLicenseInformation`, service state | #1365 |
| `publish-install` | `Publish-NAVApp`, `Sync-NAVApp`, `Start-NAVAppDataUpgrade` / `Install-NAVApp`, `Unpublish-NAVApp`, then the restart group | #1375, #1367 |
| `restart-group` | Stop and start the tiers of one database (local service control or remote, below) | #1368, #1371 |
| `restart-instance`, `stop-instance`, `start-instance` | One tier | #1371 |
| `import-licence` | `Import-NAVServerLicense`, then the restart group | #1369 |

**Files.** A command that needs files (an `.app`, a licence) names them by file id with their
SHA-256. The gateway downloads them from `GET /gateway/commands/{id}/files/{fileId}`, which checks
explicitly, beyond the organisation filter, that the command's gateway is the caller, that the
command is claimed or running and not expired, and that the file belongs to that command. The
gateway refuses a hash mismatch.

**Expiry.** Every command must be *claimed* by its expiry: 15 minutes past its due time, or the end
of the delivery window it was scheduled in, whichever is earlier. Only the server's clock judges
that: the command carries the seconds it has left, and the gateway refuses one that has run out by
its own count from receipt, never by comparing clocks. A due deployment whose gateway is offline
therefore fails at its expiry with "The gateway was offline", and never starts after its window.
Once claimed, a command is not timed out while the gateway keeps heartbeating it as running, because
a data upgrade can legitimately take a long time. A claimed command whose gateway stops heartbeating
is failed after 10 minutes of silence.

Silence is measured per command (`last_reported_at`, moved by the claim, each progress post and each
heartbeat that lists it), not only per gateway. That also catches a command whose claim answer never
reached the gateway: the gateway keeps heartbeating but never lists it. The two cases fail with
different messages: "The gateway stopped responding while this was running" when the gateway itself
went quiet, "The gateway stopped reporting on this while it was running" when only the command did.
Neither is ever sent again.

**Run once, across restarts.** The gateway keeps the ids it has started in a local file, so it never
runs one twice, including after it restarts. If it restarts in the middle of a command, its first
heartbeat reports that id as interrupted; the server marks the command failed with "The gateway
restarted while this was running; check the environment", asks for a fresh inventory, and never
sends it again. A person decides whether to deploy again. (The fresh inventory is queued once the
`refresh-inventory` verb exists, #1365; #1361 only fails the command.)

**The sweep** that expires and fails commands is a `PolledScheduler` that loops organisations with
`AmbientOrganizationScope`, as `DeliveryScheduler` does, so it adds no `IgnoreQueryFilters()` call.
It registers with `/healthz/workers`. As built it is `GatewayCommandSweep`, every 30 seconds; it
also clears spent `gateway_seen_tokens` and sends the online/offline notices.
`DISABLE_GATEWAY_COMMAND_SWEEP=1` turns it off, and then nothing expires and nobody is told.

## Running the cmdlets

Each operation runs as a **child PowerShell process** that imports the administration module from
that instance's own install folder (read from the Windows service's image path). This is what lets
one gateway manage BC 14 and BC 26 tiers on one machine. Older modules need Windows PowerShell 5.1;
newer ones also load in PowerShell 7; the gateway picks per instance and uses 5.1 when unsure.
Parameters go to the child as data (JSON on standard input), never by building a command string, so
nothing in a command can inject script. The runner sits behind a seam so every operation is
unit-tested on the Linux CI against captured outputs. Most cmdlets return objects the child turns into
JSON; `Export-NAVServerLicenseInformation` returns text, so its parser (licensee, expiry, object
ranges per type) is part of the gateway and tested against captured licences from BC 14 and a current
version.

Every cmdlet that acts on one app is given `-Name`, `-Publisher` and `-Version`, because several
versions of an app are published at once during an upgrade.

## The local policy file

`gateway.toml`, beside the program. **Customer IT owns it; AL Workbench cannot change it.** The
gateway validates it at start and when it changes, and reports its effective content so the
workbench can show what is allowed. A command the file does not allow is refused by the gateway,
whatever the server sent.

```toml
# Defaults per kind of environment; a tier inherits them unless it sets its own.
[defaults.production]
allow = ["inventory", "publish", "restart"]
change_hours = "20:00-06:00"   # optional: changes only in this local time window (reads any time)

[defaults.test]
allow = ["inventory", "publish", "unpublish", "restart", "service_control", "licence"]

# Service tiers this gateway may touch. Anything not listed is refused.
[[instance]]
machine = "CRONUS-BC01"        # this machine
name = "BC140"
environment = "production"     # production (default), test or development
allow = ["inventory", "publish", "unpublish", "restart", "licence"]   # overrides the production default
tenants = ["default"]          # multitenant servers: the tenants this gateway may target
publishers = ["CRONUS Partner"] # optional allow-list of app publishers
allow_unsigned = true          # Publish-NAVApp -SkipVerification (our builds are unsigned)
scope = "tenant"               # tenant (default) or global

[[instance]]
machine = "CRONUS-BC02"
name = "BC140"
environment = "production"
allow = ["inventory", "restart"] # restarted with the group, never published to

# Tiers that share a database: one publishes, all restart after an install.
[[database]]
name = "CRONUS-SQL/BC140"
publish_on = { machine = "CRONUS-BC01", instance = "BC140" }
restart = [
  { machine = "CRONUS-BC01", instance = "BC140" },
  { machine = "CRONUS-BC02", instance = "BC140" },
]
restart_mode = "stop-all-then-start-all"   # or "one-by-one"
shared_restart = false         # see "Shared multitenant tiers"; refuse restarts that reach other Solutions
licence_target = "NavDatabase" # Import-NAVServerLicense -Database: NavDatabase, Master, Default or Tenant
before = 'C:\ALWorkbench\before-deploy.ps1' # optional, customer IT's own scripts
after = 'C:\ALWorkbench\after-restart.ps1'

# How to reach other machines.
[[machine]]
name = "CRONUS-BC02"
method = "service-manager"     # or "powershell-remoting"
```

**Production, test or development is customer IT's call.** Each instance says what it is with
`environment`; one that does not say is treated as `production`, the safe default. AL Workbench
takes this from the gateway and does not let anyone override it: the Admin linking a tier sees the
type but cannot change it, and if customer IT changes it, the next inventory changes it in the
workbench too. Everything that treats production specially follows it: the extra confirmation on
deployments and tier actions, the refusal of AI-assistant deployments, and the Solution's version
and address, which come from its production tier.

**Permissions per service tier.** Each instance's permissions come from its own `allow` and
`change_hours` if it sets them, otherwise from `[defaults.<environment>]` for its kind, otherwise
`["inventory"]` with no hours limit: read-only. A tier's own value replaces the default whole, never
merges with it, so what a tier may do is always one list in one place. Anything not in the effective
list is refused. The workbench
shows the effective permissions on the environment page and greys out what a tier does not allow,
with "Not allowed by the customer's gateway settings".

| Permission | Lets the gateway |
| --- | --- |
| `inventory` | Read state, versions, tenants, apps, session counts and licence information |
| `session_users` | Also report *who* is signed in, not only how many (personal data, so off unless listed) |
| `publish` | Publish, sync, install and upgrade apps (`Publish-NAVApp`, `Sync-NAVApp`, `Install-NAVApp`, `Start-NAVAppDataUpgrade`) |
| `unpublish` | Remove an app's previous version after an upgrade |
| `restart` | Restart the tier as part of a deployment or licence import (the restart group) |
| `service_control` | Stop, start and restart the tier on demand from the environment page |
| `licence` | Import a licence file |
| `scripts` | Run the before and after scripts named for its database |

`change_hours` (per kind or per tier) limits every permission except `inventory` to a local time window, on top of the
delivery window set in AL Workbench, so customer IT can rule out daytime changes whatever the
workbench asks. A command outside it is refused with "Outside the hours set on the customer's
gateway".

**File permissions are part of the policy.** At start, and before each command, the gateway checks
that the program folder, the policy file and every script it names are writable only by
Administrators. The service account may read them but not write them: a compromised gateway process
must not be able to widen its own policy. The gateway refuses to start otherwise, because whoever
can write them gets code running as a Business Central administrator.

**Shared multitenant tiers.** On a multitenant server several tenants, often several customers,
share the service tiers and the application database. Linking is per tenant, but a restart or a
licence imported into the application database reaches every tenant on those tiers. So: the Deploy,
licence and restart dialogs list every Solution whose environments sit on the tiers involved; the
gateway refuses a restart group or an application-level licence import that reaches tiers linked to
more than one Solution unless that database sets `shared_restart = true`; and a deployment to one
tenant of a shared server is published tenant-scoped (below) so the app itself never reaches the
others.

**Remote machines.** The service account needs remote service control rights on each machine
(remote procedure call and file sharing reachable inside the network), or PowerShell remoting where
that method is chosen; the setup guide (#1376) says this. In Windows PowerShell 5.1, `Stop-Service` / `Start-Service` have no
`-ComputerName`, but `Get-Service -ComputerName <machine> -Name 'MicrosoftDynamicsNavServer$<instance>'
| Stop-Service` works over the remote service manager. PowerShell 7 removed `-ComputerName` from
`Get-Service`, so `service-manager` steps always run in 5.1. `powershell-remoting` uses
`Invoke-Command -ComputerName` where WinRM is enabled.

## Data model

New tables, all org-scoped with the usual query filter:

| Table | Holds |
| --- | --- |
| `gateways` | Name, public key and its fingerprint, state (pending, approved), registered at, approved by and at, last seen, reported version, machine name, effective policy (JSON), revoked at, the online state last announced to Admins |
| `gateway_registration_codes` | Code hash, created by, expires at, used at, gateway id (set for a rotation code) |
| `gateway_seen_tokens` | Gateway, `jti`, expires at (replay protection; swept with the commands) |
| `gateway_commands` | As in "Commands" above |
| `gateway_command_files` | Id, command id, file name, SHA-256, bytes (or a reference to the artifact row it came from) |
| `gateway_instances` | Gateway, machine, instance name, environment (production, test, development, from the policy file), version, state, database server and name, multitenant, open sessions, licence (licensee, expiry, ranges as JSON with object types), read at, missing since |
| `gateway_instance_tenants` | Instance, tenant id, state |
| `gateway_instance_apps` | Instance, tenant, app id, name, publisher, version, data version, scope, installed, sync state |
| `oe_project_licenses` | Solution, file name, bytes (at most 1 MB), SHA-256, uploaded by and at (code spells it `License`, as `OeProject.LicenseType` does; copy says licence) |

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

- `(gateway_instance_id, tenant_id)` is **unique** among on-premises rows: a partial unique index
  filtered to `kind = 'OnPremises'`, with a single-tenant server stored as tenant `default` rather
  than null so the index cannot be dodged. One service tier and tenant belongs to one Solution. Only an **org Admin** links and unlinks (audited), so the managers of
  one Solution can never deploy to, restart or relicense another customer's tier on a shared
  hosting gateway.
- The name is chosen when linking and is unique per Solution, because the existing unique index on
  `(project_id, name)` covers both kinds. The type is **not** chosen: it comes from the instance's
  `environment` in the policy file (`gateway_instances.environment`), rewritten by every inventory.
  It is stored as `Production` or `Sandbox`, the values the existing checks compare against
  (`BcContracts`, `ArtifactService`, `CustomerModuleService`, `DependencyDriftService`): `production`
  maps to `Production`, `test` and `development` to `Sandbox`. On-premises copy shows the policy's
  own word, "Test" or "Development".
  `Version`, `Status` and `WebClientLoginUrl` are written from the inventory, so pages that read
  them need no branching.
- **Unlinking does not delete the row.** A deployment pipeline's foreign key to the environment is
  `Restrict`, so an unlinked row is stamped `MissingSince` (shown as "no longer linked") and its
  `OeEnvironmentApp` rows are removed. Pipelines that point at it show it as gone and refuse to
  deploy, as for an online environment the customer deleted. Linking the same tier again revives
  the row.
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
| `EnvironmentRefreshScheduler` / `ProjectConnectionService` refresh | **Excluded** - filters `Kind = Online` before upserting and before stamping `MissingSince`, so a refresh never marks a linked tier missing |
| `BcUpdateScheduleChange`, `EnvironmentQueries` (the panel and update reads) | **Excluded** |
| `UpgradeFleetService`, `EnvironmentUpgradeService`, `UpgradeActionService`, `UpgradeActionWorker` (Upgrades page) | **Excluded** - Microsoft's platform updates only |
| `EnvironmentUpdateNotifier` | **Excluded** - Microsoft's update notices only |
| `HeldInstalls` | **Excluded** - Microsoft's deferred schedules only |
| `DeliveryService`, `ReleasePipelineService` | Included, through the delivery seam (#1374, #1367) |
| `PipelinesDashboardService` | Included |
| `ProjectCustomerInfoService` (production facts) | Included, with a new predicate: today `ReadProductionFactsAsync` requires `MicrosoftCloud` hosting and a configured connection (`ConfiguredProjects`); it becomes *(online, configured, Production row)* or *(on-premises, linked Production row)* |
| `ArtifactService` (what is shipped to production) | Included |
| `ProjectBuildService` missing-dependency report | Included |
| `CustomerModuleService` | Included - versions from installed apps |
| `DependencyDriftService` | Included - installed versions are what drift compares against |
| `EnvironmentPaletteSource`, `PaletteContextService` | Included |
| `ProjectBuildService` | Included (licence check, #1370) |
| Environments list and environment page | Included, with only the fields on-premises has |
| MCP tools (`DeliverTools`, the environment and deployment reads) | Included, marked `onPremises` |

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

The delivery orchestration stays one flow. #1374 puts everything in `DeliveryService` that talks
to Business Central behind one interface with two implementations, the Admin Center one (today's
code) and the gateway one: the live status re-read before uploading, the installed-apps read, the
downgrade refusal that reads it, and the install of each app. That clears CLAUDE.md's interface bar the way
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
4. Decided from `Get-NAVAppInfo -Tenant <id> -TenantSpecificProperties`, which reports the data
   version: an older version installed,
   or uninstalled with its data kept, gets `Start-NAVAppDataUpgrade`; one never installed gets
   `Install-NAVApp`; the same version already installed is skipped.
5. If the pipeline says so (default on), `Unpublish-NAVApp` the previous version: with `-Tenant` for
   a tenant-scoped app; for a globally published one, **only when no tenant on the server still has
   it installed**, otherwise it is left and the result says why.

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
tier with `-Database` set as the policy's `licence_target` says (`NavDatabase`, `Master`, `Default`,
or `Tenant` with `-Tenant`), then the restart group (tiers only read a new licence on restart), then
a fresh inventory. A database-level import on a shared server reaches every tenant and follows the
shared-tier rule above. Format: `.flf` before BC 22, `.bclicense` from BC 22. Whether BC 22 and later
still accept an `.flf` is checked against a real server when #1369 is built; until then a file
the target's version cannot import is refused with a message naming both, and that rule follows the
finding. The file follows the Solution's visibility. AL
Workbench never parses licence files; what it shows is the gateway's parse of
`Export-NAVServerLicenseInformation`.

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
- A leaked registration code: the gateway it enrols waits for an Admin to approve its fingerprint.
- A replayed or intercepted request: one signed token per request, bound to the method, path and
  body, 60 seconds long, `jti` remembered across restarts, TLS.
- A compromised gateway process widening its own policy: the policy and scripts are read-only to
  the service account.
- A Solution manager reaching another customer: Admin-only linking, one Solution per tier.
- A command the customer did not agree to: the local policy file, which the server cannot change.
- Script injection: no verb carries script; parameters are data.
- Tampered or swapped files in transit: per-command download with hash check.

**What it does not protect against, stated plainly:** a **compromised AL Workbench** can still
install any app on any tier the policy allows. The hash is chosen by the server, and the publisher
allow-list checks what the app says about itself. The real limits are the policy file (which tiers,
what each tier allows, the hours changes may run) and the customer's choice to install a gateway at all. If that is not enough for a
customer, two later options exist: local approval on the gateway before an install, or apps signed
with a certificate the customer trusts and `allow_unsigned = false`. The setup guide says this to
customer IT in plain words.

**Tenant isolation.** Two new `IgnoreQueryFilters()` call sites, both category 1 (pre-auth routing),
approved by the maintainer in #1359. Each carries its one-line category comment and moves the
per-file baseline in `IgnoreQueryFiltersBaselineTests`:

1. Gateway credential validation: the lookup by `gateways.id == <iss>` with `revoked_at == null`, to
   read the public key and the organisation before any claim exists.
2. The registration-code exchange: the lookup by code hash with `used_at == null` and not expired.

The replay table's `jti` check and the file download run under the organisation claim, and the
download also checks the command's gateway against the caller explicitly.

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
| `ALWorkbench.Gateway` | `net10.0-windows` | Thin Windows Service host, the Windows key store, event log; `EnableWindowsTargeting` so the solution still builds on Linux |

These projects use the product name, not the `ALDevToolbox` plumbing spelling. The
"AL Workbench in the product" rule in CLAUDE.md protects *existing* identifiers; new projects have
none to protect, and the gateway's exe, Windows service ("AL Workbench Gateway") and event log
source are read by customer IT. The app keeps `ALDevToolbox`. CLAUDE.md's naming table records this, and its "Project at a glance"
and the `PROJECT.md` folder map list the gateway projects next to the app and its tests once they
exist (#1361, #1362).

The Dockerfile's restore layer copies the contracts csproj alongside `ALDevToolbox.csproj`;
`.dockerignore` leaves the other two gateway projects out of the image's build context. The
release workflow gains a Windows job that publishes the gateway self-contained single-file for
`win-x64`, signs it and attaches it to the release; the Gateways page links the build matching the
running version with its SHA-256. The contracts carry a minimum supported gateway version; an older
gateway is refused with a message saying to update.

### The Windows service, as built (#1362)

The frame: it registers, keeps its key, heartbeats, polls, downloads and checks files, and reports.
It runs no verb yet; every command fails with "This version of the gateway cannot run '<verb>'
commands yet" until #1365 onward plug executors in behind `ICommandExecutor`.

**Commands customer IT runs** (from an administrator prompt), in either order:

- `install [--account DOMAIN\name$]` creates the service `ALWorkbenchGateway` ("AL Workbench
  Gateway"), delayed automatic start, restarted by Windows after a crash but not after a clean stop.
  It runs as its own **virtual account** `NT SERVICE\ALWorkbenchGateway` by default: no password,
  not an administrator, the computer account on the network. A **group managed service account**
  can be named instead, for when the gateway must act on other machines as a domain identity
  (#1373); it must be installed on the computer and hold "Log on as a service". An ordinary user
  account is refused, because the gateway never stores a password. Which Business Central rights
  the account needs is the setup guide's (#1376). `install` uses `sc.exe`, so `sc qc
  ALWorkbenchGateway` shows exactly what it did.
- `register --url <address> --code <code>` makes a key, exchanges the code, checks that the
  fingerprint AL Workbench stored is the one of the key it made (a mismatch means something on the
  path changed the request, and it says not to approve), prints the fingerprint for the Admin to
  compare, and restarts the service if it is installed. A rotation code is registered the same way:
  the new key is made beside the old one, which is deleted only once AL Workbench has accepted the
  new one. The address must be `https://` (plain `http` only for the computer itself) and is the
  workbench's `PUBLIC_BASE_URL`, path included, because the token audience is built from it.
- `uninstall` stops and removes the service, deletes the key, the settings and the state, removes
  the event log source, keeps the log files, and reminds them to have an Admin revoke the gateway.

**The key.** A CNG machine key, ECDSA P-256, non-exportable, created in the Microsoft Platform
Crypto Provider (the TPM) and, if that fails, in the Microsoft Software Key Storage Provider;
`register` prints which. Its access list is Administrators and SYSTEM full control and the service
account read (enough to sign). The virtual account's SID is computed from the service name, so
`register` can grant the key before the service exists; `install` re-grants it when the account
changes.

**Files**, in `%ProgramData%\AL Workbench Gateway` (`GatewayDataFolder`), which does not inherit
`%ProgramData%`'s rights: `gateway.json` (address, gateway id, key name and fingerprint, service
account; nothing secret) writable by administrators only; `state\started-commands.json` (the
run-once list), `work\<command id>\` (a command's downloaded files, removed when it ends) and
`logs\` writable by the service account too.

**The loop** (`GatewayRunner`) is one loop, so the order is plain:

- The first heartbeat goes before any poll, and carries the ids the run-once file lists as started
  but never finished: the interrupted ones. Once a heartbeat is answered as approved (AL Workbench
  ignores a pending gateway's lists, so a pending one keeps sending them) they are marked finished
  locally and never reported or run again.
- Heartbeats every 60 seconds, also while a command runs, listing it as running.
- While the gateway is not approved (the heartbeat says so, or a poll answers 403) it does not poll
  at all; it heartbeats each minute and polls after the first heartbeat that says it is approved.
- One command at a time. An empty poll that comes back in under two seconds is followed by a wait,
  so a proxy that answers at once cannot turn the loop into a stream of requests.
- AL Workbench unreachable (no connection, a timeout, 407, 408, 429, a 5xx) or answering 400: back
  off 2, 4, 8 ... seconds up to five minutes, each wait drawn from the upper half of its step.
- **401 does not stop the gateway.** Revoked and "this computer's clock is far off" both answer
  401, and a drifted clock must not leave the customer with a dead service. Each refused attempt
  logs one error (the workbench's reason, and what to do for a clock or a revocation) and the loop
  retries on the same back-off, so a gateway left refused writes a line every five minutes. A 401
  on a progress or result post is retried the same way; the result is kept until it arrives.

**A command**, once claimed:

1. Refused if the run-once file already has it ("already started this command once").
2. Refused if its seconds left are 0, or have run out by the gateway's monotonic clock since the
   answer arrived. It is recorded as started-and-finished, so it is never considered again.
3. Recorded as started, on disk, before anything else happens.
4. Files downloaded into its work folder, each stopped as soon as it exceeds its announced size,
   then checked by size and SHA-256. Any mismatch fails the command; the verb never sees the file.
5. The executor runs. Its progress lines are batched: at most one post every 3 seconds, at most 200
   lines a post, lines over 2,000 characters cut, at most 10,000 lines held (more are counted and
   reported as left out). A 409 on progress stops the verb.
6. The result is posted, retried with back-off until it arrives, including through a 401 or a 403 while a new key waits for approval (a 409 means AL Workbench already
   closed it, and is accepted), then the command is marked finished.

A stop in the middle leaves the command started-not-finished, which the next start reports as
interrupted.

**The local log**: the Windows event log (Application, source "AL Workbench Gateway", created by
`install`) and a file per day in `logs\`, kept 30 days. Each command gets a line when it starts and
when it ends, naming the verb, who asked for it, and the outcome, with fixed event ids (1000
started, 1001 succeeded, 1002 failed, 1003 refused, 1004 interrupted) so customer IT can filter on
them. "Who asked" travels with the command: `GatewayCommandMessage.requestedBy` (the requester's
display name) and `startedByAgent`, added for this; an older gateway ignores them.

**Proxy and certificates.** One `HttpClient` with the system proxy (WinHTTP / Internet Options, or
`HTTPS_PROXY`), the service account's own credentials for a proxy that asks, and Windows' certificate
validation, so TLS inspection with the company's root certificate works.

Tests: the core library's in `ALDevToolbox.Tests/Gateways/` against a fake workbench and a clock
whose waits return at once, and one end-to-end test in `Endpoints/GatewayServiceFrameEndpointTests`
that drives the gateway's own code against the real `/gateway` routes. The Windows host has no
automated tests: the key store, `sc.exe` and the event log need Windows.

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
