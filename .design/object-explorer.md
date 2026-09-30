# Object Explorer ingest and reference resolution

This document specifies how the Object Explorer ingests Business Central application content and how it resolves "Find references" queries across the imported corpus. It supersedes the ad-hoc model implied by the current `base_app_files` / `base_app_symbols` / `base_app_versions` tables once implemented.

**Status:** proposal. No code in this doc has been written. Once approved, it replaces the existing ingest path (which today imports a single Base App source ZIP per `BaseAppVersion`) with a multi-app, symbol-aware pipeline. The current single-source-ZIP path stays as a fallback for partner apps that ship without a `.app` symbol package.

## Why

Today the Object Explorer imports one "Base Application" source ZIP per BC version and runs find-references as a name-only regex over file contents. That has two known limits:

1. **Name-only matching is wrong.** `ErrorInfo.Create(...)` shows up as a reference to any codeunit's `Create` procedure; every table's `Code` field surfaces every other table's same-named declaration. The receiver-awareness band-aid (separate small PR) closes part of this gap by inspecting the call site's qualifier, but the underlying model still has no way to distinguish "different objects that happen to share an identifier".
2. **Single-app-per-version is wrong for BC.** A real BC deployment is dozens to hundreds of `.app` modules: Base Application, System Application, Business Foundation, every localisation, every Microsoft first-party extension (Shopify Connector, Sales & Inventory Forecast, AI Test Toolkit, …), every partner app, every customer customisation. A cross-app reference search — "where in everything you've imported does anyone call `Codeunit "Sales-Post"`?" — is exactly what VS Code can't do because VS Code only sees one workspace. That's the feature the workbench should own.

The `.app` archive is the right ingest format because it carries a `SymbolReference.json` with **fully resolved type metadata**: every variable's `TypeDefinition.Subtype = (ModuleId, Id, Name)` triplet uniquely identifies the target object across the entire BC ecosystem. Ingesting `.app` files turns receiver resolution from a regex heuristic into a SQL join.

## Scope

In scope:

- A new ingest endpoint that accepts a single archive containing many `.app` (and optionally `.Source.zip`) files arranged as a BC DVD's `applications/` folder.
- New entities — `OeRelease`, `OeModule`, `OeModuleObject`, `OeModuleSymbol`, `OeModuleVariable`, `OeModuleReference`, `OeModuleFile` — replacing the current `base_app_versions` / `base_app_files` / `base_app_symbols` tables. The current tables are dropped in the migration; the data they hold (one BC version of source-ZIP-only imports) is not preserved.
- Cross-app reference resolution via the symbol package: a Find references on `Codeunit X` returns exact hits from every module's variables, properties, and extension targets that name X by ModuleId+Id, plus source-scanned intra-module hits.
- UI shift to a three-level hierarchy: **Release › Module › Object**.
- A keep-list policy for what we actually store from each `.app` (source `.al` files yes; `.rdlc` / `.xlf` / images / `.pdf` no — see Storage policy).

Out of scope:

- Anything beyond reference and structural queries. We're not building a full IntelliSense, not implementing semantic refactoring, not handling AL projects under active development.
- Compiling AL. The `.app` is the input; we read what the compiler already produced. (Compiling *customer source* into `.app`s is its own documented path — see `object-explorer-project-builds.md` — but it produces `.app`s that feed this same ingest pipeline, so this core ingest doc still treats the `.app` as its input.)
- Source-only imports going away. Partner apps without `.app` files (or with `IncludeSourceInSymbolFile="false"` and no separate source ZIP) still need a heuristic path. The receiver-aware band-aid plan covers that case and stays in service.
- Editable corpus. Modules are read-only once imported — re-upload replaces, not patches.

## Input shape

The DVD's `applications/` folder is the canonical input. A representative slice (from a BC 25 DVD):

```
applications/
├── BaseApp/Source/
│   ├── Microsoft_Base Application.app
│   ├── Base Application.Source.zip
│   ├── Microsoft_Danish language (Denmark).app       ← translation-only
│   └── … 20 more language packs
├── BaseApp/Test/                                     ← skipped (tests)
├── DKCore/Source/
│   ├── Microsoft_DK Core.app
│   └── DK Core.Source.zip
├── DKCore/Test/                                      ← skipped
├── OIOUBL/Source/
│   ├── Microsoft_OIOUBL.app
│   └── OIOUBL.Source.zip
├── EDocumentConnectors/Avalara/Source/               ← nested sub-product
├── EDocumentConnectors/Logiq/Source/
├── APIV1/Source/
│   ├── Microsoft__Exclude_APIV1_.app                 ← `_Exclude_` prefix; ingested anyway
│   └── _Exclude_APIV1_.Source.zip
├── testframework/                                    ← entire subtree skipped (tests)
└── …
```

Some older BC downloads (e.g. *Update 15.1 Dynamics 365 Business Central 2019 Release Wave 2 DK*) ship the DVD **wrapped in an outer ZIP** whose only payload is a single nested `<Name>.DVD.zip`. The outer walk finds no apps, so `ReleaseZipStaging.OpenStagedZip` descends into the lone nested zip — extracting it to a `DeleteOnClose` temp file so its lifetime is tied to the archive the caller already disposes — and walks that instead. The descent only fires when the outer archive yields zero apps and holds exactly one nested `.zip` (or a sole `*.dvd.zip` when other zips sit alongside it); several ambiguous candidates are refused, and the "no apps" diagnostic (`DescribeAppLocations`) then names the nested zips so the admin can re-zip with just the DVD inside.

A second accepted input shape (same upload box) is a **zipped VS Code AL workspace** — a multi-root layout where each top-level folder holds an `app.json` plus a `src/` tree, the folder's own compiled `.app`(s), and a `.alpackages/` dependency cache. `FolderZipWalker.LooksLikeWorkspace` detects it (any `app.json` outside a tooling/dependency folder) and `WalkWorkspace` scopes ingest to each app's *own* build output: it imports the `.app` sitting directly in an app-root folder (newest version when several builds are present), and deliberately skips the `.alpackages/` copies (those are dependencies that belong to their own folders / the parent first-party release), the `.dep.app` symbols-only sidecars, and `.snapshots` / `.vscode` / `.git`. A folder's own `.app` is identified by matching its `app.json` identity (normalised `publisher_name`) against the `.app` filename stem — a folder root can hold a *stray* copy of a sibling app's `.app` (a dependency dropped next to `app.json` instead of in `.alpackages`), and binding to `app.json` keeps that stray from being claimed by — and imported under — the wrong folder (which would otherwise collide on `(AppId, Version)` and abort the whole release). A final dedupe by `(publisher_name, version)` is the belt-and-suspenders guard for two folders sharing an `app.json` id. App folders with an `app.json` but no *own* compiled `.app` (not built yet, or holding only a stray) are skipped; `DescribeUncompiledAppRoots` names them and the import worker logs a warning so the admin knows to build and amend them in. This is the convenient path for customer solutions assembled as a workspace rather than shipped as a DVD; importing source from an *uncompiled* app folder (no `SymbolReference.json`) is a separate, lower-fidelity ingest path tracked in the roadmap.

Full real-world DVD is ~500 MB compressed. A `.app` file is a ZIP with a 40-byte `NAVX` header prefix you have to strip; the inner ZIP contains `NavxManifest.xml`, `SymbolReference.json`, an optional embedded `src/` source tree, translations, layouts, and the entitlement XML. The reader pulls the `src/` tree whenever it's *physically present*, regardless of the manifest's `IncludeSourceInSymbolFile` policy flag: that flag declares the publisher's intent to expose source, not what the archive actually carries, and some partner apps (Tasklet's Mobile WMS (Tenant Edition) is the observed case — 311 `.al` files with the flag off) ship full source while leaving the flag `false`. Gating on the flag dropped that source on the floor (objects ingested, 0 files); reading whatever is in the box fixes it, and an app that genuinely embeds no source still yields zero file rows. The `IncludeSourceInSymbolFile` flag (and the BC 14 `ShowMyCode="True"` fallback on `<App>` for runtime-3.0 manifests that predate `<ResourceExposurePolicy>`) is still parsed and surfaced on the manifest for information, but no longer gates the read.

### What we filter

| Path pattern (case-insensitive) | Action |
|---|---|
| Any segment matching `Test` / `test` / `Test Library` / `Test Libraries` | Skip whole subtree |
| `testframework/` | Skip whole subtree |
| `_Exclude_` prefix on `.app` filename | **Ingest as normal** (per Decision 4). They're real platform modules used in production debugging. |
| Translation-only language packs (no codeunits/tables/pages in `SymbolReference.json`) | Ingest as bare `OeModule` rows so the dependency graph is closed, but don't extract per-object detail or store source. |

The filename `_Exclude_` prefix and Microsoft's `Target="OnPrem"` / `Target="Cloud"` distinction don't affect ingest behaviour. They're surfaced on the `OeModule` row so the UI can filter if needed.

**Translations auto-extract on first-party ingest.** For `first_party` releases only, `AppPackageReader.ReadAsync(captureTranslations: true)` pulls each module's `Translations/*.xlf` (raw bytes, capped) out of the `.app`, and `ReleaseImportService` streams each through the parser into `TranslationImportService.ImportForModuleAsync` — which writes the per-module `oe_module_translations` rows *and* upserts the source→target pairs into the org-wide translation memory (the same sink the admin uploads use). Attaching is by *containing module*, not the XLIFF's `<file original>`, because the full release isn't in the DB yet during the per-module pass; the memory is keyed by text so the pairs land regardless. The source-only `<Module>.g.xlf` (source-language == target-language) is skipped, and the whole step is best-effort per file — a bad XLIFF is logged and skipped, never failing the ingest. This was only safe to turn on once `AlXliffParser` became a forward-only streaming reader: the earlier `XDocument.Load` DOM parse OOM'd on hundred-MB base-app XLIFFs, which is why translations used to be admin-upload-only. Partner / project imports don't capture translations; an admin uploads those on demand. See `.design/translator/`.

## Storage policy

The user's constraint: we don't need to store everything inside an `.app` — only what's required to answer reference queries and render the file viewer. That cuts the corpus size dramatically.

| Artifact | Stored? | Why |
|---|---|---|
| `NavxManifest.xml` (parsed) | Yes — as columns on `OeModule` | AppId, name, publisher, version, dependencies, target, resource-exposure flags. |
| `SymbolReference.json` (parsed) | Yes — fanned out into `OeModuleObject` / `OeModuleSymbol` / `OeModuleVariable` / `OeModuleReference` rows | The reason we exist. |
| `.al` source files | Yes — full content in `OeModuleFile.content` | Powers the file viewer and the intra-module reference scanner. Prefer the paired `<Name>.Source.zip` when one is uploaded alongside the `.app`; fall back to the `.app`'s embedded `src/` only when no zip was provided (e.g. single-file partner uploads). The `.app`'s embedded source is unreliable in BC 28.x — Microsoft's Ready2Run wrappers ship a partial inner `src/` tree while the canonical full source sits in the sibling `.Source.zip` on the DVD. |
| `.rdlc` report layouts | No | Often the biggest payload per `.app` (the DK Core sample has a 740 KB layout). Not referenced by any code, not browsed in the Object Explorer. |
| Translation `.xlf` files | No | Not addressable from AL code. |
| `logo/`, images, `entitlement/*.xml`, `MediaIdListing.xml`, `[Content_Types].xml`, `navigation.xml`, `DocComments.xml` | No | None contribute to reference search or file viewing. |

Approximate corpus footprint after filtering for a full BC 25 DVD: order of 200–300 MB of `.al` source text plus a comparable amount of resolved-symbol rows. The 500 MB upload contracts roughly 2× into PostgreSQL.

## Ingest pipeline

A new endpoint, e.g. `POST /admin/object-explorer/releases`, accepts a multipart upload of one archive plus a label (`"BC 25.18"`). Processing runs synchronously inside the request (consistent with the rest of the workbench — no background workers) and streams progress to the page via Blazor Server's interactive render.

1. **Stream the upload to a temp file.** A 500 MB body can't live in memory.
2. **Walk the archive's directory entries.** No full extraction. For each `.app` encountered:
   1. Skip if the path matches the test-filter list above.
   2. Stream-read the `.app` entry into a `MemoryStream`, strip the 40-byte `NAVX` prefix, treat the remainder as a ZIP.
   3. Parse `NavxManifest.xml`. Reject silently if `App@Id` or `App@Version` are missing (malformed `.app`).
   4. Idempotency check: if an `OeModule` with `(AppId, Version)` already exists in the target Release, **skip silently** (Decision 3).
   5. Parse `SymbolReference.json` into the typed structures. Walk the namespace tree depth-first, emitting one `OeModuleObject` per codeunit / table / page / report / xmlport / query / controladdin / enum / interface / permissionset / pageextension / tableextension / reportextension / enumextension / permissionsetextension. BC 22 introduced AL namespaces and moved these collections under a `Namespaces[]` tree; pre-namespace releases (BC 14 through ~21, the oldest `.app`s supported) hang the collections straight off the symbol-file root with no `Namespaces` key. The reader treats the root itself as the implicit empty namespace, so both shapes ingest — older objects simply land with an empty `oe_module_objects.namespace`. `ReferenceSourceFileName` on each object is captured but **not** used to link objects to files — Microsoft ships it with inconsistent path conventions even within a single release (some modules use a nested `src/`, some a project-folder prefix, some bare paths), so a path-string join is unreliable. The link is established header-side instead (step 6).
   6. Extract source: prefer the paired `<Name>.Source.zip` in the same archive folder when one is present (BC 28.x first-party modules ship the canonical full source there); fall back to the `src/` inside the `.app` only when no zip was uploaded alongside it. Every entry is canonicalised through `AppPackageReader.CanonicalizeSourcePath` so the four shipped layouts (`src/...`, `src/src/...`, `<Project>/src/...`, bare `Codeunits/...`) all key onto the same `src/<relative>` shape for stable storage in `OeModuleFile.path`. Canonicalisation also percent-decodes each path segment, so publishers that URL-encode filenames in the embedded `src/` tree (Tasklet's Mobile WMS double-encodes spaces as `%2520`; BC's own `ReferenceSourceFileName` single-encodes them as `%20`) store and display the clean name (`Command XML Management.Codeunit.al`) rather than the encoded form. Store every `.al` text file in `OeModuleFile`. Discard everything else.
   6a. Link `OeModuleObject` rows to their source file by parsing the top-level `<kind> [<id>] <name>` declaration in each ingested `.al` file and indexing the result by `(Kind, Name)`. The symbol package's `(Kind, Name)` then looks up directly into that index — AL enforces one object per file in practice and the declaration is deterministic to parse, so this is the stable join. The first-match-wins regex covers the rare multi-object files (only the first object's link is established; subsequent objects in the same file fall through to a null `SourceFileId`).
   7. Emit `OeModuleSymbol` rows (procedures, triggers, event publishers, event subscribers) from the symbol package's `Methods` array. The shipped implementation leaves `OeModuleSymbol.LineNumber` / `ColumnStart` / `ColumnEnd` at 0 — symbol-package coordinates are not stored statically; consumers that need them re-derive against the file content. `OeModuleObject.LineNumber` is stamped during ingest via a single regex pass over each `.al` file. Locals not in the symbol package are out of scope for the current implementation; a future intra-procedure scanner can fill them in incrementally.
   8. Emit `OeModuleVariable` rows for every object-scoped variable (globals only — procedure-locals are not in the symbol package and stay in the source-driven heuristic path).
   9. Emit `OeModuleReference` rows for every fully-qualified type reference: variable subtypes, extension `TargetObject`, codeunit `TableNo` properties, report data items, event subscriber bindings (publisher event reference). Same-module refs whose Subtype omits `ModuleId` get stamped with the importing module's AppId. Reference rows store the `target_app_id` triplet from the symbol package; they do **not** carry a resolved `target_module_id` — that's computed at query time via the parent-chain join above.

      **`TargetObject` / `Target` are read verbatim.** `AppPackageReader.ParseExtendsRef` peels the `#<32-hex-appid>#` wrapper when present and returns the rest unchanged. We deliberately do **not** try to strip a namespace prefix:

      - **What the symbol package actually ships.** A sweep of `Target`, `TargetObject`, and `Subtype.Name` across BC 28.1 BaseApp, BusinessFoundation, SystemApp, QualityManagement, EDocument Core, Intrastat Core, OIOUBL, and DK Core found zero namespace-qualified values. Every name appears bare, even when the object itself lives deep in `Microsoft.Foo.Bar`. The same is true for every shape with internal dots (`Gen. Journal Line`, `Sales Cr.Memo Header`, `Doc. Sending Profile Elec.Doc.`, `Whse.-Source - Create Document`, `Blanket Purch. Order Arch.Sub.`).
      - **What we tried before.** A previous implementation guessed where the namespace ended by walking dots and validating segments. Every refinement of that heuristic (`LastIndexOf('.')` → "dot followed by whitespace" → "dot followed by an identifier-start char") still required escape hatches for some legitimate AL name with an internal dot. With no qualified inputs in the wild, the heuristic was solving an imagined problem and adding real bugs.
      - **The rollback signal.** If a future BC release (or a partner app) starts emitting namespace-qualified targets, the chain walker's `base.name = ext.extends_object_name` join inside `PropagateSourceTableToPageExtensionsAsync` and `_extensionsByBaseName` will silently miss — the symptom is missing dependency edges in the file viewer's Used-by panel for cross-module extensions, plus phantom Using entries for tableextensions. At that point the right fix is **not** to reintroduce a guess. It's to plumb the namespace through explicitly: the symbol JSON already nests every object under its `Namespaces[].Namespaces[]...` path, so the importer can carry that into `oe_module_objects.namespace` (already done) and join on `(target_app_id, target_object_name)` after stripping the namespace using **that** stored namespace as the prefix to strip, rather than guessing from a flat string. Heuristic with real data, not in the dark.
3. **Mark the Release ready.** Once every `.app` in the archive has been processed, flip the Release row's `status` from `'ingesting'` to `'ready'`. Until then, the Release is hidden from the picker. Failure mid-ingest leaves a tombstone row (`status = 'failed'`, with an error message) that a SiteAdmin can clear.

**Retrying a failed import.** A `'failed'` release can be re-run in place from its manage page (`ReleaseImportService.ReopenForRetryAsync` + the `/retry` endpoint) instead of forcing a delete-and-reimport: the release keeps its label and metadata, the previous attempt's partial modules are wiped first (`ReleaseManagementService.ClearIngestedDataAsync`, so the re-run can't skip a half-written module on the idempotency check), the row flips back to `'ingesting'`, and a fresh import job is queued. A URL import re-runs from its original download URL — read back from the durable `oe_import_jobs.download_url` (the URL lives only there, not on the release) — or from a corrected URL the admin pastes; a staged-ZIP / C-AL import needs the file re-uploaded because its temp file is gone after the first run.

Re-upload of a full Release with the same label: same idempotency rule — every `(AppId, Version)` already present is skipped. To replace a Release entirely the user has to delete it first.

## Importing from Microsoft artifacts

Beyond the manual Upload and DVD-URL paths, releases can be pulled straight from Microsoft's CDN — the same source `BcContainerHelper`'s `Get-BCArtifactUrl` uses. `BcArtifactIndex` (pure) ports the discovery logic; `BcArtifactService` adds the network + cache; `ArtifactReleaseImporter` is the shared coordinator both the UI and the scheduler call.

- **OnPrem for shipped builds.** The public storage's OnPrem type is what a shipped release is imported from; the type isn't exposed in the UI. (An earlier note here said Sandbox artifacts hid their apps inside a database backup and weren't walkable — that is no longer true: current Sandbox artifacts carry the same `Applications.<country>/` + `Extensions/` layout as OnPrem ones, which is what makes the preview channel below possible.)
- **Discovery.** `GET https://bcartifacts.blob.core.windows.net/onprem/indexes/{country}.json` returns the available builds (`Version` + `CreationTime`); a sibling `platform.json` is the cross-check (an application build is only usable when its version has a platform artifact too). The download URL is `https://{cdn}/onprem/{version}/{country}`.
- **Two-part download, asymmetric walk.** The application artifact's `manifest.json` carries a `platformUrl`; `BcArtifactService.DownloadArtifactSetAsync` downloads both (reusing `DvdDownloadService`'s SSRF-guarded, resumable transfer; both hosts vetted against a fixed Microsoft-host allow-list). The two artifacts are *not* shaped like the DVD, so they get different walks (`FolderZipWalker.WalkBcArtifact*` via `ReleaseZipStaging.OpenBcArtifactZip`):
  - **Application (country) artifact** — the localized apps live under `Applications.<country>/` (e.g. `Applications.DK/`) and first-party `Extensions/`, with *versioned* `.app` filenames (`Microsoft_Base Application_25.5.30849.48785.app`) whose paired `.Source.zip` is named *without* the version (`Base Application.Source.zip`) — `PairingCandidates` tries version-stripped stems to match them. Its test apps are flat (`Tests-*`, `* Test Library`, `Test Runner`, `Permissions Mock`, …) rather than under a `Test/` folder, so a filename heuristic drops them on top of the `Test*`-folder rule.
  - The insider (preview) Sandbox artifact adds one wrinkle: the core apps appear in both `Applications.<country>/` (beside their `.Source.zip`) and `Extensions/` as *different bytes* under the same filename. `WalkBcArtifactApplication` keeps the country-folder copy when a leaf name appears in both, since importing both would trip the `(AppId, Version)` collision guard.
  - **Platform artifact** — the classic W1 DVD (`platform/Applications/`, unversioned names) *plus* the unique `System.app` (under `platform/ModernDev/.../AL Development Environment/`). We take **only `System.app`** from it: its W1 apps duplicate the localized ones at the same `(AppId, Version)` with different bytes, which would abort the import on the collision guard.
  - The new `ReleaseImportSource.BcArtifact` job is resumable across restarts like a URL import.
- **Naming + dedup.** Artifact releases are always `first_party`, labelled **`Business Central {Major}.{Minor} ({CC})`** (e.g. `Business Central 28.2 (DK)`). That label is the dedup key: a version whose label already exists is skipped, so later cumulative updates of the same minor are intentionally not re-imported — a new minor creates a new release.
- **Version cache.** The Artifacts tab persists the available versions per org+country into `oe_artifact_versions` (`BcArtifactService.RefreshIndexAsync` upserts and drops versions Microsoft no longer offers), so the read-only table renders from the DB rather than re-querying Azure each load. "Imported" status is derived by matching the computed label against `oe_releases`, not stored, so the cache can't drift.
- **Preview builds.** Microsoft publishes pre-release builds of upcoming versions on a second storage, the insider one (`BcArtifactIndex.InsiderCdnHost`, env override `BC_INSIDER_CDN_HOST`; the same host BcContainerHelper's `-storageAccount bcinsider` resolves to). It serves anonymously, but only the **Sandbox** type — its OnPrem indexes return 403 — so `BcArtifactChannel.Preview` means "insider storage, sandbox type" and `BcArtifactChannel.Release` means "public storage, onprem type"; every URL helper takes the channel. `BcArtifactService.ResolvePreviewsAsync` reads the newest shipped major off the public index, then picks from the insider index one build per major above it: the newest build of the lowest minor listed (`BcArtifactIndex.SelectPreviewVersions`) — so with 28.5 shipped and 28.6 / 29.0 / 29.1 / 30.0 in preview, it resolves 29.0 and 30.0, and leaves the current major's minor previews out. A preview release is `first_party` with `is_prerelease = true`, labelled **`Business Central 29.0 (DK) Preview`**, dedup key **`bc-insider:{Major}.{Minor}:{cc}`** — a different prefix from the shipped key so a preview and its successor can coexist for the moment they are swapped and so "is 29.0 imported?" never answers yes because of a preview. It is marked wherever a release is listed: a "Preview" pill on the Releases browser card, the release detail subtitle (plus a meta-row note that its objects may still change) and the admin release list, and an `IsPrerelease` field on MCP `list_releases`; the Releases hero features the newest *shipped* release and never mentions a preview, so nothing on the page can make a pre-release read as GA. Two lifecycle rules run in the daily sweep (`ArtifactReleaseImporter`), each in its own try so a failed shipped import never costs a country its preview housekeeping: **refresh** — a preview older than `PreviewRefreshAge` (14 days) whose build no longer matches the insider channel's current pick is soft-deleted and the current build queued in its place (`ArtifactImportStatus.Replaced`) inside one transaction, so a refused quota leaves the old preview standing; a preview still ingesting is never replaced, and when the channel moves a major on to its next minor the preview of the earlier minor is retired so a major never carries two; and **supersede** — `SupersedePreviewsAsync` soft-deletes any preview whose Major.Minor is at or below the newest Major.Minor with a ready, non-deleted `bc-onprem:` release for that country (so a 29.0 preview still goes when the org's first shipped 29 is 29.1, and a next-minor build's 28.6 preview, #993, stays while 28.5 is the newest shipped), and runs for every opted-in org whether or not previews are still ticked. Both are soft deletes, so the admin release list can restore or purge them; the purge is manual, and `RestoreAsync` refuses to bring back a row whose dedup key a successor now holds. Previews are opt-in per org (`OrganizationSettings.AutoImportPreviewsEnabled`, a second checkbox on the automatic-import card that requires the sweep itself to be on) and never offered on the manual browse table. **What a preview never does:** drive the dependency-drift scan or count as the newest first-party release for "Check again" (`DependencyDriftService` filters `is_prerelease`), seed the translation memory (`captureTranslations` is off for it), or appear in the parent-release pickers on the upload and DVD import tabs — a third-party release parented on a preview would lose its parent when the preview is retired. The command palette reads a preview's country off the `bc-insider:` key the same way it does off `bc-onprem:`.
- **Auto-import.** `OrganizationSettings.AutoImportReleasesEnabled` + `AutoImportCountry` opt an org into a daily sweep (`ReleaseAutoImportScheduler`, the same poll-once-daily shape as `BackupScheduler`, env opt-out `DISABLE_RELEASE_AUTO_IMPORT_SCHEDULER`). `AutoImportCountry` may be a comma-separated list (`w1,dk,nl`) — the sweep imports each code independently, and the country in the release label keeps them from colliding on dedup. Codes are stored canonically (trimmed/lower-cased/de-duped) by `OrganizationConfigService.SaveAutoImportAsync`; `ParseAutoImportCountries` is the shared parser. Each org's import runs under its own `AmbientOrganizationScope`; the sweep is idempotent via the same label dedup, so a re-run (restart, or twice in a day) downloads nothing new. After each per-org pass the sweep stamps `OrganizationSettings.AutoImportLastRunAt` (and invalidates the org-config cache) so the artifacts import page can show an "Active / last checked" status on its automatic-import card. Because the setting no longer names a single country, project builds no longer fall back to it for symbol resolution — `OeProject.DefaultArtifactCountry` is required on create/edit instead (the base app varies by localisation), and a legacy project without one fails its build with a "set the project's country" message.

## Entities

The current `base_app_*` tables are dropped. Their replacements drop the "base app" naming entirely because the new tables hold any `.app` content, not just Microsoft's Base Application. SQL table names take an `oe_` prefix to keep them clearly Object-Explorer-owned and to avoid colliding with the unrelated template-catalogue `Module` entity (`Domain/Entities/Module.cs`, used in `unified-extensions.md`). C# entity types live in a new `Domain/Entities/ObjectExplorer/` folder and carry an `Oe` prefix, so the type `ALDevToolbox.Domain.Entities.ObjectExplorer.OeModule` is distinct from the existing `ALDevToolbox.Domain.Entities.Module`.

```
Organization ──┐
               └── has many ──> Release  (table: oe_releases)
                                  ├── (label, bc_version, kind, parent_release_id, imported_at, status)
                                  └── has many ──> Module  (table: oe_modules, one per .app)
                                                     ├── (app_id, name, publisher, version, target,
                                                     │    is_test, is_internal, is_language_pack,
                                                     │    dependencies_json)
                                                     ├── has many ──> ModuleFile     (oe_module_files, one per .al kept)
                                                     ├── has many ──> ModuleObject   (oe_module_objects)
                                                     │                   ├── (kind, object_id, name,
                                                     │                   │    source_file_id, line_number)
                                                     │                   ├── has many ──> ModuleSymbol   (oe_module_symbols — procedures, triggers, events, fields)
                                                     │                   └── has many ──> ModuleVariable (oe_module_variables — globals only; resolved type)
                                                     └── has many ──> ModuleReference (oe_module_references)
                                                                       ├── (source_object_id,
                                                                       │    target_app_id, target_object_kind,
                                                                       │    target_object_id, target_object_name,
                                                                       │    reference_kind)
                                                                       └── reference_kind ∈ { variable_type, extends_target,
                                                                                              table_no, return_type, parameter_type,
                                                                                              event_publisher, ... }
```

Service rename landed in step with the schema: `BaseAppService` → `ObjectExplorerService`. The find-references query mechanics live in the same class; a sibling `ReferenceQueryService` may earn its own file when the implementation grows another set of resolution kinds.

Four key design points:

- **`OeRelease` replaces `BaseAppVersion`.** The label (`"BC 25.18"`) and BC platform/application versions come from any one of the contained Modules' manifests (Base App is canonical when present). The unit the user picks in the UI is a Release.
- **`OeModule.app_id` is the cross-Release identity.** Same AppId across two Releases lets us answer "how did `Codeunit 80 "Sales-Post"` evolve from BC 25.15 to 25.18" (Decision 5) without a separate evolution-tracking table.
- **`OeRelease.parent_release_id` enables layered third-party uploads.** A first-party DVD has `parent_release_id = NULL` and `kind = 'first_party'`. A third-party Release (a partner app, a customer's customisation set — the two are the same kind, `'third_party'`, differentiated by `publisher`; the earlier separate `'project'` kind for manual imports was merged away by migration `MergeProjectKindAndAddCal` and `'project'` is now reserved for pipeline-build Releases stamped by `ProjectBuildImporter`) has `parent_release_id` pointing at another Release. Legacy C/AL TXT imports are their own kind, `'cal'` — self-contained snapshots, always imported with no parent, publisher, or project; the import endpoint stamps the kind server-side whenever a `CalTxtFile` is posted, and the user-facing `/object-explorer` gives them their own tab (hidden while the org has none). The user-facing Third-party tab groups by publisher and additionally surfaces each pipeline's latest ready build (see `artifacts.md`); MCP `list_releases` deliberately does *not* include those pipeline entries — agents have `list_pipelines` / `list_pipeline_builds` / `get_project_build` for that surface, and duplicating them in the release list would double-report. Chains are arbitrary depth — Customer X → Continia Document Capture → Continia Core → Base Application is a real four-layer stack that the schema must support, since partner apps routinely depend on each other before bottoming out on Base App. Reference resolution walks the chain via a PostgreSQL recursive CTE; resolver code is depth-agnostic.
- **Reference targets are stored as facts, not as resolved pointers.** `oe_module_references` carries the `(target_app_id, target_object_kind, target_object_id, target_object_name)` triplet exactly as the symbol package reports it. The actual target `OeModule` row is **resolved at query time** by joining against the parent-release chain. This makes retargeting a one-line `UPDATE oe_releases SET parent_release_id = …` instead of a bulk row rewrite, and it lets the same reference row resolve to *different* modules depending on which Release chain it's queried against — which is exactly what powers conflict detection on retarget (see "Retargeting and upgrade-conflict detection" below).

A migration drops `base_app_versions` / `base_app_files` / `base_app_symbols` and any rows therein. The current single-version Object Explorer corpus is small and re-importable; the upgrade cost is "re-ingest your DVDs once".

## Reference resolution

Find references on an `OeModuleObject` runs two queries in parallel and merges:

1. **Cross-module exact (SQL).** The resolution is a JOIN of `oe_module_references` against `oe_modules`, scoped by a recursive CTE over `parent_release_id` so the chain of Releases containing the current browse context plus its ancestors and descendants is in play. Outline:

   ```sql
   WITH RECURSIVE chain(release_id) AS (
       SELECT id FROM oe_releases WHERE id = @currentRelease
       UNION ALL
       SELECT r.parent_release_id FROM oe_releases r
       JOIN chain c ON r.id = c.release_id WHERE r.parent_release_id IS NOT NULL
       UNION ALL
       SELECT r.id FROM oe_releases r
       JOIN chain c ON r.parent_release_id = c.release_id
   )
   SELECT mr.*, src.module_id AS src_module_id, src_m.name AS src_module_name
   FROM oe_module_references mr
   JOIN oe_module_objects src ON src.id = mr.source_object_id
   JOIN oe_modules src_m ON src_m.id = src.module_id
   JOIN oe_modules tgt_m ON tgt_m.app_id = mr.target_app_id
                        AND tgt_m.release_id IN (SELECT release_id FROM chain)
   WHERE tgt_m.id = @targetModuleId
     AND mr.target_object_kind = @kind
     AND mr.target_object_id   = @objectId;
   ```

   The recursive CTE walks both directions: ancestors (so a third-party Release's references *out* into Base App surface up) and descendants (so a first-party Release sees third-party hits *in*). Every row is an authoritative hit; the UI groups by source module and reference kind. No regex, no false positives.

   **The reference scan is scoped by module, not by target app.** The outline above reads naturally as "find every reference to `target_app_id` + kind + id, then keep the ones whose target module is in the chain." Implemented literally that's a trap: `target_app_id` is the *extension GUID*, which is identical in every imported Release (Base App is the same GUID on BC 24, 25, 26…), so an index on `oe_module_references (target_app_id, target_object_kind, target_object_id)` matches the target in **all** Releases at once and the chain filter throws ~99.5% away afterward — on a 200-Release catalogue that read ~200k rows to return a few dozen and tipped the query into a parallel plan that exhausted the container's `/dev/shm`. The fix (`ReferenceQueryService.FindReferencesAsync` / `BuildUsedByAsync`, and the system-call twin `FindSystemReferencesAsync` over `oe_module_system_references`): resolve the winning module ids first, then filter `mr.module_id = ANY(<winning ids>)` so the scan is bounded to the chain up front, and **split the id-vs-name match into a `UNION ALL`** (not an `OR`) so each branch uses its own module-scoped index — `ix_oe_module_references_module_target (module_id, target_object_kind, target_object_id)` for the id-branch and the partial `ix_oe_module_references_module_target_name (module_id, target_object_kind, target_object_name) WHERE target_object_id IS NULL` for the name-branch (`oe_module_system_references` carries the same pair). A single `OR` would let the planner build one `BitmapOr` dragged back onto the app-keyed indexes; the `UNION ALL` keeps the two branches on their own index paths. `target_app_id` stays as a recheck filter (the module-scoped indexes are keyed on kind + id|name only, and a different app can ship the same object id). This took the canonical Entry Summary "Used by" from ~7.8 s cold / 188 ms warm to ~10 ms.

   **Same-AppId shadowing.** When the chain contains more than one Module with the same `app_id` at different versions (legal — idempotency only deduplicates exact `(AppId, Version)` matches, so a child Release can legitimately pull in a newer or older copy of an ancestor's app), the resolver picks the one **closest to the current Release** in the chain. The CTE assigns a `chain_depth` to each step; the join's `ORDER BY chain_depth ASC LIMIT 1` per `(target_app_id, target_object_kind, target_object_id)` triplet picks the winner. The shadowed Module is still reachable when its declaring Release is browsed directly — shadowing is per-query-context, not a permanent hide.

   **Dependency links (#901).** A pipeline build that fetches a vendor's symbols from the public feeds ingests each package once per (app id, version) as its own shared `third_party` Release (see `object-explorer-project-builds.md`, "Ingest the resolved symbols"), parented onto the Microsoft Release the build resolved. That vendor Release is a *sibling* of the project Release, not an ancestor, so the build also writes a row to `oe_release_dependencies (release_id, dependency_release_id)` linking the project Release to it. The chain CTE (`ReleaseAncestrySql.Chain`) has two seeds: the Release itself, and the Releases it links; from both it follows the ordinary `parent_release_id`. The linear parent chain is otherwise unchanged — this is a link, not a second parent. The CTE's `depth` is a rank rather than a hop count: a dependency link costs one and a parent step two, so the order is the Release (0), its dependency Releases (1), its own parent (2), a dependency's parent (3), and so on. Shadowing stays "closest wins": our own objects beat the vendor's, the vendor's beat Microsoft's, and where a shared vendor Release was first ingested on top of a different Business Central version than this build's, our own Microsoft parent still beats the vendor's. A Release reachable by two paths keeps its lowest rank. Two limits, deliberately:
   - **Links are followed one level only.** A dependency Release's own links are never walked; symbol packages are flat, and a build links every package the feed resolved for it, transitive ones included.
   - **A rank tie between two Releases carrying the same app id goes to the higher release id** (the newer ingest). A single build never links two versions of one app, so this only matters for hand-written links.

   The raw chain SQL carries the tenant fence described on `ChainObjectResolution`: the build writes links inside its own org scope, and the dependency seed does not trust the row anyway — it joins both ends to `oe_releases` and follows a link only to a Release in the seed's own organisation.

   **What a symbols-only Release contains.** A feed package's `.app` holds `NavxManifest.xml`, `SymbolReference.json`, `DocComments.xml` and no `src/`, so its Release gets modules, objects, fields, keys and procedure signatures and **zero file rows**. That is enough for search, outlines and — through the link — for our own code's references into the vendor to resolve at ingest and in find-references. It gives no source view and no call graph *inside* the vendor app: a module without source contributes targets, never outgoing call sites. `DocComments.xml` is not read yet.
2. **Intra-module source scan.** The existing receiver-aware scanner (the band-aid plan) runs against the declaring module's source files. The global variable map is **read from `OeModuleVariable`** instead of being regex-extracted — that's the upgrade. Procedure-local vars still come from the source-side regex. Results from this pass are split into the existing `Likely` / `PossiblyRelated` buckets.

The "Possibly related" bucket becomes much smaller because the heuristic only runs against one module's source, where the symbol package's globals already eliminate most receiver ambiguity.

**Resolving a *name* to a base object walks the same chain.** The reference-target join above resolves a `(target_app_id, object)` triplet you already hold; the inverse direction — turning a clicked token or a `find_references` target name into an object identity — has to walk the ancestry too, or a base object referenced from a project Release resolves to nothing (it lives in the parent). `ChainObjectResolution` (used by `SourceViewerService` go-to-definition, `ReferenceResolver`'s object/member/extracted-ref strategies, and the MCP `find_references` / `find_system_references` owner lookups) runs the same `WinningModules` shadowing CTE: a child sees an ancestor's object only when it doesn't ship its own, closest-depth wins, and base-object kinds sort ahead of their extension kinds so a bare `Customer` token lands on the table rather than a same-named `tableextension`. Without this, clicking a base table from a project Release (e.g. Dansani on BC 14.52) underlines nothing and go-to-definition dead-ends. Note `get_object_outline` still resolves seed-only — targeting a base object's outline from a child Release is a remaining follow-up.

Find references on an `OeModuleSymbol` (procedure / field) is the same shape with one extra hop: the cross-module pass starts by finding every `OeModuleVariable` whose subtype matches the object that *holds* the symbol, then narrows to call sites of the named member via source scanning of those holding modules. This is where the receiver-aware logic still earns its keep — but now bootstrapped from authoritative type data.

## UI

The Object Explorer's hierarchy becomes:

```
Object Explorer › BC 25.18 › Base Application › Agent
                  ↑              ↑                ↑
                  Release        Module           Object
```

A Release picker replaces the current version dropdown. A Module picker sits between Release and Object — defaults to "all modules" so the existing flat browse still works, but filters down once a module is chosen. The breadcrumb shows the full path. The file viewer's URL becomes `/object-explorer/{releaseId}/{moduleId}/files/{fileId}`.

Find-references results page shows two sections:

- **Cross-module references** (the new top section) — grouped by module, each module's hits grouped by source object. No "Possibly related" — every row is an exact reference.
- **Intra-module references** — the existing two-bucket display (Likely / Possibly related) scoped to the declaring module, fed by source scanning.

`_Exclude_`-flagged modules are styled the same as any other (Decision 4). A small badge ("internal" or similar) on the module chip is enough.

### Upload flow for third-party Releases

When the third-party upload UI lands (deferred milestone — schema ships earlier), the form is hybrid: it reads the uploaded `.app`'s `NavxManifest.xml`, pulls the `Dependencies` block plus the `Application` version constraint, and looks for existing Releases that satisfy them. The form's "Parent Release" dropdown defaults to the inferred match (annotated "(inferred from manifest)") with the full list of existing Releases available as an override. Multiple satisfying Releases — common when several BC versions are imported — surface the closest version match by default; the user can pick a different one. No automatic match available (no Release satisfies the dependencies) means the user must pick explicitly or the upload is refused.

### Retargeting and upgrade-conflict detection

A third-party Release's parent is mutable. `Releases › <release> › Settings` exposes a "Retarget to a different Release" action that runs `UPDATE oe_releases SET parent_release_id = @newParent WHERE id = @release`. Because reference rows store target facts (AppId + ObjectKind + ObjectId + ObjectName) rather than resolved Module pointers, every subsequent find-references query re-resolves transparently against the new parent chain. No row rewriting, no re-ingest.

This makes upgrade-conflict detection a derived feature, no separate machinery needed. After retargeting Customer X from BC 25.18 to BC 25.20, the same query that powers Find references also answers "what *broke*":

```sql
-- References on a retargeted Release whose target no longer resolves
SELECT mr.target_app_id, mr.target_object_kind, mr.target_object_name, COUNT(*) AS broken_count
FROM oe_module_references mr
JOIN oe_module_objects o ON o.id = mr.source_object_id
JOIN oe_modules m ON m.id = o.module_id
WHERE m.release_id = @retargetedRelease
  AND NOT EXISTS (
      SELECT 1 FROM oe_modules tgt
      WHERE tgt.app_id = mr.target_app_id
        AND tgt.release_id IN (SELECT release_id FROM chain_for(@retargetedRelease))
        AND EXISTS (SELECT 1 FROM oe_module_objects to_
                    WHERE to_.module_id = tgt.id
                      AND to_.kind = mr.target_object_kind
                      AND (to_.object_id = mr.target_object_id OR to_.name = mr.target_object_name))
  )
GROUP BY mr.target_app_id, mr.target_object_kind, mr.target_object_name
ORDER BY broken_count DESC;
```

The result is exactly an upgrade compatibility report: "Codeunit X went away", "Field Y was renamed", "Procedure Z's signature changed and its overload no longer matches". The UI for this is a `Releases › <release> › Compatibility` page that runs the above against the current parent chain, grouped by target module. Empty result = the retarget is a clean lift. Non-empty result = work to do.

Two design notes on retargeting:

- Allow retarget only on Releases with `kind ∈ {'third_party', 'project'}`. First-party Releases have `parent_release_id = NULL` by definition; the column stays null. The form refuses to expose retargeting on first-party rows.
- Retargeting doesn't break the symmetric resolution from the parent's side. After Customer X moves from BC 25.18 to BC 25.20, BC 25.18 stops seeing Customer X's calls *into* it (because the chain no longer joins them) and BC 25.20 starts seeing them. Both directions are correct under the new chain.

## Migration from the current shape

The current `base_app_*` tables and the `BaseAppImportService` source-ZIP path are dropped. Specifically:

- `base_app_versions`, `base_app_extensions`, `base_app_files`, `base_app_symbols`: dropped in `20260602000000_DropBaseAppTables`. EF entities removed in the same PR. The new tables (`oe_releases`, `oe_modules`, `oe_module_files`, `oe_module_objects`, `oe_module_symbols`, `oe_module_variables`, `oe_module_references`) were created earlier in `20260601000000_AddObjectExplorerTables`.
- `BaseAppImportService` is replaced by `ReleaseImportService`. The minimal-API endpoint that fronted the source-ZIP upload is removed; the new `POST /admin/object-explorer/import` replaces it.
- `BaseAppService` is replaced by `ObjectExplorerService`. `FindReferencesAsync` runs against `oe_module_references` joined through a recursive-CTE chain walk; cross-module references are resolved by stored `target_app_id` triplets at write time rather than re-resolved at query time. The receiver-aware band-aid classifier (`ClassifyHitConfidence` and friends) is retired entirely — variable subtypes are exact in the symbol package, so the heuristics it papered over no longer have anything to disambiguate.
- `AlSymbolExtractor`, `AlGoToDefinitionLocator`, `AlDeclarationParser`, `AlResolvableTokenScanner`: removed along with the legacy file viewer / inspector / go-to-definition surface. The shipped Object Explorer relies on the symbol package's own coordinates plus a single regex pass at ingest time for object-header line numbers; intra-procedure navigation is not (yet) re-implemented for the new surface.
- `SymbolReindexer`, `SymbolReindexQueue`: removed. Re-ingest happens by re-uploading the DVD; idempotency on `(release_id, app_id, version, file_hash)` makes that cheap.
- Razor pages and URL routes under `Components/Pages/ObjectExplorer/` shift from `{versionId}` to `{releaseId}` / `{moduleId}` / `{objectId}` segments. Old links break — there are no production redirects to preserve.

Forward-only migration. No data preservation — re-ingest your DVDs. There's no production traffic on this surface large enough to justify the migration work.

## Comparison

The Release page hosts a fourth search scope, `Compare with release`. Picking a release on the right and a direction (`A → B` or `B → A`) flattens module-and-file-level differences into a single table of `(Module, Path, Status, Action)` rows. The Status column carries `added` / `removed` / `modified`; the Action column opens the diff viewer for `modified` rows or the underlying file viewer for the others. Service entry: `ObjectExplorerService.CompareReleaseFilesFlatAsync(leftReleaseId, rightReleaseId)`, which composes `CompareReleasesAsync` (module-level bucketing by `AppId`) with `CompareModuleFilesAsync` (file-level bucketing by canonical `Path`). Files match by `OeModuleFile.ContentHash`; the canonical path comes from `AppPackageReader.CanonicalizeSourcePath`.

The single-file viewer also carries a `Compare with release` `<select>` in its top-right corner. The dropdown is populated server-side from `GetCompareTargetsAsync(currentFileId)`, which joins `oe_module_files → oe_modules → oe_releases` on `(AppId, Path)` and only surfaces releases that actually hold a matching file — the picker is dead-link-free.

Both compare entry points (the source-viewer picker and the objects-grid row action) order the panes **chronologically — older release on the left** (`ReleaseComparisonService.IsOlderRelease`: BC version when both parse, import time otherwise), so green always reads "new in the newer version" regardless of which side the user started from. The explicit `left`/`right` query params and the Swap button remain a manual override.

An objects-grid results row carries a `Compare with...` action too, and it means "compare THIS object": picking a release in the shared modal resolves the object's counterpart by identity — `ReleaseComparisonService.FindObjectFileInReleaseAsync`, keyed `(Kind, ObjectId)` with a name fallback for id-less objects, the same key the object-level release compare uses — and navigates straight into the side-by-side file diff. Identity matching (not `(AppId, Path)`) is what lets two independently imported C/AL releases line up, since each is its own synthetic module. The release-wide diff remains a page-level concern, reached through the `Compare with release` search scope.

The side-by-side diff viewer lives at `/object-explorer/compare/file?left={id}&right={id}` and renders two `.source-viewer.source-viewer--compare` roots inside a grid. DiffPlex's `SideBySideDiffBuilder` produces the line-aligned model server-side; the page serialises a compact `[{line, kind}, …]` array onto each pane's `data-diff` attribute. `wwwroot/source-viewer.js` reads it on mount, converts to `{line: cssClass}` and forwards it to the existing `mountReadOnly({ lineDecorations })` extension point — no new CodeMirror surface, the existing line-decoration path already implemented for full-line backgrounds in diff scenarios. Modified lines additionally carry intra-line word ranges (`data-word-diff`, from DiffPlex's `SubPieces`) painted as stronger-tint `cm-diff-word` marks, so the changed words stand out inside the tinted row. Compare mounts disable code folding — the alignment fillers are computed server-side against the full text, so folding one pane would silently break the line-up. An optional `?line=N` (the right pane's top line) deep-links a reading position; the viewer mirrors the current position back into the URL via debounced `replaceState`, so a refresh keeps your place and the address bar is always shareable.

Scroll-sync between the two panes is gated on `roots.length === 2 && first root has source-viewer--compare`. Vertical sync is line-anchored (top line + filler arithmetic, `syncComparePanes` in `code-editor.js`), and only the pane the pointer is over drives it — the other pane's scroll events are ignored wholesale, because a programmatic sync moves the destination twice (scrollIntoView hop + measured correction) and per-event echo detection can't reliably tell those from user scrolls.

Out of scope for v1: object-level diff (added/removed/renamed objects), symbol-level diff (procedure signature changes), reference-impact rollups. The underlying schema supports these — the `ObjectExplorerService.Compare*` surface is the staging point — but the UI deliberately doesn't surface them until someone needs them.

## Outline dependencies

The source-viewer outline carries two extra collapsible sections at the bottom (Issue #148):

- **Using** — outgoing dependencies of the file's primary objects. UNION of `oe_module_variables.target_*` (typed globals) and `oe_module_references` rows, resolved through the parent-release ancestor chain via the same recursive CTE pattern `FindReferencesAsync` uses. Targets without ingested source render with the kind badge but no link; tooltip says `"no source available"`.
- **Used by** — incoming references. For each of the file's primary objects, walk `oe_module_references` across the visible release chain and surface the caller's identity (module + object). Same shadowing rules as find-references.

Self-references (`Rec` / `xRec` on tables; same-object internal calls) are filtered on both sides by matching `(TargetAppId, TargetObjectKind, TargetObjectId|Name)` against the file's own objects.

The shell is rendered SSR; the data is lazy-loaded via one `GET /api/object-explorer/files/{id}/dependencies` after `mountReadOnly` returns, so first paint is unaffected. Empty sections collapse and show `(none)`.

## Open questions

1. **Dependency graph in the UI.** Each `OeModule` carries `dependencies_json` from its manifest. Worth surfacing a "what does this module depend on / what depends on this module" view, or defer? Leaning defer until someone asks.
2. **Storage growth across many Releases.** Two BC versions = two full DVDs of source. Per-Release deletion is supported, but no automatic cleanup. Worth a SiteAdmin pruning UI from day one. Deletion of a parent Release should be refused while any child Release still points at it (PostgreSQL FK with `ON DELETE RESTRICT`).
3. **Symbol package vs source disagreements.** When a `.app`'s embedded source and its `SymbolReference.json` disagree on object IDs or names (shouldn't happen for Microsoft-shipped `.app`s, could happen for partner-shipped ones with mismatched artifacts), which wins? Recommend: symbol package wins, log a warning per discrepancy.
4. **Conflict-report fidelity.** The retarget conflict query above flags references whose `(target_app_id, target_object_kind, (target_object_id OR target_object_name))` no longer matches anything in the new chain. That catches object removals and renames but not deeper semantic breaks — a procedure that still exists with the same name but whose parameter list changed will not flag here. Worth picking up signature mismatches in a follow-up: the symbol package's `Methods[].Parameters` already gives us typed signatures per Release, so a procedure-signature-diff pass across two Releases of the same AppId is feasible but adds complexity. Defer until someone hits the case.

## Verification

The ingest pipeline gets test coverage in `ALDevToolbox.Tests/ObjectExplorer/`:

- `ReleaseImportServiceTests.IngestsDkCoreSampleEndToEnd` — fixtures: the DK Core `.app` and `.Source.zip` already inspected during this design. Assert: one Release row, one Module row with the right AppId/Version, four `OeModuleObject` rows for the codeunits, three for the page extensions, seven for the report extensions; `OeModuleVariable` rows for the report extensions' globals with resolved `(ModuleId=Base App AppId, Id=…)` subtype; no `_Exclude_`/test artifacts present.
- `ReleaseImportServiceTests.IngestsOIOUBLWithTableAndExtensions` — fixtures: OIOUBL `.app` plus separate source ZIP. Assert: the table's two fields exist as `OeModuleSymbol` rows; tableextension `TargetObject` is captured as an `OeModuleReference` row pointing at Base App's `Finance Charge Memo Line`; intra-module codeunit-to-codeunit reference (`OIOUBLDocumentEncode` var) resolves to the OIOUBL module itself.
- `ReleaseImportServiceTests.SkipsTestAndExcludeFiltersOnly` — synthetic archive with `Foo/Source/Microsoft_Foo.app`, `Foo/Test/Microsoft_Foo Tests.app`, `Foo/Source/Microsoft__Exclude_Internal.app`. Assert: Foo is ingested, Foo Tests is skipped, `_Exclude_Internal` is ingested with `IsInternal = true`.
- `ReleaseImportServiceTests.IgnoresDuplicateAppIdVersion` — same `.app` twice in two folders. Assert: one Module row, second occurrence silently skipped.
- `ReleaseImportServiceTests.HandlesPartnerAppWithSeparateSourceZip` — synthetic `.app` with `IncludeSourceInSymbolFile="false"`; assert source comes from the paired `.Source.zip`.
- `ObjectExplorerServiceTests.FindReferences_returns_cross_module_exact_hits` — two-module single-Release fixture (a "consumer" module that declares `var X: Codeunit "Foo"; X.Bar()` plus a "library" module that declares `Codeunit "Foo" with procedure Bar`). Assert: the consumer's variable shows up as a cross-module reference with `reference_kind = variable_type`.
- `ObjectExplorerServiceTests.FindReferences_resolves_across_parent_release` — two-Release fixture: parent first-party Release holds the library module declaring `Codeunit "Foo"`; child third-party Release holds the consumer module. Browsing the consumer's `Bar` procedure, assert the parent module shows up; browsing the library's `Bar` from the parent Release with the child attached, assert the child's call site shows up. Then assert that browsing the parent Release **without** the child attached doesn't surface the child's hits (releases are isolated unless linked by parent).
- `ObjectExplorerServiceTests.FindReferences_resolves_through_multi_layer_chain` — three-Release fixture mirroring a real BC stack: layer 0 = "Base App" with `Codeunit "Foo"`; layer 1 = "Continia Core" (third-party) on top of layer 0, declaring `Codeunit "Cont Helper"` that calls Foo; layer 2 = "Continia Document Capture" (third-party) on top of layer 1, declaring a codeunit that calls Cont Helper. Assert: from layer 2, references to `Foo` resolve up through layer 0 (skipping nothing); from layer 0, browsing `Foo` shows the call site in layer 1 *and* the indirect reference is **not** doubled up from layer 2.
- `ObjectExplorerServiceTests.Retarget_updates_resolution_without_rowrewrite` — start with a third-party Release whose `parent_release_id` points at BC 25.18. A reference into Base App resolves to that Release's `Codeunit "Foo"`. Update `parent_release_id` to a fixture "BC 25.20" Release (also containing `Codeunit "Foo"` but at a different `OeModule.id`). Same find-references query now returns the 25.20 Module without touching `oe_module_references` rows. Assert `oe_module_references` rows are byte-identical before and after.
- `ObjectExplorerServiceTests.Retarget_surfaces_broken_references_in_compatibility_report` — retarget a third-party Release from a parent that has `Codeunit "Foo"` to a parent that has removed it (or renamed it). The compatibility-report query returns one broken-reference row keyed on the removed object; the same retarget back resolves to zero broken rows. Validates the "upgrade conflict detector" feature falls out of the resolution model without additional code.

End-to-end smoke (manual, after CI green): import a real BC 25.18 DVD, open `Codeunit "Sales-Post"`, click Find references. Confirm hits from every Microsoft first-party extension that uses Sales-Post (Excel Reports, OIOUBL where applicable, EDocument connectors, etc.) appear in the cross-module section, with no false positives. Compare against the band-aid-only output of the current source-only Base App import to quantify precision lift.
