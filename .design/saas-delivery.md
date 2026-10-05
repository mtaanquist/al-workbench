# SaaS delivery — publishing builds to a BC environment

> **Status: shipped.** The delivery pipeline is built and running. Implementation lives in
> `Services/ObjectExplorer/Delivery/DeliveryService.cs`, `DeliveryScheduler.cs`, `DeliveryWorker.cs`,
> `DeliveryQueue.cs`, and `ReleasePipelineService.cs`, with the BC API clients under
> `Services/ObjectExplorer/Bc/`. The entities are `OeProjectDelivery`, `OeProjectDeliveryResult`, and
> `OeReleasePipeline`; the maintenance-window math is the `UpdateWindow` value object; MCP tools
> expose the surface to agents. It extends `OeProject` (the customer) and `OePipeline` (the build
> config) so a successful build can be published straight to a Business Central SaaS environment via
> the **Admin Center API's App Management surface**, on a schedule that avoids the customer's working
> hours. The automation API that published v1 is gone: Microsoft is removing its upload surface, and
> the replacement is not company-scoped, so the company went with it.
>
> The sections below are the original design proposal, kept as the record of intent; where a detail
> drifted from what shipped, the code is the source of truth.

## Vocabulary

"Release" used to mean four things in this product, and two of them were this document's. On
2026-09-23 the maintainer renamed those two, following CLAUDE.md's Solution/Project rule:

| A person or an agent reads | The code keeps |
| --- | --- |
| **Deployment pipeline** - the configured target: a build pipeline's builds (or a repository's GitHub releases) into one environment | `OeReleasePipeline`, `oe_release_pipelines`, `ReleasePipelineService`, `release_pipeline_id` |
| **Deployment** - one run of it, numbered in the pipeline's history ("Deployment 3"); the verb is **Deploy** ("Deploy", "Deploy again", "Deploy now", "Schedule deployment") | `OeProjectDelivery`, `oe_project_deliveries`, `DeliveryService`, and "delivery" throughout the code |
| Routes `/pipelines/deployments` and `/pipelines/deployments/{id}`; the build pipelines list at `/pipelines/builds`; the Pipelines dashboard at `/pipelines` | `/releases` and `/releases/{id}` redirect (`LegacyRedirectEndpoints`) |
| **When you deploy** (pipeline card), **When deployed** (list column), **When a deployment installs** (editor), with values as sentences: "Installs right away", "Installs in the delivery window", "Installs with the next minor Business Central update". Chosen 2026-09-24 because "Installs run: Right away" read as "installs run when a build lands"; nothing deploys until a person presses Deploy or approves a prepared deployment. The sheets still draw "Installs" / "Installs run"; the port diverges on purpose. | `DeploymentSchedule`, `DeliveryModeDisplay.ScheduleSentence` (the short `Schedule` wording stays for the Deploy dialogs, where "Installs: Right away" has its verb from the dialog) |
| MCP `list_deployment_pipelines`, `list_deployments`, `deploy_build`, `list_recent_deployments`, and `deploymentPipelineId` / `deploymentId` on the wire | The C# tool methods and the frozen tool keys `ToolKey.Pipelines` / `ToolKey.Releases` (persisted by name in the disabled-tools settings) |

The sidebar groups the two under one **Pipelines** parent with the children **Builds** and
**Deployments**; the parent links to the Pipelines dashboard at `/pipelines` (see "Pipelines
dashboard" below).

Two meanings of "release" stay, because they are other people's words: an **Object Explorer
release** (a Business Central version ingested for browsing) and a **GitHub release** (a tagged
release on a repository, which a deployment pipeline can install from; `list_github_releases` and
`stage_github_release` keep their names). State words ("Deployed", "Handed to Business Central",
"Failed", "Scheduled") and "Delivery window" are unchanged. Entity and column names below are the
code's, so "delivery" in a table or class name is a deployment.

## Goal & scope

When a pipeline's build succeeds, upload and install the compiled `.app`s into a chosen BC SaaS
environment, automatically, inside a maintenance window — no manual "download the zip and upload it
in the admin center" step.

**In scope (v1):** per-tenant extension upload + install + deployment-status polling, for the apps a
pipeline already compiles, using S2S (client-credentials) auth.

**Out of scope:** company management of any kind, RapidStart packages, user/permission/security-group
management, feature management. Companies are worth one explicit note: an extension installs into the
**environment** and is then available to every company in it, so there is nothing per-company for this
tool to choose. The company that v1 stored was an artifact of the automation API being an OData
surface bound to `companies({id})`, and it was dropped when publishing moved.

## End-to-end flow (the user's journey)

1. **One-time per customer (Project):** enter the BC connection — tenant id, the customer's Entra app
   (client id + secret + expiry), timezone — and **Test connection**, which fetches the environments
   (flagging a missing GDAP). Owner/Admin only.
2. **One-time per target (Deployment pipeline):** create a deployment pipeline — choose the **source Build pipeline**, the **target environment**, version
   mode, sync mode, and a default publish time. (Can be created inline the first time you deploy to a
   new environment.) Its name is generated, e.g. `main to Production`; see `artifacts.md`, "Pipeline
   names".
3. **Build:** from the Build pipeline, trigger a build — it clones latest `HEAD`, so "from a new
   commit" just means running it again. Clone → compile → ingest, tracked live as today.
4. **Deploy:** once a build is **successful**, "Deploy" (on the deployment pipeline) or "Deploy to…"
   (on the successful build row) → the dialog resolves the **target** (= a deployment pipeline, carrying
   the environment + modes), defaults to the **latest successful build** (older ones selectable), and
   you pick the **date+time**. → enqueues a scheduled `OeProjectDelivery`.
5. **Run:** at the scheduled time the background worker **claims** the delivery (after which it's no
   longer cancellable) and runs upload → install → poll; status flows
   `scheduled → claimed → uploading → installing → deployed | failed`.
6. **Track:** build progress on the build/pipeline page; delivery progress on the deployment pipeline's
   delivery history; both summarised on the pipelines landing. Cancel is available while `scheduled`.

The key point: **"target" is a deployment pipeline, not an ad-hoc environment pick** — so the *same*
successful build can be deployed through `Contoso → Production` and `Contoso → Sandbox` independently,
each with its own schedule and history (build-once-deploy-many).

## Fences this crosses — the explicit asks

Per `CLAUDE.md`, three things here need your sign-off before any code:

1. **A new per-tenant secret.** We'd store each customer's S2S **client secret**. This must follow
   the SMTP-password precedent exactly: encrypted with the Data Protection key ring (the `app-keys`
   ring; losing it means re-entering secrets), written only through a service, **never** returned to
   the UI or logged, org-scoped, and access-gated to the project owner / org Admin. Secrets are a
   named fence — this doc is the ask.
2. **Outbound HTTP to Microsoft.** New calls to `login.microsoftonline.com` (token) and
   `api.businesscentral.dynamics.com` (the Admin Center API). This is the *same kind* of outbound
   dependency we already have (`BcArtifactService` → Microsoft CDN, `AlCompilerProvisioner` → NuGet),
   **not** a new piece of infra (no broker/cache/datastore). Framing it that way so it's clearly
   inside the existing fence, but calling it out.
3. **Scheduled background work.** Delivery must run *later* (the maintenance window), not in-request.
   Reuse the sanctioned in-process pattern — a `BackgroundService` scheduler + bounded `Channel`
   queue + worker, persisted rows for restart-resume — mirroring `ReleaseAutoImportScheduler` +
   `ReleaseImportQueue`/`ReleaseImportWorker` + `PersistedImportJobs`, and the newer
   `ProjectDiscoveryQueue`/`Worker`. **No external queue/broker.**

Migration discipline (future-dated timestamps) and tenant isolation (`IgnoreQueryFilters()` stays
untouched) apply as always.

## Data model

**Decision — separate Build and Deployment, rather than one pipeline that does both.** A pipeline name
like *"Deploy Contoso App on Production"* is really a deployment concern, and a partner usually wants to
**build once and deploy that same build to several environments** (test in Sandbox, then promote the
identical artifact to Production). Fusing build + delivery onto one entity can't express that without
rebuilding. So instead of a `kind` discriminator on `OePipeline` (which would mean many
nullable-by-kind columns, since build and deployment fields barely overlap), model them as two entities:

```
Project (customer)
├─ Build pipeline  (Pipeline — unchanged)      subset of extensions → Build(s) → artifacts
└─ Deployment pipeline (ReleasePipeline — new)     draws a Build's artifacts → an environment
   └─ Deployment (ProjectDelivery)              one scheduled run of a deployment pipeline
```

A Deployment pipeline references **one** Build pipeline as its artifact source and **one** environment
as its target; a Build pipeline can feed several Deployment pipelines. The existing `OePipeline` (shipped
in 7.1.0) keeps its meaning untouched — we add `OeReleasePipeline` alongside it. (Alternative if you'd
rather not add an entity: a `kind` column on `OePipeline` — noted in open questions.)

### 1. Project = the customer connection (the tenant + credentials)

A customer has one Entra tenant and one set of S2S credentials shared across all their environments,
so these live on `OeProject` (new columns on `oe_projects`, snake_case):

| Column | Type | Why |
|---|---|---|
| `bc_tenant_id` | `uuid?` | The customer's Entra (AAD) tenant GUID. Used for the **OAuth token endpoint**; the Admin Center API is scoped by the token rather than by a tenant segment in the URL. |
| `bc_client_id` | `text?` | The S2S app registration's client id (one app **per project/customer** — see below). |
| `bc_client_secret_encrypted` | `text?` | Client secret, DP-key-ring encrypted. Write-only in the UI ("secret is set ✓"); never read back. |
| `bc_client_secret_expires_at` | `timestamptz?` | When the client secret expires. Entra secrets have a **max 2-year lifetime**; we surface a warning as it approaches so a delivery doesn't fail on an expired secret. Entered alongside the secret (Entra shows the expiry at creation). |
| `bc_credentials_updated_at` | `timestamptz?` | For the "last updated" caption + key-ring-loss diagnostics. |
| `bc_time_zone` | `text?` | IANA tz (e.g. `Europe/Copenhagen`) — the customer's local time, so scheduling defaults and "working hours" mean *their* hours. Defaults to the org default if unset. |
| `bc_connection_verified_at` | `timestamptz?` | Set by a "Test connection" action (token + list-environments round-trip). |

**Decision — one Entra app per project/customer.** Microsoft is deprecating cross-tenant Entra app
registrations, so each customer gets its own app: the per-project columns above are the right model
(not a shared system-level secret). Because each secret is short-lived, `bc_client_secret_expires_at`
is first-class — the Project connection card warns when a secret is within ~N weeks of expiry, and a
delivery scheduled past the expiry is flagged at scheduling time.

**Environments (fetched, persisted).** Deployment pipelines reference an environment, so persist the
customer's environments as a child `OeProjectEnvironment` (`oe_project_environments`: `name`, `type`
Production/Sandbox, `status`, `fetched_at`, and the rest of the fetched record), populated by Test
connection / a Refresh — the same fetch-and-cache shape as the discovery cache. Deployment pipelines then
point at an `OeProjectEnvironment` rather than re-typing a name.

Each `OeProjectEnvironment` also carries a recurring **update window** (see below), so the time-of-day
defaulting is per-environment, not per-deployment-pipeline.

**As built — the whole environment record is kept, not just name and type.** The environments call
already returns everything the admin center knows about an environment, so discarding it and then
needing a second call later was pure loss. Every field the API reports is persisted on the row,
nullable, and rewritten on each Refresh: `friendly_name`, `application_family`, `status`,
`status_fetched_at`, `country_code`, `aad_tenant_id`, `web_client_login_url`, `location_name`,
`geo_name`, `ring_name`, `app_source_apps_update_cadence`, `version`, `grace_period_start_date`,
`enforced_update_period_start_date`, `soft_deleted_on`, `hard_delete_pending_on`, `delete_reason`.

Three rules that come with them:

- **Verbatim, never normalised.** Microsoft's casing for enum-ish values differs between endpoints
  (`productFamily: "BusinessCentral"` beside `creatorPrincipalType: "app"`), so values are stored
  exactly as returned and every comparison is case-insensitive. `application_family` in particular is
  the family the API reported — it is *not* assumed, because it addresses the environment in later
  admin-center calls.
- **Two fields are deliberately not persisted.** `appInsightsKey` is secret-adjacent (storing it
  would pull the Data Protection key ring into a cache table — a fence conversation, not a detail),
  and `webServiceUrl` is derivable and unused.
- **`soft_deleted_on` and `missing_since` are different signals.** A soft-deleted environment still
  comes back from the API; a hard-deleted one vanishes from it. The first is the customer's state,
  the second is ours. **A soft delete also renames the environment:** it returns under its old name
  with the deletion time appended (`JLE` becomes `JLE-260911110359`, `yyMMddHHmmss`), presumably so
  the name is free to be reused. Nothing here builds that name; the behaviour is inferred from a
  customer tenant (issue #808), not from documentation we can cite. Matched on name alone that reads
  as one environment vanishing and another appearing, so the upsert folds the suffixed, soft-deleted
  fetch back onto the existing row — same row id, same pipelines, `missing_since` left clear — and
  the row then carries the API name, because that is what addresses the environment in later
  admin-center calls. It folds only when the base name is absent from the same fetch, so a reused
  name stays a second environment. See `ProjectConnectionService.UpsertEnvironmentsAsync`.
- **A soft-deleted environment is not part of the upgrade fleet.** Its update date cannot be moved,
  so the Upgrades page leaves it out. The Environments page and the solution's Business Central tab
  still list it, because "deleted, still restorable" is a state worth seeing and restoring it before
  the hard delete is the useful action. Both pages read one query — `UpgradeFleetService.ListFleetAsync`,
  whose `includeSoftDeleted` flag is the only difference between them.

The refresh upsert still touches only fetched fields, so the user's own settings on the same row (the
update window) survive a Refresh unchanged.

#### Two update windows, and why they must not be conflated

There are two daily windows in play and they mean different things. Reading one as the
other produces a delivery aimed straight into a platform upgrade, so they are kept in
separate columns, separate prose, and separate columns on screen.

| | **Delivery window** (ours) | **Business Central updates** (Microsoft's) |
|---|---|---|
| Where | `update_window_start` / `_end` on `OeProjectEnvironment`, in `OeProject.BcTimeZone` | `bc_update_window_*`, mirrored from `settings/upgrade` |
| What it means | the commercial slot agreed with the customer for *our* installs | when Microsoft patches the environment |
| Who enforces it | our scheduler and worker — a delivery holds until the slot opens | Microsoft |
| Editable | yes, by the consultant, from the environment page | read-only mirror (the API can write it, but that is a separate, explicit action) |

Neither is derived from the other. In particular the delivery slot is **not** implemented
by the App Management API's `deploymentSchedule: "UpdateWindow"` — that value defers the
install to *Microsoft's* window, which is a different time chosen by a different party,
and it stays out of the deployment-pipeline picker for exactly that reason. A deployment pipeline
*can* be set to install in the delivery window (#928), and that is ours all the way down:
see *Deployment schedules* below.

The one relationship worth computing is **overlap**: a delivery slot that lands inside
Microsoft's maintenance hours is the case the environment-status gate then refuses, so the
project page warns about it while the consultant is still choosing. The comparison
projects both windows onto the same UTC day because they can be expressed in different
zones and either may wrap past midnight. DST makes it an approximation — a window's offset
shifts twice a year — which is fine for a warning; the status re-read at delivery time is
what actually protects the deployment.

**Time zones cross a platform boundary.** Business Central speaks *Windows* time-zone ids
(`Romance Standard Time`) and accepts only those back on a write. The host runs Linux,
where handing a raw Windows id to `TimeZoneInfo.FindSystemTimeZoneById` is not safe to
rely on. So the id is converted once at fetch time with `TryConvertWindowsIdToIanaId` and
**both forms are stored**: the Windows id for round-tripping to the API, the IANA id for
display maths. When the conversion has no answer, display falls back to the project's own
zone, then to UTC — never to a throw, and never to a silently wrong hour presented as
fact.

**Fetch strategy.** The mirror rides the environments Refresh, one `settings/upgrade` call
per environment — twenty sandboxes make a Refresh twenty-one requests. That is a real cost
and it was chosen over fetching on panel-open, which would put a round trip in front of
every glance at the table for data that changes about as often as the environment list.
The call is *per-environment tolerant*: a failure is logged and skipped, leaving the
previous answer and its timestamp intact, because the environment list is what a Refresh
is for. `bc_update_window_fetched_at` is stamped only on success, so the page can say how
old the answer is rather than implying "no window" when it means "not read". If the N+1
ever bites, that method is the single place to make lazy.

#### The status gate (deliveries)

`status` is the field that earns its keep: publishing to an environment mid-upgrade fails in ways
that read as our bug. Classification — `Active` publishes; `Upgrading` / `Preparing` / `NotReady` /
`Recovering` refuse with retryable wording; `Removing` / `SoftDeleting` / `SoftDeleted` refuse with
terminal wording; anything ending `Failed` refuses as a failed state in Business Central. An absent
or unrecognised status does **not** block — rows fetched before the field was captured have none, and
a status Microsoft adds later shouldn't silently stop every deployment.

It is checked twice, and the second check is the one that matters:

1. **At scheduling**, against the cached status, as a field-keyed validation error so it lands next to
   the environment on the form.
2. **At claim time**, by re-reading the single environment (`GET .../environments/{name}`) after the
   claim and before the first upload. A delivery scheduled at 09:00 for 22:00 was fine when it was
   scheduled; an update that landed at 20:00 is invisible to check 1. The fresh status is written back
   to the row, so the project page doesn't keep showing the status the delivery just contradicted. A
   404 there means the environment is gone; a transport failure is *not* treated as a refusal, since
   an unreachable API is no evidence about the environment's health.

Note the by-name response omits `geo_name`, so a live re-read leaves the cached value alone rather
than erasing it.

#### Update window (per environment)

Every BC SaaS environment already *has* an update window in the admin center — a recurring daily
time range during which Microsoft applies platform/app updates — so BC admins reach for exactly this
model. We mirror it: two nullable columns on `OeProjectEnvironment`, interpreted in the project's
`bc_time_zone`:

| Column | Type | Why |
|---|---|---|
| `update_window_start` | `time?` | Start of the daily window (e.g. `22:00`), in `bc_time_zone`. |
| `update_window_end` | `time?` | End of the daily window (e.g. `06:00`); may wrap past midnight. |

Both null ⇒ **no window** (deliver any time) — the normal Sandbox case. Set ⇒ a recurring default a
Production environment is happy to receive updates in. v1 is a single daily range, matching BC's own
admin-center field (no weekday mask — add one only if a real case needs it). The window is in
`bc_time_zone` for now; BC environments carry their *own* tz, which we could fetch from the admin API
later, but one project-level tz is the v1 simplification consistent with the rest of this doc.

**It's a default, not a lock.** This is the one place we deliberately differ from BC's own window
(which Microsoft enforces): ours only computes the **prefilled `scheduled_for`** when a user schedules
a delivery — "next time this environment's window opens." The user can override it to run now, or at
any other time; the consultant is the one in control, not the platform. Overriding the window (or
delivering to an environment that has one set, outside it) is **audited** — recorded on the
`OeProjectDelivery` and surfaced in history — so the safe default protects you and the opt-out is a
deliberate, traceable act. Production targets, which already get an extra confirm, are the case this
most matters for.

**The organisation can set the window a new environment starts with** (#962). Administration →
Business Central holds one default for Production and one for Sandbox, each start-and-end or "any
time", in four nullable `time` columns on `organization_settings`
(`default_delivery_window_{production,sandbox}_{start,end}`), both-or-neither per pair like the
environment's own editor, and audited like every other settings column. They are read in exactly one
place: the discovery upsert, when it creates an `OeProjectEnvironment` row, copies the default for the
fetched environment's type onto `update_window_start` / `_end`. Copied as clock digits, not converted,
because both the default and the row's window are read in the customer's `bc_time_zone`. A row that
exists already is never touched — not when its type changes, not when it is recovered, not when a
soft-delete rename folds onto it — and setting a default later does not backfill; the bulk action on
the Environments list (#961) is how existing rows are brought in line. An environment copied from
another is a new row, so it takes the default rather than its source's window. The discovery worker
reads the setting under the org it has already pinned, through the query filter.

**Many environments at once** (#961). The Environments list lets rows be ticked (not a deleted
environment's) and offers **Set delivery window...**: one start and end, or "any time", for every
ticked row, still read in each customer's own `bc_time_zone` - applying 22:00-06:00 to ten
customers gives ten local 22:00s. `ProjectConnectionService.PreviewUpdateWindowForManyAsync`
groups the selection first (**Will change**, **Already set**, **No access** - the manage check is
asked once per solution - and **Missing or being deleted**), and
`SetUpdateWindowForManyAsync` writes each changing row through `SetUpdateWindowAsync`, so the
both-or-neither rule, the access check and the log line are the single-environment ones, and
returns a result per row that the dialog shows. A deployment already scheduled for an
environment's current window (`scheduled_by_delivery_window`) keeps its time; the preview says
so, and the next deployment uses the new window.

This **supersedes `OeReleasePipeline.default_publish_time`** as the source of the schedule prefill: the
window lives on the environment (where it's reused across every deployment pipeline targeting it and
matches the BC mental model), rather than being re-entered per deployment pipeline. Keep
`default_publish_time` only if a pipeline ever needs to differ from its environment's window;
otherwise drop it (see the amended row in §3).

#### Next platform update (per environment), mirrored — and the Upgrades page

Alongside Microsoft's window, each environment's **next platform update** is mirrored onto
its row: seven nullable `bc_next_update_*` columns holding the version, type and status
verbatim, the scheduled date, the latest date it can still be pushed to, whether it ignores
Microsoft's window, and when the mirror last succeeded. It exists so a cross-project
Upgrades page can list a hundred environments from cached rows instead of a hundred live
round trips, and it rides the same per-environment loop (and the same failure isolation) as
the update window above. The **full** updates list is still fetched for the environment
panel rather than read from the mirror.

Everything else about that feature — the selection rule, the nightly sweep, the three writes
(two move an update's date, one changes its version), the `oe_environment_upgrade_actions` table that is both the
action queue and the activity feed, and the `/upgrades` page itself — is its own tool and
lives in **[`environment-updates.md`](./environment-updates.md)**. It shares this document's
`OeProjectEnvironment` row and its Admin Center client, and nothing else: the delivery slot and
Microsoft's update window stay the two separate things the table above says they are, and
Upgrades acts only on Microsoft's.

### 2. Build pipeline (`OePipeline`) — unchanged

The 7.1.0 entity stays exactly as is: a named subset of the project's extensions that compiles to
`Build`s (`OeProjectBuild`) and artifacts. No new columns. It's now explicitly the *build* half of the
split; deployments draw from its builds.

### 3. Deployment pipeline (`OeReleasePipeline`) — new

The reusable "where + how" of a deploy: a named, listable config (`oe_release_pipelines`) that draws
from one Build pipeline and targets one environment. This is the *"Deploy Contoso App on Production"*
the naming suggested.

| Column | Type | Why |
|---|---|---|
| `id` / `organization_id` / `project_id` / `created_by_user_id` / `deleted_at` | | Standard, org-scoped, soft-deletable, owner-managed (same as `OePipeline`). |
| `name` | `text` | e.g. `Contoso App → Production`. |
| `artifact_source` | `text` | Where the apps come from: `build` (the default) or `github_release`. Added by #632, when "redeploy a version the workbench did not build" stopped being a hole in the model. |
| `build_pipeline_id` | FK → `oe_pipelines`, **nullable** | The artifact source when `artifact_source = build` — releases publish *this* build pipeline's builds. Null (and unused) for a Release-sourced pipeline. |
| `github_release_repository_id` | FK → `oe_project_repositories`, nullable | The repository whose GitHub Releases the pipeline installs, when `artifact_source = github_release`. Exactly one of these two is set. |
| `project_environment_id` | FK → `oe_project_environments` | The target environment (carries its type and fetched status). |
| `deployment_schedule` | `text` | App Management `deploymentSchedule` — **when** BC installs the upload: `Immediate` (default) / `UpdateWindow` / `NextMinorUpdate` / `NextMajorUpdate` — or our own `OurDeliveryWindow` (#928), which is never sent and becomes `Immediate` at the wire. **Renamed from `version_mode`** when publishing moved off the retired upload API: the old column held a *version target* (`Current version` / `Next minor version` / `Next major version`) and the new field genuinely means a time, so the values were migrated as well as the name. Four are offered in the picker — see *Deployment schedules* below. |
| `schema_sync_mode` | `text` | App Management `syncMode`: `Add` (default, safe) or `ForceSync` (can drop columns — gate behind a confirm). Note the missing space: the retired API spelled it `Force Sync`, so stored values were migrated too. |
| `prepare_release_on_new_build` | `bool` | #934. Off by default. When on, a new successful build of the source build pipeline **prepares** a release through this pipeline - a `proposed` delivery - and a person approves or dismisses it (see *Prepared releases* below). Nothing is ever approved on its own. Ignored (saved as false) for a pipeline that installs GitHub releases. |
| `restrict_branch` / `allowed_branch` | `bool` / `text?` | Only deploy builds made from `allowed_branch` (null is the repositories' default branch). See *Which branch may reach an environment* below. Ignored (saved off) for a pipeline that installs GitHub releases. |
| `default_publish_time` | `time?` | **Superseded by the target environment's update window** (§1 → *Update window*) as the schedule prefill, and likely droppable. Keep only as a per-pipeline override when one deployment pipeline must default to a different time than its environment's window. The execution model is unchanged: the real schedule is always a concrete date+time per delivery (`OeProjectDelivery.scheduled_for`, §4) — the window/`default_publish_time` only seed the picker. **As built (CRUD slice):** the column was *not* added — there is no scheduling in the CRUD slice to prefill, and the per-environment update window (phase 3) is the intended source. Add it back only if a per-pipeline override turns out to be needed. |

#### Which branch may reach an environment

A deployment pipeline draws from one build pipeline, and each build records the branch it was
made from (`OeProjectBuild.Branch`, the build pipeline's branch at the time; null for the
repositories' default branch). So a test branch's build reaches production only if someone points
the build pipeline at that branch, or points the production deployment pipeline at the wrong build
pipeline. The branch rule closes that: with `restrict_branch` on, `DeliveryService.ResolveReleaseAsync`
(which every deployment passes through: the dialog, an approval, a prepared deployment,
`deploy_build`) refuses a build whose branch is not `allowed_branch`, compared exactly
(`DeploymentBranchRule`). The version number is deliberately not the guard: Business Central installs
anything higher than what it has, so a "test" pattern in a version would not stop it.

- **On for production.** The editor turns the rule on when the target is a Production environment,
  allowing the branch the chosen build pipeline builds, until the person changes it. The migration
  gave every existing deployment pipeline into a Production environment the rule with its build
  pipeline's branch at the time, so today's builds keep deploying and a later branch change is
  what gets refused.
- **Older than installed.** Separately, `ResolveReleaseAsync` refuses an app whose version is lower
  than the environment's mirrored app list says is installed (Business Central never replaces an app
  with an older version), and the run checks every app against the live list once more before its
  first upload, so a refusal never leaves a build half installed. Versions are compared part by part as numbers.

### 4. Delivery = one run of a deployment pipeline (the analogue of `OeProjectBuild`)

New entity `OeProjectDelivery` (`oe_project_deliveries`), created when the user schedules a deployment of a
specific build. Mirrors how `OeProjectBuild` records a build run:

- FKs: `release_pipeline_id`, `project_build_id` (the chosen build's `.app` blobs — already persisted
  as `OeProjectBuildArtifact`), `organization_id`, `triggered_by_user_id`.
- **Snapshot** at creation (so later edits to the deployment pipeline don't rewrite history):
  `environment_name`, `deployment_schedule`, `schema_sync_mode`. `deployment_schedule` is the
  **wire value actually sent**, so a delivery-window pipeline records `Immediate`;
  `scheduled_by_delivery_window` (#928) records that the pipeline's rule was the delivery window.
  Read beside `scheduled_outside_window`: both true means the person deploying overrode the rule.
- Schedule: `scheduled_for` (the UTC instant the user picked), `claimed_at`, `started_at`, `finished_at`.
- **Status lifecycle + the cancel/run race:**
  `scheduled → claimed → uploading → installing → deployed | failed`, plus `scheduled → cancelled`,
  and for a prepared deployment (#934) `proposed → scheduled` (approved) or `proposed → dismissed`
  (dismissed, or replaced by a newer build; terminal, and deliberately not `cancelled`, because
  it never was a deployment). `proposed` is never enqueued and never claimed: the
  scheduler's due sweep and the worker's claim both match `scheduled` only.
  - While `scheduled`, the delivery is **cancellable**. Cancel is an atomic compare-and-set
    (`UPDATE ... SET status='cancelled' WHERE id=? AND status='scheduled'`) — it only succeeds if the
    worker hasn't taken the row yet.
  - The scheduler/worker **claims** the same way (`SET status='claimed', claimed_at=now() WHERE
    id=? AND status='scheduled'`). Whoever wins the compare-and-set decides the outcome: a claim that
    finds the row already `cancelled` does nothing; a cancel that finds it already `claimed` is
    refused with "already started". This is the "cancellable until a worker picks it up" guarantee,
    enforced in the DB rather than with a lock.
- Per-app rows (`oe_project_delivery_results`, like `OeProjectBuildResult`): app name/id, the BC
  install `operation_id`, the operation's result, message.
- `failure_message`, and a log section for the raw API responses (secret-free).

**As built (#632):** a delivery can also publish a build the workbench never compiled. Choosing a
tag on a Release-sourced pipeline downloads that Release's `.app` assets and **stages them as an
ordinary `OeProjectBuild`** — status `ready`, no `pipeline_id`, `github_release_tag` set — so
`OeProjectDelivery` and every downstream reader are unchanged; `ScheduleDeliveryAsync` accepts such a
build in place of its build-pipeline check. `oe_project_builds` gained `github_release_tag`,
`github_release_url` and `github_release_error` for both halves of that traffic: a build the workbench
compiled records where it was *published*, and a staged build records where it came *from*.

**As built:** `oe_project_deliveries` also carries a denormalised `project_id` (so the worker
resolves the BC credentials without a join) and a `diagnostics_log` text column (the secret-free
per-step run log). The per-app `app_id` is now **populated**: BC reads it out of the uploaded package
and returns it on the install operation, which is what lets the poll ask about one specific app
rather than matching on a name. `company_id` and the automation API's `extension_upload_id` are gone
from both tables — extensions install per environment, and the ids belonged to a surface that no
longer exists.

## Authentication (client credentials / S2S)

- **Token:** `POST https://login.microsoftonline.com/{bc_tenant_id}/oauth2/v2.0/token`,
  `grant_type=client_credentials`, `scope=https://api.businesscentral.dynamics.com/.default`,
  client id + secret. Tokens are ~1 h — **cache in memory** keyed by project (a singleton, like the
  compiler gate), **never persisted**. Refresh on expiry/401.
- **Whose app registration.** An organisation can hold one registration for all its customers
  (`organization_settings.bc_client_id`, `bc_client_secret_encrypted`,
  `bc_client_secret_expires_at`; Administration → Business Central, Admins only), because each
  customer authorises it in their own admin center. A solution then needs only a tenant id. A
  solution's own `bc_client_id` is the override, and it is the *choice*, not merely a value: when
  it is set the organisation's registration is never tried, even if the solution's own secret is
  missing or has expired. A silent fallback would connect a customer through a registration
  nobody chose for them, so that case fails and says which secret expired and who can fix it.
  Both secrets are encrypted under the same Data Protection purpose, so
  `ProjectConnectionService` is the only reader of either. Saving or removing the organisation's
  registration drops the cached tokens of every solution on it and clears their "verified" stamp.
  The secret is redacted in the audit trail like the other organisation secrets.
- **Customer-side prerequisites (document for onboarding, we can't do it for them):** the Entra app
  needs the `AdminCenter.ReadWrite.All` permission with admin consent granted, and must be authorized
  in the customer's BC admin center. It used to *also* need registering inside each environment as an
  application (S2S) user holding extension-management permission sets — that requirement belonged to
  the automation API and is gone with it.
- **BC-side prerequisites are two separate registrations, and neither is visible from Entra.**
  This is the part that looks finished when it isn't: granting the API permissions in Entra only
  gets a *token*, and Business Central keeps its own allow-lists.
  1. **Admin Center API** — the app's client id must be on the **Authorized Microsoft Entra apps**
     page in the BC admin center (tenant-wide). Missing → **401** on the environments call.
  There used to be a second, per-environment registration: the app had to be added on the
  **Microsoft Entra applications** page *inside each environment*, with permission sets, before the
  automation API would accept a publish. Publishing through the Admin Center's App Management surface
  needs only the tenant-level registration above — verified against a real tenant before the move —
  so that step is gone, and with it the "looks finished but fails per environment" trap. Onboarding a
  customer is now two registrations in two portals, and the Entra app needs
  `AdminCenter.ReadWrite.All` alone.
## Environment discovery

Environments come from the **Admin Center API** — the only BC surface this tool calls now:

- **Environments**:
  `GET https://api.businesscentral.dynamics.com/admin/{version}/applications/businesscentral/environments`
  (tenant scoped by the token). This is the **primary** path. Manual environment-name entry stays
  as a fallback, but fetching is the expected flow.
  **On the version:** this line used to read `admin/v2.x`, and that placeholder is what caused the
  drift — the implementer substituted whatever was current that week (`v2.21`), and it then sat
  eight versions behind for months, below the `v2.24` that `authorizedAadApps/manageableTenants`
  needs. The version now lives in exactly one place, `BcConstants.AdminApiVersion`, and
  `.github/workflows/bc-api-version.yml` probes Microsoft monthly and opens an issue when a newer
  one ships. **Don't write a concrete version into this doc** — it will rot the same way; name the
  constant instead. Old versions keep serving for years (v2.15 still answered in Aug 2026), so
  falling behind never fails loudly, which is precisely why it needs watching rather than trusting.
  **Denials must be told apart, because they are fixed in different portals:**
  **401** = BC won't accept the app at all (it's missing from *Authorized Microsoft Entra apps*);
  **403** = the app is known but not permitted (missing/unconsented `AdminCenter.ReadWrite.All`, or
  — for a customer's tenant managed as a partner — a missing GDAP relationship). An earlier revision
  assumed "GDAP is always set up" and so reported *both* as missing GDAP; that message sent a
  maintainer connecting their **own** tenant hunting a delegated-admin relationship their setup
  never needed. GDAP is one possible cause of one of the two, not the diagnosis for either.
- **Ordering.** Environments render production-first, then sandboxes, name-ordered within each
  group: production is what a consultant looks for when something is wrong, and a customer often
  has enough sandboxes to bury it.

UI flow: enter credentials → Test connection (token + list environments) → pick the environment a
deployment pipeline targets. The connection card carries the two-step setup checklist in its rail,
because the second step happens outside Entra and is invisible from the app.

## The environment panel (read on demand, cached for fifteen minutes)

The environment's own page (`/environments/{id}`, see `.design/environment-updates.md`) - until
#809 an inline panel on the solution's Business Central tab - answers the question a
consultant otherwise opens the admin center for: *what is on this customer's environment,
and what is about to change?* It shows four things, read from Business Central when the
panel opens and reused for a short window after that:

- **Scheduled installs** — per-tenant extension versions Business Central is holding for a
  later window, each cancellable. This is what makes a `handed_off` delivery actionable:
  the delivery ends when BC accepts the upload, and this is where it can still be pulled
  back. Cancelling removes the uploaded package permanently, so the version has to be
  deployed again afterwards.
- **Installed apps**, with per-tenant extensions first and anything this workbench has
  actually deployed to that environment marked as ours. The correlation is best-effort, by
  app id, from the delivery history — enough to answer "is that pending install mine?".
- **AppSource updates waiting** — AppSource (Marketplace) apps only. The endpoint is documented
  as global-app updates, so *per-tenant extensions never appear here*; the copy says so,
  because "my extension isn't listed" would otherwise read as a bug.
- **Business Central updates** — the platform versions coming to the environment, released
  or merely expected, and which one is scheduled next.

One write hangs off the third list: a ready AppSource update can be started from its row
(`ProjectConnectionService.UpdateAppAsync`, `POST .../apps/{appId}/update`). It never pulls
dependencies along and never takes a preview version, so Business Central refuses rather than
updating apps nobody picked. The rules and the confirm are in `.design/environment-updates.md`.

**The four reads go out together, and the answer is held for fifteen minutes.** They do
not depend on each other, so issuing them in parallel costs one round trip's wait rather
than four. Nothing is persisted — the cache is in memory (`BcPanelCache`, a singleton
beside `BcTokenService`) and a restart simply loses it.

This *revises* the original rule that the panel is never cached. That rule was written
against a real failure — a consultant opens the panel precisely to see what is true now,
and a stale answer defeats the point — but it treated every kind of staleness alike. The
answers that go stale fastest are the ones **we** changed, so those invalidate the entry
outright: publishing a build, cancelling a scheduled install, choosing a target version,
moving an update's date, starting an update now. A consultant can therefore never be shown
a stale panel as a consequence of something they just did in the workbench. What remains is
a change made directly in Business Central within the last quarter of an hour, and the
panel's **Refresh** re-reads past the cache for exactly that.

The window is short on purpose. Nearly all the repeat traffic is one person expanding an
environment, collapsing it, opening another and coming back; fifteen minutes collapses a
working session into one fetch, where a longer TTL would mostly buy the *next* person
tomorrow at a much worse staleness. It also matters that we honour no throttle: the BC
clients have no `429`/`Retry-After` handling, so restraint in how often we ask is the only
politeness we currently offer that API. The panel says how old its answer is rather than
claiming freshness it does not have.

There is still no background polling and no reconciler.

**Each section fails on its own.** The app-management reads and the platform-update read
are different permissions in practice, so one refusal is rendered in its own section and
the other three still show. A panel that blanks entirely because one endpoint was denied
would send a consultant to the admin center anyway.

**Mixed-tool invisibility is called out in the copy** (a Microsoft-documented behaviour):
a PTE uploaded through the web client's own Extension Management page is invisible to the
admin center until it installs, and one scheduled through the admin center is invisible
there. Using both surfaces for one customer means neither shows the whole picture, so the
scheduled-installs section says to pick one.

### Changing settings on the customer's environment (5b)

Four settings on the panel write to the *customer's* tenant, so each is behind a confirm
that names the environment and says what the click does there:

- **AppSource apps update cadence** — how often AppSource apps the customer installed are
  updated. The one write that also touches a row of ours: the cached
  `app_source_apps_update_cadence` is refreshed from the value we just set, so the page
  agrees with the tenant without waiting for a Refresh.
- **Access with Microsoft 365 licences** — whether people holding only an M365 licence can
  sign in. It changes who can get into the environment, so the confirm says so in those
  words.
- **Next platform version** — a reschedule of the customer's Business Central upgrade, and
  the most consequential control in the tool. The confirm names the environment, says out
  loud when it is a production one, and states both versions. Only a version the
  environment's own updates read reports as `available` can be chosen, and the service
  re-checks that at write time so a stale page can't schedule something Microsoft hasn't
  released.
- **Updating an AppSource app** - to the version Business Central has waiting. An app that
  waits for others can be updated too: the confirm lists every app it waits for, and the
  send passes `installOrUpdateNeededDependencies` true only when there are some. Business
  Central takes each prerequisite to the newest version the environment supports, not the
  minimum.

  **Since #1001 an update is booked exactly like an upload** (below): the dialog asks the
  same "When should it install?" (the shared `InstallTimingPicker`: delivery window, or the
  BC update window when there is none; a picked time; now), and every answer writes one
  `Pending` `UpdateApp` row carrying the app (`package_bc_app_id`, `app_name`), the target
  version (`target_version`) and the prerequisites Business Central listed and the person
  agreed to (`update_prerequisite_app_ids`). Nothing is stored as a package: the app is
  Business Central's own. `UpgradeActionWorker` sends it in the install pass, as
  `useEnvironmentUpdateWindow: false`, and waits for the operation with
  `BcAppOperationPoller`, so it takes its turn with the uploads and never runs beside one.
  Business Central's own `UpdateWindow` schedule is never handed over, for the reason an
  upload never hands one over: order and non-overlap are promises the workbench keeps.

  The waiting list is not taken on trust, twice. At booking the service reads it and books
  only an app that is on it at exactly that version, with every prerequisite agreed to;
  at send time it reads it again and refuses in plain words if the version has moved or
  Business Central now asks for an app outside the agreed set, so nobody's agreement covers
  an app they never saw. One booking per app per environment. A booked update appears under
  *Scheduled installs* ("Update booked here", with its version) and in the Workbench
  history, with Install now and Cancel, gated like the booking on managing the solution;
  the app's Update button reads "Booked" and is disabled meanwhile, for the app and for the
  prerequisites that move with it. The platform-update surfaces ignore it, as they ignore a
  booked upload. The `UpdateApp` rows written before #1001 are records only, already
  `Sent`, with what moved in `Outcome`; the history still reads them as "Updated an
  AppSource app".
- **Uploading apps** - `.app` files another company built, for which there is no pipeline
  here. Several may be chosen at once: each manifest is read on the spot
  (`AppPackageReader.TryReadManifestAsync`) and the apps are put in dependency order with
  the same `DependencyOrder` the project build compiles siblings in; a protected vendor
  app hides its manifest and cannot be placed, and the dialog says to upload such apps
  separately, dependencies first.
  The sync mode is always Add and dependencies are **not** pulled along: a missing one is
  refused by name at send time. Apps we build still go through Deployments.

  **Every timing is a booking, never a schedule handed to Business Central.** The dialog
  offers three: the delivery window (or, when the environment has none, the BC update
  window, booked from the hours mirrored on the environment and refused until they have
  been read), a picked time, and now. The service takes all four `UploadAppTiming`
  values, and all four write one `Pending` `UploadApp` row
  per app in `oe_environment_upgrade_actions` carrying the file (`package_file_name`,
  `package_content`) and, for several apps, a batch (`package_batch_id`,
  `package_batch_order`). `UpgradeActionWorker` sends a batch's rows in order as
  `Immediate` when the slot arrives, and waits for each install to finish
  (`BcAppOperationPoller`, the poll a delivery has always done) before sending the next:
  two installs started back to back can deadlock on Business Central's own bookkeeping
  table, and the API's `UpdateWindow` queue would pick its own order. A failed app stops
  the rest of its batch ("not installed, because X before it didn't install"); a
  cancelled one does not. "Now" is a slot that has already come, so the next sweep
  (within thirty seconds) takes it - an install that takes minutes never runs inside a
  page request. The delivery window falls back to Microsoft's window when the
  environment has none, and a window that is open at the moment of asking books now. A
  sweep makes two passes: every organisation's platform-update moves first, then at most
  **one** upload across all organisations, so a batch spreads over as many sweeps as it
  has apps, no customer's agreed update slot waits behind another's install, and a sweep
  costs at most one install (the worker's heartbeat is ticked on every poll of it). "Now"
  therefore means "shortly", not "within a minute", when another install is running. An
  install the workbench could not see finish - a run of failed polls, the wait ran out,
  or a restart while it was installing - is recorded as **sent, unconfirmed** and marked
  to be asked about again (`package_confirmation_due`, #1002). A later sweep re-polls it
  **once** from the operation ids Business Central answered with (`package_bc_app_id` /
  `package_bc_operation_id`), in place of that sweep's one install and as the requester,
  and settles the row from the answer: installed, failed in Business Central's words, or
  still unconfirmed when no answer can be had (the poll gives up again, or the solution,
  environment, credentials or the requester's access are gone). The mark is cleared
  whatever the answer, so a row never keeps the worker busy for ever. The rest of the
  batch waits for that second look: a failure it finds stops the dependents like any
  other, and installed or still-unconfirmed lets them go, where a dependent that needed
  the app is refused by Business Central if it did not land. A booked AppSource update is
  asked about the same way. Only Business Central's own refusal is a failure that stops
  a batch. A
  pending upload is **not** a platform-update booking: the Upgrades page's "already
  booked" marker and the bulk version change ignore it.

  A picked time is read in the organisation's display zone - the zone every other time
  on the page is shown in - and echoed back under the field with the zone named, so
  12:32 on the person's clock is never 12:32 UTC; a window slot is said back in the
  customer's clock first, because that is the clock it was chosen on. **The package is
  held only while the row is pending**: the write that settles the row (sent, failed, or
  cancelled) clears it in the same statement, and the worker's first sweep after a
  restart drops any package left on a settled row. This is a change of scope from the
  first version, which never stored the file: while a booking waits, its packages (up to
  ten per upload, up to 50 MB each, with no cap across bookings) sit in the database and
  in any `pg_dump` taken meanwhile. Booked
  uploads appear in the
  environment's *Scheduled installs* beside Business Central's own, with their place in
  the batch, "Install now" (`UpgradeActionService.RunUploadNowAsync`, which moves the
  whole batch's slot to now for the next sweep rather than sending from the page) and
  "Cancel install", gated like the upload itself on managing the solution; they are also
  in the Workbench history.

Refusals are keyed on Microsoft's error **codes** (`environmentNotFound`,
`applicationTypeDoesNotExist`, and so on) and rendered as an instruction; the message beside the
code is Microsoft's prose and is treated as opaque, the same rule the install path follows.

#### What is audited, and what is only logged

`OeProjectEnvironment` joins the audit map **column-scoped**, the same shape as
`ProjectConnectionColumns` on `OeProject`: only `update_window_start`, `update_window_end`
and `app_source_apps_update_cadence` — the columns a person changes on purpose. Everything
else on that entity is fetched cache that a Refresh rewrites wholesale, and auditing it
would put a row per environment per click into the log and bury the changes that matter.
A test asserts both halves: a cadence edit writes an audit row, a Refresh writes none.

The other two writes never touch a row of ours — they change the customer's tenant and
nothing here — so this route cannot record them. Rather than invent a second audit
mechanism for cross-tenant calls, they are logged at Information with the acting user,
environment and value, which is what the delivery path already does for its own API calls.

**Half of that gap has since been closed, and the other half hasn't.** The two Upgrades
writes (#657) do record audit rows for their cross-tenant changes, by writing to `audit_log`
directly rather than through the interceptor — see
[`environment-updates.md`](./environment-updates.md). The panel's own version pick and the
Microsoft 365 licence toggle still only log, so the same treatment is available to them
whenever a maintainer decides it is worth the second writer.

#### Deliberately not built

- **Security group assignment** — the API takes a Microsoft Graph group *object id*, which
  a consultant would have to paste by hand. That is a mechanic needing explanation, and by
  the house UX rule the affordance is wrong until there is a way to pick a group by name.
- **`partneraccess`, `linkEnvironment`/`unlinkEnvironment`** — global-admin only, S2S
  unsupported, so this tool cannot call them at all.
- **Environment create / copy / delete / rename / restore** — destructive tenant
  operations that belong in the admin center, not in a build-and-deploy tool.
- **`appinsightskey`** — restarts the environment when set, and the key is secret-adjacent;
  storing or setting it here would drag in the Data Protection key ring for no gain.

## Publish flow

Publishing goes through the **Admin Center API's App Management surface** (`pteInstall`), not the
automation API's `extensionUpload`. Microsoft is removing `extensionUpload` as an upload surface, and
the replacement needs only the tenant-wide *Authorized Microsoft Entra apps* registration — the
per-environment one the automation path required is not needed to publish.

There is no company anywhere in this flow. Extensions install per **environment** and are then
available to every company in it; the company was only ever an artifact of the automation API being
an OData surface bound to `companies({id})`.

Once per delivery:

1. `GET .../apps` — what the environment already has. Read before anything is uploaded, because the
   API only accepts a deferred schedule for an app it already knows.

Then, for each app in the build in **dependency order** (the order the build stamped; deliveries
preserve it by ordering on artifact id rather than re-sorting):

2. `POST .../apps/pteInstall` — a multipart upload carrying the `.app` file itself, the deployment
   schedule, the sync mode, and `acceptIsvEula`. BC reads the app id and version out of the package
   and returns an **operation** to track; both ids are recorded on the per-app result row. The run
   checks the version BC read against the version the build promised, and fails the app if they differ.
3. If the schedule is `Immediate`: poll `GET .../apps/{appId}/operations/{operationId}` until the
   operation reports a terminal state. The poll is keyed on **ids**, which is what makes it safe when
   two extensions share a display name — the retired flow matched on name and could confuse them.
4. Otherwise the operation comes back `scheduled` and never goes terminal while we watch, because BC
   runs it in its own window. The delivery ends in `handed_off` (see below).

`installOrUpdateNeededDependencies` is always sent true (the API defaults it to false). It only
resolves dependencies BC can already see — it cannot conjure a sibling extension that hasn't been
uploaded yet — so it supplements our dependency ordering rather than replacing it.

**No language is sent.** `languageId` sets the extension's install locale, and the workbench has no
concept of a language; defaulting to `en-US` would be wrong for, say, a Danish customer. BC applies
its own default until a deployment pipeline can say what the language should be. Open question, below.

### `acceptIsvEula` is sent true, unattended, on the customer's behalf

The API refuses an install without it. There is no interactive surface on which to show the
Marketplace terms, so sending it agrees to those terms for someone else's tenant — the same thing
the admin center's own UI does behind a checkbox, but without a human at the checkbox. That is a
deliberate decision rather than an incidental constant: it is stated here, and it belongs in the
onboarding copy so nobody discovers it by reading the code.

### Deployment schedules, and the two rules around them

`Immediate` installs as soon as BC accepts the upload. The other schedules hand the app to Business
Central to install later, which changes what a delivery can promise:

- **`handed_off`** is a terminal delivery state meaning *BC accepted this and will install it on its
  own schedule*. It is not "succeeded" — we never saw the install happen — and it is not "still
  running" either, because nothing on our side is driving it any more. Cancelling one means
  cancelling it in Business Central (`removeScheduledPteVersion`, keyed on app id + version +
  schedule). There is deliberately **no background reconciler** polling scheduled operations; the
  on-demand read on the environment panel is enough.
- **Several apps on a deferred schedule are refused.** BC decides the order it installs a window's
  queue in; our dependency order only decides the order things were *uploaded*. With one app that's
  harmless, with several it can install a dependent before its dependency, so the delivery is refused
  at scheduling time with a message saying to install right away or deploy one app at a time.
- **A first install can't be deferred to a version bump.** `NextMinorUpdate` / `NextMajorUpdate` are
  instructions to bump an app BC already has; it rejects them for an app it has never seen. The run
  catches this against the installed-apps read and fails with a message naming the app, rather than
  letting BC answer with a 400 that doesn't say which rule was broken.

**Installing in the delivery window is ours, not a schedule Business Central runs** (#928). A
deployment pipeline can be set to `OurDeliveryWindow`, labelled "In {environment}'s delivery window"
in the editor and "Delivery window" in lists. It only decides *when we send*: the Deploy dialog
defaults the time to the next opening of the target environment's delivery window and says so
("Scheduled for the next delivery window, Thursday 22:00, in the solution's time zone"), "Now" is
the explicit override, and at the scheduled time the delivery goes to Business Central as
`Immediate`. Nothing about it is deferred to Business Central, so none of the rules above apply —
several apps are fine, and so is a first install. The choice is refused when the target environment
has no delivery window, and the editor disables it with a link to the environment page. If the
window is cleared later, the Deploy dialog says so and asks for a time. Microsoft's `UpdateWindow`
remains a different thing and remains out of the picker: ours is when *we* start the install,
Microsoft's is when *they* do. `deploy_build` deploys now whatever the pipeline says (see *MCP
parity*).

Because the stored values go to the API verbatim, a deployment pipeline saved under the retired API
holds wording this one rejects. Those values were migrated with the columns, and both the edit screen
and the scheduling path refuse an unmigrated value rather than guessing at it — that refusal is what
makes the data migration required rather than optional.

Per-app result statuses are `pending → uploading → installing → completed | failed | skipped`, plus
`scheduled` for an app handed to BC's own window. A `skipped` row is one an earlier app's failure
short-circuited.

**Failure detail comes from the codes, never the message.** A failed operation carries `errorMessage`
localized to the *environment's* language (a real failure came back in Danish) with the structured
`code` / `innerError.code` embedded in it as JSON. The run keys everything on those codes and carries
the message through only as display text. Since #930 one parser (`BcFailureText`) takes that text apart: it drops the
"A request to the Data Plane Admin Service failed. Http status code: ... Error:" wrapper and reads
`code`, `message` and `innerError` out of the JSON. The deployment keeps one line built from the code
("Business Central refused a schema change while installing X"); the failed app keeps the code's
sentence followed by Business Central's message verbatim; the diagnostics log keeps the response
whole. The page reads the log line back through the same parser, so a deployment stored before #930
(whose failure message was the long raw line) renders the same way.

## Services & seams

- **`IBcAdminClient` / `IBcAppManagementClient`** — HTTP seams (interfaces) over the two Admin Center
  surfaces (environments, and app management), so
  the orchestration is unit-testable without hitting Microsoft. This is the *same* sanctioned reason
  we introduced `IProcessRunner` for git/alc (a real test seam, two-impl-or-test rule satisfied).
- **`BcTokenService`** — singleton, in-memory token cache + client-credentials flow.
- **`ProjectConnectionService`** — writes/reads the connection config; owns the secret (encrypt on
  write, never return it), the Test-connection action, the environment fetch. Access-gated.
- **`ReleasePipelineService`** — CRUD over `OeReleasePipeline` (name, source build pipeline or GitHub
  repository, target environment, deployment schedule, schema sync mode). Access-gated like `PipelineService`.
- **`DeliveryService`** — creates an `OeProjectDelivery` when the user schedules a deployment of a chosen
  build (no auto-on-build in v1; from #934 a new build may *prepare* one for a person to approve,
  never send one); converts the picked local date+time to a UTC `scheduled_for` using
  the project's timezone; owns the atomic cancel/claim transitions. **As built:** the engine slice
  ships `ReleaseBuildNowAsync` (immediate run, `scheduled_for = now`) + `RunDeliveryAsync` (claim →
  upload → install → poll); it takes the access token through a narrow **`IDeliveryTokenSource`**
  seam (implemented by `ProjectConnectionService`) so the orchestration is unit-testable without the
  OAuth round-trip or the key ring — mirroring the BC client seams. The future-time
  scheduler and cancel surface land in the scheduling slice.
- **`DeliveryScheduler`** (`BackgroundService`) — polls for due `scheduled` rows, enqueues to
  **`DeliveryQueue`** (bounded `Channel`); **`DeliveryWorker`** drains and runs the publish under the
  triggering user's captured `AmbientOrganizationScope` identity. Persisted rows = restart-resume.

## UI surfaces

- **Project detail:** a "Business Central connection" section — tenant id, client id, secret
  (write-only) + secret-expiry, Test connection (flags missing GDAP), timezone, and the fetched
  environment list with Refresh. The single sensitive screen; owner/Admin only. Each environment row
  carries per-environment settings (its **update window** — start/end time, or
  "Any time"); these hang off the row's settings affordance so the table stays calm. Setting/clearing
  a window must survive a Refresh (it's user config on a fetched row — the upsert touches only the
  discovered fields, keyed on `(project_id, name)`), and a vanished environment keeps its window
  read-only. Each row also shows the environment's **status** (a badge, toned by the same
  classification the delivery gate uses, so the badge and the refusal never disagree) with its
  version underneath, and an **Open in Business Central** link built from `web_client_login_url` —
  the question the row has to answer is "is this environment safe to deploy to right now".
- **Deployment pipelines:** a listable surface alongside Build pipelines (own icon — e.g. `rocket` for
  build stays, a `send`/`upload-cloud` for deployment), with a create/edit dialog: name, source build
  pipeline or GitHub repository, target environment (picker), when installs run, schema sync mode
  (Force sync behind an acknowledgement). The list (`/pipelines/deployments`, #935) also says what each pipeline
  is doing: a "Shipping now" band per deployment in flight (which app of how many, and how long the
  last successful deployment took), the newest finished deployment's outcome, and the next one - a
  scheduled deployment, or for one handed to Business Central, the environment's next update. It is
  ordered by urgency (shipping now, needs attention, scheduled, the rest) and re-reads itself every
  two seconds while something is shipping, the way the pipeline's own page does. The environment,
  the solution and the source build pipeline are links.
- **Schedule a deployment:** lives on the **Deployment pipeline** — a "Deploy" action that's enabled once
  the source Build pipeline has a *successful* build. It defaults to the **latest successful build**
  (with the option to pick an older one), then "pick the date+time" (prefilled to the **next opening
  of the target environment's update window**, or now if it has none) → creates a scheduled
  `OeProjectDelivery`. The user can override the prefill to run now or any other time; doing so outside a
  set window is recorded on the delivery. Failed/in-progress builds aren't
  releasable. Production targets get an extra confirm; scheduling past secret expiry warns but allows.
  A "Deploy to…" shortcut on a successful build row in the Build pipeline's history can open this same
  dialog as a convenience, but the canonical action is on the deployment pipeline.
- **Delivery history:** per deployment pipeline, the `OeProjectDelivery` runs with status,
  scheduled/started times, per-app results, and **Cancel** (only while `scheduled`) / **Reschedule**.
- **Deployment pipeline page (`/pipelines/deployments/{id}`), as built (#929, #932):** ports
  `.design/handoff/ReleasePipelineBody.dc.html` on the `DetailPage` frame. The head names the
  solution, the source (build pipeline, or the repository for a GitHub-release pipeline) and the
  target environment as links, so a source and an environment with the same name are told apart.
  A summary card carries a health keyline and one factual sentence (deploying now / last deployment
  failed / scheduled / healthy / handed to Business Central / nothing yet), a "This pipeline"
  block of stored facts (source, install timing, the environment's delivery window when set,
  schema sync with the Force sync warning, last and next deployment) and a sunken "From Business
  Central" block read from the stored environment mirror - the page never calls Business Central
  to render. The failure sentence names the version the environment had before the run, or what
  the panel cache reports if someone read the environment since. The deployments list numbers
  deployments per pipeline for display ("Deployment 49"), shows the newest ten and extends in place
  with "Show older deployments"; a failed row carries the code's short sentence and the code as a tag, and opens into "What
  happened" (our sentence, then Business Central's message as given in its own labelled block, then
  what the environment reports installed), a suggested next step, and the raw response behind a
  fold with "Copy for support" (DeliveryRowPanel.dc.html, section 2; nothing is translated); a step strip
  (only the steps whose moments were recorded), per-app rows with their state word, version
  change, message and duration, and the diagnostics log (UTC, open on failures, with a copy
  button). A skipped app says "Skipped because it depends on X" only when the build's manifests
  show it does (`DeliveryService.GetSkipReasonsAsync`, read when a row opens), else "Skipped
  after X failed". The phone layout is a container query on the page. To feed it, the run now
  records per-app `started_at` / `finished_at` and `previous_version` (from the installed-apps
  read before the first upload, matched on app id then name), the delivery's
  `install_started_at` (the first upload accepted), and `cancelled_by_user_id`. Rows written
  before that have nulls and the page hides those cells. The approval band (#934) is described
  under *Prepared deployments*.
- **Deploy again (#931):** a failed row carries "Deploy again" (manage-gated; the phone layout
  moves it to a full-width button in the opened row), and when Business Central refused a schema
  change (`ExtensionChangeFailed`) on a deployment that did not already use Force sync, the failure's
  suggested next step carries "Deploy again with Force sync", which opens the same dialog with
  Force sync pre-ticked (DeliveryRowPanel.dc.html, section 4). The dialog names the build, the
  environment, the apps and when they install, and the apps the failed deployment already put in
  ("... is already on this version and is left alone"). "Use Force sync for this deployment only"
  is off by default and needs the pipeline editor's acknowledgement when ticked; Production
  keeps the Deploy dialog's acknowledgement; the one primary is "Deploy". The new delivery
  (`DeliveryService.ReleaseAgainAsync`) is an ordinary deployment of the same build through the
  same pipeline, now, with every check a deployment has; a one-time Force sync is snapshotted on
  the delivery's own `schema_sync_mode` and nowhere else, so the pipeline and the deployment after
  this one stay on the pipeline's mode. The row reads "Force sync, this deployment only" and the
  run's log says the same. No new column: the snapshot was already there.
- **Prepared deployments (#934):** see the section of that name below.
- **Pipelines dashboard (#955):** see the section of that name below.

## Pipelines dashboard (#955)

`/pipelines` is the page behind the sidebar's Pipelines parent: what built, what shipped, what
failed and what is waiting, across every solution the reader can see, before they drill into
Builds or Deployments. It is the dashboard archetype (the Admin dashboard's shape), built to
`.design/handoff/PagePipelines.dc.html` with the port notes in
`briefs/2026-09-pipelines-dashboard.md`. `PipelinesDashboardService` assembles it in one call,
reusing `ListReleasePipelineOverviewAsync` for the deployment side and `DeliveryFeedService` for
the deployment half of the timeline, under the lists' own gate (`VisibleProjectPredicate`).

- **Seven tiles**, each a link into its list, pre-filtered through the list's tabs (`?show=` on
  both lists): build pipelines (last run), builds in the last seven days (the newest, and its
  branch), build pipelines whose newest build failed, deployment pipelines (last deployment),
  deployments shipping now (app n of m, and where to), deployment pipelines whose last deployment
  failed, and prepared deployments waiting for approval (the oldest). "Today" in the head's
  sentence is the organisation's day, in the zone it shows times in.
- **Needs attention**, newest first: a failed newest build, a failed last deployment, a prepared
  deployment (its row opens the deployment pipeline page, where approving happens), a deployment
  pipeline aimed at an environment that is missing, being deleted or failed (it refuses
  deployments until re-pointed), and a Business Central client secret lapsing within 14 days on a
  solution with a deployment pipeline - one row per solution with its own app registration, one
  row for the organisation's shared registration. Nothing else: this is not where update windows
  or environment health in general live.
- **Recent activity**: the ten newest builds and deployments merged, each at its latest moment.
  The avatar is the person who started it; a pull-request build reads "PR" and a deployment the
  pipeline prepared reads "Auto". An agent acting over MCP acts as the person whose token it
  holds, so it shows as that person - nothing records it separately, and the sheet's "Agent"
  avatar is not drawn.
- **Facts only.** No build durations (not recorded), no upload progress, no forecasts.
- **Tool toggles.** The page answers while either `ToolKey.Pipelines` or `ToolKey.Releases` is on
  (`ToolAccessGate` treats the exact path as belonging to both), and draws only the half whose
  tool is on.

## Prepared deployments (#934)

v1 said "no auto-on-build" and "auto-deliver on build success is explicitly not v1", and for the
*install* that still stands. What #934 lifts is the step before it: with the pipeline setting
"Prepare a deployment when a new build succeeds" on, a new successful build **prepares** a deployment
through the pipeline, and a person decides. The exclusion was about the workbench installing into
a customer's environment without anyone choosing to; nothing here does that. What it cost to keep
it whole was that somebody had to notice a build had landed, open the pipeline and press Deploy -
the gap the issue names - and a prepared deployment closes that gap without moving the decision.

- **What is prepared.** When a build flips to `ready` (`ReleaseImportWorker`, after the build and
  its GitHub publish), `DeliveryService.ProposeReleasesForBuildAsync` writes a `proposed` delivery
  for every active deployment pipeline that draws from that build pipeline and has the setting on:
  the build, its apps as pending rows, and `scheduled_for` by the pipeline's rule (the next opening
  of the delivery window for `OurDeliveryWindow`, otherwise the moment it was prepared). The
  deployment is checked the way a hand-made one is (`ResolveReleaseAsync`, shared), except for the
  cached environment status and the access check: an environment mid-update when a build lands is
  ready again long before anyone approves, and preparing sends nothing, so the approval is where
  both are checked. A pipeline the build can't go through - environment gone, a stale setting, a
  multi-app build on a deferred schedule - is skipped with a warning in the log; the build is fine.
  Pull-request builds are never prepared. `triggered_by_user_id` stays null until someone approves.
- **One at a time.** A newer build **replaces** an unapproved proposal rather than stacking a
  queue: the older row goes `proposed → dismissed` (compare-and-set on `proposed`) with
  `replaced_by_project_build_id` set to the newer build, `dismiss_reason` "Replaced by build #N",
  and `cancelled_by_user_id` left null - no person did it. The same build twice, or an older
  build finishing after a newer one, changes nothing.
- **Approve.** `ApproveProposalAsync` (manage-gated) re-runs every check a hand-made deployment has,
  re-snapshots the pipeline's settings as they are now, makes the approver the triggering user the
  worker runs as, and schedules it by the pipeline's rule *from now*; then `proposed → scheduled`
  by compare-and-set, and a deployment due now is queued, exactly as the Deploy dialog does. From
  there it is an ordinary deployment: cancellable until claimed, reschedulable, run by the worker. The
  page asks for the Production acknowledgement before it calls this, as it does for every deployment.
- **Dismiss.** `DismissProposalAsync` (manage-gated) takes an optional reason (500 characters),
  `proposed → dismissed` by compare-and-set, with `cancelled_by_user_id` (the column #929 added
  for who cancelled) naming the person and `dismiss_reason` holding their reason, or null. The
  apps are marked "Not sent". Nothing was sent, so nothing needs undoing.
- **Stored facts, not log text.** What became of a prepared deployment is its status and three
  columns: `cancelled_by_user_id` (who dismissed it), `dismiss_reason` and
  `replaced_by_project_build_id` (a plain id, no foreign key: a fact about history, and the build
  may be removed later). The last two arrived in the same migration as the pipeline setting. The
  log still gets a line for each step ("Prepared from build #N ...", "Approved by ...",
  "Dismissed by ...", "Replaced by build #N ..."), and a run of an approved deployment appends to
  those lines rather than writing over them, but the log is for reading: nothing parses it back.
  A first version did, telling a dismissed proposal from a cancelled deployment by its log lines;
  that was replaced before merging because a change of wording would have silently changed what
  the pages said. A `dismissed` row never was a deployment, so it is not "the last deployment" on the
  pipeline's page or the list.
- **Pages.** The pipeline page draws a proposal in the "Waiting for approval" band at the top of
  the deployments card (`ReleasePipelineBody.dc.html`), not as a row: its number and build, one
  sentence (which build finished when, where it would go, when it installs once approved), and
  Approve / Dismiss for somebody who manages the solution - outline buttons, so Deploy stays the
  page's one primary. Approve opens a confirm naming the build, the apps, the environment and when;
  Production adds the Deploy dialog's acknowledgement. Dismiss opens one with the optional
  reason. A dismissed or replaced proposal stays in the list, reading "Dismissed" or "Replaced",
  and opens into who and why with a two-step strip (Prepared, then Dismissed or Replaced). The
  summary card says "Waiting for approval" after a failure and before a schedule. The Deployment pipelines
  list gives such a pipeline the draft keyline and a person glyph, counts it under "Needs
  attention", fills its Next deployment cell with "Waiting for approval", and opens with "1 deployment
  waiting for approval: <pipeline>". The solution page opens with the same line, naming the build.
  The status is `proposed` in `ProjectDeliveryStatus` and `RowStateIcon` (draft keyline, `user`), and `dismissed` beside it (the cancelled keyline, `x`).
- **Never automatic, never an agent.** Nothing approves a proposal but a person pressing Approve.
  The MCP tools read it - `list_deployments` and `list_recent_deployments` report `proposed` and `dismissed`, and
  `list_deployment_pipelines` reports the setting - and there is no tool to approve or dismiss one.
- **Not built.** A notification when a deployment is prepared is a later slice; the list's line and
  the band are the v1 signal. A pipeline that installs **GitHub releases** does not prepare one:
  there is no sweep that sees a new release on GitHub today (releases are listed on demand when
  somebody opens the dialog), so the setting is hidden for that source and saved as false.

## Security & tenant isolation

- Every new row carries `organization_id`; reads ride the EF query filter. No new
  `IgnoreQueryFilters()` — deliveries run under the triggering user's captured identity in the worker
  (the blessed deferred-work analogue), exactly like the build worker.
- The secret never leaves the server: encrypted column, write-only field, redacted from logs and from
  the delivery's stored API-response log.
- "Test connection" and "Publish" are owner/Admin-gated via `ProjectAccess`.
- Production deploys want a deliberate confirm; consider an audit-log entry per delivery.

## MCP parity

A future `deploy_build` / `list_deployments` MCP tool would let agents drive delivery the way humans
do. Not v1, but design the `DeliveryService` API so a tool can sit on it without reaching past it.

**As built (phase 4b):** shipped as `DeliveryTools` (`Services/Mcp/Tools/DeliveryTools.cs`) — a
trio so the flow is usable end-to-end: `list_deployment_pipelines` (discover the id), `deploy_build`
(deploy a `ready` build *now*, delegating to `DeliveryService.ReleaseBuildNowAsync`), and
`list_deployments` (poll history with per-app outcomes). Publishing runs in the same in-process
worker as the web "Deploy now", so `deploy_build` returns the new delivery id to poll rather than
blocking. Access-gating + validation come from `DeliveryService`/`ProjectAccess` unchanged; the tool
only maps `ProjectAccessDeniedException`/`PlanValidationException` to `McpException`. Scheduling a
*future* delivery and the Production extra-confirm stay web-only — the agent path is deploy-now,
including for a pipeline that installs in the delivery window (#928): the tool deploys immediately
and the delivery records that it ran outside the window when it did. `deploy_build` always uses
the pipeline's own schema sync mode and has no parameter to change it: a one-time Force sync
(#931) is a person's decision, taken in the web UI behind its acknowledgement, and no agent or
palette write may escalate to it. A deployment the pipeline prepared from a new build (#934) is
read-only to agents: it reads as `proposed`, then `dismissed` if set aside, and approving or dismissing it is a person's act in
the web UI.
A preview build (#994: one of the nightly preview check's builds against the next minor or
next major) is refused by `deploy_build` the same way the web refuses it, because the refusal
lives in `DeliveryService.ResolveReleaseAsync` rather than in either surface; the build reads
tools (`list_pipeline_builds`, `list_solution_builds`, `get_solution_build`) say `isPreview`
so an agent knows before it asks, and `list_pipelines` returns each pipeline's check results.
See `object-explorer-project-builds.md`, "The nightly preview check".

**The Deliver reads (#912):** ten read-only tools in their own class, `DeliverTools`, so the
area's one write stays in `DeliveryTools` and a test (`DeliverToolsTests`) can walk the new
class and fail on anything that is not `ReadOnly = true`. `get_solution`, `list_environments`,
`get_environment`, `list_environment_history`, `list_upgrades`, `list_recent_deployments`,
`list_customer_contacts`, `get_customer_access`, `list_customer_knowledge`,
`list_customer_modules`. The rules they keep:

- **The mirror, never the tenant.** Environment facts come from `oe_project_environments` and
  `oe_environment_apps` as the nightly sweep or the last Refresh left them, and every output
  carries the read time of the row it came from (`environmentReadAt`, `nextUpdateReadAt`,
  `updateWindowReadAt`, `installedAppsReadAt`, `versionReadAt`). Sessions and Business
  Central's operations log are live reads with the customer's credentials and are not here.
- **The pages' gates.** Everything goes through the pages' own service methods, so
  `ProjectAccess.VisibleProjectPredicate` decides what exists; a Private solution the caller
  is not on is absent, not locked. `list_upgrades` refuses a caller without the
  environment-updates grant anywhere, as the Upgrades page does, and marks per solution
  whether they may move the date (`UpdateOpsProjectPredicate`).
- **Planned upgrades are read, never driven (#984).** `list_planned_upgrades` lists the
  upgrade headers with their derived status and a count per line state (open first; the
  archive with `includeArchived`, searchable by name and target version), and `get_upgrade`
  returns one header with every visible line: the environment, its current version and next
  update as `list_upgrades` shows them, the derived state word, the last action taken from
  the upgrade, the assignee and the check. Both sit on `EnvironmentUpgradeService`'s own
  reads, so a line from a solution the caller cannot see is left out of the lines and the
  counts, and both ask for the environment-updates grant as `list_upgrades` does. Creating an
  upgrade, adding lines, and every move, start and check stay on the page: the fleet-writes
  rule in `environment-updates.md` holds for waves as for single rows.
- **Query additions, not page edits.** Four reads had no page method an agent could use:
  `UpgradeFleetService.ListFleetDetailsAsync` (the fleet with each row's windows, one query
  rather than one per row) and `ListInstalledAppsAsync` (the installed-apps mirror, through
  the same visibility join), `ProjectCustomerInfoService.ListCustomersKnownByAsync` (the
  "which customers does Anne know" direction), and `DeliveryFeedService` (deliveries across
  solutions; the page reads one deployment pipeline at a time).

## Suggested phasing

1. **Connection + auth + Test** (Project columns incl. secret-expiry, secret handling,
   `BcTokenService`, `IBcAdminClient` list-environments with GDAP-missing detection, Test-connection,
   expiry warning). No publishing yet — just prove the creds.
2. **Deployment pipelines + manual publish** (`OeProjectEnvironment` fetch, `OeReleasePipeline` CRUD,
   "Deploy this build now" running the full upload→install→poll in-worker, no scheduling yet).
3. **Scheduling** (pick a concrete date+time per delivery, `DeliveryScheduler`/`Queue`/`Worker`, the
   atomic claim/cancel transition, cancellable-until-claimed, restart-resume, delivery history UI).
4. **Polish** (partial-failure reporting, Production confirms, secret-expiry-vs-scheduled-time
   guard, audit-log entries, MCP tool). *Auto-deliver on build success is explicitly **not** v1* (#934 later added a prepared deployment a person approves; the install is still never automatic).
   **As built:** partial-failure reporting + Production/Force-Sync confirms shipped in phases 2–3.
   **Phase 4a** adds the secret-expiry-vs-schedule guard (a warn-but-allow note in the deployment dialog
   and reschedule modal when the picked time is past the secret's expiry — the run's hard-fail stays
   the backstop) and audit-log entries: `OeReleasePipeline` (create/edit/delete) and `OeProject` are now
   audited, the latter **column-scoped** to BC connection/secret changes so the background discovery
   worker's cache writes and name edits don't flood the log. Deliveries keep their richer
   self-history rather than the entity-granularity interceptor (which would miss the `ExecuteUpdate`
   cancel/reschedule transitions and flood on every worker save). **Phase 4b** is the MCP trio above.

**As built (phases 1–3 are in `main`):**
- Phase 1 = #462; phase 2 = #465 (CRUD) + #468 (publish engine) + #469 (UI).
- **Phase 3 (scheduling):** the per-environment update window (`update_window_start`/`update_window_end`
  on `OeProjectEnvironment`, edited on the project's BC page), the schedule picker (prefilled to the
  next window opening in the project tz), `DeliveryService.ScheduleDeliveryAsync` / `CancelDeliveryAsync`
  / `RescheduleDeliveryAsync`, a `DeliveryScheduler` poller, and Cancel/Reschedule + an "outside window"
  badge in delivery history. Overriding the window is audited via
  `OeProjectDelivery.ScheduledOutsideWindow`.
  - **Scheduler tenant scope — deliberate divergence from `ReleaseAutoImportScheduler`:** the delivery
    scheduler enumerates **all non-pending orgs *including the system org*** (org enumeration only —
    per-org work stays filtered; the orgs table carries no filter, so no bypass is needed). It must *not* skip
    the system org the way the release auto-importer does, because in single-tenant (and fresh
    bootstrap-admin) deployments the working org **is** the system org, so its deliveries have to run.
  - **Restart-resume:** scheduled rows survive a restart (re-picked on the next due sweep); a delivery
    orphaned mid-publish is failed on the scheduler's first per-org sweep (nothing runs yet at startup,
    so an active delivery is never tripped) — folded into the scheduler to avoid a second
    cross-org startup site.
  - Times are entered/displayed in the project's `bc_time_zone` (customer's local time). The window
    may wrap past midnight.

## Decisions (resolved)

- **Build vs Deployment:** two distinct concepts, as a **separate `OeReleasePipeline` entity** (not a
  `kind` column on `OePipeline`). `OePipeline` (build) stays as shipped; `OeReleasePipeline` draws from one
  build pipeline and targets one environment. Build-once-deploy-many falls out for free (one build
  pipeline → several deployment pipelines).
- **One environment per deployment pipeline** (1:1). Naming reads *"Deploy Contoso App on Production."*
- **Environments are persisted** as an `OeProjectEnvironment` child (fetched + refreshable), so the
  picker and deployment pipelines share one row — its id, its update window and its last-known status —
  rather than each deployment pipeline inlining an environment name.
- **Deployment trigger:** the "Deploy" action is on the **Deployment pipeline**, enabled once its source
  Build pipeline has a successful build; defaults to the latest successful build, with the option to
  pick an older one. (A build-history "Deploy to…" shortcut opens the same dialog.)
- **Credential model:** one Entra app **per project/customer** (cross-tenant app registrations are
  being deprecated). Track the secret's expiry (max 2-year lifetime) and warn before it lapses.
- **Environment listing:** fetch via the Admin Center API as the primary path; Test connection
  **names the step that failed** — 401 (app not on BC's authorized-apps list) and 403 (permission or
  GDAP) are separate outcomes with separate remedies. GDAP is *not* assumed: the same connection
  serves the maintainer's own tenant, where no delegated-admin relationship exists at all. Manual
  entry is a fallback.
- **Deployment schedule:** `Immediate` (default), our own delivery window (`OurDeliveryWindow`, #928),
  `NextMinorUpdate` and `NextMajorUpdate` offered; `UpdateWindow` is supported by the engine and
  deliberately not in the picker (see *Open questions*).
- **Trigger model:** no auto-publish in v1. The user explicitly schedules a delivery for a concrete
  date+time; it then runs automatically at that time, and is **cancellable until a worker claims it**.
  **Revised by #934:** a pipeline may *prepare* a deployment when a new build succeeds, for a person to
  approve; it still never publishes on its own (see *Prepared deployments*).
- **Per-environment update window (revised):** each `OeProjectEnvironment` carries a recurring daily
  update window (start/end time in `bc_time_zone`, nullable = any time), mirroring BC's admin-center
  environment update window — the model BC admins already know. It is a **default, not a lock**:
  scheduling prefills `scheduled_for` to the next window opening, and the user can override to run now
  or any time, with overrides recorded on the delivery. This **revises** the earlier framing that the
  schedule was "not a recurring window"; the *execution* model is unchanged (a concrete per-delivery
  `scheduled_for`), but the **default** that seeds it is now a per-environment recurring window rather
  than `OeReleasePipeline.default_publish_time` (which this supersedes and likely retires).
- **Expired-secret behaviour:** warn-but-allow at scheduling; the run hard-fails with a clear "secret
  expired — rotate it" message if it's actually lapsed when the worker fires.

## Open questions

The shape is settled (see Decisions). What's left is **implementation detail to settle when building**,
not architecture:

- **Install language.** `pteInstall` takes a `languageId` that sets the extension's install locale.
  We send none, because the workbench has no language concept and `en-US` would be wrong for a Danish
  customer. It probably belongs on the deployment pipeline, beside the other per-target settings.
- **Whether to offer "install in Business Central's update window".** The API's `UpdateWindow`
  schedule is supported by the engine and deliberately absent from the picker. It means *whenever
  Microsoft next patches this environment*, which is a different promise from the delivery window
  the picker now offers (#928). If it is ever offered, the two sit side by side in one list, so the
  copy has to keep them apart: ours is named for the environment ("In Production's delivery
  window") and starts when *we* send; Microsoft's must name Microsoft ("When Business Central next
  updates Production") and starts when *they* do. The internal names are already apart —
  `OurDeliveryWindow` against the wire's `UpdateWindow` — so the question is only the product one.
- **Re-deploying a version that's already scheduled.** Decided (#937): pre-check. Before uploading
  on a deferred schedule the run reads `scheduledPteOperations` once and refuses an app whose same
  version is already waiting for the same schedule, with "{app} {version} is already waiting for the
  next minor update on {environment}; cancel it there first." Still open: whether a *different*
  version waiting for the same schedule also 400s, or replaces the waiting one.
  **Re-deploying a version that's already installed** is answered too (#931): the run compares
  each app with the installed-apps read it already makes (by app id, the name only when the
  artifact has none), and an app already on the build's version is marked Skipped with "Already on
  {version}." and the run goes on to the next. That is what makes deploying again after a partial
  failure safe, with no setting for it. A run where every app was already on its version uploads
  nothing and ends as deployed.
- **Mixed-tool invisibility.** A version scheduled through the web client's Extension Management page
  isn't visible in the admin center until it installs, and vice versa. If a customer's own consultant
  uploads that way while we schedule through the admin center, neither surface shows the other's
  work. That needs UI copy before it generates support calls.
- **Partial-failure semantics:** one app installs, a dependent fails — surface like the build report.
- **Secret-expiry warning lead time** (the "~N weeks" before expiry to start nagging).
