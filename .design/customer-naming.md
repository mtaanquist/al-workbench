# Customer naming: one customer, three names

How the New Workspace generator turns the name of a customer into the names the
generated output needs, and how that same act registers the customer as a
Solution. Supersedes the "workspace name" rules in `generation-engine.md`, which
this document rewrites in place as each slice lands.

**Status:** shipped (#755-#760, PRs #763, #765, #762, #766, #768, #769). This
document describes the current behaviour; `generation-engine.md` and
`github-integration.md` carry the parts that belong to their subsystems.

## Why

The generator asks for a "Workspace name" and derives everything from it by
stripping whitespace. That leaks two hidden rules onto the person filling in the
form: the name must be ASCII letters, digits and spaces, and it is silently also
the folder name, the `.code-workspace` name and the seed of the extension names.
A Danish consultant typing "Jørgensen Møbler" is refused. The same person then
types the customer a second time as a repository name and a third time as a
Solution, and nothing links the three.

The extension prefix has the mirror problem: it is pre-filled from the
*template*, but it is an organisation convention, and some organisations have no
such convention at all and would rather not see the field.

The point of the generator is that starting a new AL project for a new customer
should be one obvious act. This document makes the customer name that act.

## The names

One customer, typed once, produces these names. Each serves the place it
appears, which is why they do not share a convention.

| Name | Comes from | Style | Example |
| --- | --- | --- | --- |
| Customer name | typed by the user | as typed, any characters | Jørgensen Møbler |
| Short name | typed by the user, optional | as typed | JM |
| Workspace folder and `<folder>.code-workspace` | customer name | org folder style, default PascalCase, transliterated | JorgensenMobler |
| Repository | customer name | org repository style, default lowercase kebab-case, transliterated | jorgensen-mobler |
| Extension names | short name, falling back to the customer name | as typed, plus the extension's own suffix | JM Core |
| Extension prefix | org policy (see below), defaulting to the short name | as typed | JM |
| Solution name | customer name | as typed | Jørgensen Møbler |

Two rules make the table hold together:

- **The short name is an abbreviation, not a slug.** It exists because long
  customer names make ugly extension names in Business Central's Extension
  Management page, and it is the consultant's call what the abbreviation is.
  When it is blank, the customer name is used unchanged, so "Jørgensen Møbler
  Core" is a perfectly good extension name.
- **The derived names never use the short name.** The folder and the repository
  are meant to be found by the full customer name, so they are always derived
  from it. A repository called `jm` would be a mystery a year later.

## Transliteration and styles

`Services/Generation/CustomerNaming.cs` owns both. It is the single home for
turning a free-text customer name into a machine name; `GenerationNaming.StripWhitespace`
and `GitHubWorkspaceRepositoryService.SuggestName` are replaced by it.

**Transliteration** runs first and is the same for every style. It is a fixed
table, not a setting: Unicode canonical decomposition strips diacritics
(é to e, ä to a, ñ to n), and an explicit map covers the letters that do not
decompose:

| From | To |
| --- | --- |
| æ / Æ | ae / AE |
| ø / Ø | o / O |
| å / Å | aa / AA |
| ß | ss |
| œ / Œ | oe / OE |
| ð / Ð, đ / Đ | d / D |
| þ / Þ | th / TH |
| ł / Ł | l / L |

`å` becomes `aa` because that is the Danish and Norwegian convention and this
tool's users are largely Scandinavian; `ä`, `ö` and `ü` lose their diacritic
rather than gaining an `e` for the same reason (Swedish, not German, is the
neighbour). Anything that is still not a letter or digit after transliteration
is a word separator. A name with no letters or digits left is invalid.

**Styles** (`NamingStyle` enum) decide how the words are joined:

| Style | "Jørgensen Møbler A/S" |
| --- | --- |
| `PascalCase` | `JorgensenMoblerAS` |
| `camelCase` | `jorgensenMoblerAS` |
| `kebab-case` | `jorgensen-mobler-a-s` |
| `snake_case` | `jorgensen_mobler_a_s` |
| `lowercase` | `jorgensenmobleras` |
| `None` | `Jorgensen Mobler A S` (transliterated, separators collapsed to one space) |

Word boundaries are separators only; an existing PascalCase run inside a word
is left alone (`CRONUS` stays `CRONUS` in PascalCase, `cronus` in kebab-case).
Results are capped at 100 characters, which is also GitHub's repository-name
limit.

## Organisation settings

Three new columns on `organization_settings`, edited on the existing Defaults
page (`/admin/templates/defaults`) in a new **Naming** section. No new admin tab.

| Column | Type | Default | Meaning |
| --- | --- | --- | --- |
| `naming_folder_style` | `NamingStyle` text | `PascalCase` | Style of the workspace folder and `.code-workspace` name. |
| `naming_repository_style` | `NamingStyle` text | `kebab-case` | Style of the suggested repository name. |
| `extension_prefix_mode` | text: `Hidden` / `Fixed` / `PerWorkspace` | `PerWorkspace` | See below. |
| `extension_prefix` | text, nullable | null | The org-wide value when the mode is `Fixed`, or the pre-fill when `PerWorkspace`. |

`None` is only offered for the folder style; a repository name cannot contain
spaces, so the repository style picker leaves it out.

The **extension prefix** resolves per mode, and the result is what
`{{extension_prefix}}` renders to:

- `Hidden` — the field is not on the form, and the prefix *is the short name*
  (with its customer-name fallback). Existing name templates written as
  `"{{extension_prefix}} Core"` keep producing "JM Core" without editing.
- `Fixed` — the field is not on the form; the org value is used for every
  workspace. This is for organisations whose extension names carry the
  partner's mark rather than the customer's.
- `PerWorkspace` — the field is on the form, pre-filled with the org value if
  one is set, else the short name. Today's behaviour, minus the template-level
  default: `TemplateDefaults.ExtensionPrefix` is no longer read by the form and
  is dropped from the TOML schema in the same slice.

The MCP tool mirrors this: `extensionPrefix` is ignored under `Hidden` and
`Fixed`, and the result reports the prefix that was actually used.

## Plan shape

`ProjectPlan.WorkspaceName` keeps its C# name (the spine rule from `CLAUDE.md`:
rename what a person sees, not the identifiers) but now means *the customer
name as typed*. The form labels it **Solution**, in the section headed
**Solution** - it is the customer's solution that is being named, and Solution
is what the rest of the app calls that record (issue #813). The plan gains
`ShortName`.

```csharp
record ProjectPlan(
    string TemplateKey,
    string WorkspaceName,            // the customer name, as typed
    string? ShortName,               // abbreviation; null or blank falls back to WorkspaceName
    string ExtensionPrefix,          // already resolved per the org's prefix mode
    string Brief,
    ...unchanged
);
```

Validation (`GenerationService.ValidateWorkspacePlan`):

| Field | Rule | Message |
| --- | --- | --- |
| `WorkspaceName` | required; 1 to 100 characters; must contain at least one letter or digit; no control characters | "Required. Give the customer's name, for example CRONUS A/S." |
| `ShortName` | optional; at most 50 characters; no control characters | "At most 50 characters." |
| rendered extension name | at most 200 characters | "The extension name would be longer than 200 characters, which Business Central refuses. Use a shorter short name." |

The 200-character rule is AppSourceCop AS0047; the platform itself stores the
name as `Text[250]`, so 200 is the safe ceiling for both cases. The old
`WorkspaceNameRegex` and its HTML `pattern=` go away. The standalone New
Extension form keeps its own extension-name regex for now; it names an
extension, not a customer.

## Mustache variables

| Variable | Value | Change |
| --- | --- | --- |
| `{{workspace_name}}` | The customer name as typed, "Jørgensen Møbler". | Unchanged meaning; now allows any characters. |
| `{{customer_name}}` | Same as `{{workspace_name}}`. | New. The name templates should use it; the alias exists so the word on the form matches the word in the template. |
| `{{short_name}}` | The short name, or the customer name when blank: "JM", or "Jørgensen Møbler". | **Changed.** Was the whitespace-stripped workspace name. |
| `{{workspace_folder}}` | The folder-style derivation, "JorgensenMobler". | New. Takes over the one job the old `{{short_name}}` was doing. |
| `{{extension_prefix}}` | The resolved prefix per the org's mode. | Value source changed; name unchanged. |

The change to `{{short_name}}` is deliberate and is the one that needs a
migration of content: an `organization_files` row or a template file using
`{{short_name}}` for a *path* (a `.code-workspace` folder entry, a namespace
root) must switch to `{{workspace_folder}}`. The system-org bodies in
`PlatformOrganizationFiles.cs` are updated in the slice; forked org rows are
found by a one-off migration that rewrites `{{short_name}}` to
`{{workspace_folder}}` only inside `code_workspace_json` and file paths, where a
display string can never have been intended. Prose uses ("Customizations made
for {{short_name}}") are left alone: "made for JM" and "made for Jørgensen
Møbler" are both what the author meant.

`MustacheVariableCatalog` gains the two new rows so the admin editors list them.

## `workspace.aldt.toml`

The `[workspace]` section gains `short_name` next to `name`. `name` stays the
customer name. `WorkspaceConfigService` reads both back, with the saved
`extension_prefix`, so the New Extension sibling flow names the new extension
"JM Banking" beside "JM Core" instead of guessing from the folder: the user types
"Banking" and the prefix is added (`generation-engine.md`, "Adding to an
existing workspace").

## The Solution picker

The **Solution** field on New Workspace is a combobox over the Solutions the
user can see (`ProjectAccess`, the same rule `list_solutions` applies), searched
as the user types, with a final row **Create "Jørgensen Møbler" as a new
solution** when the typed text matches no existing name. It is a shared
component, `Components/Shared/SolutionPicker.razor`, because New Extension will
want it next.

Picking an existing Solution pre-fills:

- the customer name (read-only until the selection is cleared),
- the short name, from the Solution's new `short_name` column,
- the tenant ID, from `oe_projects.bc_tenant_id` when set,
- and shows the Solution's existing repositories under the GitHub card, so a
  second workspace for the same customer is a visible choice, not an accident.

Choosing "create new" creates nothing yet. The Solution comes into being only
when **Create repository** succeeds, for the same reason the repository itself
is not created until every refusal is ruled out: an abandoned form leaves no
orphan. **Download ZIP** never creates a Solution. A consultant who downloads
and pushes by hand registers the Solution afterwards as they do today; that is a
conscious trade against a Solutions list full of experiments.

`oe_projects` gains `short_name` (text, nullable, at most 50). The Solution
detail page gets a **Short name** field with the same caption as the generator.
`ProjectInput` carries it.

## Create repository registers the Solution

`GitHubWorkspaceRepositoryService.CreateAsync` takes an optional
`solutionId`. After the repository exists and is filled:

- with a `solutionId`, an `oe_project_repositories` row is added to that
  Solution (`provider = github`, the clone URL, the repository name as display
  name), through `ProjectService` so discovery warms as it does for a manually
  added repository;
- without one, `ProjectService.CreateProjectAsync` creates a Solution named
  after the customer, with the short name, that one repository, and the default
  visibility (`Public`, meaning everyone in the organisation, as every Solution
  starts today). The creating user owns it.

A Solution also carries a country code to compile against, and this form never
asks for one, so the created Solution takes the organisation's first import
country - the same pre-fill the untracked-repositories panel offers - and `w1`
when the organisation has none. It is one field on the Solution's own page; a
Solution that refused to save over it would be a worse answer than a default.

A `solutionId` the caller may not manage is refused up front, with the other
refusals, before anything is created. Either way the audit entry for the
generation names the Solution: it is the entry's entity id, which was zero
before this because a repository on GitHub has no id of ours. The success card
on the page links to it.

**None of this happens when the organisation does not use Solutions.** The tool
can be switched off site-wide by a SiteAdmin or per organisation by an org Admin,
and when it is, Solutions is not in the sidebar and `/solutions/*` is a 404 - so
registering one would file the customer somewhere nobody in that organisation can
look. New Workspace then shows the plain Customer text box rather than the picker,
`CreateAsync` skips the registration entirely (no `SolutionId`, no
`SolutionCreated`, no warning, and the audit entry's entity id stays zero), and a
`solutionId` passed anyway - by MCP, which is the only surface that still can - is
refused on the `SolutionId` field before anything is created. The question is
asked once, by `Services/Tools/ToolEnablement`, which combines the site toggle
with the acting organisation's own opt-out exactly as the sidebar and the route
gate do (issue #772).

The repository is created first because it is the step that can fail for
reasons outside the tool (name taken, permissions), and a Solution with no
repository is exactly the orphan the ordering exists to avoid. A repository
that exists but whose Solution row failed to save is reported as a warning on a
success, the same shape as `StandardsWarning`.

## MCP parity

`generate_workspace` gains `shortName` (on `ProjectPlanInput`) and `solutionId`
(beside `createRepository`, since it only means anything there) alongside the
existing `workspaceName`, whose description becomes "the customer's name". When
`createRepository` is set, the result carries the Solution id and name the
repository was registered under, whether it was created, and the warning when it
could not be. `create_repository` takes the same `solutionId`, because it calls
the same service and would otherwise be the one way to make a repository that
registers nothing.
`list_solutions` gains `shortName` in its rows so an agent can see it.
`extensionPrefix` follows the org mode as described above.

## The example-files toggle

Not a naming change, but it lands in the same milestone because it is the other
thing the fresh-eyes review of the page settled. **Include example AL files**
moves from the Options section at the bottom of the form into the header of
the live preview card, as a toggle beside the Live badge. The preview tree
responds to it at once by greying and striking out the files that would be
left out, so the option explains itself and needs no caption.

## Out of scope

Recorded so they are not pulled in by accident. The ones worth keeping are in
`roadmap.md`.

- Collapsing the rarely-changed fields (descriptions, tenant ID, ID range)
  behind a disclosure.
- Rendering the substituted description under the field as the user types.
- Renaming the primary button.
- A Solution picker on New Extension.
- Per-template naming overrides. Naming is an organisation convention.
