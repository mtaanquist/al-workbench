# On-premises gateway - managing on-premises Business Central from AL Workbench

> **Status: being built.** Contracts, registration, the command queue (#1360, #1361), the Windows service frame (#1362), the policy file and PowerShell runner (#1373), the download with each release (#1363), the inventory (#1365), linking service tiers to a Solution (#1366), the delivery seam (#1374), the gateway side of `publish-install` (#1375), deploying through the gateway from AL Workbench (#1367), the restart group with the customer's before and after scripts (#1368), uploading and importing a licence, with its expiry notices (#1369), checking a build's objects against the customer's licence on builds and deployments (#1370), restarting, stopping and starting service tiers from the environment page (#1371), and the setup guide for customer IT at `/docs/gateway-setup` (#1376), exist; the other verbs do not yet. Milestone #8, issues #1359 to #1376. Built on the
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
- A progress post carries its batch number (`sequence`, 1 upward per command). The command keeps the
  highest one appended (`last_progress_sequence`); a batch at or below it is a resend after a lost
  answer, accepted without appending its lines again. A post without a number (0) is always appended.
- A heartbeat's running and interrupted lists are applied only for an approved gateway.
- Files are stored with the command (`gateway_command_files.content`), not referenced from the
  artifact row. Since #1367 a command built from a deployment references the build's artifacts
  instead (`project_build_artifact_id`; see "Deploying to on-premises, as built"); the gateway only
  sees a file id and its hash either way. A file
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
sends it again. A person decides whether to deploy again. (As built, the server queues nothing for
the fresh inventory: a gateway that restarted reads every tier as its first act anyway; see
"Inventory, as built".)

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
JSON; `Export-NAVServerLicenseInformation` returns text, so it needs a parser (licensee, expiry, object
ranges per type), tested against captured licences from BC 14 and a current version. As built
(#1365) that parser runs in AL Workbench, not the gateway; see "Inventory, as built".

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
signers = ["3F2A9C41D07E5B86A1C2E4F60718293A4B5C6D7E"] # optional: accept signatures only from these (thumbprint or subject)
allow_force_sync = false       # optional: true lets a deployment sync with ForceSync (default false)
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

Three per-tier settings narrow what `publish` may publish, and are not permissions in `allow`
because they qualify one rather than grant one:

| Setting | Means |
| --- | --- |
| `publishers` | Only apps from these publishers, compared ignoring case. Left out: any publisher |
| `allow_unsigned` | `true` takes apps without a signature (published with `-SkipVerification`). Off unless set |
| `signers` | Only signatures from these certificates, each a thumbprint (case and spaces ignored) or a subject (case ignored). Left out: any signature Windows calls valid. When set, a signed app from anyone else is refused even where `allow_unsigned = true` |
| `allow_force_sync` | `true` lets a deployment sync with `ForceSync`, which can delete tables and fields with their data. Off unless set: a ForceSync command is refused by the policy check before its files download, and again by the verb before any cmdlet runs. Reported with the policy (`allowForceSync`) so AL Workbench can hide ForceSync for a tier that does not allow it (#1367) |

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
licence and restart dialogs list every Solution whose environments sit on the tiers involved; a
restart group or an application-level licence import that reaches tiers linked to more than one
Solution is refused unless that database sets `shared_restart = true`; and a deployment to one
tenant of a shared server is published tenant-scoped (below) so the app itself never reaches the
others. As built (#1368) the refusal is AL Workbench's, not the gateway's, and the Deploy dialog
gives a count of the other Solutions rather than their names: "The restart group, as built".

**Remote machines.** The service account needs remote service control rights on each machine
(remote procedure call and file sharing reachable inside the network), or PowerShell remoting where
that method is chosen; the setup guide (#1376) says this. In Windows PowerShell 5.1, `Stop-Service` / `Start-Service` have no
`-ComputerName`, but `Get-Service -ComputerName <machine> -Name 'MicrosoftDynamicsNavServer$<instance>'
| Stop-Service` works over the remote service manager. PowerShell 7 removed `-ComputerName` from
`Get-Service`, so `service-manager` steps always run in 5.1. `powershell-remoting` uses
`Invoke-Command -ComputerName` where WinRM is enabled.

### The policy file and the runner, as built (#1373)

In `ALWorkbench.Gateway.Core/Policy/` and `PowerShell/`, with the Windows-only pieces in the host.
`gateway.example.toml` ships beside the program with comments for customer IT; they copy it to
`gateway.toml`.

**Reading the file** (`GatewayPolicyParser`, on Tomlyn's syntax tree because only the tree knows
line numbers). Strict: a key, a section or a value it does not know is a mistake reported with its
line number, never ignored, so a typo cannot silently widen or narrow what the gateway may do. All
mistakes are reported at once. Beyond the example above:

- Values are case-sensitive words (`"production"`, `"stop-all-then-start-all"`, `"NavDatabase"`);
  machine and instance names compare without regard to case, as Windows does.
- `tenants` left out means `["default"]`; `publishers` left out means any publisher. An empty list
  for either is a mistake ("leave the line out instead"). `allow = []` is allowed and means nothing,
  not even `inventory`.
- `[defaults.<kind>]` holds only `allow` and `change_hours`. Each of the two is taken from the tier
  if it sets it, else from its kind's default, independently; there is no `[gateway]` section.
- `change_hours` is `HH:MM-HH:MM`, start inside and end outside, overnight when the start is later;
  judged on the gateway computer's wall clock, so daylight saving moves with it (an hour that happens
  twice is inside both times). A window that starts and ends at the same time is a mistake.
- A database must name `publish_on`; `restart` left out restarts only that tier; every tier it names
  needs its own `[[instance]]`, and a tier belongs to one database. `licence_target` left out
  refuses licence imports.
- `before` / `after` must be the full path of a `.ps1` on a local drive (`C:\...`), not a share.
- A tier on another computer needs a `[[machine]]` section; one for this computer is a mistake.
- An optional `powershell = "5.1"` or `"7"` per tier overrides the gateway's choice (below).
- At most 200 tiers, so the report sent to AL Workbench stays small.

**Keeping it current** (`GatewayPolicySource`). The file is read at every heartbeat and before every
command, and parsed again when its SHA-256 changed: cheaper than a file watcher to get right, and a
command never runs under an older version than the one on disk. States, reported to AL Workbench:

| State | When | What is allowed |
| --- | --- | --- |
| `valid` | Read without mistakes | The file as written |
| `invalid` | Mistakes, a named script is missing, or the file could not be read | The tiers of the last good version, `inventory` only, until fixed |
| `missing` | No `gateway.toml` | Nothing; the gateway still starts, so it can be registered and approved first |
| `unsafe` | Someone other than administrators can change the files | Nothing; at start the gateway stops with exit code 1 |

A file with mistakes keeps the last good tiers readable rather than refusing everything: an edit
that broke the file may have been meant to take a permission away, so the old permissions are not
kept, but reads change nothing and AL Workbench keeps seeing the tiers. A file that is there but
cannot be read (held open by an editor, or its rights changed) is treated the same way until a read
succeeds, since what it now says is unknown. Each change of state is logged once (event ids 3000
read, 3001 mistakes, 3002 missing, 3003 unsafe).

**File permissions** (`IFileAccessCheck`; `WindowsFileAccessCheck` in the host reads the access
list, `AccessRules` judges it). The program folder and every file and folder in it (the program, its
DLLs, `gateway.toml`), and each named script and its folder must be owned by Administrators, SYSTEM
or TrustedInstaller, and no one else may hold a right that changes them (write, append, attributes,
delete, delete children, change permissions, take ownership, generic write or all). That includes
the gateway's own service account. On a folder, an inherit-only entry that is passed on to files or
folders created in it counts like one on the folder itself, since it decides who can write the next
DLL; on a file it applies to nothing. Deny entries, and the CREATOR OWNER and CREATOR GROUP
placeholders, add nobody. A folder where many files are open names five and counts the rest. The
same rule covers the tier's install folder whose `NavAdminTool.ps1` or module the runner loads (see
the runner, below). Checked on the same schedule as the file; at start a problem logs event 3003 and ends the service with exit code 1,
the same way as loosened data-folder rights (event 2008), so the recovery options `install` sets
restart it every minute and it starts by itself once the rights are fixed. Later, a problem refuses
every command, reads included.

**The check before every command** (`PolicyEnforcer`, called by `GatewayRunner` before any file is
downloaded). A refusal is reported as the command's failure with the reason and "Nothing was
changed.", and logged with event id 1003. The command names its tier with the parameters in
`GatewayCommandParameters` (`machine`, `instance`, `tenant`, and for `publish-install` `apps`
with each app's `file`, `publisher` and `signed`, and `unpublishPrevious`; the verb itself also
reads each app's `id`, `name` and `version` and the command's `syncMode`, #1375); a parameter of the wrong
shape refuses the command. A `publish-install` must list its apps, never an empty list, with exactly
one app for each of the command's files, named by the file's name; otherwise it is refused, because
an empty list would pass every publisher and signing rule while the verb published whatever files
came with it. What each verb needs:

| Verb | Needs |
| --- | --- |
| `refresh-inventory` | `inventory` on the named tier; with no tier named, the verb reads only the tiers that allow it |
| `publish-install` | Sent to the database's `publish_on` tier: `publish` there, the tenant and each publisher on its lists, `allow_unsigned` for an unsigned app, `allow_force_sync` for `syncMode = ForceSync` (any other mode than `Add` or `ForceSync` refuses the command), `unpublish` when previous versions go, `scripts` when the database names a script, and `restart` on every tier of the restart group |
| `import-licence` | Sent to the `publish_on` tier of a database with a `licence_target`: `licence` there (and the tenant for `Tenant`), `restart` on every tier of the group |
| `restart-group` | `service_control` on every tier of the group |
| `restart-instance`, `stop-instance`, `start-instance` | `service_control` on the tier |

Then, for every change: refused when any tier it touches is `production` and an AI assistant asked
("AI assistants cannot change on-premises production"), and refused outside any touched tier's
`change_hours`. Everything this check knows about an app is what the command declares: the
publisher, the `signed` flag and which file is which. It refuses on the claim early; the
`publish-install` verb (#1375) then checks it against the files themselves ("The `publish-install`
verb, as built"). The shared-tier rule
(`shared_restart`) needs to know which Solutions each tier is linked to, which only AL Workbench
knows, so AL Workbench enforces it (#1368, "The restart group, as built").

**The report.** `GatewayPolicyReport` (contracts) goes with the first heartbeat after start, after
any change, and every 15 minutes; other heartbeats stay small. AL Workbench stores it in
`gateways.effective_policy` (jsonb) with `policy_reported_at`, whether or not the gateway is
approved yet, and keeps the stored one when a heartbeat carries none. The gateway lists at most 50
problems (and counts the rest), and a report over 128 KB of JSON is replaced by a short one with
`tooLarge` set and the state, so a large file can never get the heartbeat refused and the gateway
shown offline; `check` still shows all of it. When AL Workbench receives that short report, or one
over its own 256 KB limit from a gateway that did not cut it down, it keeps the stored report and
sets `policy_stale`, so it is never shown as what the gateway enforces now; the next full report
clears it.

**The runner** (`PowerShellRunner` over `IProcessRunner`). One child process per operation:
`powershell.exe` or `pwsh.exe` with fixed switches (`-NoLogo -NoProfile -NonInteractive
-ExecutionPolicy Bypass -EncodedCommand`) and an encoded command that is the fixed bootstrap alone.
The operation's own constant script, the parameters, the module folder and a per-run random marker
go on standard input as base64 of UTF-8 JSON; the bootstrap makes a script block of the operation's
script and passes it the parameters, read with `ConvertFrom-Json`, as one object, so the command
line is identical for every operation and whatever the parameters hold. (Until #1365 the operation's
script was part of the encoded command; UTF-16 and base64 cap that at about 11,000 characters of
script, and the inventory script is longer. Standard input is a pipe between the gateway and its own
child, so carrying a constant there opens nothing.) The bootstrap dot-sources `NavAdminTool.ps1`
from the tier's install folder, or, without it, imports whichever of the
`Microsoft.Dynamics.Nav.Management`, `Microsoft.Dynamics.Nav.Apps.Management`,
`Microsoft.BusinessCentral.Management` and `Microsoft.BusinessCentral.Apps.Management` modules
the folder holds (`.psd1`, `.psm1`, then `.dll`); the folder is the one in the tier's Windows service
image path (`MicrosoftDynamicsNavServer$<instance>`, read by `RegistryServiceTierLocator`). The
result comes back on standard output between two marker lines as base64 JSON; other lines are log,
streamed as progress; a failure is a non-zero exit with PowerShell's error on standard error.
A timeout or a cancellation kills the whole process tree. On Windows each child also goes into a
job object set to kill everything in it when closed (`JobObjectContainment` behind
`IProcessContainment`), closed when the operation ends, so a grandchild whose parent already exited,
which a tree kill no longer finds, cannot outlive the command.

Before it loads a tier's module, the runner checks the tier's install folder and everything in it
with the same rule as the gateway's own files, and refuses to run anything against a tier where
someone other than administrators can change them: whoever can replace `NavAdminTool.ps1` or a
module DLL there gets code running in the gateway's PowerShell. `check` reports it for each tier.

PowerShell per tier (`PowerShellChoice`): the file's `powershell` if set; else 7 from Business
Central 24 (assumed to be the first whose module needs modern .NET; to be confirmed against real servers
with #1365), else 5.1, and 5.1 when the version is unknown. Remote machines are #1368's.

**`ALWorkbench.Gateway check`**, run by customer IT as administrator: shows the file's mistakes, who
else can change the files, each tier's effective permissions, and runs a harmless
`Get-NAVServerInstance` against each tier on this computer, which proves the right PowerShell and
module load. It changes nothing.

Tests: `ALDevToolbox.Tests/Gateways/GatewayPolicy*Tests` and `PowerShellRunnerTests`. The runner's
real round trip (hostile values, loading `NavAdminTool.ps1` from a tier folder) runs when `pwsh` is
on the PATH and is skipped otherwise; Windows PowerShell 5.1 and real Business Central modules are
first exercised with #1365.

**Open items**, each owned by a later step:

- **PowerShell choice and standard input under 5.1** (#1365). The version from which a tier gets
  PowerShell 7 (Business Central 24) is an assumption, and passing the parameters on standard input
  is only tested under `pwsh`. #1365 built the inventory that is to confirm both, but has not yet
  been run against a real server; "Inventory, as built" lists what to check.
- ~~**`shared_restart`** (#1368).~~ Closed by #1368: AL Workbench enforces it, since only it knows
  which Solutions are linked to which tiers. The gateway still parses and reports it, and does not
  enforce it: "The restart group, as built" has why a rule on the gateway's side was rejected.
- ~~**Each `.app` against the declared list** (#1375).~~ Closed by #1375. The policy check still sees
  only what the command declares; before any cmdlet runs, `publish-install` now hashes each file
  again, reads its id, name, publisher and version from the `.app`'s own manifest and refuses a
  mismatch with what was declared, checks the file's publisher against the tier's list, and, with
  `allow_unsigned = false`, refuses a file that carries no signature whatever `signed` claimed, then
  has Business Central read each file too (`Get-NAVAppInfo -Path`) and Windows verify the signature
  and its signer (`signers`), and publishes without `-SkipVerification`. "The `publish-install`
  verb, as built" has how, and where that check stops short off Windows.

### Inventory, as built (#1365)

**What is read.** For each tier the policy file lets it read (`inventory`), on its own computer, the
gateway runs one fixed script (`InventoryScript`) through the runner and gets one JSON document
back: the instance's state and version (`Get-NAVServerInstance`); its Windows service's state, start
mode and account (`Win32_Service`); ten whitelisted configuration keys (database server, instance
and name, multitenant, the five ports, the public web base URL), never the whole configuration;
its tenants (`Get-NAVTenant`; a single-tenant tier gets one tenant, `default`, with no cmdlet
needed); each tenant's apps (`Get-NAVAppInfo -Tenant`, with `-TenantSpecificProperties` where the
cmdlet has it, which adds data version and sync state); session counts per tenant
(`Get-NAVServerSession`); and the licence as text (`Export-NAVServerLicenseInformation | Out-String`).
User names of sessions are read only where the tier allows `session_users`, and dropped again by the
gateway if the script returned them anyway. A stopped tier is asked only for the first three. It is
read-only throughout: no cmdlet that changes anything is in the script.

**Defensive by construction, for BC 14.** Before each part the script checks that the cmdlet, and
any parameter it would add, exists (`Get-Command`). A part that is missing, fails or runs too long is
left out and named in `unavailable` with the reason, and the rest is still sent. Each part runs in
a second runspace with its own time limit (60 seconds, three minutes for one tenant's apps), and the
whole tier has 20 minutes; a part that times out is abandoned with its runspace and the next part
gets a fresh one. A tier on another computer (#1368's) or without a Windows service is reported with
an error instead of being read.

**When.** At start once approved, then every 15 minutes, two minutes after a round that could not
be sent, and after every command that is not itself a refresh (anything a verb does may change
what a tier looks like). Not while the gateway's files are unsafe, and not while no policy is in
force (no `gateway.toml`, or a broken one with no earlier good version): an empty roster would have
AL Workbench mark every tier missing, so the gateway reports nothing and AL Workbench keeps what it
last knew; a `refresh-inventory` then fails saying so. A valid file that lists no tiers is different,
and its empty roster is meant. A round is not a command: it
runs between polls, like one, with heartbeats going on. A restarted gateway therefore inventories
first, which is what "Run once, across restarts" needs; the server queues nothing for it.

**`refresh-inventory`** reads the named tier, or every readable tier when it names none, and the
command's result is `{ read, failed }`. A tier the policy file does not let the gateway read fails
the command. The gateway page's **Refresh now** (Admin only, approved gateways only) queues one with
no tier named, or returns the one already waiting; the page then polls for three minutes.

**Sending: `POST /gateway/inventory`, one tier per post.** Not a heartbeat field, because a
heartbeat must stay small for a large server never to get it refused and the gateway shown offline;
not a command result, because the scheduled round has no command. The report
(`GatewayInventoryReport`, `schemaVersion` 1) carries the tiers read and, in a full round, the
**roster**: every tier the policy file lets the gateway read. A tier AL Workbench knows that is not in
the roster is marked missing; a single-tier refresh sends no roster and marks nothing. With no
readable tier at all, one report with an empty roster is sent. A tier too large for one post (aimed
at 2 MB, against the 4 MB body limit) is split by tenants, the whole tenant list in every part and
each tenant's apps in exactly one. One post per tier every 15 minutes is far inside the gateway rate
limit.

**A refused tier does not stop the round.** A tier AL Workbench refuses (a 4xx such as a report
over a limit, or a 500 it answers for that report) is logged (event 1008) and skipped, and the round
goes on to the next tier; it is sent again with the next reading, not in two minutes. Only an
AL Workbench that cannot be reached, or a 502, 503 or 504 from in front of it, ends the round for the
two-minute retry. A 409 means two posts created the same new tier, tenant or app at the same moment
(the unique index caught it, nothing of that post was kept); the gateway sends it again, up to three
times.

**Bounds.** The gateway cuts each list to its limit (`GatewayInventoryLimits`: 1,000 tenants per
tier, 1,000 apps per tenant, 500 session users, 512 KB of licence text, 200 tiers) and flags what it
cut (`tenantsTruncated`, `appsTruncated`, `truncated`). AL Workbench refuses a report over any limit
whole, with a field-keyed 400: only a gateway that does not cut can send one, and refusing is
clearer than storing part of it silently. So is a report with no list of tiers, a licence without its
text, or a PowerShell version word over 10 characters. Every other text field is cut to 1,000
characters on the server whatever the gateway sent. A cut list marks nothing missing, since what was
cut is unknown; the script says when it stopped at the tenant limit (`tenantsTruncated`), so tenants
past it are not marked missing either.

**Storing.** `GatewayInventoryService` upserts by gateway + machine + instance, tenant id, and app id
+ version, all without regard to case, so ids are stable and the latest spelling is kept. What is
true now (state, version, service, session counts) is overwritten, null meaning unread; what
changes slowly (configuration, tenants, apps, licence) is kept when a report could not read it. A
tier that could not be read at all keeps everything and shows why. An unchanged app is not written.
The licence is parsed when its text's SHA-256 changes, and its ranges are replaced whole. The five
tables are organisation-scoped with the query filter, listed in `TenantTableCatalog` with the
authentication and audit tables (counted, never restored from a snapshot: the gateway rewrites them
within 15 minutes), and deliberately not audited: they record what a server reported, not anyone's
decision.

**The licence is parsed in AL Workbench, not the gateway** (`GatewayLicenceParser`), so a layout we
read wrongly is fixed with a deployment rather than a manual update on every customer's server.
It reads the licensee, serial number, expiry date (ISO, day-first with `-` or `.`, `/` when only one
reading is possible; one that reads both ways is a parse problem rather than a guess) and object
ranges by type (`50000 50099`, `50000..50099` or `50000-50099`, with the permissions after them).
What it cannot read is kept as parse problems next to the text. Lines over 1,000 characters are
skipped (both patterns backtrack on long runs of spaces), each match has a 200 ms limit, and a year
outside 1900 to 9999 is a parse problem rather than a date.

**The page.** The gateway's name on Administration → Gateways opens its page
(`/admin/administration/gateways/{id}`, Admin only): status, last seen, version, and a table of
tiers with machine, version, state, database, tenants, installed apps, licence expiry (marked when
expired or within 30 days), when it was last read and whether it is missing, with the parts not
read listed under a tier. A tier that could not be read says why in plain words (the server took too
long, there is no such service, the program files are not safe) and what to do, with the gateway's
own message under "What the gateway said". Empty states say what to do for a gateway still waiting
for approval, a revoked one, an offline one (check that the service is running on that server), and
one that has not reported yet; a refresh asked of an offline gateway says it will read when it
reconnects.

**`ALWorkbench.Gateway check --inventory <folder>`** runs the script for every readable tier on this
computer and writes each raw result to `inventory-<machine>-<instance>.json`. It sends nothing. That
is how the samples in `ALDevToolbox.Tests/Gateways/InventorySamples/` are to be replaced: they are
synthetic today, written from documentation.

**To verify on real servers** before the inventory is trusted (none of this has met a real Business
Central yet; the script has only run under `pwsh` on Linux against stand-in cmdlets):

- that Windows PowerShell 5.1 reads the operation and parameters from standard input, and the
  runspace-per-part timeouts behave the same there;
- that Business Central 24 is where the module needs PowerShell 7 (`PowerShellChoice`), on a machine
  with BC 14 and a current version side by side;
- the module and `NavAdminTool.ps1` names in each version's install folder, and whether loading the
  module a second time for the part runspace takes long;
- which versions have `-TenantSpecificProperties` on `Get-NAVAppInfo` and `-Tenant` on
  `Get-NAVServerSession`, and what `Get-NAVTenant` does on a single-tenant tier;
- the shape of `Get-NAVServerConfiguration` with and without `-AsXml`, and `Multitenant` as text;
- the layout of `Export-NAVServerLicenseInformation` for an `.flf` (BC 14) and a `.bclicense`
  licence, especially the expiry line and the object ranges, then replace the samples;
- how long `Get-NAVAppInfo` takes on a tenant with many apps, against the three-minute limit;
- the service account the gateway runs as can run every cmdlet above.

## Data model

New tables, all org-scoped with the usual query filter:

| Table | Holds |
| --- | --- |
| `gateways` | Name, public key and its fingerprint, state (pending, approved), registered at, approved by and at, last seen, reported version, machine name, effective policy (JSON), when it was reported and whether it is out of date, revoked at, the online state last announced to Admins |
| `gateway_registration_codes` | Code hash, created by, expires at, used at, gateway id (set for a rotation code) |
| `gateway_seen_tokens` | Gateway, `jti`, expires at (replay protection; swept with the commands) |
| `gateway_commands` | As in "Commands" above |
| `gateway_command_files` | Id, command id, file name, SHA-256, bytes (or a reference to the artifact row it came from) |
| `gateway_instances` | Gateway, machine, instance name, environment (production, test, development, from the policy file), version, state, Windows service state, start mode and account, database server, instance and name, multitenant, ports, public web base URL, open sessions (and user names where `session_users` allows), PowerShell used, read error, parts not read (JSON), first reported, reported at, read at, missing since |
| `gateway_instance_tenants` | Instance, tenant id, state, database name, open sessions, apps cut to size, apps read at, read at, missing since |
| `gateway_instance_apps` | Tenant, app id, name, publisher, version, data version, scope, installed, published, sync state, missing since |
| `gateway_instance_licences` | Instance (one each), licensee, serial number, expiry, the text as read, its SHA-256, cut to size, parse problems, read at |
| `gateway_instance_licence_ranges` | Licence, object type, from and to id, permissions |
| `oe_project_licenses` | Solution, environment, file name, bytes (at most 1 MB), size, SHA-256, the `licence_target` it went to, uploaded by and at, the `import-licence` command (code spells it `License`, as `OeProject.LicenseType` does; copy says licence). As built (#1369): one row per upload, written once; see "Licences, as built" |

The inventory tables are a stable upsert keyed by gateway + machine + instance (+ tenant, + app id
and version); an instance, tenant or app that stops being reported is stamped `missing_since`, never
deleted, as `OeProjectEnvironment` treats environments that vanish. Licence ranges are replaced whole
when the licence text changes.

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

#### As built (#1366)

`OnPremisesEnvironmentService` (`Services/Gateways/`) links and unlinks; the Solution's
**Environments** tab (`ProjectDetailEnvironments.razor`, on-premises Solutions only, readable by
anyone who can see the Solution) is where an Admin does it, with one "Link service tier" button and
a picker grouped by gateway that names each choice `SERVER / INSTANCE`, plus the tenant on a
multitenant server. Where it differs from, or adds to, the decision above:

- **The unique index is also filtered to `missing_since IS NULL`**
  (`ix_oe_project_environments_gateway_tenant`: `kind = 'OnPremises' AND missing_since IS NULL`).
  Filtered to the kind alone, an unlinked row would hold its tier forever, and a customer who moves
  from one Solution to another could never be linked to the new one. Linking the tier back to the
  Solution that had it still revives that Solution's own row.
- **An unlinked row gives its name up too.** `ix_oe_project_environments_project_name` is filtered
  to `missing_since IS NULL OR kind = 'Online'` (`EnvironmentQueries.HoldsName` is the same rule for
  lookups by name, such as `DeliveryService`'s). So a customer who moves to Business Central online
  gets their "Production" from the next refresh, and a tier on a rebuilt gateway (a new
  `gateway_instances` row) can be linked as "Production" again. Online rows keep holding their name
  while missing, as before.
- **Relinking under a taken name is refused, not renamed.** Linking a tier back revives its own row;
  if another live row has taken the name meanwhile, the link is refused with a `Name` error and the
  Admin picks another name. Reviving a second "Production" is impossible, and renaming either row
  behind the Admin's back would be worse than asking.
- **Plain indexes for the foreign keys.** The partial indexes above cannot serve a referential
  action, which has to find every referencing row, so `gateway_instance_id` (`SET NULL`) and
  `project_id` (`CASCADE`, no longer covered once the name index was filtered) each have a plain
  index. `OeFactTableIndexTests` now ignores filtered indexes, except one filtered to
  `<column> IS NOT NULL`.
- **`gateway_instance_id` is `ON DELETE SET NULL`.** Inventory rows are never deleted today, but a
  gateway deleted outright must not take a Solution's environment (and its pipelines' target) with
  it.
- **Linking needs on-premises hosting.** A Solution set to Microsoft's cloud is refused with a
  `HostingType` error, the mirror of the rule below that refuses Microsoft cloud while a tier is
  linked.
- **Status:** a tier the gateway reads as `Running` is stored as `Active`, the word the existing
  checks treat as running; any other state (`Stopped`, `Starting`, ...) is kept as the server said
  it. `BcEnvironmentStatus.StatusWord` and the list's row state learned those words.
- **Apps:** only installed apps that are still reported are mirrored, one row per app id (the
  installed version, else the newer). A tenant whose apps were never read, or read as none, leaves
  the mirror alone, as an online read with no apps does.
- **Freshness:** an on-premises row says "Gateway offline since <time>" when its gateway is offline
  (the gateway's own state, `GatewayService.StatusOf`), "Not read since <time>" when the gateway is
  online but the tier has not been read for an hour, and that the gateway "no longer reports" it when
  the instance, or the tenant on it, is stamped missing. Those three are the only states with a
  warning colour.
- **Out of touch is never healthy.** In any of those three states (`OnPremisesTierFacts.NeedsAttention`,
  `FleetRowState.IsOutOfTouch`) the last state is shown as the last one: the environment page's pill
  is muted and reads "Last seen running" (or the last state), and the Environments list gives the row
  the warning glyph and keyline and counts it under Needs attention. The environment page then shows
  only the warning, without the freshness line; "Open the gateway" in it is for Admins only.
- **A tenant the gateway stops reporting** is treated like a tier it stops reporting. The inventory
  does not write the server's newer facts onto its environment, and the readers that take the
  customer's current state (production facts, module versions, the module filter) leave it out
  through `EnvironmentQueries.StillReported`. Nothing is stored for it: it is derived from
  `gateway_instance_tenants.missing_since` on read.
- **`FetchedAt` is the last good read.** An inventory whose read of the tier failed (`ReadError`)
  leaves the environment's `FetchedAt` alone, so "read 3 days ago" stays true.
- **Multitenant is inferred when the flag is unknown.** A server reporting more than one live tenant
  is multitenant even when the gateway could not read the configuration flag, in the link dialog, in
  `LinkAsync` (which then wants a tenant) and on the environment page.
- **Deploying was refused until #1367.** `DeliveryService`, the pipeline editor and
  `ReleasePipelineService` refused an on-premises target; #1367 lifted all three (see "Deploying to
  on-premises, as built"). `DeliveryTargetFactory.Unavailable` now only refuses when no gateway services are
  registered; a row with no tier linked reads as a missing environment instead.
- **What the environment page shows:** Overview (the service tier, its database, tenant, sessions,
  licence expiry and the gateway), Apps (from the mirror) and Workbench history. Operations and
  Sessions are Business Central online's live reads and are not offered; an address naming them lands
  on Overview. Upload and Copy are not offered.

### Readers of `OeProjectEnvironments`

Every reader decides whether on-premises rows belong:

| Reader | On-premises rows |
| --- | --- |
| `EnvironmentRefreshScheduler` / `ProjectConnectionService` refresh | **Excluded** - filters `Kind = Online` before upserting and before stamping `MissingSince`, so a refresh never marks a linked tier missing |
| `BcUpdateScheduleChange`, `EnvironmentQueries` (the panel and update reads) | **Excluded** |
| `UpgradeFleetService`, `EnvironmentUpgradeService`, `UpgradeActionService`, `UpgradeActionWorker` (Upgrades page) | **Excluded** - Microsoft's platform updates only. One exception from #1371: an on-premises row's service tier actions are `oe_environment_upgrade_actions` rows, so `UpgradeActionService.ListEnvironmentActivityAsync` (the Workbench history) includes them; `UpgradeActionWorker` only picks rows of `Online` environments and never fires them |
| `EnvironmentUpdateNotifier` | **Excluded** - Microsoft's update notices only |
| `HeldInstalls` | **Excluded** - Microsoft's deferred schedules only |
| `DeliveryService`, `ReleasePipelineService` | Included, through the delivery seam: `GatewayDeliveryTarget` (#1367). A row with no tier linked reads as missing |
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

As built (#1366): both hosting rules are in `ProjectCustomerInfoService.SaveBasicsAsync`, and the
Microsoft-cloud refusal says an administrator can unlink the tiers on the Environments tab.
`ReadProductionFactsAsync` takes *(online, configured, Production)* or *(on-premises hosting, linked
Production)*, and `CustomerModuleService` reads module versions from the linked Production tier
while one exists (typed module versions are refused meanwhile and come back on unlink). The Solution
gains an **Environments** tab in place of the Business Central tab, and the palette offers it.

## Deploying to on-premises

The delivery orchestration stays one flow. #1374 puts everything in `DeliveryService` that talks
to Business Central behind one interface with two implementations, the Admin Center one (today's
code) and the gateway one: the live status re-read before uploading, the installed-apps read, the
downgrade refusal that reads it, and the install of each app. That clears CLAUDE.md's interface bar the way
`IOffsiteStorageProvider` did. Shared, unchanged: claiming, per-app result rows, dependency order,
skip-if-already-installed (#931), failure short-circuit, the delivery window, prepared deployments
and approvals, one deployment per environment at a time.

As built (#1374): the seam is `IDeliveryTarget`, one instance per environment per run, picked
from `OeProjectEnvironment.Kind` by `DeliveryTargetFactory` (the `OffsiteStorageProviderFactory`
shape). `AdminCenterDeliveryTarget` is today's code moved, not rewritten: the token source
(`ConnectAsync`, and `RenewAsync` before each app after the first, #1113), the application
family, the live status re-read and its refusal (`ReadStatusAsync`), the installed-apps read,
the upload (`StartInstallAsync`), operation polling through `BcAppOperationPoller`
(`WaitForInstallAsync`), the open-sessions read, and the held-install list and cancel. What a
target can do is a reported capability, `DeliveryTargetCapabilities.DeferredSchedules`, not a
null the shared code guesses at: true online, false on-premises. The shared code asks it when a
deployment is resolved and again when it is claimed, and only a target that has it is asked for
held installs. Still in `DeliveryService`: the downgrade refusal (it compares the build with the
installed list the target returns), writing the live status back to the environment row, the
not-a-sandbox rule for unapproved and agent deployments, and the rest of the shared list above.
Until #1367 the factory had no on-premises target and `DeliveryTargetFactory.Unavailable` refused
one before signing in; since #1367 it builds `GatewayDeliveryTarget` (see "Deploying to on-premises,
as built").

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

### Deploying to on-premises, as built (#1367)

The AL Workbench side of `publish-install`. The restart group and the before and after scripts came
with #1368 ("The restart group, as built"); what follows was written before them, and the notes on
restarts below are kept for a gateway older than that.

**The target.** `GatewayDeliveryTarget` (`Services/ObjectExplorer/Delivery/`) is the second
`IDeliveryTarget`, built by `DeliveryTargetFactory` for an `OnPremises` row with a linked tier, from
`GatewayTargetServices` (the database, the command queue and the clock). It reports `DeferredSchedules = false`.
Everything it reads about the tier comes from the inventory mirror, through `OnPremisesDeployFacts`
(`Services/Gateways/`): the tier and its gateway, the publishing tier for the database, what the
customer's gateway settings allow (`allow_force_sync`, and a refusal sentence when the tier is not
in the settings at all), the gateway's online state, the installed apps and the open sessions.
Nothing is read from the tier live, so `DeliveryTargetStatus.ReadLive` is false and the shared code
does not write the status back: the row is already written from the same inventory.

**One command per build.** The shared run is per app; the gateway is per command. They meet through
`IDeliveryTarget.PrepareInstallsAsync`, which the run calls once with every app still to send
(after skip-if-already-installed, so an app the mirror shows on the build's version is never in the
command): the gateway target queues one `publish-install` with `GatewayPublishInstallParameters`,
each app's id, publisher and version read from its manifest, and `signed` from the `.app` header.
It answers a `DeliveryHandOff` naming the command; online answers null and the run goes on app by
app exactly as before. A `DeliveryTargetRefusedException` from it refuses the whole batch before
anything is sent: the first app is Refused, the rest Skipped.

**The run hands off; a follower finishes.** A command can run for hours (a large data upgrade), so
no `DeliveryWorker` lane waits on it. With a hand-off the run marks every app Installing, stores the
command on the deployment (`oe_project_deliveries.gateway_command_id`, plus `gateway_log_offset`,
how much of the command's progress is already copied), logs "Waiting for the gateway to pick it
up." and returns, releasing its lane and its `DbContext`. `GatewayDeliveryFollower`
(`Services/ObjectExplorer/Delivery/`), a `PolledScheduler` on a 5-second poll that opts out with
`DISABLE_GATEWAY_DELIVERY_FOLLOWER=1`, visits each organisation inside its
`AmbientOrganizationScope` and calls `DeliveryService.FollowGatewayDeliveriesAsync`: new progress
lines are appended to the deployment log ("Gateway: ...", which the page polls as before), and once
the command has ended each Installing app takes its own line of the result, in order, through the
same outcome mapping and first-failure Skipped as online, then the deployment settles and
`DeploymentNotifier` tells the person behind it. A scheduler rather than a hook in the command
service's completion path because three of the four things it does need a clock: copying progress
while the command runs, finishing a command that ended without the gateway ever reporting (expired,
interrupted, revoked), and giving up after a day. The completion path would cover only the fourth.

There is no foreign key from the deployment to the command: a command that is no longer there reads
as an unknown outcome rather than blocking a delete. The command and the deployment's
`gateway_command_id` are written in one transaction on the run's context: the run opens it before
`PrepareInstallsAsync`, the command service saves the command inside it (and, seeing the open
transaction, leaves waking the gateway to the caller), and `HandOffAsync` commits it with the save
that records the command, then wakes the gateway. Any other way out of the run rolls it back first.
So a gateway can never collect a command that no deployment records, even after a crash between the
two writes, and a deployment never records a command that is not there.

**Restarts and the one-deployment rule.** On start, `FailInterruptedDeliveriesAsync` fails a
deployment that was mid-run, except an Installing one with a `gateway_command_id`: its command is
still the gateway's, so the follower keeps following it. While such a deployment is Installing,
another deployment to the same environment stays scheduled rather than starting, which is the
existing one-deployment-per-environment rule, now holding across a restart.

**Giving up.** A deployment still waiting 24 hours after it handed off is given up. A command still
Queued is cancelled first (`GatewayCommandService.CancelQueuedAsync`, a compare-and-set from Queued
to Failed), so the apps fail with "AL Workbench stopped waiting after a day, and the gateway had not
picked this up, so nothing was sent." A command the gateway already collected cannot be pulled back,
so its apps are Unconfirmed instead (below). A command queued in the delivery window must be claimed
before the window closes (`GatewayCommandService.ClaimDeadline`), so a gateway that comes back after
the window does not install outside it; the queue's own sweep expires it, and the follower reads that
as nothing sent.

**Unconfirmed.** A command that ended with no result is mapped by whether it ever left the queue
(`GatewayCommandProgress.LeftQueue`: claimed, or Claimed/Running). Never collected: the command's
message becomes the first app's failure and the rest are Skipped. Collected and then lost (the
gateway restarted mid-run, went quiet): each app is **Unconfirmed**, "Not confirmed: the gateway
stopped before it said what became of this app. Check the environment's apps after the gateway's
next inventory.", and the deployment fails with "...whether the apps went in is not known. Check the
apps on {environment} after the gateway's next inventory, then deploy again if they are not there."
The deployment status stays Failed (no new terminal status, which a dozen readers would have to
learn); the page reads it as "Not confirmed" and the banner as "Last deployment not confirmed". The
same reading applies to an online deployment a restart left with Unconfirmed apps. A collected
command whose message ends "Nothing was changed." is the exception: the verb refused before doing
anything, so a settings refusal reads as Refused and anything else as Failed.

**Files are referenced, not copied.** A command file names its `project_build_artifacts` row
(`gateway_command_files.project_build_artifact_id`, `ON DELETE SET NULL`) and stores no content. The
hash and size are computed from the artifact when the command is queued; the download reads
`COALESCE(f.content, a.content)` joined on the artifact in the command's organisation. A build is
up to 50 MB per app and already stored once; copying it per command doubled that for nothing, and
the gateway only ever sees a file id and its hash either way. If the artifact is deleted while the
command waits, the download is refused and the command fails. The copy path stays for a file that is
not a build artifact.

**Per-app outcomes.** The gateway's result per app maps to the result row: `installed` and
`upgraded` to Completed (an upgrade records the version it came from), `already installed` and
`skipped` to Skipped ("Already on {version}" for the first, which the gateway finds for itself
when the mirror was stale), and **`refused` to a new status, Refused** (`OeProjectDeliveryResult`
status is text, so no migration). Anything else is Failed. A refusal is the customer's rule, not a fault: the deployment
fails with "The gateway refused {app}: {reason}", the page shows the app as Refused with a shield
icon and "Nothing was changed for this app.", the collapsed row says only "The gateway refused
{app}", and the apps after it read "Skipped after the gateway refused {app}". The dashboard and the
pipeline's last-deployment line treat Refused as the app that stopped the run. Log lines from the
gateway land in the deployment log; a `REFUSED` line is tinted like a failure.

**Restarts, before #1368.** The command's result carries `restarted`; it is stored as
`oe_project_deliveries.tiers_restarted` (null online). A gateway older than #1368 always says false
and reports no steps, and for it a deployed on-premises run still says "Service tiers were not
restarted" and the summary adds "The service tiers were not restarted.", so nobody assumes a BC 14
tier has dropped its old subscribers. From #1368 the gateway reports each tier and script, and those
lines take the note's place.

**What on-premises refuses, and where.** The rules are enforced in the services, not only the editor:

- **Deferred schedules:** `ReleasePipelineService` refuses saving one against an on-premises target,
  and the run checks `DeferredSchedules` when a deployment is resolved and when it is claimed, with
  `WaitsForUpdateRefusal`. Moving a held deployment (`MoveHeldInstallAsync`) checks the capability
  first and throws a `PlanValidationException` rather than asking the target for held installs.
- **ForceSync:** offered by the editor only when the tier's settings allow it; refused on save, when
  a deployment is resolved, for a one-time "deploy with Force sync" (`CreateDeliveryAsync`), and once
  more when the run claims the deployment, since the customer may have changed the settings in
  between: `PrepareInstallsAsync` then refuses the batch ("Not allowed by the customer's gateway
  settings: {tier} does not allow Force sync, so nothing was sent.") and the app shows as Refused.
  The gateway refuses it again itself. The "Deploy again with Force sync" button only appears for
  Business Central online's `ExtensionChangeFailed`, so it never appears on-premises.
- **Offline gateway:** `ConnectAsync` refuses at once when the gateway is offline, revoked or not yet
  approved, and nothing is queued: "The gateway was offline, so nothing was sent. Check that the
  gateway service is running on {machine}, then deploy again."
- **A tier no longer linked** (`GatewayInstanceId` null) reads as a missing environment, in the
  editor and in the run.

**Open sessions** come back as `OpenSessionsCheck` (sessions and tiers) instead of a list of
`BcSession`: the target sums the sessions of every tier the gateway reports on the environment's
database, so a session on a non-publishing tier still holds the deployment. Tiers are matched on a
key from `DatabaseKey.For` (`Services/Gateways/`): a `tcp:` prefix and `,port` suffix are dropped,
`host\instance` is split, the local spellings (`.`, `(local)`, `localhost`, `127.0.0.1`, `::1`) become
the reporting gateway's machine, the host compares on its first DNS label case-insensitively (an IP
address whole), a null instance, `""` and `MSSQLSERVER` are one default instance, and the database
name compares case-insensitively. A tier whose gateway is not online is not counted, because its
session count is stale; the confirmation says how many were left out ("1 more service tier on the
same database was not counted, because its gateway is offline.") and asks to confirm.

**The editor** (`ReleasePipelineEdit.razor`) offers linked on-premises tiers as targets, marked
"(on-premises, production/test)", says under the picker 'Sent through the "{gateway}" gateway to
{server} / {tier}.' (plus "{tier} shares a database with {publisher}; apps are published on
{publisher}." when another tier publishes), warns
when the tier is unlinked, outside the gateway's settings, or its gateway is revoked, waiting for
approval or offline (the pipeline can still be saved), offers only *Now* and the delivery window,
offers Force sync only when allowed, and has **"Remove the previous version after upgrading"**
(`oe_release_pipelines.unpublish_previous`, default on, sent as `unpublishPrevious`). A pipeline
whose saved schedule is deferred has to have it chosen again before it saves. Add is described by
what it does (`Sync-NAVApp -Mode Add` applies additive schema changes and fails on a destructive
one): "new tables and fields only; a change that would remove data stops the deployment". At phone
width the editor's head is no longer pinned (`.page-head--sticky` unpins below 700px for every edit
page) and its cards and selects shrink to the viewport.

The deployment page names the tier "through its gateway", labels the steps "Picked up" and "Sent to
the gateway" rather than "Claimed" and "Uploaded", says "Running for" while a run is active, and
shows the last lines "Latest from the gateway". The pipeline's facts show the delivery window only
when deployments install in it. When the gateway is not online, the environment's health reads
"Gateway offline, last heard from {time}" rather than the tier's last reported state. A deployment
the customer's gateway settings refused offers no "Deploy again" (the same build meets the same
rule); its suggested next step comes from `GatewayRefusalAdvice` by kind: publisher, signer,
unsigned, Force sync, or a generic "ask the customer's IT to allow this in the gateway settings for
{tier}". A failure with no app to name (the gateway was offline) gives its reason in the banner. A
deployed run that did not restart the tiers adds "Restart the service tiers, or ask the customer's
IT to, when users can be signed out." The pipelines dashboard already included every environment
kind.

**Seam questions from #1374, answered:**

- *Exceptions:* `DeliveryTargetException` is the base the run catches; `BcApiException` (the
  Admin Center's errors) is now a subclass of it. The gateway target throws the base.
- *Shapes:* `BcAppOperation` and `BcInstalledApp` stay the seam's records. The gateway target fills
  what it has and leaves the rest at `Guid.Empty` or an empty version; nothing shared reads those
  fields for on-premises.
- *Status write-back:* only when the target read the status live (`ReadLive`), which is online only.
- *Sessions:* `OpenSessionsCheck` replaces the session list, so the check can span tiers.
- *Batching:* `PrepareInstallsAsync`, once per run, a no-op online (it answers null).
- *Progress:* `WaitForInstallAsync` takes an `onProgress` callback online. On-premises the run hands
  off instead, and the follower appends the gateway's lines to the deployment log, which is what the
  page polls; the gateway target's `StartInstallAsync` and `WaitForInstallAsync` are never called.

**Migration** `20270205000000_DeployToOnPremises`: `oe_release_pipelines.unpublish_previous`
(default true), `oe_project_deliveries.tiers_restarted`, `gateway_command_id` (no foreign key, as
above) and `gateway_log_offset` (default 0), and
`gateway_command_files.project_build_artifact_id` with its index and foreign key.

### The `publish-install` verb, as built (#1375)

The gateway side only (AL Workbench sends the command since #1367, above), and the restart group
and the before and after scripts are #1368's. In `ALWorkbench.Gateway.Core/PublishInstall/`;
`PublishInstallVerb` is an `ICommandExecutor`, routed to by `VerbExecutors`, which the Windows
service gives the loop.

**The command** (contracts: `GatewayCommandParameters`, and `GatewayPublishInstallParameters`,
which writes exactly that object for #1367 to send): `machine` and `instance` of the publishing
tier, `tenant` (left out on a single-tenant server), `syncMode` (`Add`, the default, or
`ForceSync`; `GatewaySyncModes`, the same words as `BcSyncMode`; any other word refuses the
command, and `ForceSync` needs `allow_force_sync` on the tier), `unpublishPrevious`, and `apps` in
installation order, each with `file`, `id`, `name`, `publisher`, `version` and `signed`. `id` and
`version` are new and required: the verb refuses a command without them, because it cannot check
the file against an app it was not told about. `name` is optional on both sides:
`GatewayPublishInstallParameters` leaves it out when it is empty, and the gateway checks it only
when it is there, refusing a `name` that is present but blank, as it does any blank parameter.

**Before any cmdlet runs**, after the loop's own policy check and download:

1. The policy file is checked again, as it is now (a large download takes a while), and the tier is
   looked up in it for its scope, its publishers and whether it takes unsigned apps.
2. The tier must be on this computer and have a Windows service (other computers are #1368's).
3. For every app: its SHA-256 again, against the hash the command carries (defence in depth: the
   loop already refused a mismatch, and holds the file open against writes, but only Windows
   enforces that); then `AppFile` reads the `.app` itself. The layout it relies on: a 40-byte
   header, `NAVX`, header length, format version, package id, package length (64-bit), `NAVX`;
   then the package (a ZIP with `NavxManifest.xml`), exactly that long; then, in a signed file, the
   signature. Id, version, publisher and (when sent) name must match what was declared (id and
   version exactly, a version as four parts; publisher and name without regard to case, as the
   publisher allow-list compares); the file's publisher must be on the tier's list; the same app
   may not be listed twice; and with `allow_unsigned = false` a file with nothing after its package
   is refused as unsigned whatever `signed` said. An encrypted package (`NAVX.NEA`, AppSource's) is
   refused: its manifest cannot be read, so its publisher cannot be checked. A package with more
   than one entry named `NavxManifest.xml` (or `readytorunappmanifest.json`), compared without
   regard to case, is refused: which one Business Central reads is not the gateway's to guess, and a
   second manifest naming an allowed publisher would otherwise get another app past the list. A
   Ready2Run wrapper is read through to the app inside. The manifest is read with no DTD and a size
   cap, and the header's package length is checked against what is left of the file after the
   header, never summed with it, so a length near the 64-bit limit cannot overflow into one that
   passes.
4. `PublishInstallScript.Examine`, one read-only child process for all the command's files, has
   Business Central read each file (`Get-NAVAppInfo -Path`) and, where it is asked for, Windows
   read its signature (`Get-AuthenticodeSignature`). Business Central's id, name, publisher,
   version and, where it reports one, package id must equal the gateway's own reading, so the
   publisher that passed the list is the one Business Central installs; a version whose
   `Get-NAVAppInfo` has no `-Path`, or that cannot read the file, refuses the command. The
   signature is asked for when the tier does not take unsigned apps and whenever the file carries
   one; its signer (the certificate's subject and thumbprint) is recorded in the `signature` step
   and the app's result either way. Where the tier does not take unsigned apps, or lists `signers`
   and the file is signed, anything but `Valid` is refused, as is a valid signature whose subject
   and thumbprint are both missing from `signers`.

One refusal refuses the command with "Nothing was changed."; the refused app is `refused` in the
result and the others `skipped`.

**Then app by app**, each in two child processes, with the decision between them in C#:

- `PublishInstallScript.Inspect` (read-only) returns every version of the app `Get-NAVAppInfo
  -Tenant` lists for the tenant, each with its `PackageId`, and whether it is installed and its
  data version. `IsInstalled` is filled only with `-TenantSpecificProperties`, so the script
  passes the switch wherever the cmdlet has it, for this tenant and every other one, and reads
  `IsInstalled` only then. Where the cmdlet lacks it, which tenants have each version installed
  comes from `Get-NAVAppTenant -Name -Publisher -Version` (each result's `Id`, else `TenantId`);
  with neither, the installed state is reported as unknown, never as "not installed". When
  previous versions are to go it also returns which versions each other tenant has installed
  (`Get-NAVTenant`, then the same read for each); anything unknown there leaves the list out. And
  which optional parameters this version's cmdlets have.
- `AppInstallPlanner` decides. Any version whose installed state is unknown: refused, since
  install, upgrade and the downgrade check all depend on it. The same version already published
  as a different build (its `PackageId` is not the file's header package id): refused, "a
  different build with the same version is already published". The same version installed:
  nothing (`already-installed`, #931). A newer version installed, or data kept from a newer one:
  refused, never a downgrade. An older version installed, or none installed with data kept from an
  older one: publish, sync, `Start-NAVAppDataUpgrade`. Otherwise (never installed, or data kept at
  this very version): publish, sync, `Install-NAVApp`. Publishing is left out when this version is
  already published as this very build (a deployment that failed after publishing, sent again);
  when the server does not report the published build's `PackageId`, the app is refused rather
  than installed in the file's place. Publishing is `-Scope Tenant -Tenant` unless the tier says
  `scope = "global"`; `-SkipVerification` exactly when the tier allows unsigned apps; the
  signature was checked before anything ran (step 4 above). Previous versions, when asked:
  one published to this tenant is unpublished with `-Tenant`; one published for every tenant (or
  whose scope the server does not report) only when no other tenant has it installed, otherwise
  the step is `skipped` with the tenants named, and when the other tenants cannot be read it is
  left too, saying so.
- `PublishInstallScript.Apply` runs the planned steps in order, from a fixed set of step kinds,
  each cmdlet given `-Name`, `-Publisher` and `-Version` read from the file, and stops at the first
  that fails. Each step writes a line before it starts, so a long data upgrade shows in the log
  while it runs, and whatever the cmdlets print goes to the log too.

Deciding before publishing rather than after syncing, as the list above reads, is deliberate:
publishing and syncing change neither what is installed nor the data version, so the decision is
the same, and it keeps the decision in C# where it is tested without Business Central, at the cost
of two module loads per app rather than one.

**Business Central 14 and later.** Each optional parameter (`-TenantSpecificProperties`; `-Path`
on `Get-NAVAppInfo`; `-Scope` and `-Tenant` on `Publish-NAVApp`; `-Tenant` on the others) is
looked up with `Get-Command` first. Without `-TenantSpecificProperties` there is no data version,
so an app uninstalled with its data kept is installed rather than upgraded, and Business Central's
own refusal is what the person sees; whether an app is installed then comes from
`Get-NAVAppTenant`. Which of the two BC 14 has is to be verified (below): the switch is believed
to exist there, and the fallback covers a version that lacks it.
A cmdlet without `-Tenant` is called without it on tenant `default` and refuses for any other
tenant; a tenant-scoped publish where `Publish-NAVApp` has no `-Scope` refuses, saying customer IT
can set `scope = "global"`, rather than quietly publishing to every tenant.

**Signatures, and where the check stops.** Three layers where unsigned apps are not allowed: the
gateway refuses a file that carries no signature (it can tell that much on any platform, from the
header); Windows checks the signature (`Get-AuthenticodeSignature`, which reads an `.app`'s
signature through the component Business Central installs for that file type, and must answer
`Valid`, from a signer on `signers` when the tier lists any); and `Publish-NAVApp` runs without
`-SkipVerification`, so Business Central checks it again. A valid signature on its own proves only
that someone with a trusted code-signing certificate signed the file, which is why the signer is
always recorded and `signers` can pin it. The gateway itself does not verify the signature's cryptography: how the signature is laid
out after the package is undocumented, and Windows' answer is the authoritative one. Where
Windows cannot check (no `Get-AuthenticodeSignature`, or the component is not installed and the
answer is anything but `Valid`) the app is refused, never published unchecked. Off Windows (the
tests) there is no `Get-AuthenticodeSignature` at all, so that layer is only exercised against a
stand-in.

**Reporting.** Each step is a line in the command's log ("CRONUS Coffee 1.4.0.0: upgraded from
1.3.0.0."), sent in the numbered progress batches. The first failure fails the command with the
app, the step and Business Central's own words, and the apps after it are `skipped`. Every app is
in the result, `GatewayPublishInstallResult` (contracts, `schema` 1): per app its file, id, name,
publisher, version, `outcome` (`installed`, `upgraded`, `already-installed`, `failed`, `refused`,
`skipped`), `fromVersion`, a message, `signerSubject` and `signerThumbprint` when the signature
was read, and its steps (`verify`, `signature`, `publish`, `sync`, `install` or `upgrade`,
`unpublish`, each `done`, `failed` or `skipped`; the `signature` step carries the signer too), and
`restarted`. The signer fields are additive, so the schema stays 1.

**The restart group's hook.** `PublishInstallVerb.RestartGroupAsync` runs only after every app
went through. Until #1368 it wrote "this version of the gateway does not restart them yet" and the
result said `restarted: false`; it now runs the restart group and the after script, and `restarted`
is true only when every tier of the group came back ("The restart group, as built").

**Where it differs from the steps above:** an unpublish that fails fails the deployment (the new
version is installed; the step says so), as the first failure stops everything; a downgrade is
refused on the gateway too, not only by `DeliveryService`; and an encrypted AppSource package
cannot be deployed through the gateway.

**Samples are synthetic.** `ALDevToolbox.Tests/Gateways/PublishInstallSamples/` holds what the
inspect script prints for BC 14 (under 5.1) and BC 25, and stand-in `NavAdminTool.ps1` files for
both that the real scripts run against under `pwsh`. None came from a server; they were written
from the cmdlets' documented parameters and output, and are to be replaced with real captures (the
folder's README says how). Tests: `PublishInstallVerbTests` (against a fake Business Central behind
the fake process runner), `PublishInstallSampleTests` (the samples, and the real scripts when
`pwsh` is on the PATH), `GatewayAppFileTests`, and two in `GatewayRunnerTests`: a download whose
hash does not match, and a file rewritten while the verb runs. The runner holds each file open with
`FileShare.Read`, which Windows enforces; elsewhere .NET's file sharing is an advisory lock that a
writer can get past, so off Windows that test shows the verb's own hash catching the change, and on
Windows that the write is refused.

**To verify on real servers** before the verb is trusted:

- the `.app` layout above against signed and unsigned files from the AL compiler and AppSource,
  including where the signature sits;
- `Get-AuthenticodeSignature` on a signed `.app`, an unsigned one and a tampered one, on a server
  with Business Central installed, and what it answers where the component is missing. It reads an
  `.app` through Business Central's subject interface package (NavSip), which is registered per
  bitness: if it is not registered for the PowerShell the gateway runs (64-bit 5.1 or 7), every
  signed app comes back as not `Valid` and is refused. That fails closed, but it would stop all
  signed deployments, so check it on each Business Central version, and what `SignerCertificate`
  holds;
- each cmdlet's parameters on BC 14 and a current version, against the stand-ins, especially
  `-Scope`/`-Tenant` on `Publish-NAVApp`, `-Tenant` on `Unpublish-NAVApp`,
  `-TenantSpecificProperties` and `-Path` on `Get-NAVAppInfo`. Whether BC 14's `Get-NAVAppInfo`
  has `-TenantSpecificProperties` decides which path its tiers take; the stand-in assumes it does
  not, so the `Get-NAVAppTenant` fallback is what the tests run;
- `Get-NAVAppTenant` on BC 14: that it exists in the apps management module, takes `-Name`,
  `-Publisher` and `-Version`, lists the tenants that have that version *installed* (not those it
  is published to), and what property names the tenant id (`Id` or `TenantId`);
- that `Get-NAVAppInfo` reports `PackageId` for a published version, on BC 14 and a current one,
  and that it equals the package id in the `.app`'s NAVX header (for a Ready2Run package, which
  of the two ids it reports); where it is missing, a re-sent deployment of an already published
  version is refused;
- what `Get-NAVAppInfo -Path` returns for a normal, a signed and a Ready2Run `.app`;
- what `Get-NAVAppInfo -Tenant` lists for a version published to another tenant, and whether
  `Scope` and `ExtensionDataVersion` are reported as the planner expects (and `0.0.0.0` for none);
- that `Publish-NAVApp -Path` can read the file from the gateway's work folder under the gateway's
  service account;
- how long a large data upgrade takes against the six-hour limit per app.

### The restart group, as built (#1368)

**Order.** `publish-install` runs, on the gateway: the policy check; every file verified and
examined; the database's **before script**; then publish, sync, install or upgrade, unpublish for
each app on the publishing tier; then, only when every app went through, the **restart group**;
then the **after script**. A failing before script skips every app ("Not started, because the
before script failed."), records the after script as not run, and fails the command with "No app was
published.". An app that fails records the after script as not run and restarts nothing. When every
app was already installed nothing changed, so nothing is restarted and the after script does not
run either. The settings are read again just before the restart, and the group, the after script
and the permissions all come from that read: if they no longer allow `restart` on a group tier, the
command fails with the apps installed and nothing restarted; if they no longer allow `scripts` on the
publishing tier, the after script is "not run" and the command fails with the tiers restarted.
Everything before that comes from the settings the policy check judged (`PolicyDecision.Policy`),
never from a later read, so what runs is what was allowed.

**The group.** The tiers are the `restart` list of the `[[database]]` the publishing tier is listed
under (`publish_on` when the list is left out), in the file's order; a tier no database names is
restarted on its own. Only tiers the file lists are ever touched, whatever the command says.
`stop-all-then-start-all` stops each tier in order until one fails, then starts every tier it
stopped; the tiers it did not reach are "not restarted". `one-by-one` stops and starts each tier
before the next, and stops at the first that does not come back; the rest are "Not restarted,
because X did not come back." (or "did not stop."). The gateway waits up to 5 minutes for a tier to
stop and 10 for it to run again. These are fixed, not settings.

**Every tier sent a stop is sent a start.** That includes a tier whose stop timed out (the start
waits for a stop still pending, then starts it), one whose PowerShell died, and every tier stopped
before the command was cancelled, whether AL Workbench failed it or the service is stopping. The
starts ignore cancellation; each keeps its own 10-minute limit. The tier's line keeps its failure
and adds where it was left ("The gateway started it again, and it is running.", or why not), and a
tier that was stopped and started though the group was not restarted says so. A deployment stopped
after the restart records the after script as not run and returns its result, which the gateway
keeps and reports, rather than being reported as interrupted.

**Tiers left stopped.** Before it sends a stop, the gateway records the tier in its run-once state
file; once it has tried the start it removes it. If the service stops or the computer goes down in
between, the next start (`StoppedTierRecovery`) starts every recorded tier before it takes any
command, logs it (events 1010, tiers left stopped; 1011, started again; 1012, could not be started
again, start it by hand), and reports the command as failed with what it did, instead of as
interrupted. A result already saved for the command is kept. A stop of the service waits for the
starts: the host's shutdown timeout covers one start (12 minutes with the process margin) plus the
result grace, and the service asks Windows for that time when it is stopped
(`GatewayServiceLifetime`, `RequestAdditionalTime`), since Windows otherwise gives a service about
20 seconds. When the computer itself shuts down, Windows does not wait that long whatever the
service asks; the record covers that case. Recovery waits for gateway settings that are not
unsafe, like everything else.

**Service control** (`Core/RestartGroup/ServiceControlScript.cs`, `RestartGroupRunner.cs`) always
runs in Windows PowerShell 5.1, whatever the tier's `powershell` setting, and never loads the
Business Central module: it only needs the Windows service. That decision is the one
"Remote machines" above anticipates: PowerShell 7 dropped `-ComputerName` from `Get-Service`, so
`service-manager` needs 5.1, and spawning `powershell.exe` for every tier keeps one code path for
local and remote tiers. A local tier and `service-manager` use `Get-Service -Name
'MicrosoftDynamicsNavServer$<instance>'` (with `-ComputerName <machine>` for a remote one);
`powershell-remoting` runs the same script block through `Invoke-Command -ComputerName`. The machine
and instance go in as parameters, never into the script text. It calls the service controller's
`Stop()`/`Start()` and `WaitForStatus` with the timeout, not `Stop-Service`, because `Stop-Service`
waits without a limit. A remote tier needs its `[[machine]]` section; the check refuses one without.

**Scripts** (`Core/RestartGroup/CustomerScripts.cs`) are named only in the settings file, as the
full path of a `.ps1` on a local drive with no `[` or `]` (PowerShell reads those as a pattern,
which could pick another file than the one checked) and no `:` after the drive (an alternate data
stream). Before each run, and whenever the settings are read, the gateway checks the file exists,
that it and its folder are writable only by Administrators and SYSTEM (the same `AccessRules` as the
program folder), and that nobody else can rename, delete or replace anything in the folders above
it, up to the drive root (`AccessRules.EvaluateAncestor`: creating a new folder is allowed there,
since every drive root allows it, and entries that reach only folders created later do not count).
Otherwise a writable parent would let someone move the script's folder aside and put their own in
its place. It refuses otherwise. It
runs the script in its own Windows PowerShell 5.1 process (`powershell.exe -File`, inside the same
job object) as the service account, with its path passed as data, for up to 30 minutes; every line
it prints goes to the deployment log as "before script: ..." or "after script: ...". **What fails a
script:** an explicit `exit N` with N other than 0, an error that stops it, or the timeout. That is
the exit code `-File` gives: a script that ends normally exits 0 whatever the last program it ran
returned, so a trailing `robocopy` that returns 1 ("files copied") does not fail it, and a script
that wants a program's result to count ends with `exit $LASTEXITCODE`. `gateway.example.toml` says
the same to customer IT. The tier needs `scripts` in its allow list (the policy check asks for it
when the database names a script).

**Messages are cut short.** A script can write megabytes to its error stream, and a result must stay
inside the 4 MB request limit. The gateway cuts each script's and tier's message to 2,000 characters
(`GatewayStepText`), and AL Workbench cuts them again before storing them, keeps at most 20 steps,
and shortens every message to 300 characters if the steps would still pass 32,000 characters as
JSON. The whole output stays in the command's log.

**The `restart-group` verb** (`RestartGroupVerb`) restarts a database's group on its own, for
the environment page's restart (#1371); a licence import does not send it, since `import-licence`
restarts the group itself ("Licences, as built"). Its parameters are the machine
and instance of any tier of the group; the check needs `service_control` on every group tier. It
runs no scripts. **It does not check `shared_restart`**: only AL Workbench knows which Solutions a
tier serves, so whatever sends this verb (#1371) must refuse a shared
group in AL Workbench first, with `RestartGroupPlan.Refusal`, exactly as deployments do. The
environment page's "Restart all tiers" is what sends it, and refuses first ("Service tier actions,
as built"). It succeeds when every tier came back and otherwise fails with the first failed
tier's message, the result attached.

**The result.** `GatewayPublishInstallResult` gained `restart` (`GatewayRestartGroupResult`: the
mode, `restarted`, a message when nothing was restarted, and each tier with `restarted`, `failed` or
`not-restarted`, its message and its last state) and `scripts` (each with its stage, path, outcome
`succeeded`, `failed` or `not-run`, exit code and message). Both are additive; the schema stays 1.

**AL Workbench.** The follower (`DeliveryService.FollowGatewayDeliveryAsync`) reads the steps out of
the result (`DeliveryGatewaySteps.From`) and stores them on the deployment as
`oe_project_deliveries.gateway_steps` (jsonb, migration `20270206000000_RecordGatewaySteps`): the
before script, one line per tier, the after script, each `done`, `failed` or `not-done`. A group
nothing was done to (nothing installed, the settings changed) is one line naming its tiers. A
command that failed though no app did fails the deployment with the step's own sentence: "The
customer's before script (before-deploy.ps1) failed, so nothing was installed.", "The apps were
installed, but CRONUS-BC02 / BC140 did not restart.", or "The apps were installed and the service
tiers restarted, but the customer's after script (after-restart.ps1) failed."; otherwise the
gateway's own message. The deployment page shows a "Service tiers and scripts" list under the apps,
the timeline's last step says "Installed, restart failed" or "Installed, after script failed", the
next step says what to ask the customer's IT ("The apps are installed. Ask the customer's IT to
start CRONUS-BC02 / BC140 and run after-restart.ps1 by hand. You don't need to deploy again.", the
script named only when the restart skipped it), and "Deploy again" is not offered when the apps went
in, since the same build would change nothing and restart nothing. The log line says "Deployment
failed:", and the log wraps long lines rather than scrolling sideways. On premises the summary's
health is labelled with the linked tier ("Health of CRONUS-BC01 / BC140"), so its "Running" beside
another tier's restart failure is not read as all clear. `list_deployments` returns the steps as
`gatewaySteps`.

**The Deploy dialog** reads `RestartGroupPlan` (`Services/Gateways/`) through
`DeliveryService.GetRestartPlanAsync` once it is open: the tiers that restart, how, the scripts, and
the tiers the inventory reports on the same database (by `DatabaseKey`) that the group leaves out.
It warns, before the person commits, that those tiers restart and that everyone signed in is signed
out and loses unsaved work; a tier on the same database that the group leaves out is listed with
them, marked "not restarted". Before the gateway has reported valid settings it says at least the
linked tier restarts. Deploy stays disabled until the plan has been read, and if the read fails the
dialog says so and keeps it disabled, rather than showing the online wording. On Production the
acknowledgement reads "I understand this installs into CRONUS's live Production environment and
signs everyone out of the 2 service tiers listed.".

**`shared_restart` is enforced by AL Workbench.** `RestartGroupPlan.Refusal` refuses when another
Solution has a live on-premises environment linked to one of the group's tiers and the database does
not set `shared_restart = true`. It is checked when the deployment is made (`ResolveReleaseAsync`)
and again when it runs (`GatewayDeliveryTarget.ReadStatusAsync`), as AL Workbench's own refusal,
not "The gateway refused". The Deploy dialog shows it as an error and disables its button. Solutions
are counted under the caller's query filter, so another organisation's links are not seen; a
gateway belongs to one organisation, so that only matters for a tier two organisations' gateways
both report. A rule on the gateway was considered and rejected: the only thing it can see is the
tenant list, and several tenants of one customer on one server is a normal setup that it would have
refused. The dialog gives a count of the other Solutions, not their names, so a deployment does not
tell one customer's consultant another customer's name; that differs from "Shared multitenant tiers"
above, which says to list them. The refusal leads with the effect ("This deployment would restart
service tiers (the named tiers) that another Solution also uses, and sign out its users."), then what
to do next (ask the customer's IT to allow shared restarts for this database in the gateway's
settings file, `shared_restart`, or to give each Solution its own service tiers, then try again; a
licence import, #1369, says the same with "import"), and the dialog hides the Production
acknowledgement, since there is nothing to acknowledge.

**To verify on real servers:** that `powershell.exe -File` exits 0 for a script that ends with a
`robocopy` returning 1 and no `exit`, 2 for `exit 2`, and 1 for a `throw`; that `RequestAdditionalTime`
holds a service stop open while a tier starts; `Get-Service -ComputerName` against a remote BC 14 tier with the
service account's rights, and the error it gives without them; `Invoke-Command` with the same
script block where WinRM is on; how long a BC 14 tier with many companies takes to stop and start
against the 5 and 10 minute limits; that a service that never reaches Running surfaces as the
timeout and not as a hang; a before and an after script under 5.1 as the service account,
including one that writes to the error stream and one that calls `exit 2`; and that the folder
check accepts the default rights on `C:\Program Files\AL Workbench Gateway\scripts` and, by the rule for the folders above a script, on `C:\Program Files` and the drive root.

## Licences

**Upload and import (#1369).** On an on-premises environment a person who manages the Solution
uploads the licence file, sees where it goes (database or tenant, as the policy sets it), and
confirms that the listed tiers will restart. The `import-licence` verb runs `Import-NAVServerLicense` on the publishing
tier with `-Database` set as the policy's `licence_target` says (`NavDatabase`, `Master`, `Default`,
or `Tenant` with `-Tenant`), then the restart group (tiers only read a new licence on restart), then
a fresh inventory. A database-level import on a shared server reaches every tenant and follows the
shared-tier rule above. Format: `.flf` before BC 22, `.bclicense` from BC 22. Whether BC 22 and later
still accept an `.flf` has not been checked against a real server yet ("Licences, as built", To verify);
until it is, a file the target's version cannot import is refused with a message naming both, and
that rule follows the finding. The file follows the Solution's visibility. AL
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

### Licences, as built (#1369)

**Who and where.** The environment page of a linked on-premises environment has a **Licence** card on
its Overview tab: the licence the linked tier runs as the gateway last read it (licensed to, expiry,
serial number, when it was read, and the object ranges folded under "Object ranges (N)"), the last
upload (file, who, when, where it went, and how its import went: Waiting, Importing, Imported,
Imported with problems, Not imported, Not known, with a sentence for the consultant), and an outline
**Import licence...** button for people who can manage the Solution (`ProjectAccess.CanManageAsync`).
Anyone who can see the Solution sees the card; the file itself downloads (`GET /licences/{id}/file`)
only for a manager, and anything else answers 404. The meta row above the tabs carries "Licence
expires" (the date alone), and from 30 days before the expiry a warning sits above the tabs on every
tab, a danger alert once it has expired, with its own **Import licence...** button. While the last
upload is waiting or importing, that warning gives way to "A new licence is being imported", so it
does not argue with the card; the page re-reads the card every 5 seconds until the import ends, and
stops when the page moves on to another environment.

**Days are counted on the customer's calendar.** "Today", for the warning, the days left and the
notices, is the date in the Solution's Business Central time zone (`OeProject.BcTimeZone`, the zone
its update windows are in), since a licence runs out at the customer's midnight; UTC when the
Solution has none or it is not a known zone (`OnPremisesLicenceService.TodayFor`).

**Where the licence goes is shown, not chosen.** The policy file sets one `licence_target` per
database, so there is nothing for the person to pick: the dialog ("Import a new licence into
Production") says "Imported into the application database (set in the customer's gateway settings)"
(or the master database, for every tenant; the server's default database; the tenant X only),
"Imported by" the tier, and one hint under the file button: "Business Central 25 needs a .bclicense
file." The command carries the target
the person was shown (`licenceTarget`), and the gateway refuses it if its settings now say anything
else, so what runs is what was confirmed. A database with no `licence_target` refuses the upload.

**The dialog** (`UploadLicenceDialog`) reads `OnPremisesLicenceService.GetImportPlanAsync`: target,
importing tier, version and format, and the `RestartGroupPlan`. It warns, as the Deploy dialog does
and in the same shape, that Business Central reads a new licence only when it starts, lists the tiers
that restart (and any tier on the same database left out of the group), and that everyone signed in
is signed out. The file input takes `accept=".flf,.bclicense"`; the file's format and size are
checked as soon as it is chosen, by the same rule the service applies. Anything that would refuse the
import is shown as an error and holds **Import licence** disabled.

**Checked by AL Workbench, in this order, before anything is stored or sent**
(`OnPremisesLicenceService.UploadAndImportAsync`): manage rights (`ProjectAccessDeniedException`);
then the file, keyed `file` (a name, not empty, at most 1 MB, a `.flf` or `.bclicense`, and the
format the importing tier's version takes, naming both); then the environment, keyed `environment`:
linked, the tier and tenant still reported, the gateway online, settings reported and valid, the
publishing tier listed with `licence`, a `licence_target` set, the tenant allowed for a `Tenant`
target, `restart` on every group tier, the publishing tier running, its version known, and finally
`RestartGroupPlan.RefusalFor("import", ...)`. The upload row and the command are written in one
transaction, then the gateway is woken, so a crash in between leaves neither.

**`shared_restart` applies to every target, `Tenant` included.** A `Tenant` import changes the
licence of one tenant only, but the restart that follows restarts the service tiers, and a restarted
tier signs out every tenant on it. So a Tenant import whose restart group reaches another Solution's
environment is refused exactly as a deployment is, by the same `RestartGroupPlan` rule, before any
command is sent; the gateway never sees it, because only AL Workbench knows which Solutions share a
tier. The refusal leads with the effect ("This import would restart service tiers that another
Solution also uses, and sign out its users.") and names `shared_restart` as a line in the gateway's
settings file, since AL Workbench has no screen for it; a deployment's says the same with
"deployment".

**The `import-licence` verb** (`Core/Licence/ImportLicenceVerb.cs`) checks the command against the
settings again (the publishing tier, `licence`, the tenant for a Tenant target, `restart` on every
group tier, the confirmed target), the file (one, present, at most 1 MB, its SHA-256, and the
format for the tier's version), then runs **import, restart, refresh** in that order:
`Import-NAVServerLicense -ServerInstance -LicenseFile -Database <target>` (with `-Tenant` for a
Tenant target, refusing rather than importing server-wide when the cmdlet has no `-Tenant`, and
`-Force` when the cmdlet has it, so a non-interactive session is never asked to confirm) on the
publishing tier, in the tier's PowerShell; then the database's restart group through
`RestartGroupRunner`, after the **whole** policy check again (`IGatewayPolicy.Check`: change hours,
the production and agent rules, the permissions, read-only), not only the restart permissions, so a
settings file changed during the import, or change hours that closed meanwhile, stop the restart the
way they would stop a new command (the tiers keep running and the command fails with the licence
imported); then it reads every tier of the database again (`GatewayCommandContext.RefreshInventory`)
and posts the inventory, so the page shows the new licence before the result arrives.

Only Business Central's own answer counts as "not imported": the script's `{ ok: false }` ("Nothing
was restarted, so the service tiers keep the licence they had."). An import that was **cut off**
before it answered (PowerShell timed out, ended without a result, or the command or the gateway was
stopped) may have reached the database, so it restarts nothing and reports `importUnknown`: "It is
not known whether the licence was imported ...". A stop is caught and returned as that result rather
than thrown, so the gateway keeps it and sends it when it can. A failed restart or reading afterwards
fails or notes it with "The licence was imported, but ...". The result (`GatewayImportLicenceResult`:
target, `imported`, `importUnknown`, `restart`, `refreshed`, `refreshMessage`) is additive; the
schema stays 1. No before or after scripts run for a licence import.

**How the card reads the command.** Queued or claimed is Waiting, running is Importing; a result with
the import done is Imported or Imported with problems; `importUnknown` is **Not known**; Business
Central's refusal is Not imported with its reason. A failed command with **no** result is Not imported
only when the gateway refused it before running anything (its refusals all end "Nothing was
changed."); otherwise (the gateway restarted or went silent mid-run and the sweep failed it) it is
Not known, and so is an upload whose command is gone with its gateway. The gateway's refusal texts
name `gateway.toml` and its sections, which are customer IT's, so the card says "The customer's
gateway settings did not allow this import ... Ask the customer's IT to check them." instead, and the
raw text stays in the command's log on the gateway's page; a restart the settings stopped is said the
same way.

**Storage and audit.** `oe_project_licenses` (migration `20270208000000_AddProjectLicenses`) keeps
each upload once, never changed afterwards, so its one audit row (`ProjectLicense`, "Licence file")
is the upload itself. It stores the target and, for a Tenant import, the tenant as sent, so "Last
upload ... into the tenant X" names where it went even if the environment is later linked elsewhere; the bytes are recorded as `ContentSha256`, never copied. How the import went is
read from the command, not stored on the row. The command carries its own copy of the file.

**Expiry notices.** `LicenceExpiryScheduler` (hourly per organisation, off with
`DISABLE_LICENCE_EXPIRY_NOTICES=1`) runs `LicenceExpiryNotifier`, which tells the Solution's followers
(category Solutions; in the app, and by email for those who chose it) 30 days before the expiry the
linked tier's licence states, 7 days before, and once on the day it has expired. Each threshold is
claimed on the environment row (`license_expiry_notice_for`, `license_expiry_notice_days`) with a
compare-and-set before anything is sent, so it sends once however often the sweep runs; if sending
then fails, the claim is set back the same way (only if it is still this sweep's), so the next sweep
tries again rather than losing that notice. A licence first seen inside a threshold sends only the
nearest one, and a new expiry date starts the thresholds again. Days are counted in the Solution's
time zone, as on the page. Unlinked environments are skipped.

**MCP.** `get_environment` gives an on-premises environment's `licence`: licensee, serial number,
expiry, when read, the object ranges, and the last upload (file, who, when, where it went, its import
state and message). `onPremisesTier` also carries the licensee. There is no write tool.

**To verify on real servers:**

- Whether Business Central 22 and later still import an `.flf`. Until then AL Workbench and the
  gateway refuse an `.flf` from 22 on, naming both formats; if 22+ accepts one, relax
  `GatewayLicenceFormats.Refusal` in one place.
- `Import-NAVServerLicense`'s parameters on BC 14 and on a current version: that `-Database` takes
  the four targets, whether `-Tenant` exists on BC 14 (the script refuses a Tenant import without
  it), and whether `-Force` exists and is what keeps a non-interactive session from being asked to
  confirm.
- That the cmdlet's error for an invalid or wrong-format file comes back as an error the script
  reports, not a prompt or a hang.
- That a tier reads the new licence after the restart, and that the inventory read straight after it
  shows the new expiry and ranges.
- That a `Master` import reaches every tenant of a multitenant server, and a `Tenant` import only
  the one tenant.

### Licensed object ranges, as built (#1370)

**One rule, two callers.** `LicenceRangeCheck` (`Services/Gateways/`) holds the whole rule as a pure
function (`Evaluate`: the build's objects, the licence's ranges, the tier's version), and two readers
that load what it needs: `ForBuildAsync` for a build and `ForDeploymentAsync` for one target. Both
read under the query filter; there is no new `IgnoreQueryFilters()`.

**Which objects.** The objects are the build's own. When the Object Explorer indexed the build, they
come from `oe_module_objects` of the build's release, which holds the build's apps and nothing else,
carried apps included. A build with no index (staged from a GitHub release, or one whose index was
removed) is read from the symbols of its own `.app` files (`oe_project_build_artifacts`, through
`AppPackageReader`, one file at a time). A file that cannot be read, such as an NEA-encrypted one,
makes the answer "not checked" for that build. Checked, each against the ranges of its own type:
`table` against **Table** (not TableData: a table object needs the object range, its data permissions
are another line), `page`, `report`, `codeunit`, `query` (**Query**) and `xmlport` (**XMLport**). A
`permissionset` is checked against **PermissionSet** ranges only when the licence lists at least one:
older licences predate permission set objects and say nothing about them, and flagging every
permission set against a licence that has no such line would be noise nobody can act on. Never
checked: enums, interfaces, every extension object (`tableextension`, `pageextension`,
`reportextension`, `enumextension`, `permissionsetextension`), and the kinds without an id (control
add-ins, profiles). On a tier whose major version is 14 or lower, an `enum` is checked against the
**Table** ranges; on a newer tier enums are not even read. The version is the tier's own, from the
inventory (`gateway_instances.version`), never the build's. An object read twice is one finding. On a
BC 14 hybrid server only the AL objects in the build are checked; the C/AL objects in the database are
not ours to read.

**Which ranges count: any permission.** The parser keeps a range's permissions as the text after
its two ids, trimmed, so which column a word stood in (read, insert, modify, delete, execute) is
gone. The rule is therefore "any permission": a range with no permission text at all counts (the
`.bclicense` layout prints none), and so does one with any word other than `None`, `No`, `0`, `N/A`,
`False` or a run of dashes (`Direct`, `Indirect`, `RIMDX`, `R----` all count; `-----` and `None` do
not). Text written as labels and values is judged by its values only, `:` separating them, so
`Execute: No` does not count and `Read: No, Execute: Yes` does. Reading the execute column
specifically would need the parser to keep positions; it is a to-verify item below rather than a
guess.

**When it is not checked.** "Not checked" is an answer with a reason, never a silent pass. The reason
is shown to the consultant as it is, so it names AL Workbench and the customer's server rather than
the gateway, and says what to do next; the build page and the Deploy dialog link the environment page
beside it. It is not checked when: the environment is no longer linked; the customer's server is not
connected (offline, revoked, waiting for approval); AL Workbench no longer sees the tier or its tenant
(tenant ids compared trimmed and in any case, as the inventory compares them); the licence was never
read; it was last read more than an hour ago (`OnPremisesTierFacts.StaleAfter`, four missed rounds);
its text was cut to size by the gateway; it has no ranges the parser could read; **only part of its
ranges could be read** (a line too long or too slow to match was skipped, or ranges past
`GatewayLicenceParser.MaxRanges` were dropped: the missing range may be the one that covers an object,
so flagging it would be a false refusal); the tier's version is unknown (the BC 14 rule needs it); or
the build's objects can't be read (no `.app` files, or one that does not open). The stored licence keeps
only the parser's problem sentences, so when it has any, the check reads the stored text again to learn
whether a range was lost (`ParsedLicence.RangesComplete`); no column was added for it. The read-again
reasons say that AL Workbench reads the licence every 15 minutes while the customer's server is
connected, and to check the environment page if that does not change: "Licence not checked for
Production. AL Workbench has not read the licence on CRONUS-BC11 / BC140 yet. It reads it every
15 minutes while the customer's server is connected; check the environment page if this does not
change." An online Solution (hosting set to Microsoft's cloud) and an online environment are never
checked at all, and get no answer rather than "not checked".

**On the build: a warning, per environment.** Once a build is ready and its objects indexed, the
import worker calls `ProjectBuildService.RecordLicenceCheckAsync`, for pipeline builds and
pull-request builds alike, before it prepares or makes any automatic deployment; a preview check is
skipped (it is never deployed). It checks each linked, not-missing on-premises environment of the
Solution that is **Production, or that a deployment pipeline drawing from the build's pipeline
targets** (a test tier included), each on its own, and stores the answers as
`oe_project_builds.licence_check` (jsonb, migration `20270210000000_RecordBuildLicenceCheck`): per
environment the outcome (`passed`, `outside_licence`, `not_checked`), the reason, the findings (at most
200 kept, the count whole), how many objects were asked about, the tier's version, when it was checked
and when the licence it was compared with was read. It is best-effort: anything thrown is logged and
the build stays ready. It is a snapshot of the day the build was made; deploying checks again. The
pipeline page's latest-build card shows, under the files, per environment: a warning headed by the
consequence, "**Can't be deployed to Production: 2 objects are outside the customer's licence.** AL
Workbench will not deploy this build to Production until the licence covers them. Ask the customer to
add them to their licence, or renumber them to object IDs the licence covers.", the objects (the first
20, then "and N more"), and "Checked 15 Oct 09:10, against the licence read 15 Oct 09:04."; "**Licence
not checked for Production.** (reason) Open Production"; or "Every object in this build is inside the
customer's licence on Production.". The findings read "Table 50120 \"CRONUS Tasting Note\" is not in
the licensed table range 50000-50099" (several ranges: "ranges 50000-50099, 50200-50249", the first
four then "and N more"; none: "is not in the licence, which has no query ranges") and, on BC 14, "Enum
50130 \"CRONUS Pallet Type\" is not in the licensed table range 50000-50099, which BC 14 checks enums
against". The finding keeps its sentence as well as type, id and name, because the sentence names the
licensed ranges as they were when it was checked.

**Automatic deployments say why where people look.** A deployment pipeline that deploys a new build
on its own, or prepares one for approval, goes through the same refusal, so a build outside the licence
is neither deployed nor prepared. That is not left to a log line: the build page shows the warning for
the pipeline's tier (above), and the deployment pipeline's own page shows, while its newest deployable
build is the refused one, "**Build #42 can't be deployed to Test: 2 objects are outside the customer's
licence.** AL Workbench will not deploy it here until the licence covers them, so no deployment of it
was made or prepared. See which objects on the build".

**At deploy time: a refusal, before anything is made or sent.** `DeliveryService.ResolveReleaseAsync`
checks the build against the **target** environment (whatever its type) after the `shared_restart`
rule, so a deployment made by hand, an approval, a prepared deployment and `deploy_build` are all
refused there, keyed `Build`, with no deployment row and no gateway command: "Build #42 can't be
deployed to Production: 2 objects are outside the customer's licence there, so nothing was sent.
Table 50120 ... ; Report 50060 .... Ask the customer to add these objects to their licence, or renumber
them to object IDs the licence covers, then deploy again." **The refusal names the first 200 objects,
then "and N more"** (`LicenceRangeCheck.MaxStoredFindings`): the issue asked for every object, and the
maintainer capped it, because a refusal is a validation message and an MCP error, and a build with
thousands of objects outside a licence (a renumbering gone wrong) would make either unreadable. The
whole count is always given, and the build page and `get_solution_build` carry the same first 200. The
run checks again when it claims the deployment (`PublishAsync`, after the live status re-read and before
the hand-off transaction), because the customer's licence can change while a deployment waits for its
window: a refusal there fails the deployment with the same sentence, every app Skipped and no command
queued. A licence that cannot be checked does not refuse; the run's log says "Licence not checked for
Production. (reason)", and a pass says "Licence checked against Production: every object in this build
is inside the licence." The Deploy dialog reads `DeliveryService.CheckLicenceAsync` for the picked
build (again whenever another is picked, Deploy disabled meanwhile; a read for a build no longer picked
changes nothing): outside the licence is an error naming the objects (the first 20, then "and N more")
and when AL Workbench read the licence, which holds Deploy disabled and hides both the restart warning
and the Production acknowledgement; "not checked" is a note with the reason, a link to the environment
page, and "You can still deploy. If the licence does not cover every object, Business Central refuses
the install." A GitHub release is not checked in the dialog, which lists its files before they are
downloaded; deploying it checks the files it stages.

**MCP.** `get_solution_build` carries `licenceChecks` (null when none was made), each with the
environment, `outcome`, `reason`, `findings` (at most 200), `outsideCount`, `checkedObjectCount`,
`summary`, `checkedAt` and `licenceReadAt`. `list_pipeline_builds` and `list_solution_builds` carry a
shorter `licenceChecks` per build: the environment, `outcome`, `outsideCount`, `summary` and
`checkedAt`, without the objects, so a list of twenty builds stays small. `deploy_build` returns the
refusal text ("Couldn't deploy that build: Build #42 can't be deployed ..."). Agents still cannot deploy
to an on-premises Production tier, so for them the refusal is met on test tiers.

**Also in this change.** The pipeline page scrolled sideways at phone width, with or without the
licence warning: the build history table set the width of the page's main column. The column is now
pinned to the screen and the table scrolls inside its own card.

**To verify on real servers:**

- The object type words `Export-NAVServerLicenseInformation` prints for queries, XMLports and
  permission sets on a BC 14 `.flf` and a current `.bclicense` (`Query`, `XMLport`, `Permission Set`?),
  and whether the parser's list of types needs another spelling.
- Whether the printed ranges carry their permissions in columns (RIMDX) on either format, and if they
  do, whether Business Central requires **execute** on the Table, Page, Report, Codeunit, Query and
  XMLport range to publish; if so, the parser should keep column positions and `LicenceRangeCheck.Grants`
  should ask for execute. Also whether any layout writes permissions as labels and values.
- That a BC 14 tier refuses to publish an extension whose enum id is outside the licensed table
  ranges, and that BC 15 and later do not.
- Whether a current version checks permission set objects against a PermissionSet range, and what a
  licence without such a range does with one.
- That a table object needs the **Table** range and not only **TableData**.
- The error `Publish-NAVApp` gives for an object outside the licence, so a deployment that got past an
  out-of-date check fails with a sentence a consultant recognises.

## Service tier actions (#1371)

Restart, stop and start a tier, or restart the whole group, from the environment page. The
confirmation shows the open sessions across the database's tiers; Production asks again, as online
Production deliveries do. Manage rights only, audited, and the policy file still decides. Changing
server configuration (`Set-NAVServerConfiguration`) is out of scope. **Before it sends a
`restart-group` command, AL Workbench must refuse a group that `RestartGroupPlan.Refusal` refuses
(`shared_restart`)**: the gateway does not check it ("The restart group, as built").

### Service tier actions, as built (#1371)

**Who and where.** The named user is a consultant whose customer rings to say Business Central is
stuck. On an on-premises environment's Overview, the Service tier card offers **Restart service
tier...** in its head (or **Start service tier...** when the tier is stopped), and under the facts
**Restart both service tiers...** / **Restart all N service tiers...** (when the database's restart
group has more than one tier; the dialog's title and button use the same words) and, last and set
apart by a separator, **Stop service tier (stays stopped)...**. Only people who can manage the
Solution see the buttons; everyone else reads a line saying who can and whom to ask. Nothing here is
a primary button. A greyed-out button says why under the row, once per reason, except the shared
rule below, which is said once directly under the card head, above the facts and the buttons.

**The tier's state after an action.** The inventory reads a tier every 15 minutes, so straight after
a stop it still says Running. Until the inventory reads the tier again, the card takes the state from
the last finished action on it (`GatewayServiceTierResult.state`, or a restart group's tier
outcome), so a tier just stopped offers Start at once and its State reads Stopped.

**The confirmation** (`ServiceTierActionDialog`, over `ConfirmDialog`) lists the tiers it touches,
then the sessions ("14 sessions are open on this service tier (23 across both service tiers on this
database)."): open on the tiers it touches, and in all across every tier on the same database
(`DatabaseKey`), as the last inventory counted them (whole-tier counts, every tenant included), with
when that was. A touched tier without a count makes it "at least N", and it says a tier did not
report a count. It says those people are signed out and lose unsaved work. When the action reaches
Production, every action that signs people out (restart, restart all, stop) also needs the box
ticked, as `ReleaseBuildDialog` asks for online Production; Start does not, since it signs nobody
out. **Production is decided by the tiers, not the page** (decision, from review): the action reaches
Production when this environment is Production, when another live environment linked to a touched
tier is (a Test environment's tier can carry the same Solution's Production tenant), or when the
customer's settings call a touched tier `environment = "production"` (a restart of every tier from
Test can reach a production tier); the tick box names which. **The tick box is UI-only** (decision):
`RequestAsync` does not ask for it, exactly as online Production deliveries do not; it guards a
person's slip, and every caller is a person on this page, since there is no MCP write tool. The
confirm button stays disabled until the plan has loaded without a refusal.

**What is sent** (`OnPremisesTierActionService.RequestAsync`): `restart-instance`, `restart-group`,
`stop-instance` or `start-instance`, with the linked tier's machine and instance only; the group's
other tiers come from the customer's settings file. In one transaction it queues the command,
writes an `oe_environment_upgrade_actions` row (`RestartServiceTier`, `RestartServiceTiers`,
`StopServiceTier`, `StartServiceTier`) linked by the new nullable `gateway_command_id` (migration
`20270212000000_LinkServiceTierActionsToCommands`, no foreign key, as `oe_project_licenses`), and an
audit row on the environment (who, when, the action, the tiers, the command id). That table is not
audited by the interceptor, so the audit row is written by hand, as `ProjectConnectionService`
does for its environment actions. The gateway is woken after the commit. If the save fails after the
command was queued, the change tracker is cleared, so the rolled-back command and rows do not ride
along on the next save of the same scoped context; the licence upload does the same.

**One thing at a time on a tier.** Before queuing, inside the transaction and under
`pg_advisory_xact_lock` keyed by the gateway, the service reads the gateway's unfinished commands
that stop, start or restart tiers (the four tier verbs, `publish-install` and `import-licence`).
A deployment, a licence import or a restart of every tier reaches the whole restart group when the
tier it names is in it; the single-tier verbs reach their own tier. If any reaches a tier this
action touches, it is refused with what is running ("A deployment to this server is still running;
try again when it has finished."). The same check greys the buttons out. Deployments and licence
imports do not take this lock themselves, so one of those queued in the same instant as a tier
action can still get past this check (to verify, below).

**The outcome is read, not stored.** As for licences, the history row stays as written and
`ServiceTierActionOutcome` reads the command each time: queued or claimed reads *Waiting*, running
reads *Running* (both `Pending`), succeeded reads `Sent` with a sentence ("CRONUS-BC01 / BC250
restarted and is running."), failed or expired reads `Failed` with the gateway's reason, and a
refusal by the customer's settings reads the same sentence licence imports use, without their file.
The page polls while an action is in flight and refreshes the history when it ends. In the history a
running action shows the Running pill and a spinner, and a restart of every tier names its database
("Restarted every service tier on database CRONUS-SQL / CRONUS").

**Refusals, all before anything is queued**, in this order: no tier linked, the tier no longer
reported, the gateway not approved or offline, no valid settings file reported, a touched tier
whose settings do not allow `service_control` (the gateway would refuse it too), something already
working on a touched tier (above), and the shared rule below. Each is a `PlanValidationException`
keyed `environment`; the same sentence shows on the card and in the dialog.

**The shared rule (decision).** Restart all uses `RestartGroupPlan.Refused` exactly as deployments
do: refused when another Solution has a live environment on any tier of the group and the database
does not set `shared_restart`. A **single-tier restart or stop is refused the same way**, but
counting only Solutions linked to *that* tier (`RestartGroupPlan.LoadForTierAsync`): stopping a
tier signs out every tenant on it, which is the harm `shared_restart` exists to prevent, while
other tiers of the database keep running. Start is never refused for this reason; it signs nobody
out. `shared_restart` on the database allows all three. The refusal reads "Restarting CRONUS-BC11 /
BC140 would also sign out people using another Solution on the same service tier. Ask the
customer's IT to allow shared restarts for this database in the AL Workbench gateway's settings (the
shared_restart setting), or to give each Solution its own service tier."

**The gateway side.** `ServiceTierVerb` handles the three single-tier verbs: it checks the
settings file first (`service_control` on the tier, through `PolicyEnforcer`; a refusal ends
"Nothing was changed." before any process runs), then hands the tier to
`RestartGroupRunner.RunTierActionAsync`, which reuses the restart group's stop and start steps,
remote methods and timeouts (5 minutes to stop, 10 to start). Restart is a one-tier restart group
and keeps its run-once ledger entry, so a restart cut short by the gateway stopping is started on
its next start. **A deliberate stop is never written to the ledger** (decision), so
`StoppedTierRecovery` leaves it stopped; a stop that does not finish is reported and not started
again. The result (`GatewayServiceTierResult`) carries the action, the tier, whether it was done,
the state before and after, and a message; `GatewayRestartTierResult` gained `before`. Both are
additive; the schema stays 1.

**Known edge.** A restart group (a deployment, a licence import or Restart all) still starts a tier
that someone stopped on purpose, since every tier in the group is stopped and then started. That is
the existing group behaviour and is left as it is; the person who stopped it sees it running again
on the card.

**MCP.** No write tool. `list_environment_history` and `get_environment` show the actions with their
tier and outcome ("MCP parity").

**To verify on real servers:**

- How long `Stop-NAVServerInstance` takes on a tier with many open sessions, against the 5-minute
  stop timeout, and what the session count reads straight after a restart.
- That stopping a tier through the service manager on another computer leaves it stopped after the
  next inventory, and that the inventory reads it `Stopped`, so the card offers Start.
- That the dialog's session count, taken from the last inventory, is close enough to what the
  consultant sees in the Business Central admin tool to be trusted.
- What the gateway does when a deployment and a tier action on the same tiers reach it together
  (deployments and licence imports do not take the per-gateway lock); whether it runs them one
  after the other or both at once.

## MCP parity

- **Reads:** `list_environments`, `get_environment`, `get_solution` and the deployment reads include
  on-premises environments, marked `onPremises: true`, with the gateway's online state. As built
  (#1366): `list_environments` and `get_environment` carry `onPremisesTier` (gateway, whether it is
  online and when it was last seen, server, instance, tenant, the customer's own word for the tier,
  database, when it was read, whether it is still reported, and the licence expiry); `get_solution`'s
  environments carry `onPremises` and `gatewayOnline`.
- **Deploy:** `deploy_build` and `list_deployments` work for on-premises targets **except
  production**. `agents_may_deploy_to_production` does not cover on-premises targets: a deployment
  there also restarts the customer's tiers, and that starts as a person's decision in the web UI.
  As built (#1367): `deploy_build` refuses an on-premises Production target from an agent whatever
  the organisation's setting, and otherwise runs the same path as the web UI, including the
  offline-gateway failure; `list_deployment_pipelines` carries `targetIsOnPremises`, and
  `list_deployments` reports `refused` app statuses and `tiersRestarted`, and from #1368
  `gatewaySteps` (each script and tier restart, in order).
- **No write tools** for licences or tier actions in this milestone, as environment actions are
  web-only today. As built (#1371): `list_environment_history` and `get_environment`'s recent
  history carry the service tier actions (`RestartServiceTier`, `RestartServiceTiers`,
  `StopServiceTier`, `StartServiceTier`) with the tier in `serviceTier`, `Pending` until the gateway
  has finished, then `Sent` or `Failed` with the outcome. As built (#1369): `get_environment` carries an on-premises environment's
  `licence` (licensee, serial number, expiry, object ranges and the last upload with its import
  state), and `onPremisesTier` its licensee. As built (#1370): `get_solution_build` carries the build's
  `licenceChecks` with the objects outside the licence, `list_pipeline_builds` and
  `list_solution_builds` the outcome per environment without them, and `deploy_build` returns
  the licence refusal ("Licensed object ranges, as built").

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
with a certificate the customer trusts, `allow_unsigned = false` and that certificate in `signers`
(a valid signature from anyone else is not enough). The setup guide says this to
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
Signing, now Artifact Signing). Until it is set up, builds ship unsigned and are marked "not code-signed
yet" (the decision in #1359 said "preview only"; "preview" is now reserved for `0.0.0-*` builds).
The gateway does not update itself; the workbench shows "Out of date" and customer IT updates it.
"Versions and updates" below has how, as built (#1363).

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
release workflow has a Windows job that publishes the gateway self-contained single-file for
`win-x64`, signs it and attaches it to the release; the Gateways page links the build matching the
running version with its SHA-256. The contracts carry a minimum supported gateway version; an older
gateway is refused with a message saying to update. "Versions and updates" below has the detail.

### The Windows service, as built (#1362)

The frame: it registers, keeps its key, heartbeats, polls, downloads and checks files, and reports.
As first built it ran no verb; a command whose verb has no executor still fails with "This version
of the gateway cannot run '<verb>' commands yet". Verbs plug in behind `ICommandExecutor`: the
service hands the loop a `VerbExecutors` that routes each verb to its own (`publish-install` since
#1375), and `refresh-inventory` stays the loop's own because the loop also runs it on a schedule.

**Commands customer IT runs** (from an administrator prompt), in either order:

- `install [--account DOMAIN\name$]` creates the service `ALWorkbenchGateway` ("AL Workbench
  Gateway"), delayed automatic start, restarted by Windows after a crash but not after a clean stop. The recovery options also apply
  when the service stops with a failure exit code (`sc failureflag ... 1`): an unexpected error ends
  the loop with exit code 1 instead of a clean stop.
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
- `check` reads `gateway.toml` and tries each local tier, changing nothing (#1373, below "The
  policy file and the runner, as built").
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
`logs\` writable by the service account too. Any local user may create a folder in
`%ProgramData%`, and its owner can always rewrite its rights, so `install` and `register` make
Administrators the owner of the folder and of everything in it, give the subfolders no rules of
their own beyond the service account's change right (they inherit the rest), and refuse with a
plain message if the owner cannot be set or the folder holds a link. Every service start checks it
again: an owner other than Administrators or SYSTEM (or the service account, for what it wrote in
the writable folders), an inherited root, or a write right for anyone else, and the service logs
what it found (event 2008) and exits with a failure code instead of running.

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
3. Refused if the local policy file does not allow it (#1373), with the reason; recorded as
   started-and-finished like 2.
4. Recorded as started, on disk, before anything else happens.
5. Files downloaded into its work folder, each stopped as soon as it exceeds its announced size,
   then checked by size and SHA-256. Any mismatch fails the command; the verb never sees the file.
   A download has 30 minutes in all, body included, and gives up if nothing arrives for 2 minutes
   (a connection dropped without a word), then is retried like any unreachable answer. Each file
   is opened read-only, sharing read only, before it is hashed, and stays open until the verb
   ends, so on Windows nothing can change or delete it between the check and its use.
6. The executor runs. Its progress lines are batched: at most one post every 3 seconds, at most 200
   lines a post, lines over 2,000 characters cut, at most 10,000 lines held (more are counted and
   reported as left out). Each batch is numbered, and one whose post failed is sent again
   unchanged before anything newer, so a lost answer never doubles lines. A 409 on progress stops
   the verb.
7. The result is saved in the run-once file, then posted, retried with back-off until it arrives,
   including through a 401 or a 403 while a new key waits for approval (a 409 means AL Workbench
   already closed it, and is accepted), then the command is marked finished.

A stop while the verb runs leaves the command started-not-finished, which the next start reports
as interrupted. A stop after the verb finished does not lose its result: the post gets 15 more
seconds (the host waits 30 for the service to stop), and a result that still has not arrived stays
saved. The next start lists it as running, sends it before polling for anything new, and logs that
it did (event 1005); it is not reported as interrupted.

**The local log**: the Windows event log (Application, source "AL Workbench Gateway", created by
`install`) and a file per day in `logs\`, kept 30 days. Each command gets a line when it starts and
when it ends, naming the verb, who asked for it, and the outcome, with fixed event ids (1000
started, 1001 succeeded, 1002 failed, 1003 refused, 1004 interrupted, 1005 result sent after a
restart; 2007 stopped on an unexpected error, 2008 data folder rights loosened; 3000 to 3003 the
policy file, above) so customer IT can
filter on them. "Who asked" travels with the command: `GatewayCommandMessage.requestedBy` (the requester's
display name) and `startedByAgent`, added for this; an older gateway ignores them.

**Proxy and certificates.** One `HttpClient` with the system proxy (WinHTTP / Internet Options, or
`HTTPS_PROXY`), the service account's own credentials for a proxy that asks, and Windows' certificate
validation, so TLS inspection with the company's root certificate works.

Tests: the core library's in `ALDevToolbox.Tests/Gateways/` against a fake workbench and a clock
whose waits return at once, and one end-to-end test in `Endpoints/GatewayServiceFrameEndpointTests`
that drives the gateway's own code against the real `/gateway` routes. The Windows host has no
automated tests: the key store, `sc.exe` and the event log need Windows.

### Versions and updates, as built (#1363)

**One version for both.** A gateway is published with every AL Workbench release, from the same
tag, and reports that version: `release.yml` passes the tag (`v12.1.0` becomes `12.1.0`) to the
image as `RELEASE_VERSION` and to the gateway's `dotnet publish` as `ReleaseVersion`. So "the
gateway this AL Workbench expects" is simply its own version, and there is no compatibility table
to keep. A staging preview reports `0.0.0-staging.<commit>` and a local build `0.0.0-dev`.

**Comparing.** `GatewayVersion` (contracts) orders versions the semantic-versioning way: numbers
as numbers, a pre-release before its release (`12.1.0-rc.1` < `12.1.0`), numeric pre-release parts
as numbers (`rc.2` < `rc.10`), build metadata after `+` ignored. **Major version 0 is a preview**
(local or staging) and is never compared with a release: never refused for its version, never
called out of date, so a developer can point a local gateway at any workbench.

**What the Gateways page says**, in the Version column, from the version the last heartbeat
reported (`GatewayBuild.StateOf`):

| State | When | Shown |
| --- | --- | --- |
| Current | the workbench's version or newer, or the workbench is itself unstamped | the version |
| Out of date | older than the workbench, at or above the minimum | "Out of date", a link "Update to x" to the download card |
| Unsupported | below `GatewayProtocol.MinimumGatewayVersion` | "Unsupported", "AL Workbench refuses this version. Update it" (linked) |
| Preview / unknown | major 0, or nothing parseable reported | the version as reported |

The connection status (Online, Offline, ...) stays in its own column; an unsupported gateway is
also Offline before long, because its heartbeats are refused.

**The minimum, and the refusal.** `GatewayProtocol.MinimumGatewayVersion` (`11.19.0`, below which
no gateway was ever released) is raised when a change makes older gateways unsafe or unable to
work, never above the release that carries the change. Every gateway request carries its version
in the `ALWorkbench-Gateway-Version` header (set once on the `HttpClient`, beside the user agent).
`GatewayVersionGate`, middleware on `/gateway/*` ahead of authentication, answers a release below
the minimum with **426 Upgrade Required** and a `GatewayErrorResponse` keyed `version` naming the
version needed and where to download it. 426 because it is exactly RFC 9110's meaning ("might be
willing to do so after the client upgrades") and is distinct from 401 (revoked or clock), 403
(waiting for approval) and 400, so the gateway can tell it apart. It runs before the token is
read so a gateway too old to build today's token still hears why; that is also why it is a
header and not a body field. The RFC's `Upgrade` response header is not sent: it names
connection protocols, is hop-by-hop (Caddy drops it) and is forbidden in HTTP/2. A request with
no version header is let through to the usual checks; no released gateway omits it, and the
version is not a security property: a gateway that claims a wrong version only refuses itself
work. A refused registration leaves the code unused, and a refused heartbeat does not mark the
gateway seen.

**The gateway's side.** A 426 becomes `GatewayOutdatedException`. The loop logs event **2009**
(error) with the workbench's words and how to update, and backs off as for a 401 - it does not
stop, so the service is still there, still saying why, when someone looks. A result refused this
way stays saved and is retried; an updated gateway sends it on its first start. `register`
prints the reason and keeps no key.

**The download.** The release job publishes `ALWorkbench.Gateway.exe` (self-contained, single
file, symbols embedded), signs it when signing is set up, verifies the signature with
`Get-AuthenticodeSignature` (a configured but failed signing fails the release), then hashes the
signed file. The image build receives the hash and whether it was signed (`GATEWAY_SHA256`,
`GATEWAY_SIGNED`, stamped as assembly metadata like the release version), and the GitHub release
gets three assets: the program, `ALWorkbench.Gateway.exe.sha256` (`sha256sum` format) and
`gateway.example.toml`, plus a note above the generated release notes with the hash and, while
unsigned, "Not code-signed yet". The asset name never changes, so the service's
path keeps pointing at the right file after an update.

The Gateways page (`GatewayDownloadLink`) links that asset on the release matching the running
version, shows the SHA-256 in upper case with the `Get-FileHash` command to check it, and says
plainly when the build is not code-signed yet. A local or staging AL Workbench has no release to
link to and says "There is no gateway download for this AL Workbench, because it is not a
released version" instead of a broken link, and an unsupported row there says which version to
ask for instead of linking to a download that does not exist. The link is in the first install
step when adding a gateway and in a "Download the gateway" card under the list, which also
carries the update steps; the empty state stays short (three one-line steps and Add gateway). It is an outline button: Add gateway stays the page's one primary.

**Updating is manual.** No self-update (supply-chain risk; an MSI and auto-update are out of
scope). On the gateway's server, as an administrator:

1. Download the new `ALWorkbench.Gateway.exe` from Administration → Gateways and check its SHA-256.
2. Stop the service: `sc stop ALWorkbenchGateway` (or Services, "AL Workbench Gateway").
3. Replace `ALWorkbench.Gateway.exe` in its folder with the new file.
4. Start it: `sc start ALWorkbenchGateway`.

The registration, key, settings, policy file and run-once state are in `%ProgramData%` or beside
the program and are kept, so nothing needs approving again; a command interrupted by the stop is
reported as interrupted, as after any restart. The setup guide (#1376) repeats this for customer IT.

**Signing** uses Azure Artifact Signing through `azure/artifact-signing-action` after an OIDC
`azure/login` in a job bound to the `gateway-signing` environment; the repository variables and
the federated credential it needs are listed in `PROJECT.md`, "Releases and image publishing".
Staging builds are never signed and are kept as a workflow artifact on the run, not a release.

### The setup guide, as built (#1376)

`/docs/gateway-setup` (`Components/Pages/Docs/GatewaySetupDocs.razor`, the docs archetype) is what
customer IT reads before agreeing to install a gateway and while installing it: what it does and
never does, what the server needs, the service account, install / register / approve / check,
the settings file setting by setting with two examples (one server; two tiers on one database with
a restart group and before and after scripts), who may change the files, updating, removing, what
happens offline or revoked, and the event ids. The download block is the Gateways page's own
`GatewayDownloadLink`, so the hash and "not code-signed yet" read the same in both places, and the
signature check is shown only for a signed release. Linked from Administration → Gateways in the
add card, the empty state and the download card, and found by the search box (`DocsPaletteSource`).

**Signed in to read.** It carries `[Authorize]` like `/docs/search`, so someone without an account
cannot open it; `/docs/mcp` is the one anonymous docs page. Whether to make it anonymous, so a
consultant can send customer IT the link before they have an account, is open: the page reads no
data and would need an entry in `RouteAuthorizationTests`.

**Held to the gateway.** `GatewaySetupDocsTests` checks that each settings table lists exactly the
settings the parser knows (read from the parser's own "The settings are: ..." answer to an unknown
key), the permissions table exactly `PolicyWords.Permissions`, that every accepted value word is on
the page, and that both examples parse without a mistake.

**What it does not promise.** Where this document lists something under "To verify on real servers",
the guide says it has not been confirmed or leaves it out: the Business Central rights the service
account needs (it says so, and points at the gateway page's tier read as the first proof), PowerShell 7
from Business Central 24 (stated as what the gateway expects, with `powershell` to override), and
Windows checking an `.app`'s signature (stated as fails-closed). The minimum Windows version is
stated conservatively as Windows Server 2016, the first with Windows PowerShell 5.1 built in.

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
- **Connection:** outbound HTTPS long-poll; commands as rows; inventory pushed on its own route (`POST /gateway/inventory`), not the heartbeat (#1365).
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
