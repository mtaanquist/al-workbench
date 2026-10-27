# Teams and project visibility

Status: **shipped.** Teams exist and are managed (slice 1), project visibility is
modelled and enforced on every project-id surface (slice 2), and the release-id
surfaces, Object Explorer, MCP, and the project detail Access section that turns
the feature on all landed with slice 3. Each section below is labelled with the
slice that landed it.

## Why

Projects and Pipelines are approaching real internal use, which means real customer
data: repository URLs, build logs, compiled `.app` files, and — through Object
Explorer — the customer's source. Today every user in an organisation can read all
of it, and only the project's owner or an org Admin can write. `artifacts.md`
deferred project visibility past v1 on purpose; NDAs and consultants who work with
competing customers are what bring it back.

Two things are needed and they are separable, which is why this doc covers both:

- a way to name a **group of people** — a team — that outlives any one project;
- a way to say **how visible a project is** to people outside that group.

Being on a team confers view and manage rights on the projects that team is
assigned to. On a Public project it confers nothing extra, because everyone already
has both — see "Public is open both ways" below.

### Named users

- **An org Admin setting up a customer engagement.** Creates a team, adds the
  consultants on that account, and later marks the customer's project Private.
  Does not want to be the bottleneck for every membership change afterwards.
- **A consultant who manages their engagement's team.** Adds a colleague who just
  joined the account. Is not an admin and should not need to be.
- **A consultant on a different account.** Should not see the first customer's
  project contents at all — but should not be confused by a project that seems to
  vanish, which is why a Private project still shows its name.

## Data model

### Slice 1 — `teams`, `team_members`

| Entity | Table | Notes |
|---|---|---|
| `Domain/Entities/Team.cs` | `teams` | `Id`, `OrganizationId`, `Name`, `CreatedAt`, `UpdatedAt`. **Hard delete.** |
| `Domain/Entities/TeamMember.cs` | `team_members` | Surrogate `Id`, denormalised `OrganizationId`, `TeamId`, `UserId`, `IsManager`, `CreatedAt`. Unique `(team_id, user_id)`, index on `user_id`. Cascade deletes. |

Both tables carry `organization_id` and are scoped by the standard
`ScopeToOrganization<T>()` query filter in `AppDbContext.OnModelCreating`. The
per-entity configurations install **no** filters of their own, per the house rule.

Three choices worth recording:

- **Teams are hard-deleted.** A team is a name plus a membership list — there is
  nothing to recover. A soft-deleted team would have to be excluded from every
  visibility predicate written from slice 2 onward, and a missed exclusion there
  fails *open*, exposing a project. The blast radius of the alternative is worse
  than the loss.
- **`IsManager` is a boolean, not a role enum.** There is exactly one elevated
  capability on a team. An enum would invite a hierarchy nobody has asked for.
- **`team_members.organization_id` is denormalised.** A membership and its team
  always belong to the same org (the service enforces it on insert), but carrying
  the column means the row is scoped by the same one-line query filter as every
  other tenanted table rather than by a join the filter would have to reach through.

Name uniqueness is per-org and **case-insensitive**, enforced by
`ix_teams_org_name_lower` — a functional unique index on
`(organization_id, lower(name))` written by hand in the `20260904000000_AddTeams`
migration, because EF cannot model one. This mirrors `ix_oe_projects_org_name_active`
and `ix_oe_pipelines_project_name_active`. `TeamService` pre-checks the same rule so
the user gets an inline field error rather than a 500; the index is what makes the
rule true under concurrency.

### Slice 2 — `oe_project_teams` and `visibility`

| Entity | Table | Notes |
|---|---|---|
| `Domain/Entities/ObjectExplorer/ProjectTeam.cs` | `oe_project_teams` | Surrogate `Id`, `OrganizationId`, `ProjectId`, `TeamId`, `CreatedAt`. Unique `(project_id, team_id)`, index on `team_id`. Cascade. |

`oe_projects` gains `visibility text NOT NULL DEFAULT 'Public'`, a new
`ProjectVisibility { Public, ReadOnly, Private }` stored via `HasConversion<string>()`.
Visibility is one level for the whole project regardless of how many teams are
assigned. Migration `20260905000000_AddProjectVisibility`.

## The visibility model (slices 2–3)

| Level | Who can view | Who can manage |
|---|---|---|
| **Public** (default) | Everyone in the org | **Everyone in the org** |
| **Read-only** | Everyone in the org | Owner, org Admin, SiteAdmin, **assigned-team members** |
| **Private** | Owner, org Admin, SiteAdmin, **assigned-team members** | Same set |

### Public is open both ways

The three levels are one ladder: Public is open in both directions, Read-only narrows
*writing* to the assigned teams, Private narrows *reading* to them as well. Each step
down takes something away, and the name of the middle one finally says what it does.

It did not start there. Public originally meant "everyone reads it; the owner and the
admins write it", which made the ladder bend: the level whose whole statement is "this
solution is open to the organisation" was also the level that reserved every write to two
people, and there was no way to say "anyone may look after this" at all. The symptom that
surfaced it was an environment page that showed four of its five tabs only to a solution's
owner — but the same shape was everywhere manage is checked, and widening those reads was
only half the answer.

Two consequences worth stating, because they are what keep this from being a blanket
loosening:

- **Delete is still the carve-out.** Managing a Public solution does not include deleting
  it; that stays with the owner, org Admin and SiteAdmin, exactly as a team grant never
  included it. Ending the thing is a different act from doing the work on it.
- **So is changing the access level itself.** `SetAccessAsync` moved from the manage axis
  to the delete axis in the same change, and the page's Access tab with it
  (`ProjectService.CanChangeAccessAsync`). Leaving governance on manage would have meant
  that anybody who can manage a Public solution could re-govern it - lock a shared
  solution to a team of their own, or open a narrowed one back up - which is the one
  grant a level must never hand to the people it governs. This also tightens Read-only
  and Private, where an assigned team could previously change the level; nothing relied
  on that, and "a team grant is about doing the work" covers this as squarely as it
  covers delete.
- **Environment updates do not follow.** `CanManageEnvironmentUpdatesAsync` is a separate
  axis and still needs `ManagesUpdates` on a team *assigned to the project*. A Public
  project has no teams by the invariant below, so scheduling a customer's platform update
  remains admin-only there — which is the practical reason a real customer engagement
  belongs on Read-only with its caretaker teams, not on Public.

**What manage actually grants is wide**, and it is worth reading the list before leaving a
customer solution on the default: the Business Central connection (tenant id, client id
and the client secret), publishing builds to a customer environment, uploading and
installing apps, copying and recovering environments, ending sessions, pipelines and
repositories. `/solutions` carries only `[Authorize]`, with no role requirement, so
"everyone in the org" includes the `User` role. Public is the right level for a solution
the whole company genuinely shares; it is not the right level for a customer under NDA,
and it is still the level a new solution is created at.

**Why not a "caretaker team" on a Public project instead.** Because a team on a Public
project would confer manage — `CanManageAsync` grants it to any assigned team member —
and Public-plus-teams would then be indistinguishable from Read-only. The alternative, a
team that labels without granting, would make every manage check read the project's
visibility to decide whether the team counts, which is a fail-open shape on the hottest
authorization path. Read-only *is* the caretaker level; it wanted a better name, not a
new mechanism.

- **Delete is carved out.** Assigned-team members get everything management covers
  *except* deleting the project — soft-delete stays with the owner, org Admin, and
  SiteAdmin. A team grant is about doing the work, not about ending it.
- **Reading is the view axis, even when the read costs something.** A read that spends
  the customer's Business Central credentials — what is installed on an environment,
  what Business Central has been doing to it, who is signed in right now — is still a
  read, so it is gated on view and not on manage. Gating those on manage looks careful
  and isn't: a Public project has no teams by construction, so it hides from the whole
  organisation exactly the data the org was told it could see, while a Private project's
  teams are already the view answer. `ProjectConnectionService`'s
  `EnvironmentGate.View` is that rule; what *acts* on the customer's tenant stays on
  manage. See `environment-updates.md`, "The environment's own page".
- **Org Admins and SiteAdmins bypass visibility entirely.** This cannot live in an
  EF query filter (the membership set is not a scalar and the bypass is not a model
  fact), which is why enforcement is explicit predicates rather than a second global
  filter.
- **A Private project still shows its name** in `/solutions`, as a greyed, locked,
  non-clickable row with the caption "Private — visible to its team". Everything
  else about the row — owner, status, counts — leaks activity and is omitted. MCP's
  `list_solutions` omits Private solutions entirely; a locked name is no use to an agent.

### The invariant

> `Visibility != Public` **⇔** at least one team is assigned.

Enforced atomically by a single `ProjectService.SetAccessAsync(projectId, visibility,
teamIds)` — never by two independent writes that could interleave into a Private
project nobody can see.

**The level is chosen on the create form, not corrected afterwards.**
`CreateProjectAsync` takes an optional `ProjectAccessSettings` and writes it in the same
`SaveChanges` as the solution, its repositories and its teams; both it and
`SetAccessAsync` run the invariant through one `ValidateAccessAsync`, so the two cannot
drift. This matters more since Public became open both ways: a create that landed Public
and was narrowed a moment later would put a customer's Business Central connection in
front of the whole organisation for that moment, and a create-then-set pair can
half-succeed and leave it there for good. Defaulting new solutions to Read-only instead
is not available — the invariant needs a team, and a fresh organisation has none — which
is the other reason the question belongs on the form, where the team is picked in the
same breath. The Access tab renders on `/solutions/new` with no save of its own; the
page's Create solution writes the answer with the rest. A caller that says nothing about
the level (the GitHub import paths) still gets Public with no teams.

Its consequence for teams: **deleting a team is refused while it is the last team on
any non-Public project**, listing the projects that block it. The refusal is
deliberate rather than auto-resetting those projects to Public — silently making a
Private project world-readable is exactly the failure this feature exists to prevent.
That refusal lives in `TeamService.DeleteTeamAsync`.

## Authorisation

### Slice 1 — `TeamService`

Two levels, deliberately different:

| Capability | Who |
|---|---|
| Create a team, delete a team | Org Admin, SiteAdmin |
| Rename, add / remove members, promote or demote a manager | The above, plus a member of that team with `IsManager` |

- `CanManageTeamAsync(teamId)` / `EnsureCanManageTeamAsync(teamId)` resolve in that
  order: SiteAdmin → org Admin → `IsManager` member of *that* team. Modelled on
  `Services/ObjectExplorer/ProjectAccess.cs`.
- A caller with no signed-in user (a background worker under an ambient org scope)
  gets `false`, never an exception — the check is safe to call from a render path.
- **Zero managers on a team is a valid state.** Org Admins can always manage any
  team, so there is nothing to recover from, and a last-manager guard would strand
  the final manager on a team they wanted to leave. `/teams/{id}` shows a quiet hint
  in that state rather than an error.
- Every mutation re-checks authorisation inside the service. Hiding a button is a
  courtesy to the user, not the gate.

### Slice 2 — `ProjectAccess` grows a second axis (shipped)

`ProjectAccess` stays the single authority for project authorisation and gains:

- `AccessSnapshot(UserId, IsSiteAdmin, IsOrgAdmin, TeamIds)`, loaded lazily once per
  DI scope, with `BypassesVisibility => IsSiteAdmin || IsOrgAdmin`. A null user means
  "no grants", never a throw. Snapshot staleness across a long-lived Blazor circuit
  is accepted — it matches the circuit-scoped `AppDbContext` consistency model, and a
  page reload picks up membership changes.
- **Manage axis**: `CanManageAsync(projectId, createdByUserId)` — SiteAdmin → owner →
  org Admin → member of any assigned team.
- **Delete axis**: `EnsureCanDeleteAsync` keeps today's stricter rule.
- **View axis**: `CanViewAsync(projectId)` / `EnsureCanViewAsync(projectId)`, throwing
  `ProjectAccessDeniedException`.
- `VisibleProjectPredicate(snapshot)` for list queries, skipped when the snapshot
  bypasses.
- `LockedProjectPredicate(snapshot)` — the complement of the above, written out
  longhand rather than negated at the call site, so `/solutions` can pull the
  name-only rows in one query and a reader can check the two halves against each
  other.
### Slice 3 — the release axis (shipped)

`IsReleaseVisibleAsync(releaseId)` / `VisibleReleasePredicate(snapshot)`. A
`OeRelease` has no `ProjectId`, so a release is hidden iff it is linked — via
`oe_project_builds.release_id` or `oe_import_jobs.project_id` — to a Private
project the caller cannot view. Unlinked releases stay governed by the org filter
alone. Both shapes say the same thing as `LockedProjectPredicate`, reached through
those two links; a change to one belongs in the other.

`VisibleReleasePredicate` is an instance method rather than a static one, because
its subquery has to reach `oe_import_jobs` and no navigation property on `OeRelease`
exposes it. `IsReleaseVisibleAsync` caches its answers for the DI scope, for the
same reason the snapshot is cached: the source viewer asks the same question four
or five times while rendering one page.

**Every gate answers "nothing here", never "refused."** A denied read returns the
same null / empty / 404 an id from another organisation gets. A distinct refusal
would confirm the project exists, which is the thing the locked row in `/solutions`
already discloses exactly as much of as it should.

### The environment-update grant (`team_members.manages_updates`)

A per-membership boolean, added for the Upgrades fleet page
([#657](https://github.com/mtaanquist/ALDevToolbox/issues/657)). It grants exactly
one thing: running Business Central platform-update actions — scheduling and
re-scheduling an environment's update date — on the projects the team is assigned
to.

- **Teams-scoped, not role-scoped.** You may act on a project iff you hold the flag
  in one of *that project's* assigned teams. Org Admin and SiteAdmin may act
  everywhere. A Public project with no teams therefore has no non-admin operators at
  all, which is the intended shape: the fleet page's grant follows the same team
  assignments that already decide who sees the customer.
- **Deliberately not a fourth `UserRole`.** A new role would ripple through the
  hard-coded role lists, label switches, invite pages and MCP checks in ~30 files and
  would still not compose with per-project visibility. The flag touches almost none
  of that and composes for free.
- **A different axis from every existing one.** Managing the team does not grant it,
  managing the project does not grant it, and *owning* the project does not grant it
  either — these actions land on the customer's production tenant, which is a
  narrower thing to hand out than the project itself. Holding the flag grants nothing
  else in return.
- **Never enters claims.** It is not in the cookie, the PAT handler, or the Entra
  claims transformer; the nav, the page, and every write resolve it through
  `ProjectAccess` against the database. That keeps a grant change effective on the
  next page load rather than the next sign-in, and keeps the claims pipeline out of
  the blast radius.
- Granted from the team roster (`/teams/{id}`) by whoever passes
  `CanManageTeamAsync` — the same gate as the manager toggle. Unlike that toggle, a
  membership row that isn't there is a validation error rather than a silent no-op: a
  grant aimed at the wrong row should not look like it landed.
- Audited with the rest of the membership row; `team_members` has no column gate.

`ProjectAccess` carries it: the snapshot gains `UpdateOpsTeamIds` (the teams where
*this* user's membership holds the flag, read from the same membership query the
existing `TeamIds` comes from) and `CanUseEnvironmentOps`, which gates the page and
its sidebar entry without naming a project. Per-project answers come from
`CanManageEnvironmentUpdatesAsync` / `EnsureCanManageEnvironmentUpdatesAsync`, and
list queries from `UpdateOpsProjectPredicate(snapshot)` — composed *alongside*
`VisibleProjectPredicate`, never instead of it. The two answer different questions
and a page needs both: what you may see, and what you may act on.

## Gated-surface inventory (slices 2–3)

Every read surface that takes a project id or a release id must opt in. A new
surface added later is **not** covered by default — add it here and gate it.

All of the project-id surfaces below are gated as of slice 2; the release-id and
MCP sections are slice 3.

Two list surfaces answer the same question differently, on purpose.
`ArtifactService.ListProjectsAsync` is what `/solutions` renders, so it returns a
**locked name-only row** for a project the caller cannot open.
`ProjectService.ListProjectsAsync` feeds the project *pickers* (new pipeline, new
release pipeline) and simply **omits** those projects — a name you cannot act on is
only in the way of choosing one you can.

**Project-id surfaces** (`EnsureCanViewAsync` at the top, or `VisibleProjectPredicate`
in the list query): `ArtifactService` (`ListProjectsAsync`, `GetProjectHeaderAsync`,
`ListPipelinesAsync`, `GetPipelineHeaderAsync`, `ListBuildsAsync`,
`ListBuildsForProjectAsync`, `GetBuildDetailAsync`, artifact bytes and log getters,
`GetProjectIdForReleaseAsync`, `ListComparableBuildsAsync`); `ProjectService`
(`ListProjectsAsync`, `GetProjectAsync`, `ListProjectReleasesAsync`,
`ListSupplementalSymbolsAsync`); `PipelineService` and `ReleasePipelineService` list
and get reads; `DeliveryService` (`ListDeliveriesAsync`,
`ListDeliveryHistoryAsync`); `Bc/ProjectConnectionService` (`GetConnectionAsync`,
`ListEnvironmentsAsync`); `ProjectDiscoveryService.GetDiscoveryAsync`;
`Endpoints/ArtifactEndpoints.cs` (gate before streaming bytes — slice 3; the
`ArtifactService` byte getters those endpoints call are already gated).

A pipeline, a release pipeline, a build, and a delivery have no visibility of their
own: each gate resolves the owning project and defers to the one authority. A row
that does not exist passes the gate — the read below returns nothing on its own, and
refusing would confirm an id that is not in this org.

**Release-id surfaces** (via `IsReleaseVisibleAsync` / `VisibleReleasePredicate`),
all gated as of slice 3:

- `ObjectSearchService` and `ReferenceQueryService` — their existing
  `ReleaseVisibleAsync` hooks now check both fences, tenant *and* project, which
  covers object/procedure/content search, find-references, find-system-references,
  file dependencies, interface implementers, and `ReferenceSessionService` (whose
  four `Create*` entry points additionally refuse to mint a session on a hidden
  seed — the session's label carries the object's name, which is itself a fact
  about the project).
- `ObjectExplorerService` — `GetReleaseAsync`, `ListModulesAsync`,
  `ListModuleSummariesAsync`, `ListObjectsAsync`, `GetObjectAsync`,
  `GetObjectByNameAsync`, `GetObjectOutlineAsync`, `GetProcedureSourceAsync`,
  `ListProcedureCallsAsync`, the two `GetProjectBuildResults*` reads, and
  `ListLatestPipelineBuildReleasesAsync` (the Releases browser's one deliberate
  leak of a project build). `ListReleasesAsync` needs the predicate only with
  `includeProjectBuilds: true` — it excludes project releases otherwise.
- `SourceViewerService` and `ExplorerTreeService` — every file-, module-, and
  symbol-keyed entry point, resolving the owning release first through the
  `SourceVisibility` fence they share.
- `ReleaseComparisonService` — **both** sides of a comparison, plus the
  "compare with release" picker.
- `Endpoints/ObjectExplorerViewerEndpoints.cs` — the JSON endpoints inherit the
  service gates; the SymbolReference stream reads `oe_modules` directly and so
  carries its own check.
- `Endpoints/ArtifactEndpoints.cs` — `ArtifactService`'s byte getters were already
  gated in slice 2, so each handler catches `ProjectAccessDeniedException` and
  answers its existing 404 rather than letting a 500 escape.

**Release-id writes** (#1430). Every authoring write that takes a release id checks
`IsReleaseVisibleAsync`, and asks it before the existence read so a hidden id and an
unknown one run the same queries:

- `ReleaseManagementService` — `SoftDeleteAsync`, `RestoreAsync`,
  `UpdateMetadataAsync`, `HardDeleteAsync`, and `EnsureBackfillableAsync` (the
  pre-check of the one-release system-reference backfill, which answers 404).
- `ReleaseImportService.AmendReleaseAsync` — the module upload onto an existing
  release.
- `TranslationImportService` — `ImportSingleAsync` and `ImportZipAsync`, the two
  translation uploads.
- `ReleaseImportRequestService.RetryAsync` — the manage page's Retry. A hidden
  release is treated as unknown: its saved source and its release row are read
  and then discarded, so nothing is reused and the reopen answers not found. A
  retry of a pipeline build is additionally held to the solution manage check, as
  pressing Build is.
- `ReleaseImportRequestService.FindSymbolRecoveryProjectAsync` — the pre-check of
  manual-symbols recovery, which answers a hidden release with the same "isn't a
  solution build" refusal as an unknown id, before the solution manage check the
  rebuild itself holds to.
- `ReleaseManagementService.ListBackfillableReleaseIdsAsync` — the bulk "backfill
  every release" action queues only the releases `VisibleReleasePredicate` lets the
  caller see, so the count it reports leaves the hidden ones out too.

Admin and Editor are the roles that reach them, so the rule is the read rule, not a
stricter one: an org Admin and a SiteAdmin bypass it, the owner and the assigned
teams' members pass, and anyone else gets exactly what an unknown id gets (the same
field error word for word, or the same 404), so the refusal confirms nothing. The
import services and `ReleaseImportRequestService` take `ProjectAccess` as a required
constructor argument, so nothing builds one without the fence.

Not gated here: the importer's own soft-delete (`SupersedeAsync`, which
`ArtifactReleaseImporter` uses to replace or retire a Microsoft artifact release),
under the worker rule below, since no project owns such a release and the sweep runs
with nobody signed in.

**MCP** (`Services/Mcp/Tools/`, per the parity guide in `PROJECT.md`), gated as of
slice 3. Each tool class has exactly one id-resolution choke point, which is where
its gate lives:

- `ArtifactsTools` — `ResolveProjectAsync` and `ResolveReadyBuildAsync` query
  `oe_projects` directly, so the predicate goes inside them and all six tools
  inherit it. `list_projects` drops the locked rows entirely: a name an agent
  cannot act on is only noise. The tools that call a slice-2-gated service
  (`list_pipeline_builds`, `get_project_build`) translate
  `ProjectAccessDeniedException` into the same "not found in this organisation"
  message an unknown id gets.
- `DeliveryTools` — the same predicate on `EnsureReleasePipelineExistsAsync` and on
  `list_release_pipelines`. `publish_build` was already gated by `DeliveryService`.
- `ObjectExplorerTools` — `ResolveReleaseAsync` is the single choke point for all
  fourteen release-keyed tools. The one bypass is `get_procedure_source` /
  `list_procedure_calls` called with an explicit `symbolId`, which skips release
  resolution altogether; those check the symbol's own release instead.

**Not gated**: background worker internals (queues and workers must not be starved
by a view gate), `/site-admin/*`, and Public / Read-only reads.

## Audit

`AuditEntityType.Team` and `AuditEntityType.TeamMember` (slice 1), plus
`AuditEntityType.ProjectTeam` and a `Visibility` entry in the `OeProject` column
gate (slice 2). An `OeProjectTeam` row stays unnamed like every other join row; the
snapshot carries the project and team ids.

Team and membership changes are audited in full rather than behind a column gate:
who could see what, and when that changed, is the point of the feature, and there is
little to gate anyway — a team has only a name, a membership row only its manager
flag. `Team.Name` resolves through the existing `AuditEntityName` candidate list, so
the log reads "changed the team Nordics". A `TeamMember` row stays unnamed, like
every other join row; its snapshot carries the team and user ids.

## UI

### Slice 1

- **Administration → Teams** (`/admin/administration/teams`, Admin only). Team list
  with member and manager counts, "Create team" as the page's one primary action,
  delete behind `ConfirmDialog`. Empty state: "No teams yet. Teams let you limit who
  can see and change a customer's project." The team name links to the shared detail
  page.
- **`/teams`** (any signed-in user). The teams *you* are on. Admins wanting the full
  list have the Administration tab; showing everyone every team here would bury the
  one thing the page is for. Empty state: "You're not on any team yet. Ask an admin
  to add you."
- **`/teams/{id}`** (any signed-in user). Roster visible to everyone in the org —
  membership is not a secret inside an organisation, and knowing who to ask is the
  point. Rename, an explicit "+ Add member" button opening a picker of org users not
  yet on the team, Remove, and a Manager toggle render only when
  `CanManageTeamAsync`. Not a ghost-row grid — see the `AdminProjectDetail`
  cautionary tale in `CLAUDE.md`. With no manager, a quiet hint: "No manager yet —
  admins manage this team's members."
- No sidebar entry for `/teams` in slice 1; it is reachable from Administration and
  from links on team names.

### Slices 2–3

- **Project detail gains an Access tab** (slice 3; visible only when the viewer can
  manage the project): the three visibility levels as radio cards with
  plain-language captions, a checkbox list of the org's teams, and one outline
  "Save access" button. Inline errors keyed `Visibility` / `Teams`, in their own
  dictionary so they render next to the control rather than in the page-wide alert.
  Choosing Public clears the team picks rather than greying them while still set —
  the service refuses Public-with-teams, and a disabled control holding values is a
  trap on the next save. With no teams in the org, an Admin gets a "Create a team"
  button and everyone else is told to ask one.
  It is a **tab** rather than a block under General because it saves on its own
  button, and the page hides its header primary on any tab that owns its actions.
  This is the switch that turns the feature on, which is why it shipped **last** —
  after OE and MCP enforcement. Shipping the Private toggle before those would let
  a user believe a project is hidden while `search_objects` still read it.
- **`/projects` renders locked rows** for Private projects the viewer cannot see:
  name only, greyed, a `lock` icon, a plain `<span>` rather than a link. Direct
  navigation to a locked project maps `ProjectAccessDeniedException` onto
  `ProjectDetail`'s not-found state — don't confirm more than the list already shows.

## Out of scope

- **Per-user grants on a project.** Teams are the unit. A one-person team is the
  answer to the case that wants this, and it stays greppable.
- **Nested teams / team hierarchies.** No evidence anyone wants them, and they make
  every visibility predicate recursive.
- **Cross-organisation teams.** Both tables are org-scoped; a person belongs to one
  org, so a team cannot span two.
- **Per-pipeline or per-build visibility.** Visibility is a property of the project;
  everything beneath it inherits.
