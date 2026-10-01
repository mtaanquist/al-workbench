# Generation engine

This document specifies what `GenerationService` produces given a runtime template and a project plan.

The generation model is the **unified-extensions** walk (Issue #54): a workspace is N extensions — required template-declared ones, optional template-declared ones the user ticked, and one cloned extension per selected catalogue module — and each emits its own folder. The data-model spec lives in `unified-extensions.md`; this doc covers the algorithm and output layout.

## Inputs

A `ProjectPlan` value object collected from the form:

```csharp
record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,            // the customer's name, as typed
    string? ShortName,               // the customer's name abbreviated; blank falls back to WorkspaceName
    string ExtensionPrefix,          // resolved from the org's extension_prefix_mode; see customer-naming.md
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int CoreIdRangeFrom,
    int CoreIdRangeTo,
    bool IncludeExamples,
    IReadOnlyList<string> SelectedExtensionPaths,  // optional template-declared extensions the user ticked
    IReadOnlyList<string> SelectedModuleKeys       // catalogue modules that contribute cloned extensions
);
```

`WorkspaceName` is the customer's name as typed, in any script: required, 1 to 100 characters, no control characters, and at least one letter or digit once transliterated (so "!!!" is refused and "Jørgensen Møbler" is not). The folder name and the `.code-workspace` file name are derived from it by `CustomerNaming` — Unicode canonical decomposition plus a fixed table for the letters that do not decompose (ø → o, å → aa, ß → ss), then PascalCase — which is why the name itself needs no character rule of its own.

`ShortName` is the customer's name abbreviated for the extension names ("JM" for "Jørgensen Møbler"): optional, at most 50 characters, no control characters, and used as typed. Blank means the customer name is used unchanged, so it is never a missing value. It is what `{{short_name}}` renders to, and the rendered extension names it feeds are checked against Business Central's 200-character ceiling (AppSourceCop AS0047) before anything is written - a plan that would exceed it is refused with the short name as the field to fix.

The walk concatenates the emittable extension list in this order: template-required `WorkspaceExtension` rows (always emitted) → optional template-declared extensions whose `Path` appears in `SelectedExtensionPaths` (in template `Ordering`) → one cloned extension per `SelectedModuleKeys` entry (in selection order). `{{extension_prefix}}` and `{{affix}}` (with `defaults.affixType`) drive mustache substitution. There is no Core-vs-module split; "Core" is just the conventional `path` of the first required template extension. There is no `IncludeForNav` toggle either — ForNAV is a normal catalogue module that templates declare under `[[template.default_modules]]`.

The naming rules for `WorkspaceName` (the customer name), the short name, the derived folder and the `{{short_name}}` / `{{workspace_folder}}` variables are being reworked under `customer-naming.md`; that document wins where the two disagree until its slices land and this one is rewritten.

For the **New Extension** flow, the inputs are slightly different — see [Standalone extension generation](#standalone-extension-generation) at the bottom.

## Output

A `Stream` containing a ZIP archive. The caller is responsible for writing it to the HTTP response.

The structure of the ZIP — for a workspace called "CRONUS Customer" from a template declaring Core + Hotfix extensions, with the Document Capture module selected — looks like:

```
CRONUSCustomer/
├── .alpackages/
│   └── .gitkeep                          # folder declared empty by the template
├── .assets/
│   ├── images/
│   │   └── logo.png
│   └── rulesets/
│       └── Company.ruleset.json
├── Core/                                # path of the first required WorkspaceExtension
│   ├── app.json                         # org file scoped to every extension; mustache-substituted per extension
│   ├── AppSourceCop.json                # org file scoped to every extension, when the template opts in
│   ├── src/                             # folder from workspace_extension_folders
│   │   └── codeunits/
│   │       └── AppInstall.Codeunit.al   # file from workspace_extension_files; mustache-substituted
│   └── ...
├── Hotfix/                              # path of a second required WorkspaceExtension
│   ├── app.json                         # carries a dependencies[] entry referencing Core's freshly-allocated GUID
│   ├── AppSourceCop.json
│   ├── ...
├── DocumentCapture/                     # path = module.extension_name for the cloned-from-catalogue module
│   ├── app.json                         # implicit deps on Core and Hotfix; module's own deps follow
│   ├── AppSourceCop.json
│   ├── src/                             # folder from module_extension_folders
│   │   └── ...                          # files from module_extension_files
│   └── ...
├── CRONUSCustomer.code-workspace
├── workspace.aldt.toml                  # the form-post shape, so the workspace can be regenerated
├── .gitignore
└── README.md
```

What the template declares is what the ZIP contains — there are no static fallback folders. A folder declared with no files (and no children) ships with a single `.gitkeep` placeholder so the directory survives the ZIP round-trip.

Root-level folders come from two places: every emitted extension contributes one named for its `path`, and the template's `runtime_template_root_folders` rows each contribute one that is *deliberately* empty. The second kind exists because every extension folder carries an `app.json`, so there was no way to declare a folder that holds nothing — the symbol cache an AL build writes into (`.alpackages`), a team's `docs/` convention. They carry a path and nothing else; a folder that needs content is an `organization_files` row whose path nests, the way the shared ruleset lives under `.assets/rulesets/`.

**The placeholder only survives a commit if the repository's ignore rules allow it.** The platform `.gitignore` excludes `.alpackages/`, and git cannot re-include anything under a directory excluded outright — so a template declaring `.alpackages` ships the folder in the ZIP but git drops it. Keeping it needs the two-line form in the org's own `.gitignore` (`.alpackages/*` plus `!.alpackages/.gitkeep`), which is the organisation's ignore policy to set, not the generator's to rewrite. The template editor detects the clash per row and names both lines rather than letting an admin discover it from a repository that is missing the folder.

## Algorithm

```
1. Load the runtime template by key, eagerly including its WorkspaceExtension rows
   plus the recursive folder/file/dep trees. EF's Include doesn't recurse past one
   level — load folders/files via flat queries and reassemble the tree client-side
   (see GenerationService.AssembleFolderTree).
2. Load the selected modules by their keys, similarly including their recursive
   module_extension_folder / module_extension_file trees.
3. Build the EmittableExtension list:
     a. For each template extension (ordered by ordering): include if Required, OR
        if its Path is in plan.SelectedExtensionPaths.
     b. Append one EmittableExtension per selected module (using the module's
        recursive folder tree as its content; the module's `extension_name`
        (PascalCase) becomes the ZIP folder name; the rendered name defaults
        to "{{extension_prefix}} {module.extension_name}"). The module's
        `key` is the admin / URL slug and the dep-ref target, not the folder.
     c. Allocate id ranges in walk order. See "ID range allocation" below.
     d. Resolve each extension's dependencies into freshly-substituted name +
        freshly-allocated GUID. See "Dependency resolution" below.
4. For each EmittableExtension in walk order:
     a. Emit every `organization_files` row scoped to EveryExtension that
        the template opted into, rendering each through the per-extension
        mustache context (WorkspaceZipBuilder.WritePerExtensionOrgFiles).
        app.json is one of those rows — a canonical body seeded from
        PlatformOrganizationFiles that resolves {{extension_id}},
        {{dependencies_array}}, {{id_ranges_array}} and friends from the
        context built in step 3; see "app.json construction" below for what
        goes into each variable. Its output is re-serialised into AL's
        2-space style so inline arrays don't land on one line.
     b. AppSourceCop.json, when the org authors one, is just another such
        row — there is no generator-built AppSourceCop.json any more. The
        template's `app_source_cop_json` column survives as the authoring
        surface (and the TOML round-trip) but no longer emits the file.
     c. Walk the folder tree depth-first. For each folder, emit its declared
        files (skipping IsExample files when plan.IncludeExamples is false). If
        the folder ends up with no emitted files AND no child folders, drop a
        .gitkeep placeholder so empty directories survive the ZIP round-trip.
5. Generate workspace-root files:
     a. {{workspace_folder}}.code-workspace (see below).
     b. workspace.aldt.toml — the plan's form-post shape, so the New
        Workspace / New Extension pages can read a generated workspace back
        and regenerate it (WorkspaceConfigService). Its `[workspace]` section
        carries `name` (the customer name) and `short_name` beside it, which
        is how the New Extension sibling flow names "JM Banking" next to the
        workspace's own "JM Core" rather than guessing from the folder. A
        config written before short names existed has no key and falls back
        to the customer name.
     c. README.md and .gitignore are workspace-root-scoped
        `organization_files` rows, seeded from PlatformOrganizationFiles.
        Neither is generator-built and neither is an embedded resource any
        more; an org edits both at /admin/templates/files and each
        template opts in.
     d. Per-template-included files from `organization_files`, filtered by
        the `runtime_template_included_files` join — admins opt each template
        into the files that belong with it. Each row's `path` is
        workspace-root-relative; mustache substitution runs when
        `mustache_enabled` is true, using the same context as per-extension
        files. The New Workspace live preview folds the same set into the
        workspace-root tree so what the user sees matches the ZIP.
5b. Emit the template's declared empty root folders
     (`runtime_template_root_folders`, WorkspaceZipBuilder.WriteRootFolders).
     Each gets the same `.gitkeep` placeholder an empty leaf gets, since a ZIP
     carries no empty directory and neither does git. A folder that one of the
     workspace-root files from step 5d already lives inside is skipped — it is
     in the ZIP either way, with real content rather than a placeholder. The
     template validator refuses that overlap at save time; the check here is
     the belt-and-braces half, because an admin can opt a template into a new
     org file after declaring the folder. These folders do NOT appear in the
     `.code-workspace` `folders` array — see below.
6. Generate .assets:
     a. images/logo.{png|svg|jpg} — bytes from organization_assets for the
        acting org (M14). The file extension matches the asset's `content_type`.
        Pre-M14 this was an embedded resource; that path is gone.
     b. rulesets/Company.ruleset.json — also a workspace-root-scoped
        `organization_files` row (its `path` carries the .assets/rulesets
        prefix), seeded per org and template-opt-in like the rest. It was an
        embedded resource before; that path is gone.
7. Stream the whole thing as a ZIP.
```

Validation that doesn't fit the template-save validator (because it depends on plan-time inputs) runs upfront: `ValidateIdRanges` rejects a plan whose extensions resolve to overlapping ranges, surfacing a field-keyed `PlanValidationException`.

## `app.json` construction

For each emittable extension, the `app.json` is built by:

1. Start with the runtime template's `defaults_json` deserialized as the base. `application` and `platform` come from the plan (which pre-fills them from `defaults.application` / `defaults.platform` but lets the user override).
2. Set `id` to a freshly generated GUID — kept on the `EmittableExtension` so dependency resolution can reference it.
3. Set `name` to the extension's `NameTemplate` after mustache substitution. The template default is `"{{extension_prefix}} {extension_path}"` for declared extensions and `"{{extension_prefix}} {module_name}"` for module clones; admins can override either by writing the desired name template directly.
4. Set `brief`, `description` from the project plan.
5. Set `version` to `"0.0.0.1"` for new generations.
6. Set `application` to the extension's per-extension override when set, otherwise the plan's `ApplicationVersion`.
7. Set `runtime` to the extension's per-extension override when set, otherwise the plan's `RuntimeVersion`.
8. Set `idRanges` to a single range computed via the three-layer allocation below.
9. Set `dependencies` via the resolver below.

Serialise with 2-space indent for readability of the generated file.

## `AppSourceCop.json` construction

Deserialise `template.app_source_cop_json`, write it into each extension's folder. The `include` flag on the JSON column gates emission — set it to `false` to suppress the file workspace-wide. The flag is stripped before serialisation since AL would reject an unknown field. No per-extension customisation today.

## `.code-workspace` construction

A JSON file with:

```json
{
    "folders": [
        { "path": "Core" },
        { "path": "Hotfix" },
        { "path": "DocumentCapture" },
        { "path": ".", "name": "Workspace" }
    ],
    "settings": {
        "files.exclude": {
            "Core": true,
            "Hotfix": true,
            "DocumentCapture": true
        },
        "editor.formatOnSave": true,
        "editor.autoIndent": "full",
        "editor.detectIndentation": false,
        "editor.tabSize": 4,
        "editor.insertSpaces": true,
        "al.codeAnalyzers": [
            "${CodeCop}",
            "${AppSourceCop}",
            "${UICop}"
        ],
        "al.enableCodeAnalysis": true,
        "al.ruleSetPath": "../.assets/rulesets/Company.ruleset.json"
    }
}
```

That JSON is not a constant. `WorkspaceZipBuilder.BuildCodeWorkspace` layers three sources (Issue #61):

1. The organisation's base template, `organization_settings.code_workspace_json`, edited at `/admin/templates/workspace` (falling back to `OrganizationDefaults.CodeWorkspaceJson` when the org hasn't set one).
2. The optional per-template overlay, `runtime_templates.code_workspace_json`, edited on the template's own edit page. It deep-merges on the `settings` object and replaces wholesale on every other top-level key.
3. The computed `folders` array, written last and always authoritative — the workspace has to point at the folders the generator actually emitted, whatever either layer pasted.
4. The computed `settings."files.exclude"` entries: one `true` per extension folder, merged into whatever exclusions the two layers already set.

Mustache substitution runs over each layer before the merge, so both can use `{{publisher}}`, `{{workspace_folder}}` and the rest. A layer that doesn't parse as a JSON object raises a field-keyed `PlanValidationException` so the workspace and template error surfaces stay distinct.

Each `folders` entry uses the extension's `path`, which is also its on-disk folder name. For a module clone that path is the module's `extension_name` (PascalCase), not its `key` — the key is the admin/URL slug and the dependency-reference target.

The array is built from the emitted extensions, followed by one entry for the workspace root itself (`"path": "."`, named `Workspace`) so the root files (README, `.gitignore`, `.assets`, the declared empty folders) are reachable from the VS Code explorer. Without more, the root entry would list every extension a second time, so each extension's path is also written into `settings."files.exclude"` as `true`. Both come from the same folder list, so the sibling New Extension flow, which rebuilds the file with the new folder added, gets them too.

Apart from the root entry, the array is built from the emitted extensions alone. The template's declared empty root folders are deliberately left out: each entry here is an AL app root, and pointing the AL extension at a folder with no `app.json` breaks the workspace. `MergeTemplateOverlay` already skips the `folders` key, so neither layer can add one either.

## Mustache substitution

Substitution runs on every `.al` file's content, on the rendered extension `name`, on `AppSourceCop.json` strings, and on every workspace-root file from `organization_files` that has `mustache_enabled = true`. Non-AL extension files are written verbatim by default; admins can opt a particular folder/file into substitution explicitly via the authoring surface when they need it.

Available variables (canonical names are snake_case to match the TOML schema):

| Variable                | Source                                                                   |
|-------------------------|--------------------------------------------------------------------------|
| `{{name}}`              | The full extension name, e.g. "CRONUS Customer Core"                        |
| `{{workspace_name}}`    | The workspace name from the plan — the customer's name as typed, e.g. "Jørgensen Møbler" |
| `{{customer_name}}`     | The same value as `{{workspace_name}}`, under the word the form uses. Prefer it in new content. |
| `{{short_name}}`        | The plan's `ShortName`, or the customer name when it is blank, e.g. "JM" or "Jørgensen Møbler". A display value, not a path. |
| `{{workspace_folder}}`  | The folder the workspace unpacks into: the workspace name transliterated into the org's `naming_folder_style` (PascalCase by default), e.g. "JorgensenMobler". Use it wherever a path is needed. |
| `{{module_name}}`       | For module-cloned extensions, the module's `extension_name` (PascalCase). For template-declared extensions, equals `{{name}}`. |
| `{{publisher}}`         | `OrganizationSettings.DefaultPublisher`. |
| `{{extension_prefix}}`  | The plan's `ExtensionPrefix`, resolved server-side from the organisation's `extension_prefix_mode`: the short name (`Hidden`), the org's own value (`Fixed`), or what the form carried (`PerWorkspace`). See `customer-naming.md`. |
| `{{affix}}`             | `defaults.affix` when `defaults.affixType` is `Prefix` or `Suffix`; empty string when `AffixType.None`. Replaces the pre-unified `{{prefix}}` / `{{suffix}}`. |
| `{{namespace}}`         | The current folder's path, dot-separated, e.g. `src/codeunits` → `src.codeunits`. Used for AL `namespace` declarations. |
| `{{guid}}`              | A freshly generated GUID per substitution call. Use sparingly — prefer hand-authored GUIDs in example files. |
| `{{tenant_id}}`         | Tenant GUID captured on the New Workspace form (empty for standalone extensions). Persisted in `workspace.aldt.toml` so regeneration is reproducible. |

Legacy camelCase names `{{workspaceName}}`, `{{shortName}}`, `{{moduleName}}` still resolve as aliases — `MustacheRenderer` logs a single warning per render listing any encountered so admins can rename their org files at leisure.

If a variable in the content isn't recognised, leave it as-is (don't crash). Log a warning.

The mustache implementation is intentionally naive — regex replacement of `\{\{(\w+)\}\}` is enough.

## ID range allocation

Each extension gets one `idRanges` entry. Three layers, in priority order:

1. **Explicit on the extension.** If the row carries both `id_range_from` and `id_range_to`, use them verbatim. The cursor does not advance.
2. **Module-supplied size.** For a module-cloned extension, allocate `[cursor, cursor + size - 1]` where `size = module.IdRangeSize ?? template.ModuleIdRangeSize`. Cursor advances past the slice.
3. **Auto-allocated from the template.** The first auto-allocated template-declared extension consumes the plan's `[CoreIdRangeFrom, CoreIdRangeTo]`. Subsequent auto-allocated extensions take a `template.ModuleIdRangeSize`-wide slice from the cursor, and the cursor advances after each consumed slice.

**Where the cursor starts (#730).** `template.ModuleIdRangeStart` describes where the module range begins *relative to that template's own Core range*, not at a fixed absolute id. The cursor therefore starts at

```
template.ModuleIdRangeStart + (plan.CoreIdRangeTo - template.CoreIdRangeTo)
```

so whatever gap the template author left between the end of Core and the start of the module range (usually zero) is preserved wherever the workspace puts Core. The shift keys on the range's *end*, so widening Core also pushes what follows out of its way; leaving Core at the template's own default shifts by zero and allocates exactly as before. It applies whether or not the first extension carries explicit ids of its own — the plan's Core range is still what the user asked for.

So for a template with `core_id_range = 90000..90999`, `module_id_range_start = 91000` and `module_id_range_size = 200`, and a workspace selecting Core (auto), Hotfix (auto), and the Document Capture module:

- Core (first auto): `90000..90999` from the plan.
- Hotfix (next auto): `91000..91199` from the cursor.
- Document Capture (module, default size 200): `91200..91399`.

The same template and selection, with the workspace moving Core down to `60000..60999`:

- Core (first auto): `60000..60999` from the plan.
- Hotfix (next auto): `61000..61199` — the cursor shifted by `60999 - 90999 = -30000`.
- Document Capture: `61200..61399`.

Validation: the resolved ranges must not overlap. `ValidateIdRanges` checks this up front and throws `PlanValidationException` with a field-keyed `Extensions[*].IdRange` error pointing at the colliding pair. A downward move large enough to push the shifted cursor below object id 1 is refused earlier, keyed on `CoreIdRangeTo` so the New Workspace page can render it next to the range editor.

## Dependency resolution

`workspace_extension_dependencies` rows are emitted in `Ordering`, after any implicit deps the resolver injects. Three reference shapes (the CHECK constraint enforces exactly one per row):

- **`ref_extension_path`** — intra-workspace reference to another extension. The resolver looks up the target's freshly-allocated GUID and substituted name and emits a dep node. A reference to a `path` that isn't in this workspace (optional but not selected, or a typo) fails fast with `PlanValidationException`.
- **`ref_module_key`** — catalogue reference. When the named module was also selected for this workspace, point at its cloned extension's GUID. Otherwise the dep is silently dropped; the template-save validator should have rejected dangling references already.
- **`lit_id` + `lit_name` + `lit_publisher` + `lit_version`** — literal AL app coordinates. Emitted as-is.

**Implicit deps.** Module-cloned extensions get an implicit dependency on every required template-declared extension (Core, Hotfix, etc.) prepended before their own declared deps. So a Document Capture module clone in a workspace with required Core and Hotfix extensions ships with `dependencies = [Core, Hotfix, ...module's own]` even when the module's own declarations don't mention them. This lets the AL compiler see them without the template author having to spell it out.

Resolution is by stable identifier (`path` or `key`), never display name — display names get mustache-substituted and would drift between save time and generation time.

## Standalone extension generation

The New Extension flow uses the same `GenerationService` but with a simpler input:

```csharp
record StandaloneExtensionPlan(
    string TemplateKey,
    string ExtensionName,           // user-supplied freeform
    string Brief,
    string Description,
    string ApplicationVersion,
    string RuntimeVersion,
    int IdRangeFrom,
    int IdRangeTo,
    bool IncludeExamples,
    string Publisher,                // editable, defaults to template's publisher
    IReadOnlyList<DependencyEntry> Dependencies
);

record DependencyEntry(
    string DepId, string DepName, string DepPublisher, string DepVersion
);
```

The output is a single folder (no workspace wrapper, no `.code-workspace`), zipped:

```
MyExtension/
├── .alpackages/.gitkeep          # folder the template declares empty, if any
├── .assets/
│   └── rulesets/
│       └── Company.ruleset.json  # workspace-root-scoped org file, if the template opts in
├── app.json                      # per-extension-scoped org file
├── AppSourceCop.json             # per-extension-scoped org file
├── src/...                       # folder structure from the template's first extension
├── Translations/.gitkeep
├── .gitignore                    # workspace-root-scoped org file
├── README.md                     # workspace-root-scoped org file
└── workspace.aldt.toml           # the saved plan, so the form can be restored
```

**Both scopes of `organization_files` land here** (issue #522). The extension folder *is* the root of what the user unzips, so the workspace-root-scoped rows the template opts into (`.gitignore`, `README.md`, the shared ruleset, whatever else an admin curated) are emitted at that folder's root alongside the per-extension-scoped ones (`app.json`, `AppSourceCop.json`). Previously only the per-extension scope was written, so a standalone extension silently shipped without the `.gitignore` its template declares. `{{publisher}}` in those root files resolves to the plan's user-editable `Publisher` — the same value the extension's own `app.json` carries — rather than the org default the workspace flow uses.

Two exceptions:

- **The org logo is not emitted.** It's an `organization_assets` row, not a file, and `app.json`'s `logo` field points at it with a `../`-relative path that assumes the workspace wrapper this flow doesn't produce.
- **Sibling mode skips the workspace-root scope.** When the New Extension form has imported a `workspace.aldt.toml` and is scaffolding a sibling for an existing workspace, that workspace already carries these files at its own root; a second copy nested one level down would be noise. The sibling ZIP still carries the rewritten `{{workspace_folder}}.code-workspace` at its root.

The template's **declared empty root folders follow the workspace-root files exactly**: emitted in the normal standalone flow (the extension folder is the root of what the user unzips), skipped in sibling mode and when the caller is committing into a repository that already has a workspace root. A `.alpackages` one level down from the workspace root is not the folder the compiler looks in.

The folder structure inside the extension reuses the template's **first** `WorkspaceExtension` row as the scaffold (typically the conventional `Core` extension). All other declared extensions / modules / dependencies are ignored: the user is dropping the result into an existing workspace and wants only a self-contained extension shell. The `app.json` `dependencies` array comes from the user-supplied list rather than the template; if they want to depend on something Core-like in their existing workspace, they pick it via the dependency picker that sources from `well_known_dependencies`.

The success page should explain "drop this folder into your existing workspace and add a corresponding entry to `.code-workspace` yourself" clearly, with a copy-pasteable line for the workspace `folders` array.

## Error handling

- Invalid `ProjectPlan` (validation failed) → throw `PlanValidationException` with a dictionary of field-keyed errors. The page catches this and renders inline next to the offending field.
- IO error mid-stream → propagate. The user gets a server error page; the audit log captures nothing because nothing was written.

(There is no "missing example file" warning case any more: file content lives in the DB alongside the folder row, so it can't go missing relative to the row.)

## What gets logged

Each successful generation logs (at `Information` level) via structured fields:

- Workspace name (or standalone extension name).
- Template key.
- The walk order of emitted extension paths.
- Generated file count.
- ZIP size in bytes.
- Duration.

This is for operational telemetry, not user-facing. No need to persist it — `ILogger` output is enough.
