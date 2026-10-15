# Domain model

This document specifies the entities, their relationships, and the PostgreSQL schema. EF Core code-first migrations are the expected mechanism for creating the schema; the SQL types below are the target shape.

The schema was a SQLite one through v1 (M1–M15) and switched to PostgreSQL 18 in P4.16. The two changes that matter at this layer are:

- `defaults_json` and `app_source_cop_json` are now `jsonb` columns. The C# value-converter still serialises through a `string` round-trip, so application code is unchanged. No JSONB GIN index yet — add one when a query needs it.
- All `DateTime` columns are `timestamp with time zone`. Application code is already disciplined about `DateTime.UtcNow` / `DateTimeKind.Utc` literals, and `OnModelCreating` pins the column type so a new column added without an explicit type still lands correctly.

The current shape is the **unified-extensions model** (Issue #54). A workspace is N extensions: required template-declared ones, optional template-declared ones the user ticks, and one cloned extension per selected catalogue module. The spec for the data model lives in `unified-extensions.md`; the rest of this document maps that spec onto the EF entities and the PostgreSQL schema. Migration `20260514000000_UnifyExtensions` collapses the pre-unified `template_folders` / `template_files` / `template_module_folders` / `template_module_files` rows into the new tables and is forward-only.

## Entities

```
RuntimeTemplate ──┐
                  ├── has many ──> WorkspaceExtension (ordered)
                  │                    ├── has many ──> WorkspaceExtensionFolder (recursive tree)
                  │                    │                    └── has many ──> WorkspaceExtensionFile (per folder, any depth)
                  │                    └── has many ──> WorkspaceExtensionDependency
                  │                                          (one-of: ref_extension_path / ref_module_key / lit_*)
                  ├── has many ──> RuntimeTemplateDefaultModule (ordered) ──> Module
                  └── has one ────> TemplateDefaults (JSON column on the row;
                                       carries publisher, application, platform,
                                       extension_prefix, affix, affixType, …)

Module ───────────┐
                  ├── has many ──> ModuleDependency (ordered)
                  ├── has many ──> ModuleExtensionFolder (recursive tree)
                  │                    └── has many ──> ModuleExtensionFile (per folder, any depth)
                  └── id-range fields on the row

WellKnownDependency  (flat list, used by New Extension flow's dep picker)

AuditLogEntry        (one row per change to any of the above)
```

`WorkspaceExtension.Path` is the stable identifier — dependencies referencing another extension by `ref_extension_path = "Core"` resolve through it. `WorkspaceExtension.Required` is `true` for always-emitted extensions and `false` for opt-in checkboxes on the New Workspace form. See `unified-extensions.md` for the full per-extension shape (id-range allocation, name template, optional per-extension `application` / `runtime` overrides).

Module-supplied extensions clone the module's `ModuleExtensionFolder` tree into the workspace at generation time; the catalogue rows stay normalised. A workspace is the concatenation of the template's required extensions, the optional extensions the user ticked, and one cloned extension per selected module — emitted in the same display order they appear in those three lists.

## Tables

All identifiers are PostgreSQL snake_case, configured explicitly in `AppDbContext.OnModelCreating`. EF's pluralising / casing conventions are *not* in play — never rely on them.

### `runtime_templates`

The primary template entity — corresponds one-to-one with a runtime version (or rather, a *named* template; you might have multiple per runtime in theory, though we don't expect to).

| Column                  | Type                          | Notes                                              |
|-------------------------|-------------------------------|----------------------------------------------------|
| `id`                    | INTEGER PK                    | Identity column                                    |
| `organization_id`       | INTEGER NOT NULL              | Multi-tenant key; rows are scoped per-org via the EF query filter |
| `key`                   | TEXT NOT NULL                 | Unique within an org. Used in URLs and the dropdown. e.g. "runtime-15" |
| `runtime`               | TEXT NOT NULL                 | The AL runtime version, e.g. "15"                  |
| `name`                  | TEXT NOT NULL                 | Display name in the dropdown                       |
| `description`           | TEXT                          | Caption under the dropdown selection               |
| `defaults_json`         | JSONB NOT NULL                | The `TemplateDefaults` blob — `publisher`, `application`, `platform`, `extension_prefix`, `affix`, `affixType`, `features`, `supportedLocales`, `resourceExposurePolicy`, etc. `application` / `platform` moved off the row in Issue #54. |
| `app_source_cop_json`   | JSONB NOT NULL                | The AppSourceCop.json contents — `mandatoryPrefix`, `supportedCountries`, … |
| `core_id_range_from`    | INTEGER NOT NULL              | Default 90000 — the plan's Core range when the first auto-allocated extension consumes it |
| `core_id_range_to`      | INTEGER NOT NULL              | Default 90999                                      |
| `module_id_range_start` | INTEGER NOT NULL              | Default 91000 — first auto-allocated subsequent slice |
| `module_id_range_size`  | INTEGER NOT NULL              | Default 200 — span per auto-allocated extension / module |
| `default_application_version_id` | INTEGER                | Optional FK → `app_versions.id`; nullable          |
| `deprecated`            | BOOLEAN NOT NULL              | Hidden from end-user dropdowns when true           |
| `is_default`            | BOOLEAN NOT NULL DEFAULT false | At most one active default per org (filtered unique index) |
| `created_at`            | TIMESTAMPTZ NOT NULL          |                                                    |
| `updated_at`            | TIMESTAMPTZ NOT NULL          |                                                    |
| `deleted_at`            | TIMESTAMPTZ                   | Null = active. Soft-delete                         |

Indexes:
- `(organization_id, key)` UNIQUE — per-org uniqueness of the slug.
- `(organization_id, is_default) UNIQUE WHERE is_default = true AND deleted_at IS NULL` — at most one active default per org.

`defaults_json` and `app_source_cop_json` are JSONB columns rather than normalised tables because they're write-once-edit-rarely metadata, and the structure is closed (defined by the AL/BC ecosystem). Normalising them would mean a schema migration every time AL adds a new app.json field. EF Core uses `HasConversion<string>()` with `JsonSerializer` to make these typed in C# while stored as jsonb.

### `workspace_extensions`

One row per declared extension on a template. The template's required extensions, plus its optional extensions (`required = false`) the user can tick on New Workspace, plus the implicit module clones (which don't live here — see `module_extension_folders` instead).

| Column           | Type             | Notes                                                          |
|------------------|------------------|----------------------------------------------------------------|
| `id`             | INTEGER PK       | Identity                                                       |
| `organization_id`| INTEGER NOT NULL |                                                                |
| `template_id`    | INTEGER FK NOT NULL | → `runtime_templates.id`, cascade delete                    |
| `ordering`       | INTEGER NOT NULL | Position in the template's extension list                      |
| `path`           | TEXT NOT NULL    | Stable identifier (folder name in the ZIP, dependency ref target). Single segment, no `/` |
| `name_template`  | TEXT NOT NULL    | Mustache template for the rendered extension name, e.g. `"{{extension_prefix}} Core"` |
| `required`       | BOOLEAN NOT NULL | True = always emitted; false = opt-in checkbox                  |
| `application`    | TEXT             | Optional per-extension override of the template-wide `defaults.application` |
| `runtime`        | TEXT             | Optional per-extension override of the template-wide runtime    |
| `id_range_from`  | INTEGER          | Optional explicit start. When both this and `id_range_to` are set, the generator uses them verbatim. |
| `id_range_to`    | INTEGER          | Optional explicit end                                          |

Indexes:
- `(organization_id, template_id, ordering)` — ordered enumeration.
- `(template_id, path)` UNIQUE — paths are unique within a template.

### `workspace_extension_folders`

Recursive folder tree, one row per folder. Top-level folders have `parent_folder_id IS NULL`; nested folders point at their parent. Both the parent FK and the owning-extension FK are kept on every row (denormalised) so leaf queries don't have to walk the chain.

| Column                | Type             | Notes                                                       |
|-----------------------|------------------|-------------------------------------------------------------|
| `id`                  | INTEGER PK       |                                                             |
| `organization_id`     | INTEGER NOT NULL |                                                             |
| `workspace_extension_id` | INTEGER FK NOT NULL | → `workspace_extensions.id`, cascade delete            |
| `parent_folder_id`    | INTEGER FK       | → `workspace_extension_folders.id`, cascade delete. Null on top-level folders. |
| `ordering`            | INTEGER NOT NULL | Position among siblings                                     |
| `path`                | TEXT NOT NULL    | Single path segment (no `/`)                                |

Indexes:
- `(workspace_extension_id, parent_folder_id, ordering)` for ordered enumeration.
- `(parent_folder_id, path) UNIQUE WHERE parent_folder_id IS NOT NULL` — siblings can't collide on path.
- `(workspace_extension_id, path) UNIQUE WHERE parent_folder_id IS NULL` — top-level siblings can't collide either.

Because EF's `Include`/`ThenInclude` only recurses one level at a time, code that loads the tree should issue a single flat query and reassemble client-side — see `GenerationService.AssembleFolderTree` and `TemplateImportService.HydrateExtensionTreeAsync`.

### `workspace_extension_files`

One row per file attached to a folder. Files can attach at any depth, including intermediate folders.

| Column                          | Type             | Notes                                                  |
|---------------------------------|------------------|--------------------------------------------------------|
| `id`                            | INTEGER PK       |                                                        |
| `organization_id`               | INTEGER NOT NULL |                                                        |
| `workspace_extension_folder_id` | INTEGER FK NOT NULL | → `workspace_extension_folders.id`, cascade delete  |
| `ordering`                      | INTEGER NOT NULL | Position within the folder                             |
| `path`                          | TEXT NOT NULL    | Filename (no `/`, no `..`). Mustache substitution runs at generation time, not at write time. |
| `content`                       | TEXT NOT NULL    | UTF-8 text. Binary assets are not supported in v1.    |
| `is_example`                    | BOOLEAN NOT NULL | When true, the file ships only if the plan's `IncludeExamples` is set. |

Indexes:
- `(workspace_extension_folder_id, ordering)` for ordered enumeration.
- `(workspace_extension_folder_id, path)` UNIQUE — a folder can't carry two files at the same name.

Editing `path` or `content` flows through the same `TemplateAuthoring` pipeline both authoring surfaces use. The TOML editor expresses the same data via `[[extensions.folders.files]]` blocks at any depth (see `templates-and-seeding.md`).

### `workspace_extension_dependencies`

One row per `[[extensions.dependencies]]` entry. Each row sets **exactly one** of three reference shapes, enforced by a CHECK constraint:

| Column                   | Type             | Notes                                                  |
|--------------------------|------------------|--------------------------------------------------------|
| `id`                     | INTEGER PK       |                                                        |
| `organization_id`        | INTEGER NOT NULL |                                                        |
| `workspace_extension_id` | INTEGER FK NOT NULL | → `workspace_extensions.id`, cascade delete         |
| `ordering`               | INTEGER NOT NULL | Position within the extension's dependency list        |
| `ref_extension_path`     | TEXT             | Intra-workspace ref: another extension's `path`        |
| `ref_module_key`         | TEXT             | Catalogue ref: a `modules.key`                         |
| `lit_id`                 | TEXT             | Literal ref: an AL app GUID                            |
| `lit_name`               | TEXT             | Literal ref: paired with `lit_id`                      |
| `lit_publisher`          | TEXT             | Literal ref: paired with `lit_id`                      |
| `lit_version`            | TEXT             | Literal ref: paired with `lit_id`                      |

Indexes:
- `(workspace_extension_id, ordering)` for ordered enumeration.

CHECK constraint `ck_workspace_extension_dependencies_one_ref`: exactly one of `ref_extension_path`, `ref_module_key`, `lit_id` is non-null per row. The service layer pre-validates the same shape so the DB constraint is a belt-and-braces guard, not a live error surface.

### `module_extension_folders`

Mirror of `workspace_extension_folders` for the catalogue side: each module declares its own recursive folder tree, cloned into the workspace at generation time when the user selects the module.

| Column            | Type             | Notes                                                       |
|-------------------|------------------|-------------------------------------------------------------|
| `id`              | INTEGER PK       |                                                             |
| `organization_id` | INTEGER NOT NULL |                                                             |
| `module_id`       | INTEGER FK NOT NULL | → `modules.id`, cascade delete                           |
| `parent_folder_id`| INTEGER FK       | → self, cascade delete. Null on top-level folders.          |
| `ordering`        | INTEGER NOT NULL |                                                             |
| `path`            | TEXT NOT NULL    | Single segment                                              |

Indexes mirror `workspace_extension_folders`:
- `(module_id, parent_folder_id, ordering)`
- `(parent_folder_id, path) UNIQUE WHERE parent_folder_id IS NOT NULL`
- `(module_id, path) UNIQUE WHERE parent_folder_id IS NULL`

### `module_extension_files`

Mirror of `workspace_extension_files`: per-folder file content on the module side.

| Column                       | Type             | Notes                                                  |
|------------------------------|------------------|--------------------------------------------------------|
| `id`                         | INTEGER PK       |                                                        |
| `organization_id`            | INTEGER NOT NULL |                                                        |
| `module_extension_folder_id` | INTEGER FK NOT NULL | → `module_extension_folders.id`, cascade delete     |
| `ordering`                   | INTEGER NOT NULL |                                                        |
| `path`                       | TEXT NOT NULL    | Filename                                               |
| `content`                    | TEXT NOT NULL    | UTF-8 text                                             |
| `is_example`                 | BOOLEAN NOT NULL |                                                        |

Indexes:
- `(module_extension_folder_id, ordering)`
- `(module_extension_folder_id, path)` UNIQUE

### `runtime_template_default_modules`

Pre-selected modules per template (Milestone P2.1). When a user picks a template on the New Workspace form, the modules listed here are ticked automatically — they have to opt out rather than in.

| Column                | Type             | Notes                                         |
|-----------------------|------------------|-----------------------------------------------|
| `id`                  | INTEGER PK       |                                               |
| `organization_id`     | INTEGER NOT NULL |                                               |
| `runtime_template_id` | INTEGER FK NOT NULL | → `runtime_templates.id`, cascade delete   |
| `module_id`           | INTEGER FK NOT NULL | → `modules.id`, cascade delete             |
| `ordering`            | INTEGER NOT NULL | Display order in the admin's chosen sequence  |

Indexes:
- `(runtime_template_id, ordering)` for ordered enumeration.
- `(runtime_template_id, module_id)` UNIQUE — the same module can't appear twice on a template's default list.

The relation is purely cosmetic for end-users — generation does not consult it. A user who deselects a default module gets a workspace without that module, just as they would by leaving an unticked module unticked. Soft-deleted modules are silently dropped from the pre-selection at form load time.

Deleting a module leaves its rows here. The template editor and its TOML leave a soft-deleted module out (it has no checkbox to untick), and saving keeps its row, ordered after the live ones, so restoring the module makes it a default again (#1326).

The write-path reconciler in `TemplateService.UpdateAsync` matches existing rows by `module_id` (the natural identity), **not** by list position — position-based mutation would trip the `(runtime_template_id, module_id)` unique index when two rows swap places, and EF would refuse with a circular-dependency error. Reorders rewrite `ordering`; `module_id` stays put on each row.

### `runtime_template_included_files`

Per-template opt-in for the organisation's always-included file library. A new `OrganizationFile` row is **off-by-default** for every template until the admin explicitly ticks it on the template editor.

| Column                | Type             | Notes                                            |
|-----------------------|------------------|--------------------------------------------------|
| `id`                  | INTEGER PK       |                                                  |
| `organization_id`     | INTEGER NOT NULL |                                                  |
| `runtime_template_id` | INTEGER FK NOT NULL | → `runtime_templates.id`, cascade delete      |
| `organization_file_id`| INTEGER FK NOT NULL | → `organization_files.id`, cascade delete     |
| `ordering`            | INTEGER NOT NULL | Admin-chosen emit sequence inside the template   |

Indexes:
- `(runtime_template_id, ordering)` for ordered enumeration.
- `(runtime_template_id, organization_file_id)` UNIQUE — a file can't be listed twice on the same template.

Same reconciler shape as `runtime_template_default_modules`: matches existing rows by `organization_file_id`. The `WorkspaceZipBuilder` filters `OrganizationConfig.Files` through this join at generation time; the New Workspace live preview folds the included files into the workspace-root tree so what the user sees is what they get.

### `runtime_template_root_folders`

The folders a template puts at the workspace root with nothing in them. Every other root-level folder comes from an emitted extension and carries an `app.json`, so this table is the only way to declare an empty one — the symbol cache an AL build fills in (`.alpackages`), a team's `docs/` convention. Not a join: each row names a path and nothing else, so there is no shared library to opt into.

| Column                | Type             | Notes                                            |
|-----------------------|------------------|--------------------------------------------------|
| `id`                  | INTEGER PK       |                                                  |
| `organization_id`     | INTEGER NOT NULL |                                                  |
| `runtime_template_id` | INTEGER FK NOT NULL | → `runtime_templates.id`, cascade delete      |
| `path`                | VARCHAR(400) NOT NULL | Workspace-root-relative, forward slashes, may nest. No leading slash, no `..`; a leading dot on a segment is expected. |
| `ordering`            | INTEGER NOT NULL | Position in the admin's reorderable list         |

Indexes:
- `(organization_id, runtime_template_id, ordering)` for ordered enumeration.
- `(runtime_template_id, path)` UNIQUE — a folder can't be listed twice on the same template.

Same reconciler shape as the two joins above, matching existing rows by `path` (the natural identity here). `WorkspaceZipBuilder.WriteRootFolders` emits each with a `.gitkeep` inside, skipping any folder a workspace-root `organization_files` row already fills; the previews on New Workspace, New Extension and Template Detail fold the same set in. These folders are deliberately absent from the `.code-workspace` `folders` array — see `generation-engine.md`.

### `modules`

| Column                   | Type             | Notes                                          |
|--------------------------|------------------|------------------------------------------------|
| `id`                     | INTEGER PK       |                                                |
| `organization_id`        | INTEGER NOT NULL |                                                |
| `key`                    | TEXT NOT NULL    | Unique per org. Admin/URL slug, e.g. "document-capture". Also the dependency-reference target. |
| `name`                   | TEXT NOT NULL    | Display name e.g. "Document Capture"           |
| `extension_name`         | TEXT NOT NULL    | PascalCase. ZIP folder name AND rendered AL extension name (after `{{extension_prefix}}` substitution). Distinct from `key` so admins can pick a URL slug that differs from the folder layout. |
| `id_range_size`          | INTEGER          | Optional override; null = use the template's `module_id_range_size` |
| `deprecated`             | BOOLEAN NOT NULL |                                                |
| `created_at`             | TIMESTAMPTZ NOT NULL |                                            |
| `updated_at`             | TIMESTAMPTZ NOT NULL |                                            |
| `deleted_at`             | TIMESTAMPTZ      |                                                |

Modules are not tied to a specific runtime — they declare dependencies that work across runtimes. A module contributes one cloned extension to the workspace (folder/file tree from `module_extension_folders` / `module_extension_files`) plus entries in its `app.json`'s `dependencies` array from `module_dependencies`.

### `module_dependencies`

| Column         | Type             | Notes                                                |
|----------------|------------------|------------------------------------------------------|
| `id`           | INTEGER PK       |                                                      |
| `organization_id` | INTEGER NOT NULL |                                                   |
| `module_id`    | INTEGER FK NOT NULL | → `modules.id`, cascade delete                    |
| `ordering`     | INTEGER NOT NULL |                                                      |
| `dep_id`       | TEXT NOT NULL    | The GUID of the dependency, e.g. "4b915d7e-..."      |
| `dep_name`     | TEXT NOT NULL    | e.g. "Continia Core"                                 |
| `dep_publisher`| TEXT NOT NULL    | e.g. "Continia Software"                             |
| `dep_version`  | TEXT NOT NULL    | e.g. "1.0.0.0"                                       |

Index: `(module_id, ordering)`.

The admin module editor sources dependencies from `well_known_dependencies`: picking a catalogue entry inserts a `module_dependencies` row with the GUID, name, publisher, and the catalogue's default version copied in. The columns are intentionally a snapshot — once added, editing or deleting the source catalogue entry does not change existing modules, so generation stays stable across catalogue maintenance. `dep_version` is the only field admins edit per-module (to pin a specific version that differs from the catalogue default). Rows whose GUID no longer matches any catalogue entry (legacy data, or entries deleted from the catalogue) are still rendered in the editor with a "Not in catalogue" hint and remain removable.

### `well_known_dependencies`

The catalogue of "things you might depend on without us knowing in advance." It's the single source admins pick from when composing modules (above) **and** when adding dependencies to a standalone extension from the New Extension page. The table is kept deliberately separate from `module_dependencies` because:

- Modules group dependencies into sets. Well-known deps are flat.
- The same dependency might appear in multiple modules' lists (Continia Core appears in three).
- Admins should be able to add new well-known deps without creating a fake module for them.

| Column         | Type             | Notes                                                |
|----------------|------------------|------------------------------------------------------|
| `id`           | INTEGER PK       |                                                      |
| `organization_id` | INTEGER NOT NULL |                                                   |
| `dep_id`       | TEXT NOT NULL    | GUID                                                 |
| `dep_name`     | TEXT NOT NULL    |                                                      |
| `dep_publisher`| TEXT NOT NULL    |                                                      |
| `dep_version_default` | TEXT NOT NULL | Pre-fills the version field; the user can override it for their specific extension |
| `category`     | TEXT             | Optional grouping label e.g. "Continia", "ForNAV", "Other" — used to group items in the picker UI |
| `ordering`     | INTEGER NOT NULL |                                                      |
| `created_at`   | TIMESTAMPTZ NOT NULL |                                                  |
| `updated_at`   | TIMESTAMPTZ NOT NULL |                                                  |

### `audit_log`

One row per write to any of the above tables. See `auth-and-audit.md` for the full behaviour.

| Column               | Type             | Notes                                          |
|----------------------|------------------|------------------------------------------------|
| `id`                 | INTEGER PK       |                                                |
| `timestamp`          | TIMESTAMPTZ NOT NULL | UTC                                        |
| `changed_by`         | TEXT NOT NULL    | `"display_name <email>"` of the acting user; `"unknown"` for pre-login writes (seed/bootstrap). |
| `changed_by_user_id` | INTEGER NULL FK → `users` | Null for pre-login changes (seed, bootstrap). |
| `organization_id`    | INTEGER NULL FK → `organizations` | Owning org; null for changes with no org context (seed bootstrap). |
| `entity_type`        | TEXT NOT NULL    | The `AuditEntityType` enum (the authoritative list — see `Domain/Entities/AuditLogEntry.cs`). Covers the template / module / workspace-extension entities plus the account, organisation, system-settings, backup, invite, recipe, and personal-access-token entities. Pre-`UnifyExtensions` values (`template_folder` / `template_file` / `template_module_folder` / `template_module_file`) are retained for historical reads. |
| `entity_id`          | INTEGER NOT NULL | The id within that entity's table              |
| `action`             | TEXT NOT NULL    | `created` \| `updated` \| `deleted`            |
| `snapshot_json`      | TEXT             | Full JSON of the row's state *before* the change. Null for `created`. |

Indexes: `(entity_type, entity_id, timestamp)` for the per-entity history view; `(timestamp)` for the global log overview; `(organization_id, timestamp)` for the per-org audit search.

## Validation rules

These are domain rules — they should be enforced in service code, not just at the form layer. In particular, anything that calls `TemplateService.CreateAsync` / `UpdateAsync` should fail before hitting the DB if these are violated, throwing `PlanValidationException` with field-keyed errors so the UI can render them inline.

- `runtime_templates.key` must match `^[a-z0-9-]+$` and be unique within an org.
- `runtime_templates.runtime` must be a non-empty string.
- `runtime_templates.core_id_range_from < core_id_range_to`.
- `workspace_extensions.path` must be a single segment matching `^[A-Za-z0-9._-]+$` (no `/`, no `..`), unique within a template.
- `workspace_extension_folders.path` must be a single segment (no `/`). Top-level siblings under one extension can't share a path; nested siblings under one parent can't share a path.
- `workspace_extension_files.path` must be a filename (no `/`, no `..`). Unique within a folder.
- `workspace_extension_files.content` is UTF-8 text. Audit snapshots store a SHA-256 hash of the content (plus the path) rather than the content itself, so the audit log doesn't bloat with copies of every AL file on every save.
- `workspace_extension_dependencies`: exactly one of `ref_extension_path`, `ref_module_key`, `lit_id` is non-null. When `ref_extension_path` is set, it must resolve to another extension in the same template; the save validator rejects unresolvable references rather than letting them slip through to generation. When `ref_module_key` is set and the key is new in that save (not already on the template), it must name a live module in the org; a blank key is refused. A key the template already had is accepted even if its module has since been deleted, and generation drops it.
- `module_extension_folders` / `module_extension_files`: same path-segment rules as the workspace mirrors.
- `modules.key` must match `^[a-z0-9-]+$` and be unique within an org.
- `module_dependencies.dep_id` must be a GUID format.
- `well_known_dependencies.dep_id` must be a GUID format.

## Soft-delete and "deprecated" semantics

These are deliberately separate flags:

- **`deprecated`** — visible in admin lists, hidden from end-user-facing dropdowns. Used for "we don't recommend this anymore but old projects still need to be regeneratable."
- **`deleted_at`** — soft-deleted. Hidden from both admin and end-user lists by default; recoverable through a "show deleted" toggle in the admin section.

Hard delete is not exposed in the UI. Hard delete only happens via direct database access if someone really needs to clean up.
