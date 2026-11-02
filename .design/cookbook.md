# Cookbook

Per-organisation library of reusable AL **recipes** — what used to be called
"Snippets". The rename matters because recipes now span more than one file
and a folder structure: the original Snippets feature was capped to a flat
list of files, which forced authors to either lose the folder layout or skip
larger artefacts.

## Entities

- `Recipe` — one row per published recipe. Columns mirror the pre-rename
  `Snippet` shape (title, description, keywords, deprecated, instructions,
  minimum-application-version FK, soft-delete) **plus** a `Type` int
  discriminator.
- `RecipeFile` — one row per file inside a recipe. Carries a
  `RelativePath` text column for the folder it lives in (empty = root) and
  a flat `FileName` (no slashes). The ZIP download joins `RelativePath`
  and `FileName` with `/` so `ZipArchive` materialises folders
  automatically.
- `RecipeSuggestion` / `RecipeSuggestionFile` — same shape, separate
  tables, for the user-suggested-draft → admin-approval workflow.

The pre-rename tables (`snippets`, `snippet_files`, etc.) were renamed in
migration `20260618000000_RenameSnippetsToCookbook`. Existing rows surface
as `Type = Snippet` with an empty `RelativePath`, identical to their
pre-rename behaviour. URL redirects in `Program.cs` keep old `/snippets`
links working.

## Recipe types

`Domain/ValueObjects/RecipeType.cs`:

- **`Snippet`** — a small pattern, typically one or two files. Use for
  self-contained fragments: a single event subscriber, one
  `tableextension`, a focused helper codeunit.
- **`Pattern`** — a few related files that together solve one problem.
  Examples: an event subscriber plus the page/table it modifies; a setup
  table + page + install codeunit. Files may live under a folder structure.
- **`Module`** — a near-complete feature spanning several files and
  namespaces under one top-level namespace. Bigger than a Pattern; smaller
  than a full BC app.

The type is a chip-row filter on `/cookbook` and a badge on each card. It
is **not** part of the search expression — `RecipeService.SearchAsync`
ignores it. Type filtering happens client-side over the search result.

## Folder layout in files

`RelativePath` validation, enforced by `RecipeService.ValidateFiles`:

- Empty allowed (root).
- Split on `/`; each segment matches
  `^[A-Za-z0-9._-][A-Za-z0-9._ -]*$` — no `..`, no `.`, no empty segments,
  no control characters.
- No leading or trailing `/` (normalised away before validation fires).
- Max 8 segments, max 260 characters total.
- `(RelativePath, FileName)` must be unique within a recipe (case-insensitive).
- At most 100 files per recipe (`RecipeService.MaxFiles`); a suggestion is held
  to the same cap because it goes through the same check.

`Components/Shared/RecipeFileEditor.razor` exposes Folder + File-name +
Content inputs per row; the user-facing `RecipeDetail` page renders a
flat list showing `RelativePath/FileName` on each file.

## Download attribution

Every time a recipe leaves the app a `RecipeDownload` row is written —
`Source = Download` for the ZIP, `Source = Copy` for a per-file copy (once
per visit, no customer asked). The download modal asks which customer the
recipe is for and explains why, but does not gate the download on an
answer: a required field here collected "test" and "x" from everyone
downloading for a demo, and null ("Not recorded") is the more useful
answer. See #539.

The modal's suggestion list is the union of the org's **active projects**
and the distinct customer names recorded on earlier uses, de-duplicated
case-insensitively with the project's spelling winning and sorted
alphabetically (`RecipeService.GetCustomerSuggestionsAsync`). At record
time the typed name is matched case-insensitively against active project
names the user can see and, on an exact match, `recipe_downloads.project_id`
is stamped —
`Project.Name` is unique per org among active rows, so "picked a project"
and "typed exactly a project's name" are the same event and no UI has to
tell them apart. A Private solution outside the user's teams is not matched:
the use is recorded as a plain customer name, and the suggestion list leaves
out both that solution and any recorded name that spells it (#1545). The free-text name is still stored as typed: it is the
label, the id is the attribution. `project_id` is nullable with
`ON DELETE SET NULL`, so hard-deleting a project leaves the history intact.
The admin recipe page links the customer cell to the project when one
matched. See #541.

A third source, `Repository`, records that the recipe was committed to a
GitHub repository as a pull request (`RecordRepositoryApplyAsync`, #626).
It is the only source that names a *place*: `recipe_downloads.repository`
holds the `owner/name`, and the distinct values
(`GetAppliedRepositoriesAsync`, most recently applied first) are what the
admin page's "Update the repositories that use this recipe" card iterates
when a bug found in a recipe has to reach everywhere it landed. The
customer half behaves exactly as a download's does, project match
included. Both the download modal and the `apply_recipe` MCP tool record
through the same method, so an agent's apply shows up in the history like
anyone else's — the older "downloads are a web-UI flow only" rule no
longer holds for this source.

## MCP surface

Tools live in `Services/Mcp/Tools/CookbookTools.cs`:

- `search_recipes(query, includeDeprecated?, limit?)` — fuzzy ILIKE over title /
  description / keywords. Returns a `RecipeSummary` per match including
  the type string: 25 by default, at most 50, ordered by title, with
  `truncated` and `note` when more matched. A description is cut at 300
  characters; keywords are kept whole (#1512).
- `get_recipe(id, path?, offset?)` — the recipe and its files; each file's
  `Path` already joins `RelativePath` and `FileName`. Bodies come back in
  order while they fit about 30,000 characters together with the recipe's
  own text; a file past that is listed with a null body, and a single file
  too large for one answer is cut. `path` names a file or a folder to read,
  and `offset` continues a cut file from where the note says it stopped
  (#1512).
- `get_cookbook_guidance()` — returns the org's authored Markdown
  conventions (from `organization_settings.cookbook_guidance`), a
  built-in dictionary describing what each `RecipeType` means, **and a
  short-lived signed `GuidanceToken`** the write tools require. Built-in
  type descriptions live in code so an empty org-level guidance still
  steers the agent.
- `suggest_recipe(input)` — submits to the admin queue. Input includes a
  `GuidanceToken` from `get_cookbook_guidance`, plus `Type` as a string
  (`"Snippet"` / `"Pattern"` / `"Module"`) and a list of files each with
  its own `RelativePath`.
- `update_recipe_suggestion(input)` — edit a pending suggestion; same
  `GuidanceToken` gate. Because the submitter can still edit while a
  reviewer has the suggestion open, approval publishes only the version
  the reviewer saw: the review page passes a fingerprint of the content
  it rendered, and approval is refused (and the page reloads with the new
  version) if the suggestion changed since.
- `update_recipe(input)` — full-replace edit of an already-published
  recipe. Same `GuidanceToken` gate, plus a role gate: the acting user
  must be Editor or Admin (or SiteAdmin), mirroring the web admin pages.
  The optional fields preserve the recipe's stored value when omitted,
  so a `get_recipe` → `update_recipe` round-trip can't silently reset
  them (#1626): `Deprecated`; `EstimatedValueHours` (removed only by
  `ClearEstimatedValueHours`); and the minimum version, set by
  `MinimumApplicationVersionId` or by `MinimumApplication` - the version
  string `get_recipe` returns, or the catalogue name or key, resolved
  through the org's own catalogue - and removed by an empty
  `MinimumApplication`; a kept version that has since been removed from
  the catalogue is refused with a message asking for one of those. A file sent with a null `Content` keeps the
  stored body of the file at that path, because `get_recipe` lists
  files that didn't fit in its answer with a null body; a null body for
  a path the recipe doesn't have is refused.
- `apply_recipe(recipeId, repository, customer?)` — commits the recipe's
  files into a GitHub repository and opens a pull request for them,
  returning a `RepositoryDeliveryResult` (with `IsNewPullRequest`). It
  routes through `GitHubRecipeDeliveryService`, the same service the
  download modal uses, so `GitHubRepositoryService.ResolveAsync`'s gate is
  inherited: a repository the picker would not offer is refused here too.
  Records the apply exactly as the page does. See #626 and
  `.design/github-integration-phase2.md`.

Suggestion inputs (and `update_recipe`) also accept an optional
`EstimatedValueHours`; the proposed value is stored on the suggestion
row and carried onto the recipe at approval.

### Mandatory two-step protocol

The write tools (`suggest_recipe`, `update_recipe_suggestion`,
`update_recipe`) refuse to run
without a valid `GuidanceToken` from a recent `get_cookbook_guidance`.
This makes the ordering mandatory rather than suggested — the steering
doesn't depend on the agent model being well-behaved.

The token is built and verified by `CookbookTools` via
`IDataProtectionProvider` with purpose
`ALDevToolbox.Cookbook.GuidanceToken.v1`:

- **Payload**: `"{organizationId}|{expiresAtUnix}"`.
- **Protection**: Data Protection's HMAC + AES + key rotation, sharing
  the `app-keys` volume the rest of the app uses for the SMTP password
  and off-site backup credentials.
- **Lifetime**: 30 minutes (`CookbookTools.GuidanceTokenLifetime`). Long
  enough for an agent to draft after reading the guidance; short enough
  to force a fresh consultation per session. Tokens are reusable inside
  the window — submitting two recipes after one consultation works.
- **Org-binding**: the token signs the issuing organisation's id, so a
  token from org A leaked into a session in org B is refused.

Error messages are deliberately specific so the agent knows the
recovery action: every refusal includes "Call get_cookbook_guidance and
pass the returned GuidanceToken to this tool."

## Org-level authoring guidance

`OrganizationSettings.CookbookGuidance` is a Markdown column edited from
the **Cookbook authoring guidance** section on `/admin/cookbook`. 10,000
character cap, persisted by
`OrganizationConfigService.SaveCookbookGuidanceAsync`, read by
`get_cookbook_guidance`. Encourage organisations to document naming
conventions, prefixes, preferred event-subscriber style — anything an
agent should know before drafting a recipe.

## URLs at a glance

| Old (still works via 301) | New |
|---|---|
| `/snippets` | `/cookbook` |
| `/snippets/{id}` | `/cookbook/{id}` |
| `/snippets/suggest` | `/cookbook/suggest` |
| `/admin/snippets` | `/admin/cookbook` |
| `/admin/snippets/new` | `/admin/cookbook/new` |
| `/admin/snippets/{id}` | `/admin/cookbook/{id}` |
| `/admin/snippets/suggestions` | `/admin/cookbook/suggestions` |
| `/api/snippets/{id}/download` | `/api/cookbook/{id}/download` |

The MCP tool names also changed (`search_snippets` → `search_recipes`
etc.) — no aliases are kept because agents discover tools dynamically and
two tool names with overlapping responsibilities would dilute the
"call get_cookbook_guidance first" steering.
