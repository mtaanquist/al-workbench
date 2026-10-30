# Templates and seeding

This document covers where templates live, the TOML schema admins author against, and the import-from-system-org flow. The data-model spec lives in `domain-model.md`; the unified-extensions data shape (Issue #54) is documented in `unified-extensions.md`.

## Where templates live

**Persistence:** in the PostgreSQL database. Templates, modules, and catalogue entries are stored as rows in the tables described in `domain-model.md`. The DB is the source of truth at runtime — every read on the user-facing flows hits these tables.

**Authoring surfaces:** two equal options, both writing through the same `TemplateAuthoring` validation pipeline into the DB.

1. **The structured admin form.** Field-by-field editing on `/admin/templates/{key}`. Best for incremental tweaks, single-flag changes, and folder reordering.
2. **TOML.** A textarea on the same admin page (toggle in the header) that round-trips a `template.toml` document. Best for authoring a new template from scratch, bulk folder edits, and copy-pasting a template from one environment to another. New templates default to TOML mode because that's the workflow this is optimised for.

**Bootstrap:** the singleton **system organisation** is the canonical source other orgs fork from. The Default org carries `organizations.is_system = true` (stamped by migration `20260513000000_MoveSeedToSystemOrg`); a partial unique index refuses a second system org per deployment. SiteAdmins author templates, modules and application versions there via the regular `/admin/templates*` pages — the system org is otherwise a normal org from the data model's point of view.

Every other organisation starts **empty**. New-org signup (auto-approved or admin-approved) no longer triggers any seed step; the admin lands on `/admin/templates` with two ways to populate it:

1. **Import from site.** `/admin/templates` renders a "From the site catalogue" section listing the system org's published templates. Clicking **Import** copies the chosen template (plus its referenced modules and default application version) into the local org via `TemplateImportService.ImportTemplateAsync`. Imports are one-way clones — once a template is in the local org, system-side edits no longer propagate.
2. **Author from scratch.** The structured form and TOML editor work exactly as before; the org simply doesn't have any starting content until the admin adds some.

The retired on-disk `Templates.seed/` directory is gone, along with `SeedService`. The `OrganizationConfigService.PopulateDefaultsAsync` pipeline and the disk-default logo are also gone — new orgs start with no logo and no settings row; admins fill them in via `/admin/configuration`.

This split exists so:
- The deployment has exactly one canonical catalogue that SiteAdmins curate at runtime, instead of a per-PR file-system bootstrap.
- New orgs come up empty and pull only the templates they care about, rather than the entire historical seed snapshot.
- Day-to-day edits — and authoring entire new templates — don't require a PR + redeploy.
- Admins comfortable with TOML can stay in TOML; admins who prefer forms can stay in forms.

## Import strategy

`TemplateImportService.ImportTemplateAsync(systemTemplateId)` clones one template into the acting organisation. Pseudocode:

```
TemplateImportService.ImportTemplateAsync(systemTemplateId):
    require an authenticated org context
    refuse if the acting org IS the system org (would import from itself)
    resolve the system org via IsSystem = true (bypassing query filters)
    load the source template with its WorkspaceExtension graph:
      - extensions ordered by Ordering
      - the recursive workspace_extension_folders tree (loaded via a flat
        query and reassembled client-side — EF's Include doesn't recurse)
      - workspace_extension_files attached to each folder at its depth
      - workspace_extension_dependencies per extension
      - default modules + their dependencies (soft-deleted modules are
        skipped: the catalogue retired them)
      - default application version
      - included organisation files (the join plus each file's path)
    refuse if the local org already has a template with the same key
    for each referenced Module:
        reuse the local module that shares the key, otherwise clone it
        (with its module_dependencies AND its recursive
        module_extension_folders / module_extension_files tree) into the
        local org; if the local module with that key is soft-deleted, the
        org retired it, so leave it out of the template's defaults rather
        than restoring it
    for the default application version:
        reuse the local row that shares the key, otherwise clone it
    for each included organisation file:
        point the clone at the acting org's own file with the same path
        (every org carries its own copy, possibly edited); skip a path the
        acting org doesn't have, and put the acting org's app.json first
        when the source didn't include it, as a new template does
    insert a fresh runtime_template row (organization_id = acting org)
        plus the cloned WorkspaceExtension graph, root folders, the
        .code-workspace overlay and the "Latest" default-version flag
    return the new template
```

The full graph commits in a single `SaveChangesAsync`. The audit interceptor records every insert, so the importing org's audit log carries the operation in full.

`ListSystemTemplatesAsync()` is the read-side counterpart — it returns the system org's active templates with an `AlreadyImported` flag so the UI can render "Already imported" instead of an Import button on rows the local org already has.

Idempotency is per-template-key per-org: a second click on the same row throws `PlanValidationException("Key", …)` rather than silently overwriting.

## Tomlyn usage

Tomlyn deserialises directly into POCOs. `Domain/Seed/` carries the mirrored types used by the admin TOML editor and the export pipeline (it is no longer wired to any on-disk bootstrap):

```csharp
class TemplateSeed {
    public TemplateMetaSeed Template { get; set; }
    public DefaultsSeed Defaults { get; set; }
    public AppSourceCopSeed AppSourceCop { get; set; }
    public List<ExtensionSeed> Extensions { get; set; }  // [[extensions]]
}
class ExtensionSeed {
    public string Path { get; set; }                     // stable identifier
    public string Name { get; set; }                     // mustache name template
    public bool Required { get; set; } = true;
    public string? Application { get; set; }             // per-extension override
    public string? Runtime { get; set; }                 // per-extension override
    public int? IdRangeFrom { get; set; }                // explicit override
    public int? IdRangeTo { get; set; }
    public List<FolderSeed> Folders { get; set; } = new();
    public List<DependencySeed> Dependencies { get; set; } = new();
}
class FolderSeed {
    public string Path { get; set; }                     // single segment
    public List<FolderSeed> Folders { get; set; } = new();   // recursive
    public List<FolderFileSeed> Files { get; set; } = new(); // attached at this depth
}
class FolderFileSeed {
    public string Path { get; set; }
    public string Content { get; set; }                  // mustache-substituted at generation time
    public bool IsExample { get; set; }
}
class DependencySeed {
    public string? Extension { get; set; }               // one of these
    public string? Module { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Publisher { get; set; }
    public string? Version { get; set; }
}
```

`TemplateTomlMapper.FromToml(text, deprecated)` does the deserialisation and emits a `TemplateAuthoring` ready for `TemplateService.CreateAsync` / `UpdateAsync`. The `defaults_json` and `app_source_cop_json` columns are populated by re-serialising the relevant sub-objects to JSON.

## Template TOML schema

A `template.toml` document — pasted into the admin TOML editor or produced by the export pipeline — follows the **unified-extensions** shape:

```toml
# Required: template metadata
[template]
key = "runtime-15"
runtime = "15"
name = "Runtime 15+"
description = "Namespace folders, AppSource-ready"
core_id_range_from = 90000
core_id_range_to = 90999
module_id_range_start = 91000
module_id_range_size = 200
is_default = false

# Optional: folders the generated workspace gets at its top level with nothing
# in them - the symbol cache an AL build fills in, a team's docs convention.
# Every other root folder comes from an extension and carries an app.json, so
# this is the only way to declare an empty one. Paths are workspace-root
# relative and may nest ("docs/decisions"). Each ships a .gitkeep so the
# directory survives a git commit, and none of them is added to the
# .code-workspace folders array (they are not AL app roots). A path an
# extension already owns, or one an included file already puts content in, is
# refused at save time. Empty / missing means no extra root folders.
root_folders = [".alpackages"]

# Optional: default-selected catalogue modules. End-users can still opt out of
# any entry; this only seeds the initial selection on the New Workspace form.
# Unknown keys drop with a warning rather than failing the import.
[[template.default_modules]]
key = "document-capture"

# Optional: the organisation's always-included files this template opts into.
# Paths reference rows in organization_files; new templates default to empty
# (admins explicitly tick the files that belong with each template via the
# editor). Empty / missing means the template emits no org files.
included_files = [".editorconfig", "README.md"]

# Required: defaults merged into every generated app.json. The form pre-fills
# from these, the user edits, the final values flow into per-extension app.json.
[defaults]
publisher = "Consortio IT"
target = "Cloud"
application = "27.0.0.0"        # pre-fill; user can override on New Workspace
platform = "1.0.0.0"            # pre-fill; user can override on New Workspace
url = "https://www.consortio.dk/"
logo = "../.assets/images/logo.png"
features = ["TranslationFile", "NoImplicitWith"]
supportedLocales = ["en-US", "da-DK"]
affix = "CRONUS"
affixType = "Prefix"            # "None" | "Prefix" | "Suffix"

[defaults.resourceExposurePolicy]
allowDebugging = true
allowDownloadingSource = false
includeSourceInSymbolFile = true

# Required: AppSourceCop.json contents (merged into every extension's
# AppSourceCop.json — mustache substitution runs at generation time).
[appSourceCop]
mandatoryPrefix = "CONIT"
supportedCountries = ["US", "DK"]

# Required: at least one [[extensions]] entry. Display order is array order;
# required = true entries always emit, required = false entries surface as
# checkboxes on the New Workspace form.

[[extensions]]
path = "Core"                   # stable identifier (folder name in ZIP, dep ref target)
name = "{{extension_prefix}} Core"
required = true
# id_range_from / id_range_to optional — auto-allocate from the template ranges
# when omitted (see generation-engine.md "ID range allocation").

# Folders nest: each [[extensions.folders]] is a top-level folder under the
# extension; [[extensions.folders.folders]] are children, recursively. Files
# attach at any depth via [[extensions.folders.files]] / nested .files blocks.
[[extensions.folders]]
path = "src"

[[extensions.folders.folders]]
path = "codeunits"

[[extensions.folders.folders.files]]
path = "AppInstall.Codeunit.al"
content = """
namespace {{namespace}};

codeunit 90100 "{{affix}} App Install"
{
    Subtype = Install;
}
"""

[[extensions.folders.folders.files]]
path = "Example.Codeunit.al"
content = """codeunit 90101 \"{{affix}} Example\" { }"""
is_example = true              # only emitted when plan.IncludeExamples is true

# A second required extension — depending on Core by stable path.
[[extensions]]
path = "Hotfix"
name = "{{extension_prefix}} Hotfix"
required = true
application = "27.0.0.0"        # per-extension override (optional)
runtime = "15"                  # per-extension override (optional)

[[extensions.dependencies]]
extension = "Core"              # refs another [[extensions]] entry by `path`

[[extensions.folders]]
path = "src"

# An optional extension — admins flip required = false to surface it as a
# checkbox on New Workspace. Hidden until ticked.
[[extensions]]
path = "Translations"
name = "{{extension_prefix}} Translations"
required = false
```

Notes:

- **`path` is the stable identifier** of an extension. `name` is display-only (mustache-substituted), `path` is the immutable reference target for dependencies.
- **`required` defaults to true.** Template-declared extensions are always emitted unless explicitly marked optional. Optional extensions surface as checkboxes on New Workspace.
- **Folders are a recursive tree, not flat paths.** Each `path` is a single segment; nesting is `[[extensions.folders.folders]]`. Files attach at any depth via `[[extensions.folders.files]]` / `[[extensions.folders.folders.files]]` / etc.
- **No `[[module_folders]]`** at the template level. Modules carry their own folder/file trees on the module entity itself (see "Module TOML schema" below).
- **`application` and `runtime`** are template-wide by default (from `[defaults]`), override-able per extension. Most workspaces use one version; the override unblocks mixed-version edge cases.

## Module TOML schema

Each module's TOML document. Modules now carry their own folder/file tree alongside their dependencies — when a module is selected on New Workspace, its folder tree clones into the workspace as one cloned extension at generation time.

```toml
[module]
key = "document-capture"            # admin / URL slug (lowercase-hyphen)
name = "Document Capture"           # display name shown in pickers
extension_name = "DocumentCapture"  # PascalCase: ZIP folder name AND rendered AL extension name
id_range_size = 200             # optional; null/missing = use template default

[[module.dependencies]]
dep_id = "4b915d7e-c02a-435f-85ab-649086c1e002"
dep_name = "Continia Core"
dep_publisher = "Continia Software"
dep_version = "1.0.0.0"

# Optional: folder tree contributed by the module. Same shape as the template's
# [[extensions.folders]] — recursive, files attach at any depth.
[[module.folders]]
path = "src"

[[module.folders.folders]]
path = "codeunits"

[[module.folders.folders.files]]
path = "DocAdapter.Codeunit.al"
content = """codeunit 91000 \"{{affix}} Doc Adapter\" { }"""
```

Order of `[[module.dependencies]]` blocks is preserved.

A module copied from a GitHub repository exports with a `[module.source]` table (`repository`, `ref`, `path`, `commit`) beside its copied folders. It is a record of where the copy came from; nothing imports it, so a module brought in from an export is an ordinary written-here module holding the same files.

## Modules from a GitHub repository

Issue #1415. Instead of typing a module's files into the editor, an Editor can point the module at a GitHub repository, and AL Workbench copies the extension's files into the module's ordinary folder rows. Generation never knows the difference: it reads the copy from the database like any other module, so New Workspace and Create repository keep working when GitHub is down, and the "generation is read-only against the DB" fence holds.

**What is stored.** `modules.source_repository` (`owner/name`, the canonical casing GitHub returned), `source_ref` (branch, tag or commit; null means the default branch), `source_path` (a folder inside the repository; null means the root), `source_commit_sha` (what the copy was made from), `source_fetched_at`, `source_checked_at` and `source_error` (bookkeeping for the module page), and two JSON columns: `source_skipped_json` (files left out and why) and `source_dependencies_json` (the repository `app.json`'s dependencies). A module with a null `source_repository` is a written-here module; switching a module back to written-here keeps the current copy as editable files.

**Which repositories.** Public repositories on github.com, and private ones in the GitHub organisation connected under Administration, only when the Editor saving the module can see that repository on GitHub themselves (the same check as solution repositories, #1294). Reads use the organisation's installation token when it has one and are anonymous otherwise. The archive is downloaded from `codeload.github.com` only, with no credential on that hop.

**What is copied.** The files of the chosen folder, with placeholders filled at generation time like any module file, and object IDs left exactly as the repository has them (clashes are the new workspace's to resolve). Left out silently: `.git*`, `.vscode`, `.alpackages`, `.snapshots`, `.altestrunner`, `*.app`, and the folder's own `app.json`, which is read instead: its dependencies are added to the generated extension after the typed ones (a typed entry for the same app id wins), and its ID range size fills the module's when the Editor left that blank. Left out and listed on the module page: files directly in the extension folder (module trees hold folders only), binary or non-UTF-8 files, files over 512 KB, links, unsafe paths and names that differ only in case. A copy over 500 files or 5 MB, or a download over 50 MB, is refused.

**When the copy is refreshed.** On save (a repository that cannot be read refuses the save), on "Update now", on a push to the followed branch through the same GitHub App webhook the pipelines use (`GitHubPullRequestBuildWorker.RecordBranchActivityAsync`), (so only for repositories the connected organisation's App is installed on; a public repository elsewhere relies on the daily pass), and in a daily pass at 03:00 UTC (`ModuleSourceRefreshScheduler`, `DISABLE_MODULE_SOURCE_REFRESH_SCHEDULER`) for anything a push missed. A module pinned to a full commit SHA is skipped by the daily pass. A refresh that finds the same commit only stamps `source_checked_at`; a failed refresh keeps the old copy and records why in `source_error`, which the module page and list show. Each refresh reads the repository again: a renamed repository is followed and the new name stored, and one that has become private outside the connected organisation stops refreshing with the reason shown. The Editor's own access is checked only at save time, as with automatic builds, and a module forked from the system organisation keeps its source as it was. A refresh replaces only the files and the source columns, under a row lock, and is dropped if the module was saved or switched while it downloaded. A new commit that leaves the module's folder as it was only moves the recorded commit; the files are not rewritten. In a push or the daily pass, modules that share a repository share one look at it and one download, a module already at the pushed commit is skipped, and GitHub limiting requests ends the pass with the rest left as they are for next time. Saving a repository module without changing its repository, branch or folder keeps the copy as it is.

**Generated workspaces** carry a plain copy with no link back. `workspace.aldt.toml` records the repository and commit in the extension's identity row so a later reader can tell where the files came from.

## Catalogue TOML schema

The catalogue TOML document carries every well-known dep in one file:

```toml
[[dependency]]
dep_id = "4b915d7e-c02a-435f-85ab-649086c1e002"
dep_name = "Continia Core"
dep_publisher = "Continia Software"
dep_version_default = "1.0.0.0"
category = "Continia"

[[dependency]]
dep_id = "6f0293d3-86fc-4ff8-9632-54a580be6546"
dep_name = "ForNAV Core"
dep_publisher = "ForNAV"
dep_version_default = "1.0.0.0"
category = "ForNAV"
```

## Mustache variables (reference)

These are the variables available when seeding AL files into a generated extension. See `generation-engine.md` for substitution behaviour.

Canonical names are snake_case to match the rest of the TOML schema. Legacy camelCase names (`{{workspaceName}}`, `{{shortName}}`, `{{moduleName}}`) still resolve as aliases — the renderer logs one warning per render listing any encountered so admins can rename their files at leisure.

| Variable                | Description                                                |
|-------------------------|------------------------------------------------------------|
| `{{name}}`              | Full rendered extension name                               |
| `{{workspace_name}}`    | The workspace name from the form                           |
| `{{short_name}}`        | The short name typed on the form, or the customer name when blank |
| `{{module_name}}`       | Module's `extension_name` (PascalCase) for module-cloned extensions |
| `{{publisher}}`         | `OrganizationSettings.DefaultPublisher`                    |
| `{{extension_prefix}}`  | Resolved from the organisation's prefix policy, e.g. "CRO" (see `customer-naming.md`) |
| `{{affix}}`             | `defaults.affix` when `affixType ∈ {Prefix, Suffix}`; empty when `None`. Replaces the pre-unified `{{prefix}}` / `{{suffix}}`. |
| `{{namespace}}`         | The folder path, dot-separated                             |
| `{{guid}}`              | A fresh GUID per call                                      |
| `{{tenant_id}}`         | Tenant GUID captured on the New Workspace form; empty for standalone extensions |

## TOML as an authoring surface

Both `/admin/templates/new` and `/admin/templates/{key}` expose a TOML editor next to the structured form via a header toggle. The new-template flow opens in TOML mode by default because pasting in a `template.toml` is usually the fastest way to get a fresh template going; the edit flow opens in the structured form because surgical tweaks are what people do most often there.

Saving from TOML mode parses the textarea via Tomlyn, maps it onto the same `TemplateAuthoring` payload the structured form produces (once the structured editor is rewritten around the recursive tree), and runs through identical validation in `TemplateService.CreateAsync` / `UpdateAsync`. There is no separate code path on the way to the database.

The mapper (`Services/Templates/TemplateTomlMapper.cs`) is load-bearing infrastructure for the admin editor and the export pipeline. A few things to keep in mind:

- One TOML schema covers the admin editor, the export ZIP, and any future import-from-TOML affordance. Don't fork it.
- `deprecated` is deliberately not represented in the TOML view — flip it from the structured form's checkbox.
- Per-extension and per-folder ordering is expressed by array order; `ordering` columns are filled in from array position by the mapper.
- `[[extensions.folders.files]]` blocks make the TOML lossless even when extensions carry file content. For larger files this gets noisy; the structured editor will be the more comfortable surface for editing AL source once it's been rewritten around the recursive tree. Both write through the same pipeline, so neither is "more correct" than the other.
- Tomlyn silently drops unknown keys, so old templates that still mention `default_application` at `[template]` level or `[[folders]]` at the top level parse cleanly — they just don't produce any extensions. The mapper rewrite is responsible for surfacing this as a validation error rather than letting the import succeed with an empty extension list.

## Export to TOML

`/admin` exposes a one-click "Export all to TOML" button that produces a ZIP containing the current state of the database serialised into TOML. This is the safety hatch for:

- Periodic snapshotting into a backup branch.
- Quickly grepping/diffing the entire template state outside the app.
- Disaster recovery if the database is unrecoverable (paste the templates back via the admin TOML editor).

Implementation: walk all active rows, serialise each template/module/catalogue back into the TOML shape via `TemplateTomlMapper.ToToml`. Recursive folder trees emit `[[extensions.folders.folders]]` blocks in depth order; files emit `[[extensions.folders.files]]` (or `[[extensions.folders.folders.files]]` for nested) at the parent folder's scope. The mapper hand-emits recursive folder blocks rather than going through Tomlyn's reflection serialiser, which produces inline arrays.

## What is *not* admin-editable

- The static `.gitignore` template and the AL ruleset JSON. These ship as embedded resources under `Resources/` because they're per-deployment policy rather than per-template content. Treat them as code.
- The `README.md` boilerplate written into a generated workspace — it's emitted by `GenerationService` and shaped by template metadata, not edited directly.
- Binary files inside template or module folders. v1 stores file content as UTF-8 text only; PNGs, ZIPs, or anything else non-text don't have a place in `workspace_extension_files` / `module_extension_files`. If we need binary template assets later, the likely shape is a separate `*_file_blobs` table or a URL-fetched asset; defer until there's a real ask.

The org logo, default publisher / URL / logo / supported countries / ID range / brief / core description, and the always-included file library **are** admin-editable from `/admin/configuration` and live in the database (`organization_assets`, `organization_settings`, `organization_files`). Fresh orgs start without any of these rows — admins fill them in once they need them. Templates opt into specific organisation files via the `runtime_template_included_files` join (TOML field: `[template] included_files`) — a new file in the library is off-by-default until at least one template lists it.

The empty root folders a template declares are its own rows rather than a join, in `runtime_template_root_folders` (TOML field: `[template] root_folders`) — they name a path and nothing else, so there is no shared library to opt into. `TemplateImportService` copies them straight across when an org forks a canonical template, since a path needs no remapping into the acting org.
