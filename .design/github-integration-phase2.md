# GitHub integration, phase 2

Milestone "GitHub integration, phase 2" (issues #626-#633). Builds on the App, the
per-organisation connection, the per-user link and the shared repository picker that
`github-integration.md` describes; read that document first, because every rule in it
still holds here - the credential split, the single resolver
(`GitHubRepositoryService.ResolveAsync`), "404 is never gone", "an answer we could not
get is a refusal", the field-keyed `PlanValidationException` refusals.

This document is the spec for the eight issues. Where it disagrees with an issue body,
this document wins and the deviation is called out. Each section carries a **Decisions**
list settled before the code was written and an **As built** list appended by the
implementer.

## What phase 2 changes about the phase 1 fences

Phase 1 kept two fences that phase 2 relaxes deliberately, both inside the sanctioned
shapes `CLAUDE.md` already names:

- **"No queue - every GitHub call runs on the request thread."** Three features here
  are background work by nature: repository discovery (#629), translation-memory ingest
  (#631) and the pull-request compile gate (#627). They run on the existing in-process
  bases (`PolledScheduler`, `JobQueue`/`QueueDrainWorker`) - no broker, no external
  queue. Everything a *person* triggers still runs on the request thread behind a
  loading state.
- **"Webhooks are out of scope - nothing needs GitHub to call us."** #627 is the first
  inbound call. It is one anonymous `POST`, verified by HMAC over a deployment-wide
  secret, that only ever enqueues.

Two things phase 2 does **not** do without the maintainer's explicit yes, and the
implementations are written so they do not need to:

- **No new `IgnoreQueryFilters()` call site.** Every scheduler enumerates
  `organizations` (a table with no tenant filter) and enters an `AmbientOrganizationScope`
  per org before reading that org's settings, the way `EnvironmentRefreshScheduler`
  does. The webhook worker resolves an `installation_id` back to an organisation the
  same way. A single cross-org lookup would be cheaper; it is a fence crossing and is
  listed as an ask, not done.
- **One new key-ring-encrypted secret**, the webhook secret (#627), on
  `system_settings`, redacted by `AuditInterceptor` like the App's private key. The issue
  asked for exactly this; the PR body says so.

## Shared plumbing

- **`GitHubAppClient` is `partial`.** Each feature adds its REST calls in its own file
  (`GitHubAppClient.Releases.cs`, `GitHubAppClient.Checks.cs`, `GitHubAppClient.Rulesets.cs`,
  `GitHubAppClient.Blobs.cs`) so parallel work does not fight over one 1,200-line file.
  Same conventions: credential first, `JsonDocument` reads, anonymous-object writes,
  `GitHubApiException` on refusal, a `CancellationToken` on every method.
- **`app.json` parsing moves out of `ProjectBuildService`.** `ParseManifest`,
  `AppJsonManifest`, `AppJsonDependency` and the test-folder exclusion rules become a
  static `AppJsonManifestParser` (`Services/ObjectExplorer/Import/AppJsonManifestParser.cs`);
  the build service delegates. #629 lifts it; #630 extends `AppJsonDependency` with the
  dependency `version` the current parser drops.
- **Translation-file rules move out of `GitHubTranslationService`.** "A `.xlf` whose
  parent folder is `Translations`, at any depth" and the language/source detection
  become a static `TranslationFileRules`; the Translator page and the ingest (#631) both
  use it.
- **Which GitHub permissions the App needs grows.** Phase 1 needed `administration`,
  `contents`, `metadata`, `pull_requests`, `members`. Phase 2 adds `checks: write`
  (#627) and uses `contents: write` for Releases (#632) and `administration: write` for
  rulesets (#628). `GitHubPermissionLabels` describes the new ones; the SiteAdmin
  walkthrough names them.

## #629 Discover AL repositories across the connected organisation

Named user: a BC consultant who has just connected their GitHub organisation and wants
to see which of its repositories the workbench does not know about yet.

**Decisions**

- The existing "project discovery" (`ProjectDiscoveryQueue`, `oe_projects.discovered_*`)
  is *extension* discovery inside one solution and keeps its name. This feature is
  **repository discovery**: `RepositoryDiscoveryService`, `RepositoryDiscoveryScheduler`,
  table `github_repository_candidates`.
- A candidate row is per org: `full_name`, `html_url`, `clone_url`, `default_branch`,
  `app_name`, `app_id`, `app_json_path`, `discovered_at`, `last_seen_at`, `ignored_at`,
  `ignored_by_user_id`. A repository that stops matching (app.json removed, repository
  gone) is removed on the next sweep; an ignored one stays ignored until it is tracked.
- **The probe is one recursive tree read per repository** (`ListTreeAsync` at the
  default branch), looking for `app.json` at the root or exactly one folder down,
  skipping test folders by the same rules the build uses. The manifest is then read
  once for its name and id. No clone.
- **The sweep runs daily** (`RepositoryDiscoveryScheduler : PolledScheduler`, fixed hour,
  `DISABLE_GITHUB_REPOSITORY_DISCOVERY_SCHEDULER=1` opt-out) and **on demand** from the
  Solutions page ("Check GitHub now"), on the request thread behind a loading state.
  Both use the installation token: listing the organisation's repositories is an act of
  the organisation.
- **The panel narrows to the viewer.** "N AL repositories in {org} are not tracked yet"
  is computed *after* `GitHubAccessService.FilterAccessibleAsync` for the person
  looking, so a repository they cannot open is neither counted nor listed. Narrowing is
  per user by construction, so it happens at render time in the panel's own service
  scope, not in the sweep.
- **"Already tracked" is decided against every solution in the org**, not just the ones
  the viewer can see - otherwise a repository a Private solution already tracks would be
  offered as untracked and leak that solution's existence. The subtraction runs over
  `oe_project_repositories` under the ordinary tenant filter, matched on the normalised
  clone URL; the display list is what the GitHub check narrows.
- **Track as solution** is a compact inline form, not a bare click: the solution name
  (from `app.json`) and the artifact country (from the organisation's auto-import
  country when set) are pre-filled, and the person confirms. `ProjectService.CreateProjectAsync`
  requires a country, so a silent default would be a guess made for them. **Ignore**
  stamps `ignored_at` and the row disappears from the panel.
- The panel is an interactive child component above the list on `/solutions`
  (`ProjectsBrowser` stays static SSR). It renders nothing at all unless the
  organisation allows GitHub repositories, has connected one, and the viewer has linked
  their account - the same rule as #624's picker.
- No MCP tool: discovery proposes and a person decides; agents list repositories through
  #633's `list_repositories`.

**As built**

- **The table is `github_repository_candidates`**, with the columns the decisions
  name, a unique `(organization_id, full_name)` the sweep upserts on, and the
  ordinary tenant query filter. Migration `20260918000000_AddGitHubRepositoryCandidates`.
  No `IgnoreQueryFilters()` call was added anywhere on this feature's path.
- **`AppJsonManifestParser` is the lift the shared-plumbing section called for.**
  `AppJsonManifest`, `AppJsonDependency`, `ParseManifest` and the test-folder rules
  moved there unchanged; `ProjectBuildService` keeps `IsTestSegment` and
  `ParseManifest` as one-line forwarders so the build's own call sites and tests
  did not move. The excluded-folder list (`.alpackages`, `.git`, ...) went with
  them, because the probe needs exactly the same answer the walk does.
- **The probe reads the tree once and at most one manifest.** Root first, then
  one-folder-down paths in a stable order, so two sweeps over an unchanged
  repository settle on the same manifest; the first path that parses wins and the
  rest are not fetched. A tree GitHub cropped is used as far as it goes, with a
  warning - the alternative is a call per folder, which is what the recursive read
  exists to avoid. A repository whose probe throws is logged and skipped: one
  unreadable repository must not cost the organisation its sweep.
- **A vanished candidate is deleted even when it was ignored.** The finding is
  what the row records, so when the repository stops matching there is nothing
  left for the decision to apply to. An ignored row whose repository is still
  found keeps its `ignored_at`, and tracking one deletes the row outright.
- **"Already tracked" ignores solutions in the recycle bin.** The subtraction is
  over `oe_project_repositories` joined to non-deleted solutions, matched on the
  clone URL with the `.git` suffix and trailing slash stripped - the same
  normalisation `ProjectDetail` uses when it refuses a duplicate pick.
- **The panel loads its list after its first render, not during it.** Narrowing
  costs one call to GitHub per candidate, so "Checking GitHub..." is only honest
  if it is on screen while that happens; readiness itself is two cached database
  reads, so an unreachable GitHub cannot hold up the Solutions page behind it.
  Every read runs in the component's own service scope, as `RepositoryPicker`
  does.
- **The panel renders nothing until it can help**, and that includes while it is
  still working out whether it can: the organisation must allow GitHub, have
  connected one, and the viewer must be linked. Failing that, the Solutions page
  is exactly what it was.
- **`RepositoryDiscoveryScheduler` sweeps at 04:00 UTC**, an hour after the
  environment refresh so the two do not share a peak, polling every five minutes
  on `PolledScheduler` with `DISABLE_GITHUB_REPOSITORY_DISCOVERY_SCHEDULER=1` as
  the opt-out. It enumerates `organizations` and enters an
  `AmbientOrganizationScope` per organisation, exactly as
  `EnvironmentRefreshScheduler` does; an organisation that fails is logged and the
  sweep carries on.
- **No MCP tool**, as decided.

## #628 Repository standards when the workbench creates a repository

Named user: an org Admin who wants every repository the workbench creates to look like the
ones their team already maintains by hand.

**Decisions**

- **Per-organisation, two parts.** A **branch ruleset** for the default branch, stored as
  one JSON column `organization_settings.github_repository_ruleset_json`
  (`GitHubRepositoryRuleset`: require a pull request, required approvals, require linear
  history, required status checks, block force pushes), and a **set of files** added to
  every new repository, stored in a new table `github_repository_standard_files`
  (`path`, `content`, `ordering`, `updated_at`; unique `(organization_id, path)`). The
  files are plain text, not mustache: they are the same in every repository by
  definition.
- **Not `organization_files`.** That table's rows are template opt-ins the generator
  emits; standards are applied to a repository regardless of template and never appear
  in a ZIP. A third `OrganizationFileScope` would have muddled the "Always-included
  files" page for both audiences.
- **Applied in the same commit as the workspace**, on the installation token, then the
  ruleset via `POST /repos/{owner}/{repo}/rulesets`. They arrived as a second commit of
  their own until issue #811 folded them into the workspace commit, whose tree is built
  from nothing and so has to describe the whole repository; the ordering that matters
  (files before ruleset) is unchanged. A standards file at a path the generator
  also produced replaces it - the organisation's standard wins over the template.
- **A ruleset refusal is a warning, not a failure.** By then the repository exists and is
  committed, so the success card says so and names what GitHub refused (typically the
  missing `administration: write` grant), rather than leaving a repository behind with
  a stack trace over it. Files are committed before the ruleset is attempted, so a
  ruleset that requires a pull request never blocks the standards commit.
- **Admin surface:** a "Repository standards" row on Administration -> Repositories,
  in the connected block after "What the workbench may do there", linking to a dedicated
  editor page (`/admin/administration/repositories/standards`) modelled on
  `AdminTemplateFiles`. The row summarises what is configured ("3 files and a branch
  ruleset").
- **New Workspace shows one line**, not options: "Will be created with your
  organisation's repository standards" in the create-repository card's caption, only
  when anything is configured. The MCP `generate_workspace` result gains the same fact.
- Retro-fitting existing repositories stays out of scope, as the issue says.

**As built**

`GitHubWorkspaceRepositoryService.CreateAsync` reads
`GitHubRepositoryStandardsService.GetAsync` once, before it writes anything, and hands the
files to the fill and the ruleset to the step after it - so an organisation that never
opens the page pays one query and no extra call to GitHub. The result record grew
`StandardsFileCount` and `StandardsWarning`, and the MCP `RepositoryCreationResult` grew
the same two, both defaulted so no existing caller had to change.

Standards are laid over the generated files by path before anything is uploaded, so an
override is one blob and one tree entry rather than a second commit replacing a file the
first one just wrote. The one-file shortcut this used to have to dodge is gone with the
second commit; a workspace of a single file would take the same route as any other.

A ruleset is only posted when it asks GitHub for something. `GitHubRepositoryRuleset.IsEmpty`
is the guard, and it is why "3 approvals" with "require a pull request" switched off counts
as nothing configured everywhere: the summary sentence, the New Workspace caption and the
call itself. A ruleset named after the workbench that enforces nothing would be worse than
no ruleset.

`GitHubApiException` from the ruleset call is caught, logged at Warning and returned as a
sentence; everything else on the path still throws. The failure that matters in practice is
the missing `administration: write` grant, and the sentence says what to ask for without
naming the permission, the way the create-repository refusal already does.

`CreateRepositoryRulesetAsync` lives in `GitHubAppClient.Rulesets.cs` and sends the full
`pull_request` parameter object (the four booleans as well as the approval count) and
`strict_required_status_checks_policy` alongside the check contexts, because GitHub rejects
a partial parameter object on those two rules. The ruleset carries a fixed name,
`AL Workbench repository standards`, so somebody reading a repository's settings can see
which rules were not written by hand. **These request shapes are from the documented API and
have not been exercised against api.github.com from this environment** - the same caveat
phase 1's tests carry.

Storage split the way the decisions above set out: the ruleset as one nullable jsonb column
with a value converter (null means "not configured", which stays distinguishable from an
empty one), the files as `github_repository_standard_files` under the ordinary tenant query
filter. No `IgnoreQueryFilters()` call was added; every read names the acting organisation
id. Saving the ruleset invalidates `OrganizationConfigService`'s settings cache, since the
column now rides on the row that cache holds.

`github_repository_standard_files` is listed in `TenantTableCatalog.ContentTables`, which is
what makes it part of a per-tenant backup and of an organisation's prorated disk usage.
`TenantTableCatalogTests` catches a new tenanted table that is not - a standards file that
survived a per-tenant restore only by accident would be a quiet data-loss bug.

The editor page is Admin-only and its Save is an outline button: the page has no primary
action, because Generate owns that everywhere in this app. Path validation is the same rule
the always-included files use, which already admits `.github/workflows/build.yml` and
`CODEOWNERS`. The list is reconciled by primary key, so renaming a file keeps its row rather
than deleting and re-adding it.

New Workspace says one line and offers no choice, as decided. It is rendered from the same
summary the Repositories row uses, read in `OnInitializedAsync` alongside the GitHub
readiness - one more database read, no call to GitHub, so the card still renders while
GitHub is down. The success card names the second commit and shows the ruleset warning as a
warning beside the success rather than instead of it.

## #626 Cookbook: apply a recipe to a repository, and update every repository that took it

Named user: a consultant on a customer project who found the right recipe and wants it
in the customer's repository as a reviewable change, not a ZIP on their desktop.

**Decisions**

- **One operation, two callers.** `GitHubRecipeDeliveryService.ApplyAsync(recipeId,
  repoFullName, customerName?)` commits the recipe's files at their `RelativePath` onto
  `aldt/recipe-<slug>` **with the user's token** and opens a pull request into the default
  branch. The download modal calls it once; the admin "Open update pull requests" calls
  it once per repository that took the recipe. The pull request body says which it is.
- **Branch reuse, then stepping.** If `aldt/recipe-<slug>` has an open pull request, the
  commit is added to it (find-before-create, as the Translator does). If the branch
  exists with no open pull request (merged or closed), the name is stepped (`-2`, `-3`)
  as extension delivery does, so history is never rewritten.
- **Files are written over, never merged.** A repository that has since diverged in the
  recipe's files gets the pull request anyway; the diff shows the conflict. No three-way
  merge (the issue says so).
- **Attribution is a place.** `recipe_downloads` gains `repository` (the full name) and
  the `Source` enum gains `Repository`; `project_id` is still resolved from the customer
  name when one is given. "Where this recipe has been used" on the admin page shows the
  repository, and the distinct repositories are what "Open update pull requests" iterates.
- **The update button appears only when it can act**: the recipe has been applied to at
  least one repository and the Admin looking has a working GitHub link. It opens one
  pull request per repository, as the Admin, and reports per repository - one refusal
  does not stop the others.
- **`apply_recipe`** is the MCP twin, in `CookbookTools`, taking a recipe id, a
  repository as `owner/name` and an optional customer name, returning the pull request
  (`RepositoryDeliveryResult`). It routes through the same service, so the resolver gate
  is inherited. `.design/cookbook.md`'s "downloads are a web-UI flow only" sentence is
  corrected.

**As built**

- `GitHubRecipeDeliveryService.ApplyAsync(recipeId, repoFullName, customerName?)`
  returns `GitHubRecipeDelivery(Repository, PullRequest, IsNewPullRequest, FileCount)`.
  It needed no new REST call: every primitive the commit uses - branch head, commit
  tree, blob, tree, commit, create/update ref, find/create pull request - was already
  on `GitHubAppClient` from phase 1, so this feature adds no
  `GitHubAppClient.<Feature>.cs` file and leaves that class as it found it.
- **Branch choice is one read-only walk, not two rules.** `aldt/recipe-<slug>`,
  `-2`, `-3`... are tried in order and each is asked two questions: is a pull request
  open on it (join that one, and commit on the branch's own head), and does the branch
  exist at all (if not, cut it from the default branch head). The first name to answer
  either is the target. That is a superset of the spec's "reuse the base name, else
  step it", and it covers the case the literal reading gets wrong: once the base
  name's pull request is merged, a third apply would open a *third* pull request
  beside the still-open `-2` rather than joining it. Ten names is the cap, as
  extension delivery uses.
- **The tree's base is the target's own tree**, not always the default branch's -
  otherwise the second commit on a reused branch would silently revert the first.
  Files are still written over rather than merged, as the issue asks.
- **"Which it is" is on the commit message, not the pull-request body.** A body is
  only ever written when the pull request is opened, so by the time a second commit
  joins one the words are already there and could not be corrected; the body says
  what the pull request brings, and the commit says "Apply" or "Update".
- **The attribution row is written after the pull request exists.** A row for a
  commit that never landed would send the next round of fixes to a repository that
  never took the recipe, which is exactly the question the row is kept to answer.
- `recipe_downloads.repository` (text, 300, nullable) and `RecipeUseSource.Repository`,
  in migration `20260920000000_AddRecipeDownloadRepository`. No new index: the admin
  card reads one recipe's rows, which the existing
  `(organization_id, recipe_id, downloaded_at)` index already serves.
- **The zip-slip-safe path join and the title slug moved to
  `Services/Cookbook/RecipePaths.cs`.** A recipe now leaves the app two ways and both
  have to produce the same paths; `CookbookEndpoints.BuildSafeEntryPath` stays as a
  one-line delegate so the existing tests still name the rule where they found it.
- **The download modal gains an "or" section, not a second dialog.** It renders only
  when `GetAccessAsync` says Ready - somebody with no GitHub account connected is not
  told about a door they cannot open - and its button is an outline one, so the
  download stays the dialog's only primary action. The customer name above is shared
  by both paths. On success the dialog closes and the page carries the pull-request
  link, because that link is what the consultant sends on.
- **The admin card iterates in the page, not in a service.** "Open all" awaits one
  apply per repository in order and records each outcome against its row; a refusal is
  caught per repository so one archived repository does not stop the rest. No queue -
  this is a person pressing a button behind a loading state, like every other GitHub
  call a person triggers.
- `apply_recipe` returns the shared `RepositoryDeliveryResult`, which gained an
  `IsNewPullRequest` defaulting to true (a `generate_*` tool always opens a fresh pull
  request, so the default is right there and the DTO stays one shape).
- Tests: `GitHubRecipeDeliveryTests` (13), `ApplyRecipeToolTests` (5),
  `RecipeDownloadTests` (+8 for the column and the distinct list), and two bUnit files
  - `RecipeDetailRepositoryTests` and `AdminRecipeUpdateRepositoriesTests` - which are
  this feature's rendered evidence, there being no browser in the build environment.

## #632 Publish build artifacts as GitHub Releases, and deliver from them

Named user: a consultant whose customer wants every shipped `.app` on the repository's
Releases page, and who sometimes has to redeploy a version the workbench did not build.

**Decisions**

- **Publish is a build-pipeline option that names a repository.** `oe_pipelines` gains
  `github_release_repository_id` (nullable FK to `oe_project_repositories`); non-null
  means "publish every successful build there". The editor offers the solution's GitHub
  repositories inside the connected organisation, so a solution with several
  repositories says which one gets the Release.
- **The tag is `v<version>`** where the version is the built app's version. A build with
  several artifacts at different versions is tagged `build-<number>` instead (it used to be
  recorded as "not published: the apps have different versions"): once builds add their
  number to each app's version, apps that keep their own Major.Minor share only the build
  number (`.design/object-explorer-project-builds.md`, "Build numbers in app versions"). A publish failure of any kind is a build log section and a note
  on the build page, never a failed build: the `.app` exists and downloads regardless.
- **Idempotent on re-run.** An existing Release at the tag has its assets replaced
  (delete then upload) and its body updated; the tag is never moved. Installation token
  throughout - a Release is an act of the organisation, and there may be no user (a
  webhook build).
- **Deliver from a Release is a release-pipeline source.** `oe_release_pipelines` gains
  `artifact_source` (`build` | `github_release`) and `github_release_repository_id`;
  `build_pipeline_id` becomes nullable and is required exactly when the source is
  `build`. Choosing a tag downloads the `.app` assets and **stages them as a
  `OeProjectBuild` row** (status `ready`, no pipeline, `github_release_tag` set) with
  `OeProjectBuildArtifact` rows, so `DeliveryService` and every downstream reader work
  unchanged. `ScheduleDeliveryAsync` accepts a staged build for a Release-sourced
  pipeline in place of the build-pipeline check.
- **Asset downloads follow the redirect by hand.** The typed client does not follow
  redirects (a 302 is an answer elsewhere), so the asset call reads `Location` and fetches
  the storage URL without the Authorization header.
- **Refusals are plain.** A "restrict tag creation" rule surfaces as GitHub's own message
  on the build; a Release whose assets are not `.app` files is refused at staging time.
- **MCP:** `BuildRow` gains `GitHubReleaseTag`/`GitHubReleaseUrl`; a new
  `stage_github_release(releasePipelineId, tag)` returns the staged build so
  `publish_build` can take it; `list_github_releases(releasePipelineId)` lists the tags.
  `.design/saas-delivery.md`'s "the artifact source is this build pipeline's builds" is
  widened accordingly.

**As built**

- **Publishing hangs off the build worker, not off the build service.**
  `ReleaseImportWorker` calls `GitHubReleaseService.PublishBuildAsync` immediately after
  `MarkBuildReadyAsync`, inside the same DI scope and ambient organisation identity. That
  is the one point where a build is final *and* nothing downstream is waiting, so a
  publish can only add to it. The call is wrapped twice - the service catches GitHub's
  refusals itself, the worker catches everything else - because "a publish failure is
  never a build failure" has to hold for a bug in the publisher too.
- **The outcome lives in three places, and each is for a different reader.**
  `oe_project_builds.github_release_tag` / `_url` / `_error` are what the build card
  renders ("Published as v1.2.3.0", linked, or "Not published to GitHub: ..."); a
  `oe_project_build_logs` row headed "GitHub Release" is what the raw log download shows;
  the returned `GitHubReleasePublishResult` is what a caller acts on. The log row is
  appended after `PersistLogsAsync` has run rather than fed through it, so publishing does
  not have to be part of the build service's log lifecycle.
- **The tag doubles as the staged-build marker, and `pipeline_id is null` is the other
  half of it.** A build with a tag and a pipeline was compiled here and published there; a
  build with a tag and no pipeline was downloaded from a Release. `ScheduleDeliveryAsync`
  distinguishes them on exactly that pair, so nothing new had to be added to say which
  kind of build a delivery is publishing.
- **Staging is idempotent on `(tag, release URL)`, not on the tag alone.** Two release
  pipelines in one solution can draw from different repositories, and both their `v1.0.0.0`
  releases are real; matching the URL as well keeps "which one did we deploy" a question
  with one answer without inventing a repository column on the build.
- **The `.app`'s manifest is read directly, not through `AppPackageReader`.** Staging wants
  a name and a version; the full reader parses every symbol in the package, which on a
  base-app-sized `.app` is minutes of work for two strings. `GitHubReleaseService` opens the
  zip behind the 40-byte NAVX header, reads `NavxManifest.xml`, and falls back to the
  `Publisher_Name_Version.app` file-name convention and then to the bare file name. No new
  package.
- **The publish path never asks `GitHubRepositoryService.ResolveAsync`.** That resolver is
  the *user*-credential gate, and a build worker has no user - a webhook build will have
  none at all. The repository is instead pinned to the pipeline's own
  `oe_project_repositories` row (which only a manager of that solution can set), checked to
  be on GitHub, and checked to sit inside the connected organisation's login before any
  call is made. The two agent-facing calls, which do run as a person, go through
  `ProjectAccess.EnsureCanManageAsync` on the owning solution.
- **The editors show nothing they cannot act on.** Both the publish select and the
  release-pipeline source choice are rendered only when
  `GitHubReleaseService.ListRepositoryOptionsAsync` returns something, which needs a
  connected GitHub organisation *and* a repository of this solution inside it. An
  organisation on Azure DevOps sees the two dialogs exactly as they were - the same rule
  #624 settled for the picker. A release pipeline with no build pipeline is no longer a
  dead end either: it now offers the GitHub source instead of only pointing at the
  Pipelines tab.
- **`build_pipeline_id` became nullable rather than gaining a sentinel**, and
  `ReleasePipelineService.ValidateAsync` writes exactly one of the two source columns -
  switching an existing pipeline to Releases clears the build pipeline it used to draw
  from. `artifact_source` is backfilled to `build` by the migration, so every pipeline that
  existed before this reads as what it already was.
- **Asset uploads and downloads are the two calls that leave `api.github.com`.** The upload
  goes to the `upload_url` GitHub hands back (on `uploads.github.com`, with the
  `{?name,label}` template stripped and the name passed as a query parameter, body as raw
  bytes); the download follows the 302 to `objects.githubusercontent.com` by hand and
  fetches it **with no Authorization header**, since the storage URL is already signed and
  handing it our installation token would be giving a credential to a service that never
  asked. A test asserts that call carries no credential.
- **`contents: write` is what a Release needs**, and `GitHubPermissionLabels` now says so in
  words ("Read and write files, and publish releases, in repositories") rather than adding a
  permission name to the tab.
- Deliberately left out: publishing a build to several repositories, a per-pipeline tag
  template (the tag is `v<version>`), draft or pre-release Releases, and any retro-fitting
  of Releases for builds that finished before this shipped.

**Review fixes.** Three things the first cut got wrong, all about the upload half:

- **The upload host is checked before the credential is attached.** `upload_url` arrives
  inside GitHub's own answer, and an empty one used to strip to a relative address that
  resolved against `api.github.com` - so a build, with the organisation's installation
  token, went somewhere nobody chose. The client now refuses anything that is not an
  absolute `https` address on a `*.github.com` host, and `GitHubReleaseService` says so in
  words when a created Release comes back without one rather than reporting a publish with
  no files as a success.
- **A file transfer is not a metadata read.** The typed client's 30-second `Timeout`
  applied to a Release asset as well, which is a few megabytes on a slow link. The client
  now has no timeout of its own; every call carries a linked deadline instead - 30 seconds
  by default, ten minutes for the upload and the download.
- **Saving a solution no longer unsets the Release repository.** `oe_pipelines` and
  `oe_release_pipelines` point at an `oe_project_repositories` row with `ON DELETE SET
  NULL`, and `ProjectService.UpdateProjectAsync` used to drop every repository row and
  re-add it - so renaming a solution silently left every Release-sourced pipeline in it
  with `artifact_source = github_release` and no repository. Repositories are reconciled in
  place now, matched on provider plus normalised URL, and only a repository the user
  actually removed is deleted. That case still nulls the pipelines, which is what was
  asked for, and `GitHubReleaseService` words the resulting state honestly ("This release
  pipeline no longer names a repository; pick one on the release pipeline.") rather than
  claiming the pipeline draws from a build pipeline.
- **GitHub rewrites some asset filenames** (spaces become dots, for one). The workbench
  reads the assets back by the name GitHub reports, not by the name it sent, so this costs
  nothing today - but a future feature that matches on the uploaded name has to read the
  name back rather than assume it.

## #627 Compile a pull-request branch and post the result as a check run

Named user: a team with no CI of its own that wants "does this still compile?" answered
on every pull request, inline in the Files tab.

**Decisions**

- **One deployment-wide webhook secret** on `system_settings`
  (`github_webhook_secret_encrypted`), entered on `/site-admin/settings/github` beside
  the client secret, with the **Webhook URL** shown read-only from `PublicOrigin` (falling
  back to the request host when unset) so the operator copies one address. Redacted in
  audit; blank keeps, a clear flag wipes.
- **`POST /github/webhook`** is anonymous, antiforgery-disabled, rate-limited, size-capped
  (1 MB; 25 MB, GitHub's own ceiling, for a `push` with a well-formed signature header,
  whose commit file lists can be large - #1126; at most four bodies that declare more than
  a megabyte, or no length, are read at once, since the body must be read before a forgery
  can be told apart. A push declaring a megabyte or less never waits for those slots; one
  source address holds at most one slot (the address the rate limiter uses); one that
  cannot get a slot within two seconds is answered 503, a slotted read that has not
  finished within ten seconds, or that the server drops for its minimum data rate, is
  answered 408, and the read buffer grows with the bytes
  that arrive rather than from the declared length - #1174), verifies `X-Hub-Signature-256` with a constant-time compare over the raw body,
  and writes a body on every response so the status-pages middleware does not rewrite a
  401 into a 400. It is on the maintenance-mode allow-list: accepting a delivery is
  enqueueing, and GitHub disables hooks that keep failing. `ping` answers 200.
- **Only GitHub's webhook addresses may deliver (#1201).** The slots above bound what
  one address can hold, but someone with many addresses could still hold them all with
  forged slow pushes. So the first thing the endpoint does, before it reads the body or
  takes a slot, is check the sender against the `hooks` ranges of `GET /meta`, and answer
  anything else 403 with a short text. `GitHubHookAddressRefreshScheduler` reads that list
  (unauthenticated, through the App's API client) about twenty seconds after start-up and
  then daily, into the singleton `GitHubHookAddressAllowList`; a failed or empty refresh
  keeps the list in use. Until a list has loaded once the check **fails open** - every
  sender is let through, with a rate-limited warning - so a GitHub outage or a host with
  no outbound route does not lose deliveries; `DISABLE_GITHUB_HOOK_ADDRESS_REFRESH=1`
  keeps it that way for good. An IPv4 sender reported as IPv4-mapped IPv6 is matched as
  IPv4; a request with no address at all is refused once a list is loaded. Redeliveries
  the recovery sweep asks for come from the same ranges, so they pass, and the sweep
  treats a logged 403 as worth resending: only GitHub's own deliveries are in that log,
  so a 403 there was a genuine delivery refused for a proxy setting or a stale list.
  **The check depends on the trusted-proxy setting.** The sender is
  `HttpContext.Connection.RemoteIpAddress` after `UseForwardedHeaders`, which takes the
  `X-Forwarded-For` value only from a peer listed in `TRUSTED_PROXIES` (or loopback),
  and only the last hop (`ForwardLimit` 1). Behind a proxy that is not listed, every
  delivery looks like it came from the proxy and is refused; the refusal warning (at most
  one every five minutes, with a count of the ones it skipped) names the address and
  points at `TRUSTED_PROXIES`, and the site admin's GitHub settings page shows when the
  list was last read and the last refused sender, for a day after the refusal. A client's
  own `X-Forwarded-For` is ignored when it connects directly from an untrusted address,
  and a trusted proxy appends the peer it saw last - **provided the only peers inside the
  trusted range are proxies.** A trusted range as wide as the compose bridge network
  (`172.16.0.0/12`) also covers Docker's port forwarding, so while the app's own port is
  published a client can connect to it, appear to come from the bridge gateway, and name
  a GitHub address; the README and `compose.yaml` therefore say to remove that mapping
  or bind it to `127.0.0.1` when Caddy is in front. The signature check still applies to
  such a request. A refusal also asks the refresh scheduler for an early read of the
  list (at most one an hour), so a range GitHub has just added is picked up while the
  recovery sweep still has resends left.
  `pull_request` with action `opened`, `synchronize` or `reopened` enqueues; everything
  else is 204. Since #963 it also takes `push`, and a `pull_request` `closed` with
  `merged: true`: both are parsed, enqueued and answered 202 behind exactly the same
  secret, size and signature checks, and neither builds anything (see **Branch
  watching** below). A tag push, or a pull request closed without merging, is 204.
- **The org is resolved from `installation.id` inside the worker**, by the per-org
  ambient loop (no `IgnoreQueryFilters()`). The repository is matched to
  `oe_project_repositories` rows by normalised clone URL under that org's filter; every
  solution that tracks it gets a build and a check run named
  "AL Workbench / {solution}".
- **The clone uses the installation token** (`x-access-token:<token>` in the same
  `http.extraHeader` shape as a PAT). A webhook has no user, and the check runs as the
  App - the honest model the issue names. Manual builds keep the user's PAT; nothing
  about them changes.
- **A PR build is an `OeProjectBuild`** with `trigger = pull_request`, `Branch` = head ref,
  `pull_request_number`, `head_sha`, `check_run_id`, no pipeline and no user, all apps
  selected. It rides the existing `ReleaseImportQueue` through a new
  `ReleaseImportSource.PullRequestBuild` case so its symbols resolve and its Release
  ingests exactly as a manual build's do.
- **Supersession.** A `GitHubWebhookQueue` keyed on `(installation, repository, PR)`
  records the latest head SHA per key; a job whose SHA is no longer the latest is skipped
  when dequeued, and an in-flight build for the same key is cancelled through a linked
  token the compile loop already honours. One build per PR head at a time.
- **Diagnostics become rows.** `alc`'s `path(line,col): severity CODE: message` lines
  are parsed into `oe_project_build_diagnostics` (build, repository, path, line, column,
  severity, code, message) for every build, PR or manual. The check run posts them as
  annotations in batches of 50 (GitHub's cap per request), and the build page shows the
  count.
- **Check run lifecycle:** `in_progress` when the build starts, `completed` with
  `success` / `failure` (any compile error) / `neutral` (build could not run, e.g. no
  symbols), and a summary naming the apps compiled. `checks: write` is the new App
  permission this needs.
- **No MCP surface.** There is no user.

**As built**

- **The webhook secret is its own resolver, not part of the App.**
  `SystemSettingsService.ResolveGitHubWebhookSecretAsync` reads and decrypts only
  `github_webhook_secret_encrypted`, deliberately without requiring the App id or
  private key the way `ResolveGitHubAppAsync` does. The signature check is the first
  thing an anonymous request meets and has nothing to do with whether tokens can be
  minted; null there means every delivery is refused, which is the safe direction.
  Its own Data Protection purpose, redacted by `AuditInterceptor`, cleared by the
  same three-branch clear/set/keep the client secret uses, and wiped with the App id
  like everything else on that row.
- **The Webhook URL is read from `PublicOrigin`, unlike the Setup and Callback
  URLs.** Those two are copied by an operator whose browser is already on the right
  host; GitHub reaches the webhook from the internet, so behind a reverse proxy the
  page's own host can be the wrong answer. With `PUBLIC_BASE_URL` unset it falls
  back to the request host, which is what the sibling addresses have always used.
  The walkthrough grew a step for the `pull_request` event subscription and a clause
  for the `checks` grant: both are ticked on GitHub, and leaving either out makes the
  gate silently never fire.
- **The endpoint touches nothing.** `POST /github/webhook` resolves the secret,
  verifies the HMAC, parses the payload and writes to a channel - no organisation is
  resolved and no row is read or written on the request thread. Every response
  carries a body, including the 401s, because `UseStatusCodePagesWithReExecute` would
  otherwise rewrite them to 400 in GitHub's delivery log. A payload we cannot read
  (no installation, missing fields, not JSON) answers 204 rather than 4xx: GitHub
  would retry a 4xx forever and there is nothing on our side to fix. The rate limit
  is a fixed 300 per minute per source address.
- **The queue does supersession itself rather than using the dedupe gate.**
  `JobQueue<TJob, TKey>`'s gate would coalesce a *new* head into the *old* build,
  which is the opposite of what a reviewer wants. So `GitHubWebhookQueue` records the
  latest head SHA per `(installation, repository, pull request)` and holds the
  `CancellationTokenSource` of the build in flight for that key: announcing a newer
  SHA cancels the running build, and a job dequeued for an older SHA is skipped. A
  key it has never heard of counts as current, so a restart's lost bookkeeping
  builds rather than refuses. The record only moves forward: each delivery carries the
  pull request's `updated_at`, a head older than the one recorded is not announced, and
  the swap is a compare-and-set, so two deliveries handled at once (GitHub does not
  promise order) cannot leave the older head as the latest (#1120); a built head is remembered
  for the three days the resend sweep looks back, so a late redelivery of an older one is still recognised. The superseded build is recorded as failed with
  "Superseded by a newer commit on the same pull request" - the newer job carries
  its own check run.
- **No durable job row for a pull-request build.** `StartPullRequestBuildAsync`
  enqueues with `JobRowId: 0` and writes no `oe_import_jobs` row, so the startup
  reconciler never resumes one. By the time a restarted process got to it the head
  may have moved, and re-running would complete a check run about a commit nobody is
  reviewing. Instead, on its first pass after startup `GitHubWebhookRecoveryScheduler`
  marks every pull-request build left queued or building by the previous process as
  failed ("The workbench restarted before this build finished") and completes its check
  run as `neutral` with the same words, so the pull request is not left spinning. The
  next push is the recovery (#1121).
- **The organisation is found by walking, not by querying across tenants.**
  `GitHubPullRequestBuildWorker.ResolveOrganizationAsync` reads `organizations` (no
  tenant filter, so no bypass), then enters an `AmbientOrganizationScope` and a fresh
  DI scope per organisation and asks `GitHubConnectionService.GetStatusAsync()` under
  that organisation's own filter, stopping at the first installation id that matches.
  Pending organisations are skipped, as the sweeps skip them. **No
  `IgnoreQueryFilters()` was added anywhere in this issue**; the baseline test is
  unchanged.
- **Repository matching is on a normalised clone URL** - host and path, lower-cased,
  no scheme, no `.git`, no trailing slash, and scp-style remotes folded in - because
  the same repository is entered by hand in several spellings. Every distinct
  solution that tracks it gets its own check run and its own build; one solution
  failing to start does not stop the others.
- **`ProjectBuildOptions` carries what makes a build not-manual** (the repository
  under review, the head SHA, the installation token) rather than three more
  parameters on `BuildAsync`. The pull-request repository is cloned as usual and then
  `git fetch --depth 1 origin <sha>` + `git checkout --detach <sha>`; the head is
  normally on a branch the blobless single-branch clone did not follow. A commit that
  has been force-pushed away between the delivery and the fetch fails that repository
  the way a clone failure does, not the whole build.
- **The installation token travels in the same `http.extraHeader` shape a PAT
  does**, reusing `BasicAuthHeaderValue`'s `x-access-token:<token>` form, so nothing
  about how git is invoked changes. Every GitHub repository of the solution uses it,
  not only the one under review - a webhook build has no user, so there is no
  personal token for the others either. A non-GitHub repository in the same solution
  is skipped with a plain reason rather than a token error.
- **Diagnostics are parsed for every build, manual included.** `AlcOutputParser` is
  static and I/O-free; it reads the `path(line,col): severity CODE: message` shape,
  drops the trailing `[project]` the compiler sometimes appends, and keeps a Windows
  drive letter's colon out of the split. Paths are made repository-relative against
  the clone root, because an absolute build-machine path matches no file GitHub knows
  and the annotation is silently dropped. Rows land in
  `oe_project_build_diagnostics` beside the logs, on the success path and the failure
  `finally` alike, and the pipeline build card shows the counts. The new table is
  listed in `TenantTableCatalog.ContentTables` right after its parent, so a
  per-tenant restore puts the rows back instead of leaving them cascade-deleted -
  the schema tests catch exactly that omission.
- **Three conclusions, and the third is the interesting one.** `success` when the
  build is ready with no error diagnostics; `failure` when the compiler reported an
  error; `neutral` when the build could not run at all (no symbols for the declared
  application version, no compiler, the commit gone) - nothing was learned about the
  code, so a red X would be a claim we cannot support, and the summary says what
  stopped it. Annotations go up in batches of fifty, GitHub's cap, and only the first
  batch carries the conclusion so `completed_at` is stamped once.
- **Reporting is best-effort throughout.** A check run GitHub refuses (the missing
  `checks: write` grant, most often) is logged and the build carries on with
  `check_run_id` null; a build with no check run is still a build, still ingests, and
  is still visible in the workbench. The reverse is not true: a build that fell over
  because GitHub was unreachable would be a missing answer, which is worse.
- **`details_url` points at the Solution** (`/solutions/{id}`), not at a per-build
  page - there is no route for one, and a pull-request build has no pipeline whose
  page it could use. It is omitted entirely when `PUBLIC_BASE_URL` is unset, since a
  link to `localhost` is worse than none.

**Member forks.** Refusing every fork turned out to refuse the ordinary case too: a
consultant who works from a personal fork of the customer's repository, which is a normal
way to work in a GitHub organisation, got no answer on any pull request. A fork is now
built when three things hold, and all three are needed.

- **GitHub calls the author a member.** `pull_request.author_association` is `MEMBER` or
  `OWNER`. That is GitHub's own verdict on who the person is to the repository, and the
  delivery carrying it is HMAC-verified, so it is worth reading - `COLLABORATOR`,
  `CONTRIBUTOR`, `FIRST_TIME_CONTRIBUTOR`, `NONE` and the field being absent are all read
  as no.
- **The fork is the author's own.** `pull_request.head.repo.owner.login` equals
  `pull_request.user.login`, case-insensitively. A fork's owner can hand push rights to
  anybody, so a member opening a pull request from a third party's fork is still somebody
  else's code arriving under a member's name, and it is refused with its own log line.
- **The membership still holds at build time.** `author_association` is stamped when the
  pull request is opened and re-used by every later `synchronize`, so somebody who left the
  organisation in between still arrives labelled `MEMBER`. The worker therefore asks GitHub
  again, on the installation token, through
  `GitHubAppClient.InstallationSeesOrgMemberAsync` (`GET /orgs/{org}/members/{username}`,
  a new `GitHubAppClient.Members.cs` partial): 204 is yes, and 404, the 302 GitHub sends a
  caller who is not itself in the organisation, and GitHub not answering at all are all no.
  **An answer we could not get is a refusal** - the cost of guessing wrong is a stranger's
  code compiled on the customer's installation. The `Members: read` grant the App has
  carried since phase 1 is what makes the call possible.

The check happens after the organisation is resolved and **before any check run is
opened**, so a refused fork leaves nothing spinning on the pull request; the workbench
simply says nothing, which is what it already does for a repository no solution tracks.
A same-repository pull request never makes the call, so nothing about the ordinary path
costs an extra request on the organisation's rate limit.

The clone is unchanged. `ProjectBuildService.CheckoutCommitAsync` already runs
`git fetch --depth 1 origin -- <sha>` against the *base* repository's remote, and GitHub
advertises the pull request head there as `refs/pull/N/head`, so the fork's commit is
reachable without ever adding the fork as a remote or handing a token to it. That also
keeps the credential story intact: the installation token is only ever presented to the
repository the organisation actually installed the App on.

What the reviewer sees says where the code came from: the check-run summary carries
"Built from {author}'s fork." The author rides along on the in-memory
`ReleaseImportSource.PullRequestBuild` job rather than on `oe_project_builds` - it exists
for the length of one build and needs no column, so this change has no migration.

**Review fixes.** The gate as first written would have failed on a default deployment,
and it accepted work it should not have. What changed:

- **A null is not an absent field.** Request bodies are serialised with
  `DefaultIgnoreCondition = WhenWritingNull`, so a check run with no details URL and an
  annotation with no code leave the property out rather than sending `null` - GitHub's
  check-run schema answers 422 to the second, which on a deployment that has not been told
  its own public address is every check run there is.
- **Fork pull requests from outside the organisation are not built.** Anyone on GitHub can
  fork a public repository and open a pull request against it; building one would clone and
  compile a stranger's code on the customer's own installation token, on a machine holding
  that organisation's symbols. The webhook parser reads `pull_request.head.repo` and answers
  204 when the head repository is not the repository the delivery is about, logging it at
  Information. A fork belonging to a *member* of the connected organisation is the one
  exception, added afterwards and described under **Member forks** below; there is still no
  opt-in for anybody else's.
- **What reaches git is checked before it gets there.** The head SHA must match
  `^[0-9a-f]{7,40}$` and the head ref may only contain `A-Za-z0-9._/-` and may not start
  with a dash; `git fetch` gets a `--` before the revision. `git checkout` deliberately
  does not - a `--` there turns the revision into a pathspec and git refuses it - so the
  same SHA check is repeated in `ProjectBuildService` at the boundary that actually runs
  git.
- **Supersession has no window.** The worker re-asks `IsLatest` immediately after
  registering its cancellation source, because a newer head announced between the enqueue
  and that registration would have found nothing to cancel; the stale build is recorded as
  superseded and says nothing on GitHub, since the newer job owns its own check run. The
  endpoint announces only after a successful enqueue, so a delivery that was refused cannot
  cancel the build that is running. `EndBuild` evicts the newest-head entry when the head
  just built is still the newest, so the map does not grow for the life of the process.
- **The queue refuses rather than waits.** The webhook runs on a request thread GitHub is
  timing, so a full channel is answered with 503 and a body ("Busy; the delivery will be
  requested again") instead of blocking.
- **A refused delivery is asked for again (#1121).** GitHub does not resend a delivery
  it logged as failed - a 503 from a full queue, a 413, a 5xx, or no answer at all while
  the app was down; it waits for somebody to press Redeliver. So
  `GitHubWebhookRecoveryScheduler` does that every five minutes: it reads the App's
  delivery log (`GET /app/hook/deliveries`, as the App), and for each event the endpoint
  acts on (every `push`; `pull_request` opened, synchronize, reopened and closed) whose
  attempts all failed it asks GitHub for one more (`POST /app/hook/deliveries/{id}/attempts`).
  It looks back at most three days (what GitHub keeps), asks for no more than the webhook
  queue has room for (half its capacity, less what is waiting), stops after five attempts
  at one event, and asks in the order the events first failed. A 4xx other than 403, 408,
  413 and 429 is not resent: that is a delivery we read and turned away. A resent push is
  safe because the push path ignores a push it has seen or one older than the recorded
  head. The pull-request path has no such guard - the endpoint would take a resent older
  head as the newest and cancel the build of the real one - so a pull-request build
  delivery is resent only while GitHub still reports its head as the open pull request's
  head (read from the logged payload, then `GET /repos/{owner}/{repo}/pulls/{n}`).
  Attempt counts are in memory, so a restart allows five more. An attempt is counted only
  once GitHub takes the resend: a 404, 410 or 422 settles the event; a 401 or a 403 that is
  not rate limiting is the App being turned away, so it leaves that event and the rest of
  the sweep for the next one; a 5xx, a timeout, rate limiting or no answer leaves the event
  for the next sweep and moves it behind the events GitHub has not refused, and the sweep
  stops once two different events have been refused that way (#1175). Each sweep reads at most twenty pages: the newest stretch back to where the
  last complete read began, then whatever older stretch an earlier sweep's page budget
  did not reach, resumed from its cursor; failures it found but did not get to are carried
  to the next sweep by id rather than by reading the window again.
- **A check run is never left spinning.** The run is opened before the build is queued, so
  a failure between the two completes it as `neutral` with the reason; and a delivery that
  arrives while a restore is in flight is held and re-queued rather than reaching the
  database (the webhook route stays open through maintenance on purpose, because GitHub
  disables a hook whose deliveries keep failing).
- **The run is decided on the repository under review.** A solution can track several
  repositories and a pull request is about one of them: diagnostics are filtered to the
  `oe_project_repositories` row whose normalised clone URL matches the delivery, the
  conclusion is that repository's, and the others are counted in the summary as "N errors
  in other repositories of this solution" without failing the run. Annotations are capped
  at 200 (four of GitHub's batches) and the summary says how many were left out.

### Branch watching (#963)

Named user: a consultant who merged two pull requests this morning and wants to know,
without opening GitHub, whether the customer's pipeline has a build of them yet.

A build pipeline watches one branch, and the workbench knows when that branch has moved
past what the pipeline last built. **Nothing builds on push** unless the pipeline asks
for it (see "Building on push (#1079)" below). This is the engine for an indicator (the
surfaces are #964): a person reads it and decides whether to press Build. Nothing polls GitHub either: pushes are free and
immediate, and a deployment whose App is not subscribed simply has no heads.

- **A branch on the pipeline.** `oe_pipelines.branch` (nullable; null is each
  repository's default branch, which is what a build took before). Edited in the pipeline
  editor; `PipelineService` holds it to git's ref-name rules over the alphabet
  `A-Z a-z 0-9 . _ / -` (`GitBranchName`), and the field's `pattern=` mirrors the rule, with
  a test running both against the same names. A manual build snapshots the branch onto
  `oe_project_builds.branch` when it is queued, as it already snapshots the extension
  selection, and the clone passes `--branch <name>` - re-checked against the same rule at
  the boundary that runs git. A repository without that branch fails its clone with git's
  own message, like any clone failure. A pull-request build ignores the pipeline's branch
  and keeps its own head. The extension picker's discovery clone still reads the default
  branch.
- **The `push` event.** The endpoint reads `refs/heads/*` pushes into a `GitHubPushJob`
  (tags and other refs are 204), holding the SHA to a full 40-hex object id and the branch
  to the same rule. It carries `forced`, `deleted`, the pusher, `repository.pushed_at`,
  `repository.default_branch`, and the last ten of `commits[]` (GitHub lists at most
  twenty), messages clipped to 500 characters. It rides the same `GitHubWebhookQueue`
  (the queue's element type is now a small `GitHubWebhookJob` hierarchy; supersession is
  still only about pull-request builds), and `GitHubPullRequestBuildWorker` resolves the
  organisation by installation id exactly as for a pull request - the same per-org ambient
  walk, **no `IgnoreQueryFilters()`** - then hands it to `GitHubBranchActivityService`.
  Repository matching is the same normalised clone URL, now shared by both paths
  (`GitHubRepositoryMatch`). A push to a repository no solution tracks, or for an
  installation nobody connected, is dropped with a Debug log.
- **`oe_repository_branch_heads`**: one row per solution repository and branch
  (`project_repository_id`, `branch`, unique together), upserted per push: `head_sha`,
  `pushed_at`, `pusher_login`, `forced`, `commit_count` (what the last push listed),
  `commits_json` (up to ten `{sha, message}`, oldest first), `is_default_branch` and
  `deleted_at`. A push that fast-forwards from the stored head appends to the commit
  list; a forced push, a recreated branch, or a push whose `before` is not the stored head
  replaces it. A deleted branch keeps its row with `deleted_at` set (and the commit it last
  pointed at), and a later push to it clears the mark. `is_default_branch` is not in the
  issue's column list: it is how a pipeline with no branch knows which row to read, and
  every push re-marks it, so a default-branch rename follows on the next push. Two
  solutions tracking one repository each get their own rows.
- **`oe_repository_merged_pull_requests`**: `project_repository_id`, `number` (unique
  together, so a redelivery updates rather than duplicates), `title`, `base_branch`,
  `merge_sha`, `merged_at`, `author_login`. No fork check on this path: nothing is cloned or
  run, and whatever merged is in the repository regardless of where it was written.
  `opened` / `synchronize` / `reopened` keep queueing builds exactly as before.
- **`BuildFreshnessService.GetAsync(pipelineId)`**, gated on the solution's visibility like
  the pipeline pages, returns one entry per repository of the solution: the watched branch
  (the pipeline's, or the head marked default), the stored head, the commit the pipeline's
  last `ready` build pinned in that repository (`oe_project_build_repo_commits`), and a
  state, decided in this order: `BranchGone` (the head row is marked deleted),
  `NeverBuilt` (no successful build of this pipeline pinned a commit there - including a
  repository added since), `Unknown` (no head stored for the branch), `UpToDate` (same
  SHA), otherwise `Ahead`. Comparison is by SHA only. For `Ahead` it adds, best effort, the
  merged pull requests into that branch since the built commit's committer date (the
  built commit's own merge excluded by SHA), and the stored commits after the built one,
  both newest first, with `CommitsComplete` false when the built commit is not in the
  stored list - after a force push, or more than ten commits later. The head's `forced`
  flag is passed through so a surface can say why a count would be meaningless.
- **Both tables are restorable tenant content** (`TenantTableCatalog.ContentTables`, right
  after `oe_project_repositories`): nothing can recompute them, since nothing polls.
- **The App must subscribe to Push.** The site-admin GitHub page says so beside the
  Webhook URL, and the walkthrough's subscription step now names Push and Pull request.
  Without Push every repository reads `Unknown`, and nothing else changes.

**The surfaces (#964).** Named user: the same consultant, opening the Builds list in the
morning to see which customers need a build before today's deployments. The per-repository
answer folds into one sentence per pipeline (`PipelineFreshnessSummary`, strongest first:
any repository ahead, a deleted branch, no successful build, a repository missing from the
last build, up to date; a repository whose head is unknown says nothing either way), drawn
by the shared `FreshnessLine`: "Up to date with main", "3 commits ahead on main, 2 hours
ago", "2 pull requests merged since build #118" (preferred when merged pull requests are
stored), "main was force-pushed since build #118" (never a count after a force push, nor
when the stored commits do not reach back to the build: then "New commits on main since
build #118"), "No successful build yet" (the issue said "Never built", which read as a contradiction beside a failed build), "Branch main no longer exists", or nothing.
When every repository that is ahead already has a queued or running build of the pipeline
that will build its new head (a build on push of that exact commit, or a build somebody
started after the push), the sentence is "Building the latest commit on main" instead, and
the pipeline is not ahead: there is nothing for anybody to start (#1128). A preview check
does not count.

- **Builds list**: the sentence under the pipeline's name. An ahead pipeline with no build
  already queued or running takes the warning keyline, a **Build** button beside the sentence (outline, not in the kebab-only actions cell, and
  only for someone who manages the solution) and the **Ready to build** tab
  (`?show=ready`). Build queues the build and stays on the list.
- **Pipeline page**: a **Branch** card above the latest build, per repository: the branch,
  the latest commit (short SHA linking to GitHub, pusher, time), the commit the last
  successful build used, then the merged pull requests (or the pushed commits) in between,
  each linking to GitHub; after a force push or a partial list, a link to GitHub's
  comparison of the two commits. No card when nothing is known. Build in the head stays
  the only primary action.
- **Pipelines dashboard**: a **Ready to build** tile after Failed builds (warning tone when
  above zero, landing on the list's tab) and one "Needs attention" row per ready pipeline
  ("CRONUS Base is 3 commits ahead of its last build"), linking to the pipeline's page.
- `BuildFreshnessService.ListAsync` answers every visible pipeline in a fixed number of
  queries for the list and the dashboard, with whether the caller may build each.
- The handoff sheets have no line, card or tile for this yet; all three are composed from
  `.cell-stack`, `.card` + `.kv-grid` + `.meta-item`, and the dashboard's `.cue`, for the
  design project's next round.

### Building on push (#1079)

Named user: a developer on a customer team who pushes several small fixes to `test` in an
afternoon and wants a build of each one waiting on the pipeline, without pressing Build
after every push.

A build pipeline can opt in to **building automatically when new commits are pushed**
(`oe_pipelines.build_on_push`, off by default and off for every existing pipeline; a
checkbox in the pipeline editor). Mads chose one build per push on 2026-10-06:

- **One build per push, not per commit.** A push that carries five commits is one build, at
  the commit the push left the branch on (`oe_project_builds.head_sha`, plus
  `head_repository_id` naming which of the solution's repositories it belongs to). The
  solution's other repositories clone the pipeline's branch as usual. Intermediate commits
  of one push are never built.
- **Queued, in order.** A build on push is not refused while another build of the pipeline
  runs (#1072): it is queued behind it. The import worker runs one build at a time in queue
  order, so a run of pushes gives a run of builds, each at its own commit, and the build
  history holds one build per change. A build on push that is still waiting does hold up a
  manual build, by the same rule as any other. Past five waiting builds on push for one
  pipeline (`ProjectBuildImporter.MaxWaitingPushBuilds`), a push moves the newest waiting
  build onto its commit instead of adding a sixth, so a long rebase pushed piecemeal cannot
  fill the queue and the branch's latest state is still built. A build the worker has
  started is never moved.
- **Checked again when its turn comes (#1112).** A build on push or a nightly preview
  check that waited is refused as it starts if the pipeline has since been deleted, the
  setting turned off, someone else has taken the automatic builds over, or the person it
  runs as is no longer active or can no longer manage the solution
  (`ProjectBuildService.EnsureAutomationStillOnAsync`). The build fails with that reason
  and clones nothing. Such a refusal sends no build email and is not the build the next
  one is compared with. Only the first run is checked: Retry or Recover symbols on a
  finished build is a person's own choice and runs as them.
- **What does not build.** A forced push (it may have removed others' work, so a person
  decides), a deleted branch, a push to any other branch, and a redelivery of a push whose
  commit this pipeline already built on push. A pipeline with no branch builds pushes to
  the default branch GitHub names on the delivery. GitHub repositories only: pushes arrive
  by GitHub webhook, and Azure DevOps would need polling or a service hook.
- **Whose identity.** The build runs as **the person who last saved the pipeline with
  building on push on** (`build_on_push_by_user_id`), the way a deployment pipeline's
  "deploy without approval" follows whoever saved it, so one manager cannot repoint a
  pipeline and have it clone with someone else's access. As with the nightly preview check,
  the access check, the clone credential (their connected GitHub account, then their build
  token), the name on the build and its notifications are theirs. It is not the GitHub
  App's installation token, which would let builds keep running with nobody's permission
  behind them. When that person is gone, can no longer manage the solution, or has nothing
  to clone with, the push is not built and building on push is **paused** with the reason
  on the pipeline page (`build_on_push_blocked`), where anyone who manages the solution can
  take it over ("Resume with my access"); saving the pipeline does the same. The
  next push that starts a build lifts the pause.
- **An ordinary pipeline build otherwise.** Trigger `push`; numbered and published like a
  manual build (build versioning, GitHub release, #934's prepared deployment), compared
  with manual builds for "build is failing / fixed" notices, and shown on the dashboard
  with the pipeline as the actor. Builds are resumed after a restart like any pipeline
  build, at the same commit.

## #631 Translation memory from every .xlf in the organisation's repositories

Named user: a translator who wants the organisation's past translations to surface as
suggestions without anyone uploading anything.

**Decisions**

- **Scope is tracked repositories inside the connected GitHub organisation**, not the
  whole installation. Tracking a repository as a solution is the organisation's own act
  and is the gate an installation-token read needs.
- **One recursive tree read per repository**, filtered by `TranslationFileRules`; only
  blobs whose SHA differs from the last ingested one are read. A new table
  `translation_memory_sources` (`repository`, `path`, `blob_sha`, `last_ingested_at`,
  `unit_count`) carries that state. Files GitHub will not inline are read through a new
  `GetBlobAsync` (raw blob by SHA); a truncated tree is logged and skipped.
- **Attribution is two new nullable columns** on `translation_memory`:
  `source_repository` and `source_path`, plus `Origin` set to "{repository} / {folder}".
  The unique pair index is unchanged, so a pair seen in two files keeps the most recent
  file's attribution - "where did this come from" has one answer, and it is a real one.
- **The ingest runs daily** (`TranslationMemoryIngestScheduler : PolledScheduler`,
  `DISABLE_TRANSLATION_MEMORY_INGEST_SCHEDULER=1`), per org through the ambient loop,
  and **on demand** from the admin Translation memory page ("Refresh from
  repositories") behind a loading state. It feeds `TranslationMemoryService.UpsertAsync`
  exactly as an import does and never fails the caller.
- **Suggestions say where a match came from**: the chip's caption becomes
  "from {repository} / {folder}" with a link to the file on GitHub when the entry has a
  source. `MemoryEntryView` and the MCP `TranslationMemoryHit` gain the two fields.

**As built**

Seven things settled while building it.

**The generated `.g.xlf` is skipped without being read.** `TranslationFileRules` already
tells a source file from a translation, and a `.g.xlf` holds every string with no
translations in it - so the parser would yield nothing from one. Reading it would cost a
call per extension per night to learn nothing, so the ingest filters it out of the tree
listing rather than downloading it and discovering that.

**The tree and the file reads are taken at `HEAD`, not at a branch name we looked up.**
GitHub resolves `HEAD` to the repository's own default branch server-side, so this is the
same branch a build clones, costs one call fewer than asking the repository what its
default branch is called, and cannot go stale when somebody renames it. The same reasoning
gives the "From" links their shape: `https://github.com/{repo}/blob/HEAD/{path}` needs no
stored branch and survives a rename, so no column carries one.

**A truncated tree suspends the deletion half of the sweep, not the learning half.**
GitHub caps a recursive listing and says so. Learning from the part that came back is
strictly better than learning from none; but treating the files it did *not* list as
"gone" would throw away their recorded blob shas and make the next night re-read the whole
repository. So a truncated listing still ingests, and simply skips the removal pass.

**`GetFileAsync` was returning an empty file where it promised null.** Its own comment
said a file too large for the Contents API to inline comes back as null; in fact GitHub
answers with a complete-looking object whose `encoding` is `"none"` and whose `content` is
`""`, which the method decoded into a file whose text was empty - indistinguishable from a
genuinely empty file. It now returns null unless the encoding is `base64`, which makes the
documented contract true and is what triggers the ingest's fall back to `GetBlobAsync`.
The Translator's own open path gets the same fix for free: a huge file now reads as "not
there" rather than as an empty editor.

**Scope is narrowed twice, and neither narrowing is optional.** The tracked list comes out
of `oe_project_repositories` under the ordinary tenant filter, and each row's clone URL
must also name the connected organisation's login. The installation token can reach every
repository the App was installed on, so without the second check a solution pointing at
`someone-else/their-app` would be read with this organisation's credential. A repository
tracked by two solutions is read once.

**The whole feature adds no `IgnoreQueryFilters()` call.** The scheduler enumerates
`organizations` (no tenant filter on that table), enters the org's `AmbientOrganizationScope`,
and asks `GitHubConnectionService.GetStatusAsync` from the database before spending
anything on GitHub - so an organisation with no connection costs one cached read and no
call at all.

**The page states the two counts a person actually asks about.** "Refresh from
repositories" reports what it read, what it learned, and - only when they are non-zero -
how many files had not changed and how many repositories could not be read. It never
fails: an unreachable GitHub becomes a sentence, and the button is offered only once the
organisation has connected a GitHub organisation, because with nothing connected the only
answer it could give is that nothing is connected. Rendered evidence is
`ALDevToolbox.Tests/Components/AdminTranslationMemoryTests.cs`; a screenshot was not
possible in the build environment.

## #630 Dependency drift: pull requests bumping app.json when a new release is imported

Named user: a consultant who wants to know which customer repositories still target last
year's Business Central after a new release lands in the Object Explorer.

**Decisions**

- **The scan runs when a first-party release reaches `ready`** (the completion point in
  `ReleaseImportService.ProcessReleaseAsync`) and on demand. It reads every tracked GitHub
  repository's `app.json` files through the API on the installation token (the same
  read discovery does), parses them with `AppJsonManifestParser` (extended so dependency
  versions survive), and compares `application` / `platform` against the release and
  `dependencies[*].version` against the well-known-dependency defaults.
- **Drift is a per-org table**, `github_repository_drift` (repository, path, field,
  current, proposed, release id, detected_at), so the Solutions page can say "12
  repositories still on 27.x" from the database.
- **Open update pull requests** edits the `app.json` in place (only the fields that
  moved, preserving formatting elsewhere) on `aldt/bump-bc-<major.minor>` **with the
  user's token**, one pull request per repository, reused while open. The body says what
  moved and links to the release's compare view (`ObjectExplorerLinks.ReleaseCompare`).
- **Only repositories whose `application` range excludes the new version are proposed**
  by default; the "optional" in the issue is the default because the alternative
  proposes a pull request that changes nothing.
- The check-run combination (#627) comes for free: the pull request is a pull request.

**As built**

- **The table is `github_repository_drift`, and a scan replaces the whole
  organisation's rows.** Not only the rows for the release being scanned: a
  finding against a release that has since been superseded is not something
  anybody should be offered a pull request for, and "replace everything" is also
  what makes drift somebody has fixed by hand disappear on its own. The unique
  index is `(organization_id, repository, path, field)`; `field` is
  `application`, `platform` or `dependency:<app id>`, the id normalised the way
  AL means it (braces and case are decoration). The row hangs off `oe_releases`
  with a cascade, which is why the table is in `TenantTableCatalog.ContentTables`
  rather than excluded as a cache - a per-tenant restore that deleted the
  releases would otherwise take the findings with it and put nothing back.
- **What counts as behind.** `application` and `platform` are compared at
  `major.minor` (`BcArtifactIndex.ToMajorMinor` through `BcVersionComparer`) -
  the wave a repository is on, not a build number nobody typed - and the
  proposal is written in the shape the manifest already used, so `27.0.0.0`
  becomes `28.2.0.0` rather than the release's four-part build. `platform` comes
  from the release's `System` module (publisher Microsoft), `application` from
  its `BcVersion`. Dependencies are compared full-version against the
  well-known-dependency catalogue's default. A manifest with no `application` is
  skipped whole: it states no Business Central to be behind of. So is a
  dependency entry that states no version - there would be nothing to edit.
- **The scan reads the same manifests repository discovery does**, through
  `RepositoryDiscoveryService.ManifestPaths` on one recursive tree read per
  repository, so the two features cannot come to disagree about what a
  repository ships. Which repositories are read is "every GitHub repository a
  live solution tracks, in the connected organisation" - matched to the
  installation's own repository list, so one that is tracked but not shared with
  the App is logged and skipped rather than failing the scan.
- **`ReleaseImportService` takes the drift service as an optional constructor
  parameter** (`DependencyDriftService? drift = null`). DI always supplies it;
  the default exists so a test - or any caller that builds the importer by hand,
  of which there were already four - can ingest a release without standing up
  the whole GitHub stack. The hook runs at both completion points (import and
  amend), only for `Kind == "first_party"`, and swallows everything: the modules
  are in by the time it runs, so an unreachable GitHub is a warning, never a
  failed release.
- **The edit is a byte-level splice, not a re-serialise.** `AppJsonValueEditor`
  walks the manifest with `Utf8JsonReader` and uses `TokenStartIndex` to find
  exactly where a value sits, then replaces those bytes - so key order,
  indentation, comments and a trailing byte-order mark all survive, and the
  reviewer sees a two-line diff. A whole-document `JsonNode` rewrite is kept as
  a fallback for a manifest the walk cannot place a value in; it logs when it
  comes to that, because the formatting is then lost. Before each value is
  written the manifest as it stands on the branch is re-checked, so a repository
  that has moved on since the scan is not pushed back - and a run where
  everything is already current commits nothing and says so.
- **Branches follow the recipe service's rule**: `aldt/bump-bc-<major.minor>`,
  joined while its pull request is open, stepped to `-2`, `-3` past a branch
  whose pull request was merged or closed, and refused after ten. One pull
  request per repository, one refusal never stopping the rest - every repository
  asked for comes back with either a pull request or a reason a person can act
  on.
- **The panel narrows by solution, not by GitHub.** `GetSummaryAsync` drops the
  repositories of solutions the viewer cannot see
  (`ProjectAccess.VisibleProjectPredicate`), which keeps a Private solution's
  repository from being named to somebody not on it - the same rule #629
  follows, decided one step earlier because the answer is already in the
  database and the Solutions page should not cost a call to GitHub per
  repository to render. `OpenUpdatePullRequestsAsync` applies it again before
  writing, and `GitHubRepositoryService.ResolveAsync` is still the gate on the
  repository itself.
- **The customer's environment is the yardstick (#1081).** A repository's
  `application` and `platform` are measured against the Business Central version of the
  production environment of the solution that tracks it - the first by name that is not
  deleted and has a version - because that is what the customer runs; a freshly imported
  wave is not something a repository is behind until the customer is on it. A solution
  with no such production environment is measured against its first sandbox, and one
  with no environment at all is not checked: it is not live yet, so nothing it runs can
  be behind. The release import still triggers the scan, but no longer sets the target.
  `platform` is the environment version's major with a zero minor, since an environment
  reports only its application version. A finding records the environment in
  `environment_id` (cascade, like the release); the panel says per repository which
  environment it was measured against, the pull request names it in its body (only
  when the person can see its solution), and the release compare link is given only
  when that release is the version being moved to. Environment refreshes do not
  rescan by themselves; Check again does, and so does the nightly pass below.
- **`AppJsonDependency` gained `Version` as an optional positional parameter**,
  so every existing caller compiles unchanged.
- **No MCP tool.** `list_dependency_drift` was optional in the brief and is not
  here: the GitHub MCP surface is #633's, and adding a tool to it from this
  issue would have meant editing the same files that issue is rewriting. The
  service method it would wrap (`GetSummaryAsync`) is ready for it.
- **Left out.** No per-repository dismissal - drift is a fact, not a proposal, and it disappears
  when it is fixed. No auto-merge, no compile before opening, no bump of
  anything the catalogue has no default for.

### Opening update pull requests on their own (#1104)

Named user: a consultant responsible for a handful of customers who wants the update
pull request waiting in GitHub the morning after a customer's environment moves to a new
Business Central, the way Dependabot does it, instead of remembering to press the button.

- **Per solution, off by default.** "Open update pull requests automatically" sits on the
  solution's Repositories tab, saved with the rest of the solution, and only shows once
  the solution exists and has a GitHub repository. Per solution because the yardstick is
  already the solution's environment, and customers differ in how they want to be moved.
- **Opened as the person who turned it on**, with their GitHub account - the same rule as
  building on push and the nightly preview check (`oe_projects.auto_update_pull_requests_by_user_id`,
  `SET NULL` on delete). There is no GitHub App fallback. When that person is gone, can no
  longer manage the solution, or has no GitHub account connected, the solution says so
  (`auto_update_pull_requests_blocked`) with "Resume with my GitHub account", which makes
  whoever presses it the new person. A repository GitHub refused shows the same way; the
  next night tries again either way.
- **Adding a repository takes it over (#1177).** Whoever saves the solution with a GitHub
  repository it did not have before - added, or an address changed - while the setting is
  on becomes the person the pull requests are opened as - the "whoever last saved" rule of
  building on push - so nobody can add a repository they cannot write to and have it
  written to with somebody else's account. Removing a repository does not change who it
  is. When the new person has no GitHub account connected, the solution shows it as
  stopped straight away, with the same Resume button; nothing is opened as the previous
  person in between. While the list has such an unsaved change, the section tells the
  person that saving will use their GitHub account. Resume refuses a person with no
  GitHub account connected rather than clearing the warning, and the warning is worded
  to the person it is about ("you have not connected your GitHub account").
- **`DependencyDriftScheduler` runs once a night at 05:00 UTC**, after the environment
  refresh, for every organisation with an imported first-party release: it rescans (so a
  customer moved to a new version during the day is measured against it, and the
  Solutions panel stays current with nobody pressing Check again), then for each solution
  with the setting on, opens the pull requests of its repositories that are behind, in a
  scope signed in as that person. Opt out with `DISABLE_DEPENDENCY_DRIFT_SCHEDULER=1`.
- **Only its own environment counts (#1177).** A repository two solutions share is measured
  once, against one of their environments. The automatic run of a solution acts only on
  findings measured against one of that solution's own environments, so a customer still
  on 25 is never offered the 27 the other customer moved to. The panel and the button are
  unchanged. To keep that from silencing a solution that asked for automatic pull
  requests, the scan measures a shared repository against a solution with the setting on
  before one without it (then the oldest, as before). When two solutions sharing a
  repository both have it on, only the older one's run opens pull requests there; the
  other's customer is not measured for it.
- **Paced, and stopped by a rate limit (#1177).** The automatic run leaves a second between
  the writes it makes to GitHub (GitHub's guidance for requests that create content; its
  secondary limit is about 80 a minute per account) - closing a superseded pull request
  and commenting on it count as two - and the spacing is kept per person across all their
  solutions in one nightly pass. It stops at the first rate-limit
  refusal. The solution does not show that as stopped - there is nothing for it to fix -
  and the person's other solutions wait for the next night too. The button stops at a
  rate limit as well, and says to try again in a few minutes.
- **Each version is offered once per repository.** Every update pull request the
  workbench opens or joins, by hand or not, is recorded in `github_update_pull_requests`
  (repository, version, number, whether it was automatic). The automatic run skips a
  version that has a row and no open pull request, because a closed pull request is
  somebody's answer and GitHub usually deletes its branch with it. The row, not the
  branch, is what decides: like the button, the run walks `aldt/bump-bc-<version>`, `-2`,
  `-3` and joins the first one with an open pull request (so one a person opened as `-2`
  is joined too), stepping past branches with none. A branch with neither an open pull
  request nor a row is a run that failed between creating the branch and opening the pull
  request; it is stepped past like any other, so one failed night never strands a
  version. An open one is joined, never doubled. The button is unaffected: a person can
  always ask again.
- **A newer version supersedes the older automatic pull request.** Once the pull request
  for, say, 29.0 is open, the automatic ones for older versions on the same repository are
  closed with a comment naming the new one, since both edit the same lines. A pull request
  a person opened is theirs to close.
- **Left out.** No email or in-app notification: GitHub already tells the people watching
  the repository. No auto-merge.

## #633 MCP parity for the GitHub workflows

**Decisions**

- `list_repositories` wraps `GitHubRepositoryService.ListAccessibleAsync` and reports the
  readiness reason when the list is empty for a fixable reason ("link your GitHub
  account"), so an agent can tell the person what to do.
- `create_repository` and `add_extension_to_repository` are the standalone twins of the
  options on `generate_workspace` / `generate_extension`: same plan input, same service
  call, same result shape. They exist so an agent can find them by name; the options
  stay.
- `list_translation_files(repository)` and `open_translation_pr(repository, path,
  targetLanguage, edits[], summary)` cover the Translator. The agent supplies per-unit
  edits, applied with `XliffTargetWriter.ApplyEdits` against the file the tool has just
  read, so every other byte is unchanged and the SHA the write quotes is the one it read.
- `apply_recipe` ships with #626.
- Every tool acts as the calling user, describes what it will create on GitHub before it
  does it, and is in `McpToolCatalog`.

**As built**

- **One new tool class, `Services/Mcp/Tools/GitHubTools.cs`**, holding all five tools and
  no `AppDbContext` of its own. It depends on exactly the four services the pages use -
  `GitHubRepositoryService`, `GitHubWorkspaceRepositoryService`,
  `GitHubExtensionDeliveryService`, `GitHubTranslationService` - so every repository name
  an agent supplies is resolved by `ResolveAsync` and nothing can be reached that the
  picker would not have offered. Registered in `Startup/McpRegistration.cs`; the tools sit
  in a new **GitHub repositories** group in `McpToolCatalog`, because five tools about
  repositories read badly split between "generation" and "Translator".
- **`list_repositories` answers even when there is nothing to list.** It returns the
  readiness state by name (`NotConfigured`, `NotConnected`, `NotLinked`,
  `LinkNeedsRepair`, `Ready`) so an agent can branch on it, plus one plain sentence of
  guidance - the same four sentences the pages give - and `null` guidance when everything
  is in place. Answering "why not" costs no GitHub call, so this is still useful while
  GitHub is down.
- **`create_repository` takes a name, never an owner**, exactly like the option on
  `generate_workspace`: the organisation is the connected one and there is nothing for an
  agent to re-aim. Both routes now project their result through
  `RepositoryCreationResult.From` / `RepositoryDeliveryResult.From` in the DTO file, so
  the shapes cannot drift apart. Neither standalone tool carries the ZIP - an agent that
  wants the bytes too calls `generate_workspace` / `generate_extension` with the option,
  and the descriptions say so.
- **`open_translation_pr` reads before it writes, and validates against what it read.**
  The file is opened through `GitHubTranslationService.OpenAsync`, parsed with
  `AlXliffParser`, and any edit naming a trans-unit id the file does not carry is refused
  with the ids listed and nothing committed - an edit that silently does nothing is worse
  than a refusal. An empty `edits` list is refused before GitHub is asked anything.
  `XliffTargetWriter.ApplyEdits` then writes only those targets, and the sha quoted on the
  write is the one that came back from the read.
- **The `.g.xlf` rule mirrors the Translator page.** Reading the compiler's generated file
  writes a new language file beside it (`App.g.xlf` -> `App.da-DK.xlf`) with no base sha,
  which is what makes the save refuse to flatten a translation that is already there;
  reading an ordinary translation file writes that file back, quoting its sha.
  `SetTargetLanguage` is applied when the file came from the generated source or when the
  file's declared target language is not the one being translated into - never on an
  ordinary save of a file that already names its language, so the byte-fidelity contract
  holds.
- **A conflict is reported as itself.** `GitHubContentConflictException` becomes an
  `McpException` saying the file changed since it was read and telling the agent to read
  it again and re-apply, rather than a generic GitHub failure. The other three refusals
  keep the wording the other tools use: `PlanValidationException` as
  "Validation failed: ...", `GitHubApiException` as "GitHub refused the request: ...", and
  the not-configured one passed through.
- **The pull request body's summary is optional.** Left out, the tool writes a count of
  what it changed, so a pull request opened by an assistant still says what happened.
- No migration, no new service, no new GitHub REST call: every route these tools use was
  already there for the pages.

## UX pass over the phase 2 pages

A fresh-eyes review of the pages phase 2 added or changed, applied as one pass. It
changed wording and affordances only - no new tables, columns, endpoints or agent
surfaces.

**As built**

- **Release dialog.** A release-sourced pipeline whose repository has no release with an
  `.app` attached now says so and names the repository, instead of an empty picker with a
  disabled button. The repository name is optional throughout: with none, the "downloaded
  from ..." clause is dropped rather than saying "the repository". GitHub's refusals are
  worded as something to check ("the app is still installed for {repo}").
- **Publishing field.** `GitHubReleaseService.DescribeRepositoryOptionsAsync` is the
  richer read behind `ListRepositoryOptionsAsync` (which now delegates to it): it also
  reports whether the deployment has a GitHub App and whether the solution names any
  GitHub repository. That is what lets the pipeline editor tell a solution on GitHub whose
  organisation has not connected ("an admin can connect it under Administration →
  Repositories") apart from an organisation with no GitHub at all, which still sees
  nothing. A load failure is its own message.
- **Repository standards.** Save is the page's one primary action, as on the other admin
  forms; it is blocked, with a note, while the editor still holds a file nobody has put on
  the list. Edit focuses the path box and marks the row. File order controls nothing - the
  files go into one tree at unique paths - so Move up/down went away rather than acquiring
  a caption. The summary reads "your branch rules" so it fits "Every new repository gets
  ...".
- **Untracked repositories.** The vocabulary is solutions, not tracking: "N AL
  repositories have no solution yet", "Create a solution", "Hide". Hiding is undoable for
  the session through the new `RepositoryDiscoveryService.UnignoreAsync(fullName)`, named
  by repository because that is what the panel still holds once the row has gone.
- **Recipes.** The dialog is "Use this recipe" and both ways out sit in one footer row,
  with Download still the only primary; Enter follows whichever one is set up. The admin
  page asks before opening a pull request in every repository at once, and disables the
  buttons (rather than captioning them) while the form is dirty.
- **Translation memory.** The empty state is split: a memory nobody has filled offers the
  ways to fill it, a search that matched nothing offers Clear filters.

## Fences

- **Tenant isolation.** Every new table and column is per-org or per-user. No new
  `IgnoreQueryFilters()` call site; see "What phase 2 changes" above for the one ask.
- **Secrets.** One new key-ring-encrypted secret (the webhook secret), redacted in audit.
- **No new external dependency.** Releases, check runs, rulesets and webhooks are all
  hand-rolled REST on `HttpClient`, and the HMAC is `System.Security.Cryptography`.
- **Background work stays in-process** on the existing worker bases.
- **Network.** `api.github.com`, `github.com` and now `uploads.github.com` and
  `objects.githubusercontent.com` (Release assets) outbound; one inbound route.

## Out of scope

Retro-fitting standards onto existing repositories, auto-creating solutions without a
click, three-way merges of recipe files, multi-file translation batches, editing an
existing extension in a repository, and any MCP surface for the webhook flow.

Open item, deliberately not solved here: **pull-request builds are never pruned.** Every
push to an open pull request leaves a Release and an `OeProjectBuild` row behind, so a busy
repository accumulates them indefinitely; retention for those rows wants a policy an admin
can see and set, which is its own piece of work rather than a number picked here.
