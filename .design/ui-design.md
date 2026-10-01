# UI design

## Shell layout

```
┌──────────────────────────────────────────────────────────┐
│  [icon] AL Workbench                        [user/login]│  ← top bar
├─────────────┬────────────────────────────────────────────┤
│             │                                              │
│  TOOLS      │                                              │
│  ⊕ Project  │            page content slot                 │
│    gener…   │                                              │
│             │                                              │
│  RESOURCES  │                                              │
│  ↗ Repo     │                                              │
│  ? Docs     │                                              │
│             │                                              │
│  ─────────  │                                              │
│  [admin]    │                                              │
│             │                                              │
└─────────────┴────────────────────────────────────────────┘
```

Implemented as a single `MainLayout.razor`:
- Top bar shows the app name and the current user's display name (or "Sign in" if anonymous).
- Sidebar shows tool links, resource links, and (when authenticated as admin) a section for admin pages.
- The content slot renders the active page.

## Routes

> **This document is a Phase-1 design snapshot.** It predates Phase 3 (accounts, multi-tenancy) and Phase 4 (Postgres, site-admin), so the route list and per-page descriptions below cover only the original generator + admin surface. The app now has ~80 routes (the full auth stack, account self-service, `/site-admin/*`, invites, MCP/OAuth). The authoritative route list is the Blazor router (the `@page` directives across `Components/Pages/`); for the current auth model and the page-by-page auth requirements, see `auth-and-audit.md`.

A key change since this snapshot: the generator pages (`/projects/new`, `/projects/extension`) and `/templates` are now **authenticated** (`[Authorize]`, `User`+). Anonymous visitors are redirected to `/login`. The original Phase-1 surface was:

| Route                          | Page                          | Auth (current) |
|--------------------------------|-------------------------------|----------------|
| `/`                            | Redirects to `/projects/new`  | —              |
| `/projects/new`                | NewWorkspace                  | Yes (User+)    |
| `/projects/extension`          | NewExtension                  | Yes (User+)    |
| `/templates`                   | TemplatesBrowser (read-only)  | Yes (User+)    |
| `/admin/*`                     | Admin pages                   | Yes (Editor/Admin) |
| `/login`                       | Login                         | No             |

## Page: New workspace (`/projects/new`)

Two-column layout. Left column = form, right column = live preview that updates on every change.

### Left column sections

In order, top to bottom:

1. **Top tab bar** (above the two columns): `New workspace | New extension | Templates`. Active = "New workspace".

2. **Runtime template** — single `<select>` dropdown. Options come from `TemplateService.ListActive()` (excludes deprecated and soft-deleted). Each option shows the template's `name`. Below the dropdown, a small caption shows the selected template's description and "Default application: {default_application}".

3. **Project** — three fields:
   - Name (text, required) — placeholder "e.g. CRONUS Customer"
   - Brief (text, required)
   - Description (textarea, required, ~3 rows)

4. **Core ID range** — two number inputs, "from" and "to". Pre-filled from the selected template's `core_id_range_from`/`to`. Editable.

5. **Modules** — a vertical list of cards, one per module from `ModuleService.ListActive()`. Each card is a label wrapping a checkbox + the module name + a caption "+ N {category} dependencies" describing what it brings. Clicking anywhere on the card toggles. Selected cards have a slightly stronger background.

6. **Options** — checkboxes:
   - `[x]` Include example AL files
   - `[ ]` Include ForNAV in Core

### Right column

1. **Preview** — a monospace folder tree, rendered live from the current form state. As the user toggles modules, options, or changes the workspace name, the tree updates. The tree uses Lucide folder icons in the secondary text colour, with a subtle accent (info colour) on the *generated extension* folders (Core and module folders) to distinguish them from grouping/static folders (.assets, Translations).

2. **Stat cards** — two small cards: "Extensions: N" and "Dependencies: M".

3. **Generate button** — primary button, full width. On click, posts the form to the GenerationService and triggers a file download via Blazor's `IJSRuntime` (or via a regular form post to a controller endpoint that returns a `FileStreamResult` — the latter is simpler).

The right column should be `position: sticky` so it stays in view as the user scrolls through a long modules list.

### Validation

Run synchronously on Generate click:

- Name: required, must match `^[A-Za-z][A-Za-z0-9 ]*$` (letters/digits/spaces, must start with a letter).
- Brief: required, non-empty.
- Description: required, non-empty.
- Core ID range: from > 0, to > from.
- Application version: must match `^\d+\.\d+\.\d+\.\d+$`.

Show errors inline next to each field. Don't submit if anything fails.

## Page: New extension (`/projects/extension`)

Same shell, same two-column layout. Differs in the form sections:

### Left column

1. **Runtime template** — same dropdown.

2. **Extension** — fields:
   - Name (e.g. "MyCustomFeature")
   - Brief
   - Description
   - Publisher (pre-filled from template's defaults publisher field, editable)

3. **ID range** — single from/to. Default placeholders 90000/90999 with helper text: "you're responsible for choosing a range that doesn't collide with the rest of your workspace."

4. **Dependencies** — two parts:

   a. **From the catalogue** — sectioned list grouped by `category`. Each item is a checkable row showing the dep name + publisher + version. When checked, the version is editable inline (in case the user wants a different version than the catalogue default).

   b. **Add manual dependency** — a small inline form: id (GUID), name, publisher, version. "Add" button appends it to a list below. Each manually-added entry shows in a small chip/row with a remove button.

5. **Options**:
   - `[x]` Include example AL files

### Right column

Same preview / stats / Generate pattern, but:
- The folder tree only shows the single extension folder (no workspace wrapper).
- After generation, the success page (or a section that appears below the Generate button on success) shows:

   > Drop `MyCustomFeature/` into your existing workspace folder, then add this line to the `folders` array in your `.code-workspace`:
   > ```json
   > { "path": "MyCustomFeature" }
   > ```
   > If the file also has a `files.exclude` list under `settings`, add this line there too, so the folder isn't shown twice:
   > ```json
   > "MyCustomFeature": true
   > ```

   With a copy button on the snippet.

## Page: Templates browser (`/templates`)

Read-only listing of available templates and modules, intended to make the system discoverable for end users.

- A list of templates: name, runtime, description, status (active / deprecated).
- A list of modules: name, dependency count, status.
- A small banner at the top: "Templates and modules are managed by [link to admin]. To request changes, [link to your team's process]." (Implementer can leave the second link as a TODO comment.)

## Admin pages (`/admin/*`)

All gated behind the password (see `auth-and-audit.md`).

### `/admin` — dashboard

A simple landing page for admins. Tiles or links to:
- Templates (with count)
- Modules (with count)
- Catalogue (with count)
- Audit log (with last entry timestamp)

### `/admin/templates` — list

Table of all templates including deprecated and soft-deleted (with a toggle to hide deleted). Columns: key, runtime, name, status, last updated. Each row links to the edit page.

A "New template" button at the top.

### `/admin/templates/{key}` — edit

A form mirroring the `runtime_templates` schema:

- Top: key (read-only after creation), runtime number, name, description.
- Default application, default platform, defaults JSON (for v1, this can be a `<textarea>` with JSON; a structured editor is a "nice to have" for later).
- AppSourceCop JSON (textarea).
- Core ID range from/to.
- Module ID range start, module ID range size.
- Deprecated toggle.

Below: a sortable list of folders with add/edit/delete. Each folder row has: path, example_path, drag handle for reordering.

Actions:
- Save → calls `TemplateService.Update` → audit log captures the previous state.
- Delete → soft-delete (sets `deleted_at`), confirmation modal first.
- Restore (if soft-deleted) → clears `deleted_at`.

Below the form: a "History" panel showing the last 20 audit entries for this template, newest first. Each entry is timestamp + who + action. Clicking expands to show the JSON snapshot.

### `/admin/modules` — list and `/admin/modules/{key}` — edit

Same pattern as templates. The edit page has a sub-form for dependencies (add/edit/remove rows with id/name/publisher/version).

### `/admin/catalog` — well-known dependencies

A flat editable list. One row per dependency with id/name/publisher/version-default/category. Reorderable. Add/delete in place.

### `/admin/audit` — global audit log

Table of all audit entries across all entities, newest first. Filters: entity type, who, date range. Each row clickable to see the snapshot.

## Components to factor out

These appear in multiple pages — pulling them into reusable components saves duplication. The first three shipped and live in `Components/Shared/`:

- **`<DependencyPicker>`** — used by New Extension and admin module/catalogue editing. Takes a list of `DependencyEntry`, supports add/remove. Catalogue picker mode shows checkboxes over the catalogue; freeform mode shows a list with add buttons.
- **`<FolderTreePreview>`** — takes a list of folder paths and renders the tree. Used by both project flows.
- **`<AuditHistoryPanel>`** — takes an entity type + id, fetches recent audit entries. Used at the bottom of every admin edit page.
- **`<JsonEditor>`** — *not built; superseded.* The plan was a textarea for `defaults_json` / `app_source_cop_json`. Instead, the visible knobs moved to dedicated form fields (`/admin/templates/defaults`) and the remaining raw JSON is edited through the TOML editor's round-trip rather than a bespoke component. See the rationale comment in `AdminTemplateEdit.razor`.

## Visual conventions

- Section labels (e.g. "RUNTIME TEMPLATE", "MODULES") in 11px, weight 500, secondary text colour, slight letter-spacing.
- Form fields full-width within their section.
- Cards (modules, dep entries) have subtle borders; selected state has a stronger background.
- The Generate button is the only "primary" button visually — everything else is the default outline button style.
- Icons are Lucide, vendored as SVGs under `ALDevToolbox/Resources/Icons/` (see `VERSION.txt` for the pinned upstream tag) and rendered inline by `Components/Shared/Icon.razor`. No mixing icon families. Don't reintroduce a third-party icon NuGet — the package this replaced (`Lucide.Blazor`) was unmaintained and threw on render when an icon name didn't exist (issue #47); the vendored catalogue logs a warning and renders a placeholder instead.
- Stick to a single accent colour for "active" or "selected" states throughout.

## Mobile

Not in scope. This is a desktop tool. The layout can collapse the sidebar on narrow viewports if it's free, but don't spend effort on it.
