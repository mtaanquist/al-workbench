# Artifacts: per-project builds and downloadable `.app` deliverables

> **Naming.** The entity this doc calls a *project* is called a **Solution** in the
> product — nav, headings, routes (`/solutions*`) and MCP tool names — because
> Business Central now uses "project" for its own project-accounting area. The C#
> types, tables and columns still say `OeProject`, deliberately; CLAUDE.md has the
> table of which side says which. Prose below that says "project" is describing the
> code and is still correct.

This document specifies the **Artifacts** tool and the navigation/entity rework around it. It
promotes the Object Explorer's compile-from-source path (`object-explorer-project-builds.md`) into
a first-class, end-user-facing surface: point a **Project** at one or more Git repositories, build
it (per user, per commit), and get the compiled `.app` files back — GitHub-Releases style, keyed by
commit hash. The compiled output still flows through `ReleaseImportService.ProcessReleaseAsync` so
its objects remain navigable in the Object Explorer; what changes is *who* owns the build, *how*
it's credentialed, and *where* it surfaces.

**Status:** implemented on the `feat/artifacts` branch across four slices (per-user tokens; the
`OeProjectBuild` model; the Projects/Artifacts tools + nav rework + OE split; MCP parity + the
existing-data backfill). This doc is the behavioural contract. Where it diverges from
`object-explorer-project-builds.md`, this doc wins and that doc is updated to describe the
post-split OE (symbol navigation only).

**Update (post-launch): the Pipeline layer.** The **Artifacts** tool was renamed to **Pipelines**
(the artifact is the *product* of a build, not the action), and a first-class **`OePipeline`** entity
was introduced between Project and Build. The model is now **Project → Pipeline(s) → Build(s) →
Artifacts**:

- A **Project** is a *customer* — repositories, localisation, owner, and a delivery "location": a
  Business Central environment for pushing builds via BC's Admin Center API (now shipped — see
  `saas-delivery.md`). Setup only; it no longer triggers builds.
- A **Pipeline** (`oe_pipelines`, org-scoped, soft-deleted) is a *named build configuration* under a
  project. A project has **many** — different customer environments get different subsets of
  extensions. The pipeline owns the extension selection (`RequestedAppIdsJson`, null = build all).
- A **Build** (`OeProjectBuild`) is one *run of a pipeline*: it gains `PipelineId` (keeps `ProjectId`),
  and **snapshots** the pipeline's selection onto its own `RequestedAppIdsJson` at run time so
  editing the pipeline later doesn't rewrite history.

Creating/editing a pipeline shows the project's extension checklist from a **per-project discovery
cache** (`oe_projects.discovered_extensions_json`), warmed in the background when repos change and
refreshable on demand — so the editor opens instantly instead of cloning every time. **Build** then
just runs the pipeline's saved selection. The `OeProjectBuild` entity, the `/artifacts/build/...` download
endpoints, `ArtifactService`, and the `ArtifactsTools` MCP surface keep their names ("artifact"
still names the downloadable `.app`). Routes: `/pipelines` (landing, lists pipelines),
`/pipelines/{pipelineId}` (pipeline detail), pipelines listed/created on the project detail page;
old `/artifacts` → `/pipelines` and `/artifacts/{projectId}` → `/projects/{projectId}` redirect. A
migration backfills a `Default` pipeline (build-everything) per existing project and re-parents its
builds. MCP adds `list_pipelines` + `list_pipeline_builds` (`list_solution_builds` stays,
project-wide). The earlier per-*build* extension picker is superseded by this per-*pipeline*
selection. The delivery target — publishing a build to a BC environment via the Admin Center API —
has since shipped; see `saas-delivery.md`. Details inline below.

## Pipeline names

People named pipelines freely and every solution ended up reading differently, so since 2026-10 a
pipeline's name is generated from its setup (`PipelineNames`) and is not edited. The name holds only
what tells two pipelines in one solution apart; settings that can be switched on and off (preview
check, version numbering, schedule, approval, branch rule) stay out, so changing one never renames
anything. The solution is not in the name either: every surface that shows a pipeline name, emails
included, shows the solution beside it.

- **Build pipeline:** `{branch}`, or `Default branch` when none is set, plus the extensions in
  brackets when it builds only some: the extension's name when it is one, otherwise how many.
  `main`, `release/25.0 (CRONUS Sales)`, `main (3 extensions)`.
- **Deployment pipeline:** `{source} to {environment}`, where the source is the build pipeline's name
  or `{repository} releases`. `main to Production`, `cronus-apps releases to UAT`. The environment's
  type is not added, since environment names usually carry it; lists show it beside the name.

Names stay unique per solution. Two pipelines set up the same way are expected to be rare, so a clash
is refused with a message and the editor then offers a name field; a typed name is stored with
`name_is_custom` and kept on later saves until it is cleared. Renaming a build pipeline renames the
deployment pipelines named after it, except typed ones and any whose new name is taken. Nothing else renames a pipeline on its own: an environment, repository or extension renamed
elsewhere shows in the name the next time the pipeline is saved. When a deployment pipeline's name is
too long, the source is shortened so the environment stays. Migration
`GeneratePipelineNames` renamed every existing active pipeline the same way; one whose generated name
was already taken kept its old name, marked as typed. That rename runs in SQL, outside the audit log,
so the old names are not kept.

## Why

The compile-from-source pipeline shipped inside the Object Explorer admin surface: a single
per-org PAT, projects managed under `/admin/object-explorer`, and each build landing as a
`project`-kind Release listed alongside Microsoft and third-party imports. Three things make that
the wrong long-term home:

- **Access is personal, not organisational.** A consultant may have access to some customer repos
  and not others. A single shared org PAT can't express that, and a build that "succeeds" using
  someone else's token hides a real access problem. Tokens must be per-user so a build fails for
  the person who lacks access.
- **Projects are an entity, not an admin setting.** The list of customer projects is something
  every developer browses and downloads from — not an Editor/Admin authoring chore. It deserves its
  own tools, ownership, and a download-centric UI.
- **Builds pile up.** Commit-keyed builds produce thousands of `project`-kind Releases over time.
  They must not pollute the Object Explorer's release list or its global compare picker; they're
  reached by deep-link from the artifact that owns them.

The hero task: a BC developer ("the builder") opens **Artifacts**, finds a customer project by
name, and downloads the newest build's `.app`s in a click or two.

## Navigation & tools

The split produces **three** distinct Tools-section entries where there was one overloaded one.
New order (User role and up for the public tools):

`Home · Piper · Templates · Cookbook · Object Explorer · Projects · Artifacts · Translator · MCP`

- **Templates** — the renamed Workspace/Extension *generator* (today's "Projects" item, which
  already routes to `/templates`). Its sub-routes move off the `/projects/*` namespace onto
  `/templates/workspace` and `/templates/extension` to free `/projects` for the entity tool. Only
  `/projects/extension` gets a redirect (preserving its `?template=` query); `/projects/new` can't,
  because that path is now the new-project page — old workspace-generator bookmarks to it land on
  the new tool and should use `/templates/workspace`.
- **Projects** — the customer/project entity: a directory you browse and create in, and where the
  owner configures repositories and settings. *Setup.* (No longer triggers builds — see the
  post-launch update above.)
- **Pipelines** (renamed from **Artifacts**) — the build surface: the **New build** action,
  per-project build history, changelog, logs, downloadable `.app`s, and project-scoped build
  comparison. *Build & deliverables.*

Build **trigger** (the **New build** action with its extension picker) is a Pipelines affordance
(owner/admin). Build **download** is also on Pipelines (any signed-in user).

## Roles & ownership

- An `OeProject` records `CreatedByUserId` — its **owner** — and a `Visibility`
  (`Public` / `ReadOnly` / `Private`) with a set of assigned **teams**. Any signed-in user may
  create a project; who may read or change an existing one follows from those two.
- **Browsing and downloading** is open to everyone in the org *except* on a `Private` project,
  where it is limited to the owner, a member of an assigned team, an org **Admin**, or a
  SiteAdmin. A `Private` project a viewer has no grant on still shows its **name** in `/projects`,
  as a greyed locked row and nothing more.
- **Adding/removing repositories, triggering builds, and editing settings** are restricted to the
  owner, an org **Admin**, a SiteAdmin, or a member of a team assigned to the project.
- **Deleting** is deliberately stricter: owner, org Admin, SiteAdmin only. A team grant is about
  doing the work on a project, not about ending it.
- Enforced in the service layer (source of truth, via `ProjectAccess`) and mirrored in the UI —
  the affordances are hidden for everyone else, but hiding a button is a courtesy, not the gate.
- No new role: "Admin" is the existing org `Admin`. Teams are not a role; they are a named group
  a project's access is granted to. See `teams-and-visibility.md` for the model, the
  `Visibility != Public` ⇔ *at least one team* invariant, and the full gated-surface inventory.

## Credentials: per-user repository tokens

The per-org `OrganizationSettings.AzureDevOpsPatEncrypted` / `GitHubPatEncrypted` columns are
**retired**. Two things replace them:

- **`UserRepositoryToken`** — a per-`(user, organization, provider)` token, encrypted with the Data
  Protection key ring under per-provider purpose strings
  (`ALDevToolbox.UserRepositoryToken.GitHub` / `…AzureDevOps`), mirroring the SMTP-password
  pattern in `SystemSettingsService` and the per-(user, org) scoping of `PersonalAccessToken`. A
  unique index on `(UserId, OrganizationId, Provider)` keeps it one token per provider per user.
  Managed by the user on an account page; the view exposes only presence + last-used, never
  ciphertext. The audit interceptor redacts the ciphertext column.
- **Org allowed-providers setting** — a multi-select (`GitHub` / `AzureDevOps`, at least one
  required) on `OrganizationSettings`, managed by an Admin. It gates which token fields a user sees,
  which providers the add-repo picker offers, and rejects tokens/repos for a disallowed provider
  with a field-keyed `PlanValidationException`. The common case (one provider) means a user never
  sees an irrelevant token box.

A build clones each repo as the **triggering user**, resolving that user's token for the repo's
provider. A missing or unauthorised token fails the build visibly, attributed to the right person.
The token is decrypted only inside the build service, only for the clone, injected as a transient
`http.extraHeader` credential — never on disk, never logged, never in the clone URL.

## The build entity

Builds are split off `OeRelease` into a first-class **`OeProjectBuild`**. This is the central
modelling decision: a build is a *set* of `(repository, commit)` pairs with logs, a changelog, and
multiple downloadable `.app`s — none of which `OeRelease` models — while still producing exactly one
`project`-kind `OeRelease` for object navigation.

New entities under `Domain/Entities/ObjectExplorer/` (`oe_` tables, org-scoped via the standard
query filter):

- **`OeProjectBuild`** — `ProjectId`, `StartedByUserId`, `Branch`, `Status`
  (`queued|building|ready|failed`), `BcVersion`, `StartedAt`, `FinishedAt`, `FailureMessage`,
  **`RequestedAppIdsJson`** (the per-build extension selection — a JSON array of app-id GUIDs, or
  `null` for "build everything"; see "Extension selection" below), and **`ReleaseId`** (nullable FK
  to the produced `OeRelease` — *the Object Explorer hook*).
- **`OeProjectBuildRepoCommit`** — `(ProjectBuildId, ProjectRepositoryId, CommitHash, CommittedAt)`.
  The per-repo keying; a build is identified by this set, not a single hash.
- **`OeProjectBuildCommit`** — the changelog: `(ProjectBuildId, ProjectRepositoryId, ShortHash,
  Message, Author, CommittedAt)`, captured at build time.
- **`OeProjectBuildArtifact`** — a downloadable deliverable: `(ProjectBuildId, FileName, AppId, AppName,
  AppVersion, RuntimeVersion, SizeBytes, Content)`. `*.dep.app` is excluded at ingest, so it never
  appears as a download. `AppId` (#901, Part 3) is the manifest's app id, lower-case GUID text, stamped
  when the row is written; it is what a later build looks a dependency up by (see "Resolve symbols" in
  `object-explorer-project-builds.md`). Rows retained before the column existed are stamped once by a
  startup pass that reads each package's own manifest; a row whose bytes are not a readable `.app`
  keeps a null id and is never a candidate.
- **`OeProjectBuildLog`** — `(ProjectBuildId, ProjectRepositoryId?, Content)` — captured clone +
  `alc` stdout/stderr, with a `Raw log` download.

`OeProject` drops `AutoBuildEnabled` (builds are user-initiated only). The existing
`oe_project_build_results` table is superseded by `OeProjectBuildRepoCommit` + `OeProjectBuildArtifact`
and migrated onto them.

## Build flow

The clean seam in `Services/ObjectExplorer/` is preserved: `ProjectBuildService.BuildAsync`
produces uploads, and `ReleaseImportService.ProcessReleaseAsync` ingests them into an `OeRelease`
unchanged. The lifecycle is wrapped in `OeProjectBuild`:

1. `StartBuildAsync(projectId)` (owner/admin) creates an `OeProjectBuild` (`queued`, with
   `StartedByUserId`) and the `OeRelease` (`Kind=project`, `Status=ingesting`), links them via
   `OeProjectBuild.ReleaseId`, and enqueues the existing `ReleaseImportJob`. A manual build is
   refused while another build of the same pipeline is still `queued` or `building` (against the
   current version, with its release still ingesting, so a lost job cannot lock the pipeline);
   the pipeline page shows Build disabled as "Build running" until it finishes. The nightly
   preview check neither blocks nor is blocked by a manual build.
2. The worker runs `BuildAsync`, which now also:
   - clones each repo with the **triggering user's** token and records HEAD per repo
     (`OeProjectBuildRepoCommit`);
   - computes the changelog per repo as `git log <prev>..<new>` against the project's **last
     successful build**, with a merge-base ancestry check and guards for first-build /
     force-push (non-ancestor) / very large ranges (cap ~100, "…and N more")
     (`OeProjectBuildCommit`);
   - captures clone + `alc` output (`OeProjectBuildLog`);
   - excludes `*.dep.app` and **retains** the real `.app` bytes (`OeProjectBuildArtifact`) — new,
     since today's uploads stream into ingest and aren't kept;
   - still returns `outcome.Uploads` for `ProcessReleaseAsync`.
3. `OeProjectBuild.Status` flips ready/failed alongside the Release flip. The detail page's bounded
   status poll drives "Building…" → "Ready" live.

### Extension selection

A project's repositories often contain extensions you no longer want to compile (a retired legacy
app). The **New build** action lets the user pick which to build:

- **Cached discovery.** The pipeline editor's checklist is served from a per-project cache on
  `oe_projects` (`discovered_extensions_json` + `discovered_at` + `discovery_error`), so it opens
  instantly. The cache is warmed in the background by `ProjectDiscoveryWorker` — a small in-process
  queue/worker pair (`ProjectDiscoveryQueue`, in-memory dedupe, no external dependency) mirroring the
  release-import pair — which runs `ProjectBuildService.DiscoverExtensionsForCacheAsync` under the
  requesting user's captured identity (needed so the per-user repo token resolves off-request). The
  discovery itself is a blobless, `--no-checkout`, sparse-`app.json` clone of each repo (fast even on
  repos whose `.git` history is bloated by the `.alpackages` binaries older repositories committed,
  before builds fetched third-party symbols from the public feeds), walked for `app.json`. The
  request side (`ProjectDiscoveryService`) gates the enqueue (owner/Admin + existence) and reads the
  cache back. A refresh fires on repo changes (create/update with repos) and from the editor's
  **Refresh** button; the editor polls while a discovery is in flight and auto-triggers one whenever
  it opens on a project with no usable list — never discovered, or whose last attempt failed (a
  cached failure is retried, not shown, because its cause is often fixed elsewhere in the
  meantime). A failed refresh records `discovery_error` and leaves
  the prior good list intact — discovery is a picker convenience, so the build re-clones and filters
  by the pipeline's saved app-ids regardless. The discovery clone is intentionally separate from the
  build's full clone (the changelog needs history).
- **Persisted on the build.** The picked app-ids are stored on `OeProjectBuild.RequestedAppIdsJson`
  (the build row is the source of truth, so a restart-resumed job rebuilds the same subset). When
  *every* extension is selected the value is `null` — "build everything" — so an app added to a repo
  after discovery is still built. App-ids are compared normalised (trimmed, de-braced, lower-cased)
  so a selection captured at discovery matches the manifest read at build time.
- **Applied in `BuildAsync`.** After discovery, the worker narrows the discovered set to the
  selection before resolving symbols and compiling; excluded apps are noted in the build log.

## Object Explorer split

- Project management moves out of `/admin/object-explorer/*` into the Projects/Artifacts tools; the
  old `AdminProjects` / `AdminProjectDetail` pages are removed.
- **`Kind=project` Releases are unlisted across the Object Explorer's *global* surfaces** — the
  release browser, the org-wide A/B compare picker, and any other org-wide release dropdown or
  search scope. Audit `ObjectExplorerService` listings, the global compare picker, and any
  `WHERE kind …` enumeration, and exclude project releases from the global ones.
  **One deliberate exception:** the `/object-explorer` Third-party tab surfaces each pipeline's
  *latest ready* build release (`ObjectExplorerService.ListLatestPipelineBuildReleasesAsync` — one
  row per pipeline, grouped under the project's name next to the manual third-party publishers) so
  a pipeline's current objects are findable without opening its project first. Older builds remain
  Artifacts-only, and MCP `list_releases` still excludes all project releases.
- **Comparing two builds of the same project is kept** — it's genuinely useful — but scoped to that
  project. Artifacts offers a "Compare builds" picker listing only *this project's* builds;
  selecting two reuses `ReleaseComparisonService.CompareReleases` on the underlying Release ids and
  the existing compare view. Only the picker is project-scoped; the global picker never lists
  project builds.
  The Compare picker on a build's own release page is the same project-scoped list: the other
  ready builds of that project, its own pipeline's first, newest first, each named "Build #N of
  {pipeline}" because every build of a project shares one release label (#1075). Builds of a
  deleted pipeline are left out, and the list stops at the 50 most recent.
- The only path into a build's objects is: open the artifact → deep-link to
  `/object-explorer/release/{ReleaseId}` (or the project-scoped compare view). `OeReleaseDetail`
  stays reachable by id and gains a "back to artifact" affordance so a deep-linked user isn't
  stranded in an unlisted release.
- Each build still creates a full Release + module/object ingest — visibility changes, not whether
  the Release exists.

## UI

Recreate the Claude Design handoff screens as idiomatic Blazor (real `.razor` components,
server-side `@code`, existing CSS tokens, Lucide via `Icon.razor`, downloads via a minimal-API
endpoint) — not a port of the prototype's structure. Every list page renders loading / empty /
populated; one primary button per page.

- **Solutions** (`Components/Pages/Projects/`): `ProjectsBrowser` (`/solutions`) — searchable
  directory, `+ New solution` primary, latest-build status chip linking into Pipelines;
  `ProjectDetail` (`/solutions/{id}`, also `/solutions/new`) — the solution's **settings**, grouped
  behind a left sub-nav so each concern loads on its own instead of one long scroll: **General**
  (name + default country, with a read-only audit trail), **Repositories** (allowed-provider editor),
  **Business Central** (the SaaS connection + environments, owner/admin only), **Pipelines** (links
  into the Pipelines tool), and a **Danger zone** delete. Create mode (`/solutions/new`) shows just
  General + Repositories until the solution exists, and starts with no repository rows so the empty
  state and **Add repository** carry the first run. *Setup only — the single primary action is
  **Create solution** in create mode and **Save solution** once it exists; building moved to
  Pipelines.*

  **Each tab has its own address** (#1077): `/solutions/{slug}/general`, `/repositories`, `/bc`,
  `/pipelines`, `/symbols`, `/access`; Customer, the default, is the bare `/solutions/{slug}`.
  Opening one of those lands on that tab. Clicking a tab switches it in place and rewrites the
  address bar (replacing the entry, so Back leaves the solution) rather than navigating: General
  and Repositories share one Save, and a navigation builds the page afresh and would drop what
  was typed. A tab name that is unknown, or one this person cannot open, lands on the bare
  address. The old `?tab=` links still work and are forwarded to the path form. The create form
  (`/solutions/new`) has no address to put a tab in.

  **Readable addresses.** A solution also answers at `/solutions/{slug}` (`cronus-a-s`), and that
  is the address the app links to and the one the page settles on: `/solutions/{id}` still
  resolves and forwards there, keeping the tab. The slug is lowercase ASCII words joined by
  dashes, unique per organisation among active solutions, never all digits (that shape is an id)
  and never `new`. It is derived from the short name (ABJ becomes `abj`), or the name when there is none, on create (a counter is appended when two names
  fold to the same slug), kept on a rename so saved links keep working, and editable as
  **Web address** on General, where a blank field makes a fresh one the same way. Environments
  use it too: see `.design/environment-updates.md`, "The environment's own page".
- **Pipelines** (`Components/Pages/Pipelines/`, renamed from Artifacts): `PipelinesBrowser`
  (`/pipelines`, alias `/artifacts`) — cross-project landing summarising each project's latest build
  with a quick `Download all` (latest *successful* build); `PipelineBuilds`
  (`/pipelines/{projectId}`, alias `/artifacts/{projectId}`) — the **New build** primary action
  (cache-backed extension picker with Refresh, a `.confirm-modal` panel), latest-build card with per-`.app`
  download + Download all (outline), the per-repo changelog, build history (failures shown
  honestly), the BUILD LOG card with `Raw log`, project-scoped Compare builds, and the OE deep-link.
- Shared: `RowStateIcon` (the row-state glyph; `BuildStatusPill` was retired in PR 18c once every caller moved onto `.status-pill` or the row keyline) under `Components/Shared/`. `CommitRef` (mono short hash + branch) was sketched here but never used by a page, and has since been deleted.

Each user-facing page states the CLAUDE.md "UX definition of done" and gets a fresh-eyes
`design-review` pass on the rendered screens (light + dark).

## Downloads

Minimal-API `ArtifactEndpoints` (mirroring `GenerationEndpoints` / `EndpointHelpers`:
`ValidateAntiforgeryAsync`, `WriteAttachmentHeaders`, `.RequireAuthorization()`): single `.app`,
a build's Download-all zip, and the raw log. Each re-checks org scope before streaming bytes from
`OeProjectBuildArtifact` / `OeProjectBuildLog`.

## MCP parity

A new `ArtifactsTools` surface over the same services: list projects, list a project's builds
(commit set, status, changelog), fetch/download a build's `.app`s, and compare two builds of the
same project. Per the CLAUDE.md parity rule — agents want the same answers as the web UI.

## Migration

- Backfill `OeProject.CreatedByUserId` (ownerless → an org Admin).
- For each existing `Kind=project` Release, synthesise an `OeProjectBuild` from the
  `oe_import_jobs.project_id → release_id` mapping and migrate `oe_project_build_results`
  provenance into `OeProjectBuildRepoCommit`. Older builds lack retained `.app` bytes, full logs, and
  changelog — surface "captured before logs were kept" rather than fabricating them. The backfill
  ships as the idempotent data migration `BackfillArtifactsData`. `oe_project_build_results` itself
  is **retained**, not dropped — the Object Explorer release-manage page still reads it; the
  migration copies *from* it. Dropping it is a later cleanup once that page is cut over.
- Drop the org PAT columns; users re-enter personal tokens. Seed the org allowed-providers set from
  the providers existing repos already use (default both if none).

## Out of scope (v1)

- **Build/Release retention & pruning.** Every build keeps a full Release + ingest; at thousands of
  builds this grows unbounded. A retention policy (prune old ingests, keep only the `.app` +
  metadata past N builds) is a follow-up.
- **Workspace/Extension cross-links** on the project — improvised prototype UI, excluded.
  (Project visibility, listed here originally, has since shipped — see
  `teams-and-visibility.md`.)
- **Background/auto builds** — removed with `AutoBuildEnabled`; builds are user-initiated only.
- **Branch/tag/commit selection** — unchanged from the OE build path (default branch, HEAD).
