# PROJECT.md

The map of the project: where things live, how the design docs and the design
handoff work, the domain-specific maintenance guides, and how releases are cut.
`CLAUDE.md` is the companion file about *how we build* — principles, fences,
conventions — and is read every session; this file is read when a task touches
one of its sections.

## Where things live

App folders are relative to `ALDevToolbox/`.

| Folder                       | What goes there                                                              |
|------------------------------|------------------------------------------------------------------------------|
| `Components/Pages/`          | Routable pages (one `.razor` per route). A tool with more than a page or two gets its own subfolder — `Pages/Upgrades/` is the Business Central platform-update fleet page. |
| `Components/Layout/`         | Shell layout, sidebar, top bar, reconnect modal.                             |
| `Components/Email/`          | The emails: `EmailLayout`, `EmailParagraph`, `EmailButton`, `EmailCode`, the `EmailTheme` token values, one component per email, and `EmailPreviews` (sample data for the SiteAdmin preview tab). Rendered by `Services/Email/`, never routed. See `.design/email.md`. |
| `Components/Shared/`         | Reusable components (`SettingRow`, `AuthCard`, `ConfirmDialog`, `DependencyPicker`, `AuditHistoryPanel`, `EnvironmentActivityFeed`, ...). Check here before building a new control. |
| `Components/Shared/Archetypes/` | The page frame, one component per handoff structure: `PageHead`, `EmptyState`, `LoadingBlock`, `FilterBar`, the page frames `ListPage`, `DetailPage`, `EditPage`, `GeneratorPage` and `LauncherPage`, plus the content archetypes `DocsPage`, `SetupSteps` / `SetupStep` and `ErrorPage`. Pages compose these instead of copying the markup — see "Page archetypes" below. |
| `Endpoints/`                 | Minimal-API endpoint groups (`AccountEndpoints`, `GenerationEndpoints`, `SiteAdminEndpoints`, …) registered from `Program.cs` via `Map*Endpoints()` extensions. |
| `Startup/`                   | Service-registration groups, one file per area (`AddObjectExplorer`, `AddAccountServices`, `AddMcp`, `AddBackgroundWorkers`, …), called from `Program.cs` as `builder.Services.AddX()`. A new registration goes into the matching `Add*` method, not back into `Program.cs`. |
| `Services/`                  | Only the cross-cutting primitives every folder below reaches for: the tenancy context (`IOrganizationContext`, `HttpOrganizationContext`, `AmbientOrganizationScope`, `OrganizationContextExtensions`), auditing (`AuditService`, `AuditActor`, `AuditAnonymization`), `EmailService`/`EmailContent`/`EmailAddress` and the send-with-retries outbox beside it (`EmailOutbox`, `OutboxEmailService`, `EmailOutboxScheduler`), `DashboardService`, and the small helpers (`DbErrors`, `IconCatalog`, `MarkdownRenderer`, `GridPasteParser`, `PiperTransform`, `CacheBust`). Anything belonging to one tool or one concern goes in a subfolder — a file only earns the top level once three unrelated folders use it. |
| `Services/Email/`            | Rendering emails: `EmailRenderer` (Razor components to HTML) and `EmailPlainText` (the plain-text part from that HTML). Sending stays at the top level. |
| `Services/Notifications/`    | Notifications, in the app and by email: `NotificationPreferenceService` (each person's choice per category), `NotificationService` (list in the app, email now, keep for a digest, or drop), `InAppNotificationService` (the bell count and the Notifications page), the notifiers that decide who hears about what (`BuildNotifier`, `DeploymentNotifier`, `UpgradeActionNotifier`, `UpgradeCheckNotifier`, `EnvironmentUpdateNotifier`), and `NotificationDigestService` with its `NotificationDigestScheduler` (digests, and the 30-day cleanup). Who follows a solution is `ProjectFollowService`, in `Services/ObjectExplorer/Projects/`. See `.design/notifications.md`. |
| `Services/Account/`          | Sign-in, accounts and user administration: `AuthService`, `AccountService`, `EntraSignInService`, `PasskeyService`, `EmailMfaService`, the invite flow (`InviteService`, `TokenIssuer`), the org-scoped `UserAdministrationService` and its cross-org sibling `SiteAdminService`, and `LoginAttemptPruneScheduler`. |
| `Services/Al/` and `Services/Cal/` | The AL and C/AL source parsers and reference extractors (see the extractor guides in this file). |
| `Services/Generation/`       | The generation core: `GenerationService` (the entry point every surface calls), `WorkspaceZipBuilder`, `MustacheRenderer`, `IdRangeAllocator`, `EmittableExtension`, and `WorkspaceConfigService` (the `workspace.aldt.toml` round-trip). |
| `Services/Templates/`        | The authoring content the generator reads: templates (`TemplateService`, `TemplateImportService`, `TemplateAuthoring`/`Mapper`, `TemplateDefaultsResolver`, `TemplateTomlMapper`, `TemplateValidation`, `TomlParseException`), modules (`ModuleService`, `ModuleAuthoringMapper`), the dependency catalogue (`CatalogService`) and application versions (`ApplicationVersionService`), plus the shared `FolderTreeHydrator`. |
| `Services/Organizations/`    | Per-organisation configuration and administration. The configuration is split by concern across four siblings: `OrganizationConfigService` (cached read model + the settings writes), `OrganizationBrandingService` (name and logo), `RepositoryProviderPolicyService` (the source-control provider allow-list) and `OrganizationConfigTomlImporter` (the wipe-and-replace TOML restore) and its counterpart `ExportService` (the TOML snapshot ZIP it restores from). Also here: `OrganizationAdminService`, `TeamService`, the platform file bodies and their seeder (`PlatformOrganizationFiles`, `PlatformOrganizationFileSeeder`), and the storage footprint (`TenantTableCatalog`, `DatabaseUsageService`, `UsageSnapshotScheduler`, `StorageQuotaGuard`). |
| `Services/ObjectExplorer/`   | By far the largest subsystem, split one folder per tool (below). Only the pieces every one of them uses stay at the folder root: the shared read DTOs (`ObjectExplorerDtos`, `ObjectExplorerCompareDtos`), `ProjectAccess` (the visibility gate every query asks) and `IProcessRunner`/`ProcessRunner` (also used from `Services/BcQuality/`). |
| `Services/ObjectExplorer/Import/` | Getting releases and modules *in*: the upload/queue/worker chain (`ReleaseImportQueue` for release imports and `ProjectBuildQueue`/`ProjectBuildWorker` for pipeline and pull-request builds, which run side by side up to the site's build limit; `ReleaseImportRequestService` holds the upload form's policy — which ingest path a submission takes, what is staged to disk, and what goes on the queue — so the endpoints only read the form and redirect on the outcome), the `.app`/DVD/artifact readers (`AppPackageReader`, `FolderZipWalker`, `DvdDownloadService`, `BcArtifactService` with `BcArtifactIndex` and `BcVersionComparer`, and `BcArtifactCache`, which keeps downloaded artifacts on disk between builds), `BuildResourceMonitor` (warns when running builds are short of processor or memory), C/AL and translation ingest, and the release lifecycle that follows (`ReleaseManagementService`, `ObjectExplorerVacuumScheduler`). |
| `Services/ObjectExplorer/Explore/` | Reading back what was ingested: the object/module/release queries (`ObjectExplorerService`, `ExplorerTreeService`, `ObjectSearchService`), the source viewer, the reference lookups (`ReferenceQueryService`, `ReferenceResolver`, `ReferenceSessionService`), release comparison, and `SourceVisibility`/`ObjectExplorerLinks`. |
| `Services/ObjectExplorer/Projects/` | Solutions and their repositories: `ProjectService`, discovery (`ProjectDiscoveryService`/`Queue`/`Worker`), builds (`ProjectBuildService`, `ProjectBuildImporter`, `AlCompilerProvisioner`, `AlSymbolFeedResolver`, `AlcOutputParser`) and the build artifacts (`ArtifactService`). |
| `Services/ObjectExplorer/Delivery/` | Pipelines and shipping their output: `PipelineService`, `ReleasePipelineService`, and the delivery chain that deploys a build to a BC environment (`DeliveryService`, `DeliveryQueue`/`Scheduler`/`Worker`). |
| `Services/ObjectExplorer/Bc/`| Everything that talks to a customer's Business Central tenant: the Admin Center clients, `ProjectConnectionService`, and the Upgrades services (`UpgradeFleetService`, `UpgradeActionService`, `UpgradeActionWorker`, `EnvironmentRefreshScheduler`/`Queue`/`Worker`). The `Bc`-prefixed artifact trio lives in `Import/`, not here, and stays there (#795): the line this folder draws is *whose* Business Central you are talking to. These three fetch Microsoft's public artifact feeds to ingest a release; nothing in `Bc/` is reachable without a customer tenant's credentials. |
| `Services/Translation/`      | Translator services: translation memory, machine-translation providers and their per-organisation settings (`MachineTranslationSettingsService`), suggestion coordination. |
| `Services/Mcp/`              | MCP tool implementations and their DTOs (see the MCP-parity guide below).    |
| `Services/OAuth/`            | The MCP OAuth surface: client resolution, claims transformation, bearer policy. |
| `Services/Offsite/`          | `IOffsiteStorageProvider` and its S3 / Azure Blob implementations, plus `OffsiteStorageProviderFactory`. Raw transport only — the orchestration on top of it lives in `Services/Backups/`. |
| `Services/Backups/`          | Backing the database up and putting it back: `BackupService` (the `pg_dump` / restore), `BackupScheduler`, `BackupCoordination`, the per-tenant snapshots (`PerTenantBackupService`, `PerTenantBackupJson`) and the off-site orchestration (`OffsiteBackupService`, `OffsiteRestoreJobs`) that delegates transport to `Services/Offsite/`. |
| `Services/GitHub/`           | The GitHub App integration: `GitHubAppClient` (REST, the App JWT and the user-to-server token exchange), `GitHubConnectionService` (the per-organisation connection), `GitHubAccessService` (the per-user account link and the access checks every feature asks), `GitHubRepositoryService` (the shared repository resolver every caller routes through); phase 2 adds the feature services on top - `GitHubRecipeDeliveryService`, `GitHubReleaseService`, `GitHubRepositoryStandardsService`, `RepositoryDiscoveryService` (+ scheduler), `DependencyDriftService`, and the pull-request compile gate (`GitHubWebhookQueue`, `GitHubPullRequestBuildWorker`, `GitHubCheckRunService`) - with `GitHubAppClient` split into per-feature partial files. |
| `Services/Operations/`       | Running-instance concerns: the health checks behind `/healthz`, `/readyz` and `/healthz/workers` (`DatabaseHealthCheck`, `DataProtectionHealthCheck`, `StartupReadinessHealthCheck`/`State`, `BackgroundWorkerHealthCheck`), `MaintenanceModeState`, `BuildInfo`, `DeploymentIdentity`, and the singleton `SystemSettingsService`. |
| `Services/Workers/`          | The in-process background-work base classes every queue and scheduler subclasses (`JobQueue`, `QueueDrainWorker`, `PolledScheduler`), the `WorkerHeartbeat` liveness record they tick, and the small pieces jobs running side by side share: `KeyedGate` (one at a time per key), `InUseLeases` (keeps files a job is using from being pruned) and `SweptOrganizations` (the organisations a per-org sweep visits). |
| `Services/Configuration/`    | Deployment configuration read once at startup (`BackupOptions`, `SmtpFallbackOptions`, `AlCompilerOptions`, `AlSymbolFeedOptions`) and passed to the services that need it, rather than each service reaching into the process environment. The env var names stay the operator-facing interface. |
| `Services/BcQuality/`, `Services/Cookbook/`, `Services/Diff/`, `Services/SingleTenant/`, `Services/Tools/` | One folder per remaining tool or cross-cutting concern. |
| `Domain/Entities/`           | EF Core entity classes (mutable, persisted).                                 |
| `Domain/ValueObjects/`       | Immutable records / JSON-mapped value objects, exceptions, plans.            |
| `Domain/Seed/`               | Tomlyn POCOs that mirror the TOML schema for the admin editor and export.   |
| `Data/`                      | `AppDbContext`, design-time factory, migrations.                             |
| `Data/Configurations/`       | Per-entity `IEntityTypeConfiguration<T>` classes (one file per entity).      |
| `Resources/Icons/`           | Vendored Lucide SVGs, embedded and rendered inline by `Components/Shared/Icon.razor`. This is all `Resources/` holds now — the ruleset and `.gitignore` moved into `organization_files` rows. |
| `wwwroot/`                   | Global CSS (the token/archetype sheets - byte-locked, see the handoff section - plus `app.css`), favicon. |

Test folders are relative to `ALDevToolbox.Tests/`.

There is one folder per subsystem, mirroring the app's own layering. The full list, so you
can pick the right existing bucket instead of inventing a near-duplicate:

| Group                | Folders                                                                    |
|----------------------|----------------------------------------------------------------------------|
| Plumbing             | `Builders/` (entity / plan builders with sane defaults), `Infrastructure/` (`TestDb` — the Testcontainers / service-container Postgres fixture), `Fixtures/` (sample data files) |
| Generation           | `Generation/`, `Templates/`, `Extensions/`, `Catalogue/`, `Toml/`, `Configuration/`, `Validation/` |
| Accounts and tenancy | `Auth/`, `Account/`, `OAuth/`, `Teams/`, `SiteAdmin/`, `Admin/`, `Audit/`, `Schema/` (tenant-filter and data-integrity invariants) |
| Object Explorer      | `ObjectExplorer/`, `Al/`, `Cal/`, `Diff/`                                   |
| Translator           | `Translator/` (memory, suggestions, XLIFF writing), `Translations/` (XLIFF parsing and import), `Translation/` (machine-translation providers) |
| Other tools          | `Cookbook/`, `BcQuality/`, `Mcp/`, `Tools/`, `Dashboard/`, `GitHub/`        |
| UI and shell         | `Components/`, `Assets/` (stylesheet and rendered-markup invariants), `Icons/`, `Routing/`, `Endpoints/` |
| Operations           | `Migrations/`, `Storage/`, `Services/` (`BuildInfo`, `WorkerHeartbeat`), `Piper/` |

The three Translat* folders are a wart, not a pattern to copy: put new Translator tests in the
folder whose existing tests they sit closest to.

When you add a new file, match the folder. Resist creating top-level folders — the layered split is intentional. Test patterns are documented in `ALDevToolbox.Tests/README.md`; new service tests should follow them.

## Working with the design docs

`.design/` is the spec. Treat it as the contract:

- `architecture.md` — stack and layering decisions, request flow.
- `domain-model.md` — the **generator core** of the schema: templates, unified extensions, modules, the catalogue, organisations and accounts, audit. It is not a full data dictionary — the tools that came later document their own tables in their own docs (see the subsystem table below), and `AppDbContext` is the authoritative list either way.
- `generation-engine.md` — what the ZIP must look like and how to build it.
- `customer-naming.md` — how the customer name typed on New Workspace becomes the folder, repository, extension and Solution names, and how Create repository registers the Solution.
- `templates-and-seeding.md` — TOML schema and the seed contract.
- `auth-and-audit.md` — how the password gate and audit interceptor work.
- `teams-and-visibility.md` — teams, their managers, and the per-project visibility model they grant.
- `saas-delivery.md` — publishing a build to a Business Central SaaS environment: the BC connection, deployment pipelines (the `OeReleasePipeline` entity), deployments (`OeProjectDelivery`), and the two update windows.
- `environment-updates.md` — the Upgrades fleet page: the team-scoped grant, the mirrored next platform update, the two date writes, and the actions-and-history table behind them.
- `ui-design.md` — page layout, copy, components to factor out.
- `bcquality.md` — the mirrored BCQuality knowledge base: ingest, schema, refresh policy, and the two MCP tools over it.
- `github-integration.md` — the GitHub App: which credential acts (installation vs the user's link), the schema, and the four features built on them.
- `github-integration-phase2.md` — phase 2 of the GitHub integration (#626-#633): repository discovery, repository standards, recipe delivery, Releases, the pull-request compile gate, translation-memory ingest, dependency drift, and the MCP twins.
- `completed-milestones.md` — the record of what each shipped milestone added (M1–M21).
- `roadmap.md` — uncommitted forward-looking ideas (successor to the old `milestones.md` plan).

### Which doc covers which subsystem

`AppDbContext` holds far more than the generator core. When you need the data model for a
subsystem, start here rather than in `domain-model.md`:

| Subsystem (entity prefix / table prefix)                              | Doc                                                        |
|-----------------------------------------------------------------------|------------------------------------------------------------|
| Templates, unified extensions, modules, catalogue (`runtime_templates`, `workspace_extension_*`, `module_extension_*`) | `domain-model.md`, `unified-extensions.md`, `templates-and-seeding.md` |
| Organisations, users, signups, sessions, audit                        | `domain-model.md`, `auth-and-audit.md`                      |
| Teams and per-project visibility                                      | `teams-and-visibility.md`                                   |
| Object Explorer: releases, modules, objects, symbols (`oe_*`)          | `object-explorer.md`                                        |
| Projects, repos, pipeline builds (`oe_project_*`)                      | `object-explorer-project-builds.md`                         |
| Deployments and deployment pipelines                                   | `saas-delivery.md`                                          |
| BC environments, upgrade actions, the fleet page                       | `environment-updates.md`                                    |
| Translations and the translation memory (`oe_module_translations`, memory tables) | `object-explorer.md` (the translations section)  |
| Cookbook recipes                                                       | `cookbook.md`                                               |
| BCQuality mirror                                                       | `bcquality.md`                                              |
| MCP OAuth clients, tokens, grants                                      | `mcp-oauth.md`                                              |
| System settings, backups, off-site storage                             | `deployment.md`                                             |
| GitHub App installation, per-user account link, repository writes      | `github-integration.md`                                     |

When implementing a milestone:

1. Re-read the relevant design docs first.
2. If the design says something the code can't easily satisfy, write the question into the PR description and pause for input rather than improvising.
3. If a design choice has aged badly, update the design doc in the same PR as the code change — don't leave the doc claiming something the code no longer does.

### Implementing a Claude Design handoff

A Claude Design handoff (the prototype HTML/CSS/JS a `.design/*.md` points at — e.g. the screens named in `artifacts.md`) is a **visual spec to translate, not a codebase to port**. The prototype is vanilla JS building DOM with its own self-contained CSS; recreate it as idiomatic Blazor — but be *faithful to the pixels* while you re-express the *implementation*. The failure mode is letting "idiomatic Blazor / adapt to our data" become an excuse to silently drop visual detail that was right there in the handoff.

- **Translate, don't transliterate.** Re-express structure through our components and conventions (`BuildStatusPill`, `.ra__menu` kebabs, `.btn--*`, scoped CSS), but treat the prototype's visual details — what's in each cell, the styled controls, per-row affordances, spacing — as the spec to preserve, not to re-derive.
- **Port the prototype's component CSS near-verbatim onto our tokens.** Its rules are usually good; bring them over swapping its private system for ours (`--bad`→`--danger`, `.btn.sm`→`.btn--sm`) rather than re-writing thinner versions. Drop only its canvas scaffolding (`.frame`, the side-by-side light/dark frames) — the app already themes via `data-theme`. Note there is no global `.input` class: a styled input/select needs the scoped rules (see `.cb-search .input`), not a bare class.
- **Diff against the *rendered* prototype early, cell by cell.** Screenshot your page next to the handoff's screenshots and ask "what's in their cell that's missing in mine?" — checking only that yours looks internally cohesive is how details slip.
- **Every data-driven omission is a flag, not a default.** "Prototype shows X, our DTO lacks X" → wire it through or call it out in the PR; never silently drop it.
- Reuse the existing token system and components; don't stand up a parallel one just because the prototype ships its own.
- **The ported sheets are byte-locked.** `StylesheetLoadOrderTests` asserts that `wwwroot/tokens.css`, `components.css`, `shell.css` and the `pages-*.css` family match their `.design/handoff/` copies byte for byte. Never edit just one copy — the test fails, and worse, silence means drift. The durable path for a new rule is: push it to the Claude Design project (the id is in `.design/handoff/README.md`; use DesignSync), then land the identical change in both checked-in copies. Patching both copies locally is an acceptable stopgap inside one PR, but say so in the PR body so the upstream push isn't forgotten.

### Page archetypes

The frame of a page — head, filter row, loading and empty states — is the same handoff markup on every page, and it used to be copied onto every page. Nothing compared the copies, so they drifted: the Upgrades page reproduced the Environments filter bar with one wrapper different and shipped with its Search button wrapped under the box (#805). `Components/Shared/Archetypes/` holds that markup once.

- **New pages compose an archetype component; they don't copy the markup.** `PageHead` (title, subtitle, crumbs, actions, optional sticky), `EmptyState` (icon, title, text, action), `LoadingBlock`, and `FilterBar` (search, filters, trailing) are the shared primitives. The per-archetype components of the "Page archetype components" milestone build on them.
- **Composition, not inheritance.** The components take `RenderFragment` slots. There is no `@inherits` base class for markup, the same way `SettingsPage` / `TabbedPage` already work for archetype 7.
- **They render the handoff's classes exactly and add nothing to the byte-locked sheets.** Each component's header comment names the handoff sheet it mirrors; `ArchetypePrimitivesTests` pins class names and slot order. If a structure needs a rule the handoff lacks, that goes through the design project like any other CSS change (see above).
- **An archetype with one consumer does not get a component.** `LauncherPage` (archetype 1) exists because `Home` renders the launcher twice, signed in and signed out, and the two copies had to be kept in step tile by tile. The dashboard (archetype 4) has exactly one page, `AdminDashboard` - there is no site-admin landing - so it composes `PageHead` and `EmptyState` and keeps its cue grid and activity lists as its own markup. Build `DashboardPage` when a second dashboard arrives, from the two real pages rather than from a guess.
- **The content archetypes (12 to 14).** `DocsPage` owns the docs frame and builds the "On this page" list from `DocsTocEntry` data, so a table of contents cannot drift from a hand-written `<nav>`. `ErrorPage` is the full-page error state in the handoff's slot order, and injects nothing because `/Error` renders after something has already failed (#559). Archetype 13 is `SetupSteps` / `SetupStep`, a list rather than a page: its two consumers (the MCP page and the GitHub app settings card) sit in different frames and share only the steps. `Components/Shared/CopyButton.razor` is the copy-to-clipboard button for text that is on the page; pages copying a value held in component state keep their own `@onclick` button.
- **The power tools (archetypes 9 to 11) keep their own bodies, by design.** `Translator`, `SourceFileViewer`, `OeCompareFile` and `Diff` are one-of-a-kind layouts inside the handoff's `.pw` frame (`pages-power.css`), whose `pw__head` is that frame's own head - `PageHead` does not replace it, and a `TranslationGridPage` or `SourceViewerPage` would have exactly one consumer. What they take from this folder is `PageHead` for the states that render outside the frame (the Translator's open-a-file landing, the source viewer's not-found), plus `PillTabs` / `HeaderTabs` where they already use them. `Piper` is the same case without a `.pw` frame: `PageHead` over a layout of its own.
- **`ListPage` (archetype 2) owns the frame and the states, not the table.** A page says which of four states it is in - loading, empty, no matches, populated - and hands over a fragment for each, so there is no branch to forget. "Empty" and "no matches" are different on purpose: the filter row goes in the first and stays in the second, because it is the way back. Pages with a table pass the real `<thead>` over `TableSkeletonRows` as `Loading`. A page with two lists under one head (`TemplatesBrowser`) uses it for the frame and keeps each section's states itself.
- **Inside a tabbed frame, compose the primitives; `ListPage` is for a page that owns its head.** A list under `SettingsPage`, `TabbedPage` or `AdministrationHeader` already has a `.page` and a head from that frame, so it uses `EmptyState` and `LoadingBlock` directly and keeps its own branches. `ListPage` has no head-less mode on purpose: the only thing left of it without a head is an if/else. The same goes for an editor form that happens to edit a list (`AdminCatalog`, `AdminApplicationVersions`): it takes `PageHead` and keeps its body. `After` is for sections that follow a list in every state (the admin Cookbook's house rules).
- **`DetailPage` (archetypes 3 and 8) is the entity-detail frame**, from `PageDetail.dc.html`: crumbs *above* a `.detail-head`, a title row that holds the state pill, one subtitle, the facts in a `.meta-row`, then the page's sections. Loading and not-found replace the whole page, so the not-found `EmptyState` sets `Heading` to give the page its `<h1>`. The run monitors (`PipelineBuilds`, `ReleasePipelineDetail`) are the same frame with their state in `TitleRow`, not a second component. A fact does not get a second subtitle line - it goes in `Meta`. Tabs are not a feature of it: compose `TabbedPage`, or put `.pill-tabs` at the top of the content as the sheet does.
- **`EditPage` (archetype 6) is the admin-edit frame**, from `PageAdminEdit.dc.html`. Unlike `DetailPage` its head is always drawn - "Edit module" is known before the module is - so loading and not-found replace only the body. The form stays with the page: `.edit-col` is usually the `<form>` element itself and the `@onsubmit` belongs to whoever owns the model, so `ChildContent` is the page's own `<form class="edit-col">`. `History` is a slot for the page's `AuditHistoryPanel`, never a query: the panel opens its own `DbContext` because it loads while the page may be saving (#741). A page with a sticky side panel (`.gen__aside`) puts that layout in its content - it is a body, not a frame.
- **The fence.** `ArchetypeConformanceTests` fails a component that hand-writes a `page-head`, and a routable page that draws anything without composing a frame - directly or through a shared component that carries one (`SettingsPage`, `TabbedPage`, the section headers, `AuthCard` for the sign-in family). Redirect pages draw nothing and are exempt by that fact. The exceptions are named in the test with their reasons: `Diff` and `OeCompareFile` (the `.pw` frame), `RecipeDetail` and `TeamDetail` (heads that hold more than a title).
- **A power list keeps its own bar.** Upgrades is archetype 15: a sticky `.cmdbar` holding commands as well as filters sits where a list has its filter row. It goes in `ListPage`'s `Toolbar` slot, which shows exactly when a filter row would, rather than being bent into `FilterBar`. Upgrades is also the list with views (Open, Archive, Fleet - `PageUpgradesList.dc.html`): the switch goes in `ListPage`'s `Views` slot, a row of its own under the head in every state, and each view decides the four states for itself under the one head. That is the slot the frame gained for it, rather than three `ListPage`s repeating the head. `Mcp` is not a list at all - its sheet is `PageConnectAgent.dc.html`, with one consumer - so it composes `PageHead` and `EmptyState` and stops there.
- **A crumb trail is a list, not markup.** Every frame takes `Trail='@([new("Admin", "/admin"), new("Modules")])'` - `Crumb(Label, Href)`, the page itself last and unlinked - and `CrumbNav` draws the links and chevrons one way. The `Crumbs` fragment stays for a trail a list cannot say, and the reason is not always obvious: an attribute is evaluated on every render of the frame, a fragment only when it is drawn, so a step that names the *loaded* record (`_release!.Label`) must stay a fragment or the page throws while it is still loading. `ArchetypeConformanceTests` lists the pages that keep the fragment, with the reason beside each.
- **An alert is `<Alert Tone="AlertTone.Danger">`, never a hand-written `.alert`.** The tone picks the icon and the `role` (an error is `role="alert"`, a confirmation or warning `role="status"`, an info note has none), both of which pages used to choose by hand and chose differently. `Icon` and `Role` override, an empty string removes. It renders a `<p>` unless `Block` is set - the paragraph's browser margins are part of the pages' spacing, so the two are not interchangeable. `ArchetypeConformanceTests` refuses a hand-written one. **A class passed through `Class` cannot be styled from the page's scoped stylesheet** - the element wearing it is rendered by the component, outside the page's scope, and no test sees the rule stop matching. Reach it with `::deep` from a page-rendered ancestor (`.ext-deliver ::deep > .alert`), or, where there is no such ancestor (the component is the outermost element, or sits straight in a frame's slot), put the rule in `app.css` under "Classes handed to a shared component's root".
- **A status pill is `<StatusPill Tone="success">`, never a hand-written `.status-pill`.** `Tone` is the sheet's own word (muted, success, warn, danger, info, queued, running, live, the translation states), so a helper that maps a state to a tone passes it straight in. The component always writes the `status-pill__dot`, which the sheet shows only on live and running; hand-written pills left it out about half the time, which looks right until a computed tone comes out running. `ArchetypeConformanceTests` refuses a hand-written one. **A class passed through `Class` cannot be styled from the page's scoped stylesheet as written** - the pill is rendered by the component, outside the page's scope, and no test sees the rule stop matching. Write `::deep .upg-prod`, which works wherever a page-rendered element (a `td`, a card) sits above the pill.
- **A `<select class="select">` goes inside `<SelectBox>`, never a hand-written `.select-wrap`.** `.select` removes the browser's arrow, so the caret the wrapper draws is the only thing that says "this opens a list", and it was a line every page had to remember. The select itself stays the page's - `@bind`, `@bind:after` and `@onchange` are compile-time directives on a real element. A width class goes through `Class` and, being on an element the component renders, is styled from the page's scoped sheet with `::deep` (`::deep .upg-view`). `UnstyledMarkupTests` checks both halves.
- **Admin pages find their way back by crumbs, and their heads stick.** As the sheets have it, and in place of the "Back to admin" / "Back to list" buttons the admin pages used to carry in their actions (a "Back to ..." button inside a not-found state stays - there it is the next step, not navigation). `PageHead`'s own stylesheet restores the handoff's `--sticky-head` clearance on a `.gen` that follows a sticky head, which `pages-forms.css` zeroes for the pages that have none; without it the template editor's rail slides up under the head.
- **`GeneratorPage` (archetype 5) is the generator frame**, from `PageGenerator.dc.html`: the head, then a form column of `.form-sec` sections beside the sticky aside with the preview, two counts and Generate. It renders the primary button itself, loading contract included, and takes only the label - so "one primary per page" is structural here, and `GeneratorPageTests` fails a generator page that writes its own `btn--primary` outside its empty state. Its `<form>` wraps the aside as well as the form column, which the sheet's does not, because the submit button and the example-files value live in the aside. Every submit runs the page's `OnSubmit` and the native one is always cancelled; the page validates and then posts through `generate.js` (#546). The template editors that borrow `.gen__aside` for a side panel are `EditPage`s, not generators - see the bullet above.
- **`ArchetypeConformanceTests` is the ratchet.** It keeps a baseline of the components that still hand-write a `page-head`. A file off the list that writes its own fails the build; a file on the list that stops writing one fails too, until its line is deleted. So the list only shrinks, and it is always the honest count of what is left to migrate. Never add to it.

## Local development environment

- **SDK:** `global.json` pins .NET 10.0.300, installed via asdf — run `export PATH="$HOME/.asdf/shims:$PATH"` before any `dotnet` command, or the pin fails against the bare-PATH SDK.
- **Lock files:** building with a mismatched local SDK rewrites `packages.lock.json` with different transitive pins. Revert that churn before committing — it is noise, not a dependency change.
- **Tests:** `dotnet test` spins up PostgreSQL via Testcontainers when Docker is running (`docker info` to check). The 8 backup tests need `pg_dump`/`pg_restore` on the host at the server's major (PG18) and skip otherwise.
- **Running the app:** `dotnet run --project ALDevToolbox/ALDevToolbox.csproj` binds http://localhost:5246 (launchSettings wins over `ASPNETCORE_URLS`); point `ConnectionStrings__DefaultConnection` at a local Postgres and set `BOOTSTRAP_ADMIN_EMAIL`/`BOOTSTRAP_ADMIN_PASSWORD` on a fresh database. Screenshot verification uses Playwright.

## Keeping the AL reference extractor's allow-lists current

The Object Explorer's reference extractor (`Services/Al/AlReferenceExtractor.cs`) reports an Unresolved count after each Phase-2 import. New BC releases occasionally ship new built-in methods, scalar types, runtime APIs, or platform virtual tables that need to land in our allow-lists to keep that number trustworthy. Two files cover the surface:

- **`Services/Al/AlBuiltinMethods.cs`** — every category of "built-in name we expect to skip" (method sets per receiver kind, scalar types, system functions, statement keywords, DSL keywords, static-receiver names). The class-level doc-comment has a labelled `EXTENDING WHEN MICROSOFT ADDS NEW METHODS / TYPES` checklist mapping each kind of addition to the right `HashSet`.
- **`Services/ObjectExplorer/Import/ReleaseImportAllowLists.cs`** — `PlatformVirtualTables` (the named id → name map for the `2000000001..2000000999` runtime tables) and `FoundationalAppNames` (Microsoft umbrella apps every extension implicitly depends on). Both have `EXTENDING` notes at their definition. (They used to live in `ReleaseImportService.cs`; the file is theirs now.)

`AlReferenceExtractor.IsPlatformVirtualTableId` is the range-check safety net for the platform-table ids — even if a numeric id isn't named, the diagnostic silences. Add to the named list when the symbol package resolves the id to a name (so `Record Field`-style chains work), not just to silence noise.

When new noise patterns appear in the Phase-2 sample log, prefer extending one of these allow-lists over adding bespoke code paths to the walker. The diagnostic itself (`AlReferenceExtractor.CaptureUnresolved`) is intentionally cheap and structured so operators can grep the log by `Reason=` and trace each new bucket back to a list above.

The legacy **C/AL TXT** ingest path (`Services/Cal/`) has its own parallel allow-list — **`Services/Cal/CalBuiltinMethods.cs`** — because classic C/AL's runtime surface and casing differ from AL (uppercase `SETRANGE`/`FINDFIRST`, `FIND('-')`, the `DATABASE::`/`CODEUNIT::` static receivers). Its class-level doc-comment carries the same `EXTENDING WHEN A NEW C/AL RELEASE ADDS NAMES` checklist mapping each kind of addition to the right `HashSet` (`ReceiverMethods`, `BareFunctions`, `FieldNameTakingMethods`, `StaticReceivers`, `Keywords`). `CalReferenceExtractor` counts unresolved receivers the same way; extend this list — not the walker — when a real C/AL export surfaces a new built-in as noise. The object-literal half of those static receivers (`CODEUNIT::"Sales-Post"`, `DATABASE::Customer`, and the `PAGE::`/`REPORT::`/`XMLPORT::`/`QUERY::`/`FORM::` forms) *is* the walker's business: `CalReferenceExtractor` emits them as `property_object` references carrying the object name, matching what the AL walker emits for the same literal, and `CalImportService` resolves the id from the name in its post-pass.

## Keeping MCP parity with the web UI

The MCP server (`Services/Mcp/Tools/*Tools.cs`) is a parallel front-end on the same services the Blazor pages use — agents reach the Object Explorer (and friends) through these tools. When you add a feature that's user-visible in the web UI — a new reference kind, an outline section, a derived relationship, a filter — check whether it should also show up through MCP, and wire it through in the same PR. Two patterns matter:

- **Service-level features come for free.** If the new behaviour lives behind an existing service method (e.g. `FindReferencesAsync` matching a new `reference_kind`), the matching MCP tool usually picks it up automatically. Verify it actually reaches the MCP path — the tool may call a sibling method that doesn't see the new bucket.
- **New DTOs and query paths need plumbing.** When a feature lands a new field on a DTO (e.g. `ObjectOutline.ImplementedBy`) or a separate query method (`FindReferencesForSymbolAsync` vs `FindReferencesAsync`), the MCP tool has to be updated to populate the field or route to the right query. Otherwise the web UI shows the relationship and MCP agents stay blind to it.

- **A new gate is a feature too — and it lives on the service.** The MCP id resolvers
  are methods on the service that owns the entity, not private helpers in the tool
  classes: `ObjectExplorerService.ResolveReleaseAsync` /
  `EnsureSymbolVisibleAsync` / `ResolveProcedureSymbolIdAsync`,
  `ProjectService.ResolveProjectAsync` / `ResolveReadyBuildAsync`, and
  `ReleasePipelineService.EnsureReleasePipelineExistsAsync`. Add an access rule
  there and every tool that resolves an id through it inherits the gate — the tool
  classes hold no `AppDbContext` of their own for these lookups. When you add a tool,
  route its ids through the matching resolver rather than querying the DbSets; and
  when a tool can *bypass* one (a `symbolId` argument that skips release resolution),
  gate it explicitly with the entity's own visibility check. A denied read answers the
  tool's existing "not found" message, never a distinct refusal — see the
  project-visibility fence in `.design/teams-and-visibility.md` for the worked example.

- **A read-only area gets a read-only class, and a test that says so.** The Deliver
  area's reads (#912) live in `DeliverTools`, apart from `DeliveryTools` where the one
  write (`deploy_build`) sits behind its own gate. `DeliverToolsTests` walks the class
  and fails if any public method is not an `[McpServerTool(ReadOnly = true)]`, so a write
  added there by accident is a red build. Its twelve tools, one line each:
  `get_solution` (hosting, version, address, connection, environments in a line),
  `list_environments` (the fleet with solution/type/status/version/storage/next-update
  filters), `get_environment` (one environment with its installed apps),
  `list_environment_history` (the Workbench history), `list_upgrades` (the Upgrades fleet,
  behind the environment-updates grant), `list_planned_upgrades` and `get_upgrade` (the
  planned upgrades of #984 and one upgrade's lines with their derived states, behind the
  same grant), `list_recent_deployments` (deployments across
  solutions), `list_customer_contacts` (contacts with phone and email, each call logged),
  `get_customer_access` (getting-in and hosting notes, integrations),
  `list_customer_knowledge` (who knows a customer, or which customers a colleague knows)
  and `list_customer_modules` (the module catalogue, by module or by solution). None of
  them calls a customer's tenant: they read the mirror, and every mirrored fact carries
  the time it was read.

Skip the MCP path only when it genuinely doesn't apply — pure UI affordances (resizers, badge styling, keyboard shortcuts), authoring flows that already have a dedicated MCP tool, or per-org admin pages that aren't part of the AL-reading surface. When in doubt, expose it through MCP; agents tend to want the same answers humans do.

## Releases and image publishing

Releases are cut by pushing a git tag; `.github/workflows/release.yml` builds the Dockerfile, pushes `ghcr.io/mtaanquist/al-workbench` to GHCR, and publishes the matching GitHub Release with auto-generated notes. There is no release on every merge — `main` stays continuously green via `build.yml`, and a release is a deliberate tag on a commit that's already passed CI.

**The repository was renamed** from `ALDevToolbox` to `al-workbench` in September 2026, to
match the product name. The image path is not a literal: both `release.yml` and
`staging.yml` build it from `ghcr.io/${{ github.repository }}`, so it moved with the rename
and no workflow had to change - but a GHCR package does not follow a repository rename. The
old package, `ghcr.io/mtaanquist/aldevtoolbox`, keeps serving the tags it already has and
would never receive a new one, so anything still pulling it would silently stop updating.
Two things cover the move:

- For one release, v11.9.1, `release.yml` had a **mirror step** that also tagged the image at
  the old path, so the production server kept updating until its compose file had moved. It
  was deleted once that deploy was confirmed healthy on the new path. The old package holds
  every version up to and including v11.9.1 and will never receive another.
- `compose.yaml` pulls the new path, and its tag variable is `ALWORKBENCH_TAG` with
  `ALDEVTOOLBOX_TAG` as a permanent fallback, so an `.env` written before the rename keeps
  pinning what it pinned.

**The order mattered, and is worth keeping for next time.** `compose.yaml` could not move in
the same change as the rename: CI's compose smoke test pulls the image anonymously, and the
new package did not exist until a release had published it - while releasing needed that
change merged. So: release once from the renamed repository (v11.9.1) with compose still on
the old path, check the new package can be pulled without signing in, then flip the image
line.

The new path only holds releases cut after the rename (v11.9.1 onwards). A deployment
pinned to an older version must keep the old image path until it upgrades; v11.9.1 is the
one version at both.

The new package came up **public**, inheriting the repository's visibility - not private, as
this document and the rename PR had both predicted. Check rather than assume:
`docker logout ghcr.io && docker manifest inspect ghcr.io/mtaanquist/al-workbench:<tag>`.
Old links to the repository,
its issues and its pull requests redirect for as long as nothing else takes the old name.

**Version scheme — one major per shipped end-user tool.** The major number is the count of distinct tools in the sidebar's Tools section. Each new tool bumps the major; everything else (features within a tool, cross-cutting work like auth/backups/hosting, polish) is a minor or a patch. The mapping (10 is the tag the Upgrades work is cut as):

| Major | Tool that opened it      | Landed |
|-------|--------------------------|--------|
| 1     | Projects (Workspace + Extension generators) | the original product |
| 2     | Piper                    | #64    |
| 3     | Object Explorer          | #103   |
| 4     | MCP server               | #173   |
| 5     | Cookbook (née Snippets)  | ~#180  |
| 6     | Translator               | #295   |
| 7     | Pipelines (project builds + artifacts) | #449 |
| 8     | Diff (né Compare)        | #512   |
| 9     | — the whole-app redesign (see below) | #596 |
| 10    | Upgrades (Business Central platform updates across the fleet) | #657 |

- **Major** — a new top-level tool ships (the next entry in the table). Don't bump major for anything short of a genuinely new tool surface. The one non-tool exception on record is v9.0.0, the whole-app redesign: every screen changed at once, and operators pinning `8` should not receive that unasked. A future change of that magnitude — every screen, or a migration operators must plan for — may take a major on the same reasoning; a big feature inside one tool still may not.
- **Minor** — a new feature, page, or capability inside an existing tool, or cross-cutting work (a new role, backup tooling, a hosting endpoint). Most releases are minor bumps.
- **Patch** — bug fixes and copy/UX tweaks with no new surface.

**Cutting a release:**

1. Make sure `main` is green (the commit you're tagging passed `build.yml`). This is a hard
   requirement, not a courtesy: the tag push deliberately does **not** start its own
   `build.yml` run (#729 — it would duplicate the one the branch push already did, and
   double the wall-clock of every release). `release.yml`'s gate looks up the run by commit
   SHA, so a tag on a commit that never reached a branch has no run to find and the release
   is refused within a few minutes, naming that as the reason.
2. Pick the version per the scheme above. Tag and push:
   ```bash
   git tag vX.Y.Z
   git push origin vX.Y.Z
   ```
3. `release.yml` fires on the `v*.*.*` tag, builds the image, pushes the moving tags `latest`, `X`, `X.Y` plus the exact `X.Y.Z`, and publishes the GitHub Release with generated notes. Nothing to publish by hand. Operators pin the image as loosely or tightly as they want.

**The image is stamped with its version.** `release.yml` passes the tag and the build date to the Dockerfile as the `RELEASE_VERSION` / `RELEASE_DATE` build args, which reach `dotnet publish` as the `ReleaseVersion` / `ReleaseDate` MSBuild properties and land in the assembly as metadata attributes. `Services/Operations/BuildInfo` reads them back and the sidebar footer shows "Version x.y.z" under the copyright, linking to that release's notes with the release date on hover. Builds without the args (local `dotnet run`, plain `docker build`, staging images) carry no stamp and show the copyright line alone — never a link to a release they aren't.

Never move or re-push a published tag — cut a new patch instead. The image name is derived from `github.repository`, lowercased by `docker/metadata-action`, so it always resolves to `ghcr.io/mtaanquist/al-workbench` regardless of the repo's casing.

**Staging previews.** `.github/workflows/staging.yml` publishes the same image under a `staging` tag so a branch can be *run* before it merges. It pushes both `staging` (moves every run) and `staging-<sha>` (immutable, so a preview worth keeping can be pinned). Run it with `ALDEVTOOLBOX_TAG=staging docker compose up -d`.

Two triggers, available at different times. `gh workflow run staging.yml --ref <branch>` is the one to reach for — but GitHub only offers a manual run for workflows present on the **default branch**, so a workflow that only exists on a feature branch can't be dispatched at all. Until this file is on `main`, the `push:` branch list is what actually fires; add a branch there to get an auto-rebuilding staging instance, and remove it when the branch merges. Moving `staging` is not an exception to the rule above: that rule protects tags operators pin, and this one exists to move. Unlike `release.yml` it does **not** wait for `build.yml` to go green — a staging image is for looking at, so check CI yourself before trusting what you see.
