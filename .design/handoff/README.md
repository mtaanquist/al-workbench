# Claude Design handoff — Business Central design system

The rendered prototype this folder mirrors lives in the Claude Design project
**AL Dev Toolbox Design System**, id `63d872d4-c751-4420-b910-cb7eec63e4c3`
(`https://claude.ai/design/p/63d872d4-c751-4420-b910-cb7eec63e4c3`). It was
produced from the brief in `../design-system-brief.md`.

`DESIGN-SYSTEM.md` is the system's own index — token contract, component
inventory, page-archetype catalogue, and the six non-negotiables. Read it first.

## What is checked in here, and what is not

Checked in — the layers we are actively porting:

| File | Owns |
| --- | --- |
| `tokens.css` | The whole token contract. **Copied verbatim to `ALDevToolbox/wwwroot/tokens.css`.** |
| `components.css` | Every reusable component. References tokens only. |
| `shell.css` | Sidebar, top bar, content column, sticky page head, responsive steps. |
| `DESIGN-SYSTEM.md` | The system index. |
| `bc-reference.md` | Notes taken from real BC client screenshots (field chrome, dialogs, lists). |

Also checked in, as the PRs that ported them landed: `pages.css`,
`pages-forms.css`, `pages-power.css`, `pages-content.css`, and the review sheets
for the screens we have translated so far (`PageList.dc.html`,
`PageSettings.dc.html`, `ComponentsPanel.dc.html`, `PageLauncher.dc.html`,
`PageDashboard.dc.html`, `PageAuth.dc.html`, `PageObjectExplorer.dc.html`,
`PageCompare.dc.html`).

`PageObjectExplorer.dc.html` was pulled in PR 14c ([#570]) because PR 14b's
fidelity review could not do a cell-by-cell diff without it, and the screen it
describes now spans three PRs (14a, 14b, 14c). It was written straight from a
`get_file` result in the same turn it was fetched, then checked structurally
(35 `.codev__ln` rows, 17 `TREE` entries, 4 `.pane__sec` blocks, balanced
`div`/`span`/`button`/`svg`). Treat it as a faithful-but-transcribed copy: if a
diff against it ever looks wrong in a way the design project would not explain,
re-pull before believing it.

`PageCompare.dc.html` was pulled in PR 14d for the same reason and checked the
same way (58 `.diff__ln` rows across the side-by-side and inline layouts, 6
`.hunk` separators, 3 `.cmp__phead` blocks, balanced
`div`/`span`/`button`/`svg`/`label`). One handoff screen, two of our pages: the
Object Explorer's file diff and the standalone Diff tool (at `/diff`; called
Compare until #578) both translate from it. The handoff file keeps its
`PageCompare.dc.html` name because it mirrors the Claude Design project.

`PageEnvironmentsList.dc.html`, `PageUpgrades.dc.html` and
`PageEnvironmentDetail.dc.html` were pulled for the page-archetype milestone
([#818]) together with a refresh of `PagesStandard.dc.html`, `ShellFrame.dc.html`,
`ShellPageBody.dc.html`, `PageList.dc.html`, `DESIGN-SYSTEM.md` and
`foundations.css`. These are byte-exact, not transcribed: the `get_file` results
were extracted from the session transcript by script rather than retyped, and
the seven byte-locked sheets were compared the same way and found identical. The
app deliberately diverges from the Environments sheet's copy in a few places;
the design project's `briefs/2026-09-port-corrections.md` records which.

`PageReleasePipelines.dc.html` / `ReleasePipelinesBody.dc.html`,
`PageReleasePipeline.dc.html` / `ReleasePipelineBody.dc.html` and
`DeliveryRowPanel.dc.html` were pulled on 2026-09-23 for the release-pipeline pages
(issues #929, #930, #931, #932, #935), with the `Components.dc.html` section that hosts the
delivery row and the `ShellFrame.dc.html` bodies that mount the two pages. Extracted from
the transcript by script, as above. They are the versions after the design agent worked
through `briefs/2026-09-release-pipelines-review.md`, which lists what the sheets first
drew that the product cannot do and what they now draw from data not recorded yet.

`PageUpgradesList.dc.html` / `UpgradesListBody.dc.html`, `PageUpgrade.dc.html` /
`UpgradeBody.dc.html` and `PageUpgradePicker.dc.html` / `UpgradePicker.dc.html` were pulled
on 2026-09-26 for the planned-upgrades tool (issue #984: an upgrade header with lines, the
Open / Archive / Fleet views, the environment picker and the "Add to upgrade..." dialog),
with the `ShellFrame.dc.html` bodies that mount the two pages, the `embedded` variant of
`PageUpgrades.dc.html` that the Fleet view hosts, and a vocabulary fix to
`PageDashboard.dc.html` (projects to solutions). Extracted from the transcript by script, as
above; `components.css` and `pages.css` were compared the same way and found identical.
They are the versions after the design agent worked through
`briefs/2026-09-planned-upgrades.md` and the four review corrections that followed: the
Open-upgrades dashboard tile is parked (drawn on the picker sheet, placed nowhere, because
the Admin dashboard is Admin-only and the upgrade team is not), the note survives on a done
upgrade, Production is semibold rather than red, and the picker cannot offer deleted
environments. Three notes for the port were left with the sheets: the open upgrade's
overflow shows Reopen disabled where the app should hide it, the loading text says it reads
from Business Central where the app reads the mirror, and the picker's 1000px dialog is a
size `components.css` does not have yet.

**What shipped without a sheet** is listed in the design project's
`briefs/2026-09-shipped-without-a-sheet.md` (pushed 2026-09-21): the environment page's
five tabs with the Operations and Sessions lists, the storage bar and the Deleted view on
the Environments list, the copy-environment dialog, the Solutions list's customer-info
rail (a list-with-rail archetype variant), the Customer tab and its `.cust-list` pattern,
and the customer-modules admin page - each with what was decided and what a design pass
should settle. It also carries one **correction to `components.css`**: `.modal-backdrop`
mixes from `--ink`, which flips with the theme, so the dark scrim comes out nearly white.
The app overrides it in `app.css` until the sheet is fixed upstream and re-pulled; delete
the override then. When a doc in `.design/` says something "needs a design pass upstream",
that brief is where it is tracked. The Solutions list's entry has an addendum checked in
here, `briefs/2026-09-solutions-list-rail.md` (#906: the row as the selector, the rail
reserved with an empty state, new columns), waiting to be folded into it upstream.
A second addendum, `briefs/2026-09-preview-builds.md` (#994), records the nightly preview
check's editor box, its results on the pipelines list and pipeline page, the preview pill and
tags, and the dashboard tile's foot, also waiting to be folded in.

**The command palette** has its own brief, `briefs/2026-09-command-palette.md`, for the
same reason and written to the same shape: the palette ([#880]-[#888]) shipped without a
sheet because the system has nothing like it. It covers the overlay, the result row, the
group header, the best-match row, the foot, the four list states, light and dark, and
phone width - with the class names and tokens the built palette actually uses, so the
design side can draw what shipped rather than reconstruct it. It also carries one
**measurement for the system, not for the palette**: the selection keyline `--primary` is
2.45:1 against `--surface` in light, under the 3:1 WCAG asks of a state indicator, and it
is the same keyline as `.nav-item.is-active::before`, `.data-table tr.is-selected` and
`.run-row.is-selected` - so it wants deciding once, upstream, rather than diverging in one
sheet.

Unlike `briefs/2026-09-shipped-without-a-sheet.md`, which lives in the design project, this
one is checked in here first: it was written in a session with no `DesignSync` access. Push
it upstream with `finalize_plan` + `write_files` next time one is open, and then treat the
design project as the copy of record like the rest of this folder.

[#570]: https://github.com/mtaanquist/ALDevToolbox/issues/570
[#818]: https://github.com/mtaanquist/ALDevToolbox/issues/818
[#880]: https://github.com/mtaanquist/al-workbench/issues/880
[#888]: https://github.com/mtaanquist/al-workbench/issues/888

PR 12 is the exception: it ported archetypes 12-14, and their three sheets
(`PageDocs.dc.html`, `PageMcpSetup.dc.html`, `PageErrorStates.dc.html`) were
read against the port but **not** checked in. `get_file` hands the content to
the agent rather than to disk, so checking one in means retyping ~9 KB of HTML,
and a review sheet that is subtly wrong is worse reference material than one you
have to pull. Pull them with the command below if you need to diff against
them; what the port actually decided is written up in `../design-migration.md`.

Not checked in yet — pull the layer you are actually porting, don't bulk-import:

- the remaining `*.dc.html` review sheets (`KitchenSink.dc.html` is the
  all-in-one view), `support.js`, `foundations.css` (review scaffolding, never shipped)
- `screens/*.png` — the design agent's own screenshots, useful for pixel-diffing

Pull one with the `DesignSync` tool:
`DesignSync{ method: "get_file", projectId: "63d872d4-...", path: "pages-forms.css" }`
and write it here alongside the rest, in the same PR that ports it.

## Re-syncing

The copy of `tokens.css` in this folder mirrors the design project exactly, and
`ALDevToolbox/wwwroot/tokens.css` mirrors this one. All three are byte-identical
and should stay that way — that is what makes a re-sync diff readable:

```
diff .design/handoff/tokens.css ALDevToolbox/wwwroot/tokens.css   # must be empty
```

The same holds for `components.css`, `pages-forms.css`, `pages.css` and
`pages-content.css`: push the app copy upstream with `DesignSync`, then copy it
here, so all three match. PR 9a
found them ~220 lines apart, because earlier corrections went upstream but the
local copy was never re-pulled. Check the diff when you touch one.

Anything that shows up there is drift. Fix it by deciding which side is right,
changing **the design project**, and re-pulling — never by patching one copy.

**Corrections go upstream, not into the app.** When the port finds a real
problem with the handoff, fix it in the design project so the next re-sync keeps
the fix. Done once already: the token layer originally specified the bare Segoe
UI stack and no web fonts, which cannot work off Windows — Segoe UI is not
licensable for web embedding, and the stack fell through to Tahoma/Helvetica
rather than the platform's own UI font. `--font-sans` now names Segoe first,
then **Selawik** (Microsoft's OFL-1.1 metric-compatible replacement, vendored in
`wwwroot/fonts/` and declared in `wwwroot/fonts.css`), then `system-ui`. That
correction was written back to the design project, so this copy and the app copy
both match upstream again.

Push a correction with `DesignSync`: `finalize_plan` (writes, deletes, and
`localDir` pointing at this folder), then `write_files` with a `localPath`.

Component and page CSS is *translated*, not copied — see CLAUDE.md,
"Implementing a Claude Design handoff". Port the prototype's rules near-verbatim
onto our tokens, but re-express structure through our Blazor components.
