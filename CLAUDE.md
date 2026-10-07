# CLAUDE.md

Guidance for working on this repository: the principles, fences, and conventions to obey in every session. The *map* of the project — folder layout, the design-doc contract and handoff porting guide, the AL/C-AL allow-list and MCP-parity maintenance guides, and the release process — lives in **`PROJECT.md`**. Before adding files, porting a design screen, editing the ported stylesheets, touching the reference extractor or the MCP tools, setting up a local run, or cutting a release, read the matching `PROJECT.md` section first.

## Project at a glance

- **AL Workbench** — internal Blazor Server tool covering a Business Central solution end to end: generating workspaces and extensions from runtime templates, exploring and translating source, building and shipping repositories, and operating the customer environments they land on.
- Stack: .NET 10, Blazor Server, EF Core 10 + Npgsql against PostgreSQL 18, Tomlyn. Lucide icons are vendored as embedded SVGs (no NuGet dependency); see `Resources/Icons/`.
- Two projects at the repo root: `ALDevToolbox/` (the app, layered by folder) and `ALDevToolbox.Tests/` (xUnit v3 + AwesomeAssertions). The solution file is `ALDevToolbox.slnx` at the repo root. `PROJECT.md` has the folder-by-folder map — match it when adding files.
- Source of truth for behaviour: documents under `.design/` (indexed in `PROJECT.md`). If code disagrees with the design doc, fix one of them — don't leave them out of sync.

## Development principles

### Keep code idiomatic C# / Blazor

- Nullable reference types and implicit usings are enabled. Don't disable them per-file.
- File-scoped namespaces. PascalCase for types/members, `_camelCase` for private fields, `camelCase` for parameters/locals.
- Records for immutable data shapes (plans, DTOs, mustache contexts). Classes for EF entities — EF needs settable properties.
- Constructor-injected dependencies stored as `private readonly`. We don't use primary constructors on services yet; stay consistent until we change them all at once.
- `async`/`await` end-to-end. Every `Task`-returning service method takes a `CancellationToken` and threads it through.
- Use `AsNoTracking()` on every read-only EF query. We've been disciplined about this so far.
- Prefer minimal LINQ over hand-rolled loops, but don't reach for tricks (no `Aggregate`-as-fold games) when a `foreach` is clearer.
- Use structured logging with named placeholders (`_logger.LogInformation("Generated {Workspace}…", plan.WorkspaceName)`), never string interpolation into the message template.

### DRY, but not prematurely

- Factor shared logic out the **second** time it's needed, not the first. The split between workspace and standalone generation reuses `WriteExtensionAsync` because both flows need the same per-extension layout — that's the bar.
- Don't introduce interfaces for services until there's a second implementation or a real test seam. `GenerationService` is a concrete class injected as itself; keep it that way until something forces the change. The one place that *has* cleared this bar is off-site storage: `IOffsiteStorageProvider` (in `Services/Offsite/`) has two real implementations — `S3Provider` and `AzureBlobProvider` — selected per request by `OffsiteStorageProviderFactory` from the `offsite_provider` setting. `OffsiteBackupService` owns all orchestration and only delegates raw transport to the provider; that's the sanctioned extension point for a third backend. This bar is about *premature abstraction*, not about slowing tool work down: when you're building a **new tool** or actively iterating one, add the components, services, and second variants that tool needs and keep moving — the "wait for the second caller" caution targets cross-cutting machinery, not the normal internals of one tool.
- Reusable UI is what `Components/Shared/` is for, and its inventory is real, not aspirational — `SettingRow`, `AuthCard`, `ConfirmDialog`, `DependencyPicker`, `AuditHistoryPanel`, `CodeViewer` and friends already exist. Check the folder before building a control a sibling page already has.
- Three similar lines is fine. A premature abstraction over two callers is worse than the duplication.

### Always have the end user in mind

- Every list page renders three states: loading, empty (with a useful message that tells the user how to recover), populated. See `TemplatesBrowser.razor` for the shape.
- Forms validate on the server (the source of truth) **and** mirror the rules in HTML attributes (`pattern`, `required`, `min`) so users get instant feedback. Keep the two in sync — the `maxlength=` on the workspace-name input matches `CustomerNaming.MaxLength`, and the GUID `pattern=` on the tenant-ID field matches the server-side check.
- Validation errors return field-keyed dictionaries via `PlanValidationException` so the UI can render them inline next to the field. Don't throw plain strings for things the user typed.
- Helpful copy beats clever copy. Captions under fields, placeholders that show real examples ("e.g. CRONUS A/S"), error messages that say what to do next.
- **Placeholder names and characters in user-facing copy follow a house style — match it, don't reintroduce the old one.** Use **CRONUS** (the standard Business Central demo company) for any placeholder customer/company/workspace name in placeholders, captions, and examples — never "Acme"/"ACME". The ban is on placeholder *company* names only: `ACME` as part of a protocol or identifier name (the ACME certificate protocol, the `ACME_EMAIL` variable Caddy reads) is correct and must not be renamed. For punctuation, use **ASCII** by default: straight quotes (`'` and `"`, not `'` `'` `"` `"`) and `...` rather than the `…` ellipsis character. The em-dash `—` and the arrow `→` (e.g. "Account → Repository tokens") are the two non-ASCII characters we *do* use. When you add a `.razor` page or any visible string, grep your new copy for `Acme`, `…`, and curly quotes before committing.
- **Help text is written for the user, not the maintainer.** Field captions and hints exist to help someone who doesn't know the codebase use the tool. Keep them short and plain. AL/BC domain terms the audience already knows are fine (`.app`, `.Source.zip`, DVD, codepage, country code); codebase-internal jargon is not — never surface implementation details like class or method names ("chain walker"), internal marker conventions (`_Exclude_`), serialised filenames, MCP tool names, or AL compiler flags by name (`IncludeSourceInSymbolFile`) in user-facing copy. If a caption is explaining *how the code works*, cut it down to *what the user needs to do*. The Import Release tabs (`Components/Pages/Admin/AdminReleasesImport*.razor`) are the worked example of this tone.
- Visual hierarchy: the **Generate** button is the only primary action on any page. Everything else is the outline button style. Don't introduce a second primary button.
- Keep the user's flow synchronous when it can be — generation runs in-process and streams the ZIP back. Don't add a job queue; if generation ever gets slow, fix the slow part.
- Loading states on long-running buttons (Generate, Export). Confirmation modals on destructive actions.

### Cohesion is not friendliness — the UX definition of done

Reusing the nearest existing component and CSS classes buys **cohesion** (the page looks like the rest of the app). It does **not** buy **usability** (a first-time user knows what to do). These are different properties and the second one is the one that's easy to skip, because it can't be pattern-matched — it requires picturing a specific person doing a specific task for the first time. The project detail page (`Components/Pages/Projects/ProjectDetail.razor`) is the cautionary example: as first shipped it obeyed every cohesion rule above, yet it reused the power-user ghost-row grid for a list of one to three repositories and explained a *mechanic* ("start typing in the blank row") instead of offering an obvious "Add repository" button. It has since been fixed — the lesson is the one that generalises: the nearest component is not the argument for using it.

So: a page that takes user input isn't done until each of these holds. State them in the PR description.

- [ ] **Named user.** Say who this is for and what they're doing, knowing nothing about our code (e.g. "a BC consultant registering their first customer"). Without a named user, copy defaults to the maintainer's mental model — that's where jargon comes from.
- [ ] **Primary action is obvious**, labelled with a verb the user would use ("Create customer", not "Save"), and there's still only one primary button on the page.
- [ ] **Empty / first-run state tells the user the next step** and gives them a button to take it — not a bare table or grid.
- [ ] **No mechanic needs explaining.** If a caption explains *how the UI works*, the UI is wrong, not the caption. Fix the affordance.
- [ ] **Jargon test passed.** Read every visible word as the named user. Any class/method name, env var, volume name, package or registry name (NuGet), compiler flag, internal marker, or filename convention = fail. (This is the help-text rule above, now mandatory and checked, not aspirational.)
- [ ] **Pattern fits the task,** justified by its shape and frequency — not by which component was nearest. A rarely-edited short list is not a power-user grid even if the grid exists.
- [ ] **Looked at it rendered** — a screenshot or a real run, not just the markup. Spacing, empty states, and button prominence don't show up in `.razor` source.

When you finish a user-facing page, run a fresh-eyes pass with the **`design-review`** subagent (`.claude/agents/design-review.md`): it reviews the rendered page as a newcomer with no implementation context, which is the only reliable way to catch jargon the implementer is blind to. Don't self-certify the jargon test — the person who wrote "downloaded from NuGet" knew what NuGet was. Trust its UX judgments; verify its claims about what the code does before acting on them — it reviews the rendered page without reading the implementation, and it is often wrong about mechanics.

### Solutions in the product, Project in the code

The customer engagement the tool is built around is called a **Solution** everywhere a
person or an agent can see it, and a **Project** everywhere else. This is deliberate, not
drift.

It got the new name because Microsoft renamed Jobs to "Projects" inside Business Central
itself, so to a BC consultant "project" now names an application area we have nothing to
do with. The rename was done while the tool was still staging-only, which is why the routes
and the MCP tool names could move too.

The line runs exactly here:

| Says Solution | Says Project |
| --- | --- |
| Visible copy — nav, headings, captions, empty states, validation messages | C# types, members and locals (`OeProject`, `ProjectId`, `ProjectAccess`, `ProjectConnectionService`) |
| Routes: `/solutions`, `/solutions/new`, `/solutions/{id}` | Tables and columns: `oe_projects`, `oe_project_*`, `project_id` |
| MCP tool names, their `[Description]` text, and their agent-facing parameter names | Private helper parameters and internal comments about the code |

The spine keeps the old name because renaming it would touch ~5,300 identifiers across 148
files and need a migration over 13 tables, for nothing a user or an agent would notice. So
when you add UI copy, write Solution; when you name a variable, write Project; and don't
"fix" one side to match the other. `/projects*` still redirects (`LegacyRedirectEndpoints`).

### AL Workbench in the product, ALDevToolbox in the plumbing

The product is called **AL Workbench** everywhere a person can read it, and keeps the
spelling `ALDevToolbox` / `aldevtoolbox` / `aldt` everywhere only a machine reads it. Same
split as Solution/Project above, and for the same reason: the visible name was wrong, and
the spine is not worth breaking to fix it.

It got the new name because the tool outgrew "Dev" — Environments, Upgrades, Pipelines and
Deliveries are operations work, not development — and because "Toolbox" sits one word away
from Erik Hougaard's ToolBox, a commercial Business Central product this audience already
knows. "Workbench" covers authoring and operating without naming either.

The line runs exactly here:

| Says AL Workbench | Keeps the old spelling |
| --- | --- |
| Visible copy — page titles, the brand in the shell, captions, empty states, validation messages, emails, docs pages | The C# namespace and assembly, `ALDevToolbox.dll`, the csproj and solution file |
| OCI image labels, `README.md` / `PROJECT.md` prose, `.design/` documents | The compose service `aldevtoolbox` and the Caddy upstream that resolves it by that name |
| The GitHub check-run name and the repository-standards ruleset name | In-container paths `/var/lib/aldevtoolbox/{dp-keys,backups,altool}` |
| The seeded ruleset description and README body in `PlatformOrganizationFiles` | `POSTGRES_USER` / `POSTGRES_DB` defaults. (`ALDEVTOOLBOX_TAG` is now `ALWORKBENCH_TAG`; the old name stays as a fallback in `compose.yaml` and must not be removed) |
| The app-owned stylesheets' header comments (`app.css`, `code-editor.css`, `source-viewer.css`) | `workspace.aldt.toml`, the `aldt` JS namespace, and the `ALDT` MCP server name |
| | Every Data Protection purpose string (`ALDevToolbox.UserTotpSecret`, `ALDevToolbox.EmailOutbox.Body`, the SMTP / off-site / Entra / GitHub secrets) |
| | The `aldevtoolbox-` backup filename prefix and the `aldevtoolbox/` off-site key prefix |
| | The byte-locked stylesheets' header comments, which must stay identical to `.design/handoff/` |

Three of those are frozen for reasons worth stating, because they look like leftovers:

- **A Data Protection purpose string is a decryption key in all but name.** Change one and
  every value encrypted under it - a user's TOTP seed, a queued email body, the SMTP
  password, the off-site and Entra and GitHub secrets - stops decrypting, with no error
  until something reaches for it. They all carry the `ALDevToolbox.` prefix, which is why
  the rename could not reach them; keep it that way. The `aldevtoolbox-` prefix on
  `pg_dump` filenames and the `aldevtoolbox/` key prefix off-site are the same shape of
  problem one level down: rename either and retention stops recognising the backups that
  are already there.
- **The in-container paths are load-bearing.** They are the defaults behind
  `DATA_PROTECTION_KEY_DIR`, `BACKUPS_DIR` and `AL_COMPILER_DIR`. Rename them and a stack
  running on defaults points at an empty directory on its next start: the Data Protection
  key ring reads as absent, every login cookie is invalidated, and the stored SMTP password
  can no longer be decrypted. That is the `app-keys` loss scenario, self-inflicted during
  an upgrade. The named volumes themselves (`pg-data`, `app-keys`, `app-backups`,
  `app-altool`) never carried the product name, so they need no story.
- **`workspace.aldt.toml` is in other people's repositories.** Every generated workspace
  carries one and `WorkspaceConfigService` reads it back. It can only change if the parser
  learns to accept both names, which is not worth doing for a filename nobody reads aloud.
  The `ALDT` MCP server name is the same shape of promise, made to colleagues' agent
  configuration instead.
- **The image path follows the repo, not a literal.** `release.yml` and `staging.yml` build
  it from `ghcr.io/${{ github.repository }}`, so renaming the GitHub repository moves the
  image on its own with no workflow edit. GHCR packages do not follow a repository rename:
  the old package keeps serving its existing tags and never receives new ones. The rename to
  `al-workbench` was bridged by mirroring one release (v11.9.1) to the old path until the
  deployments had moved; `PROJECT.md` has the account, including the order it had to happen in.

So when you write something a user reads, write AL Workbench; when you name a directory, a
service, a volume or a namespace, leave it alone; and don't "fix" one side to match the
other.

### Stay inside the architectural fences

These are deliberate constraints from `.design/architecture.md` and `.design/templates-and-seeding.md`. Don't quietly relax them.

- **The PostgreSQL database is the only persistence layer for templates, modules, the catalogue, per-folder file contents, organisations, users, signup requests, password reset tokens, login attempts, organisation settings, organisation assets, and organisation files.** Both authoring surfaces (the structured admin form and the TOML editor) write through the same `TemplateInput` pipeline into the DB. The on-disk `Templates.seed/` bootstrap was retired: the singleton **system org** (`organizations.is_system = true`, stamped on the Default org by migration `20260513000000_MoveSeedToSystemOrg`) holds the canonical templates that other orgs fork via `TemplateImportService`. New orgs start empty; admins import on demand from `/admin/templates`.
- **The ruleset, `.gitignore`, the README stub and the per-extension `app.json` are database rows, not embedded resources.** They ship as `organization_files` rows seeded from the canonical bodies in `Services/Organizations/PlatformOrganizationFiles.cs`, so an admin can curate them per organisation and each template opts in to the ones it wants. `Resources/` now holds only the vendored Lucide icons. Per-folder example AL file *contents* live in `workspace_extension_files` (migration `20260514000000_UnifyExtensions` collapsed the old `template_files` into it) and are admin-editable. The logo, organisation defaults block, and always-included file list live in the database (`organization_assets`, `organization_settings`, `organization_files`) and are admin-editable. Binary files inside template folders are out of scope for v1 — text content only.
- **`defaults_json` and `app_source_cop_json` stay as JSON columns.** Don't normalise them into separate tables — the AL ecosystem changes those shapes too often.
- **Multi-tenant by default.** Every editable entity carries an `organization_id`. EF query filters on `AppDbContext` scope reads to `IOrganizationContext.CurrentOrganizationId`; pre-login flows that genuinely need cross-org reads (login, signup, bootstrap) call `IgnoreQueryFilters()` explicitly. Service code that mutates state must run inside an authenticated request — `RequireOrganizationId()` throws otherwise. The `SINGLE_TENANT_MODE=1` env var (an immutable boot-time singleton, `ISingleTenantMode`) only *hides and disables* multi-tenant **surfaces** for internal single-org hosting — storage quotas, per-tenant snapshots, and self-service org creation at signup. It does **not** relax the tenant-isolation fence (the query filters and `IgnoreQueryFilters()` rules below still apply unchanged); see `.design/deployment.md`.
- **`IgnoreQueryFilters()` is the tenant-isolation fence.** The EF query filter is the *only* thing that keeps a request from one org's user reading another org's data. There are ~180 call sites, so a closed list of them is not maintainable — instead, every call site must fall into one of these **sanctioned categories** and satisfy that category's invariant:
  1. **Pre-auth routing** — login, signup (including the email-first pending-signup rows), password reset, magic link, invite acceptance, PAT/bearer validation, and the Microsoft sign-in half of `EntraSignInService` plus `AuthService.IsLocalLoginDisabledAsync`. *Invariant:* no cookie exists yet, and the read's job is to route one sign-in to exactly one organisation.
  2. **SiteAdmin cross-org console** — `/site-admin/*` and the services behind it. *Invariant:* the method calls `RequireSiteAdmin()` (or the page carries `[Authorize(Roles = SiteAdminRole)]`) **before** crossing the fence.
  3. **Startup and schedulers with no request org** — `StartupTasks`, seeders, `BackupScheduler`, the auto-import / delivery / environment-refresh schedulers, `UpgradeActionWorker`, background job bookkeeping. *Invariant:* there is no request in scope, and the code pins the org id it acts for (an `AmbientOrganizationScope` per org, or a predicate naming the row's own id).
  4. **Explicitly scoped org-id or user-id lookups derived from the authenticated principal** — e.g. reading `organization_settings` for the current org, or a user's own MFA/passkey/PAT rows. *Invariant:* the predicate literally names the id (`o.Id == orgId`, `u.Id == userId`, `x.OrganizationId == actingOrgId`) and that id comes from the authenticated principal or an already-pinned row, never from unvalidated user input.
  5. **System-org fork reads in `TemplateImportService`** — the acting org copies canonical templates out of the singleton system org. *Invariant:* every read is pinned to `OrganizationId == systemOrgId` (or to the acting org), and writes only ever land in the acting org.
  6. **Existence-only uniqueness probes** — `LinkAsync`'s `(provider, issuer, subject)` check and the passkey credential-id check. *Invariant:* the query projects a bool, never a row, so nothing about another org reaches the caller.

  Anything that is not one of those six is a bug. **Every call site carries a one-line justification comment naming its category and the predicate that pins it** — `ALDevToolbox.Tests/Tools/IgnoreQueryFiltersBaselineTests.cs` enforces both that comment and a per-file baseline count, so adding, moving or removing a call site is a deliberate act with a visible diff. **Never add a new `IgnoreQueryFilters()` call without explicit confirmation from the maintainer** — especially not inside an MCP tool, an admin service, an endpoint, or anything that runs under a normal authenticated request. If a query feels like it needs to escape the filter, the answer is almost always to scope it tighter, not to remove the fence. The same rule applies to constructing an `AmbientOrganizationContext` with someone else's org id from inside a request — don't.
- **Email/password accounts, three roles (`User`, `Editor`, `Admin`), admin-approved signups.** `User` uses the generator only; `Editor` additionally sees the content-authoring admin pages (templates, modules, catalogue, snippets, app versions, object explorer) but not the Administration tab, Dashboard, or audit log; `Admin` sees everything in the org. Bootstrap admin via `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` env vars, applied only on a fresh database.
- **Microsoft Entra ID is the one federated sign-in, opt-in per organisation.** Email/password stays the default and the fallback; an org Admin turns Microsoft sign-in on from Administration → Identity. **The per-org tenant allow-list (`organization_settings.entra_allowed_tenant_ids`) is the security boundary** — the app registration is multi-tenant, so any Microsoft work account produces a valid token and the allow-list is the only thing that keeps strangers out. An org can additionally set `local_login_policy = EntraOnly` to refuse password login, password resets, and magic links for its members; SiteAdmin password login always survives as break-glass, and passkeys keep working. No other IdP, no SAML, no group-to-role mapping — adding a second provider is a conversation first. See `.design/auth-and-audit.md`.
- **One app container, one db container, named volumes per concern.** From P4.16, the data layer is Postgres in a sibling compose service backed by the `pg-data` named volume. Three more app-side volumes carry persisted state: `app-keys` for the Data Protection key ring (M17), `app-backups` for `pg_dump` output (M18), and `app-altool` for the AL compiler provisioned at runtime for project builds. Off-site object storage (S3 / Azure Blob, via `IOffsiteStorageProvider`) is a *sanctioned, opt-in* extension point for backups — configured per deployment, not a runtime dependency of the app. The live rule isn't "no S3/Redis ever"; it's **don't add a *new* external infra dependency (a broker, a cache, a third datastore) without asking first**.
- **SiteAdmin is a separate, cross-org role from Admin.** SiteAdmin (M17) sees `/site-admin/*` regardless of which org they belong to; Admin (M13) is org-scoped to its own org. The bootstrap admin is stamped `IsSiteAdmin = true`; later promotions come from `/site-admin/users`. The "last SiteAdmin" guard refuses to demote the final one. Pre-login flows and the SiteAdmin console call `IgnoreQueryFilters()` explicitly — everywhere else, the EF query filter on `AppDbContext` scopes to `IOrganizationContext.CurrentOrganizationId`.
- **System settings are a singleton row.** SMTP overrides, the site banner, the signup email-domain allow-list, the backup schedule and retention all live on the single `system_settings` row, managed via `SystemSettingsService`. The SMTP password column is encrypted with the Data Protection key ring; losing `app-keys` requires re-entering it. The `/site-admin/settings` form is the only writer.
- **Three operator endpoints.** `/healthz` (M21) is 200 when the database is reachable *and* the Data Protection key ring round-trips; 503 otherwise. `/readyz` (M21) is only green once startup work (migrations + first-run seed + bootstrap admin) has finished — reverse proxies should gate traffic on it. `/healthz/workers` is 200 while every registered background worker is beating and 503 when one is stalled; it is deliberately separate from `/healthz` so a stalled background job never restarts the container — wire it to alerting. The Dockerfile `HEALTHCHECK` polls `/healthz`.
- **Generation is synchronous; the in-process background workers are the sanctioned async exception.** Workspace/extension generation is read-only against the DB and runs in memory — keep it that way (no queue, no job table). Everything heavier that legitimately runs off the request thread does so through an in-process channel-backed queue/worker pair, and they share three base classes in `Services/Workers/`: `JobQueue` (bounded channel, plus an optional key-based dedupe gate), `QueueDrainWorker` (the drain loop, heartbeat bracket and last-resort try/catch) and `PolledScheduler` (startup delay, poll loop, heartbeat and the `DISABLE_*` opt-out). The queues on those bases are `ReleaseImportQueue` (DVD, artifact and C/AL imports), `ProjectDiscoveryQueue`/`Worker`, `DeliveryQueue`/`Worker` (publishing builds to a BC environment — see `.design/saas-delivery.md`) and `Bc/EnvironmentRefreshQueue`/`Worker`; `OffsiteRestoreJobs` keeps its own channel because it tracks per-job progress rather than draining opaque jobs, and `ProjectBuildQueue` (clone/compile/ingest, entered via `ProjectBuildImporter.StartBuildAsync`) keeps its own reader because it hands builds out by priority and one per pipeline to `OE_BUILD_CONCURRENCY` `ProjectBuildWorker`s, which are still `QueueDrainWorker`s. A few consumers sweep a table instead of a channel where the work has to survive a deploy (`Bc/UpgradeActionWorker`); that's the same fence. All are in-process, so the "no external services" fence holds. New in-process background work goes on those bases — subclass `QueueDrainWorker` or `PolledScheduler` rather than copying an existing loop; standing up an **external** queue or broker (Redis, a cloud queue) is still a conversation first.
- **No client-side framework beyond Blazor itself.** No React, no JS bundler. Tiny `.razor.js` companion files (like `ReconnectModal.razor.js`) are fine when needed. The one larger script is the command palette's (`.design/command-palette.md`, "The exception this makes"): `MainLayout` is a static frame, so a palette that opens on every page cannot be a Blazor component without every static page holding a circuit for it. That is a recorded exception with stated limits (one file, no dependencies, no build step, no HTML strings), not a precedent for moving page behaviour into script.

If a milestone seems to demand crossing one of these lines, stop and confirm with the maintainer before doing it.

## Code conventions

These are the patterns the existing code has settled on. New code should match unless there's a reason to break.

### Services

- One class per service in `Services/`, registered as `Scoped` in `Program.cs`. EF context is scoped; services holding it must be too. `AuditService` is the first exception: it holds no context and opens a short-lived one per read through the scoped `IDbContextFactory<AppDbContext>`, because `AuditHistoryPanel` loads while the page around it may be saving and a `DbContext` allows one operation at a time (issue #741). `DisplayTimeZone` is the second, for the same reason: the `<Timestamp>` components it serves render in the middle of pages whose own queries may still be in flight (issue #942). `InAppNotificationService` is the third: the unread count in the top bar renders in the layout on every page, beside the page's own queries (issue #1043). A fourth service moving to the factory needs the same concurrent-read justification.
- Read methods return `Task<List<T>>` or `Task<T?>`. Write methods return `Task` and throw on validation failure (don't return result objects).
- Validation lives at the top of the service method, throws `PlanValidationException(Dictionary<string,string>)`. The form-layer validators are convenience; the service is the source of truth.
- Each service logs its outcomes at `Information` for successful operations with structured fields (workspace name, template key, file count, duration). Warnings for skippable problems (missing example folder); exceptions for refusals.

### Entities and value objects

- EF entities have public mutable properties because EF needs them. Initialise reference types to sane empty defaults (`= string.Empty`, `= new()`) so newly-constructed entities aren't `null`-laden.
- Value objects (`TemplateDefaults`, `AppSourceCopSettings`, etc.) are plain classes with `[JsonPropertyName]` annotations because they round-trip through `defaults_json` / `app_source_cop_json` and need to match AL's camelCase.
- Plans (`ProjectPlan`, `StandaloneExtensionPlan`, `DependencyEntry`) are `record`s — immutable, value equality, easy to compare in tests later.
- Soft-delete is `DeletedAt` (nullable). `Deprecated` is a separate boolean. They mean different things; don't conflate them. End-user dropdowns hide both; admin lists show deprecated and (with a toggle) deleted.

### Persistence

- All column and table names are snake_case, configured explicitly in `OnModelCreating`. Don't rely on EF's default naming.
- JSON value-object conversions use `HasConversion<JsonValueConverter>` with a single shared `JsonSerializerOptions`. Keep read and write options identical; otherwise round-trips drift.
- Indexes are declared in `OnModelCreating` (`(template_id, ordering)`, audit `(entity_type, entity_id, timestamp)`). Add new ones the same way.
- **A zero `idx_scan` does not mean an index is unused — check what fires it before dropping one.** An index whose only job is covering a foreign key's `ON DELETE SET NULL` or `RESTRICT` action reads as zero for as long as nobody deletes a parent row, and then does all its work at once. `ix_oe_module_system_references_source_symbol` is the worked example (#723): a lifetime zero in production, yet one release delete registers a scan per deleted symbol, and without it that delete exceeds the ten-minute command timeout `ReleaseManagementService` sets rather than merely slowing down. That zero turned out to have a second cause stacked on the first, and it is the one that will catch you out: `idx_scan` is keyed to the index OID, so dropping and recreating an index restarts its counter, and `pg_stat_user_tables` loses the old scans too because it sums only the indexes that still exist. Production had already run that cascade 580,160 times; migration `20260713000000` replaced the index afterwards and the evidence went with it, while the never-replaced sibling `ix_oe_module_references_source_symbol` still shows all 580,179 scans. So a zero means "unused since this index was created", which equals "unused" only if the index has never been replaced — compare its OID against its table's before reading anything into it. The same holds for every other `SET NULL` foreign key on the release-delete path (`oe_module_references` source/target symbol and variable, `oe_module_translations.symbol_id`, `oe_module_objects.source_file_id`). So before dropping a zero-scan index, find its readers **and** its referential actions; `Every_other_foreign_key_still_has_a_covering_index` in `OeFactTableIndexTests` is the guard that catches the second kind. The `organization_id` indexes #691 dropped were genuinely unused, which is why that one was safe — the difference is worth establishing each time, not assumed.
- Migrations are committed to the repo. Run `dotnet ef migrations add <Name>` for every schema change; never edit a migration after it's been merged.
- **Re-stamp the migration timestamp after generating it.** Our migrations are hand-dated *into the future* (the `YYYYMMDDHHMMSS` prefixes run ahead of the wall clock), but `dotnet ef migrations add` stamps the new one with today's real date — which sorts it *before* the existing migrations, so EF applies it out of order and it fails (e.g. dropping a column a "later" migration hasn't created yet). After adding a migration, rename it to a timestamp just after the current latest: update the prefix on both the `.cs` and `.Designer.cs` files **and** the id inside the `[Migration("…")]` attribute (EF orders by that attribute, not the filename). Check `ls Data/Migrations/` for the highest existing prefix first and pick one **strictly greater** — don't reuse the current max. Two PRs in flight that both branch off the same base will each grab "current max + one day" and collide on merge; a handful of merged prefixes already share a value (e.g. `20260730000000`) because of exactly this. Those collisions are harmless — the full `[Migration]` id (prefix **plus** the name suffix) is what EF orders by and what keys `__EFMigrationsHistory`, so a shared numeric prefix with distinct names still sorts deterministically and never edit a merged migration to "fix" it — but they make the next author's "what's the highest prefix" ambiguous, so when you see a duplicate, step your new prefix clearly above the whole group. The mirror image of that hazard is a prefix that lands *below* an already-merged one, because the branch was cut before the higher one merged (`20260806000000_OptionalRecipeDownloadCustomer` and its three neighbours sit under `20260818000000_AddEntraIdentity` for exactly that reason): a fresh database then applies the pair in the opposite order to the databases that were already live, and renumbering after the fact would only break the installs that have already recorded the old id. Leave those alone as well, and write each migration so it doesn't depend on anything it didn't branch from — then either order works. The model snapshot is order-independent, so it doesn't need touching.
- **`dotnet ef migrations remove` is retired in this repo — use `scripts/redo-migration.sh <Name>` instead.** `remove` rebuilds `AppDbContextModelSnapshot.cs` from the *now-last* migration's `Designer.cs`, assuming each Designer holds the cumulative model as of that migration. The hand-dated prefixes above break that assumption: whenever two PRs off one base land out of prefix order, the last Designer by id is missing whatever merged "before" it, so `remove` installs an incomplete snapshot and the next `add` re-emits every table and column in the gap. The result applies cleanly to your machine and fails on a fresh database with `column … already exists`, naming something you never touched — and since `TestDb` migrates a fresh database per test class, it takes the whole suite with it (#794 has the worked example, found the hard way in #790). The script does the safe thing instead: restore the snapshot from `origin/main`, delete the migration, `add` it again, re-stamp the prefix, and verify with `has-pending-model-changes`. It refuses to touch a migration that is already on `main`. Do **not** repair the stale `Designer.cs` files; they are a normal consequence of merging and will reappear.
- Startup runs `MigrateAsync()` and ensures the Default org exists with `IsSystem = true` (it's the singleton system org other orgs fork from). Both steps must remain idempotent — assume the app restarts often.

### Pages and components

- One page per route file. `@page` directive at the top, `@inject` services, `@code` block at the bottom for state and lifecycle.
- Hydrate state in `OnInitializedAsync`. Render `Loading…` / empty / data states explicitly — don't render an empty grid when the data is `null`.
- **A new page composes a frame from `Components/Shared/Archetypes/`; it is never started from a copy of another page's markup.** Pick the one whose design sheet the page is (`ListPage`, `DetailPage`, `EditPage`, `GeneratorPage`, `LauncherPage`, `DocsPage`, `ErrorPage`, or `PageHead` alone over a body of its own), and inside a tabbed frame (`SettingsPage`, `TabbedPage`, a section header) use the `EmptyState` and `LoadingBlock` primitives directly. The frames make the states structural, so a page cannot forget its empty state or grow a second primary button by accident. `ALDevToolbox.Tests/Components/ArchetypeConformanceTests.cs` enforces it twice over: no file outside that folder may hand-write a `page-head`, and every routable page that draws anything must compose a frame. Both carry a short list of exceptions with the reason beside each (the power tools' `.pw` frame, two detail pages whose heads hold more than a title) - add to them only with a reason of the same kind, and if a frame is missing something a second page needs, give the frame a slot rather than writing round it. `PROJECT.md`, "Page archetypes", has what each frame is for.
- For form posts that return file downloads (Generate), use a minimal API endpoint in `Program.cs` rather than a Blazor component event — `FileStreamResult` with `Content-Disposition: attachment` is simpler than wrestling with `IJSRuntime` downloads. Always validate antiforgery first.
- CSS layers, in load order: `tokens.css` (the design system's token contract, byte-locked to the handoff copy), `components.css` / `shell.css` / `pages-*.css` (ported archetype sheets, also byte-locked — see the handoff section of `PROJECT.md` before editing any of them), then `app.css` for app-specific global rules and `Component.razor.css` for component-scoped styles. Tokens only — if you need a new colour, it goes through the design project, not a raw hex in a page sheet.
- Icons: Lucide SVGs vendored under `Resources/Icons/`, rendered inline by `Components/Shared/Icon.razor` via the singleton `IconCatalog`. No mixing icon families. The same icon name is used for the same concept across pages (e.g. `folder-plus` for "create workspace"). To add an icon, drop the SVG from lucide.dev (at the pinned version in `Resources/Icons/VERSION.txt`) into that folder — the csproj globs `*.svg` as embedded resources. A missing icon logs a warning and renders an invisible placeholder rather than throwing, but the catalogue test will fail the build if any call site references an icon that hasn't been vendored.

### Comments and docs

- XML `///` comments on public service methods, public entity properties whose meaning isn't obvious from the name, and tricky private helpers (mustache substitution, ID-range allocation). Explain *why* and *what's surprising*, not *what the code does*.
- Reference `.design/*.md` documents from code comments when behaviour is specified there — keeps maintainers from reverse-engineering decisions.
- Don't restate the design docs inside CLAUDE.md, code comments, or commit messages. Link, don't copy.

## Tests and verification

Milestone 12 stood up `ALDevToolbox.Tests/` and backfilled tests for the tricky algorithms — ID-range allocation, mustache substitution, audit snapshots, TOML round-trip, and the `PlanValidationException` field-key contract. Milestone P4.16 swapped the in-memory SQLite fixture for a real Postgres host (Testcontainers locally; service container in CI). Patterns are documented in `ALDevToolbox.Tests/README.md`.

The bar from M13 onward: every service method added ships with tests for the happy path and for any validation rule it introduces. Not a coverage metric — a posture. If the code has a rule, the rule has a test.

- `dotnet test` runs locally (no flags needed) and is part of CI (`.github/workflows/build.yml`). A red test run fails the build the same way a red compile does. xUnit v3 runs on Microsoft.Testing.Platform, so the old VSTest flags (`--filter "FullyQualifiedName~X"`, `--logger`) now exit 5 having run nothing — `ALDevToolbox.Tests/README.md` has the replacements.
- Verify generation by building a workspace, extracting the ZIP, and opening it in VS Code with the AL extension. The output structure must match `generation-engine.md`.
- Manual smoke test the end-user flows after touching shared services (generation, seed). Click through New Workspace, New Extension, Templates Browser.
- Local Docker run (`docker compose up`) before merging anything that touches startup, env vars, or volumes.

When picking which tests to add for a new feature, prefer tests that go through the public API (the service method, the endpoint, the round-trip) over tests that reach into private helpers. Internals will refactor; the contract shouldn't.

## Pull request hygiene

- One milestone per PR (or one coherent slice of one). Don't roll three milestones into a single review.
- Name branches with a type prefix that fits the work, slash-separated from a short kebab-case description: `feat/translator-xliff-editor`, `fix/audit-diff-empty-state`, `chore/bump-npgsql`, `docs/release-flow`, `refactor/generation-service`, `test/id-range-allocation`, `ci/ghcr-release`, `perf/object-explorer-ingest`. Use `feat` for a new user-visible capability, `fix` for a bug, `chore` for deps/tooling/housekeeping, `docs` for docs-only, `refactor` for behaviour-preserving restructuring, `test` for test-only work, `ci` for workflow/pipeline changes, `perf` for performance work. Pick the one that best describes the change; when a branch spans a couple, name it for the primary one.
- PR title: short, present tense ("Milestone 4: live preview"). Body: what changed, what was deliberately left out, how to verify.
- Commit messages explain *why*. The diff already shows *what*.
- Debug captures, screenshots and profiling pages go in a scratch directory outside the repository, never under `ALDevToolbox/wwwroot/`: everything there is served publicly, and #1015 merged three 36,000-line captures that way. Check `git status` before committing rather than staging everything. `StrayWwwrootFileTests` catches the common shapes.
- If you change `.design/`, call it out in the PR body — design changes deserve review attention, not just the code.
- We squash-merge, so a merged branch shares no commit ancestry with main — `git log main..branch` will look "ahead" even when the content already landed. After a PR merges, that branch is done: start follow-up work from a fresh branch off main, never push new commits onto an already-merged branch. (The repo has *auto-delete head branches* on to enforce this.)
- Auditing whether a stray branch is unmerged means comparing *content*, not commits — check whether main already contains the equivalent change, since the squash drops the original SHAs.
- The squash rule is for PR-sized branches landing on main, and the `protect-main` ruleset enforces it (squash-only, linear history). A long-lived integration branch is the one place merge commits appear: merges *from* main into such a branch are merge commits, because that ancestry stops the same files re-conflicting on every subsequent merge (#595 is the worked example). The branch still *lands* on main as a squash (#596); the per-PR history stays readable through the landing PR's commit list.
- Releases are cut by tagging main — the process, version scheme, and staging previews are in `PROJECT.md`.

## When in doubt

- Smaller is better. The "Deliberately small" list at the bottom of `completed-milestones.md` is the tie-breaker.
- If you're about to add a feature flag, an interface, a queue, or a config knob "for the future" — don't. Add it when the future arrives. (This guards against *speculative cross-cutting* machinery. Building or iterating a tool, add the components, services, and variants that tool needs without ceremony — momentum inside a tool isn't what this is about.)
- Ask before crossing one of the **safety fences** (tenant isolation / `IgnoreQueryFilters()`, secrets and the Data Protection key ring, migration discipline) and before introducing a **new external dependency**. You do *not* need to ask to add a second variant *within* a tool you're building or iterating — prefer momentum there. Reserve the ask for changes that ripple across tools or touch a safety fence.
