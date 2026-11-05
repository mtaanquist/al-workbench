# Migrating onto the Business Central design system

Working doc for the `design/bc-system` integration branch. The brief that
produced the design is `design-system-brief.md`; the handoff itself is in
`handoff/` (see `handoff/README.md` for what is imported and how to pull more).

This doc records **what we found when we measured the port**, **the decisions
that need a human**, and **the PR order**. Update it as each PR lands.

## Where the work happens

`design/bc-system` is a long-lived integration branch off `main`. Each step
below is its own PR **targeting `design/bc-system`**, not `main`; the branch
merges to `main` once the app is fully migrated and green. Branch names follow
the repo convention with a `design/` type prefix — `design/tokens`,
`design/shell`, `design/tool-translator`.

Migrating tool-by-tool is the point: each PR moves one surface onto the shared
system **and deletes that surface's bespoke CSS in the same PR**, so the old and
new never coexist long enough to drift.

**Health metric: `wwwroot/tools.css` line count.** 5,472 at the start. It only
goes down. When it hits zero the migration is done.

## Where the branch stands

*Updated after the PR 8 audit. Keep this block current — it is the first thing to
read when picking the work back up. Re-measure with
`python3 .design/progress.py` rather than trusting the numbers below
once a few PRs have landed.*

**105 commits on `design/bc-system`, all pushed** (tip `4d1c1d4`).

**Health metric, restated after PR 17b: `tools.css` 5,472 at the start of the
branch, 332 now** — and 420 is close to its floor. The line count was always a
proxy for "bespoke CSS that duplicates the design system", and ~1,000 of the
original lines were never that. See "Was the metric wrong?" below.

### Where the work is (updated 2026-08-21, after PR 17e)

The decision that opened this branch — *finish PR 8 and PR 9 before anything
else*, taken 2026-08-15 — is **spent**. PRs 8 through 14d have landed: 8 (+ its
audit, which found seven live class collisions and
[#544](https://github.com/mtaanquist/ALDevToolbox/issues/544), the type scale
rendering at 87.5% app-wide), 9a/9b, 10, 11, 12, 13, and 14a/14b/14c/14d.

`ReleasesBrowser` turned out to be **already ported** — its one "stale ref" is
the progress script reading the C# local in
`class="pill-tab @(active ? ...)"` as a class name. Expect a couple more of
those in the counts; a bare identifier inside a Razor expression is
indistinguishable from a class to a regex.

PR 14d turned out to be two pages, not one: `OeCompareFile` and the standalone
`/compare` tool shared `.oe-compare-file__panes`, so porting either alone would
have stranded the class for a second caller. Both are on archetype 11 now and
`Compare.razor.css` is gone.

**PR 15 finished the Pipelines / Projects gap and PR 16 finished the compare
screens, so no whole *tool* is left.** What remains is the residue, and PR 17 —
the final milestone — is about getting `tools.css` to zero. 17a swept the third
of the legacy layer that had no caller at all; see "PR 17" below for the
measurement and the remaining five slices.

What is left, by weight — re-measure with `.design/progress.py`, these are from
2026-08-21, after 17a:

- **Nothing.** `.design/progress.py` reports 5 stale refs and all five are
  correctly-placed app-level names: `dismiss` / `reload` inside
  `#blazor-error-ui`, and `brand__link`, which `app.css` names only to exempt
  the sidebar brand from the global link colour.
- **~2,600 lines of scoped `.razor.css`** the count still cannot see
  (`Translator.razor.css` alone is 403). Slice 17f, and it is a *review* rather
  than a port: most of it is legitimate component-scoped styling that should
  stay. Measure before planning.

~~**Decided after 14d: the compare tool's two remaining gaps wait for
Pipelines.**~~ **Overtaken — both shipped 2026-08-21.**
[#576](https://github.com/mtaanquist/ALDevToolbox/issues/576) (inline / unified
layout) and [#579](https://github.com/mtaanquist/ALDevToolbox/issues/579) (hunk
headers and collapsed context) are **closed**, built together and inline first
exactly as the write-ups proposed: PR 16a reworked the geometry, 16b brought the
inline layout and the hunks that come free with it, 16c collapsed the
side-by-side panes, and 16d acted on the design review of all three. What is
still open from that cluster is
[#581](https://github.com/mtaanquist/ALDevToolbox/issues/581) (the standalone
Compare *tool*'s layout tabs, register row 65) and #579a, the sticky
enclosing-declaration line in `.cmp__phead` — the data is already on the pane
(`data-procedures`), so it can ride along with any nearby PR.

~~Pipelines is now the only thing standing between this branch and merging.~~
**Overstated.** Pipelines is the last big *migration* slice, but the 35 open
`redesign` issues stand alongside it — see the next block. They are the debt this
branch deliberately took on: every judgment call, deferred divergence and
fresh-eyes finding got filed instead of half-built, which was the right trade
while porting and is now a backlog to triage rather than a list to burn down
unread.

### The `redesign` backlog (35 open, 2026-08-21)

To be talked through with the maintainer before Pipelines starts, and re-listed
with `gh issue list --label redesign --state open`. Two closed on 2026-08-21
after checking against shipped work — #570 (vendored) and #566 (the viewer's
footer hints) — so assume some of the rest are stale too and verify before
discussing.

- **Layer housekeeping, blocked on a retirement** — #525 (where link colour
  lives once `base.css` goes), #526 (delete the `--blue*` aliases), #565 (two
  `.tok-*` palettes sharing a prefix), #562 (retire `SourceFileViewerLegacy`),
  #580 (`.page-head--sticky` is in the vocabulary with no rule anywhere).
- **Component migration, the #529 family** — #527 (`BuildStatusPill` /
  `DeliveryStatusPill` onto `.status-pill`), #528 (the div-based run and
  delivery histories onto `.run-list`), #529 (the remaining families), #537
  (component-layer class collisions). **These overlap Pipelines** — #527, #528
  and #529 are the shared-component swaps that slice depends on, so they get
  answered by doing it, not before it.
- **Design-system decisions only the maintainer can make** — #523 (the
  `.btn--loading` divergence), #524 (does the no-pill row rule apply to every
  `data-table`), #532 (status vocabulary on the remaining admin tables), #549
  (two archetypes ported into CSS and used by nothing), #531 (screenshot-diff as
  a standing practice), #530 (Selawik's Latin-only coverage vs the Translator).
- **Fresh-eyes UX findings** — #539, #540, #546, #554, #555, #558, #560, #561,
  #564, #567, #568, #575, #577, and the compare cluster #581-#585. Each names a
  specific reader failing at a specific task; none is a migration blocker.
- **Responsive** — #569 (the OE file tree vanishes below 1100px with no way
  back), #574 (three admin list pages scroll horizontally below 1100px).

~~The prerequisite worth knowing about before either~~ **— done, PR 16a.** The
compare panes used to compute their geometry by hand in three places that had to
agree, all doing `visual = (line - 1) + fillers above`. They now ask the view
(`lineTop` / `lineAtTop`, over `view.lineBlockAt`), which is what made the
folding in 16c possible. Fixing it also turned up three bugs that had shipped
unnoticed — 14px alignment gaps from an unmeasured height oracle, scroll sync
losing its sub-line fraction, and an off-by-one at exact block boundaries. None
were visible in a screenshot; all three needed the two panes' numbers compared.
*The bugs you cannot see are the ones you have to measure.*

### The agreed sequence out of the audit (2026-08-16)

Walked through with the maintainer and approved. Steps 1 and 2 are done;
**PR 13 is done. It brought `pages-power.css` down whole, so archetypes 9, 10
and 11 are all in the app now and PR 14 inherits a sheet rather than pulling
one. What is left is PR 14 (Object Explorer, which is sections 2 and 3 of that
same sheet) and the Pipelines gap — between them, 87% of the remaining refs.**

1. ~~Short sweep — #545 TemplateDetail, #550 ghost-row chrome.~~ Done
   (`8fc02ed`). ~~#542 collision-test blind spot, #543 Add a user.~~ Done
   (`913230c`).
2. ~~**PR 9a — settings / site-admin *and* the existing
   `/admin/administration/*` pages onto the settings archetype.**~~ **Done**
   (`f56bf2a`, `00d6721`, `6250960`, `5a0d177`, `052349e`). Widening it was
   right: Administration turned out to be the same archetype and the two
   families now share `SettingsPage`. That bucket went from 16 files / 107
   stale refs to 3 files / 3.

   What it closed: the settings half of
   [#549](https://github.com/mtaanquist/ALDevToolbox/issues/549) — `.setting*`,
   `.switch` and `.header-tabs` are used now, by both families. `.edit-col` is
   still unused; it belongs to the admin *edit form* archetype, not this one,
   so it stays on the list.

   What the port turned up, none of which a screenshot could have shown:
   - **No settings tab could be saved on a fresh install.** Two columns were
     added by migration with `defaultValue: 0` while their validation demanded
     1..365 and 1..3650, and `Base()` carries every stored field into every
     tab's save — so pressing Save on the SMTP tab was rejected for a field
     that tab does not show. Data migration `20260807000000`.
   - **STARTTLS could not be switched off**, because an unticked checkbox posts
     nothing and `?? true` read the resulting null as on.
   - **`IsChecked` compared `StringValues.ToString()`** against `"true"`, which
     reads a checkbox paired with a hidden false (`"false,true"`) as unticked.
     Now one shared helper on `EndpointHelpers`.
   - **Status pills were sitting in table rows** on three site-admin pages,
     which the design system forbids; `RowStateIcon` needed `expired` and
     `disabled` arms, both of which had been falling through to "queued".
   - **"Delete client" wiped an assistant for every user in every
     organisation** from an unlabelled row button with no confirmation.
   - **Porting without retiring left the port inert**: `.job-list` moved to
     `pages-forms.css` but `base.css` loads after it and kept winning. This is
     the PR 8 collision class, reintroduced by the fix for it. Retire in the
     same commit as the port, and *measure* rather than assume.

   Not fixed here, filed instead:
   [#551](https://github.com/mtaanquist/ALDevToolbox/issues/551) — renaming the
   organisation then reloading Identity 500s on a `DbContext` concurrency
   error. Reproduced on the pre-PR page too, so it is not the port's doing.

3. ~~**PR 9b — the Account family.**~~ **Done.** 101 stale refs across 5 files
   went to 2, both of which are detector artefacts rather than real references
   (`class="@RowStateIcon.RowClass(state)"` and `class="pill-tab @(tab == ...)"`
   yield the bare tokens `state` and `tab`, which happen to be legacy class
   names). `auth.css` 244 -> 142, `tools.css` 3,642 -> 3,609.

   Five things it turned up, none of which the build or a read-through caught:
   - **`<Icon Class="...">` would have thrown at runtime.** The component's
     parameter is `Css`; an unknown component parameter is not a compile error
     in Blazor, it is an `InvalidOperationException` when the component renders.
     The build was green and the AI assistants tab would have 500'd.
   - **All four AccountSecurity page heads laid out in a *row*.** `.page-head`
     is `display: flex; justify-content: space-between`, and the shared page
     components wrap crumbs/title/sub in a bare `<div>` so they are one flex
     child. Hand-rolling a `.page-head` without that wrapper put the crumbs,
     the `<h1>` and the description side by side across the full width. Green
     build, no console error, and obvious the moment it is on screen.
   - **A repository provider with no token drew a *pencil*.** Mapping it to
     `draft` gave it the draft glyph and an amber keyline - "someone is editing
     this" - while its `aria-label` said "Not connected". `RowStateIcon` gained
     `connected` / `not-connected` arms so the class, glyph and label agree by
     construction rather than by an override, and
     `ALDevToolbox.Tests/Components/RowStateIconTests.cs` now pins every mapped
     state. This is the third instance of the same defect (`expired` and
     `disabled` in 9a), which is why it got a test rather than another arm.
   - **The design system has no vertical settings nav.** Archetype 7 is
     "Settings + sub-nav" and its sub-nav is `.header-tabs`; the component
     inventory in `DESIGN-SYSTEM.md` lists no left rail at all. So `.set-nav`
     had nothing to port *onto* - this was an archetype change, not a class
     swap. The tabs are now real links carrying `?section=`, which the page
     already understood, so every redirect that lands on a section still works
     and a tab is bookmarkable for the first time.
   - **`.set-*` / `.sn-*` / `.cap-label` / `.audit-row` could not be retired**
     even though the Account family stopped using them: `ProjectDetail.razor`
     and `ReleasePipelineDetail.razor` still do. They belong to the Pipelines
     gap and go when it does.

   Divergences, both recorded in the page's own header comment:
   - **No "Danger zone" tab.** Deleting your account is one destructive
     setting, and archetype 7 ships `.setting--danger` + `.setting__lock` for
     exactly that (its own worked example is "Maintenance mode"). It is the
     last row of Profile. `?section=danger` no longer parses and falls through
     to Profile, which is where the row lives, so old links still land right.
   - **No counts or 2FA dot on the tab row.** The rail carried `.sn-badge`
     counts and an on/off `.sn-dot`; `.header-tab` has no slot for either, and
     `.pill-tab__count` is the pill variant's, not this one's. The Security tab
     shows the same On/Off as a `.status-pill` in its own card head. If the
     at-a-glance dot is missed, adding `.header-tab__count` upstream to mirror
     `.pill-tab__count` is the faithful way to get it back - not a local hack.

   Verified by driving it, not by reading it: all four tabs and all four
   security pages in both themes, plus the create-token flow end to end
   (form -> one-shot token page -> client-snippet tabs -> the new row in the
   table). Screenshots in `scratch/pr9b/`.

   Also fixed while in there, because the port put them under the nose:
   - **Disconnecting an AI assistant had no confirmation** - a single click on
     an unlabelled icon button revoked it. It now goes through `ConfirmDialog`
     like every other destructive action on the page.
   - **The copy-snippet button kept saying "Copied" after switching client
     tabs**, about a snippet the user had not copied.
   - **The OAuth consent screen listed raw scope names** (`mcp`,
     `offline_access`) as the permission titles. They are protocol identifiers;
     the screen now uses the same words `/account` does.

   What PR 9a left on the doorstep, for the record:
   `Account.razor` has the *same* access-token table that `SiteAdminAccessTokens`
   just had, pills-in-rows and all, with the same Revoked/Expired mapping —
   `RowStateIcon` now has the `expired` arm it needs, so this is a lift of the
   pattern rather than a design decision. And `Account.razor` is the single
   heaviest file left outside the Pipelines gap (71 stale refs).

   A global sweep for pills-in-table-rows found only one other, outside both
   PR 9a and 9b: `ReleasePipelinesBrowser.razor:137` renders `EnvironmentType`
   (Production / Sandbox) as a `.status-pill`. That is an *attribute* of the
   row rather than its lifecycle state, so by the reasoning already written
   into `AdminTemplateList.razor` it wants a `.tag` — but it sits in the
   Pipelines gap, so it is noted here rather than changed.
4. ~~**[#546](https://github.com/mtaanquist/ALDevToolbox/issues/546) — inline
   generator validation.**~~ **Done.** Built as planned: `GenerationService`
   gained `ValidateWorkspaceAsync` / `ValidateExtensionAsync`, both generator
   pages cancel their own submit, validate, render `FieldError` inline, and
   hand a clean plan to `generate.js` to post natively. No stash-and-redirect.

   The rules could not be re-implemented in the page, so they are not: a
   private `PrepareWorkspaceAsync` now carries the whole pre-ZIP prefix and
   *both* `GenerateWorkspaceAsync` and `ValidateWorkspaceAsync` call it. That
   matters more than it looks — the id-range overlap check needs the template
   loaded and the module clones resolved, so a page-side reimplementation
   would have been wrong within a release. `ValidateOnlyTests` asserts parity
   in both directions on every rule rather than trusting the shared call.

   Two things fixed on the way:
   - **`ResolveVersionAsync` still wrote a plain-text 400** — the last one on
     the generation path, for "Latest" with an empty version catalogue. It was
     the case the issue's own code sample did not cover. Now the styled page,
     and caught inline before that.
   - **"Publisher: Required." pointed at a field that does not exist.** The
     publisher comes from the org's defaults, never the form, so the message
     now says where to go and set it.

   The JS handoff is the fragile part and has no visible failure mode: the
   form must keep the id `generate.js` looks up and must *not* carry
   `data-loading-form`, whose listener would start the spinner on a submit the
   page is about to cancel and never clear it. Both pinned by a bUnit test,
   self-tested by re-adding the attribute.

   Verified by driving both generators: a real ZIP download (and the
   post-download hop to `/docs/extensions-whats-next`, which the handoff had
   to preserve), a server-only rule showing inline with the form intact,
   recovery on retry, Enter-to-submit, and the browser's own `pattern` check
   still short-circuiting before any round-trip.
5. ~~**PR 10 — dashboard cues**, the other half of #549.~~ **Done.** Closes the
   dashboard half of [#549](https://github.com/mtaanquist/ALDevToolbox/issues/549):
   `.cue` / `.cue-grid` / `.cue--attention`, `.dash-cols`, `.activity` /
   `.activity--edge` and `.tool-tile__meta` are all in use. `.dash-grid` /
   `.dash-tile` are still ported-and-unused — they are the *quiet* count tile
   the hand-off reserves for a panel where "a wall of filled colour would be too
   loud", and no surface we have wants that yet. They join `.edit-col` on the
   list.

   **The `/` vs `/admin` decision the note called for: the hand-off already
   answers it.** They are different archetypes — `PageLauncher.dc.html` is
   archetype 1 and `PageDashboard.dc.html` is archetype 4 — so Home stays a
   launcher (tiles, section groups, meta lines) and `/admin` becomes a
   dashboard (cues, attention, recent activity). Both prototypes were pulled
   from the design project for this PR; neither was checked in here before.

   **`/admin` lost its ten `.tool-tile` navigation cards.** Every one of those
   destinations is a `NavMenu` entry, so the page was a second copy of the
   sidebar; the hand-off's dashboard has no tiles, and dropping them is what
   makes room for the page to answer *"is anything wrong?"* instead of *"where
   do I click?"*.

   **Scoped on attention, as instructed.** The four rows in "Needs attention"
   are all things only an admin can clear: people waiting for an account,
   recipe suggestions to review, invitations that ran out unaccepted, and
   release imports that failed. Failed *builds* are deliberately excluded —
   they belong to whoever ran them and already show a status in Pipelines. The
   cue row leads with the two attention counts and fills out with content
   counts, trimmed to six because `.cue-grid` is six columns at full width.

   What the port turned up — none of it visible in the markup or in a
   screenshot of the populated state:

   - **Home was missing four of the ten tools.** Projects, Pipelines, Releases
     and Translator were in the sidebar and had never been on the launcher, so
     the entire deliver half of the product was invisible to anyone starting
     from the front page. Now pinned by a test that compares the tile list
     against the full set.
   - **Two signed-out tiles led somewhere else.** Workspace pointed at
     `/login?returnUrl=/projects/new` — the Projects tool's create-a-customer
     page — and Extension at `/projects/extension`, which is not a route at
     all, so signing in from it produced a 404. Both now point at the real
     generators.
   - **Every sign-in wrote an audit row.** `users.last_login_at` is stamped on
     each successful login, `User` is an audited entity, and the interceptor
     runs before the auth cookie exists — so the rows read `unknown changed
     User #1` and, on a six-row dashboard feed, crowded out every real change.
     `.design/auth-and-audit.md` *already* said logins live in `login_attempts`
     and not the audit log, so this was a bug against the spec rather than a
     scope decision. `AuditInterceptor` now skips a `User` save whose only
     modified column is that one — the same column-scoped shape the `Project`
     entity already used. Narrow on purpose: a save that also changes the
     account is audited in full.
   - **Every audit avatar rendered two characters of garbage.** `Avatar.Initials`
     splits on `@` first, so `"Mads Taanquist <admin@cronus.example>"` became
     `"Mads Taanquist <admin"` and the last word's initial was `<`. It read
     `M<` on both audit pages and had done since the shell port.
   - **Ten audited entity types reached the reader as their own identifier.**
     `FriendlyAuditType` falls through to `ToString()`, so the audit log said
     `ApplicationVersion` and `PersonalAccessToken`. Filled in, and
     `AuditDisplayTests` now fails the build if any type's label contains a
     camel-case seam — the fallback is fine for single-word names and a trap
     for everything else.

   **The fresh-eyes review found the one thing the screenshots could not**, and
   it is worth naming because it is a repeatable blind spot: every screenshot
   taken before the review was of the *populated* state. A brand-new
   organisation got six cues reading `0` over two "nothing" panels — accurate,
   and no use to the person it is for. `/admin` now has a first-run branch with
   one card and one action ("Import starter content", or "Add a template" for
   the system org, which is the source and has nothing to import from). The
   empty tile metas on Home name a next step for the same reason.

   Kept against the review, with reasons: **"Activity indicators"** stays — it
   is the hand-off's own label *and* Business Central's own term for cue tiles,
   so it is domain vocabulary for this audience rather than dashboard-builder
   vocabulary. **The attention cues restating the attention rows** stays — the
   hand-off does exactly this (a "Failed runs" cue above a failed-release row),
   and the cue carries the number where the row carries the who and the what.
   **"Invite user" as the primary** stays for the populated state: it is the
   hand-off's choice, and it is the only header action, because the hand-off's
   *secondary* — "Export audit log" — has no feature behind it here. (An earlier
   draft of this entry said "Export configuration" was dropped from the header.
   That was wrong and is corrected: the pre-PR page had no header button at all;
   the configuration export was a tile blurb. The button existed only inside
   this PR, between the first draft and the design review.) Three judgment calls
   went to issues instead:
   [#554](https://github.com/mtaanquist/ALDevToolbox/issues/554) (audit rows
   name a row id, not the thing that changed),
   [#555](https://github.com/mtaanquist/ALDevToolbox/issues/555) (locked tiles
   look switched off), [#556](https://github.com/mtaanquist/ALDevToolbox/issues/556)
   (MCP named by its protocol acronym everywhere except Account), plus
   [#557](https://github.com/mtaanquist/ALDevToolbox/issues/557) from the
   second round — `ComponentCollisionTests` guards `components.css` only, so
   `pages.css` and `pages-forms.css`, which own most of what this migration
   ports, are outside the one test written to catch exactly this migration's
   recurring bug.

   Verified by driving both pages against a seeded database *and* against an
   empty one — the second run is the one that mattered.

   **Reviewed again after it landed**, by three agents with separate lenses
   (correctness, hand-off fidelity, test quality). That pass found two live data
   bugs a screenshot could never show, both from the same wrong assumption:

   - **`oe_releases` is not only imports.** `ProjectBuildImporter` stamps a row
     with `Kind = "project"` for every pipeline build, through the same queue and
     the same kind-agnostic failure path. So the "Needs attention" row fired on
     failed *builds* — precisely the scope call this PR says it makes — and would
     have been permanently red on any org that builds. The Home tile's "N BC
     releases" counted them too, drifting further from what `/object-explorer`
     lists with every build. `ObjectExplorerService` already draws this line;
     `DashboardService` now draws it in the same place and says why.
   - **Two counts measured a different page than the one they linked to.** The
     Templates tile hid deprecated templates while `/templates` lists them with
     a badge, so an org whose only templates were deprecated read "None yet" over
     a link to three of them. Each count now matches its page, which is the only
     thing that makes the number checkable.

   Also fixed from that pass: the attention rows carried state as colour and an
   unnamed glyph (**non-negotiable 4**) — the word is now on the glyph, not the
   row, because our row is a link whose accessible name `aria-label` would
   replace; a count→max race that could 500 the page if the last row went away
   between two round-trips; a `PendingQueue.OldestAt` field that held the
   *newest* failure; a first-run branch that told a single-tenant operator about
   "every other organisation"; an unreachable loading branch; and a doc-comment
   left orphaned by the interceptor edit.

   The test pass was the sharpest of the three. The audit gate — a change that
   *suppresses* rows — had two tests that were white-box restatements of its own
   body: add `Status` or `IsSiteAdmin` to `UserSignInColumns` and account
   disablement or privilege escalation would vanish from the audit log with
   nothing failing. There is now a theory over six security columns, plus a test
   that drives a real sign-in through `AuthService` rather than setting the
   column by hand. Three other tests turned out not to be load-bearing: the
   launcher's tile list was a hand-written literal (it is now derived from
   `ToolCatalog.All`, so tool eleven cannot ship half-linked), the empty-meta
   test passed against the field initialiser without the query ever running, and
   `EndpointHelpers.ReadDisabledTools` — the only path by which an org's
   switched-off tool disappears from the front page — had no coverage at all.

6. **PR 11 — the auth family.** **Done.** Eight pages onto the auth-card
   archetype (`.auth*`, ported into `pages-forms.css` months ago and used by
   nothing until now), plus a shared `AuthCard` and the shell-less `AuthLayout`.
   `auth.css` 142 → 92 lines; the sign-in tab bar and the `.login-form*` /
   `.login-passkey` rules retired with their only callers. `.login-page`
   survives on purpose — `Error.razor` and `NotFound.razor` still render it and
   they are PR 12's files.

   **The load-bearing discovery is a selector, not a rule.** The auth archetype
   is the one the catalogue marks "no shell", so its pages have no `.page`
   ancestor — and the bridge that puts the design system's `.field` back after
   `tools.css` overrides it was written as `.page .field`. Ported as-is, every
   auth label would have rendered UPPERCASE at 12.5px with a 16px bottom margin
   fighting `.auth__fields`' own gap. `.auth` now joins `.page` on both bridge
   entries. This is the PR 8 collision class once more, and the first time where
   the bridge's *reach* rather than its existence was what made the port land.

   Three structural changes beyond the CSS, each because the old shape was a
   mechanic being explained:
   - **The Password / Magic link tab bar is gone.** The hand-off's card has one
     primary and an `.auth__sso` block for everything else, which is the honest
     shape: password is what almost everyone does. Magic link and passkey sit in
     that block together.
   - **`/login/challenge`'s three `<details>` accordions became a `.pill-tabs`
     strip over one form.** Every method used to be on screen at once behind
     disclosure triangles, with a query parameter deciding which opened.
   - **Signup's `<details>`"Already have a code?" became a card.** Having just
     been told a code was coming, the reader had to notice a triangle to find
     where to type it; now the confirmation *is* where you type it.

7. **PR 12 — docs, MCP and the error pages.** **Done.** Five files onto
   archetypes 12 (docs / long-form), 13 (setup steps) and 14 (404 / 500), plus
   `pages-content.css` pulled from the design project. The bucket went 5 files /
   17 stale refs to zero, and **`auth.css` is deleted** — the first legacy sheet
   to reach zero. `Mcp.razor.css` went 190 lines → 33 and `McpDocs.razor.css`
   → 14.

   **The port found more wrong with the *content* than with the CSS.**

   - **Both tool tables were lying.** `/tools/mcp` listed 13 tools and
     `/docs/mcp` 15, against **38** actually registered — and two of the names
     on both pages, `search_snippets` and `get_snippet`, were renamed with the
     Cookbook and no longer exist, so the docs were telling people to ask for a
     tool the server refuses. Both now render `Domain/Tools/McpToolCatalog.cs`,
     and `McpToolCatalogTests` reflects over the same `[McpServerTool]`
     attributes `WithToolsFromAssembly()` discovers: it fails if the two sets
     differ, or if the page and the attribute disagree about which tools write.
     Same shape as PR 10's fix for the launcher's hand-written tile list.
   - **`Mcp.razor.css` had been painting with three dead tokens.**
     `.mcp-step__number` and `.mcp-tab--active` used `var(--blue)` /
     `var(--blue-50)` and `.mcp-snippet` used `var(--mono)`; none of the three
     survived into `tokens.css`, so the step numbers had no fill and the config
     snippet was rendering in the body font. Nobody noticed because nothing
     errors when a custom property is undefined.
   - **`McpDocs.razor.css` was the type-scale bug fossilised.** Every length in
     it — `14.7px`, `11.9px`, `17.5px`, `9.1px` — is some round number times
     0.875, left behind by the #544 fix. Deleted with the file.
   - **Nine `<details>` accordions, one per assistant.** Replaced first with
     `.pill-tabs` (PR 11's move) — which was **wrong, and only looking at it
     showed why**: `.pill-tab` is a fixed-height segmented control, and nine
     labels like "Claude on the web" overflowed the strip into a stack of
     clipped half-lines. The hand-off's own "Connect an agent" screen uses a
     `<select>` for exactly this, so that is what it is now, in a GET form so
     the page stays static SSR and anonymous.

   **Three things in the design layer itself, all fixed upstream:**

   - **Two consecutive `<p>` in `.prose` had no gap at all.** `.prose p {
     margin: 0 }` is (0,1,1) and beats the (0,1,0) `.prose > * + *` stack rule
     whatever the order, so the reset cancelled the rhythm it was supposed to
     enable. Same for a list following a paragraph. Now reset once on
     `.prose > *` and stack after. Nothing on the review sheets caught it
     because every screen there alternates prose with a callout or a code block
     and never puts two paragraphs in a row.
   - **`.docs__main` was a bare block.** A docs page cannot keep a *control*
     inside `.prose` — `.prose a` is (0,1,1) and beats any component's own class
     — so the client picker has to be a sibling of the article, and there was
     nothing giving the pieces a rhythm. Now a grid.
   - **`.docs__toc` inherited divergence 7.** `var(--sticky-head, 132px)` parks
     a sticky element 132px down at scrollTop 0; `.gen` and `.settings` already
     carry `--sticky-head: 0px` for this and `.docs` now does too.

   **The load-bearing collision this time was in `base.css`, again.**
   `a:hover { text-decoration: underline }` is (0,1,1) and beats `.toc-link` /
   `.errlink`'s own (0,1,0) `text-decoration: none`, so every contents entry and
   every error-page link would have underlined on hover. Both joined the
   anchor-underline bridge list. That is the PR 8 collision class for the fourth
   PR running.

   **It also closed [#557](https://github.com/mtaanquist/ALDevToolbox/issues/557).**
   `ComponentCollisionTests` guarded `components.css` only, and the migration had
   long since moved on to porting pages onto `pages.css` / `pages-forms.css`. It
   was hiding a live collision: `.audit` is the audit-history panel in
   `pages-forms.css` and an unrelated key/value list in `tools.css` — the case
   the test's own doc-comment names — and no test could see it because the
   design-side declaration had moved out of the sheet being watched. It is
   silenced by a bridge entry the PR 8 audit added, and removing that bridge now
   fails the test, which it could not do before.

   **Two omissions, both deliberate, both flagged rather than silent:**

   - **The 404 has no `.errpage__links` block.** The hand-off ends its 404 with
     "Or pick up where you left off" and three links back into the app. For a
     signed-in user our sidebar already *is* that block, with their real tools
     in it rather than three invented ones, and we have no "recent items" to
     put there. What the page does gain, which it never had, is the address that
     failed.
   - **The `.docs__toc` has no `is-active` state.** Scroll-spy needs JS, and a
     contents list without a current-position marker still navigates. Filed
     rather than bodged.

   **Then a three-page fresh-eyes review, and it was worth the round trip.** One
   `design-review` pass per surface, each given the rendered screenshots as well
   as the markup. Two blockers, both confirmed by driving the app rather than
   taken on trust:

   - **Microsoft 365 Copilot was told to "set the server URL" and never shown
     it.** Its guide record carries no snippet, so the code block never rendered,
     and the address appeared exactly once on that page - in the troubleshooting
     section, several screens below. Every screenshot taken of that branch had
     cropped one line above the defect. `ContentPageTests` now asserts the
     address appears *inside the steps* for all nine assistants, so the next
     client added with `Snippet: null` cannot repeat it.
   - **`${TOKEN}` was an active trap, not just a placeholder.** Inside a real
     `.vscode/mcp.json`, `${...}` is live VS Code variable syntax, so a reader
     can reasonably paste it untouched and expect the editor to fill it in. It is
     `PASTE-YOUR-TOKEN-HERE` now, every snippet carries a caption saying to swap
     it, and it is the first cause named under "401 Unauthorized".

   The structural finding was **step 1 of `/tools/mcp` presenting a fork that
   only went one way**: both cards linked to the same URL, and steps 2 and 3
   served the token path only. A Claude-web reader picked "permission screen",
   landed where everyone else landed, then met "pick your assistant" listing four
   desktop clients and an instruction to paste a token they were never given. The
   connector card is terminal now - it carries the address, and says steps 2 and
   3 are not theirs.

   Three defects the review found that the port had introduced and nobody had
   driven:

   - **The Copy button never reverted**, because `_copied` was only cleared by
     switching tabs. Fixing that surfaced a second one: Blazor re-renders when a
     handler hits its *first* await - the JS interop - by which point the flag is
     still false, so the label went straight from Copy to Copy 1.2s apart and the
     "Copied" state never rendered at all. Needs an explicit `StateHasChanged()`.
   - **A troubleshooting note nobody could follow**, written in this PR: "reload
     this page - if it tells you assistants are off, that's the answer." The
     route gate 404s a disabled tool first, so the reader gets a second 404. The
     same file's header comment says exactly that, two screens up.
   - **The token round-trip destroyed the token.** It is shown once, so it lives
     in the clipboard - and pressing Copy on the snippet overwrote it. Step 2 now
     takes the token in a field and fills the snippet, so Copy hands over
     something that works.

   Two claims measured and **rejected**:

   - Spaced hyphens were called a house-style violation. Counted: pre-PR-12
     `Home.razor` runs 9 spaced hyphens to 1 em-dash, `Account.razor` 8 to 4. The
     spaced hyphen *is* the pattern; CLAUDE.md permits the em-dash, it does not
     mandate it.
   - `.errpage` centring was reported inconsistent between the 404 and the 500.
     It was not - both were top-anchored, and the "centred" 500 was an element
     screenshot of mine with the shell cropped off. The real bug underneath it
     was worth fixing: `min-height: 100%` resolves to nothing on a grid item in
     an auto row, and giving the parent a `min-height` does not help either,
     because min-height does not make a height definite. Measured at 414px of
     content in a 936px column, before and after. `.errpage` is `60svh` now.

   The fresh-eyes review found three blockers, all of them things the port
   carried forward rather than introduced, and all of them only visible in a
   branch nobody screenshots:
   - **A raw `JSException` on the sign-in card.** `_passkeyError = ex.Message`
     put a WebAuthn constant and a .NET type name on the first screen a new user
     ever sees. Nobody wrote that string as copy, so nobody reviewed it as copy.
   - **Query-string keys bolded as labels.** Four pages rendered
     `<strong>@Field:</strong>`, so an email-link recipient could be shown
     `OrganizationSlug:` in a red box. One shared `AuthCard.FieldLabel` now maps
     the known keys and prints *nothing* for the rest.
   - **A hard lock-out on `/login/challenge`.** An account whose only second
     factor is email, on a site that cannot send email, got a single method with
     no tab strip, an error telling it to use an authenticator app it does not
     have, and an exit that looped straight back. The message now branches on
     what the account actually has.

   Also from that pass: two cards showed a "we can't send email" alert above a
   live send button (the form is gone in that state now, not disabled); the
   email-code tab led with a primary you could not use until you pressed the
   secondary below the divider; the expired-challenge card kept the title
   "Verify it's you" over a message saying you no longer can; the password rule
   was worded two ways across four pages and "admin" four ways across five; and
   `/accept-invite` sent people with no account to a sign-in form.

   **One review finding was wrong, and worth recording as such.** It read the
   shell-less layout as trapping signed-out visitors in light mode, since the
   theme switch lives in the top bar. Measured instead of accepted: with no
   cookie the server renders `<html lang="en">` with no `data-theme` at all, and
   `tokens.css`'s `prefers-color-scheme` block takes over — a dark-OS visitor
   gets `rgb(13, 17, 22)`. The screenshots looked light because the harness sets
   `aldt-theme` explicitly. Nothing to fix.

   Tests: `Routing/AuthFormActionTests` walks the real endpoint map and asserts
   every action an auth page posts to is mapped for POST, and that all eight
   still declare the shell-less layout — a mistyped `action` survives every
   other check in the suite, because the page still renders and the only symptom
   is that signing in silently does nothing. `Components/AuthCardTests` pins the
   state machines: Signup's four cards, AcceptInvite's three, SignupDetails'
   two (the one page that cannot be driven without forging a Data-Protection
   cookie), and a theory over `/login/challenge` proving it never renders a
   method the account does not have. Self-tested by reverting three fixes in
   turn; three mutations, three failures, each the intended test.

   Verified by driving every state that can be reached: a real sign-in through
   to a real MFA challenge on all three methods, a real invite token, the
   multi-tenant no-SMTP signup (the tallest card in the family), and the
   confirmation states. **PR #553 is unmerged and was deliberately not waited
   for** — see below; the reconciliation notes are in `Login.razor`'s header.

8. **PR 13 — the Translator onto the power-tool archetype.** **Done.**
   `pages-power.css` came down whole (303 lines, archetypes 9, 10 and 11), so
   PR 14 inherits the sheet rather than pulling one. `Translator.razor.css` is
   395 lines → 403 — **the line count is the wrong measure here and worth saying
   so.** What changed is what the lines are made of: 251 hard-coded pixel values
   → 65, four raw hexes → zero, and the local four-colour state ramp → gone. The
   file grew because acting on the review added rules to it; it is on the tokens
   now, which is the thing the count cannot see.

   **The count said 3 stale refs. It was a whole archetype away.** See the
   blind-spot section: the page declared `--st-todo` / `--st-review` /
   `--st-trans` / `--st-final` on its own root, which *shadowed the design
   system's identically-named tokens* for everything inside it — so the states
   in this tool were a private palette wearing the system's names. The states
   are `tokens.css`'s now, and the row keyline, the status glyph, the tab badges
   and the progress ribbon all resolve from the same four `--bar-*` / `--st-*`
   pairs.

   **`RowStateIcon` already knew this tool.** Its XLIFF family
   (`untranslated` / `fuzzy` / `translated` / `final`) maps to exactly the
   `is-*` classes `.trow` colours in the power sheet — the design system had
   anticipated the Translator and the page had never used it. That is the whole
   join now: one `DesignState(u)` picks the row class, the glyph, the badge and
   the filter.

   **The frame is the point.** `.app__content:has(.pw)` opts the page out of the
   shell's padded, page-scrolling column, so the toolbar, the grid header and
   the pane heads stay put while 436 rows go past. Only the loaded-file view
   wears `.pw`; the first-run screen stays an ordinary `.page`, because that
   rule strips the padding for the whole content column and a drop target
   floating in an unpadded viewport is not what anyone wants to land on.

   Two things the port found in the app rather than the CSS:

   - **The file input had never been hidden.** `.tr-drop__input` was a scoped
     rule against markup `<InputFile>` renders, so it compiled to
     `.tr-drop__input[b-thispage]` and matched nothing — Firefox and Safari
     users have had the browser's own "Choose File / No file chosen" chrome in
     the middle of the drop panel the whole time. Only visible because the
     screenshot harness deletes `showOpenFilePicker` to reach that branch.
     `::deep` fixes it, and a test now walks every class the razor hands to a
     child component and fails on a scoped rule that cannot reach it.
   - **`Virtualize` survives the open row.** The handoff declares
     `.trow--editing` at exactly `--row-h * 3` so a windowed list can still
     compute offsets; measured at 84px, and scrolling 4,000px with a row open
     renders 53 rows and drifts by the 56px difference, which is invisible.

   **Then the fresh-eyes review, and its first finding was a counting bug older
   than this PR.** The "Needs translation" tab showed **88** and filtered to
   **175 rows**: the badge came from `Counts()`, which excluded units needing
   review, and the filter came from `IsNeeding`, which included them. Two
   derivations of "which bucket is this unit in", quietly disagreeing since the
   tool shipped. There are four tabs now — Untranslated, Needs review,
   Translated, All — every badge is the number of rows its own tab produces, and
   both sides call `DesignState`. Verified by driving all four (88/88, 87/87,
   261/261, 436/436) and self-tested by putting the second predicate back.

   The other two blockers:

   - **The fourth state had no name anywhere the user could reach.** The picker
     legends the states, and it offered three of four — a unit could arrive
     needing review and be moved out of that state but never into it, while the
     `(!)` glyph sat on half the rows unexplained. Four buttons now, spelled the
     way `RowStateIcon` spells them.
   - **Nothing said there were unsaved edits.** `_unsaved` drove a leave-confirm
     modal and nothing else, so the first mention of a pending draft was a modal
     that fires when it is already too late to act on. A dirty dot beside the
     filename (the design system's `.ftab--dirty` idiom), `N unsaved` in the
     status bar, and a count on the Save/Export button.

   Also from that pass: **Pre-translate rewrote hundreds of rows with no confirm
   and no undo**, which CLAUDE.md's own "confirmation modals on destructive
   actions" rule already covered — it asks first now, quoting the count
   (`Fill 82 strings from memory?`). The developer note rendered BC's
   `(Namespace=…)(LookupHint=…)` wrapper raw, uncaptioned, in an unlabelled box
   between the source and the target. The suggestions panel used "memory" as a
   bare noun the page never defines, and "seed" as a verb. The status-bar hints
   read as lists of nouns, advertised `Apply suggestion` in the grid where there
   are no suggestions, and never mentioned Ctrl+S. The percentage — the one
   number anyone wants from a progress bar — existed only on the `aria-label`.

   **The layout change came out of the fix, not the other way round.** Four
   filter tabs would not fit a toolbar that was also carrying four file verbs, so
   the verbs moved up to `.pw__head` and the progress ribbon moved down to
   `.pw__foot`, where the four named counts it was duplicating are now the tabs'
   badges. Head: which file, which languages, what you can do to it. Bar: what
   you are looking at. Foot: where it stands and the keys that do the work.

   Two review claims **rejected**, both measured rather than argued: spaced
   hyphens as a house-style violation (counted in PR 12 — 9:1 in `Home.razor`;
   the spaced hyphen *is* the pattern), and renaming "units" to "strings"
   (`trans-unit` is the XLIFF term, the reviewer flagged it as unsure, and the
   word is used consistently across six surfaces).

   Two things the review could not check and this note records. It asked whether
   the row glyph carries a name: it does — `RowStateIcon` puts the state word on
   `title` and `aria-label`, which is the design system's stated contract for
   showing state without relying on colour. And it flagged, without being able
   to run it, that the rename pencil and the Save button said contradictory
   things about which file they act on. They did: `saveInPlace` writes to the
   handle the user picked, whose name on disk the rename never touches, so after
   a rename the toast named a file that does not exist. Both the tooltip and the
   toast are honest now, and a test pins the toast.

   `.trow__acts` is the one thing the port left empty on purpose —
   [#560](https://github.com/mtaanquist/ALDevToolbox/issues/560).

   Tests: `Assets/StylesheetLoadOrderTests` walks the `<link>` list in
   `App.razor` and fails if a sheet in `wwwroot/` is unlinked or linked out of
   order — load order *is* the migration mechanism and a misplaced sheet says
   nothing — and asserts each of the six shared sheets still matches its
   `.design/handoff/` copy byte for byte, so "patch the local copy and push it
   later" cannot happen quietly. `Components/TranslatorArchetypeTests` pins the
   joins that have no compiler behind them: the keyline rule for each of the four
   states, the glyph tint, the absence of a local state ramp, the `.pw` / `.page`
   split, the selectors the JS hunts for by string, the `::deep` rule above, and
   the two invariants the review found broken. Self-tested by reverting eleven
   fixes in turn; eleven mutations, eleven failures, each the intended test —
   and the first version of the `::deep` detector **passed on a broken sheet**,
   because the comment explaining `::deep` was being read as part of the selector
   that had lost it.

   Verified by driving the tool: all four filters, the rail resizer (drag,
   arrow keys, and persistence across a reload via the restore banner), the open
   grid row at its declared height, the pre-translate confirm against 30 seeded
   memory entries, and both themes.

### PR 14 — the Object Explorer: three decisions taken up front (2026-08-20)

Walked through with the maintainer before any code, because the handoff's
`PageObjectExplorer` screen is **a VS Code clone**, and how much of that to take
is not a styling question. It has a three-pane frame, an open-file tab strip
with close buttons and a dirty dot, a hover symbol card, a status bar reading
`Ln 16, Col 15 · AL · UTF-8 · Spaces: 4`, and the keybindings `F12`,
`Shift+F12`, `Ctrl+P`, `Alt+←`.

Parts of it are good and we already hold the data: the three-pane layout beats
our route-per-thing model, the `.okind` two-letter glyph (TE / PE / TB / CU)
scans faster than icons, the grouped find-references list is better than what we
have, and the symbol card is a good idea we can feed from the declarations and
doc comments the extractor already stores.

Parts of it are **false for this app rather than merely different**, and those
are the ones worth naming:

- **`.ftab--dirty`** is a dirty dot labelled "Unsaved changes" on a file tab.
  Our viewer is read-only, and the prototype's own toolbar badge says so
  ("Read-only - symbols come from the compiled .app"). The screen contradicts
  itself.
- **`Spaces: 4` and `UTF-8`** in the status bar are editor settings. Drawing
  them implies you can change them. (`Ln, Col` is fine — CodeMirror keeps a
  cursor in read-only mode and a line number is how you tell a colleague where
  to look.)
- **"Open in VS Code"** has no target. There is no file on the user's machine;
  the source came out of a compiled `.app` in our database.
- **`Symbol cache 4.1 GB - synced 07:14`** is telemetry we do not collect.
- **`Ctrl+P` is print and `F12` is DevTools.** Neither is reliably
  interceptable, and `Ctrl+P` is the headline gesture (quick-open). Advertising
  a keystroke the browser eats is worse than not having one.

**Decision 1 — panes, no tab strip.** The tree and inspector persist around the
code pane; one file open at a time; the routes and deep links are unchanged
(`SourceFileViewerLegacy` exists to keep those alive, so we clearly care).
`.ftabs` / `.ftab` join the ported-but-unused column of
[#549](https://github.com/mtaanquist/ALDevToolbox/issues/549) — a tab strip is a
session model, not CSS, and the dirty state it is built around cannot exist here.

**Decision 2 — web conventions for the keyboard, not VS Code's.** `/` or
`Ctrl+K` for go-to-object (GitHub's pattern, and free), `Shift+F12` for find
references (generally unbound in browsers), `Cmd/Ctrl-click` for go-to-definition
and `Ctrl+F` for find-in-file, both of which the viewer already does. Nothing
gets advertised in the status bar that the browser will swallow.

**Decision 3 — two PRs.** 14a is the code pane and the inspector; 14b is the
shell around them. 133 stale refs across 14 files with `OeReleaseDetail` at 966
lines is not one reviewable diff.

**And one thing that needed no discussion.** `.codev` is a hand-rendered
div-per-line grid with its own `.k` / `.t` / `.s` token classes. Our centre pane
is **CodeMirror 6**, which brings selection, find-in-file, virtualised scrolling
and the click-to-find plumbing in `CodeViewerCallbacks`. Taking `.codev`
literally would trade all of that for pixels. We theme CodeMirror from the
`--code-*` tokens instead and `.codev` stays unused.

Those tokens — `--code-key`, `--code-type`, `--code-str`, `--code-com`,
`--code-num`, `--code-obj`, `--code-bg` — have been in `tokens.css` in both
themes since the token layer landed and **have never been used**: every code
surface in the app renders with CodeMirror's stock `defaultHighlightStyle`. The
design system's AL palette has been sitting there unread.

### PR 14a — the code pane and the inspector (2026-08-20)

Landed. The first half of the split above: everything inside the file viewer,
none of the frame around it.

**The palette.** `alHighlightStyle` in `code-editor.js` is a
`HighlightStyle.define` that assigns its own class names (`tok-keyword`,
`tok-string`, …) instead of letting CodeMirror generate them, so the colours
live in CSS on the `--code-*` tokens and light / dark follow the token switch
with no re-mount. It replaces `defaultHighlightStyle` at all three mount sites,
and `@codemirror/theme-one-dark` is gone: the chrome (gutters, selection,
panels, tooltips, fold placeholders) is now a var-driven `EditorView.theme`
whose only per-theme difference is CodeMirror's own `dark` flag. Two palettes
that belonged to neither the app nor each other, replaced by one that is the
app's.

`@lezer/highlight@1.2.1` is pinned into the `?deps=` of every import that pulls
in `@codemirror/language`, for the same reason `@codemirror/state` already is:
`HighlightStyle` matches tags by object identity, so a second copy of the tag
table silently highlights nothing.

**The inspector.** The right rail is the handoff's `.pane` — a fixed
`.pane__head` over a scrolling `.pane__body`, content in `.pane__sec` blocks.
The outline is `.olist` / `.orow`, the references panel is `.refs` / `.refgrp` /
`.refhit`, and find-in-file reuses the same `.refhit` row.

**The hover card.** `.symcard` is new behaviour, not a restyle. Hovering an
underlined name fetches `/api/object-explorer/symbols/{id}/card` and shows the
signature, the module and file:line, and two actions. Two bits of plumbing were
needed: `CodeViewerResolvable` gained the `SymbolId` the importer already
resolved, and the declaration marks gained `data-member-symbol` — a declaration
stamps an `oe_module_objects` id for an object header and an `oe_module_symbols`
id for a member, two tables whose id spaces overlap, so without the flag the
card would sometimes have described a different symbol with the same number.

**Two silent bugs the port surfaced.**

- **`.tok-*` was dead CSS.** `tools.css` already styled
  `.cm-modifier-down .tok-variableName` and friends for the Cmd-held
  "everything is clickable" hint. Nothing ever installed `classHighlighter`, so
  those selectors had matched nothing since they were written. Naming the
  classes in `alHighlightStyle` brought them to life.
- **`hidden` did not hide.** Every component in `components.css` sets an
  explicit `display`, which beats the user agent's `[hidden] { display: none }`.
  The Find and Refs pill-tabs rendered with no session behind them, and the
  outline filter could not hide a row. `tools.css` already had three per-
  component patches for this (`.field[hidden]`, `.source-viewer__busy[hidden]`,
  `.source-viewer__refs-tooltip[hidden]`) — the general form went upstream as
  one `[hidden] { display: none !important; }` guard. That in turn exposed a
  panel that had been relying on class specificity to show itself while carrying
  `hidden`; there is now a test for the pattern.

**Legacy CSS: renamed rather than deleted.** *(As it stood at 14a — the second
viewer was retired later, on 2026-08-20; see "Retiring the second source
viewer".)* `SourceFileViewerLegacy.razor` was still reachable behind
`OBJECT_EXPLORER_LEGACY_VIEWER=1` and still rendered the
`source-viewer__outline-*` family, so those rules could not go. But `tools.css`
loads *after* `pages-power.css`, so any of them the new markup still matched
would quietly out-specify the port. The new viewer's behaviour hooks were
renamed to `sv-*` instead, which both breaks the collision and makes retiring
the legacy viewer a clean delete later. The refs-row rules the new panel no
longer needs (22 of them) were deleted outright, and a test pins the split in
both directions: the ported viewer renders none of the legacy names, and every
legacy name the legacy viewer still renders still has a rule.

Retiring that viewer was [#562], deferred at the time because it wanted a
maintainer call rather than a drive-by delete inside a design PR. **Taken and
done, 2026-08-20** — see "Retiring the second source viewer" below. The estimate
of ~450 lines was wrong; it was ~240.

**Verified by driving it**, not by reading it: the outline filter, section
collapse, the context menu, Shift+F12, the hover card, and the references and
find panels were each exercised in a real browser in both themes, and the
screenshots are in `scratch/14a-*`. Two of the fixes above were found that way
and nowhere else. Seeded three AL files into the dev database to do it — the
Object Explorer needs real objects, symbols and references before any of this
renders at all.

**Still open after 14a:** the symbol card has no `.symcard__doc` line, because
we do not extract XML doc comments into the symbol table. Flagged rather than
silently dropped — see the divergence register. *(Closed in PR 19e.)*

**One unrelated test came along.** The 43 new tests changed the parallel
schedule enough to trip a latent race in `McpSetupPageTests` on every run.
Proved it was scheduling rather than the port before touching it: the base
commit ran green, and so did this branch's app changes with the two new test
classes filtered out.

The first fix was wrong and the next run said so. Reaching for
`WaitForAssertion` — the idiom a sibling test in the same class already used —
only moved the failure, and revealed the real message underneath:
`UnknownEventHandlerIdException`. It was the **click** racing the render, not
the assertion: bUnit resolves an element to an event-handler id at `Find` time,
and the page's two database reads in `OnInitializedAsync` land a render in the
gap before the dispatch. The fix bUnit's own exception text prescribes is to do
both in one dispatch, `await page.InvokeAsync(() => page.Find(…).Click())`,
which is what is in now. [#563](https://github.com/mtaanquist/ALDevToolbox/issues/563).

### PR 14a, after the three-lens review (2026-08-20)

14a shipped, then got the fresh-eyes pass the earlier PRs got and should have
had before it landed. Three reviewers: the repo's `design-review` agent on the
rendered page, a fidelity pass against the handoff, and an adversarial
correctness pass over the diff. Between them, ~40 findings. The parts I was
most worried about held — tenant isolation on the new endpoint, XSS across the
client renderers, the `[hidden]` guard's blast radius, and the overlapping
`oe_module_objects` / `oe_module_symbols` id spaces all came back clean, and
the `--code-*` mapping was confirmed complete. Almost everything else that came
back was something reading the code could not have told me.

**The finding worth remembering.** `OutlineRowType` parsed the return type out
of the tail of `Signature`. `AlSymbolExtractor.DeclarationRegex` captures
`(?<sig>\([^)]*\))` — the parameter list, always ending in `)` — so that tail
is empty for every source-extracted procedure and the column shipped blank. It
looked right in review because **I wrote the seed fixture by hand and put the
return type inside the signature string**, then verified against my own
assumption. The column now reads `ReturnType` (which only the symbol-package
path fills) and falls back to the line number, and
`AlSymbolExtractorTests.A_signature_is_the_parameter_list_and_carries_no_return_type`
pins the shape so the next reader cannot make the same mistake.

The methodological point generalises past this bug: seeding your own fixture
means you choose what "realistic" means, and a fixture built to match an
assumption cannot falsify it. Where the real importer's output shape matters,
pin the shape in a test rather than in a seed script.

**Other defects fixed.** The `sv-*` rename dropped `sv-row` from the dependency
rows, so typing one character into the outline filter destroyed the Uses and
Used-by sections permanently — and the empty-needle escape lived inside the
per-row loop, so clearing the box could not bring back a section holding no
rows. Both filters now short-circuit on an empty needle. The hover card had a
stale-response race (a slow first hover overwriting a fast second one), could
strand itself on screen if the pointer left during its delay, cached transport
failures forever, leaked two window listeners per navigation, and never
dismissed on the cross-file jump it exists to offer. `@codemirror/commands` was
the one import still pulling an unpinned `@codemirror/language`, so Tab and
Enter indentation in the editable mounts had been reading the indent facets
from a second module instance.

**Fidelity.** The rail was not on `.u-compact` — the density every component in
`pages-power.css` assumes — so the whole thing rendered a size too big and the
screenshots I signed off were the roomy version. `.refhit.is-active` is now
wired, and the byte-identical `.refhit.is-current` I had invented for it is
gone. The symbol card's third meta slot is back, carrying the access level,
which AL encodes in the symbol kind rather than a column. And the ten
design-layer rules I had added to `tools.css` — in the same diff as a comment
explaining why one must not do that — went upstream to `pages-power.css`, along
with a genuine handoff bug: its `.otree__caret` rotates only via
`.otree__row.is-open`, but its own reference-group markup puts `is-open` on the
caret, where nothing matched it.

**Copy.** Three toasts said "mint", the outline heading said `USING` directly
above a line of AL reading `using Microsoft.Sales.Document;` while listing
something else entirely, the filter said "symbols" to an audience for whom that
means symbol packages, and reference rows without a snippet rendered a raw
`reference_kind` column value as their visible text. One message told users
that procedure and field references were "coming soon" — a capability that has
worked since `CreateAtPositionAsync` learned to route to
`CreateFromMemberSymbolAsync`, and which my own Shift+F12 verification had
exercised without my noticing the contradiction.

**Deferred, with reasons:** [#564](https://github.com/mtaanquist/ALDevToolbox/issues/564)
(`.orow.is-active` — needs a cursor signal out of `code-editor.js`, not a CSS
class) and [#565](https://github.com/mtaanquist/ALDevToolbox/issues/565) (the
Cookbook's separate `.tok-*` palette sharing a prefix in the same sheet — which
also corrects this branch's claim to have left "one palette").

**Two findings went to the maintainer, and both came back the same way:
the review's evidence was against the port's additions, not against the
handoff.**

The UX review wanted `.orow__glyph` deleted — undecodable without the mapping,
`f` wrong-footed for a language with no `function` keyword, redundant with the
section header above it. All true of the *five glyphs the port invented*
(`"` label, `{` object, `a` action, `e` event, `i` implemented-by) and not of
the component. The handoff draws three: `#`, `f`, `t`. Narrowed to those, with
a blank for every other kind.

The column stays because it is doing two jobs neither review weighed: the glyph
is *tinted*, so the row reads as colour at a glance even when the character
means nothing, and it gives every row a fixed left gutter that aligns the
names. A blank glyph keeps both and claims nothing, and the kind is still on
the row's `title` and in the section header. `f` stays as the handoff draws it
— swapping it for `p` is the kind of unilateral tweak that makes a design
system drift.

`Refs` stays too, for the opposite reason to the one the review assumed. It
noted the rail has room; it does not. The handoff affords `Refs` with **two**
pills in that head, and we have three plus the shortcuts button in a rail whose
minimum is 220px — the constraint is tighter for us, not looser. Trying to add
a "Shortcuts" text label to that same head clipped it into the Outline pill,
which is how we know. The panel heading spells out "References to *name*" one
line below, so the word is never actually absent.

### PR 14b — the shell around them (2026-08-20)

The other half of decision 3. `SourceFileViewer.razor` is now the handoff's
power-tool frame: `.pw` + `.u-compact` on the page root (the stopgap
`u-compact` 14a put on the rail alone is gone), `.pw__head` naming the tool and
the open file, `.pw__bar` carrying the breadcrumb, the compare picker and the
read-only badge, `.pw__body` holding the `.oe` three-pane grid, and `.pw__foot`
as the status line. No `<h1>`: a `.pw` fills the shell's content column edge to
edge, the same call PR 13 took on the Translator.

**The explorer tree is new capability, not just new pixels.** The left pane had
no counterpart in this app — the viewer was a two-column page you could only
reach by deep link, and getting to a sibling file meant going back to the
release. `.otree` now lists every module in the release, opens the one holding
the current file, and walks down the folder chain to it.

It is **lazy on purpose**, and that is the part worth reviewing. A Base
Application module carries thousands of source files, so the page response
ships only the branch that leads to the open file; every other caret fetches its
children from `/api/object-explorer/modules/{id}/tree` on first open.
`GetTreeChildrenAsync` does the narrowing in SQL — the folder half projects each
path's *first remaining segment* and takes the distinct set, so expanding
`src/` returns a dozen rows rather than seven thousand paths to group in
memory. `ExplorerTreeTests` pins that bound, and pins it against the real
`.app` fixtures rather than a hand-written one, for the reason 14a learned the
hard way.

**#566 is closed by the foot.** The keyboard and mouse model used to live
entirely behind an unlabelled `(i)` in the inspector head. The hints are now
always visible in `.pw__foot`, spelling *our* bindings: Ctrl/Cmd-click,
Shift+F12, Ctrl/Cmd-F, right-click. The Inspector's shortcuts panel stays for
the two gestures too long to fit on one line. The modifier reads `Ctrl` from
the server and `source-viewer.js` corrects it to `Cmd` on a Mac — the page is
static SSR, so the alternative was sniffing the User-Agent.

**Two things the rendered page said and the markup did not.** The status line
carried `40 lines` while the editor's own status bar, four pixels above it,
said `41` — the header column counts newlines and CodeMirror counts lines. Only
one of them can be the answer, and it is the one that moves with the cursor, so
the page-level count is gone. And the tree's names were being clipped without an
ellipsis: `.otree` is a grid whose single column is `auto`, so it sized to its
widest row, the pane scrolled sideways, and `.otree__name`'s `text-overflow`
never fired because the row was never narrow.

**A pre-existing bug found on the way past.** `/object-explorer/compare/file`
rendered its header and then two empty hairlines: the root was missing
`u-fill`, so `.app__content-inner:has(> .u-fill)` never matched, the shell's
content row stayed content-sized, and the panes' `height: 100%` resolved
against nothing. Confirmed against the branch *before* this PR — it is older
than the port. One class, fixed here because leaving a blank page in the tool
this PR is about is worse than the scope discipline is worth.

**Upstream.** Three more corrections went back to `pages-power.css`:
`text-decoration: none` on `.otree__row` (drawn as `<button>` in the handoff and
as `<a>` here, so a file is middle-clickable — the same call `.orow` and
`.refhit` already carry), `.okind`'s fixed `width: 19px` relaxed to
`min-width` + padding so a three-letter kind fits, and a narrow-viewport rule
for `.oe`, which the handoff has no responsive story for at all.

### PR 14b, after the three-lens review (2026-08-20)

Same three reviewers as 14a, run before calling it done this time: the repo's
`design-review` agent on the rendered page, a fidelity pass against the
handoff, and an adversarial pass over the diff. ~45 findings.

**Two of them were wrong, and checking mattered more than acting.** The
correctness pass reported that `StartsWith` compiles to an unescaped `LIKE`,
so a module holding both `src/Mobile_WMS/` and `src/MobileXWMS/` would show
each one's files inside the other — with SQL quoted as evidence. It also
reported that the ported root had lost `u-fill` while the compare page gained
it in the same commit, which is a genuinely alarming shape. Both were checked
rather than fixed: EF Core 10 parameterises `StartsWith` as an
*already-escaped* pattern (`src/Mobile\_WMS/%`), and `.pw` has its own
`.app__content-inner:has(.pw)` rule in the design layer, so it never wanted
`u-fill` — measured in the running app, content row 876px, foot pinned. The
escaping helper written to "fix" the first was reverted; the test written
alongside it stayed, because it pins the behaviour whoever provides it, and it
fails if anyone hand-rolls the pattern.

**The defects that were real.** A fetch landing after its ancestor had been
collapsed un-hid its children under a folder that was no longer on screen. The
`hidden` guard written for that read `frag.children` *after* `row.after(frag)`
— inserting a DocumentFragment moves its children out of it, so the guard was
always looping over nothing. A module whose `.app` shipped without embedded
source drew a caret that opened, showed nothing, and latched itself as loaded
so it could never be tried again — `OeTreeNode.HasChildren` existed for exactly
this and neither renderer read it. The tree listed test apps, internal apps and
language packs, which the release page has always hidden, so the same release
had two different app counts in one session. A folder's file list was
uncapped, and the C/AL ingest puts ~2,000 tables in one folder. Both resize
rails could be dragged to their own maximum and leave no code column at all, on
a choice that persists in localStorage. The failure message inherited
`.otree__row`'s 24px box and painted over the rows below it while wrapping to
three.

**And a lesson about test scenarios rather than about tests.** Two browser
checks reported failures that were the checks' own fault — one collapsed a
different module than the one it was testing, one used `route.continue()` on a
re-registered Playwright handler. Both looked exactly like product bugs. A
red test is evidence, not a verdict; the same discipline that says a green test
can be weak says a red one can be wrong.

**Fidelity.** Register row 24 claimed the status bar "reads the same" as
`.codev-foot`; it drops five of seven cells, two of them (`AL`, `runtime 13.0`)
for no recorded reason —
[#568](https://github.com/mtaanquist/ALDevToolbox/issues/568). Row 26 argued
"Collapse all" away on the grounds that the icon was not vendored and there was
little to collapse: the first is a chore and the second is only true at first
paint, so the row is withdrawn and the button is in. Three deviations were
living in prose and a CSS comment rather than the register (rows 29-31), and
two more the port had not noticed it was making (32-33). `.pw-split__grip` —
which PR 13 wrote in `Translator.razor.css` to solve exactly the
discoverability problem the UX review raised here — moved upstream to
`pages-power.css`, where the second tool to need it can find it.

**Copy.** The outline filter said "Filter this file..." twenty-five pixels
above a footer hint reading "find in this file", so the page offered two things
that both claimed to search the file and one of them silently searched
something else. "Object Explorer" rendered twice, once as text and once as a
link. The not-found page told the reader to pick a release above a button that
went somewhere else. The Explorer pane's count was a bare number whose subject
was invisible.

**Deferred, with reasons:** [#569](https://github.com/mtaanquist/ALDevToolbox/issues/569)
(the explorer folds away below 1100px with no toggle — the fix is a control,
not a media query, and it earns its keep on wide screens too) and
[#570](https://github.com/mtaanquist/ALDevToolbox/issues/570) (vendor
`PageObjectExplorer.dc.html`, which the fidelity reviewer could not diff
against because it is not in the repo — worth doing with a direct write rather
than a retype).

### PR 14b, after running it on real data (2026-08-20)

The staging image put the tree in front of a real BC 28.2 release — 86 apps, a
Base Application module, files hundreds of lines long. Nine things came back,
and most were only visible at that size.

**The one that mattered most was not in the Object Explorer at all.** The
Translator's first-run panel puts a transparent file input over the drop target
so the whole panel is clickable. `Translator.razor.css` moved that input to
`position: fixed` to get it out of the label's containing block — and `fixed` +
`inset: 0` is the *viewport*, so the input covered the entire application and
every click anywhere, the sidebar included, opened a file dialog. It only
renders on the path without the File System Access API, which means Firefox,
Safari, **or any deployment served over plain HTTP** — so it was invisible on
localhost and total on staging. The fix is to drop `position: relative` from
the label (a `.btn` carries it for the loading spinner) so the input's
containing block is the panel, which is what "cover the panel" meant.

**Two symptoms, one cause.** The collapse and shortcuts buttons looked jammed
into their bands, and the `Refs` count looked vertically offset. `.pane__head`
is `height: var(--row-h-head)` — 26px — and the `.pill-tabs` it is designed to
hold is a 22px pill plus 3px of padding and a 1px border each side, so 30px.
The group was overflowing its own head by 2px top and bottom, which is what put
the count out of line, and a 26px icon button in a 26px band has nowhere to sit.
Letting the head grow would have worked and would have given two side-by-side
panes head bands of different heights depending on what each held; the tab
group gets compact instead.

**Resizing at scale.** The drag handler recomputed its clamp from
`getBoundingClientRect()` on two elements immediately before writing the new
width — a read-write-read-write thrash forcing a synchronous layout of the whole
grid every frame, on top of the one the write already causes. Neither input
changes during a drag. With that gone and `content-visibility: auto` on the tree
rows, a resize measured at 306 rows went from 6.11ms to 3.11ms.

**"The collapse button does nothing"** was true from where the reader was
standing: in an 86-app release the open branch is usually scrolled off, so
collapsing it changed nothing visible. It now lands on the roots.

**A test failure that was the tooling's fault.** The full suite came back with
one failure: every search hit missing its app badge, reproducible in a namespace
run and green in isolation — a shape that reads exactly like an EF materialiser
problem, and a rewrite to an explicit join was already written before the actual
cause turned up in the working tree. `scratch/mutate.sh` copies the file it is
about to mutate and restores it at the end; a run killed by a two-minute timeout
mid-`dotnet test` never reached the restore, so `Badge: null` — a mutation
written to prove a detector had teeth — sat in the source, and the next run
baked it in as the new baseline. The script now restores on a trap. The lesson
is narrower than "read what you assert on": a tool that edits the tree to test
it must be crash-safe, or its scratch state becomes indistinguishable from
production code an hour later.

**Not reproduced.** The object list's kind badge was reported as stretching
across its column. `.otype` is `display: inline` with no rule anywhere that
could stretch it, and it measures 71px in a 140px cell locally. Changed to
`inline-block`, which is correct for a padded pill either way — an inline box's
vertical padding paints over its neighbours rather than adding to the line — but
the reported symptom is unexplained and needs the page it happened on.

### PR 14b, the second staging round (2026-08-20)

Seven more reports off the same BC 28.2 release, and the two most interesting
were both cases where the *symptom* and the *cause* had nothing to do with each
other.

**The object badge, reported twice as opposite bugs.** First "the `page` label
takes up all the space", then, after a change that could not have caused it,
"broke it the other direction". Both are one collision: `<span class="otype
page">` picks up `.page` from pages.css — the page-layout container,
`display: grid` with `container-type: inline-size`. Grid made it block-level, so
it filled the cell; once `display: inline-block` won on specificity, the
surviving `container-type` made it a size container whose inline size ignores
its contents, so it collapsed to 18px. Only the `page` kind was ever affected,
which is why neither reproduced on sample data that has none. The kind names are
namespaced now (`otype--page`), which is the fix for the whole family — `table`,
`report` and `query` are all plausible class names for something else later.

**The explorer "flash" was state loss.** Every navigation re-renders the tree
server-side, opened just far enough to show the new file, so a reader who had
opened three other apps lost all three on one click. The first fix — carry the
detached nodes from the previous page — was written and did not work, because
Blazor *reuses* the `.sv-tree` element and diffs its children: there are no
detached nodes, the rows are simply gone by the time any of our code runs. What
survives a navigation is a note in `sessionStorage` of which folders were open,
re-applied on the way in.

**Three reports, one cause, again.** Cramped icon buttons and a `Refs` count
that looked offset were the pane head being 26px while the tab group it holds is
30px. The first fix squeezed the group to fit; that worked and left every
control touching the band's edges, which is what produced the follow-up report
about a pressed button riding the border. The band grows now — a fixed height,
so two panes side by side still line up.

**And a shortcut that did nothing.** `Ctrl+Shift+F` was bound on the viewer root.
The viewer's CodeMirror is read-only, so its content element never takes focus
and the key almost always lands on `<body>` — which is not inside the root, so
the listener never heard it. Its two siblings were already on `window` with a
`document.contains(root)` guard, and the comment above them says exactly why.

### PR 14c — the Object Explorer's browse pages (2026-08-20)

The other half of the tool: the release search page, the module and object
detail pages, and the four result tables. Archetypes 5 and 6, so no power sheet
— these are `.page` / `.page-head` / `.filter-bar` / `.data-table` / `.card`,
the same shapes `TemplatesBrowser` has worn since PR 8.

**The scope selector became pill-tabs.** "Search in: Objects / Procedures /
File content / Compare" was a labelled `<select>`; it is the control that
decides what every other filter in the row *means*, and the handoff's own
Object Explorer bar spells that choice as `.pill-tabs`. That is register row 29
landing on the page it actually belongs to. The `Alt+1..4` shortcuts moved off
the visible labels onto `aria-keyshortcuts` and the tooltip — a label is not
the place to teach a keystroke.

**The multi-select object-type filter kept its shape and lost its chrome.** A
`<details>` disclosure is still the only way to pick several kinds at once, and
the handoff has no component for it. But its `<summary>` wears `.select` now and
its panel is a `.menu` with real `.check` boxes, so it sits in the filter row as
one of the dropdowns instead of as a bespoke thing that looks nearly like one.

**A kind is spelled one way now.** The release grid had a tinted word-pill from
`tools.css`; the module grid, listing the same objects one page apart, had a
bare word. Both go through `OeKindCell` — the design layer's `.okind` badge
(same two letters and tint as the explorer tree) with the word beside it. The
letters double as the search box's kind prefixes, so reading a row teaches the
syntax. Legacy C/AL kinds (`form`, `dataport`) have no prefix and so no badge;
they get the word alone, which is honest.

**`--bar-removed` went upstream.** The handoff's object-diff family is
new / modified / unchanged — half a diff. The release comparison also produces
`added` and `removed`, and `removed` had no keyline to take. `--bar-removed`
(aliasing `--bar-failed`) and `.data-table tr.is-removed` were added to
`tokens.css` / `components.css`, pushed to the design project, and re-pulled, so
all three copies match.

**306 lines of `tools.css` retired**, and the two containers they hung off
(`.object-explorer__browser`, `.object-explorer__filters`) are gone from the
markup. What the design layer had no answer for — the namespace head/tail split,
the fixed column widths, the type filter's panel — moved into scoped
`.razor.css` beside the components that render it, where a descendant selector
is not doing the scoping.

#### Three bugs the markup could not show

All three were found by looking at the rendered page. None is visible in the
source, and one of them is why the other two went unnoticed for two releases.

**A `string` component parameter given a bare attribute value takes it as a
literal.** `<OeCompareResults CompareRight="_compareRight" CompareBy="_compareBy" />`
and `<OeContentResults Search="_search" />` were handing their children the
*names* of the fields. Every other parameter type is safe — Razor reads a
non-string attribute value as C# — so `Results="_contentResults"` on the very
next line works and looks identical. Consequences: `CompareBy == "objects"` was
never true, so choosing "Compare by objects" ran the object query and then drew
the empty file table; the "pick a release" state was unreachable; file-content
search never showed its first-run state and its no-results line quoted
`_search` back at the user. Present since #441.
`StringParameterLiteralTests` pins the whole class, and was self-tested against
a re-planted instance.

**An object id that resolves to nothing spun for ever.** `_object` stayed null
and the page stayed on "Loading...", which reads as a hang rather than an
answer. It has a not-found state now.

**A 44px actions column.** The handoff's row carries a bare kebab, so
`.data-table__actions { width: 1% }` shrink-to-fit is right for it; ours holds a
`.ra` split-button, and under the `table-layout: fixed` this grid needs (so
lazy-loading the next page cannot reflow the columns being read) the colgroup
governs and 1% does not apply. The button overflowed the table's right border.
Only visible in a screenshot.

#### And a tool that had been mis-cutting CSS

`scratch/bc-design/retire-css.py` had two bugs, both found while using it here,
both of which make a *clean* run untrustworthy rather than loud:

- Its rule walker yielded offsets **relative to the `@media` body** for any rule
  inside one, while the caller spliced them against the whole sheet. 16 of
  `tools.css`'s 719 rules are inside an `@media`. Every earlier PR that ran this
  tool may have cut the wrong text; the brace-balance check it does at the end
  passes either way. Worth a look at the sheets PR 8 retired.
- It extracted class names from the text between the previous rule's `}` and
  this rule's `{` — which includes the **comment written above the rule**. A rule
  documented as "reuses `.field__input` chrome" therefore counted as naming a
  live class and survived retirement. Several dead rules had been sitting there
  because of their own prose.

Both fixed, and the walker now has a self-test that fails on the old behaviour.
A companion, `trim-dead-root.py`, removes the half of a selector list that is
rooted at a dead container while keeping the rest. Its first draft reproduced
*exactly* the failure `retire-css.py`'s docstring was written about — a
`\n\s*/\*.*?\*/\s*$` regex looks anchored but `re.search` returns the
**leftmost** match, so it cut from a comment a thousand lines earlier and took
the Cookbook and dependency-picker blocks with it.

**The check to run after any retirement pass is the class-set diff, not brace
balance.** Brace balance passes on a mis-splice — it removes one
complete-looking span and leaves valid CSS behind, which is what makes the bug
silent:

```python
def classes(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    sels = " ".join(m.group(1) for m in re.finditer(r"([^{}]+)\{", text))
    return set(re.findall(r"\.([A-Za-z][A-Za-z0-9_-]*)", sels))
```

**The audit came back clean** ([#573], closed). Three passes, each answering a
sharper version of the question:

1. *Is anything rendered today that no sheet defines?* 78 hits, all regex
   artefacts (a C# parameter name inside `class="@(Foo ? …)"`, an interpolated
   prefix like `build-pill--@x`) or classes that were **never** styled — JS
   hooks such as `.sv-tree-search`, plain markup handles. None had a rule to
   lose. Note the scan has to read the scoped `.razor.css` files too; a version
   that only read the shared sheets produced three false alarms.
2. *Which commits actually deleted a line from inside an `@media`?* Nine, four
   of which predate the tool.
3. *Which selectors have lived inside an `@media` and no longer do?* 35 ever,
   19 now, 16 gone, seven of which still name a rendered class — and all seven
   are accounted for: `.rd-*` moved into `RecipeDetail.razor.css`,
   `.source-viewer`'s rule survives as `.source-viewer:not(.pw)`, and `.oe` /
   `.oe__left` went on purpose when [#569] replaced the fold-away media query
   with a control.

A responsive sweep run alongside it (32 routes × 4 widths) found three admin
list pages whose content column scrolls sideways below 1100px — wide
`.data-table`s, unrelated, tracked as [#574].

[#573]: https://github.com/mtaanquist/ALDevToolbox/issues/573
[#569]: https://github.com/mtaanquist/ALDevToolbox/issues/569
[#574]: https://github.com/mtaanquist/ALDevToolbox/issues/574

### PR 14c, after the three-lens review (2026-08-20)

Three reviewers — the repo's `design-review` agent, a fidelity pass against the
handoff, an adversarial pass over the diff. Roughly 40 findings. **Two of them
were found independently by two reviewers each**, which is the strongest signal
the format produces and is worth reading as a ranking.

#### The two both lenses found

**F3 was silently dead.** `OeReleaseDetail.razor.js` focused the search box with
`document.querySelector("input.admin-search-input")`. This PR moved that input
onto the design layer's `.input`, so the selector returned `null`, the handler
fell through without calling `preventDefault()`, and F3 went to the browser's
find-next — the exact behaviour the comment above it says it overrides. Nothing
failed loudly, and the `Alt+1..4` half of the *same handler* kept working, which
is what made it look fine. It targets an id now. Verified by dispatching a real
`keydown` and reading `document.activeElement`, not by re-reading the selector.

**The one state this PR added drew a grey glyph on a red keyline.**
`--bar-removed` and `.data-table tr.is-removed` landed, but not the matching
`tr.is-removed .data-table__state` tint, so a removed row got a red edge next to
a grey `circle-minus` while added and modified rows got their colours. This is
the same bug the PR 13 notes record fixing for `.trow`; it reappeared because a
state was added to a family without its tint. Also fixed: `.crow.is-removed` in
`pages-power.css` still reached for `--bar-failed` directly, so the token added
to give "removed" its own name was not used by the one component that already
had the name. Both pushed upstream.

#### What the compare table looked like

Nothing in `scratch/14c/` showed a **populated** compare table — the whole new
path (`--bar-removed`, `RowStateIcon`'s diff arms, the state column) was
unverified visually, in a PR whose own write-up is titled "three bugs the markup
could not show". The fidelity lens caught that omission, which is a better
finding than most of the defects.

Seeding a second release to produce one added, one removed and two modified
files took ten minutes and confirmed all four states render correctly. The glyph
tints were then *measured* (`rgb(179, 43, 39)` = `--danger-text` on the removed
row), not eyeballed.

#### The blocker that was a fixture, again

The UX lens reported the Procedures grid printing every name twice —
`OnRunOnRun()`, `PostDocumentPostDocument(DocumentNo: Code[20])` — off a
screenshot. It was right about the pixels. It was wrong about the cause, and
only checking the *extractor* rather than the data showed why:
`AlSymbolExtractor`'s declaration regex captures `(?<sig>\([^)]*\))?`, which
begins at the `(`, and `CalObjectParser.RenderSignature` returns
`"(" + params + ")"`. Neither writes the name. Real data renders correctly.

`scratch/seed-oe-sample.py` had been writing name-inclusive signatures.

**This is the PR 14a trap running backwards.** There, the hand-written seed made
broken code look right; here it made correct code look broken. A fixture you
wrote yourself cannot falsify your own assumption *in either direction*. The
seed is corrected and carries a note at the point of edit.

#### And three things I asserted without checking

The fidelity lens went after the prose, which is where this PR was weakest:

- The comment I wrote on a 46-line `tools.css` block said it survived for
  `OeCompareFile`'s "Compare with" picker, and the "what is left" section
  repeated it as "the last holder of `.object-explorer__compare`".
  **`.object-explorer__compare` is in no markup anywhere** and has not been
  since #139; the picker moved to `.select-wrap` in PR 14b. The block was dead,
  as were seven `.object-explorer__row*` rules that the retirement pass had
  walked past. Retired, and the PR 14d scope line corrected.
- Four orphan comment blocks in `tools.css` describe rules retired in 14a/14b.
  The 14c diff *touched those exact lines* — it deleted the blank lines around
  them — and left the prose sitting above unrelated rules. Gone.
- Register row 41 said the two grids "cannot drift again". True of the two
  grids; a kind still renders as a bare word in three other places this PR
  touched, and the test pins only two call sites. The row now states its scope
  instead of overclaiming.

#### Everything else, briefly

Six tables took `.data-table--edge` with no row state, buying a 4px transparent
gutter that can never carry a signal — both the fidelity and correctness lenses
flagged it; only the two compare tables keep it. An unvalidated `?right=` left a
skeleton nothing could clear. `.kind-filter__option` had re-implemented
`.menu__item` including a `:hover` copied character-for-character from
`components.css`. The module page had the same spin-forever bug the object page
had just been fixed for. "Module" and "Extension" were both used for a `.app` on
one screen. `grep` and "chain" were in user-facing copy. The scope tab "File
content" was renamed "Source text" — it was the only one of the four naming a
container rather than what you are after, and its empty state had to spend a
card explaining the label.

Divergence register rows 43-45 record the `--edge` correction, the absent
`.pill-tab__count`, and the filter-bar ordering. [#575] tracks the one finding
that needs more than copy: the search syntax is documented **only** in the
no-results state, because the placeholder truncates at ~26 characters.

[#575]: https://github.com/mtaanquist/ALDevToolbox/issues/575

### Retiring the second source viewer (2026-08-20)

[#562], taken after a maintainer call. `SourceFileViewerLegacy.razor` was added
by #161 as a **one-release** rollback path when the source viewer moved to
static SSR. It stayed for **55 releases**. Nothing forced the deletion, so
nothing deleted it.

Gone: the 274-line razor, the `OBJECT_EXPLORER_LEGACY_VIEWER` branch and the
now-unused `LegacyViewerActive` property, and ~240 lines of `tools.css` — not
the ~450 the issue estimated, because 14a and 14b had already taken the rest as
a side effect. `ObjectExplorerLinks` is a pure function of its arguments now.

Two corrections to the issue text, both found by measuring rather than
trusting it: the line count above, and its implication that the env var gated
the route. It gated the *links*; `/object-explorer/file-legacy/{id}` was
reachable by anyone who typed it.

**And the retirement pass nearly broke the live viewer.**
`.source-viewer__outline-menu` and `-menu-item` share the retired family's
prefix but are built by the **live** `source-viewer.js` — the outline's
right-click menu. The pass took them on the strength of the name. What caught it
was an existing test, `Every_element_the_client_renderers_build_is_styled`,
which walks what the JS *renders* rather than what the sheet defines; the
class-set diff would not have, because the class stays "removed" either way.
A second rule, `.source-viewer__outline-item--child`, survived in the opposite
direction and had to go by hand.

The lesson is the same one in both directions: **a class name is not evidence
that a rule is dead — the renderer is.** The prefix-based list was written by
eye and was wrong twice in twenty-eight rules.

The guard that replaced the two legacy-specific tests now asserts the retired
vocabulary is absent from the markup, the JS *and* the sheet, plus that only one
`SourceFileViewer*.razor` exists at all.

[#562]: https://github.com/mtaanquist/ALDevToolbox/issues/562

### PR 14d — the two compare screens (2026-08-20)

Archetype 11, from `PageCompare.dc.html` (pulled and vendored in this PR — it
was not checked in). Two pages, not one: `OeCompareFile.razor` (the Object
Explorer's file diff) and `Compare.razor` (the standalone paste-two-texts tool).

**Doing one without the other was not an option.** They share
`.oe-compare-file__panes` — the tool's scoped sheet says so in a comment — so
porting only the Object Explorer page would have left the retired class alive
for a second caller. That is the exact shape of the mistake the #562 pass nearly
made and that 14b's tools.css comment *did* make. They are also the same screen:
two panes, a swap, change navigation, a live count. One frame, two sources for
the two sides.

What each takes from the archetype:

| | `OeCompareFile` | `Compare` |
| --- | --- | --- |
| `.pw__head` | Compare + the path, Swap sides | Compare, Swap sides, Clear both |
| `.pw__bar` | `.vsbar`: app, release chip → release chip, app; badge | `.vsbar`: Left / the original → Right / the changed version |
| `.cmp` | rail + `.pw-split` + view | `.cmp--norail`, view only |
| `.cmp__vbar` | state letter, path, `+7 -0`, prev/next | the live read-out, prev/next |
| `.cmp__phead` | release chip + app + Open | Original / Changed chip |
| `.pw__foot` | the change breakdown + key hints | "Nothing you paste is saved" + key hints |

`Compare.razor.css` is gone entirely; its 67 lines were a page shell the
archetype now owns.

#### Three things only driving the page found

**Every `.pw` page was rendering at its content height.**
`.app__content-inner:has(.pw) > * { height: 100% }` was set on the shell, but a
percentage height on a grid ITEM resolves against its grid AREA, and the inner's
single implicit row is `auto` — so the percentage was circular and fell back to
the item's own content. The source viewer and the Translator hid it: a pane full
of code overflows the row anyway, so they looked right at a typical window. A
diff of two small files did not, and at a 1400px-tall viewport the viewer
measured **947px inside a 1336px shell** — a band of empty page under the
editor that had been there since PR 13. The fix is one declaration,
`align-content: stretch`, pushed upstream. `CompareScreenTests` pins it.

**The overview ruler stacked under the code instead of beside it.**
`.source-viewer:not(.pw)` in tools.css makes a pane a flex **column** at (0,2,0).
The old row override was written as a descendant of the page shell
(`.oe-compare-file__panes > .source-viewer--compare`), so it carried the weight
by accident; rewriting it against the pane alone dropped it to (0,1,0) and it
silently stopped applying. Doubled to `.source-viewer.source-viewer--compare`,
with the reason in the rule.

**The change rail's drag handle was inert.** It was written `data-split="left"`,
copying the viewer — which resolves to a spec pointing at `.oe__left`, an element
this page does not have. The handle rendered, took the pointer, and did nothing.
There is now a `rail` spec (`--cmp-rail`, `.cmp` as its grid) and a test that
walks every `data-split=` in every `.razor` and requires `SPLIT_SPECS` to hold
the name.

#### The rail, and what it deliberately is not

The handoff's left pane is the change set. Ours lists the two apps' changed
files, from one `CompareModuleFilesAsync` call — two queries — rather than the
release-wide comparison, which runs a query per changed app. Capped at 500, and
the cap is **said**: the pane count reads "500 of 2,140" and the list ends in a
link to the Release page's Compare scope. Below the cap, which is the common
case, it is the whole change set.

No per-row `+`/`-`. We have each file's line count on both sides but not its
added/removed split, and deriving one from the other would put a number on
screen that looks measured and is not. Divergence 46.

#### Seed

The 25.1 copy of `SalesPostHandler.Codeunit.al` was a **one-line placeholder**,
so the first screenshot round showed a diff that was 40 lines removed and
nothing else — no added lines, no changed lines, no word-level marks, half the
screen hatched. It now carries a real 25.1 version of the file. Same lesson as
14c's compare table: *a state you cannot reach is a state you have not seen.*

#### What the fresh-eyes pass changed

Run with the repo's `design-review` agent against the rendered screenshots, as a
BC consultant handed "what changed in the Base Application between BC 25 and BC
26". It cleared the jargon test on both pages — nothing internal reaches the
screen — and found three things worth calling bugs.

**The toolbar badge lied about its scope.** It read "4 files changed", sitting
between the two release chips and nothing else, while counting the *app's*
changed files. For the review's named user — a Base Application diff inside a
forty-app release — that number is wrong by orders of magnitude and nothing on
screen says so. Its position is what made it lie. The badge counts *this file*
now; the app's count stays in the rail's head, next to the list it counts.

**Two counts of one thing that did not add up.** The view bar said `+7 -0` and
the foot said `10 changes - 7 added, 0 removed, 3 modified`, both visible at
once. The gap was the `modified` bucket, which a `+`/`-` pair has no slot for —
git counts lines added and removed, a line differ also reports lines that
*changed*. The stat is `+7 -0 ~3` now, with `.crow__mod` added upstream for the
third bucket.

**An identical pair looked like a diff that had failed to load.** With no tints
anywhere, the only signal was eleven-pixel grey at the foot of a 950px page. It
is said in the view bar now, in the slot the stat would occupy — which is what
the standalone tool already did with "The two texts are identical."

Also acted on: the next/previous buttons were never disabled (the tool's were);
there was no route back to the comparison the page was opened from, and the one
link out only rendered past the rail's 500-file cap; a malformed URL was told
"one of the two releases may have been removed", a confident wrong diagnosis;
`Ctrl+ArrowDown` in a tooltip is a `KeyboardEvent.key` value, not a key legend;
and the tool's foot said "Nothing you paste is saved", which is true and answers
a narrower question than the one a consultant with a customer's config on the
clipboard is asking — the text *is* POSTed to the server to be diffed. It now
says so.

Left as they are, with reasons — recorded because a declined finding that goes
unwritten gets re-derived by the next reader. The rail truncates paths from the
left, keeping the file name and losing the folder: recoverable on hover, and the
alternatives (two-line rows, middle truncation) either break the 26px row or are
not a CSS primitive. The `Ctrl Down` hint appears in both the view bar and the
foot; so does the handoff's. `A`/`M`/`D` stay as the handoff spells them, but the
view bar's letter carries a `title` now and every rail row already spells the
word. The `.vsbar`'s red/green chips read oddly in the one case where both sides
share a release (an object-level compare pairing two files inside one app) — the
axis that actually differs is then the path, which the head carries; rare enough
not to warrant a second layout, and the "All changed files" button now hides
itself in exactly that case. And the head carries the full path while the view
bar carries the file name, where the handoff puts the *comparison* in the head
and the path on the bar: two renderings of one fact rather than the handoff's
two facts, which costs a slot but never shows a wrong path.

### PR 14d, after the three-lens review (2026-08-21)

The repo's `design-review` agent (whose findings are above, since they landed
before the commit), a fidelity pass against `PageCompare.dc.html`, and an
adversarial pass over the diff. The two later lenses each found one thing that
was quietly wrong across a much wider surface than this PR.

#### The one that was worst, and reproduced exactly

**Every rail click bound another set of listeners.** A rail row is an `<a href>`
on a static-SSR page, so Blazor's enhanced navigation patches the DOM rather
than reloading: the `.source-viewer__code` hosts come back empty, CodeMirror's
children go, `initOne`'s double-mount guard clears, and the compare branch in
`init()` runs again. But the `.pw` frame, the `.pw-split` handle and the
toolbar's next/previous buttons are *identical* between the two pages, so Blazor
**preserves those nodes and their listeners**.

Driven, not reasoned about: three hops, then one ArrowRight on the focused
resize handle moved the rail **80px instead of 20**, and `storeWidth` persisted
it. `wireCompareChangeNav` already guarded its buttons with
`__compareNavBound` — the new wiring beside it had no equivalent, and the
pre-existing `window` keydown guard (`document.contains(left.root)`) could never
help, because that root is exactly what survives.

Three fixes: a per-element bind flag on the split handle and the filter box, and
the change-nav keydown bound once for the document against a mutable
`changeNavPanes`. That last one also fixed a bug nobody reported: the toolbar's
own next/previous buttons closed over the pane pair from the page *before last*,
so after a hop they were scrolling editors that no longer existed. Re-driven:
20px, and change-nav behaves identically fresh vs. after three hops.

#### The one that was widest

**The diff palette was never on the tokens.** Nine hard-coded `rgba()` /`rgb()`
values in `tools.css` painted the line tints, the change-bar gutter and the
overview ruler, while `tokens.css` has carried `--diff-add-bg` / `--diff-del-bg`
/ `--diff-chg-bg` per theme all along and `pages-forms.css` uses them for the
server-rendered audit diffs. So the app drew a diff two incompatible ways, and
one screen carried both: the rail's modified keyline was `--bar-draft` (#8A8300)
while the line six inches to its right was `rgb(234,179,8)`. In dark it widened —
the tokens flip, the editor did not.

Divergence 12 has claimed since PR 11 that "the *palette* is ported faithfully;
only the renderer differs". That was false for the one screen the palette exists
for. Now true.

One judgement call inside it: the handoff's word-level `mark` is the line's own
background plus a `--bar-draft` underline, which works on its sample's
proportional text and disappears in a monospace code pane — the underline ends
up under a row already tinted the same colour. Ported as the handoff's underline
over a fill mixed *from the same two tokens* (`color-mix`, an idiom already in
five sheets), so it follows the theme and still does the job the word diff exists
for. Checked at 3x in both themes rather than asserted.

#### And a marker class this PR emptied out

Moving both pages onto `.pw` took the last two users of `.u-fill` — a marker
with no rule of its own, read only by `.app__content-inner:has(> .u-fill)` in
base.css, under a comment naming three pages that no longer opt in that way. Two
opt-in mechanisms for one property, one unreachable. Retired, with a test.

#### Everything else, briefly

Both pages had lost their `<h1>` (the title is a `.pw__name` span), and
`<FocusOnNavigate Selector="h1">` in Routes.razor had nothing to land on — so
`.pw__name` and the error state's `.empty-state__title` are headings now, with
`margin: 0` pushed to the design layer. The rail head overflowed at its own
180px minimum, because `.pane__head` is a nowrap flex row with no shrinking
children and the count can read "500 of 12,431". Divergence 46 was **wrong for
two of its three cases**: an added file's `+62 -0` and a removed file's `+0 -19`
are exact, already fetched, and were being discarded at the `Select` — the rail's
fourth grid column had been rendering empty on every row. "All changed files"
pointed at a self-comparison when both files share a release. And four comments
said things that were not true, including one of mine describing a draft that
never shipped.

The tests grew from 16 to 20 and four of the original 16 were tightened. The
sharpest hole: `The_compare_pane_out_specifies_the_column_layout_it_overrides`
counted classes, but both selectors weigh (0,2,0) — the winner is decided by
*source order*, so moving the block up as a tidy-up would have put the ruler back
under the code with every test green. Six of six mutations caught now, including
that one, done by actually moving the block rather than faking it.

Filed rather than half-built: [#576] (inline / unified layout) and [#577]
(ignore whitespace) — the handoff's two bar controls, both new behaviour rather
than visual detail. Divergences 46–52.

And one gap this PR only *found*: [#578]. MCP's `compare_releases` is
object-level only, so a change that is not inside an object — a permission set,
an `.xlf`, a file added wholesale — is listed in the web UI and invisible to an
agent. It predates the redesign and the service method already exists; it is a
tool wrapper, not this PR's work.

[#576]: https://github.com/mtaanquist/ALDevToolbox/issues/576
[#577]: https://github.com/mtaanquist/ALDevToolbox/issues/577
[#578]: https://github.com/mtaanquist/ALDevToolbox/issues/578


### In flight and heading for a collision: PR #553, Entra ID sign-in

[#553](https://github.com/mtaanquist/ALDevToolbox/pull/553) ("Microsoft Entra ID
sign-in: per-org tenant allow-list, account linking, login-method policy") is
**open, not merged**, and branches off `main` — so it is written against the
*pre-redesign* markup for four files this migration has already rewritten or is
about to. Whoever merges it second owns the reconciliation; read this before
starting PR 11, and re-read it if #553 lands first.

- **`SiteAdminSettingsHeader.razor` — deleted by PR 9a.** #553 adds a new
  `SiteAdminSettingsEntra.razor` tab that renders it, and edits it to add the
  tab. On this branch the file is gone and `SiteAdminSettingsPage` replaced it;
  the new tab becomes one more entry in that component's `Tab` enum and tab
  list. Mechanical, but it will not merge on its own.
- **`Account.razor` — rewritten by PR 9b.** #553 adds a "Microsoft account"
  block to the Sign-in & security section using `.set-subhead` / `.set-panel` /
  `.acc-tbl.tt-pass` / `.acc-del`, every one of which PR 9b retired. It becomes
  a third `.card` in that section: `card__head` + a `.data-table` of links +
  a `card__foot` with the connect button, next to Passkeys, which it mirrors
  almost exactly.
- **`AdminAdministrationIdentity.razor` (+ its `.razor.css`) — rewritten by PR
  9a**, onto `SettingsPage` / `SettingRow` / `Switch`. #553 edits the old shape.
- **`Login.razor` (+ its `.razor.css`) — PR 11's file.** #553 adds the "Sign in
  with Microsoft" button and the local-login policy states. If #553 lands first,
  PR 11 ports the *post-Entra* login page and must keep all three policy states
  (local only / both / Entra only); if PR 11 lands first, the button goes onto
  the `.auth__card` archetype rather than the old `Login.razor.css`.
- **Migration `20260818000000_AddEntraIdentity`** sits above this branch's
  highest prefix (`20260807000000`), so the ordering rule in CLAUDE.md is
  satisfied either way round. Nothing to renumber — just do not renumber it.

The design side is genuinely additive: a linked-account row is the same shape as
a passkey row, and "Sign in with Microsoft" is a button on the auth card. None
of it needs a new component. The cost is purely that two branches edited the
same four files.

### Honest progress

| | |
|---|---|
| CSS still on the legacy sheets | **67%** (4,718 of 7,041 lines) |
| Legacy sheets remaining | **3** — `tools.css`, `base.css`, `admin.css` |
| Components fully on the design layer | **87** |
| Components still referencing a legacy class | **53** |
| Total stale class references | **563** |

PR 11 moved all four, and the auth bucket has left the remaining-work list
entirely: `auth.css` went 142 → 92 lines, ten components crossed onto the design
layer, and 62 stale references went with them. PR 10 moved none of them, and
that is the honest reading rather than a
disappointment: `Home.razor` and `AdminDashboard.razor` were already free of
legacy classes before it started — the dashboard's problem was that it had no
*content*, not that it had the wrong CSS. What the PR actually did was take two
whole archetypes out of the ported-but-unused column of #549, which this table
cannot see. `base.css` gained one line (the `a.activity__row:hover` entry in the
anchor-underline bridge) and `pages.css` two additive rules, both pushed
upstream so the app and hand-off copies stay byte-identical.

`tools.css` 5,472 → 3,588 (−34%), `base.css` 1,095 → 756, `admin.css` 660 → 374,
`auth.css` 244 → **0, deleted** (PR 12).
`base.css` finally went *down* (865 → 750) because PR 9a retired what it
replaced instead of leaving it to shadow the port. It is still the file that
shrinks last: what remains is mostly `.admin-form` members sharing grouped
selectors with live `.form-grid` / `.form-section` rules, which cannot go until
those callers move too.

**Landed:** token layer (1–2), component layer (3), **shell (4)**, Piper +
Compare (5), the five list-archetype browsers (6a–6c), recipe detail + the
Cookbook's loose ends (6d), both generators (7a–7b), audit history + diff (8a),
the four global audit pages (8b), **the whole admin edit-form family (8c)**,
**all of site administration and per-org Administration (9a)**, **the Account
family (9b)**, Object Explorer **landing** (14a), the toast component, the tools
home and the admin dashboard.

The fair framing: the *foundation* is finished — tokens, components, shell and
the archetypes everything plugs into — as is every high-traffic surface a
normal user touches, all of admin authoring, and now every admin and
site-admin settings surface. What is left is the Account family, auth, the two
power tools, and the gap below.

### What PR 8c actually needed, versus what the plan said

The plan said "the admin edit forms onto `.sub-rows`". That was written without
reading the files, and only a third of it survived contact:

- **`.sub-rows` fits a flat sortable list** — the module dependency editor, the
  claimed email domains. It needed one variant, `.sub-rows--reorder`, because
  the handoff reorders by dragging a grip and we have never shipped
  drag-and-drop.
- **It does not fit a spreadsheet.** The application-version and dependency
  catalogues are six columns of inline inputs with Excel paste and F8
  copy-above; a six-child row parked in the five-track grid puts the first cell
  under the 26px grip. They went onto `.data-table`, which was already
  underneath them.
- **Two components had no handoff counterpart at all** and were ported onto the
  tokens under their own names: `.folder-editor` (the recursive folder/file tree)
  and `.hint-details` (a `<details>` reference panel — the handoff's screens are
  one level deep and have no disclosure).

**Read the files before trusting a bucket in this plan.** The same caution
applies to PR 9 and to the gap below.

### What is left, by weight

From `.design/progress.py` at `e7fcea8`:

```
GAP    Pipelines / Projects        17 files   360 refs
PR 14  Object Explorer             14 files   133 refs
PR 9   settings / site-admin       16 files   107 refs
PR 9   Account                      5 files   101 refs
shared components + odds           14 files    79 refs
PR 11  auth                         8 files    62 refs
PR 12  docs / MCP / 404             5 files    18 refs
PR 13  Translator                   1 file      3 refs   <- see blind spot below
                                                       (done; the 3 refs badly
                                                        understated it)
```

### The plan has a hole: Pipelines / Projects

**363 stale refs across 17 files — the single biggest remaining chunk — and it is
not in the PR 1–14 order at all.** PR 6 migrated the Pipelines *browser*;
`PipelineBuilds` (77), `ProjectDetail` (73), `ReleasePipelineDetail` (45) and the
editor dialogs all landed *after* this plan was written. It needs a PR number and
a scoping pass against `.design/saas-delivery.md`, which nobody has read against
the archetypes yet. **Parked until 8 and 9 are done** — do not start it
opportunistically.

### The progress metric has a blind spot

It counts references to the *shared* legacy sheets. A page with its own
`.razor.css` scores zero regardless of what is in there. Some scoped CSS is
legitimate — a migrated page keeps its own layout (`RecipeDetail` 305,
`CookbookBrowser` 94) — and some is unmigrated styling hiding from the count.
Never quote a single stale count as "this page is done".

**The Translator was the worked example, and PR 13 confirmed the warning was
right.** It read as 3 stale refs (`btn`, `mono`, `muted`, `u-fill`) while
carrying 395 lines of private CSS — 251 hard-coded pixel values, four raw hexes,
and a **local `--st-todo` / `--st-review` / `--st-trans` / `--st-final` ramp
declared on the page root, which shadowed the design system's identically-named
tokens for the entire subtree**. The count could not see any of it. The page was
not 3 refs from done; it was a whole archetype away.

**Last upstream push: 2026-08-21**, carrying the six `components.css`
corrections from 15a, 15b and the Object Explorer review round — `.modal-layer`
plus the backdrop radius reset, `.confirm-dialog--wide`, body paragraph margins,
`.menu__item { text-decoration: none }`, the `.pill-tab__count` nudge, and the
four `.data-table` column modifiers scoped so they apply at all. The
pull-before-push diff was exactly those additions and nothing else; all three
copies re-verified byte-identical afterwards, and the other six sheets were
already clean.

**Upstream sync is current, and now enforced.** All six shared sheets have been
pushed back to the design project, so divergences 1–6 are the design system's own
text. Only 7 (`--sticky-head`), 8 (always-open nav groups) and 9–10 (the
Translator's grid columns) are ours to keep — differences between two apps, not
errors. `StylesheetLoadOrderTests` fails if a sheet in `wwwroot/` stops matching
its `.design/handoff/` copy byte for byte, so "patch the local copy and mean to
push it later" is no longer a thing that can happen quietly.

### The bug classes this migration keeps producing

Every defect found after a merge so far has been one of these. Check for them
before calling a page done:

1. **Class collisions.** `tools.css` loads *after* `components.css`, so any
   still-ungated legacy rule with the same class name wins on every property it
   names. Hit us three times: `.ra__menu` (kebabs 35px off, app-wide),
   `.pill-tabs` (`margin-bottom: 16px` misaligning migrated tab bars, plus
   `button.pill-tab` beating the design system's sizing), and `.nav-link-btn`
   beating `.brand__toggle`'s `display: none`. The fix is always to **gate or
   rename the legacy rule**, never to patch an override on top.

   This one is now **enforced by a test** rather than tracked by hand:
   `ALDevToolbox.Tests/Assets/ComponentCollisionTests.cs` parses the four
   stylesheets, diffs the bare `.class` rules that appear in both layers, and
   fails on any layout property the legacy rule does not override — with an
   allow-list of reviewed exceptions that must shrink as families migrate. A
   screenshot only catches the collisions on pages someone thought to look at,
   and the whole failure mode is that nobody looks: the `.ra__menu` kebab bug
   survived months for exactly that reason.
   [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) tracks the
   remaining three exceptions, all of which retire with the `.ra*` migration
   (#529) and the generator's module picker.

   **The test has a blind spot, and it shipped a bug.** It asks whether the
   legacy rule *fails to override* a design property. It never asks whether the
   legacy rule *overrides one with an incompatible value* — which is the failure
   mode once a MIGRATED page uses the class. `.form-grid` is the worked example:
   the legacy rule names `display` and `gap`, so nothing leaks and the test stays
   green, but it turns the design system's two-column grid into a one-column flex
   stack on every page that has moved. Same for `.field`'s `margin-bottom: 16px`
   and `.field__label`'s uppercasing. PR 7 patched around two of them per-page
   and nothing caught that a third page needed the same patch; they are now the
   **form-scaffolding bridge** in `base.css`, keyed on the `.page` root every
   migrated page carries and no legacy root does. Widening the test is
   [#542](https://github.com/mtaanquist/ALDevToolbox/issues/542).

   **The PR 8 audit found four more of exactly this shape**, so the bridge is
   now "the design-layer bridge" and covers seven: `.card` (legacy `--r-lg` and
   the heavy `--shadow`, so every migrated card sat on a drop shadow instead of
   the 1px hairline), `.data-table` (collapsed borders, 13px body, 8px cell
   padding — the design system sizes cells by `--row-h`, so every migrated table
   was denser than the handoff), `.audit` (a *different* component of the same
   name, the key/value trail on Project detail and Account) and `.section-label`
   (tighter tracking). None of these was visible in a screenshot: legacy values
   are plausible, just wrong. Measure with `.design/tools/collisions.py`
   (parses the sheets and reports which declarations a later one wins) and
   confirm with `.design/tools/cascade-probe.mjs`, which asks a real browser
   instead of guessing at specificity. **Wrap the probe's markup in `.page`** —
   the bridge is gated on it, and measuring outside it answers the wrong
   question.

   The same audit found the mirror-image miss: rules whose comment says they
   *moved* to the design layer, where only the comment moved. `.extension-editor*`,
   `.dep-editor__fields` and `.logo-preview` each had a "moved in 8c-3" note
   sitting directly above the original rules, which — loading last — still won.
   After deleting a block, re-run `.design/tools/dead-css.py`; a rule that
   is still defined and no longer applied is the tell.
2. **Sizing chains broken by the new shell.** `.app__content-inner` is an
   auto-height grid where `.content` used to be a definite-height flex item.
   That silently collapsed Compare's editors to 0px, and the `min-height` fix
   then stretched *every* page because grids stretch auto rows by default. Full
   height is now opt-in via `.u-fill`.

   Third instance, same rule: `.app__content-inner > *` pins `width: 100%` so
   auto margins do not shrink a page root to its content — but a page can also
   mount a `position: fixed` overlay at its root, where `100%` resolves against
   the **viewport**. Both toasts rendered as a full-width bar across the bottom
   of the window. Out-of-flow roots are now excluded by name (CSS cannot select
   on `position`); add to that list when a page mounts a new one.
3. **Grid tracks sized to min-content.** A grid's implicit `auto` track is sized
   to its content's *min*-content, which for `white-space: pre` code is the full
   unwrapped line. Use `minmax(0, 1fr)` on any track that can hold code or a
   long token.
4. **Razor swallows the space before an `@if`.** `@row.DepPublisher <span
   class="tag">` renders as `MicrosoftNot in catalogue`. It has shipped twice —
   `#42in Default` on the SiteAdmin audit list (8b) and the module editor's
   dependency publisher (8c-3). The markup looks correct both times, and a text
   pattern like `/[a-z][A-Z]/` drowns in CamelCase brand names (GitHub, DevOps,
   DeepL). **The durable fix is structural:** a label-plus-tag pair is a flex row
   with a gap, and a gap cannot be swallowed. The detector is
   `.design/tools/run-together.mjs`, which measures whether a text node's
   last glyph touches the next element's box.

### Verification that actually catches things

Screenshots alone have missed real bugs repeatedly — a page can look perfect and
still be broken. What works:

- **Drive the app, don't just shoot it.** Search boxes that never filtered,
  kebabs 35px out of place and a nav that navigated to the site root were all
  invisible in both markup and screenshots.
- **`.design/tools/sweep-stretch.mjs`** walks all 32 routes and asserts on
  `.page-head` height, `.u-fill` fill height, and horizontal overflow. The
  earlier sweep only checked overflow and console errors, and sailed straight
  past a page stretched to 4× its height. **Extend the assertions whenever a new
  bug class appears** — that is what turns the sweep from decoration into a net.
- **Seed the awkward data first.** Local seeds were too tidy to reproduce three
  reported bugs: a short code line hid the overflow, absent min-versions hid the
  chip escape, four keywords hid the table blow-out.
- **A synthetic `mouseover` event does not set CSS `:hover`.** One fix was
  verified green while the page was still visibly broken. Use real pointer hover
  (`locator.hover()`).
- **Self-test any detector before trusting a clean run.** The first
  run-together detector returned clean on a page that definitely had the bug: it
  read the text node's *parent*, and in a grid or flex parent each run of text is
  its own item, so adjacency means nothing there. `run-together.mjs` now exports
  a `selfTest` that plants the bug in the live page and asserts it is reported.
  A detector nobody has watched fire is not evidence.
- **Park the pointer and blur before shooting.** The pointer keeps its viewport
  position across a navigation, so whatever sits under it on the *next* page is
  already `:hover` before anything is measured — that read a resting table cell
  as bordered. A screenshot taken straight after driving a page catches the
  last-touched control mid-hover and mid-focus, which looks exactly like a
  styling bug.
- **Delete CSS by rule, never by range.** "Everything between rule A and rule B"
  removed 896 unrelated lines from `tools.css` in 8c-4, because the two markers
  were nowhere near each other. CSS has no compiler, so nothing failed — the line
  count caught it. Use `.design/tools/retire-css.py`, which walks the sheet
  brace by brace and drops a rule only when every class in its selector is dead
  across `.razor`, `.razor.css`, `.cs` **and** `.js` (a lot of `tools.css` is
  applied by CodeMirror, not by markup). After a deletion pass, re-sweep the
  pages you did *not* change and assert nothing collapsed: a rule that turns out
  to still be load-bearing shows up as a flattened page, not as an error.

## What we measured

### The token layer is a safe drop-in

Every custom property the app reads is either defined in the new `tokens.css` or
was **already a dead reference** before this work. Checked mechanically:

```
grep -rhoE 'var\(--[a-zA-Z0-9-]+' --include=*.css --include=*.razor \
  ALDevToolbox/wwwroot ALDevToolbox/Components | sed 's/var(//' | sort -u
```

18 names came back "not in the new token layer". Four (`--st-todo`,
`--st-todo-bg`, `--st-review`, `--st-review-bg`) are declared locally in
`Translator.razor.css` and are self-contained. One — `--source-viewer-outline-width`
— **is not dead at all**: `source-viewer.js` sets it at runtime for the
resizable Object Explorer pane, and its `340px` fallback is the correct default.
Leave it alone. The remaining **13 were never declared anywhere in the app** and
had been silently resolving to their hardcoded `var(--x, #fallback)` second
argument:

`--accent` `--border-color` `--border-muted` `--line` `--r-md` `--radius-md`
`--radius-sm` `--surface-1` `--surface-hover` `--text-1` `--text-2` `--text-3`
`--text-muted`

Two of these were live bugs rather than cosmetic drift:

- `base.css` used bare `var(--line)` with **no fallback**. An invalid `var()`
  makes the whole `border` shorthand invalid at computed-value time, so that
  row rendered with **no border at all**.
- Several fallbacks baked *dark-theme* greys in unconditionally, e.g.
  `var(--text-1, #d8d8e0)` and `var(--surface-1, #1a1c22)` in `tools.css` —
  dark-mode colours painting on the light theme.

This is exactly the drift the design system exists to stop, and it is a good
argument for the "no `#` in a component layer" non-negotiable.

### What the token swap reaches, and what it doesn't

Colour and type re-point globally the moment `tokens.css` loads. Shape only
re-points where the radius was already tokenised. Counts per stylesheet:

| File | hardcoded hex | `rgba()` | `border-radius: Npx` | `border-radius: var(--r*)` |
| --- | --- | --- | --- | --- |
| `base.css` | 3 | 2 | 12 | 12 |
| `tools.css` | **88** | **60** | **87** | 79 |
| `admin.css` | 13 | 14 | 7 | 13 |
| `auth.css` | 3 | 0 | 0 | 2 |
| scoped `*.razor.css` | 19 | - | 37 | - |

`base.css` and `auth.css` are nearly clean and mostly came along for free.
`tools.css` is where the drift lives, and it is why the migration is
tool-by-tool rather than one sweep.

## Decisions that need a call

The design agent worked greenfield. These are the places where it made a choice
we have to accept, adapt, or reject — worth settling before the component PR,
because each one ripples.

1. **Square corners.** `--r` drops 8px → **2px**, `--r-control` 2px; `--r-pill`
   is reserved for status pills, avatars and progress bars. This is deliberately
   BC-native and it is the single biggest visual change. Because the tokens flip
   globally, the *tokenised* radii went square immediately — but ~136 hardcoded
   `Npx` radii did not, so the app is currently **mixed** until each tool
   migrates. Accepting this means committing to finish.

2. **The primary button is not the brand teal.** `#00B7C3` measures 2.5:1
   against white, so `.btn--primary` fills with `--primary-strong` `#008089`
   and the bright teal is reserved for accents, keylines, focus rings and active
   bars. That is correct for contrast, but it means "BC teal" reads as a deep
   teal in the loudest place. Look at the screenshots before signing off.

3. ~~**The font stack has no non-Windows fallback.**~~ **Settled — see below.**

4. **Status changes shape and meaning.** `.status-pill` becomes a squared tag
   with a 3px left keyline instead of a rounded lozenge, and the system adds a
   rule: **pills are for cards, detail headers and inline text — never for table
   rows.** Rows carry status as a 4px right edge bar (`.data-table--edge` +
   `tr.is-succeeded`). That is an information-design change, not a restyle: it
   touches `BuildStatusPill`, `DeliveryStatusPill`, and every status column in
   Pipelines, Releases and Projects. Decide whether we take the row rule.

5. **`.is-*` for runtime state, `--modifier` for variants.** ~~We currently use
   `.theme-toggle__btn--active`.~~ **Adopted.** The design uses `.is-active` /
   `.is-selected` / `.is-open` / `.is-checked`; the split is the better convention
   and one of its stated rules. The theme toggle was renamed with the shell (PR 4),
   `theme.js` included — it toggles the class at runtime, so a rename that misses
   the JS leaves the highlight dead. Rename the rest as each component moves.
   Note the handoff also ships `.is-hover` / `.is-focus` as spec-sheet display
   aids; those are the ones safe to drop, the state ones are not.

6. **New components with no current equivalent.** `.cue` (BC activity tiles),
   `.switch`, `.skeleton`, `.toast`, `.alert`, `.header-tabs`, `.steps`.
   `.skeleton` and `.toast` fill real gaps (we render bare "Loading..." text and
   have no confirmation feedback). `.cue` is a genuine BC idiom for the admin
   dashboard. Take them as their page needs them, per CLAUDE.md — not up front.

7. **Modern CSS.** `shell.css` uses container queries against a `.shell-root`
   wrapper, and `color-mix()` appears in the focus halo, modal backdrop and
   reconnect overlay. Both are fine for our targets, but they are new to this
   codebase and worth knowing before someone "fixes" them. Both are now live —
   PR 4 shipped `.shell-root` and the three container breakpoints, verified
   firing at 1280 / 1080 / 700. Note `.page` is *also* an inline-size container,
   so a query inside a page body resolves against `.page`, not `.shell-root`.

Density tokens (`--control-h`, `--row-h`, `.u-compact`) are declared but nothing
consumes them yet — they only start mattering as components migrate.

## Settled: the font stack (decision 3)

Segoe UI **cannot be self-hosted.** Microsoft's licence covers using the font to
build software that runs on a Microsoft platform; it does not grant
redistribution or web embedding, and Segoe UI Variable is explicitly excluded
from licensing outside Microsoft products. There is a commercial route, but it
is a procurement conversation, not a download.

Microsoft's own answer is **[Selawik](https://github.com/microsoft/Selawik)** —
an open-source, *metric-compatible* Segoe UI replacement under OFL-1.1, built so
non-Windows targets can match Segoe's metrics. We take a hybrid:

```
--font-sans: "Segoe UI", "Segoe WP", Segoe, device-segoe,
             Selawik, system-ui, -apple-system,
             Tahoma, Helvetica, Arial, sans-serif;
```

Segoe stays first, so **Windows users render genuine Segoe UI and download
nothing** — web-font fetches are lazy and only fire when every earlier family
misses. Everyone else gets Selawik at 32 KB (400 + 600 as `woff2`, in
`wwwroot/fonts/`, declared in `wwwroot/fonts.css` with the OFL text alongside).
`--fw-medium` (500) has no Selawik weight; CSS font-matching resolves it down to
400 rather than synthesising a faux bold, which is the behaviour we want.

**Known gap.** Selawik maps 348 codepoints — Latin only. Verified present:
Danish `æøå`, German, French, Polish, Czech, Turkish, smart punctuation.
Verified absent: **Cyrillic, Greek, Vietnamese, CJK.** Those fall through
per-glyph to `system-ui`, so they render correctly but in a different face from
the surrounding UI. The place this shows is the **Translator**, where a target
string can be any BC locale. If that reads badly once the Translator migrates,
the fix is to scope the grid's target column to a broader stack rather than to
widen the app-wide font.

This *was* the one deliberate deviation between `.design/handoff/tokens.css`
(pristine upstream) and `ALDevToolbox/wwwroot/tokens.css`. It isn't any more —
the correction went upstream, and as of the PR 8 audit the two files are
byte-identical. See `handoff/README.md` for how to keep them that way.

Which matters for [#544](https://github.com/mtaanquist/ALDevToolbox/issues/544):
the type scale is not something we mistranscribed. Both files carry the same
`rem` values and the same "rem against a 16px root" note. The handoff renders
them at 22px / 14px / 12px because its prototype never sets a root font-size
and inherits the browser's 16px; we render them at 19.25px / 12.25px / 10.5px
because `base.css` has set `html, body { font-size: 14px }` since `1b70480`
(2026-05-20) — three months before this migration started. A `rem` scale was
imported onto a root it was not calibrated for.

Uniformly 88%, and **type only** — `--space-*`, `--control-h` and `--row-h` are
px, so the boxes were already right.

**Fixed.** `html` is 16px and `body` is 14px: `rem` resolves against the root,
so the scale sits on the root it was drawn for while the inherited default stays
where it was. `.design/tools/compare-to-handoff.mjs` renders the same markup
under the handoff's sheets and under our full stack, and now reports 100% on
every probe.

The other half of that change: **the type scale is the only rem in the app.**
The handoff sheets use px everywhere outside `tokens.css`, and our own 60
incidental rem values — scoped `.razor.css` font sizes, `.row-editor__col-*`
widths, a few paddings — were all authored against the old 14px root, so they
were pinned to the pixels they already rendered at. Correcting the root then
moved the design scale and nothing else. **Keep it that way**: a new `rem`
anywhere but `tokens.css` now means 16px, which is not what an author copying
a neighbouring rule will expect.

Residual, and not the same bug: our `body { line-height: 1.5 }` makes headings
and micro-labels a few px taller than the handoff, which sets no body
line-height and inherits `normal`. `.page-head__title` is 33px against their
25px. That is an app-wide prose decision rather than a token error — worth a
look when the last legacy sheet goes, not before.

## The fidelity audit (2026-08-16)

Six agents, one per shipped archetype, each rendering the handoff screen and the
live page and diffing them. What it found is a different *class* of defect from
the mechanical audit that preceded it: not values that are wrong, but **components
that were ported into CSS and never used**, and pages nobody had inventoried.

Findings are [#545](https://github.com/mtaanquist/ALDevToolbox/issues/545)–[#550](https://github.com/mtaanquist/ALDevToolbox/issues/550). The two that change the plan:

- **`TemplateDetail.razor` was never migrated and appears nowhere in this
  document** (#545). It fell into `progress.py`'s catch-all bucket, labelled
  "shared components + odds", which reads as leftovers rather than *an
  unmigrated user-facing page*. There is now an `UNTRACKED end-user pages`
  bucket so the next one cannot hide the same way.
- **Two whole archetypes are dead CSS** (#549): the dashboard's `.cue` /
  `.activity` / `.dash-*` family, and the settings-row `.setting*` / `.switch` /
  `.header-tabs` / `.edit-col` family. Zero markup uses either. `/admin` renders
  no counts at all where the handoff is entirely counts. The settings family is
  precisely what PR 9 needs, so that one is scheduling information.

**Read this before running the next one.** Three lessons:

- **Subagents cannot reach `DesignSync`** — it is disabled for them. All six
  hit that wall. They produced good work anyway by falling back to the ported
  CSS as the contract, but nobody diffed an actual rendered screen. Pull the
  `.dc.html` screens into `.design/handoff/` *first*; only `ComponentsPanel` and
  now `PageList` are checked in.
- **Verify before relaying.** One agent reported the Identity page's
  strong-authentication toggle as having no save control and no confirmation —
  a security setting the admin could not tell had applied. It is an
  immediate-commit toggle (`@bind:after`) that renders an `.alert--success`.
  The real finding was smaller: a bare `.check` where the handoff has a
  `.switch`. Every claim relayed from this audit was grepped first.
- **Seed before you judge a screen.** The extension generator's preview tree
  rendered as a single row because the verification org had no always-included
  files — the signature region of the signature screen, empty. Two recipes with
  no attached files likewise left the recipe detail's rail, download action and
  every `.code-block` unrendered.

## Tracked issues

Judgment calls and check-later items are GitHub issues labelled **`redesign`**,
so they survive the branch and this document. Add to that label rather than
growing a TODO list here — there will be many.

Open decisions (these need a human answer):

- [#523](https://github.com/mtaanquist/ALDevToolbox/issues/523) — confirm the `.btn--loading` divergence
- [#524](https://github.com/mtaanquist/ALDevToolbox/issues/524) — does the no-pill row rule apply to *every* data-table?
- [#525](https://github.com/mtaanquist/ALDevToolbox/issues/525) — where link colour lives once `base.css` retires

Deferred work and things to verify:

- [#526](https://github.com/mtaanquist/ALDevToolbox/issues/526) — delete the legacy `--blue*` aliases
- [#527](https://github.com/mtaanquist/ALDevToolbox/issues/527) — `BuildStatusPill` / `DeliveryStatusPill` onto `.status-pill`
- [#528](https://github.com/mtaanquist/ALDevToolbox/issues/528) — div-based run histories onto `.run-list` / `.run-row`
- [#529](https://github.com/mtaanquist/ALDevToolbox/issues/529) — remaining component families
- [#530](https://github.com/mtaanquist/ALDevToolbox/issues/530) — Selawik's Latin-only coverage vs the Translator grid
- [#531](https://github.com/mtaanquist/ALDevToolbox/issues/531) — screenshot-diff against the rendered `.dc.html` sheets
- [#532](https://github.com/mtaanquist/ALDevToolbox/issues/532) — status vocabulary on the remaining admin tables
- ~~[#536](https://github.com/mtaanquist/ALDevToolbox/issues/536) — `RecipeTypeBadge` is the last rounded object on the Cookbook page~~ — **closed in 6d**
- [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) — component-layer class collisions leak properties the old rules don't override
- [#542](https://github.com/mtaanquist/ALDevToolbox/issues/542) — the collision test is blind to same-class-different-value overrides, which is the half that bites a *migrated* page
- [#543](https://github.com/mtaanquist/ALDevToolbox/issues/543) — Add a user presents two forms where the first can already do the second's job
- [#549](https://github.com/mtaanquist/ALDevToolbox/issues/549) — ported-but-unused components (`.ftabs` / `.ftab`, `.codev`)
- [#561](https://github.com/mtaanquist/ALDevToolbox/issues/561) — the symbol card has no doc line, because the extractor does not capture `///`
- ~~[#562](https://github.com/mtaanquist/ALDevToolbox/issues/562)~~ — **done** 2026-08-20: `SourceFileViewerLegacy` and ~240 lines of `tools.css`
- [#564](https://github.com/mtaanquist/ALDevToolbox/issues/564) — `.orow.is-active` needs a cursor signal out of `code-editor.js`
- [#565](https://github.com/mtaanquist/ALDevToolbox/issues/565) — the Cookbook's separate `.tok-*` palette shares a prefix in the same sheet
- ~~[#566](https://github.com/mtaanquist/ALDevToolbox/issues/566) — keyboard hints belong in `.pw__foot`~~ — **done in 14b**
- [#567](https://github.com/mtaanquist/ALDevToolbox/issues/567) — quick-open in the viewer's toolbar, once it has a binding the browser does not eat
- [#571](https://github.com/mtaanquist/ALDevToolbox/issues/571) — a minimap for the code pane; **out of the redesign by decision**
- [#572](https://github.com/mtaanquist/ALDevToolbox/issues/572) — collapse the shell's left navigation; **out of the redesign by decision**
- [#568](https://github.com/mtaanquist/ALDevToolbox/issues/568) — the status bar's language and runtime cells
- ~~[#569](https://github.com/mtaanquist/ALDevToolbox/issues/569) — the explorer folds away below 1100px with no toggle~~ — **done in the staging round**
- [#570](https://github.com/mtaanquist/ALDevToolbox/issues/570) — vendor `PageObjectExplorer.dc.html` so the sheet can be diffed from the repo
- [#580](https://github.com/mtaanquist/ALDevToolbox/issues/580) — `.page-head--sticky` is in the vocabulary and used by `PageSettings.dc.html`, but no rule defines it

## Vendoring the handoff (done, 2026-08-21)

`PageDetail.dc.html` cost PR 15c/15d a rework because it was never pulled into
`.design/handoff/`. The fix was to vendor **everything**, so no screen can hide
again — and it is now done: all 40 files the design project holds outside
`screens/` and `uploads/` are local, byte-for-byte, alongside our own
`README.md`. Nineteen were pulled in this pass:

the four archetype prose sheets (`PagesStandard`, `PagesForms`, `PagesPower`,
`PagesContent`), five app screens (`PageGenerator`, `PageTranslator`,
`PageDocs`, `PageConnectAgent`, `PageAdminEdit`), the spec-sheet scaffolding
(`Components`, `Foundations`, `FoundationsPanel`, `KitchenSink`, `ShellFrame`,
`ShellPageBody`, `SinkBody`), plus `foundations.css` (the *doc* sheet — we do
not ship it, but every screen links it), `support.js` (the `.dc` runtime) and
`bc-reference.md`.

Several of them had already earned their keep before being vendored — read
from the project and thrown away. `PagesStandard.dc.html`'s prose settled 15e's
component choice in one line (*".run-list / .run-row stay in pages.css for
card-like histories that are not tabular"*), and it is where the *"run history
is a real `.data-table`"* rule lives. `PagesForms` and `PagesPower` are the
equivalent authorities for the admin edit-form family and the power tools.
`PagesPower` is also where the compare screen's own spec sits, including the
line PRs 16b and 16c were built against: *".hunk — the `@@` separator, repeated
in both panes so the sides stay in step."*

**It could not be delegated.** The first attempt handed the whole pass to a
subagent; `DesignSync` is session-scoped and never surfaced to it, so it fetched
nothing. The cost is context rather than difficulty: `get_file` returns a screen
into the session and it has to be written back out, so each is round-tripped
twice. The exception worth knowing — a result over ~64KB is persisted to a file
instead of being inlined, and can be extracted with a script for free, which is
how the 69KB `support.js` cost nothing at all.

## What the archetype sheet specifies (read before PRs 5+)

Pulled and rendered `PagesStandard.dc.html` + `ShellPageBody.dc.html` against our
shipped CSS. Our component layer renders the spec's own markup correctly — edge
table, `.stat-card`, `.alert`, `.card`, `.run-list` all match. What the prose
adds, and none of it is guessable from the CSS:

- **Launcher tiles are grouped** under `.section-label` headings (Build /
  Translate and compare / Deliver) *"rather than one flat wall"*. Today's Home is
  a flat wall.
- **A locked tile is a `<span role="link" aria-disabled>`, never an `<a>`**, so it
  cannot be followed, and *"copy says what unlocks it"*. Today's Home links a
  locked tile straight to `/login`.
- **The list archetype has four states, not three.** The fourth is
  **filtered-empty**, which offers "Clear filters" instead of "New" — *"because
  'no templates yet' is a lie when 18 exist."* Worth adopting everywhere.
- **The loading state is skeleton cells under the real header**, so column widths
  do not jump when data arrives.
- **Card view keeps the `.status-pill`** — *"a card has no shared edge to line
  up"*. Only table and list *rows* take the edge treatment.
- **Run history is a real `.data-table`**, *"not a bespoke row layout, so it
  sorts, filters and scans like every other table in the toolbox."*
  `.run-list` / `.run-row` are only for *"card-like histories that are not
  tabular"*. This changes [#528](https://github.com/mtaanquist/ALDevToolbox/issues/528):
  the div-based `.hist-*` / `.del-row` lists should most likely become tables,
  not `.run-list`.
- **Dashboard**: cue tiles carry a "last activity" line so *"a count always says
  how fresh it is"*; the "Needs attention" list uses `.activity--edge`, while
  *"Recent activity stays unstatused — those rows are history, not work items."*
- **Archetype per tool**: Launcher → Home. List → Templates, Cookbook, Projects,
  Pipelines, Releases, Admin Users. Detail → project / pipeline / release pages.
  Dashboard → Admin and Site-admin landings. Object Explorer, Translator, Compare
  and Piper are power tools with their own archetypes, not covered by this sheet.

**Caution: the prototype's own markup is not always consistent with its prose.**
`ShellPageBody`'s top pipelines table still uses `.status-pill` inside `<td>`,
which its own "Status placement, one rule" section forbids; a couple of its rows
are malformed (one `is-failed` row labelled `aria-label="Succeeded"`, one row
missing its state cell, one with a stray trailing `<td>`). Where markup and
prose disagree, **follow the prose** — and the corrected "Recent runs" table
lower in the same file, which does it properly.

### What `PageList.dc.html` adds on top of the prose

The archetype's own screen file is more specific than the spec column, and three
of its details were missed on the first pass at Templates:

- **Both empty states sit inside a `.card`.** A bare `.empty-state` floats on the
  page background with nothing holding it; the handoff always wraps it.
- **The skeleton table is paired with a `.loading-block` caption** underneath —
  spinner plus "Loading templates...". Shipped as `.loading-block--under-table`,
  which is only the padding trim the handoff does with an inline style.
- **`.empty-state__title` / `__text` are `<span>`s, not `<p>`s.** `.empty-state`
  is a grid with its own `gap`; a `<p>` brings the UA's `1em` margins and the
  block visibly loosens.

Also in the file but *not* adopted, and why:

- The filter bar carries `.select-wrap` filters, `.pill-tabs` with counts and a
  `.view-switch` (table ↔ cards). All optional — the spec says the switch
  "toggles table vs cards **per tool**". Templates has no second view; Cookbook
  took the `.pill-tabs` (its type filter maps onto them exactly, counts included)
  but stayed cards-only.
- Row actions are a `.ra` kebab. Templates keeps "View" and "New workspace" as
  visible `.btn--sm` links: hiding a page's main verb behind a kebab is the one
  place the prototype would be distinctly worse for us.

## Gotcha: "the old rules still win" is only true property by property

The whole migration rests on load order — `components.css` before `base.css` /
`tools.css` / `admin.css`, so an unmigrated page's own rules override the new
component rules until the old ones are deleted. That holds **per property**, not
per rule. Every property the new rule sets and the old one doesn't name still
applies, to whatever element happens to carry that class.

It bit hard once already. `.ra__menu` is the *popup* in the design system
(`position: absolute; top: calc(100% + 4px); right: 0; display: none`) and the
`<details>` *wrapper* in this app. `tools.css` overrode `position` and `display`
— enough that the menu still worked — but `top` leaked onto a `position:
relative` box and pushed **every kebab in the app** down by its own height, from
the moment the component layer landed until the Pipelines port happened to put
one in a table cell and someone looked. Six more colliding classes are listed in
[#537](https://github.com/mtaanquist/ALDevToolbox/issues/537).

So: when a component family is only *partly* migrated, check the whole property
set on both sides, not just the ones you meant to change. And prefer renaming
the app's class to the design system's meaning over patching an override — the
override is a holding action, the rename is the migration.

## Gotcha: interactivity is opt-in per page

The list archetype's filter box is the first piece of the redesign that needs
*behaviour*, and it silently did nothing at first. Blazor interactivity in this
app is opt-in: `Program.cs` calls `AddInteractiveServerRenderMode()`, but a page
only becomes interactive when it declares `@rendermode InteractiveServer`.
Without it a page still renders perfectly — `@oninput`, `@onclick` and `@bind`
just never fire, and nothing warns you.

**A screenshot cannot catch this.** The page looked right; only driving the
filter and asserting on the result did. Every archetype with a filter bar, view
switch, sort header or "Clear search" needs the directive, and its PR needs an
interaction check, not just a picture. `CookbookBrowser.razor` is the existing
example.

## Divergence register

**The default is fidelity.** The handoff's screens are worked-through
end-results, not first drafts, so implement them as specified and take a
divergence only where the handoff would be *distinctly worse* in our app —
never because something was easier to reuse or quicker to build. Every
divergence lives here with its reason, so it can be overruled in one place.

| # | Where | What the handoff does | What we do instead | Why |
| --- | --- | --- | --- | --- |
| 1 | `--font-sans` | Bare Segoe UI stack, no web fonts | Segoe → Selawik → `system-ui` | Segoe cannot be self-hosted, and the bare stack fell to Tahoma/Helvetica off Windows. **Pushed upstream** — no longer a divergence. |
| 2 | `--blue*` legacy aliases | `--primary` | `--primary-ink` | `--primary` is 2.5:1; these aliases feed 40 `color:` sites. Migration scaffolding only, deleted as tools migrate. **Pushed upstream.** |
| 3 | `a { }` in `components.css` | `color: var(--primary-strong)` | ~~Rule dropped~~ **Restored at `--primary-ink`.** | **Resolved.** `--primary-strong` measures 4.4:1 on `--bg` and 4.26:1 on `--primary-weak`, both under AA; `--primary-ink` is 6.44 / 6.25. Dropping the rule outright was wrong for the *design system*, whose spec sheets render links against `components.css` alone — the fix was the colour, not the deletion. `base.css` still wins in the app (it loads later). **Pushed upstream.** |
| 4 | `.btn--loading` | Blanks the label, draws a bare `::after` spinner | ~~Our two-span swap only~~ **Both. Pushed upstream.** Markup with a `.btn__label-busy` swaps to it; markup without one gets the handoff's spinner, at full width | **Resolved.** Rendering the handoff's own sheet against our CSS showed its loading buttons collapsing to empty boxes — our version had quietly broken its markup contract. Now additive rather than a divergence. |
| 5 | `.btn--lg`, `.btn--disabled`, `.status-pill--inline` | Not present | Carried over from the app, expressed on tokens | The app uses them; the handoff simply has no equivalent. Additive, not a contradiction. **Pushed upstream.** |
| 6 | `.data-table` | `overflow: hidden` | Dropped; the two header cells take the radius instead | The sheet puts a row-actions kebab *inside* the table (`.data-table__actions` + `.ra`), and `overflow: hidden` clipped its menu — every table's bottom rows lost the end of their menu. The overflow was only clipping the header fill to a 2px radius, which rounding the header cells does just as well. A contradiction in the handoff rather than a preference. **Pushed upstream.** |
| 7 | `.gen { --sticky-head }` | `132px` | `0px` | The variable offsets the sticky preview aside by the height of the handoff's sticky page header — which comes from its `shell.css`, a file we never adopted, so `.page-head--sticky` does not exist in this app. Nothing overlaps the scrollport here (our top bar sits *outside* `main.content`, the actual scroll container), so the clearance is zero. Left at 132px it pushed the preview 57px below the first form section **at rest**, because sticky clamps an element to its top offset even at `scrollTop: 0`. Restore the handoff's value if we ever ship a sticky page head. |
| 8 | Row-editor cells (`.row-editor__table .input`) | A bordered control in every editable cell at rest | No chrome until hover or focus — **except the trailing ghost row**, which keeps the border | The hand-off's rule was drawn for a short `.sub-rows` list. Ours are five-column spreadsheets (catalogue, application versions), where 5 borders × N rows is a wall of boxes. The cost of going quiet is that the grid reads as read-only, so the one row that *must* look editable does: the ghost row is where you add an entry and it carries the chrome the data rows don't. Reconsider if a page ever ships a *short* row list on this component. |
| 9 | `.tgrid { --tg-cols }` | `84px` key column | `200px`, clipped from the *left* (`direction: rtl`, the idiom `.crow__name` already uses for file paths) | Data, not preference. A BC XLIFF id is `Codeunit 1465371914 - NamedType 1138880009`; at 84px every row in the grid read `Codeunit ...`. The sheet declares `--tg-cols` on `.tgrid` precisely so a page can re-declare it, and the trailing segment is the half that differs between neighbouring rows. |
| 10 | `.trow` columns | Six tracks, the sixth hover-revealed row actions (`.trow__acts`) | **Seven tracks**: the sixth carries the unit's **kind** (Label / Tooltip / Caption), the seventh is `.trow__acts` | **Resolved in PR 18e.** Kind is the one attribute the grid otherwise dropped, so the actions track is appended rather than swapped in. The action is **Clear this translation** ([#560](https://github.com/mtaanquist/ALDevToolbox/issues/560)) — the state picker can move a unit between states, but nothing could undo a target filled by mistake. Clearing moves the state to to-do with it: an empty target *is* untranslated, and leaving it as "Translated" would hide the row behind the To-do filter. "Copy source into target" was the other candidate and was declined — a caption that needs no translation can be locked instead. |
| 11 | The focused editor rail (list view) | No counterpart | Ported onto the tokens under its own names (`.tr-urow`, `.tr-srcbox`, `.tr-statepick`, `.tr-sugg`) | Same call as `.folder-editor` and `.hint-details` in PR 8c. The handoff's archetype 9 is one grid; our Translator also has a one-unit-at-a-time view with translation-memory suggestions and voting, which the handoff's screens have no equivalent for. Additive, not a contradiction. |
| 12 | `.codev` | A hand-rendered div-per-line grid with its own `.k` / `.t` / `.s` token classes | CodeMirror 6, themed from the same `--code-*` tokens | Decided up front with the maintainer (see PR 14's decisions above). `.codev` would trade selection, find-in-file, virtualised scrolling and the click-to-find plumbing for pixels. The *palette* is ported faithfully; only the renderer differs. `.codev` stays in the sheet, unused — but **its six token classes are no longer only its own**: #587 made `.k .t .n .s .c .o` the app's one static-code vocabulary, on `.code-block pre`. That is the same names against the same tokens in two components, which is ordinary CSS; the defect #587 closed was two *different* vocabularies, not two hosts for one. **That palette claim was false until PR 14d** - the CodeMirror diff tints, change-bar gutter and overview ruler carried nine hard-coded `rgba()`/`rgb()` values while `--diff-*-bg` sat unused, so one screen drew two different yellows for one meaning and neither followed the theme. True now. |
| 13 | `.ftabs` / `.ftab` / `.ftab--dirty` | An open-file tab strip with close buttons and an unsaved-changes dot | Panes, one file at a time | Decision 1 above. A tab strip is a session model, not CSS, and the dirty state it is built around cannot exist in a read-only viewer — the prototype's own toolbar badge says the pane is read-only. Ported but unused; tracked in [#549](https://github.com/mtaanquist/ALDevToolbox/issues/549). |
| 14 | `.pane__sec-h` naming a symbol | Uppercased micro-label, target name included (`text-transform: uppercase`) | Label stays uppercase; the name goes in a `.sv-sec-name` span at `text-transform: none`, in the mono face | The idiom is right for a category ("FIELDS", "PROCEDURES") and wrong for user data: `GETLEGALENTITYNAME` throws away the camelCase that makes an AL identifier readable. The handoff's own sample (`References to BlockCustomer`) has the same problem and gets away with it only because the sample is short. |
| 15 | `.pane__sec-h` on the outline | A static `div` | A `button` with a caret, collapsing its section | An outline with eight sections (object, procedures, local procedures, triggers, labels, events, using, used-by) has to fold. The caret idiom is not invented — it is `.refgrp__h`, on the same screen. |
| 16 | `.refhit__c` | The source line, truncated from the right | Elided from the *left* when the marked name would otherwise fall past the ellipsis | Data, not preference — the same call as divergence 9. A real reference sits at column 60 of `Message('Posted %1 for %2', DocumentNo, SalesHeader.GetLegalEntityName());`, so right-truncation drops the one token the row exists to show. |
| 17 | `.symcard__doc` | A prose line under the signature | **Resolved in PR 19e** — rendered | Was flagged, not silent — [#561](https://github.com/mtaanquist/ALDevToolbox/issues/561). The gap was upstream of the card: nothing put XML doc comments into `oe_module_symbols`. The source extractor now captures the `<summary>` above a declaration and `oe_module_symbols.doc` carries it. Still null for most rows — an undocumented declaration, or a module imported symbols-only — so the card appends the line only when there is text rather than reserving space for it. |
| 18 | Inspector head | Two pill-tabs (Outline / Refs) | Three pill-tabs (Outline / Refs / Find) plus a separate icon button for shortcuts | Additive. Find-in-file is a real third view in this app and the handoff has no equivalent; the shortcut reference is read once rather than switched between, so it gets an affordance rather than a fourth pill in a 264px rail. |

| 19 | `.orow__type` | Always a type (`Code[20]`, `Boolean`, `trigger`) | The type when one is known, else the row's line number — and on a Uses / Used-by row, the module the target lives in | Data, not preference. Only the symbol-package importer fills `ReturnType`; the source-text extractor captures the parameter list and nothing else, so for most procedures there is no type to show. An always-empty column is worse than a slightly different one, and the line number is what the row carried before the port. |
| 20 | Reference group headers | Grouped by file (`SalesPost.CodeunitExt.al`) | Grouped by source object (`CRONUS Sales Post Handler`) | Pre-dates the port — `groupByObject` is how the panel has always clustered. An AL developer navigates by object more than by file, and one object is usually one file anyway. Recorded rather than changed because it is a data-shape decision the handoff's sample cannot settle. |
| 21 | `.pane__count` on the references heading | `7 in 3 files` | The bare total; the long form is the chip's `title` | The rail is 220px at its narrowest and the heading already carries the target name, which is the part that cannot be abbreviated. The group headers below spell out the distribution. |

| 22 | `.orow__glyph` | `#` field, `f` procedure, `t` trigger — one per row | The same three; **blank** for every other kind | Not a divergence in the component, only in how far it is stretched. The port first extended the vocabulary to eight characters and a fresh-eyes review found the five additions undecodable, which they were. The column is kept for the two jobs the glyph does besides spelling a kind: it is tinted, so the row reads as colour at a glance, and it holds a fixed gutter that aligns the names. Kinds outside the handoff's three draw nothing and are named by the section header and the row's `title` instead. |
| 23 | `.symcard__acts` | Two `.btn btn--sm` buttons, each with its shortcut as `.kbd` chips inside the label | Bare text links (`.symcard__act`); the shortcuts moved to the context menu that performs the action | Raised by the maintainer from a real session (PR 20f-20g). A tooltip that appears on hover is not a place for button chrome — the two actions are navigation, and a button's padding and border were most of what made the card overflow. The chips taught a shortcut that is learned once and then rides every hover forever; Code and Zed both put keybindings in the menu, where they are read at the moment they are useful. **The sheet is pushed upstream** (`.symcard__acts` is a flex row now and the `.btn` / `.kbd` overrides are gone), so this divergence is in the handoff's *markup*, which still renders buttons. |
| 24 | `.symcard__meta` third span | Visibility (`public`) as the last of three meta spans | Visibility joins the **declaration line** (`local procedure Foo(...)`); the meta row keeps module and `file:line` | Raised by the maintainer (PR 20h): AL writes visibility in front of the declaration, so the headline reads as the code you would type. It also removes the weakest of three items from a row that truncates all of them together, and the four values (`public` / `protected` / `internal` / `local`) are exactly the AL keywords. `declarationOf()` composes it from the symbol kind rather than the card concatenating fields. |

| 23 | `.okind` letters | `TB` table, `CU` codeunit, `RE` report (and `TE` / `PE` for the two extensions) | The app's own search prefixes, uppercased: `T`, `C`, `R`, `TE`, `PE`, ... | One alphabet instead of two. `te:` and `c:` are what the Object Explorer's search box already accepts, so the badge teaches the syntax rather than competing with it — and the handoff's own set collides with itself the moment you extend it, because `RE` is its report and `re:` is our report *extension*. `ObjectExplorerShellTests` fails if either list gains a kind the other lacks. |
| 24 | `.codev-foot` | `Ln 16, Col 15 - AL - UTF-8 - Spaces: 4 - runtime 13.0` … `Cust.TableExt.al - 35 lines - read-only` | The CodeMirror status panel in the same tokens, carrying `Ln, Col`, the containing procedure, and the line count | Two separate calls, and an earlier version of this row conflated them. **The renderer** differs for the same reason as divergence 12: the cursor position has to come from the editor's own state, so the bar is a `showPanel` extension rather than markup. **The cells** differ because five of the seven are not ours to draw: `UTF-8` and `Spaces: 4` are editor settings on a read-only pane (PR 14's decisions), the filename moved to `.pw__file` and `read-only` to the bar's badge, and `AL` / `runtime 13.0` are the only two genuinely dropped — the panel is shared with the compare panes and has no per-file metadata plumbed into it. Tracked as [#568](https://github.com/mtaanquist/ALDevToolbox/issues/568); the panel gains something the handoff has not got in exchange, the BC stack-trace-relative procedure line. |
| 25 | `.pw__head` search box | "Go to object or symbol..." with a `Ctrl` `P` hint | Not ported | Flagged, not silent — [#567](https://github.com/mtaanquist/ALDevToolbox/issues/567). Quick-open is a real feature and we should have it; what we should not have is `Ctrl+P`, which the browser takes (decision 2). The box without a working gesture is a control that does less than it looks like, so it waits for the `/`-or-`Ctrl+K` binding rather than shipping half. |
| 26 | Explorer pane head | Title, count, and a "Collapse all" button | ~~Title and count~~ **All three. Not a divergence.** | **Withdrawn.** The first version of this row said the icon was not vendored and there was little to collapse. The first half is a chore, not a reason — `chevrons-down-up` is one file and the csproj globs the folder. The second describes *first paint only*: this tree is lazy and stateful, so after a reader has opened five folders inside a Base Application module there is a great deal to collapse, which is exactly the screen where the button earns its place. Same shape as the two PR 14a findings — an argument about what the port currently looks like standing in for an argument about the component. The count is labelled (`2 apps`) rather than bare, because our pane lists a release's apps where the handoff's lists one app's objects. |
| 27 | `.otree` roots | Apps installed alongside the open one (`Base Application`, `System Application`, ...) | The modules of the *release* the file belongs to | Not a divergence in the component — the same rows, drawn from the hierarchy this app actually has. A release *is* the set of apps that shipped together. |
| 28 | `.otree` file rows | Objects, named by object | Files, named by their object where they have one | Our route is per file and a file is what the viewer opens. In AL these coincide almost always (one object per file); where they do not — `app.json`, a permission XML — the row keeps its file name and draws the generic file icon rather than a badge it cannot fill. The file name is on the row's `title` either way. |

| 29 | `.pw__bar` | Pill-tabs, a package `<select>`, a "Only objects with source" checkbox | A breadcrumb (`.sv-crumbs`), the compare picker, the read-only badge | The handoff's bar controls *what the screen lists*, which belongs to the release and module pages (PR 14c), not to a single open file. Ours carries the controls a file view actually has. The breadcrumb has no handoff counterpart at all — `tools.css` even said so in a comment, which is a register entry filed in the wrong place. |
| 30 | `.pw__head` actions | "Copy object" and "Open in VS Code" | "Download source" | "Open in VS Code" has no target (PR 14's decisions — the source came out of a compiled `.app` in our database, there is no file on the reader's machine). "Copy object" would need a definition of what gets copied that the prototype does not give. Download is the action this page has. |
| 31 | Read-only badge copy | "Read-only - symbols come from the compiled .app" | "Read-only - the source as it shipped in this release" | Same badge, corrected subject: we render source, not symbols, and it can arrive from a `.Source.zip` or a project build as well as an `.app`. |
| 32 | `.otree__id` | The object's AL id | The object's AL id on a file row; the app's **version** on a module row | The column is the row's identifying number, and a module's identifying number is its version. The handoff's own tree does the same thing — its app rows carry `24.0` in that slot — it just never says so. |
| 33 | `.otree` folder size | Every child, always | At most 400 files, then a row naming how many are left | The legacy C/AL ingest slices every object of a kind into one folder, so a real module puts ~2,000 tables in `CAL/Table/`. That is 2,000 `<a>` elements server-rendered into the page response for anyone who opens a file in it. The cap is stated on screen rather than silent, and search reaches what the tree does not. |
| 34 | `.pw__bar` read-only badge | "Read-only - symbols come from the compiled .app" | Dropped | Maintainer's call after seeing it on real data: the viewer is never going to be an editor, so a badge saying so answers a question nobody asks. The compare picker took the slot. Row 31, which recorded the corrected wording, is superseded by this. |
| 35 | Explorer pane head | Title, count, "Collapse all" | Also a tree/flat toggle, a search box on its own row, and a show/hide control in `.pw__head` | Additive, and the tree at real scale is why. A BC release is 86 apps and a Base Application module is thousands of files: a folder tree you can only expand is not navigable at that size. Search crosses the release (the tree only holds what has been opened, so filtering the rows on screen would search a handful of apps out of eighty-six); flat mode drops the folders for when you know the object's name; the show/hide control replaces the media query that used to fold the pane away with no way back. |
| 36 | Object-kind classes | n/a (the handoff has no object list) | `otype--page`, never a bare `otype page` | Not a preference — a bug with two faces. The AL kind names are ordinary English words and several are already classes here: `.page` in pages.css is the page-layout container, `display: grid` with `container-type: inline-size`. `<span class="otype page">` inherited both, and the two symptoms were reported months apart as separate bugs: first the badge filling its cell (a grid box is block-level), then, once `display: inline-block` was set and won on specificity, the badge collapsing to 18px, because `container-type` makes an element a size container whose inline size is computed *without* its contents. Only `page` ever showed it. |
| 37 | Explorer arrangement | The folder tree, and only that | A **Group by** control: Folder, Object type, or none | A vendor's folder layout is somebody else's filing system, and a reader of an app they did not write usually knows the *kind* of object they want rather than the folder it was filed in. Folder keeps the apps around it (it answers "where does this live"); the other two are one app's files and nothing else (they answer "what is in here"), with the search box and switching back to Folder as the way across apps. The choice rides in a cookie so the server renders it — restoring it client-side flashed through the folder view on every navigation. |
| 38 | Inspector head | Two pill-tabs | Three pill-tabs, and **no** shortcuts button | Supersedes row 18, which added the `(i)`. Once `.pw__foot` carried the key hints there were two places saying the same thing, and the panel was the one that could not adapt to the reader's platform — the foot rewrites Ctrl to Cmd on a Mac, the panel spelled out "Cmd/Ctrl" forever. The two right-click gestures it documented moved into the foot's own line. |
| 39 | Scope selector | n/a (the handoff's bar has Objects / Symbols / Dependencies pill-tabs) | The same `.pill-tabs`, carrying Objects / Procedures / File content / Compare | Row 29 said the handoff's `.pw__bar` belongs to the release page rather than the file view; this is where it landed. The control decides what every other filter in the row *means*, which is a tab's job and not a `<select>`'s. Different four labels because they are the four searches this tool actually runs. The `Alt+1..4` bindings moved off the visible labels onto `aria-keyshortcuts` and the tooltip. |
| 40 | Object-type filter | A single-value `<select class="select">` | A `<details>` disclosure, several kinds at once | Multi-select is the requirement — "tables and table extensions" is one question — and no native control expresses it. The summary wears `.select` and the panel is a `.menu` with `.check` boxes, so it reads as one of the row's dropdowns rather than as a bespoke thing that looks nearly like one. Only the open state, the panel placement and the scroll cap are ours. |
| 41 | Object kind in a grid cell | n/a (the handoff's list archetype renders a type as plain text) | The `.okind` badge **and** the word, in the **two object grids** | Plain text is right for `PageList`'s four types; ours has seventeen, and the tree one pane away already spells them as badges. The two grids that list objects — `OeObjectResults` and `OeModuleDetail` — go through `OeKindCell` so they cannot drift again; they had, one carrying a tinted pill and the other a bare word. The word stays because a column headed "Type" has the room and because the legacy C/AL kinds have no badge at all. **Scope, stated so it is not mistaken for more:** a kind still renders as a bare word in the object-compare grid, the procedure grid's object cell, and the object detail page's meta row and references table. Those are not grids of objects-by-kind and the badge would be decoration there — but nothing enforces it, and the test pins only the two grids. |
| 42 | `--bar-removed` | n/a — the object-diff family is new / modified / unchanged | Adds `removed` | Half a diff. Our release comparison produces `added` (the same state under the word the comparer uses) and `removed`, which had no keyline to take. Added upstream as an alias of `--bar-failed` — a thing that is gone reads red — rather than reusing `is-failed`, whose name would lie on a diff row. |
| 43 | `.data-table--edge` | The status treatment: a 4px right-edge keyline driven by an `is-*` class, paired with a leading `.data-table__col-state` glyph | Only on tables that **have** a row state | Not a divergence, a correction to this port. Six of 14c's tables took `--edge` with no state column and no `is-*` on any row, which buys a permanent 4px transparent gutter that can never carry a signal. `RowStateIcon`'s own doc says the three parts only make sense together. The two compare tables keep it; the rest are plain `.data-table`. |
| 44 | `.pill-tab__count` on the scope tabs | The handoff's bar counts its tabs (`Objects 1284`) | No counts | Three of the four scopes have no count to show until a search has run — Procedures and Source text are query-driven and Compare has no number at all. A count that appears on one tab and not its neighbours reads as the others being broken. |
| 45 | Filter-bar order | search → selects → spacer → pill-tabs | pill-tabs → search → selects → spacer → clear | The scope tabs decide what every other control in the row *means*, so they come first and read as the row's subject. The handoff's tabs filter a list the controls to their left have already scoped, which is the opposite relationship. |
| 46 | Per-file `+`/`-` in the change rail | Every rail row carries its own `+62 -0` | Added and removed rows carry theirs; **modified** rows carry none | **Narrowed after the fidelity pass, which caught the original reason overclaiming.** An added file's `+62 -0` is its own length and a removed file's `+0 -19` likewise — exact, already fetched, and being thrown away at the `Select`. Only a *modified* pair needs a split we do not have, and inventing one from the two line counts would put a number on screen that looks measured and is not; getting it honestly means diffing every file in the app to draw a sidebar. |
| 47 | What the change rail lists | The whole change set | The two apps' changed files, capped at 500 | A Base Application diff between two BC releases runs to thousands of files, and rendering them costs more than the diff. The cap is **said**, not silent: the pane count reads "500 of 2,140" and the list ends in a link to the Release page's Compare scope, which pages properly. Below the cap — a customer extension, the common case — the rail is the whole change set. |
| 48 | What the ref chip holds | A commit sha | The release label; the app name sits outside the chip, on `.vs__name` | A release is what differs between the two sides — the sha's job. The app is normally the same on both, and differs only when an object-level compare lines up two separately ingested apps, which is exactly when you want to see it. |
| 49 | `.pw__head` actions | Swap sides, Open in Piper, Create pull request | Swap sides | Nothing in this app corresponds to the other two. |
| 50 | Side-by-side / Inline, Ignore whitespace | Two `.pill-tabs` and a `.check` on the bar | Neither | Not look, behaviour: both are new capability rather than a visual detail to preserve. Inline needs a third mount path in `source-viewer.js` (the pane pair is two independent editors); ignore-whitespace needs the flag threaded through both the SSR diff and `/api/compare/diff`. Filed as [#576](https://github.com/mtaanquist/ALDevToolbox/issues/576) and [#577](https://github.com/mtaanquist/ALDevToolbox/issues/577) rather than half-built. |
| 51 | `.cmp__pane` | The scroll container for a block of rendered `.diff__ln` rows — it owns the scrolling and the mono type | `.cmp__pane--host`: keeps the divider and the min-width, drops the overflow and the type | Structural, not visual. Our pane hosts a CodeMirror instance, which owns its own scroller, its own font and its own status bar; leaving the handoff's rules on would give the column a second scrollbar and a font the editor immediately overrides. |
| 52 | "Objects affected" `.pane__sec` under the rail | A second section listing the objects the change touches | Not rendered | For a *file* diff that section is the file's outline, which the single-file viewer already gives one click away. It would be a list of the same three names in a narrower box. |
| 54 | Word-level `mark` | The line's own background plus a `--bar-draft` underline | The underline, over a fill `color-mix`ed from the same two tokens | The handoff's treatment is legible on its sample's proportional text and disappears in a monospace code pane, where the underline lands under a row already tinted the same colour. Mixed from `--bar-draft` and `--diff-chg-bg` rather than the hard-coded rgba it used to carry, so it still follows the theme. Checked at 3x in both themes. |
| 55 | `.hunk` collapse | Six `@@ -24,8 +32,14 @@` separators; only changed regions and their context are rendered | The whole file, both sides, with an overview ruler and next-change navigation | Ours are two CodeMirror instances over the complete documents, not a rendered list of hunks — which is what makes go-to-definition, find-references and the outline work on a diff pane at all. Collapsing to hunks means folding ranges and a `@@` block widget. Not built, and **not** silently dropped: [#579](https://github.com/mtaanquist/ALDevToolbox/issues/579). |
| 53 | `.crow__stat` buckets | `+` and `-`, git's two | `+`, `-` and `~` | A line differ reports three states, not two, and folding `modified` into either of the other two makes the stat disagree with the summary beside it — which is exactly what a fresh-eyes pass caught. `.crow__mod` pushed upstream. |
| 56 | `.modal-backdrop` / dialog placement | The backdrop is `position: absolute` inside the review frame each dialog is demonstrated in (`.overlay-demo`), rounded to that frame's corners | A `.modal-layer` wrapper: `position: fixed; inset: 0; z-index: 50; display: grid; place-items: center`, with the backdrop's radius reset to 0 inside it | Not a disagreement — a gap. The system's screens never show a dialog over a *page*, so nothing owns "centre this in the viewport" and the backdrop has no fixed parent to resolve `inset: 0` against. Left as-is it would dim the dialog and nothing else, and round the corners of the screen. **Pushed upstream**; the prototype's own `.overlay-demo` keeps its rounded corners because the reset is scoped to the layer. |
| 57 | `.confirm-dialog` width | One width, `min(420px, 100%)` | Plus `.confirm-dialog--wide`, `min(560px, 100%)` | Three of our dialogs carry a list rather than a yes/no prompt — an extension checklist, a release picker, a build picker — and 420px wraps every row. The legacy family had the same variant (`.confirm-modal__panel--wide`) for the same three callers. **Pushed upstream.** |
| 58 | `.confirm-dialog__body` | One block of prose | Plus `> p` margins | Our bodies stack two or three paragraphs (a description, then a caveat, then a count); the handoff's only ever holds one, so nothing separates them. **Pushed upstream.** |
| 60 | `.pill-tab__count` | Centred as a box beside the label | Nudged down 1px | 11px mono beside a 13px sans label: centring the two BOXES leaves their baselines apart, the mono ascent lifts the digits, and "Microsoft 6" reads as Microsoft-to-the-sixth. The system has the same flaw on its own screens. **Pushed upstream.** |
| 61 | `.data-table__num` / `__actions` / `__col-state` / `__col-check` | Bare modifiers, (0,1,0) | Scoped under `.data-table`, (0,2,0) | Not a divergence so much as a fix: `.data-table th, .data-table td` sets `text-align: left` at (0,1,1), so none of the four had ever applied, on any screen including the system's own. **Pushed upstream.** |
| 62 | The detail-page head | `PageDetail.dc.html`: crumbs as a sibling *above* `.detail-head`, the state pill beside the title in `__title-row`, the facts in a full-width `.meta-row`, no rail, no tool glyph | The same | **Not a divergence.** Kept in the register because three visible things left the page — `.det-pico` (a 50px tinted tool icon), the per-item glyphs in the sub-line, and the owner's initials chip — and someone will ask. 15c reached this answer from the wrong screen (`ComponentsPanel`, a *list* head) and 15d built a rail the archetype does not have; both were reworked once `PageDetail.dc.html` was vendored. |
| 63 | `.hunk` banner | A full-width band across the pane | A band across the code area; the line-number gutters beside it stay blank | The banner is a CodeMirror block widget, so it lives inside `.cm-content` — which begins *after* the sticky gutters and cannot be painted over without out-specifying CodeMirror's own `z-index: 200`. It reads the way GitHub's hunk rows do. Invisible in dark, where the gutter and the band share a background. |
| 64 | Hunks in side-by-side | Both layouts render as hunks | Inline only, for now | Side-by-side has to *fold* the unchanged runs in two panes and keep the fold ranges in step across them; unified simply does not emit them. PR 16a's geometry rework is what makes the folding version possible at all (`lineTop` stays right through a fold) — tracked as the open half of #579. |
| 65 | The Compare *tool*'s layout tabs | Both compare screens carry them | Object Explorer's file compare only | The tool's panes ARE the input — you paste into them. A unified document is one read-only pane, so switching to it would take the text-entry surface away mid-task. Needs its own answer (tabs that appear once both sides have text, inline as a read-only result view); filed rather than half-built. |
| 66 | The run past the last hunk | Not shown — the sample file ends on its last change | A band reading `... 6 unchanged lines` | Something has to stand there or a 3,000-line file with one change at the top still renders 2,990 lines. It cannot be a `@@` banner: there is no hunk below it to announce. Clicking it brings the lines back, like every other band that hides something. |
| 67 | Bands that hide nothing | Identical to the ones that do | No chevron; the ones that toggle have one | ~~Two visually identical bands where only one is a control is a real wrinkle; it is narrow, the cursor and hover state separate them, and inventing a marker the handoff does not have would be worse.~~ **Reversed by the design review.** Hover and focus only reach a band the reader has already committed to, so they were never the affordance — at rest the two were the same grey strip. See row 68: the chevron the toggle needed anyway is what separates them, so the mark is not invented for this. |
| 68 | Toggling bands | No chevron — the handoff's `.hunk` is a separator, not a disclosure | A leading chevron, rotated 180° when the lines are showing | The handoff drew a band that *announces* the code below it. PR 16c gave it a second job — hiding lines and putting them back — which the handoff never drew, and a disclosure control has to show its state. The band's own text cannot: it names the code below it, so an expanded band still read `... 6 unchanged lines` above six visible lines. The chevron hangs off `aria-expanded`, so what a screen reader announces and what the sheet rotates cannot drift. It also settles row 67 and the dead inline banners in one mark. |
| 69 | `@@ -12,8 +13,10 @@` | The handoff's separator text, verbatim | The same | **Not a divergence.** The design review called it git jargon a BC consultant cannot read, and would have replaced it with `Show N unchanged lines`. Maintainer's call, kept: the jargon rule is for captions around the site, and a diff pane is a code surface where this vocabulary is the reader's own. Recorded so it is not re-litigated — the argument against it is real, it just loses here. |
| 59 | `.menu__item` | Always a `<button>` | Also an `<a>`, with `text-decoration: none` | Half our row-action entries navigate ("View source", "Download source", "Project settings"), so they are anchors and arrived underlined. The system's screens only ever demonstrate buttons, so nothing had turned it off. **Pushed upstream.** |
| 70 | The detail body | Single column - `PageDetail.dc.html` has no rail at all | An **optional** rail (`.detail-body__aside`), 280px, sticky, collapsing at the same 1080px as `.gen` and `.settings__body` | Raised by the maintainer from a real session. The archetype is right about the *facts*: PR 15d dissolved this page's rail of cards into the `.meta-row` and that half stays. What the strip could not absorb were the two sections that are not facts - the repositories a build was cut from, and the compare picker - which then had nowhere to go but the bottom of the main column, under the build log, the one section with no upper bound on its height. Measured on `/pipelines/5` with a short log and no compare card rendering at all, "Repositories" began 1052px down a 1000px window; it is at 288px now, and the page is 92px shorter despite gaining a section. Business Central puts exactly this class of content in a FactBox beside the card's fields, which is what the page did before the port. Optional is the load-bearing word: whether the rail has anything in it is a *per-record* question here, so an empty one falls back to `.detail-body--wide` rather than reserving 280px, the same escape `.settings__body--wide` already provides. |
| 71 | Section headings on a detail page | A bare `.state-label` above a bare `.data-table--edge`; no `.card` anywhere on `PageDetail.dc.html` | The same table and the same markup, inside a `.card` - via the new `.card--flush`, so the card's border is the only one drawn | Raised by the maintainer looking at the finished page. Ours mixes cards (Latest build, Build log, Compare builds genuinely have a head *and* a padded body) with archetype-faithful bare sections, and side by side that is four sections in three heading treatments. Each was individually defensible - PR 15d skipped the card on Repositories and Build history precisely because `.sub-rows` and `.data-table` carry their own surface, and nesting one in a padded `.card__body` draws a box inside a box - which is the tell that the missing piece was a third option rather than a preference between the two. `.card--flush` is it. The archetype is still right that these are containers, not contents; it just never had to put one next to a card. |
| 72 | `.steps` measure | `max-width: 82ch` on the step LIST; `PageMcpSetup.dc.html` caps its trailing note to match | The list opts out (`.steps--wide`) and the 82ch moves to `.step__text` | Raised by the maintainer: the page did not use its width. 82ch is a *reading* measure and the archetype's own steps hold code blocks and inputs, so it is defensible - but ours additionally hold a two-up card row the archetype never had. At 1760px that came to 745px of content beside 715px of nothing, the two cards crushed to ~265px each, and `https://aldevtoolbox.taaken.dk/mcp` wrapped across three lines inside a card whose page CSS already carried a comment explaining that a real server address does not fit. The measure was never wrong about prose; it was on the wrong element. `.note` also gains the 82ch the handoff spells inline on its own trailing note. |
| 73 | `.docs` grid | `minmax(0, 1fr) 216px` - the contents pinned to the right edge | `minmax(0, 74ch) 216px` with `justify-content: start` - the contents beside the article | Flagged by two design reviews, left alone both times, then raised by the maintainer. The note that closed it twice argued the measure, and the measure is right - re-checked in the browser: a body paragraph is 14px and exactly 74 characters fit the 558px line. (A first re-measurement said 65 and was wrong; it had measured `.prose__lead`, which is 16px on purpose.) The measure was never the complaint. `1fr` lets the first track absorb every spare pixel, so at the maintainer's 1760px a 1216px track holds 558px of text and leaves a **658px hole** between the article and its own contents list - wider than the article. The earlier reviews were looking at a narrower window where it is ~340px, which is how "a docs layout looks like this" survived twice. Centring the pair instead is worse: the page title stays left and the body detaches from its own heading. |
| 74 | The sidebar's arrange mode (#956) | No counterpart: `ShellFrame.dc.html` draws one fixed order of groups and items | An **Arrange sidebar** ghost button in the foot's links row. It swaps that row for a status line ("Saved as you go, in this browser.", which becomes "Order reset." with an **Undo** after a reset, until the mode is left) over **Reset to default** (ghost) and **Done** (outline), and lays a strip of controls over the right end of every group head and top-level row: a `grip-vertical` handle as the drag cue - the whole row drags, by its name too - and **Move up** / **Move down** icon buttons, each named for its row ("Move Deliver up"). While arranging, every group shows its items, sub-pages stay in view dimmed and without controls of their own (they travel with their parent, which the design review found is how someone who came to move Environments sees where it went), and links and group heads are inert. Escape leaves the mode from anywhere on the page. Groups move among groups and items within their own group, never across. The rows are copied from one hidden set of controls by `wwwroot/nav-arrange.js` rather than rendered into every row, and the order rides on the `aldt-nav-order` cookie the server reads at render, beside `aldt-nav-collapsed` | Raised in #956, built without a sheet at the maintainer's call and made only from the sidebar's own parts - the controls are sized like `.nav-link-btn` and coloured like the group heads, all in `NavMenu.razor.css`. **Phones:** dragging under a finger is unreliable with native drag and drop, so on a coarse pointer the grip is hidden and the two buttons grow to 32px - they are the touch path, and the keyboard's. The icon rail (700-1080px) has no labels to arrange by and hides the button. **Needs a design pass upstream.** |
| 75 | `.note` and `.steps` width (#1723) | `.steps` capped at `max-width: 82ch`; `PageMcpSetup.dc.html` caps its trailing note to 82ch inline | Neither carries a `max-width`; `.steps--wide` and `SetupSteps Wide` are retired. `.step__text` keeps its 82ch | Raised by the maintainer: a note is a box in a page layout, not a column of prose, and once `.note` was used on list and tool pages (Environments, MCP, Translator, the copy-environment dialog) its 82ch made it stop halfway across the row, out of line with the table below. Supersedes the `.note` half of 72; the step-list half of 72 becomes the default instead of an opt-out. Prose keeps its measure where it sits: `.docs__main`, `.step__text`, the error page, empty-state and form captions. Patched in both checked-in copies; **needs pushing upstream to the design project.** |

**Upstream sync — done.** `components.css` has been pushed back to the design
project, so divergences 3, 4, 5 and 6 are now the design system's own text and a
re-sync will not silently undo them. The hold-up had been that pushing the file
wholesale would carry all four at once; the answer was simply to check each on
its merits rather than treat "several at once" as a reason not to. All four are
corrections or additions, not preferences:

- **6** and the `.check` native-input hiding are outright bugs (a clipped kebab
  menu; a styled checkbox rendered next to the real one).
- **4** restores a markup contract the handoff's own sheet was breaking.
- **5** is additive — classes the app needs and the handoff has no equivalent for.
- **3** needed *changing* before pushing, not pushing as-is: our copy had deleted
  the rule, which is right for our app (base.css owns links) but wrong for a
  design system whose spec sheets render against `components.css` alone. Pushed
  as `--primary-ink`, which fixes the contrast without removing the rule.

Divergence 7 is deliberately **not** pushed: 132px is correct for the handoff's
own shell, which really does have a sticky page head. That one is a difference
between two apps, not an error.

`pages-power.css` arrived whole in PR 13 and went back with two additions, both
corrections rather than preferences: `.trow` had no `cursor: pointer` despite
declaring an `.is-selected` state, and the row's status cell needed its own twin
of the `tr.is-*` rules that tint `RowStateIcon` in `components.css` — a `.trow`
is a grid, not a `<tr>`, so without them the glyph rendered grey next to a
coloured keyline. Divergences 9 and 10 are ours and were **not** pushed: 9 is a
re-declaration the sheet invites, and 10 is an app that has not caught up with
the spec yet.

PR 14a pushed three more, all corrections:

- **`[hidden] { display: none !important; }`** in `components.css`. Every
  component in that file sets an explicit `display`, so the HTML attribute had
  stopped working on all of them — the HTML spec's own suggested rendering uses
  exactly this rule for exactly this reason.
- **`text-decoration: none` on `.orow` and `.refhit`.** Both are drawn as
  `<button>` in the handoff and as `<a>` here, because a reference row has to be
  middle-clickable. `.btn` already carries the same line with the same comment.
- **`overflow-wrap: anywhere` on `.symcard__sig`.** A real AL signature is
  longer than the sample and ran straight out of the 356px card.

Divergences 12–18 are ours and were not pushed: 12, 13 and 18 are this app's
shape rather than errors, and 14–17 are data the handoff's samples do not have.


Anything not in this table should match the handoff. If you find something that
does not, it is drift — fix it toward the handoff.

## Appendix: the retirement log

`tools.css` spent the branch accumulating a comment where each rule used to be —
what retired, what it became, and which PR did it. The file is gone as of
PR 17e, so the notes live here. They are the answer to *"where did `.ftp` go?"*,
which is a question a `git log` over a deleted file answers badly.

- New Workspace / New Extension Both pages moved onto the design system's generator archetype (pages-forms.css .gen / .form-sec / .field / .tree), so the whole .workspace-page shell — its layout grid, its scoped form-input styling, its module cards, its import card and its sticky action bar — retired with them. .preview-card followed in PR 8c-3: both call sites (the template editor's aside and the always-included-files aside) are .card + .card--collapsible.
- .btn--lg retired: its only two call sites are the Generate buttons on the two generator pages, both migrated, so the design system's own large size (a --control-h-lg box rather than vertical padding) is the one to keep. The legacy rule was overriding padding and font-size while the component's height leaked past it -- see issue #537.
- The folder-tree preview moved onto the design system's flat .tree (pages-forms.css): rows indented by a --d custom property instead of nested lists, so the whole .ftp family retired with it.
- The mustache-variables hint moved to pages-forms.css as .hint-details in PR 8c-3.
- The read-only template detail moved onto the detail archetype in the PR 8 audit follow-up (#545): .page / .page-head with crumbs, .meta-row for the facts, .card per section, and .code-block for the two JSON blocks that used to be readonly textareas. .template-detail*, .kv-grid and the textarea sizing that went with them retired with it.
- The two-column editor layouts moved onto the generator's .gen archetype in PR 8c-3 -- same shape, one definition.
- .admin-template-edit__group-heading and __org-base retired in PR 8c-3: the structured form's sections are .card heads now, and the read-only organisation base is a .hint-details wrapping a .code-block.
- Pill tabs — LEGACY. The design system owns .pill-tabs / .pill-tab / .pill-tab.is-active in components.css; this block is the app's older route-navigation variant, kept until its three remaining call sites migrate (PillTabs.razor, Piper, AdminTemplateEdit). Every selector here is gated on .pill-tabs--legacy. Ungated, these rules were a live collision (#537): tools.css loads after components.css, so `margin-bottom: 16px` leaked onto the MIGRATED tabs and pushed them 16px out of alignment inside .filter-bar, while `button.pill-tab { padding: 6px 12px; font-size: 13px }` (0,1,1) quietly beat the design system's own sizing. Cookbook and the Object Explorer landing both had it. Delete this block with the last call site — do not un-gate it.
- The CodeMirror mount frame moved to pages-forms.css as .code-editor in PR 8c-3. TOML parse issues render as an .alert list.
- The folder / extension / dependency editors (.folder-editor*, .extension-editor*, .dep-editor__fields) moved to pages-forms.css in PR 8c-3, on design tokens.
- The row-table editor (.row-editor*) moved to pages-forms.css in PR 8c-2, on design tokens and leaning on the shared .data-table for the border, the uppercase head, the hover and the is-selected inset bar.
- .data-table--card - a card look for a table, WITHOUT overflow:hidden so an absolutely-positioned row menu is never clipped. This was the release page's own table treatment, scoped under .object-explorer__browser, and the admin Object Explorer's read-only tables opted into the same look through this modifier. The release page has since moved onto the design layer's .data-table, so only the admin tables render it; it retires with them.
- Shared leftovers from the retired Object Explorer landing CSS The landing page moved onto the list archetype (.page-head / .filter-bar / .pill-tabs / .card-grid / .browse-card) and its own rel-*, rh-*, rc-*, rg-* and src-tabs rules went with it. .rel-empty outlived them on the two Pipelines detail pages and went in PR 15c, onto .empty-state. What is left here is .cap-label, on Projects and Account; it retires with those.
- Browser page head + search toolbar Named for the Cookbook, which no longer uses them: the Cookbook grid moved onto the design system's list archetype (.page-head / .filter-bar / .card-grid). Projects and the admin Object Explorer index are the remaining callers, and these rules retire with them.
- ---------- Source-file viewer ----------
- IDE-style layout: page header pinned at top, code area scrolls inside the cm-editor, the outline is always next to it. The page itself doesn't scroll (height: 100% inside .content's bounded scrollport) — that's the pattern users expect from a code viewer. Narrow viewports drop back to the page-scroll behaviour because internal scrolling feels cramped on small screens. `:not(.pw)` because `.source-viewer` is two things at once: the JS hook source-viewer.js mounts on (every viewer surface carries it) and this flex column. The ported viewer is a `.pw` grid and would have had its `display: grid` overwritten from here — tools.css loads after pages-power.css. The only remaining consumers are the two compare panes - the legacy viewer this used to name went with #562. Note the pane's own `flex-direction: row` override has to carry two classes to beat this rule's (0,2,0) AND sit after it; both, not either.
- ── Inspector rail ────────────────────────────────────────────── The rail is the handoff's `.pane` (pages-power.css): a fixed head over a scrolling body, with each block of content in a `.pane__sec`. Its width is the `.oe` grid's --oe-right track, written by the drag handle in source-viewer.js; it carries no frame of its own because `.oe__right` already draws the border between it and the code. Everything below styles only what the handoff leaves to the page: the collapsible section header, the JS-driven panel switch, and the two components the viewer renders as links rather than buttons.
- A folder whose children failed to load. Sits where the children would have, says what to do, and is replaced by the real rows on a retry. `height: auto` is not decoration: the element carries `.otree__row` for the depth walk, and that class is a 24px box. Wrapped to three lines in a 236px rail it painted straight over the two rows below it.
- Section headers are collapsible here — the handoff renders `.pane__sec-h` as a static div, but an outline with eight sections needs to fold. The caret idiom is the one the same screen uses on `.refgrp__h`. Hung off `.sv-sec-h` rather than `.pane__sec-h` itself: redefining a design-layer class from a legacy sheet is what ComponentCollisionTests exists to catch (#537), and the extra class costs one word of markup.
- Outline right-click menu - one-item popover for Find references on a procedure / field / trigger / object row. Positioned by JS at the click point. NOT part of the retired pre-#161 vocabulary, despite the name: this menu is built by the LIVE source-viewer.js (see wireOutlineMenu). #562's retirement pass took it because the class shares the `source-viewer__outline` prefix, and `Every_element_the_client_renderers _build_is_styled` caught it. A class name is not a good enough reason to believe a rule is dead - the renderer is.
- Sticky highlight on the line the user navigated to via a deep link or a References-panel click. Backing colour comes from --blue-50 so it tracks the active theme. The actual line.background rule lives in CM6's baseTheme (currentLineTheme in code-editor.js) so it survives CodeMirror's row virtualisation.
- ---------- Release & file compare ----------
- The two compare screens - the Object Explorer's file diff and the standalone Compare tool - are archetype 11 (.pw + .cmp in pages-power.css). What is left here is the pane itself: a `.source-viewer` shell minus the outline DOM, so the existing CodeMirror styling carries through unchanged. Line-level diff colouring uses CodeMirror's line decoration mechanism (see code-editor.js -> buildLineDecorationExtensions). Lines flagged inserted/deleted/modified get a full-row tint - those three kinds are the whole vocabulary that reaches the client. The shorter pane is padded by .cm-diff-filler blocks between lines, not by tinted rows. Colours pulled from the existing theme palette to stay dark-mode safe.
- No .cm-diff-imaginary rule, and there should not be one: DiffPlex pads the shorter pane with Imaginary rows, but SideBySideDiffSerializer.SerializeSide drops them and SerializeFillers turns each run into a .cm-diff-filler widget instead - a gap between real lines, not a line. The kind never reaches the client, so a rule for it paints nothing and only reads as if it did.
- ════════════════════════════════════════════════════════════════════════ Projects + Artifacts tools  (recreated from the Claude Design handoff; see .design/artifacts.md). Uses the shared base.css tokens, so the screens theme light/dark automatically. ════════════════════════════════════════════════════════════════════════
- ════════════════════════════════════════════════════════════════════════ Artifacts detail: two-column layout + cards PR 15d took the pipeline detail page off this family and onto the design system (.card / .run-list / .sub-rows / .meta-row / .code-block). What is left here is the last caller, AdminReleasesImportArtifacts, which belongs to the admin import family of PR 8c and retires with it. `.det-card.hero`, the blue-ringed emphasis variant, went with the pipeline page: the design system's only card emphasis is `.card--danger`, and a focal card that is already first on the page and titled does not need a ring. ════════════════════════════════════════════════════════════════════════
- ════════════════════════════════════════════════════════════════════════ Project SETTINGS — left sub-nav + one active section (recreated from the Claude Design "Project settings" handoff; see .design/artifacts.md). Sits inside the page archetype's .page and reuses the shared base.css tokens, so it themes light/dark automatically. ════════════════════════════════════════════════════════════════════════
- ── left sub-nav ───────────────────────────────────────────────────────
- ── content side ───────────────────────────────────────────────────────
- ── releases list (Releases tool) ──────────────────────────────────────
- The whole release-pipeline dialect - .del-* delivery history, .rpe-confirm, .rb-outside, the pulsing per-app dot - went in PR 15e. The history is a .run-list now (the archetype sheet reserves it for "card-like histories that are not tabular", which a release with per-app sub-rows is), the acknowledgement is .check--ack, and the per-app state is a glyph off RowStateIcon rather than a coloured dot whose meaning lived on a title attribute.
- compact icon-only delete/disconnect button for table rows

---

## #1723: boxes fill their container (2026-11-05)

PR 29 below moved the reading measure off the step list and onto `.step__text`,
and in the same breath gave `.note` the 82ch the handoff spells inline on its
trailing MCP note. That was a no-op while every note sat in something narrower.
It stopped being one when notes spread to list and tool pages, where an info box
that ends mid-row reads as a layout bug.

**Divergence 75:** `.note` and `.steps` carry no `max-width`; a box fills its
container, and text that needs a measure gets it from its own element or from a
prose container. The caps that remain (`.docs__main`, `.step__text`,
`.errpage__inner`, `.empty-state__text`, `.error-block__text`, `.form-sec__cap`,
`.hint-details__intro`, `.reconnect__text`) are all on text, so they stay.

## PR 29: where a reading measure belongs (2026-08-22)

Two pages the maintainer flagged in one message, pulling in opposite directions
and settled by the same idea: **a reading measure belongs to prose, not to the
container prose happens to sit in.**

### /tools/mcp - the measure was on the wrong element

`.steps` caps at `max-width: 82ch`. For a guide read top to bottom that is
right, and `PageMcpSetup.dc.html` means it - its own steps hold code blocks and
inputs, and it caps its trailing note to 82ch inline so the page has one column
and no ragged edge.

Ours additionally holds a two-up card row the archetype never had. Held to 82ch
at the maintainer's 1760px that came to 745px of content beside 715px of
nothing, the two cards crushed to ~265px each, and
`https://aldevtoolbox.taaken.dk/mcp` broken across three lines. The page's own
CSS already carried a comment explaining that a real server address does not fit
in a half-width card and had been made to wrap because of it - a workaround
sitting on top of the actual cause.

**Divergence 72:** the list opts out (`.steps--wide`) and the 82ch moves to
`.step__text`, which is the only part of a step that is prose. `.note` gains the
same 82ch, taken from the value the handoff spells inline on its own trailing
note. The troubleshooting card - full width while the steps were at 619px, which
is the ragged edge that made this visible - now matches everything else because
everything else grew, not because it shrank.

### /docs/mcp - the measure was right and the grid was wrong

This one had been flagged by two design reviews and closed both times, with a
note arguing the measure. The measure is fine. Re-checked rather than taken on
trust: a body paragraph is 14px, 74ch is 558px, and exactly 74 characters fit
the line.

*(The first re-measurement said 65 characters and would have overturned a
correct decision. It had measured `.prose__lead`, which is 16px on purpose. The
lesson is the cheap one - probe the element the rule is about, not the first one
the selector matches.)*

The complaint was never the measure. `1fr` lets the first track absorb every
spare pixel while the article stays at 558px, so at 1760px a 1216px track leaves
a **658px hole** between the article and its own contents list - wider than the
article. The earlier reviews were looking at a narrower window where it is
~340px, which is how "that is what a docs layout looks like" survived twice.

**Divergence 73:** the track is sized to the measure and the grid packs to the
start. The contents sit beside the text and the surplus becomes one right
margin, which reads as margin the way a hole between two columns never does.
Centring the pair was tried and is worse - the page title stays left, so the body
detaches from its own heading.

### The bug underneath, which nobody could have seen by looking

Verifying the collapse turned up something older: **the contents list has never
hidden.** `@container (max-width: 1000px) { .docs__toc { display: none } }` sits
with the grid change at the top of the section, and `.docs__toc { ...
display: grid }` is declared later. Both selectors are `.docs__toc`, so they tie
on specificity and source order decides - the component's own rule wins and the
collapse rule does nothing. At every narrow width since the port the contents
have rendered as a full-width block under the article.

Confirmed pre-existing by re-running the measurement against a stashed tree, so
it is not something this PR introduced. The fix is to move the rule after the
component's own; the test pins it by **position**, because presence is exactly
what a later tidy-up would keep while undoing the thing that matters.

Suite 2,468 passed / 0 failed / 8 skipped.

---

## PR 28: one way to title a section (2026-08-22)

**The maintainer looked at PR 27's result and found the seam.** "Repositories"
was not inside a card the way "Compare builds" is, and "Build history" did not
match "Latest build" or "Build log" either. Four sections on one page, three
heading treatments: a `.card__title` in a bordered head, a bare
`.pb-sec__title`, and a mono uppercase `.state-label`.

Putting Repositories in the rail is what made it visible. A bare heading is
unremarkable stacked in a long column; sitting directly above a real card, at
280px, it reads as a mistake.

### Both of the obvious answers were wrong, which is the tell

PR 15d skipped the card on these two deliberately, and its reason is sound:
`.sub-rows` and `.data-table` carry their own surface, border and radius. They
are containers, not contents, and nesting one inside a padded `.card__body`
draws a box inside a box.

So the two available moves were *keep the bare heading* (cohesion loses) or
*wrap it in a card* (a doubled border). When both options are bad the missing
thing is usually a third one, not a preference between them.

**`.card--flush`** is the third one: the card frames the section, the container
gives its own frame up, and one border is drawn where there were nearly two.
Their background is already `--surface`, the same as the card's, so nothing has
to be repainted — only the border, radius and box-shadow go.

Both sections are ordinary cards now, `.pb-sec` / `__head` / `__title` are
retired, and the page has one heading treatment. `.pb-sec__note` survives: it is
the quiet mono note at the end of a card head, which is a different job.

### The corner, which only a magnified screenshot showed

The first cut stripped the border and radius and looked right at page scale. At
8× the bottom-right corner was square — and with `.data-table--edge` it is the
**4px status keyline** doing it, so the artefact is not a hairline but four
pixels of saturated colour cutting the card's corner off.

The keyline is a `border-right` on `td:last-child`, so the radius has to go on
the cells, not the table: `border-collapse: separate` means a radius on the
table element never reaches them. `.sub-rows` and `.run-list` carry
`overflow: hidden` and clip their own children, so for those the container is
enough.

**No `overflow: hidden` on `.card--flush`.** It would have fixed the corner in
one line and would have been the wrong line: clipping a `.ra__menu` escaping a
row is exactly what divergence 6 exists to prevent, and a table inside a card is
where a row kebab turns up. The trap would have been laid for whoever added one.

### The guard, and what it does not claim

`Every_section_on_the_page_is_titled_the_same_way` fails if `.state-label` or
`.pb-sec__title` returns to this page. It says nothing about bare sections being
wrong in general — only that *this page* must not mix the two, which is the
property a reader sees and the one that decays quietly as sections are added.

It failed on its first run, correctly: the file explains the two retired
treatments *by name*, and a class-name check that cannot tell prose from markup
trips on its own rationale. It strips Razor comments first now.

Suite 2,464 passed / 0 failed / 8 skipped.

---

## PR 27: the rail the archetype does not have (2026-08-22)

**Raised by the maintainer, clicking around a slightly older build.** In
Business Central a card page edits its fields across most of the width and
keeps a FactBox rail on the right — supporting information, cues to documents,
statistics, generally not editable. `/pipelines/{id}` used to be shaped that
way. After the port it is one long column, and the repository links are a long
scroll from the top.

Their own framing, which is the correct one: nobody is to blame except the
brief. The design agent was asked for a greenfield interpretation and had never
seen the old page.

### PR 15d was right, and then a little bit wrong

The rail did not vanish by accident. PR 15d dissolved it deliberately, and
`### The archetype nobody had seen` records why: `PageDetail.dc.html` turned out
to exist, four pages were already using its head, and its table said plainly
*"the facts | a 300px rail of cards | a full-width `.meta-row` strip; **no rail
exists**"*. The rail also went *out* of `pages.css` in that same pass —
`.dash-cols--rail` / `.dash-col`, withdrawn before they were ever pushed.

All of that holds for the **facts**. Project, Owner, Builds, Latest BC really do
read better as the strip they became than as a column of little cards, and that
half is not being undone.

What the strip could not absorb were the two sections that are **not** facts:

- **Repositories** — the repos a build was cut from, with links out to the host.
- **Compare builds** — a picker and a button, i.e. a secondary action.

Neither is a fact about the pipeline, so neither fitted the `.meta-row`, and
with the rail gone there was nowhere else for them to be. They went to the
bottom of the main column — under the build log, which is the one section on
this page with no upper bound on its height.

So the loss was not "the port dropped the rail". It was that dissolving the rail
solved for the content the archetype *did* have and left two sections with no
home. That is a smaller mistake and a more instructive one.

### What it measures

On `/pipelines/5`, 1440×1000, before — and note this is the *flattering* case,
a short log with the compare card not rendering at all:

| | before | after |
| --- | --- | --- |
| Repositories | y = 1052 | y = 288 |
| Compare builds | not rendering | y = 430 |
| page scroll height | 1145 | 1053 |

The page got 92px **shorter** while gaining a section, which is what a second
column buys. The before screenshot is the clearer argument though: a 1144px
column holding two lines of text, and a Description cell half empty, while the
thing you want is below the fold.

### The archetype gains an optional rail

`.detail-body` / `__main` / `__aside`, shaped like the two asides the system
already has rather than invented: sticky, `align-items: start`, collapsing at
the same 1080px as `.gen` and `.settings__body`. 280px is settings' rail, not
gen's 392px — this holds reference, not a preview.

**Optional is the load-bearing word.** Whether there is anything to put beside
the content is a *per-record* question here, not a per-page one: a pipeline
whose only build failed before it recorded a commit has neither repositories nor
a second build to compare. An aside that renders empty still costs 280px and the
content stops short of the page edge for no reason a reader could see — which is
the exact bug `.settings__body--wide` exists to prevent, so `.detail-body--wide`
is the same escape, decided per render. Verified in that state by removing the
repo commits and release ids and putting them back.

Repositories stays a `.pb-sec` over a bare `.sub-rows` rather than becoming a
card. PR 15d's rule — a component carrying its own surface, border and radius
must not be boxed inside a card — still holds in a rail, and next to the real
`.card` that Compare builds is, the two read as what they are.

### #574 is why the width was measured, not eyeballed

`.app__content` is `overflow-x: hidden`, so a table whose min-content exceeds
its column is **clipped, not scrollable**. Narrowing the main column is exactly
the move that trips it, and the only measurement that discriminates is
min-content — rendered width tells you nothing, because every stretched sibling
reports the same number.

Build history min-content is **670px**. Main column is 844px at 1440, and 780px
at the narrowest point before the collapse. Forcing the rail on *below* the
collapse reproduces the failure precisely: at a 1180px viewport the table
renders 670px inside a 632px cell.

Both numbers are in the sheet's comment and pinned by a test, because widening
the rail or dropping the collapse is what would spend that headroom.

### The breakpoint, put to the maintainer

1080px means a 1280px laptop loses the rail — the page is only 1032px there
after the sidebar and padding. Lowering it to 1000px would keep it, at 26px of
headroom instead of 106px and with Description wrapping to five lines.

**Kept at 1080.** It is not an arbitrary inheritance: at its own boundary
`.settings__body` leaves its main column 776px, and 1080 gives ours the same
floor. One less number in the system, and the floor is one the system already
lives with.

### Not done here

`pages.css` has not gone upstream through DesignSync yet, so the design project
is behind our two local copies by this change. The parity guard compares the two
**local** copies only, so the suite is green and the drift is invisible to it —
worth saying out loud, because that is precisely the shape of gap that let
`PageDetail.dc.html` stay unnoticed for a fortnight.

`ReleasePipelineDetail` and `TemplateDetail` were checked and neither wants a
rail: the first has one section (the delivery history), and the second's four
are all substantial content rather than supporting reference.

Suite 2,463 passed / 0 failed / 8 skipped.

---

## PR 26: the generator preview, and a walk that had to move (2026-08-22)

**#546's residue, and it had flagged itself honestly.** The first item was
marked *"Unconfirmed against the rendered screen — needs `PageGenerator.dc.html`"*.
Confirmed: the prototype puts `'ID ' + from + '-' + (from + 99)` in
`.tree__meta` on every generated extension row, plus a count on the `app`
folder. Our preview emitted that slot for one thing only — an `IsNew` badge.

**The second item was stale.** *"The preview aside shows no ID ranges and no
dependency list"* — the handoff has no dependency *list* either. It has a
`.stat-card` count, and both generators already render it. Half an issue is
still worth reading twice.

**Where the numbers come from was the whole of the work.** The preview already
walks the same two lists in the same order as the generator, so it could have
replayed the allocation inline — and that is exactly the reason not to.
`BuildPreview` carries a comment promising the preview matches the ZIP ("this is
exactly what `WorkspaceZipBuilder` does at emit time"), and *a second copy of a
cursor walk is how a promise like that quietly stops being true*. One caller
became two, so the rule moved out to `IdRangeAllocator` and both call it.

The walk itself is unchanged, deliberately. These numbers are baked into
`app.json` files customers build against, and eighteen assertions across the
generation tests read them back out of the ZIP — which is the only reason an
extraction here was safe to make at all. *Refactoring under a test suite that
reads the real artefact is a different act from refactoring under one that reads
the code.*

**The extraction created exactly one new way to be wrong, so that is what the
new tests are about.** `BuildExtensionList` consumes the allocator's result by
index against its own two loops, so allocation order is now a contract rather
than an implementation detail. An off-by-one would hand every extension its
neighbour's id range and still produce a perfectly valid workspace — no error,
no failed build, wrong numbers in a file someone ships.

**`.tree--loading` is applied, and the issue had the moment wrong.** It reads as
"the pane can blank between keystrokes rather than dim". The handoff dims on
`s.busy` — *while a generate runs*. That is not a smaller version of the same
idea, it is the opposite one: the tree is already correct while you type, and
dimming it then would tell the reader it was not.

Verified rendered with two modules ticked: `ID 50100-50149` and `ID
50150-50199` — the template's `module_id_range_start` plus two 50-wide slices,
checked against the rows in the database rather than against what the page said.

## PR 24-25: what constrains a column, answered once (2026-08-22)

**#574 and #549's `.edit-col` half were the same question** — the earlier note
on #549 said so and asked for them to be decided together. They were, and the
answer is one sentence: *things you read across — logs, tables, diffs — get the
width; things you fill in do not.*

### PR 24 — #574 was not a layout bug, and two diagnoses had read the wrong number

Four admin lists clipped the right of the page below 1100px. The issue guessed
the tables; a later comment on it concluded the opposite, that `.filter-bar` was
wider than the table. **Both were measuring rendered widths**, and that is the
one measurement which cannot discriminate here: once a table's min-content opens
the content column, every stretched sibling reports the same over-wide box. At
880px on `/admin/templates` the page head, the filter bar and the table all
measured exactly 938px and all overflowed by exactly 130 — which is what "the
filter bar is as wide as the table" actually meant.

**min-content discriminates**, and it names the table every time (936 against 94
for the widest filter-bar child). Within the table it names the actions column:
314px of a 966px table on `/admin/cookbook`. Those cells were still rendering
the pre-redesign row of full text buttons, where the list archetype puts exactly
one control — a `.ra` kebab. **`/pipelines`, which had already been ported to
it, overflows at no width at all.**

So a fidelity gap wearing a responsive costume, and finishing the port fixed it
with no design-layer change: no scroll wrapper, and therefore none of the
menu-clipping a wrapper would have re-introduced. That was the trap the issue's
own comment had flagged, and it is avoided by never creating the clipping
context rather than by working around it — divergence 6 took `overflow: hidden`
off `.data-table` for exactly this reason. Checked directly: all four menus open
on the **last row** of their table, fully inside the content column, clipped by
nothing.

**It was a defect, not polish.** At 1100px `/admin/cookbook` had five
unreachable Delete buttons; at 880px, its own **New recipe** button.
`.app__content` is `overflow-x: hidden`, so that content was not off-screen —
it was gone.

One thing needed protecting: folding Edit into the kebab would have made the
commonest action on each page two clicks, so the row's identifying cell became a
link. That is the same trade `RowActionsMenu` already records for the Object
Explorer's results table, and `RowActionsWidthTests` now holds both halves.

### PR 25 — #549's `.edit-col`, and a blast radius that was not one

The earlier note put this at 23 pages and left it for a deliberate call. The 23
counted `.form-sec__cap` — a **caption class** used by browsers, generators and
importers, which are archetypes 2, 5 and 12 and are not what `.edit-col` is for.
The archetype it belongs to is *admin edit + audit*: two pages canonically, four
counting the single-entity forms with the same shape and the same 1304px card.

The sheet's own spec settles the rest, and both halves of its sentence are
load-bearing: *"single column capped at 900px; forms do not get wider than they
can be read. The audit panel sits **outside** that column, at full content
width: it is a log, not a form, and the diff lines are the one thing on the page
that genuinely wants the extra characters."* The recipe page has no audit panel
but does have a "where this recipe has been used" table, which is a log by the
same argument and stays outside the column.

*900 was not an arbitrary number to us either.* `/admin/templates/new` and
`/admin/templates/files` had already reached 888 and 905 **by hand**, before any
of this — which is the strongest evidence available that the archetype's cap is
the right one.

## PR 23: the audit log stops naming primary keys (2026-08-22)

**#554, the clearest jargon-test failure left on the branch.** Every audit row
read `<actor> changed Module #4`, and `#4` is the primary key of a row in
`modules` — a number an admin has never seen, cannot map to anything, and which
made two rows about two different modules identical at a glance. Five surfaces
now read *changed the module **Sales Extensions***: both audit logs, both diff
headers, and the dashboard's activity panel. The issue named three; the two diff
headers were doing the same thing and are the same fix.

**Where the name comes from is the whole design decision.** The issue sketches a
read-time resolver — "a per-`AuditEntityType` lookup across ~25 types … plus a
deleted-row fallback that reads from the snapshot JSON". Built that way it would
have needed a live query per type, and on `/site-admin/audit`, which reads
*across* organisations, those lookups would have had to escape the tenant filter
to resolve anything but the reader's own org. That is a new
`IgnoreQueryFilters()` call site inside a normal request, which is the one thing
this codebase does not add without asking.

So the name is captured at **write** time instead, into `audit_log.entity_name`,
and that turns out to be the more truthful design rather than merely the
cheaper one. *An audit row should say what the thing was called when the change
happened.* Resolving the current name would rewrite history on every rename, and
would say nothing at all about a row that has since been deleted. Rows written
before the column exists fall back to the same fields read out of their own
"before" snapshot, which covers historical updates and deletions; historical
`Created` rows carry no snapshot by design and stay unnamed.

**One shared candidate list, not 27 per-type arms.** The interceptor's snapshot
is a flat dictionary of the row's own column values, so an ordered list of
field names — `Name`, `DisplayName`, `Title`, `Key`, `DepName`, … — resolves all
27 audited types without a type switch. The four that resolve to nothing resolve
to nothing correctly: a join row of two foreign keys, the org's single logo
asset, and two singletons the reading side words as "the organisation settings"
anyway. A reflection test reads the audited types out of the interceptor's own
map, so adding a type whose name field is not a candidate fails the build
instead of logging unnamed rows quietly for months — which is how
`ApplicationVersion` and `PersonalAccessToken` were on screen as camel-case
identifiers before the guard in PR 10.

**Two bugs, both found by reading the rendered text back rather than looking at
the screenshot.** The screenshot was fine. The text was not.

- Razor strips the whitespace between an expression and a following `@if` block,
  so the first cut rendered `changed the moduleSales Extensions`. At a glance in
  a 1440px screenshot that is a kerning artefact. In `textContent` it is
  unmissable. The component is two whole branches now rather than one span with
  a conditional inside it.
- `RelativePath` outranked `FileName` in the candidate list — and on a recipe or
  module file `RelativePath` holds the **folder**, not the file. So a change to
  `PostingRoutine.Codeunit.al` rendered as *changed the module file
  `src/Posting`*, naming the directory the file sits in. It is out of the list
  entirely; every other path-ish candidate is checked and is the thing's own
  name (`WorkspaceExtensionFile.Path` is a basename, `WorkspaceExtensionFolder.Path`
  is one segment).

*A field that looks like the better name is worth reading the doc-comment on.*
Both of these were confident-looking output. Neither would have failed a test I
had thought to write from the markup.

**The id moved rather than left.** It is on every subject's `title`, because the
audit page's own record-id filter takes one — the number is a power-user input,
not a label. Verified across 112 subject spans on three pages: none contains a
`#`, all 112 still carry their id.

## PR 22: auditing the status vocabulary, and finding it already done (2026-08-22)

**#532 had rotted, and checking that was most of the work.** It asked for six
named files to be moved off bare `.status-pill` onto the handoff's richer
variant set. Four of them no longer use a status pill at all — the Active /
Revoked / Expired surfaces moved to `RowStateIcon` during the port, and *that
vocabulary already answers the question the issue poses*: `revoked` and
`expired` map to **archived**, not to a failure, because they are end-of-life
rather than error. `AdminTemplateList`'s "Default" marker is already a `.tag`,
carrying a comment with exactly the reasoning the issue suggested. `Account`'s
2FA rows are already `--success` / `--muted`.

So rather than work the six, the whole app's `.status-pill` call sites were read
and asked the issue's own question — *what does this state actually mean?*
**One category error in thirty-one call sites.** `AdminCookbookSuggestionReview`
put a recipe **type** in a bare status pill, sitting between two `.tag` siblings
that were saying attributes — so it read as the only status in a row that has
none. There is no bare `.status-pill` left anywhere.

Three left deliberately, recorded because a later reader will otherwise
re-litigate them:

- **Deprecated** is `--warn` plus an archive glyph on all three of its pages. A
  genuine lifecycle state, consistently expressed.
- **Audit actions** (Created / Updated / Deleted) go through `AuditActionPill`
  on four surfaces. That is what happened to the row, which is state-shaped.
- **Production environments** read `--danger` on three pages. By the letter of
  the rule an environment type is an attribute and should be a `.tag` — but
  three call sites agree, and the red is carrying "this is the one that can hurt
  you", which is worth more than the taxonomy.

## PR 21: one code block, one way to tint AL (2026-08-22)

**#587 and #565 were the same job and are closed together.** The design layer
shipped two static code-block components *one letter apart* — `.code-block` in
`components.css` and `.codeblock` in `pages-content.css` — with different
surfaces, different bar chrome and different highlight vocabularies, and the
Cookbook's server-side highlighter had invented a third naming style on top.
Three ways to tint AL in an app that only ever wanted one.

**The handoff has the split too, and our call sites mirror it exactly** — docs
and MCP pages on `.codeblock`, everything else on `.code-block`, in both the
prototype and here. So this is a defect in the design system rather than
something the port introduced, and the correction goes upstream: two sheets,
three prototype screens and the two spec pages that list the inventory.

**The vocabulary did not have to be invented, which is what made this cheap.**
`.codev` — the handoff's own code viewer, ported and unused (divergence 12) —
already defines the complete six-class set `.k .t .n .s .c .o` on the `--code-*`
tokens. `.codeblock__pre` was a five-sixths copy of it missing `.o`. So "one
vocabulary" meant *adopting the system's own*, not picking a winner between two
half-vocabularies.

**`pre b` / `pre i` had no users.** #587 argues the element-based highlighter
can only ever express two kinds, which is true and is why the Cookbook needed
its own. What the issue could not know is that **not one of the thirteen
`.code-block` call sites used a `<b>` or an `<i>`** — the only match in the
whole family was a deprecation warning in prose. The structurally-limited
highlighter was also dead. Deleting it cost nothing.

**Which name survives is not a coin-flip.** `.code-block` lives in
`components.css`, and a code block is a component; `pages-content.css` is the
page-archetype sheet, where it was only ever a lodger. It also has the BEM
hyphen every other component in the system uses. The *internals* are the other
one's — the `__lang` slot, the fixed 30px bar, the class-based tinting — so the
merge keeps the better half of each rather than the more popular one.

**The surface was the one thing worth measuring, and measuring reversed it.**
`--code-bg` is the token *named* for a code canvas, so it looked like the
obvious answer and shipped that way for an hour. Then the numbers: in light it
resolves to `--surface`, making the bar (`--surface-2`) **darker** than the
code; in dark it resolves to `--surface-sunken` and the bar comes out lighter.
The chrome/code relationship inverted between themes. And on `/templates/extension`
— a code block on a white card — `--code-bg` left the block separated from its
ground by a single border pixel. `--surface-sunken` is the *same value* as
`--code-bg` in dark, so nothing there changes at all, and in light it keeps the
bar lighter than the code and the block visible on any ground.

*A token named for the job is not evidence that it does the job.* This was
found by printing each block's computed background next to the first opaque
background behind it, on three pages in two themes — not by looking, which had
already passed it.

**`overflow: hidden` did not come across.** `.codeblock` carried it; at
`--r: 2px` it buys two pixels of corner fill, and this system has already lost
a row-actions menu to exactly that property once (`.data-table`, divergence 6).
The merged component is the safer of its two inputs, not the union of them.

**The bar takes both markup shapes rather than making thirteen call sites
grow a spacer.** `justify-content: space-between` with an optional `__spacer`:
two children sit at the ends by themselves, and a three-item bar (lang, name,
button) lets the spacer eat the free space so `space-between` has nothing left
to distribute.

**The guard caught the rename before the renderer did.** `ComposedClassNameTests`
failed the moment the highlighter stopped emitting `tok-*`. Fixing it exposed a
hole the single-letter names had opened: the old check asked "does any sheet
name this class", and `.codev` defines the same six letters — so a recipe
rendering inside `.code-block` with no rules of its own would have passed. The
check is now scoped to `.code-block pre .{cls}` specifically.

**Pushed upstream, and read back to prove it.** All eight files — both
sheets, `ComponentsPanel`, `PageDocs`, `PageMcpSetup`, `PagesContent`,
`SinkBody` and `DESIGN-SYSTEM.md` — went through DesignSync, and
`components.css` and `ComponentsPanel.dc.html` were fetched back and diffed
byte-for-byte against the local copies rather than trusting `written: 8`. That
is PR 20i's lesson applied: the write count reports transport, not content.

**And the prototype's own sample was mis-tinted.** `ComponentsPanel.dc.html`
marks its AL keywords up as `<b>`, which the sheet coloured as `--code-type`:
`codeunit`, `procedure`, `begin`, `exit` and `end` all rendered as `Integer`
did. Nobody would have caught that from the screen — two kinds of thing sharing
one colour looks like a palette choice.

## PR 20: the compare cluster, and the card the maintainer opened (2026-08-22)

Four Compare issues and a five-commit run on the symbol hover card that started
as a bug report and ended with the card **smaller than the handoff draws it**.

**#585: the inline diff was not hiding its unchanged runs, it was deleting
them.** `UnifiedDiffSerializer.Build` wrote only the selected hunks into the
document, so the rows between them did not exist in the text CodeMirror held —
the `@@` bands were labels over a gap, and nothing could expand them because
there was nothing behind them to reveal. The side-by-side view had been fixed
the right way round in PR 16c (`SideBySideCollapse` marks regions over a
complete document) and the two had drifted apart. Build now writes **every**
row and emits `CollapseRegion`s in document line numbers, which is what makes
"click a band to show the unchanged code" a truthful instruction on both
layouts. *A view that hides content still has to contain it* — collapsing is a
rendering decision, and a serializer that resolves it by dropping rows has
turned it into a data decision.

The shared `CollapseRegion` record fell out of this: two collapse
implementations that had each grown a private `Region` now name the same shape,
and `CollapseJson` holds the one `JsonSerializerOptions` both serialise
through. Second caller, so the extraction cleared the bar.

**#581 rode on #585 and was sequenced to.** Giving the Compare tool the inline
layout is only worth doing once the unified document is complete; done in the
other order the tool would have inherited bands that expand to nothing. The
tool's inline pane is a **result view, not a third input** — read-only, over a
document the server synthesises on every re-diff, because on this page the two
panes *are* the input and the unified text is a function of a keystroke ago.
That forced remount discipline: CodeMirror measures nothing useful inside a
hidden container, so the pane mounts the first time the reader switches to it
and remounts whenever the text behind it has moved on (`inlineDocStale`). The
head says "Read-only — switch to Side by side to edit" rather than leaving
someone to discover it by typing into a pane that will not take a keystroke.

**#577's premise was inverted, and the real defect was next to it.** The issue
asks for an Ignore-whitespace option on screens that "compare byte for byte".
They never did: DiffPlex's `ignoreWhiteSpace` parameter defaults to `true`, so
both compare surfaces had **always** ignored it. What the issue was really
reporting is the contradiction the default produces — the Object Explorer's
change rail hashes file content, so a whitespace-only edit is `modified` in the
rail and "identical" in the diff, and the page argues with itself. So the work
split in two: the toggle became real (and defaulted to the behaviour that was
already shipping, so nobody's diff silently changed), and the page learned to
say **"Identical apart from whitespace"** when the texts differ bytewise but
not by the current setting. `IgnoreWhitespaceTests` pins what DiffPlex actually
does with the flag, since the whole issue existed because nobody had checked.

*Read the library's default before building the switch.* The issue, the code
comments and my first plan all assumed the same wrong thing, and one theory
test settled it.

**The bug the fix introduced is the one the issue warned about.** With the flag
in the query string, Swap sides and every change-rail hop dropped it — you
turned whitespace back on by navigating. Caught by asserting the query string
after each hop rather than by looking at the page, which is the only way this
class shows up: the page after the hop is perfectly plausible.

**#588: the change rail's paths, cut on a folder boundary.** Long paths were
being truncated by CSS from the left (`direction: rtl`, divergence 9's idiom),
which cuts mid-segment and produced rows reading `…US Discount Mgt`. They now
elide to the nearest `/`, measured against the actual rendered font. **The
first cut reproduced the reported bug**: falling back to CSS truncation when
even the bare filename overflowed *is* the `…US Discount Mgt` case. Found by
printing what every row rendered at five widths instead of checking the one row
in the screenshot.

### PR 20e-20h — the card, opened by the maintainer (2026-08-22)

**The symbol hover card overflowed its own border on every card, by 38-64px.**
Reported from a real session; five commits followed, and the last four came
from the maintainer looking at screenshots and asking why the card was shaped
the way it was.

The mechanism is **CSS grid's automatic minimum size**: a grid item is floored
at its min-content width unless told otherwise, so one item wider than the card
opens the whole column and *every* stretched sibling overflows by the same
amount. Two items were doing it — the actions row needed 381px in a 330px box,
and the meta row's min-content was 401px. Widening the card fixed the symptom
and `.symcard__meta > span` (`min-width: 0` + ellipsis) fixed the mechanism.

**The measurement was already on my screen.** A probe written during #561 had
computed `overflowsCard` for every card and I asserted on a different field, so
I called the card clean while its own diagnostic said otherwise. The maintainer
found it by opening the app.

Then the questions, each of which removed something:

- *"Is it necessary to have the shortcut definition on every hover?"* No — no
  IDE does this. `F12` and `Shift+F12` chips rode every card forever to teach
  a shortcut that is learned once. They moved to the **context menu that
  performs the action**, which is where Code and Zed put them, and where they
  are read at the moment they are useful. "Find in this file" deliberately
  carries none: it opens CodeMirror's search box, and the menu item and the
  shortcut are not the same feature.
- *"What about just making them pure text/links instead of buttons?"* Two
  `.btn btn--sm` in a tooltip were carrying a button's padding, border and
  background to offer navigation. `.symcard__act` is now a bare text link.
- *"Slightly less padding between the hr and the bottom?"* Yes.
- *"Wouldn't visibility make sense as part of the heading?"* Yes — AL writes it
  there. `public` / `local` / `internal` / `protected` moved out of the meta
  row into the declaration line, so the headline reads as the AL you would
  write, and the meta row dropped to the two parts that are genuinely metadata.

**And the heading turned out to be wrong for fields.** `Amount Including VAT`
of type `Decimal` rendered `Amount Including VATDecimal`, because a field's
"signature" is its type and the two were concatenated with nothing between
them. It is in four screenshots I had already looked at and described; it
surfaced only when the maintainer asked about the heading, because I had been
*measuring* the card rather than *reading* it. `declarationOf()` now switches
on kind — `procedure Name(params): Return`, `Name: Type` for a field — which is
also what made the visibility change a one-line addition.

The card ended **narrower than PR 20e made it and back at the handoff's 356px**:
the overflow was never the width, it was two pieces of content that did not
belong. PR 20e's widening was a correct repair of the wrong problem.

### PR 20i — the push, and the two comments that outlived their rules (2026-08-22)

`pages-power.css` went upstream through DesignSync so all three copies are
byte-identical again. **Verified by fetching the pushed file back**, not by
trusting `written: 1` — which is what caught two comments left standing over
rules that no longer existed: one still describing `.symcard__acts` as a
two-column grid carrying keybindings, one describing a `:has(> :only-child)`
rule that had been deleted, now floating above a blank line. This is PR 17a's
dead-sweep trap from the other side: retiring a rule and leaving its comment
teaches the next reader something false, and no test can see it.

## PR 19: the Object Explorer cluster (2026-08-22)

Five of the six OE issues closed. **Three were already built** — #568's plan
was intact but two cells had fallen out, #569 had everything except its last
bullet, #567 had its whole capability and only its signposting missing. The
backlog's staleness rate held: check the premise against the running app before
planning from an issue.

**#564 found the branch's clearest instance of a bug with no error behind it.**
`.cm-line--current` read:

```js
backgroundColor: "var(--editor-current-line-bg, rgba(99, 102, 241, 0.08))"
```

and `--editor-current-line-bg` is declared **nowhere**. So every highlighted
line had been rendering in the hard-coded fallback — an *indigo*, from outside
the design system entirely, on a screen whose accent is teal — and not
theme-aware either. *A `var()` with a fallback nobody defines is not a hook; it
is a hard-coded value with a comment on it.*

Generalised into an audit, and the class is now closed rather than left as a
suspicion: four properties are referenced only through a fallback, and the other
three are correct — `--oe-right` and `--cmp-rail` are written at runtime by the
split-drag (the fallback is the pre-drag default), and `--focus-ring-shadow`
falls back to `none`, so it is inert rather than wrong.

**Measure before believing the issue.** #569 reads as a layout nicety until you
put a number on it: at 900px, opening the tree left the code column **296px**,
about a third of an AL line. Finding a file made the file unreadable. #575 reads
as a copy fix until you open the panel and find `.menu`'s 280px cap showing two
of seventeen prefixes with no hint there were more.

**One rule beats seventeen facts.** The Type column's two-letter badges *are*
the search prefixes, and nothing said so. The tips panel teaches the rule — the
badge lower-cased is the prefix — rather than restating the table. That rule is
true only by coincidence of two independent maps, so `SearchPrefixHintTests`
holds it in both directions: every badge is a real prefix, **and** each scopes
to the kind whose badge it is. A prefix resolving to the wrong kind would be
worse than one that fails, because the search would quietly return wrong rows.

**Guards catching their author's successor.** `ComponentCollisionTests` (#537)
rejected a bare `.oe { position: relative }` here — rightly, since it would have
reached the compare screens' grid too. And a `var(--scrim)` was written an hour
after the commit message criticising exactly that pattern; caught before it
shipped, by looking for the token rather than assuming it.

### PR 19e: the doc line, and the headline that said `()` (2026-08-22)

**#561, the only OE issue that touched the schema.** Divergence 17 closed: the
extractor now reads the `/// <summary>` above a declaration, `oe_module_symbols`
gained a nullable `doc`, and the card renders `.symcard__doc` between the
signature and the meta row. The maintainer's reason for doing it at all is why
it was worth a migration — the same text lands on the outline tooltips and on
`get_object_outline` over MCP, so one column pays three surfaces.

**Source is the only place a description exists.** The AL compiler drops doc
comments, so no symbol package carries one and nothing downstream of the
compiler has ever seen one. That shapes the ingest: for a public procedure the
row is built from the package and the source match contributes positions — and
now the description, which is the one field the package *cannot* supply. Both
halves are pinned by a test through the real importer, because they reach the
row by different routes.

**`<param>` and `<returns>` are dropped, and a block with neither summary nor
prose yields null.** The signature already names the parameters; promoting a
`<param>` into the description slot would put "If true it widens the filters"
on the card as though it described the procedure. The tag-less `/// Does the
thing` form *is* kept — it is what people type when they are not writing for a
doc generator, and it is often the only documentation customer code has.

**The failure worth guarding is the wrong description, not the missing one.** A
doc block that survives a body and staples itself onto the next declaration
reads exactly as confidently as a correct one. Any code line that is not a
declaration clears the pending block; attribute lines deliberately do not,
because an `[IntegrationEvent]` sits between the doc and the procedure it
documents.

**Then the card's headline turned out to say `()`.** `sig.textContent =
data.signature || data.name` — and `Signature` is the parameter list *and
nothing else* (pinned by `AlSymbolExtractorTests` since the outline shipped a
return-type column that parsed the tail after the last `)`). So every symbol
that had a signature rendered a card headed `()`, with the name reachable only
through the `title` attribute. Adjacent to #561 and fixed with it: the headline
is now `name + params + ": " + returnType`, which is the handoff's shape. Found
by looking at the rendered card, not the markup — the same way #564 was.

## PR 18: the backlog's DesignSync batch (2026-08-21)

### PR 18f and the staleness sweep (2026-08-21)

**Ten of the backlog's issues closed in one evening, and five of them were
already done.** #527, #528 and #540 needed no code at all — their work had
landed in earlier PRs and the issues had gone stale. #546 was three-quarters
done and had *rotted* in the quarter that was left. That ratio is the argument
for checking a backlog issue's premise against the running app before planning
work from it.

**#584 is the pattern worth copying.** The compare rail showed file states as
git's A / D / M while the results table one click away showed a glyph plus the
word. The fix was not to pick better letters but to read the glyph from
`RowStateIcon.Glyph`, so the two surfaces cannot disagree again — the same move
as the three-vocabulary note above. And it cost no CSS: the `Icon` component
takes `Width`/`Height` as attributes, so a parity-locked sheet did not have to
move.

**#583's best find was the one filed as an aside.** A modified row's empty
line-stat slot sat beside rows reading `+42 -0`, which reads as *no lines
changed* — the opposite of true, since a modified pair is exactly the case whose
per-file diff has not been run. Absence of a number is not a number.

**#530, measured rather than reasoned about.** `CSS.getPlatformFontsForNode`
reports the faces actually used and how many glyphs each drew. Cyrillic and
Greek render almost entirely in the fallback face — and the 2–4 Selawik glyphs
in those strings are the *spaces*, so the face changes at every word gap. The
finding the issue did not predict: **Vietnamese mixes faces inside a single
word**, 23 glyphs Selawik and 4 DejaVu, because the diacritic-heavy characters
fall through individually. A Latin-script locale, so easy to miss when testing.
Left open: the fix is a font-stack judgment with cross-platform consequences.



### PR 18c-e — the first backlog clusters (2026-08-21)

**Two issues in the "ported but unwired" cluster were already done, and finding
that out is what found the stale claims.** #528's three bespoke row
vocabularies are at zero (`hist-`, `del-row`, `pipe-row`); #527's
`BuildStatusPill` has no call sites at all, so what was left was a component and
a stylesheet rendering the pre-redesign look to nobody. Deleted.

*A definition is not a call site.* I described that pill as "shipping the wrong
look today" on the strength of its stylesheet existing. It renders nowhere. Same
error the 17a pruner made when a comment mentioning a class kept the class
alive, one layer up.

**The docs contents marker (#558) is the branch's clearest example of a bug with
no error behind it.** Written as an IntersectionObserver first — the obvious
shape, and it answers the wrong question: band membership is "a heading is on
screen", the reader wants "which section am I in", so it marked the section
*after* the one you clicked and marked nothing at scroll top. Then two silent
coordinate faults: **the app scrolls `main.app__content`, not the window**, so a
`window` scroll listener never fires and the marker simply freezes; and
`getBoundingClientRect().top` is viewport-relative while the scrollport starts
64px down, so a bare constant runs the marker a section behind. Verified by
driving both pages: 16 of 17 anchors exact.

**#574's premise does not survive measurement.** It is filed as "wide data
tables", but at 980px the **filter bar is wider than the table** on both pages
measured, and on `/site-admin/users` the table is not in the overflow chain at
all. Three more corrections: the content is **clipped, not scrolled**
(`.app__content` is `overflow-x: hidden`), `.filter-bar` already carries
`flex-wrap: wrap`, and `min-width: 0` on `.page > *` moves the number by 1px.
Left open — it is a design-layer decision, and there is a trap waiting: any
`overflow-x: auto` wrapper becomes a clipping context on **both** axes, which
would cut off the `position: absolute` `.ra__menu` the sheet's own "no
`overflow: hidden` here, deliberately" comment exists to protect.

**#546 was mostly done and had rotted where it was not.** Both generator pages
catch validation inline; the last-resort page still linked `/base.css`, renamed
in 17e, so the one page whose job is to look like the app had lost the app's
reset. Four validation keys also fell through to their raw C# name
(`CoreIdRangeFrom — Must be greater than zero`).

**Two tests here are worth copying, and both were written wrong first.**
`GenerationFieldNameTests` asserts a key has its own *arm* rather than that its
label differs from it — `Publisher` is genuinely the word the form shows.
`TranslatorArchetypeTests` gave up on counting a row's cells from source (the
target cell is inside an `@if`/`@else`, and the editing row nests a cell inside
a track, so both "count every span" and "count the shallowest" give different
wrong answers) and checks the two joins that matter instead — plus asserts it
found both row variants, because a source-reading test that matches nothing
passes while checking nothing.

**Owed upstream.** `pages.css` (#555's `a.tool-tile--locked` contrast) is
changed in both local copies but **not yet pushed to the design project** — a
parity-locked sheet needs a DesignSync round-trip, which needs the maintainer at
the terminal. `StylesheetLoadOrderTests` compares the two *local* copies, so it
is green and will not catch this; the design project is the copy that is behind.



The port is over; what is left on the branch is the `redesign` backlog. Its 32
open issues sort into six clusters, and exactly one of them was blocked on
something only the maintainer can do — a write to a parity-locked sheet. That
cluster went first, because everything else can run unattended.

**17f was measured before it was planned, and the measurement retired it as a
milestone.** The premise — 2,600 lines of scoped `.razor.css` the count has
never seen — is no longer the right description of the debt:

- **Zero hardcoded colours across all 39 scoped sheets.** The only three `#hex`
  matches in the whole tail are `#586` issue references inside comments. The
  scoped layer is fully on the tokens already.
- What *is* there is **78 class names in 23 scoped sheets that the design layer
  also defines** — `Translator` 20, `ReleasePipelineDetail` 6, `Mcp` and
  `CookbookBrowser` and `ReleasesBrowser` 5 each. Overlap is not automatically a
  defect: a scoped sheet may legitimately *extend* a component with local
  geometry. It is a defect when it restates the component's own values, which is
  the bug class the design-layer bridge used to hide.
- A third of that list is **already filed** — `ReleasePipelineDetail`'s six
  `run-row*` rules *are* #528; `BuildStatusPill` *is* #527.

So 17f is not a separate milestone. Its actionable remainder is #527, #528,
#549 and #562 plus a `Translator` audit, and running it as its own slice would
mean reading the same 23 files twice. **Fold it into the backlog and slice by
surface** — a surface is one page, one screenshot pass, one verification.

**And the name-overlap scan has a blind spot worth writing down: it only finds
duplication that shares a name.** Duplication under a *different* name is
invisible to it, which is where the live bug turned out to be — see #527 below,
which the scan reported clean.

### #565 — the Cookbook palette was painted from the wrong vocabulary

Filed as a DesignSync item; it needed no round-trip at all. The parity-locked
half was already done — every `--blue*` call site is gone, and the one grep hit
left is a comment. What remained was in `code-editor.css`, which is ours:

```
.tok-kw  { color: var(--primary-ink); font-weight: 600; }
.tok-str { color: var(--st-final); }   /* XLIFF "translated" green */
.tok-id  { color: var(--st-fuzzy); }   /* XLIFF "fuzzy" amber */
.tok-com { color: var(--ink-4); }
```

Two of the seven Cookbook token colours were **the translation-state ramp** — a
vocabulary that means *"this segment is approved"* — reused as syntax colour.
That is why the same AL keyword changed colour between a recipe and the Object
Explorer. All four now take the `--code-*` tokens palette 2 uses, verified by
computed style rather than by eye:

| class | was | is | `--code-*` |
| --- | --- | --- | --- |
| `.tok-kw` | `--primary-ink`, 600 | `rgb(44,92,143)`, 400 | `--code-key` |
| `.tok-str` | `--st-final` | — | `--code-str` |
| `.tok-id` | `--st-fuzzy` | `rgb(80,92,109)` | `--code-obj` |
| `.tok-com` | `--ink-4` | — | `--code-com` |

The weight went too: the handoff's code palette carries meaning in hue alone,
and goes out of its way to *unbold* a type (`.code-block pre b { font-weight:
var(--fw-regular) }`).

**What the probe turned up on the way.** `"CRONUS Generic Table Proxy"`
classifies as `tok-id`, not `tok-str` — the highlighter is correctly reading
`"..."` as an AL *quoted identifier* and reserving `'...'` for string literals.
So the token being painted fuzzy-amber meant "identifier" all along. It now
matches the Object Explorer's `.tok-variableName` exactly.

The naming half of #565 stays open: two palettes still share the `tok-` prefix,
and neither can be renamed without touching the other's producer. That is a
legibility call, not a defect — the suffix sets do not intersect.

### #586 — `.progress` and `.textarea--code`, pushed upstream

Both are extractions, not inventions. The system already drew each of them; the
name was just scoped to the first block that needed it.

- **`.progress`** is `.job-list__progress` with the block taken out of the name.
  The job list composes it now, and `pages-forms.css` keeps a pointer comment
  where the four rules were.
- **`.textarea--code`** is the three properties `.folder-editor__file-content`
  and `.code-editor .cm-editor` both already set, as a modifier anything can
  take.

Three private copies retired: `ImportProgressBanner.razor.css` lost its bar
rules, `RecipeFileEditor.razor.css` lost its mono restatement, and
`TemplateJsonOverridesSection.razor.css` was *deleted* — that one rule was the
whole file.

**The indeterminate state is a pseudo-class, not a modifier.** A `<progress>`
with no `value` attribute is already indeterminate, so `.progress:indeterminate`
needs nothing kept in sync — the script drops the attribute and the sweep
starts. `appearance: none` otherwise leaves the track empty, which reads as
"0%, stuck" rather than "working"; the webkit bar pseudo-element has to be made
transparent or it paints over the gradient.

**The first sweep was wrong and a screenshot nearly passed it.** Frame-by-frame
element shots ranged 348B–836B: the gradient travelled from `-60%` to `160%`, so
for part of every 1.6s loop the bar was *completely empty* — the one thing an
indeterminate bar exists to deny, and invisible in any single screenshot. Fixed
by running `0%` → `100%` with `alternate`, which keeps the gradient inside the
track at both ends and makes the return trip the other half of the cycle. The
frames now range 789B–842B: always painted, still moving.

*Sample an animation across frames, not once.* A single shot of a moving thing
tells you what one instant looked like, which is not the same claim as "it
renders".

**Reduced motion needs no per-component guard.** `tokens.css` already carries a
global kill switch (`animation-iteration-count: 1 !important`). Two components
wrap their own animations in `@media (prefers-reduced-motion: no-preference)`
anyway — `ImportProgressBanner` and `BuildStatusPill` — which is redundant, not
wrong. Left alone; #527 will take the second one.

### #526 — the legacy alias block is gone

Eleven properties (`--blue`, `--blue-600/700/50/100`, `--on-blue`, `--good`,
`--good-bg`, `--error-text`, `--sans`, `--mono`) and the 17-line comment
explaining why the blue ramp collapsed onto `--primary-ink`. Every remaining
mention in the app is a *comment* — `Mcp.razor`, `Mcp.razor.css`, `app.css` —
recording what a surface used to be painted with.

Two things the grep-for-callers pass would have missed:

- **`Foundations.dc.html` documented them as live**, in a section titled
  "Aliases kept for the port". Prose in a `<code>` tag is not a call site, so
  no reference check flags it, and deleting the block silently makes the
  system's own spec sheet wrong. Rewritten to record that the migration
  finished and *why* the flattening was always temporary.
- **A `var()` reference is not the same shape as a class reference.** The check
  that actually proves the deletion safe is: collect every declared custom
  property and every `var(--name)` across all 46 sheets, subtract, and require
  the difference to be empty (a `var(--x, fallback)` is safe either way).
  Result: **154 declared, 141 referenced, zero undefined.** Then confirm at
  runtime — six routes, zero elements with text and an unresolved colour, no
  page errors.

Watch the matcher, though. A first pass reported 30 unused tokens including
`--ghost`, `--running` and `--disabled`; those are not properties at all —
`.btn--ghost:hover` matches `(--[\w-]+)\s*:` just as well as a declaration
does. Anchoring the declaration to `^`, `{` or `;` drops it to 18, and matters
in the other direction too: a false declaration can mask a genuinely undefined
reference.

The 18 that really are declared and unreferenced stay. Twelve are `--chart-*`,
a palette the system offers for charts we do not draw yet; `--st-fuzzy` became
unreferenced *in this PR*, when #565 took `.tok-id` off it, but it is the base
of a ramp whose `-bg` and `-text` members are still used. A design system may
ship a token ahead of its consumer. That is not the same as an alias whose only
job was to keep dead CSS resolving.

### What the audit found that is not yet filed

- **`.run-progress` / `.run-progress__fill` in `pages.css` has no caller** —
  only two comments mention it. It is *not* dead code to sweep: it is the
  indeterminate bar a running `.run-row` will want, so it belongs to #528/#549
  (ported-but-unwired), not to a dead sweep. Deleting it would remove something
  #528 needs. Left in place deliberately.
- **The design layer ships two code-block components.** `.code-block`
  (`components.css`, highlights via `b`/`i` elements, 10 call sites) and
  `.codeblock` (`pages-content.css`, highlights via `.k`/`.t`/`.n`/`.s`
  classes, 4 call sites). Different chrome, different highlight vocabulary,
  one letter apart in the name. That is a third AL palette on top of the two
  #565 is about. Worth its own issue before either grows.
- **`list_projects` does not return this project.** It filters to
  `PROJECT_TYPE_DESIGN_SYSTEM`, and the AL Workbench design system is a
  `PROJECT_TYPE_PROJECT`. `get_project` on the id in `.design/handoff/README.md`
  confirms `canEdit: true`. Go straight to the id; do not conclude from an empty
  list that there is nothing to push to.

## PR 17: the last of the legacy layer (2026-08-21)

The final milestone, and the one the health metric at the top of this doc has
been counting down to since the branch opened: **`tools.css` to zero.**

Pipelines (PR 15) and the compare screens (PR 16) both landed, so what is left
is no longer a *tool*. It is a residue spread thin across three sheets, and the
first measurement of it was a surprise worth writing down.

### What the residue actually is

317 classes live in `base.css` / `tools.css` / `admin.css` and nowhere in the
design layer. **123 of them are referenced by nothing** — not markup, not a
`.razor.css`, not a client renderer, not a test. Between the rules that select
only those classes and the rules that hang off them as descendants, that is
**~1,150 lines, or 33% of the whole legacy layer, with no caller at all.**

So the shape of PR 17 is not "port six more pages". It is: **delete the third of
the legacy layer that is already dead, then port what is genuinely left.** The
dead third is the cheapest progress on this branch since PR 8, and it makes the
rest legible — the count stops being dominated by families nobody renders.

What is genuinely left, after the sweep:

| Cluster | Files | What it is |
| --- | --- | --- |
| The source-viewer dialect | `SourceFileViewer.razor` (22), `source-viewer.js` (27 classes), `OeCompareFile`, `Compare`, `OeTreeRow` | `source-viewer*` / `sv-*` — the PR 14 remainder, and the only cluster where a **client renderer**, not markup, is the caller |
| The admin release-import family | 7 `AdminRelease*` pages + `SuggestRecipe`, `Piper` | `admin-page*`, `form-section`, `form-error/success`, `field__*`, `checkbox-label`, `oe-import-progress*`, `manage-card*`, `dc-*`/`det-*` — PR 8c's remainder, misfiled by the progress script under "Pipelines" because the filenames say `Release` |
| `DependencyPicker` | 1 | `dep-*`, 19 refs, self-contained |
| `RecipeFileEditor` | 1 | `snippet-file*`, the last of the Snippets dialect |
| Odds | `MainLayout`, `AuthLayout`, `CodeViewer`, `ConfirmDialog`, `RecipeTypeBadge`, `TemplateJsonOverridesSection`, `AdminObjectExplorerIndex` | `brand__link`, `dismiss`, `reload`, `code-viewer-host`, `visually-hidden`, `rtype`, `json-editor`, `cb-search`, `cb-toolbar` |
| The scoped tail | 2,289 lines of `.razor.css` | Invisible to the count entirely. `Translator.razor.css` is 403 of it |

### The slices

1. **17a — the dead sweep.** Delete every rule with no caller. **Done** — see
   the record below.
2. ~~**17b — the source-viewer dialect.**~~ **Done**, and it turned out not to
   be a port at all — see the record below.
3. ~~**17c — the admin release-import family.**~~ **Done**, and taken ahead of
   17b — see the record below. It turned out to be 17 files, not seven, because
   `.form-section` reached into the Cookbook and Piper as well.
4. ~~**17d — `DependencyPicker`, `RecipeFileEditor`, and the odds.**~~ **Done.**
   `RecipeFileEditor` and the odds went with 17c; this was the picker alone.
5. ~~**17e — retire `base.css` and `admin.css`.**~~ **Done**, and it took
   `tools.css` with them — see the record below. All four housekeeping issues
   answered.
6. ~~**17f — the scoped `.razor.css` tail.**~~ **Measured, and retired as a
   milestone.** The tail is fully tokenized (zero hardcoded colours in 39
   sheets); what debt exists is 78 design-layer class names restated across 23
   files, a third of which is already filed as #527/#528/#549/#562. Folded into
   the backlog — see PR 18.

**A correction to that commit message.** It says Piper's textareas picked up
`--input-border` in place of `--border-strong` "because the port took the legacy
rule away". They did not. Piper's screenshot is **bistable**: the page is
`@rendermode InteractiveServer` and Blazor's own autofocus lands on the input
*after* the harness calls `blur()`, so a run catches either the focused border
or the plain one. Two runs of the *unchanged* build differ by the same 238
bytes. Nothing changed. The harness now blurs, waits, and blurs again, which
makes the shot deterministic.

*A screenshot diff is only evidence once the harness has a stable null.* The
control run caught this the first time (PR 17a) and was not run the second.

### PR 17e — the last of it (2026-08-21)

**`tools.css` and `admin.css` are deleted. `base.css` is `app.css`, 204 lines,
and not one of its rules names a class the design layer also defines.** That
last sentence was the test for whether the file could keep existing, and passing
it is what let the whole design-layer bridge retire.

`5,472 → 0`. The metric the branch opened on, spent.

**What it found, which is the point of doing it rather than declaring victory:
every dialog in the app was rendering the wrong form styling.** The bridge
restored the component values under `.page` and `.auth`, and a dialog is mounted
*after* the page's closing `</div>` — so `PipelineEditorDialog`,
`ReleaseBuildDialog`, `ReleasePipelineEditorDialog`, `CompareReleasePickerDialog`
and `ConfirmDialog` all got `display: flex`, a 16px bottom margin and
**UPPERCASE 12.5px labels** while every page beside them rendered sentence-case
13px. Deleting the legacy `.field` / `.field__label` fixed all five at once,
which is what the bridge was always deferring. Confirmed by opening a dialog and
reading the computed style, before and after.

The audit that made the deletion safe: 28 routes, every element carrying a
bridged class (`.field`, `.field__label`, `.form-grid`, `.card`, `.data-table`,
`.audit`, `.section-label`), asking whether it had a `.page` or `.auth` ancestor.
**None did not** — so the legacy rule had no caller the bridge was not already
overriding, and both halves could go together. The dialogs are exactly the case
that audit was designed to surface, and it surfaced them.

Also settled, each of which was an open issue:

- **[#529] `.data-table`** — the second definition is gone, so the design
  system's cell metrics apply everywhere instead of only under `.page`. The
  admin tables are a row taller and their heads smaller, which is the handoff's
  sizing.
- **[#537] the collision class** — with one definition per name, there is
  nothing left to collide. The two per-page patches in
  `NewWorkspace.razor.css` / `NewExtension.razor.css` that predated the bridge
  are now comments explaining why they are empty.
- **[#525] link colour** — `app.css` owns it. The design layer styles no bare
  `a`, so this is the app's, and it sits on `--primary-ink` (6.5:1 on `--bg`),
  the token the system designates for teal text. `--primary` is 2.5:1 and is for
  accents and fills, never words. *The aliases already pointed there — I checked
  before claiming a contrast bug, and there wasn't one.*
- **[#526] the `--blue*` aliases** — every call site now names the real token
  (`--primary-ink`, `--primary-weak`), as do `--good`, `--sans`, `--mono` and
  `--error-text`. The aliases are dead but still declared: `tokens.css` is
  parity-locked, so deleting them is a DesignSync round-trip and the issue stays
  open for that half.
- **[#565] two `.tok-*` palettes** — both now live in `code-editor.css` and they
  do **not** collide: no name appears in both. The problem was legibility, so
  they are two labelled blocks with the rule of thumb written down —
  *abbreviated is ours, spelled-out is CodeMirror's lezer tag name*.
- **[#580] `.page-head--sticky`** — the issue's premise is stale. `shell.css`
  has defined it since PR 4; what is true is that **no page applies it**, which
  makes it a member of the #549 unused-component family, not a missing rule.

`.badge--*` became `.status-pill--success/warn/danger` (whether a pill belongs in
a table row at all is #524, still open — if that lands on `RowStateIcon`, it is
one line in `AdminReleaseManage`). `.data-table__row--muted` became `.u-muted`.
`.muted` and `.caption` were the app's own duplicates of `.u-muted`; the last
callers, all in `source-viewer.js`, moved and both rules went.

Fourteen asset tests named a sheet that no longer exists. Repointed at the ones
that survive — `app.css`, `code-editor.css`, `source-viewer.css` — so the "a
retired class must not come back" guards still cover every sheet that loads after
the design layer.

`.design/progress.py` says so at the top now: the counts should stay at zero, and
a number climbing is a regression rather than progress.

[#529]: https://github.com/mtaanquist/ALDevToolbox/issues/529
[#537]: https://github.com/mtaanquist/ALDevToolbox/issues/537
[#525]: https://github.com/mtaanquist/ALDevToolbox/issues/525
[#526]: https://github.com/mtaanquist/ALDevToolbox/issues/526
[#565]: https://github.com/mtaanquist/ALDevToolbox/issues/565
[#580]: https://github.com/mtaanquist/ALDevToolbox/issues/580

### PR 17d — the dependency picker (2026-08-21)

**`tools.css` 420 → 332.** The last real port on this branch, and the shortest,
because the component it wanted was already on the page next to it.

`.dep-row` — a checkbox, a name, a publisher, a tinted selected state — is
`.module-card`, which `NewWorkspace` has been picking modules with since PR 8c.
The two lists sat on adjacent screens looking like different systems for no
reason other than which sheet each was written in. The manual list is
`.sub-rows` / `.sub-row`; the four add-fields are `.input`; the inline error is
`.field-error`; the empty catalogue is an `.empty-state`.

`DependencyPicker.razor.css` keeps the three pieces of geometry the design layer
has no slot for: the category subheading, the version box that appears inside a
card once it is ticked, and the two grid templates.

Four things worth writing down:

- **The version input lives inside the `<label>`, and that is safe.** A click on
  interactive content does not forward to the label's own control, so typing a
  version cannot untick the dependency. It looked like a bug in the old markup
  too and is not.
- **A second uppercase label directly under the first reads as the same level,
  not a level down.** The page already heads the block "Dependencies"; the
  picker's own "From the catalogue" made three tiers that all looked alike. It
  is gone — the category names are the heading that list needs.
- **The id column is sized in `px`, not `ch`.** A grid track's `ch` resolves
  against the **grid container's** font, which is the sans body text, not the
  mono the cell renders in — so `25ch` came out a third short and the GUID
  truncated. Verified by asking the browser (`scrollWidth > clientWidth`), not
  by looking at it.
- **The id stays on screen rather than in a tooltip.** It is what `app.json`
  keys on, the user typed it by hand, and a mistyped GUID is the failure this
  list exists to catch.

Six bUnit tests select by class name and failed on the rename, which is them
working. Repointed, and the empty-catalogue test now asserts both halves of what
an empty state has to say — that there is nothing here, and what to do instead.

### PR 17b — the two families that were never legacy (2026-08-21)

**`tools.css` 1,289 → 420.** Nothing was ported and nothing was deleted except
two dead `@keyframes`: 868 lines moved to files named for what they are.

The audit came first, and it is why this is a move rather than a port. Every
`.sv-*` rule rides on an element that already carries its design-layer
counterpart — `.sv-list` beside `.olist`, `.sv-section` beside `.pane__sec`,
`.sv-tree-count` beside `.pane__count`, `.sv-filter` beside `.input`. Comparing
the nine of them property by property against those siblings found **zero
redundancy**: each adds a `grid-template-columns`, a `flex-wrap`, an `overflow`
the design layer does not set. There was nothing to delete and nowhere to port
to.

- **`wwwroot/code-editor.css`** (318 lines) — everything that styles DOM
  **CodeMirror** builds at runtime. It has no design-system counterpart because
  the design system does not know the library exists, and it cannot be a scoped
  `.razor.css` because Blazor's CSS isolation stamps its scope attribute on
  elements *Blazor* renders and CodeMirror's are not.
- **`wwwroot/source-viewer.css`** (515 lines) — the Object Explorer viewer's own
  composition on top of archetype 10: the breadcrumb row, the tree's column
  template, the inspector's section headers, and the four floating pieces the
  frame has no slot for (busy indicator, toast, outline menu, reference
  tooltip). Shared rather than scoped because three components render this
  markup and `source-viewer.js` builds most of it.

Both load exactly where `tools.css` did, so the cascade is unchanged. Verified
by round-tripping the selector list — 175 selectors in, 175 out, none lost, none
duplicated — and then by rendering: 17 pages, all identical inside the noise
band, including the two CodeMirror-heavy ones (`oe-file`, `oe-compare`) which
were **byte**-identical.

What is left in `tools.css` is 420 lines and genuinely legacy: the dependency
picker (17d), `.data-table` (#529), and the `.card` / `.field*` / `.badge--*`
trio that the design-layer bridge in `base.css` already neutralises on `.page`
and which retires with it (17e).

### Was the metric wrong?

Yes, and it was ours, not the handoff's. *"`tools.css` line count — when it hits
zero the migration is done"* was written on day one, before anyone measured what
was in the file. It conflated two things: **bespoke CSS that duplicates the
design system** (should reach zero) and **CSS that happens to live in a file
called `tools.css`** (a filename). Roughly 4,180 of the original 5,472 lines
were the first kind and are gone. The other ~1,000 were never migration debt.

The handoff never claimed otherwise. Its own extension guide says *"put layout
in a page layer, not in the component layer — `pages-forms.css`,
`pages-power.css` and `pages-content.css` hold grids, sticky offsets and
page-specific composition"*, which is exactly what the viewer shell is. And it
has no `.cm-*` anywhere, because styling a third-party library's runtime DOM was
never in scope for a design system.

*A metric that counts lines in a file measures the file, not the work.* It was
still the right metric to run the branch on — ~80% of what it counted was real,
and it drove every PR from 8 to 17a. It just needed retiring one slice before
zero rather than at it.

### PR 17c — the admin release-import family (2026-08-21)

Taken ahead of 17b because 17a's sweep turned up a live defect in it, not just
migration debt: `.admin-form` and `.pe-field` are rendered by no page, so
`.admin-form label.checkbox-label` and `.pe-field .field__input` had never
matched — while `checkbox-label` and `field__input` sit on seven live admin
pages. What was actually styling those inputs was `base.css`'s
`.form-section input[type="text"], .form-grid > input[type="text"], …` element
selectors, which is also why swapping `.form-section` for `.form-sec` had to
happen in the same commit as putting `.input` / `.select` / `.textarea` on every
control. Half a port would have left the fields bare.

**17 files.** The seven `AdminRelease*` pages, `ReleaseImportMetadataFields`,
`AdminObjectExplorerIndex`, `AdminObjectExplorerHeader`, `SuggestRecipe`,
`RecipeFileEditor`, `Piper`, `RecipeDetail`, `TemplateJsonOverridesSection`,
`ConfirmDialog`, `RecipeTypeBadge`.

**`tools.css` 1,536 → 1,318. `admin.css` 255 → 113. `base.css` 566 → 465.**
Stale refs 183 → 61; components still carrying legacy 24 → 9.

The mapping, for the record: `.admin-page*` → `.page` + `.page-head`;
`.form-section` / `.admin-section` → `.form-sec`; `.field__input` → `.input` /
`.select` / `.textarea`; `.field__caption` → `.field__hint`; `.checkbox-label` →
`.check`; `.form-error` / `.form-success` → `.alert--danger` / `.alert--success`;
`.manage-card*` → `.card` (+ `.card--danger`); `.det-*` / `.dc-*` → `.card` +
`.dash-cols`; `.data-table--card` → plain `.data-table`; `.cb-toolbar` /
`.cb-search` → `.filter-bar` + `.search`; `.oe-status-error` → `.field-error`;
`.pill-tabs--legacy` → `.pill-tabs` (Piper was the last caller, so the whole
gated block went); `.visually-hidden` → `.u-sr`; `.hard-delete-form` → `.u-row`.

Four things that were **not** class swaps:

- **Five bare `<p class="muted">` empty states became `.empty-state` with a
  button.** "No Releases yet. Import one now." is a sentence with a link in it;
  the UX definition of done asks for a next step the reader can press. The admin
  index, the artifacts tab, the modules list, the translations page and the
  recipe file editor each got one.
- **Every trailing submit `<section class="form-sec">` became `.form-actions`.**
  `.form-sec` is `display: grid`, so a lone button in one stretched the full
  column — the Import Release button had been a 1,140px-wide primary bar. Same
  for the four forms on Manage that carried `class="form-grid"`: that is the
  design system's *two-column field grid*, and their children are sections, so
  it was putting a form section beside a Save button.
- **`SuggestRecipe`'s Details moved onto `.form-grid` properly**, with
  `.field--full` on the long fields. The legacy page capped the column at 560px
  via `.form-grid--narrow`; without a cap every input ran the full width, which
  is worse for a title field than either. Two columns is the archetype's own
  answer. Its Keywords placeholder was also double-encoding its own quotes and
  advertised `&quot;document attachments&quot;`; fixed while in there.
- **Three components kept a small scoped sheet rather than borrowing a name.**
  `ImportProgressBanner`, `RecipeFileEditor` and `TemplateJsonOverridesSection`
  each need one thing the design layer has no component for — a styled
  `<progress>`, a flex row whose fields grow, a mono `.textarea`. The design
  layer *does* each of those once, but always BEM-scoped to a block these are
  not (`.job-list__progress`, `.folder-editor__file-content`). Borrowing one
  puts a job-list class inside an alert. Filed as
  [#586](https://github.com/mtaanquist/ALDevToolbox/issues/586) — the fix is an
  additive `.progress` and `.textarea--code` upstream, which is a parity-locked
  sheet and so needs a DesignSync round-trip.

`.rtype` moved the same way, from `tools.css` to `RecipeTypeBadge.razor.css`: a
recipe's *type* is a category, not a lifecycle state, so it is neither a
`.status-pill` (reserved for states) nor a `.tag` (one neutral colour).

**Two duplicates 17c left behind, and the detector blind spot that caused them.**
`.rtype` and `.snippet-file` moved to scoped sheets, but the tools.css copies
survived the sweep: the dead-class detector greps the whole app for the name,
and `RecipeFileEditor.razor.css`'s own header comment says *"was `.snippet-file`
in tools.css"*. A component documenting its history kept alive the rule it had
just replaced. `.rtype` survived for the sibling reason — the new **scoped**
sheet defines it, and the detector does not care which file a definition is in.

This is the same blind spot recorded in [#573], now in the other direction: the
class extractor used to read a comment *above* a rule as naming a live class,
and the liveness grep now read a comment *anywhere in the app* the same way. The
pruner strips comments from the corpus before the grep, with the `//` pattern
anchored to line starts so a URL keeps its scheme — over-stripping the corpus
makes a **live** class look dead, which is the direction that ships bugs.

That fix found 63 more dead lines in `base.css` and `admin.css` that 17c had
retired and the detector had been reading out of its own commentary.

`.card` looked like a third one and was not: `tools.css` does carry a second
`.card` with `--r-lg`/`--shadow` against the component layer's `--r`/`--shadow-xs`,
but `base.css`'s design-layer bridge already re-asserts the component values at
`.page .card` (0,2,0). Confirmed through the engine's own matched-rule list
rather than by reading the sheets. It retires with the bridge in 17e.

### Two silent failures, and the tests that now catch them

Both were found by writing the guard, not by looking at the page.

**A class the markup names that no stylesheet defines.** 17c produced two in one
sitting. `.hint` was swapped in for `.muted` across six files because a grep for
`\.hint\b` matched `.hint-details` — a hyphen is a word boundary — and there is
no bare `.hint`. `.file-row` was written as a hook and never given a rule.
Nothing errors; the element renders with browser defaults, which on a caption
looks close enough to right to survive a screenshot.
`UnstyledMarkupTests.Every_class_the_markup_names_is_defined_by_some_stylesheet`
walks the set. Its allow-list is short and each entry says why — the default is
that a name in a `class` attribute is a name somebody meant to style.

**A `.select` with no `.select-wrap`.** `.select` sets `appearance: none`, so
the browser's arrow is gone and the wrapper's `.select-wrap__caret` is the only
thing left saying "this opens a list". The test found **20 of them across 11
files** — `AuditLogPage`, `SiteAdminAudit`, `AdminTranslationMemory`,
`Translator`, `PipelineBuilds`, `ProjectDetail`, `SourceFileViewer`, and four
dialogs — none of them 17c's doing, all shipped by earlier PRs. Confirmed in the
browser before believing it (`appearance: none`, `background-image: none`, no
wrapper, no caret) and fixed in all 20; a dropdown that looks like a text box is
a defect whoever wrote it.

*Writing the first version of that test double-wrapped four selects that already
had a wrapper carrying a page class beside the component one
(`class="select-wrap tr-langsel"`) — it matched the whole attribute instead of
the class token. Caught by rendering. The test matches the token now.*

### PR 17a — the dead sweep (2026-08-21)

**`tools.css` 2,448 → 1,536. `base.css` 685 → 566. `admin.css` 374 → 255.**
1,150 lines, 133 classes, no page changed.

The interesting part was not the deletion. It was **how many ways a class can
look dead and not be**, and every one of them was found by a check rather than
by reading:

- **15 classes are composed at runtime from a literal stem.**
  `` `cm-diff-${row.kind}` `` in `source-viewer.js` builds `cm-diff-inserted` /
  `-deleted` / `-modified` / `-imaginary`; `` `cm-diff-gutter-${kind}` `` builds
  four more; `` `oe-diff-overview__mark--${run.kind}` `` four more; and
  `class="tok-@tok.Cls"` in `RecipeDetail.razor` builds the whole Cookbook
  highlighter palette. A grep for the full name finds nothing. Deleting them
  would have taken the colour out of every diff on the branch.
- **7 classes are emitted by CodeMirror itself.** `.cm-panel`, `.cm-panels-top`,
  `.cm-panels-bottom`, `.cm-search`, `.cm-textfield`, `.cm-button`,
  `.cm-gutterElement` appear in no file we wrote, because the library puts them
  in the DOM. Our rules are what make the find-in-file panel match the app.
- **The dangerous direction is a rule under a dead ancestor.** `.admin-form` and
  `.pe-field` are rendered by no page, so `.admin-form label.checkbox-label` and
  `.pe-field .field__input` have never matched anything — while
  `checkbox-label` and `field__input` are on seven live admin pages. The sweep
  is right to delete both rules, but the finding underneath is that **those
  seven pages have been rendering unstyled checkboxes and inputs this whole
  time**, and the count was calling them "styled by a legacy sheet". That is
  17c's problem now; recorded here so it is not rediscovered as a regression.

That last one is also why the stale-ref count fell by 12 without a single page
being ported: `checkbox-label`, `field__input` and `code-viewer-host` stopped
being *legacy* classes, because they stopped being classes any shared sheet
defines. **The metric can go down for a reason that is not progress.** Worth
knowing before quoting it.

**How it was verified**, because a class-set diff cannot see the dangerous
direction (see [#573] and the note in "Gotcha: the old rules still win"):

- Every one of the 2,290 tests green, including the renderer-walking guards that
  exist for exactly this failure (`Every_element_the_client_renderers_build_is_styled`).
- **17 pages screenshotted before and after** and compared pixel by pixel:
  `home`, `cookbook`, `cookbook/suggest`, `account`, `piper`, the admin Object
  Explorer index, all four import tabs, release manage, release modules, a
  release detail, a source file, a file compare, templates, new workspace.
  15 were **byte-identical**. The two that differed did so by 23 and 48 bytes
  out of 4.3M — and a control run of *after* against *after* produced
  differences of the same size on pages that were byte-identical, so the band is
  renderer noise (corner antialiasing on a rounded box), not a layout change.
  **Take the control run.** Without it, "two pages differ" reads as a
  regression, and 48 changed bytes is indistinguishable from a real one-pixel
  shift until you know what a null result looks like on this machine.

**Writing the guard found a fifth dead rule the sweep had protected.** The
test derives the diff vocabulary from `SideBySideDiffSerializer` rather than
restating it, and failed on `imaginary`: `MapKind` names four kinds, but
`SerializeSide` skips `Unchanged` **and** `Imaginary` before serialising, and
`SerializeFillers` turns each run of imaginaries into a `.cm-diff-filler`
*widget between lines* instead. So `.cm-line.cm-diff-imaginary` and
`.oe-diff-overview__mark--imaginary` have painted nothing since the fillers
landed — the `COMPOSED` allow-list saved them from the sweep, being the right
answer for their three siblings and the wrong one for them. Both rules are gone,
and the two comments that still described imaginary rows as tinted lines (one in
`tools.css`, one in `source-viewer.js`) now say what actually happens. The test
reads `SerializeSide`'s guard clause, so widening it to emit imaginaries fails
here until something paints them.

*An allow-list that stops a sweep is a claim that wants its own test — and the
test is what checked it.*

The pruner itself is at `scratch/bc-design/prune-dead-css.py` (gitignored). It
parses real blocks rather than regexing `{...}` — a regex matches the
*innermost* rule and leaves an empty `@media` shell behind, and 12 of these
sheets' rules are inside one. It prunes selector lists per-branch, so
`.dead, .live {}` keeps `.live`.

[#573]: https://github.com/mtaanquist/ALDevToolbox/issues/573

## PR 15: the Pipelines / Projects gap, scoped (2026-08-21)

The gap the plan never had a number for. `.design/progress.py` calls it 359 refs
across 17 files, but **eight of those files are not this tool** — the bucket
matches on `Release` in the filename, so `AdminReleasesImport*`,
`AdminReleaseManage`, `AdminReleaseModules` and `AdminReleaseTranslations` (97
refs between them) fall in here while belonging to the admin edit-form family of
PR 8c. The real gap is **262 refs across nine files**:

| File | Refs | Lines |
| --- | --- | --- |
| `Pipelines/PipelineBuilds.razor` | 77 | 715 |
| `Projects/ProjectDetail.razor` | 69 | 983 |
| `Pipelines/ReleasePipelineDetail.razor` | 45 | 489 |
| `Shared/PipelineEditorDialog.razor` | 24 | 535 |
| `Shared/ReleasePipelineEditorDialog.razor` | 14 | 371 |
| `Shared/ReleaseBuildDialog.razor` | 13 | 263 |
| `Shared/CompareReleasePickerDialog.razor` | 9 | 124 |
| `Pipelines/ReleasePipelinesBrowser.razor` | 6 | 240 |
| `Pipelines/PipelinesBrowser.razor` | 5 | 354 |

`ProjectsBrowser.razor` is already clean — PR 6b took it.

### What the vocabulary looks like

175 distinct legacy classes, and **154 of them are used nowhere else in the app**.
That is the good news: this is a self-contained dialect, not a shared layer, so
almost all of it can be deleted rather than migrated. The 21 that do leak out are
generic chrome owned by other buckets (`confirm-modal*`, `field__input`,
`form-section`, `muted`, `state`, `ra__*`, `dc-*`/`det-card`/`det-col`/`det-grid`
shared with `AdminReleasesImportArtifacts`).

The trap is the same one PR 14d hit with `.oe-compare-file__panes`: **the
`.det-*` head (7 classes) is shared by all three detail pages**, as are
`.art-page`, `.art-fail`, `.rel-empty*`, `.pe-field`, `.set-sec-head` and the
utility tail (`av`, `cur`, `sm`, `td`, `meta`, `mono`, `desc`, `dotsep`). Porting
one page alone strands them for the next caller. So the shared chrome is pulled
out **first**, as its own component, rather than three times.

### What it ports onto

Nothing needs pulling from the design project — every target already shipped.
The mapping, measured against `components.css` / `pages.css` / `pages-forms.css`:

| Legacy | Design layer | Note |
| --- | --- | --- |
| `.det-bc` `.det-head` `.det-id` `.det-title` `.det-sub` `.det-actions` | `.page-head` + `__crumbs` `__title` `__sub` `__actions` | `.det-pico` (the 26px tinted tool glyph) has no counterpart — divergence to record or drop |
| `.art-page` | `.page` | |
| `.det-card` `.dc-head` `.dc-t` `.dc-body` | `.card` `.card__head` `.card__title` `.card__body` | |
| `.det-grid` `.det-col` | `.dash-cols` | Two-column detail layout, already in `pages.css` |
| `.hist-*` (build history), `.del-*` (delivery history), `.pipe-row` | `.run-list` / `.run-row` + `.run-progress` + `.commit-chip` | **Currently unused CSS** — part of #549. The `.run-row` grid (`92px 1fr auto auto auto auto` = id, title+sub, state, dur, time, acts) is a direct fit, and `.run-progress` gives the in-flight build a live bar it does not have today |
| `.rel-empty` `.rel-empty-h` `.rel-empty-p` | `.empty-state` `__title` `__text` `__action` | |
| `.logbox` `.logbox-h` `.lh-l` | `.code-block` `.code-block__bar` `.code-block__name` | |
| `.pj-meta` `.pj-row` `.pj-k` `.pj-v` | `.meta-row` `.meta-item` `__label` `__value` | |
| `.set-nav*` `.set-panel*` `.set-content` `.set-grid` `.set-foot` | `.settings` `.settings__tabs` `.settings__body` `.settings__aside` `.card` `.card__foot` | See the open question below |
| `.audit-row` `.audit-k` `.audit-v` `.audit-t` | `.audit` family in `pages-forms.css` | |
| `.env-*` `.ewe-*` | `.data-table` | It is a table of environments |
| `.type-pill` | `.status-pill` | |
| `.confirm-modal*` | `.confirm-dialog*` + `.modal-backdrop` | Also unused today; `ConfirmDialog.razor` still renders the legacy one |
| `.ra__pop` `.ra__item` `.ra__divider` `.ra__solo` | `.ra__menu` `.menu__item` `.menu__sep` | This is #529 — see below |

Two of those rows are **whole ported-but-unused component families finally
getting a caller** (`.run-list`/`.run-row`, `.confirm-dialog`), which is the
cheapest kind of progress on #549 there is.

### #528 and #529 both get answered here

- **#528** asked for the div-based run and delivery histories to move onto
  `.run-list`/`.run-row`. The archetype-sheet prose then said run history should
  be *"a real `.data-table`"* and `.run-list` was only for *"card-like histories
  that are not tabular"*, which read as a contradiction. Looking at the rendered
  pages settles it: the build history is a flat six-column row with a two-line
  main cell, and `.run-row` is literally that grid — take `.run-list`. The
  **delivery** history is not tabular at all (each run expands into per-app
  result rows), so it takes `.run-list` too and keeps its sub-rows.
- **#529** is the `.ra__menu` collision, and it is *live*: `tools.css` carries a
  comment explaining that it resets `position`/`display`/`top`/`right`/`z-index`
  because the design system means the popup by that name and we mean the
  `<details>` wrapper. The two Pipelines browsers cannot go clean without it.

### The slices

Six, smallest blast radius first. The first two are shared-component work the
gap *depends* on rather than gap work itself, which is exactly why they go first
— they keep the three page PRs about their own bodies.

1. ~~**15a — `.confirm-modal` → `.confirm-dialog`.**~~ **Done** (`cc48c62`).
2. ~~**15b — #529, the `.ra` family.**~~ **Done.** 152 lines of `tools.css`.
3. ~~**15c — the shared detail head.**~~ **Done.** Not as a component — see the
   PR record below. `.page-head` on all three pages at once; `.art-page` →
   `.page`; `.rel-empty*` → `.empty-state`; `.art-fail` → `.alert--danger`.
4. ~~**15d — `PipelineBuilds`' body** (+ `PipelineEditorDialog`, `ReleaseBuildDialog`).~~
   **Done.** `.card`, `.run-list`, `.sub-rows`, `.meta-row`, `.code-block`,
   `.commit-chip`, `.status-pill`. Closes #528 and the list half of #527.
5. ~~**15e — `ReleasePipelineDetail`** (+ `ReleasePipelineEditorDialog`).~~
   **Done.** `.run-list` with per-app sub-rows; closes the rest of #527 and
   #528.
6. ~~**15f — `ProjectDetail`** onto the settings archetype.~~ **Done.** The last
   caller of the dialect, and it swept the rest out of `tools.css`.

### ~~Open question for 15f~~ — answered 2026-08-21: the tabs move

The maintainer took the faithful call. It shipped as **15f-a**, with one
deviation from `Account.razor`'s version of the same move that this page's
single Save forces — see that section. What is left of 15f is the panel
internals. The original question, kept for the reasoning:

`ProjectDetail` groups five concerns behind a **left vertical sub-nav**
(`.set-nav`), holding the section in page state. Archetype 7 and our own
`SettingsPage.razor` both use a **horizontal `header-tab` row where each tab is
its own route**. Moving it is the faithful call and would let the page drop onto
`SettingsPage` wholesale — but it turns one route into five and changes how a
half-filled create form behaves. Raise it with the maintainer when 15f starts;
everything before it is unaffected.

### PR 15c — the shared detail head (2026-08-21)

All three detail pages moved together, which was the point: `.det-bc` /
`.det-head` / `.det-id` / `.det-pico` / `.det-title` / `.det-sub` /
`.det-actions` were shared by `PipelineBuilds`, `ProjectDetail` and
`ReleasePipelineDetail`, so porting one would have deleted nothing.

`tools.css` 2,734 → 2,693, `base.css` 696 → 686. Retired: the seven `.det-*`
head rules, `.art-page` (+ its dead `.sub` / `.plain-link` descendants),
`.art-fail`, the five `.rel-empty*` rules, `.det-bc .cur`, and `.dotsep` in
`base.css`, which had no callers left once the three sub-lines became prose.
`.det-grid` / `.det-col` / `.det-card` and `.art-app__meta` stayed on purpose —
they are the body, and they go with 15d and 15e.

**Superseded in part — see "The archetype nobody had seen" below.** The head
went onto `.page-head`; the detail archetype uses `.detail-head`, and the crumbs
belong outside it. Reworked. The component decision below still stands.

**No `DetailPageHead` component, against the plan's own wording.** The plan said
"one `DetailPageHead` on `.page-head`", and that was written before counting:
**44 files already hand-roll `.page-head`**. The two components that do wrap it
(`SettingsPage`, `TabbedPage`) wrap a whole archetype, not a head. A fourth
wrapper used by three pages would have made the pattern *less* uniform, and the
stranding problem the plan was actually solving is a CSS one — once all three
are on `.page-head` the rules are shared by definition and no component is
needed to enforce it. What replaces the component as the guard is
`DetailHeadTests`.

**The handoff has a worked example of this exact page, and nobody had looked.**
`ComponentsPanel.dc.html:336` renders `Projects › CRONUS Sales Extension ›
Pipelines` with the sub-line *"3 pipelines - 2 environments - last run 4 minutes
ago"*. No tool glyph, no per-item icons, plain prose. That settled three
questions the plan had left open — including "`.det-pico` has no counterpart —
divergence to record or drop" — as *drop, faithfully*. Divergence row 62 records
it anyway, because three visible things left the page.

**What the empty states cost, and why the test pins it.** `.rel-empty-ico` went
on the `<Icon>` itself via `Css=`; `.empty-state__icon` is a 42px tinted grid
box that *centres* a glyph, so it belongs on a wrapping element. Translating the
markup mechanically produces a 42px-tall stretched `<svg>` and no tile — a shape
bug, not a spelling one, so `DetailHeadTests` checks it as a shape.

**`.art-fail` went to `.alert alert--danger`**, which is nine call sites across
six files including the three editor dialogs — none of them 15c's pages. Worth
doing here rather than three times later, and it is why this slice touches the
dialogs at all. The one exception is `ProjectDetail`'s update-window error,
which is a field-level message and took `.field-error`.

**Upstream gap found, not fixed:** `.page-head--sticky` is named in
`DESIGN-SYSTEM.md`'s vocabulary list and used by `PageSettings.dc.html`, but
**no rule for it exists in the handoff's own `components.css`**. Our copy is
byte-identical, so the class is inert in both. We do not stick our page heads
(see `SettingsPage`'s own note), so nothing is broken here — but the sheet
promises a modifier it does not define, and the next person to reach for it will
find nothing. Filed as
[#580](https://github.com/mtaanquist/ALDevToolbox/issues/580).

Verified rendered, light and dark, at 1400px: all three heads, both empty
states, both not-found states, and the danger alert. Suite 2,241 passed / 0
failed; the four new guards were mutation-tested (a returning `.det-sub` rule, a
stale `class="rel-empty"`, a page that loses its crumbs, and the glyph wearing
the tile class) and each failed exactly one test.

### PR 15d — the pipeline detail body (2026-08-21)

The heaviest single file on the branch, 77 refs, now at zero. `tools.css`
2,693 → 2,501: the whole `.lb-*` / `.app-*` / `.hist-*` / `.logbox` / `.pj-*` /
`.repo-*` / `.cmp-*` / `.chg-*` / `.nb-*` dialect went, plus `.al-date`,
`.al-sublink`, `.hdot`, `.sha` and `.art-app__meta`. What stayed is
`.det-grid` / `.det-col` / `.det-card` / `.dc-*`, whose last caller is
`AdminReleasesImportArtifacts` — PR 8c's family, and it retires with them.

**#528 and the list half of #527 are closed by the same change.** The build
history is a `.run-list` of `.run-row`s now, and a run row carries its state as
the 4px right keyline plus a glyph and the state word — never a pill. The
in-flight row gets `.run-progress`, a component that had shipped with no caller
at all (#549). `BuildStatusPill` survives only where a pill is still correct:
the Latest-build **card head**.

*(Two claims in that paragraph aged badly, checked in PR 18c. `.run-progress`
was **not** wired up — the run row names it only in a comment, as the analogy
for how `.rp-apps` spans the six tracks — so it is still on #549's
ported-and-unused list. And the card head moved onto `.status-pill` as well,
which left `BuildStatusPill` with no caller anywhere; it is deleted. The build
history on `PipelineBuilds` is a `.data-table--edge`, not a `.run-list`; the
`.run-list` is the delivery history on `ReleasePipelineDetail`.)*

**One mapping table, three vocabularies.** A `.data-table` row says
`is-<state>`, a `.run-row` says `run-row--<state>`, a card head says
`.status-pill--<tone>`. That is three ways to spell one fact, which is exactly
how a keyline and a glyph end up disagreeing. `RowStateIcon` already owned the
`.data-table` mapping and its doc-comment already said why; it now exposes
`RunState`, `Glyph`, `StateLabel`, `Spins` and `PillTone` off the same table.

**Where a card was wrong.** `.run-list`, `.sub-rows` and `.meta-row` each carry
their own surface, border and radius — they are containers, not contents.
Wrapping them in a `.card` draws a box inside a box. Build history,
Repositories and the Pipeline rail became a titled section (`.pb-sec`) over a
bare component instead; only Latest build, Build log and Compare builds are
real cards, because only those have a head *and* a padded body.

**Three additions to the design layer**, all pushed upstream:

- ~~`.dash-cols--rail` and `.dash-col`.~~ **Withdrawn** — see below. I wrote
  "the system has no detail archetype at all — `PageDetail.dc.html` does not
  exist". It does, and it has no rail.
- ~~`.run-row__acts`.~~ **Held.** The gap is real — `.run-row` declares six
  columns and the sheet names five — but the build history is a `.data-table`
  now and `.run-list` has no caller until 15e. It goes upstream when that PR
  proves it.
- `.field-warn`, the third twin beside `.field-error` and `.field-ok`. Releasing
  outside a customer's update window is *permitted* and merely recorded, so
  saying it in the error colour tells the user they cannot do a thing they can.

**One correction to an existing rule:** `.run-progress` cancelled the row's
padding with a single symmetric negative margin, but `.run-row` pads
asymmetrically (`padding: 0 var(--space-4)` then `padding-right:
var(--space-5)`). The bar stopped 4px short on the right, just before the
keyline it should run into. Only visible on a row that is actually in flight,
which is why a seeded in-flight build mattered.

**Two things I judged and one I could not see.**

- The `.det-card.hero` ring (a blue border plus a 1px glow on Latest build) is
  gone. The system's only card emphasis is `.card--danger`; a focal card that is
  already first on the page and titled does not need a ring to be found.
- The history's duration cell was showing the artifact count, which is not a
  duration. It shows a real one now (`4m 00s`, from `StartedAt`/`FinishedAt`),
  and the count moved next to the `.zip` button, where the thing it describes is.
- **The extension picker's populated state was not verified rendered.** The
  discovery poll never settles on this machine — the dialog sits on "Discovering
  extensions..." indefinitely — and that reproduces identically on the *pre-15d*
  dialog, so it is not this PR's doing. Everything else in both dialogs was
  checked in both themes. Worth returning to before 15 closes.

Suite 2,239 passed / 2 failed, and the two are
`Shared_sheets_match_their_handoff_copy_byte_for_byte` on `pages.css` and
`components.css` — the parity guard, red until the DesignSync round pushes the
four additions above.

### The archetype nobody had seen (2026-08-21)

`PageDetail.dc.html` has been in the design project all along. `.design/handoff/`
vendors **eight** screens out of twenty-odd, and this was not one of them — the
same hole #570 tracks for `PageObjectExplorer.dc.html`. Worse, its CSS has been
in our own `pages.css` since the token drop (`.detail-head`,
`.detail-head__title-row`, `.detail-head__title`, lines 86-88, byte-identical
with the handoff) and **four pages already use it** — `RecipeDetail`,
`AuditDiffPage`, `SiteAdminAuditDiffPage`, `TemplateDetail`. `RecipeDetail`'s own
file comment spells it out: *"composed from its detail pieces — `.detail-head`
for the title row and `.meta-row` for the facts."*

I wrote "the system has no detail archetype at all" into this document and into
PR 15d's commit message. One grep would have caught it. It is now vendored.

**What the screen settles**, having found it renders our exact page — a pipeline
called "Build and test" with a run history:

| | 15c / 15d shipped | The archetype |
| --- | --- | --- |
| Crumbs | inside `.page-head` | a sibling **above** `.detail-head` |
| Title + state | `.page-head__title`; pill in a card head | `.detail-head__title-row` — pill **beside the title** |
| The facts | a 300px rail of cards | a full-width `.meta-row` strip; **no rail exists** |
| Run history | `.run-list` / `.run-row` | `.data-table data-table--edge` |

The `.run-list`-versus-*"a real `.data-table`"* contradiction that #528 left open,
and that I resolved in 15d by looking at our own rendered page, is settled the
other way by the archetype. `.run-list` keeps its place for the *delivery*
history in 15e, which is genuinely not tabular — each run expands into per-app
sub-rows.

**Reworked, in one pass across all three pages** (the same argument that made 15c
one PR rather than three): crumbs lifted out of the head, `.page-head` →
`.detail-head` + `__title-row`, the rail dissolved into a `.meta-row`, the build
history onto `.data-table--edge` with `RowStateIcon` in the state column, a
`.commit-chip` commit cell and `__num` duration. `.dash-cols--rail` / `.dash-col`
withdrawn from `pages.css` before they were ever pushed. The Latest-build card
lost its pill: the head carries that state now, and two pills saying one word
read as two facts.

`DetailHeadTests` grew three guards for it — the head vocabulary, crumbs-before-
head (the two archetypes differ and copying the wrong one is easy), and
one-pill-per-state.

**The lesson, again, in a new shape.** PR 14c's was *the states you did not seed
are the states nobody reviewed*. This one is its sibling: **a screen that is not
vendored is a screen nobody diffs against** — and it stayed invisible even though
its CSS was in our tree and four pages were using it. Before the next archetype
port, list the design project and vendor what is missing.

### PR 15e — the delivery history (2026-08-21)

Started by reading `PagesStandard.dc.html` rather than guessing, which is the
rule 15c/15d's rework earned. Its spec text settles the component choice in one
line: *"`.run-list` / `.run-row` stay in `pages.css` for **card-like histories
that are not tabular**."* A delivery is exactly that — each release expands into
one sub-row per app it installed, which no table column can hold. So **#528's
two halves genuinely go different ways**: the build history is a
`.data-table--edge` (15c/15d rework) and the delivery history is a `.run-list`.
The plan had read it that way from the start; 15d is what drifted.

The same sheet confirms the rest of the detail archetype independently — *"Run
history is a real `.data-table` with sortable columns ... not a bespoke row
layout"* and *"Status is a 4px right edge bar plus a leading glyph, no label
column"*.

`tools.css` 2,501 → 2,476, and this is the last of the `.del-*` / `.rpe-*` /
`.rb-*` dialect. **`DeliveryStatusPill` is deleted** — a list row never carries
a pill — which closes the rest of [#527](https://github.com/mtaanquist/ALDevToolbox/issues/527).

**The per-app dots were colour-only.** The old `.del-app-dot--ok/fail/busy/skip/
pending` were five coloured 8px circles with the state word on a `title`
attribute and nowhere else. That is precisely what the design system's status
rule exists to prevent, and it had been sitting on a delivery page where the
states that matter are "installed" and "failed". They are `RowStateIcon` glyphs
now, with `aria-label` **and** `title`, tinted by state — three signals, none of
them colour alone. `AppCss` became `AppState`, mapping into the run family so
one table feeds both the release row and its apps.

**Two upstream additions, both pushed:**

- `.run-row__acts`, restored. 15d withdrew it because the build history had
  moved to a table and `.run-list` had no caller left; this PR is the caller
  that proves it. The row declares six columns and the sheet names five.
- `.check--ack`. The system has `.check` and it has `.alert--danger`, but
  nothing that is both — and a bare tick reading *"I understand this installs
  into the live Production environment"* is one more line of body text, which is
  the one weight it must not have. Two callers (the release dialog and the
  release-pipeline editor), so it stopped being a scoped one-off.

**The sub-rows are the whole reason for the component**, and they need
`grid-column: 1 / -1` plus a left inset matching the id track, the way
`.run-progress` spans the row. Verified by measurement, not by eye: the first
app sub-row's left edge is 393px and the release title above it is 393px.

**A seeding bug that looked like a code bug**, again. Every deployed app rendered
a grey "pending" clock. The seed had written `status = 'deployed'` where
`ProjectDeliveryResultStatus.Completed` is `'completed'` — so the fall-through
arm was correct and the data was wrong. It rendered identically on the *old*
markup, which is what identified it. Same family as 14c's unseeded compare table.

Verified rendered in both themes: scheduled (with Reschedule / Cancel),
deployed with two app sub-rows, failed with an outside-window flag, a failure
message and a failed app. Suite 2,245 passed / 0 failed; all seven shared
sheets byte-identical with `.design/handoff/`.

### PR 15f — project settings, and the end of the dialect (2026-08-21)

48 stale refs to zero. `tools.css` 2,476 → 2,407, and with it the whole
`.set-*` / `.env-*` / `.ewe-*` / `.pipe-*` / `.audit-[kvt]` / `.type-pill` /
`.set-pill` / `.state` / `.cust-save-hint` / `.row-role-select` family — 56
rules. **PR 15's own nine files are now at zero.**

The mapping was mostly mechanical once the settings archetype's own vocabulary
was read rather than guessed at: `.set-panel` → `.card` + `.card__body`,
`.set-sec-head` → `.form-sec__head` + `.form-sec__cap`, `.set-foot` →
`.form-actions`, `.set-subhead` + `.cap-label` → `.form-sec__head` +
`.section-label`, `.set-empty` → `.empty-state--quiet`, `.set-pill` / `.state` /
`.type-pill` → `.status-pill`, `.audit-[kvt]` → `.meta-row` / `.meta-item`.

**Two lists became real tables/rows.** The environments list was a div grid; it
is a `.data-table--edge` now, with `RowStateIcon` in the state column and the
update-window editor opening as a `tr.is-subrow` **underneath the environment it
belongs to** — so it can no longer drift away from the row that opened it. The
pipelines list became `.sub-rows--plain`.

**The Danger-zone question is settled, in `Account.razor`'s direction.** It was
a whole tab holding one button, and the rendered page made that obvious in a way
the markup did not. It is now a `.setting--danger` + `.setting__lock` row at the
foot of General, which is the archetype's own answer for a single destructive
setting and what Account chose for the same reason. `Section.Danger` is gone;
the page has four tabs.

**One upstream addition:** `.u-nowrap`. The utility set is `.u-row` /
`.u-stack` / `.u-between` / `.u-muted` / `.u-num` / `.u-sr` and had no way to
say *"do not break this"* — which a file name, a version, or a `.app` beside a
word needs rather more often than tabular numerals do. Three callers, all of
them the same `<code>.app</code> files` phrase. `base.css` loses `.nowrap`.

**A near-miss worth recording.** The first attempt at the panel sweep rewrote
`<section class="form-section">` to `<div class="field">` — changing the *tag*
while leaving its `</section>` — which unbalanced the document and made the
depth-walk that wraps each panel throw halfway through, leaving the file in a
half-rewritten state. Reverted and redone under one rule: **a bulk class sweep
never touches an element's tag.** `.field` works on a `<section>` just as well.

Verified rendered in both themes, all four tabs, with the environments table
populated (three environments, two with update windows) and the repository
editor filled. Suite 2,245 passed / 0 failed; all seven shared sheets
byte-identical with `.design/handoff/`.

### Verifying it

`scratch/seed-pipelines.sql` + `seed-pipelines2.sql` (gitignored) fill project 5
"CRONUS Denmark" with the states the pages actually have: two repositories, five
builds (ready-with-deliverables, in-flight, failed-with-a-real-compiler-error,
and two older), a two-repo changelog, build logs, three environments with update
windows, two release pipelines and four deliveries (scheduled, deployed, failed
outside its window, installing). Before it, the tables held three builds with no
artifacts, no commits, no logs and no deliveries — every populated state on all
three pages was unreachable. Same lesson as 14c's compare table and 14d's
identical pair: *the states you did not seed are the states nobody reviewed.*

## PR 16d — what the design review found on the compare screens (2026-08-21)

The `design-review` pass held since PR 15, run against the two compare screens
once 16a-c had reshaped them. Eight screenshots in both themes, plus the four
source files. Worth recording for the hit rate as much as the findings.

### Four of its eight defects were not defects

Every claim that would have changed code got checked before anything moved, and
half of them dissolved:

- **"Clickable bands have no `cursor`, no `:hover`, no `:focus-visible`."** They
  do — in `tools.css`, not the `pages-power.css` the reviewer grepped. It had
  flagged this one as needing a live run, which was the right instinct about its
  own evidence.
- **"The results table paints its status keyline on the wrong edge."**
  `components.css` says outright that rows take it on the *right*. Specified,
  and that sheet is byte-parity-locked to the handoff.
- **"Modified rows get no keyline at all."** All four rows carry a 4px bar;
  `is-modified` computes to `rgb(138, 131, 0)`. Both the reviewer and I lost a
  dark-mustard sliver in a downscaled PNG and read absence into it.
- **"The identical-file screen offers navigation to nothing."** *My* claim, not
  the reviewer's — the buttons are `disabled`. Probed and wrong.

The pattern is the branch's own lesson pointing at the reviewer this time:
**a screenshot is evidence of what a page looks like, not of what it does.**
Three of those four came from reading source or pixels instead of the running
page. A fresh-eyes reviewer with no repo knowledge greps one plausible file and
concludes from silence; it is exactly the failure a newcomer *would* have, which
is what makes the pass valuable and also what makes verifying it non-optional.

### What survived, and shipped here

- **A disclosure control that never showed its state.** Expanding a band left
  its text unchanged, so `... 6 unchanged lines` sat above six visible lines
  asserting they were hidden. Only the `title` flipped, and nobody hovers a
  strip they have already clicked. Register row 68.
- **No at-rest affordance.** Hover and focus only reach a band the reader has
  already committed to. Register row 67, reversed.
- **Inline's dead banners looked identical to side-by-side's live ones.** The
  unified view never emits the unchanged runs, so its bands cannot expand — same
  grey strip, one layout switch apart. The chevron settles this for free: no
  chevron, no promise.
- **A label bound to the wrong control.** The view bar carried "Ctrl Down next
  change" immediately left of the *previous* button. The same pair was already
  spelled out in the foot, in arrow order. Deleted; the buttons' titles cover
  them where they sit.

### Left alone, deliberately

`@@ -12,8 +13,10 @@` is the reviewer's largest finding and the maintainer's
call went the other way — register row 69. The jargon rule is for captions
around the site; a diff pane is a code surface, and this is the reader's own
vocabulary there.

Its judgment calls that nobody has ruled on yet — `Open diff` vs `Open` sharing
one column, `CHANGES 4` not saying what it counts, `+7 -0 ~3` with its legend
750px away, the rail's `M`/`A`/`D` against the results table's icon-plus-word
for the same three facts, and inline expandability — are filed as `redesign`
issues rather than half-answered here.

## PR 16c — collapsing the side-by-side diff (2026-08-21)

The other half of #579, and the one the geometry rework in 16a was for.

### The invariant

Two panes, two real files, kept level by blank filler rows measured against the
full text. Hide lines in one pane and the other has to hide *exactly as many*,
or every line below the gap faces the wrong counterpart — no error, no visible
break at the seam, just a diff that stops meaning anything half way down.

What makes it tractable: a collapsed run is **unchanged on both sides by
construction**, so its rows pair one-to-one and hold no fillers (fillers only
exist where one side has something the other does not). Hiding aligned rows
a..b therefore removes the same height from both panes whatever the line
numbers on either side are. `SideBySideCollapse` does one walk over the aligned
model and emits both panes' regions together, sharing an index.

That index is the second half of the answer: expanding is a **pair operation**,
so the bands cannot carry line ranges — a click has to open the same region in
both panes, and the ranges differ between them.

### Replace, not fold

CodeMirror's folding puts an inline "..." placeholder on the line above and is
built for syntax ranges. The handoff's screen has no placeholder — hunks sit
between `@@` banners and the skipped code is simply not there. So the runs are
hidden with **block replace decorations**, which take their rows out of the
layout, and the band that stands in for them is the replace decoration's own
widget. A region expanded keeps its band above the first line it revealed, so
the seam stays visible and the click reverses.

Two banner shapes, one of which the handoff never had to answer for:

- `@@ -24,8 +32,14 @@ PostDocument` — introduces the hunk below it. It also
  stands in for the run it hides, when there is one; the banner over a diff
  whose first change is at the top hides nothing and just anchors above line 1,
  which is what the handoff shows.
- `... 6 unchanged lines` — past the last hunk there is no hunk to announce.
  The handoff's sample file ends on its last change, so this is ours
  (divergence 66).

### Verifying it

Measured, because the failure is invisible. With the sample diff collapsed both
panes report **841px / 841px** of content and identical scroll ranges; expanding
one band takes both to **958 / 958**; every gutter row in one pane sits at the
same pixel offset as its counterpart in the other, before and after. Scroll sync
stays exact at every position (0px drift), and the overview ruler puts each
pane's marks at the same fractions for the rows they share — which is 16a's
payoff, since all of that reads the layout through `lineTop` and folding is
exactly the case the old filler arithmetic could not survive.

`SideBySideCollapseTests` (10) pins the server side, with the row-count
invariant as its first test. `CollapsedDiffTests` (7) pins the wiring — the
paired serialisation, the toggle driving both editors, replace-not-fold, every
band estimating its own height, banners sorting above fillers, and a band that
hides something being operable by pointer and keyboard. Each mutation-tested;
the first attempt at the pair guard passed against a commented-out call, so it
now strips comment lines before matching. Suite 2,287 passed / 0 failed.


## PR 16b — the inline diff, and hunks where they are cheap (2026-08-21)

#576 (inline layout) and #579 (hunk headers + collapsed context) shipped
together, because the handoff renders both layouts as hunks and because a
unified view is the one place hunks cost nothing.

### Why unified gets hunks for free

Side-by-side needs no new document: each pane holds a real file, and the
alignment is fillers laid over it. Collapsing the unchanged runs there means
*folding* both panes and keeping the fold ranges in step across them — that is
the remaining half of #579 and it is still open.

Unified has no file to hold. The document is synthesised
(`Services/Diff/UnifiedDiffSerializer.cs`), and once you are building the text
anyway, "leave out the unchanged runs" is a filter over the rows you were about
to emit. So the inline layout arrives already collapsed, with the `@@` banners
the handoff shows.

Two things stop being free once the document is synthetic, and both are carried
per row rather than counted:

- **Line numbers.** Row 12 of a unified document is line 12 of neither side. The
  serializer emits `[[old, new], …]` and the viewer renders two gutters instead
  of CodeMirror's one — a row that exists on only one side leaves that cell
  empty. The old column is dimmed so the eye follows the new file by default.
- **The banner's tail.** `@@ -24,8 +32,14 @@ PostDocument` names the declaration
  enclosing the hunk's first *changed* line — not its first line, which is three
  rows of context earlier and often outside the procedure entirely. The new
  side's outline names it, falling back to the old side's: an outline is
  per-file, and one side of a release compare frequently has none. (The sample
  data is exactly that case, which is how the fallback got noticed.)

### What it took on the client

Less than expected, because the inline pane is a read-only pane over a
synthesised document and everything else about it is a normal mount. Two
options on `mountReadOnly` — `unifiedGutters` and `hunks` — rather than a third
mount path, so the pane keeps the line tints, the word diff, the change-bar
gutter and the overview ruler without any of them being re-derived.

The one genuinely awkward part is timing: **the inline pane cannot mount at page
load.** CodeMirror measures its own rows and inside a `hidden` container every
measurement is zero. So the initial sweep skips it and the toggle mounts it on
first reveal. Skipping it there turned out to be load-bearing twice over — the
side-by-side wiring keys off there being exactly *two* compare roots, and a
third one joining the sweep would have stopped the two panes ever being paired.

Change navigation now routes: `goInline` walks the single document's changed
rows, `goSideBySide` does the cross-pane merge it always did. Inline jumps
top-aligned, because centring clamps to zero on any diff shorter than a
viewport — which is what a collapsed diff usually is.

### Verifying it

Rendered in both themes, and exercised as a sequence rather than a screenshot:
load → inline → next/prev → back to side → scroll → reload. The pane mounts on
first reveal, the choice survives a reload, the foot hint swaps ("Scrolling
either side moves the other" is not true of one pane), and the side-by-side
sync is still exact after the round-trip through hidden (120 / 120).

`UnifiedDiffSerializerTests` (13) covers the document — the two-row modified
line, the gutter pairing, word ranges in unified coordinates, the collapse, the
banner counts and its declaration lookup including the old-side fallback.
`InlineDiffTests` (6) pins the seams that fail quietly: the attribute contract
between page and script, the mount-on-reveal, the remembered choice, and the
nav routing. Suite 2,270 passed / 0 failed.


## PR 16a — the compare panes' geometry (2026-08-21)

Groundwork for #576 (inline diff) and #579 (hunk headers), and a bug fix that
fell out of doing it. Both issues change *what a pane renders* rather than how
it looks, and #579 renders it by folding the unchanged regions away — which the
old positioning model could not survive.

### The model that had to go

Four call sites across two files each answered "how far down the pane does
source line N sit?" with the same arithmetic over the server's filler list:
`visual = (line - 1) + sum(size of gaps anchored at or above N)`.

- `alignedRow` / `lineAtAlignedRow` (code-editor.js) — the scroll sync.
- `computeChangeBlocks` (source-viewer.js) — next/previous change.
- `buildDiffOverview` (source-viewer.js) — the overview ruler.

It is correct only while every row is present. Fold an unchanged region and the
rows above a line stop predicting where it renders, so #579 would have needed a
fifth copy that knew about folds — and the other four would have gone quietly
wrong beside it.

CodeMirror already tracks the lines, the filler block widgets *and* the folds.
So the answer now comes from the view: `lineTop(id, line)`, `lineAtTop(id, top)`,
`paneMetrics(id)` and `afterLayout(id, fn)`, exported from code-editor.js. The
two panes mount with the same configuration and so share a row height, which is
what makes a pixel offset in one pane comparable with a pixel offset in the
other — matching lines sit at equal offsets by construction, since that is the
job the fillers were computed to do.

### Three bugs the refactor surfaced

Measuring the rendered panes to prove the refactor was behaviour-preserving is
what found these. None was introduced by it; all three were shipped.

**Every alignment gap was rendered a third short.** `FillerWidget` sized itself
from `view.defaultLineHeight`, read synchronously at mount — before the height
oracle has measured anything, when it still reports CodeMirror's own 14px
default against our 19.6px rows. Measured: the two panes had content heights of
**909 and 949 px** for the same 48-row diff, so they slid ~5.6px apart per gap
and were more than a row out of step by the fourth. Now read through
`withMeasuredLineHeight` (requestMeasure for the value, rAF to get the dispatch
back *out* of the measure cycle, which refuses state updates) — both panes
measure **949 / 949**, and a next-change jump puts both scrollers on the same
number instead of 39-vs-59.

**The scroll sync dropped its sub-line offset.** `syncComparePanes` corrected
the follower to `blockTop + frac` from inside a `requestMeasure` read — but
CodeMirror applies the `scrollIntoView` being corrected during that same cycle,
so the correction was overwritten and the follower snapped to a row boundary.
Driving the left pane through seven positions: before, the follower was out by
up to **18px**; after (correction moved to the next frame), **0px at every
position**, in both directions, wheel-driven and programmatic alike.

**The ruler was built against unsettled geometry** — visible only after the fix
above made the fillers land a frame later. `afterLayout` exists for this: it is
the hook anything reading pane geometry at mount time has to wait on. Every mark
on both panes now sits within a constant 3px of the line it names (the 4px
`.cm-content` padding, which the probe includes and CodeMirror's `contentHeight`
does not).

### Verifying it

Behaviour-preserving was checked as a *diff*, not by eye: the same probe run
against the pre-refactor build and the new one
(`scratch/bc-design/geom-probe.mjs`, `sync2.mjs`, `align-probe.mjs`,
`ruler-check.mjs`, all gitignored). Ruler percentages and change-nav targets
came out identical except where the filler fix moved them, and it moved them
towards agreement between the panes.

`CompareGeometryTests` (6 tests) pins the retired arithmetic out, the four
exports in, the measured read at both mounts, the widget rendering at the height
it estimated, and the sync correction staying outside the measure cycle. Each
was mutation-tested — five mutations, each failing exactly one test. Suite 2,251
passed / 0 failed.

## PR order

**1. Token layer** — *landed on this branch.* `wwwroot/tokens.css` added as a
verbatim copy of the handoff, the `:root` blocks cut out of `base.css`
(lines 10-189), and the stylesheet linked ahead of `base.css` in `App.razor`.
No markup changed. Verified: `dotnet build` green, app runs, Home and the
workspace generator render in both themes.

**2. Fonts + dead-token housekeeping** — *landed on this branch.* Settled the
font stack (above); replaced all 13 dead `var()` references with real tokens
(68 sites — the dead names repeated far more than the unique count suggested);
and stripped 66 *unreachable* fallbacks, i.e. `var(--defined-token, #hex)` where
the hardcoded second argument could never be reached. `tools.css` hardcoded hex
fell 88 → 26 and `auth.css` reached zero.

That pass also caught a contrast regression the token swap had introduced — see
below. Verified: build green, `dotnet test` 1789 passed / 0 failed, contrast
measured in the running app in both themes.

### The legacy blue alias had to be re-pointed

`--blue` is consumed 40 times as `color:` and 4 times as a focus-ring
`outline:`. The handoff aliased it to `--primary`, but the old ramp's `--blue`
was `#2563eb` at 5.2:1 on white, and `--primary` `#00B7C3` is **2.5:1** — so the
token swap alone had pushed 44 foreground sites below AA. Measured in the app:

| Candidate | on `--bg` | on `--primary-weak` |
| --- | --- | --- |
| `--primary` `#00B7C3` | 2.5:1 | fails |
| `--primary-strong` `#008089` | 4.4:1 | 4.26:1 |
| **`--primary-ink` `#00646B`** | **6.44:1** | **6.25:1** |

`--primary-strong` is calibrated against white `--surface`, but the app paints
page content on `--bg` and selected rows on `--primary-weak`, where it lands
just under 4.5:1. `--primary-ink` is the token the system designates for teal
text on light surfaces and clears AA on every surface we actually use, so the
whole blue ramp collapses onto it for the duration of the migration. Dark theme
is unaffected — `--primary-ink` and `--primary-strong` are both `#5AD8E2` there.

The lesson generalises: **check contrast against the surface a token is actually
painted on, not the one it was calibrated against.** Expect the same question
each time a tool moves onto the component layer.

**3. Core components** — *the layer is in; two families migrated.*
`wwwroot/pages.css` (the archetype layer) now ships too, loaded after
`components.css`.
`wwwroot/components.css` now loads between `tokens.css` and `base.css`, so a
not-yet-migrated page can still override a component and the new rules stay
inert where old ones exist. Each family "activates" when its old rules are
deleted.

Migrated: **buttons** and **status pills**. Their base definitions are gone from
`base.css` (80 lines), `tools.css` (35) and `admin.css` (15).

Still to migrate, each activating when its old rules are deleted:
`.data-table` (tools 14, admin 2) · `.field` / `.input` (tools 19, needs the
`.field__input` → `.input` markup rename) · `.ra*` (tools ~20) · `.module-card`
(base 3, tools 6) · `.form-grid` (base 28) · `.card` / `.stat-card` / `.toast` /
`.page-head` / `.pill-tab` / `.section-label` (tools, small). Then the renames:
`.confirm-modal__*` → `.confirm-dialog__*`, `.page-header` → `.page-head`.

Three things the port found, all of the "every data-driven omission is a flag"
kind — worth expecting again on the next component:

- **`btn--outline` (10 sites) was a dead class**, defined in no stylesheet. The
  component layer's base `.btn` *is* the outline style, so removing it was a
  no-op. Conversely `btn--ghost` (2 sites) was undefined in the app but *is* in
  the handoff, so porting fixed two buttons that had been rendering as plain.
- **`.status-pill--warn` was hardcoded red**, not amber, and marks "Unhealthy"
  and "failed" workers — it meant *danger*. Taking the handoff's amber `--warn`
  verbatim would have silently downgraded a failure signal. Those moved to
  `--danger` / `--failed`; the one genuinely-warning site (a non-active user)
  kept `--warn`.
- **The handoff's `.btn--loading` is worse than ours** and was not taken. It
  blanks the label and draws a bare spinner; the app swaps in a "Generating..."
  busy label, which tells the user what is happening. Our two-span pattern was
  translated onto tokens instead, and `components.css` carries a comment saying
  why. Same for `.btn--lg` and `.btn--disabled`, which the handoff has no
  equivalent for.

### Decisions 4 and 5, as taken

**5 — `.is-*` for runtime state: adopted.** It is a stated rule of the handoff,
and reversing it later is a mechanical rename.

**4 — the status-pill row rule: adopted.** This one was not an open question:
the maintainer directed the design agent to replace the pill column with the
edge colour *plus a state icon*, so it is a decided end-result to implement, not
a trade-off to weigh. A run/delivery-history table now carries:

- `.data-table--edge` on the table, and `is-<state>` on each `<tr>`, which
  paints the 4px keyline on the row's right edge from the `--bar-*` ramp;
- a narrow `.data-table__col-state` column holding `<RowStateIcon>`, which
  renders `.data-table__state--icon` — the glyph that disambiguates the colour
  for anyone who cannot use it, with the state word in `aria-label` + `title`;
- no `.status-pill` anywhere in the row.

`Components/Shared/RowStateIcon.razor` owns the state → row-class + glyph
mapping so the keyline and the icon can never disagree, and exposes
`RowStateIcon.RowClass(state)` for the `<tr>`. That is the translation step: the
handoff specifies CSS classes, and three classes that only make sense together
belong behind one component in our codebase.

**Where the rule applies: everywhere.** I first scoped this to run/delivery
histories, from a qualifier in the `components.css` comment. Rendering the
component sheet settled it — the spec is unambiguous and broader:

> *"Pills are for cards, detail headers and inline text... **Table and list rows
> never use pills.**"* — and of `.data-table--edge`, *"it is the default for
> every table in the toolbox."*

So every `.data-table` takes the edge treatment, and the div-based run and
delivery lists do too. Converted so far: both tables on `/site-admin/workers`,
both on `/templates`, and the project directory. Still on pills, tracked in
[#524](https://github.com/mtaanquist/ALDevToolbox/issues/524): user lists, token
and OAuth-client tables.

**A row with nothing to report gets no bar and no glyph.** The project directory
made this concrete: a project with no build yet has no *status* — colouring its
edge queued-grey and giving it a clock would invent one. The cell stays empty
and the `<tr>` takes no `is-*` class, so the keyline renders transparent.

**The glyph column leads the row.** *"a 4px coloured right edge plus a leading
glyph in a `.data-table__col-state` cell"* — it is the first column, not
wherever the old status column happened to sit.

**Row states come in families**, and class, label and glyph always agree within
one: object diffs (`is-new` / `is-modified` / `is-unchanged`), runs (`is-queued`
/ `is-running` / `is-succeeded` / `is-failed` / `is-cancelled`), lifecycle
(`is-published` / `is-draft` / `is-archived`), XLIFF (`is-untranslated` /
`is-fuzzy` / `is-translated` / `is-final`). `RowStateIcon` implements all four;
five Lucide glyphs were vendored for them at the pinned 1.14.0 tag
(`circle-plus`, `minus`, `circle`, `check-check`, `circle-alert`).

**6a. Templates + Cookbook onto the list archetype** — *landed on this branch.*
Templates took the table view, Cookbook the card view, which is the handoff's
own split: an edge keyline needs a shared edge to line up against, so *"card view
keeps the `.status-pill`."* Both now render four states. `TableSkeletonRows` and
`RowStateIcon` came out of the Templates port; `RecipeTypeBadge` survived
this PR unchanged pending [#536](https://github.com/mtaanquist/ALDevToolbox/issues/536);
6d squared it.

Two carry-overs on Cookbook's card, both invisible when the handoff's own data
is used and both wrong without it:

- **The card is the link, not the title.** A browse grid is scanned and clicked;
  the title-only hit target is a worse affordance at the same pixels.
- **`.browse-card` re-expressed as flex with `margin-top: auto` on the foot.**
  With the handoff's `align-content: start`, the "N files / View recipe" line
  floats at a different height in every card as soon as descriptions and keyword
  rows differ in length. Identical rendering when they don't.

`tools.css` 5431 → 5358. `.cb-head` / `.cb-toolbar` / `.cb-search` survive only
because Projects and the admin Object Explorer index still use them.

**6b. Projects onto the list archetype, table view** — *landed on this branch.*
Replaces `BuildStatusPill` in the "Latest build" cell with the row's edge keyline
plus a leading glyph, which closes this page's slice of
[#524](https://github.com/mtaanquist/ALDevToolbox/issues/524) and
[#527](https://github.com/mtaanquist/ALDevToolbox/issues/527). The freed cell now
carries the BC version as a `.code` chip, falling back to the status word when a
queued or failed build never got one.

**Its search deliberately stays a plain GET form**, so this page needs no
`@rendermode` at all and a filtered result stays a shareable URL. Worth
remembering as the counter-example to the interactivity gotcha below: the
archetype's filter bar is a *look*, not a commitment to client-side filtering.
Verified by driving it in the browser — Enter in the box lands on
`/projects?q=Denmark` with one row.

`.proj-page` and `.art-latest` deleted; `tools.css` 5358 → 5356.

**6c. Pipelines + Releases onto the list archetype** — *landed on this branch.*
Both were bespoke `.alr` row grids; the handoff is explicit that a list like that
should be *"a real `.data-table` ... not a bespoke row layout, so it sorts,
filters and scans like every other table in the toolbox."* ~90 lines of
`.art-*` / `.alr` / `.al-*` went with them; `tools.css` 5356 → 5287.

Pipelines' passive counts strip ("3 pipelines · 1 building now · 2 need
attention") became `.pill-tabs` that actually filter — the same numbers, now an
affordance. Releases has no build status, so its edge keyline carries the one
thing that can be wrong with a release target instead: an environment that has
disappeared from Business Central takes `is-failed`. That signal used to be a
7-word note in a row's meta line.

Three things the port turned up, none of them cosmetic:

- **Pipelines' search had never worked.** The page reads `?q=` through
  `IHttpContextAccessor`, but it is `@rendermode InteractiveServer`, so
  `OnInitializedAsync` runs twice — once prerendering, where `HttpContext`
  exists, and again on the circuit, where it is null. The second pass reset the
  term and every row came back. Confirmed by fetching the prerendered HTML (1
  row) against the settled page (4). Now `[SupplyParameterFromQuery]`, which is
  populated in both passes, on `OnParametersSetAsync` so submitting the form
  reloads. **Any page combining a query-string filter with a render mode needs
  this** — Projects is safe only because it is static SSR.
- **Every kebab in the app was 35px out of place.** See the class-collision
  gotcha above and [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537).
- **`ReleasePipelineRow` had no `ProjectName`**, so the old page linked the
  literal word "Project". Added to the DTO and the projection; the
  `list_release_pipelines` MCP tool returns the same record, so agents get it
  too (description updated to match, per CLAUDE.md's MCP-parity rule).

`RelativeTime.Ago` came out of `Components/Shared/` here rather than becoming a
third identical copy. The *shorter* phrasing on `PipelineEditorDialog` and
`ReleasePipelineDetail` is deliberately left alone — merging it would change
visible copy on two unmigrated pages as a side effect of a refactor.

**7a. New Workspace onto the generator archetype** — *landed on this branch.*
Ships `wwwroot/pages-forms.css` (the form-centric archetype layer, covering PRs
7, 8, 9 and 11) and puts the first page on it. The form is one column beside a
sticky aside holding the tree, the two counts and Generate — so the button left
its sticky bottom bar and now sits next to a preview of what it will produce.

**The folder tree was rebuilt flat.** The handoff's `.tree` indents rows with a
`--d` custom property instead of nesting lists, so the recursion moved out of the
markup into a `Flatten()` and `FolderTreeNode` is gone — one component instead of
two. Collapsing is ours and stays: these trees run to dozens of rows. It costs no
chrome, because the whole folder row is the toggle and the glyph opens, which
keeps the row shape the sheet specifies.

`IdRangeEditor` became one `.id-range` field rather than two labelled ones, and
gained the running **"N IDs"** count the handoff puts there. That is the reason
to prefer it: a range is a size, and the old pair made you do the subtraction.

Two more collisions of the [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537)
kind, both found by looking at the page rather than the markup:

- `tools.css` still owns `.field__label` and renders it as an **uppercase
  micro-label**, which sat next to the real `.section-label` and read as a second
  tier of heading.
- `base.css` still defines `.form-grid` as a **single flex column**, so the
  component layer's two-column grid never applied — the leaked
  `grid-template-columns` does nothing to a flex box, and every pair stacked.

Both are reset in the page's scoped CSS until those families migrate. That is
now the recognisable shape of this failure: *the app's rule wins, and it is the
properties it does **not** name that decide what you get.*

**7b. New Extension onto the generator archetype** — *landed on this branch.*
The sibling page, same shape: the aside now carries the import card, the tree,
the counts, Generate and the drop-in instructions. With both pages moved, the
whole `.workspace-page` shell went — layout grid, scoped form-input styling,
module cards, import card, sticky action bar, `.success-tip`. `tools.css`
5207 → 4894, and it has now roughly halved since the branch started (9,000+ at
the token swap is not the right baseline; 5472 at the first archetype PR is).

Two bugs a screenshot caught that markup review would not:

- **`<InputFile>` renders its `<input>` inside its own component**, so the page's
  scoped CSS never reached it and the browser's native file widget sat visible
  beside our styled button. `::deep` fixes it — the same trap as `IdRangeEditor`'s
  labels.
- **Razor read `1 extension@if (...)` as an email address** and emitted the
  literal `@if` block into the page. A `@`-sign directly after a word is the
  email heuristic; the note is now one computed expression.

**8a. Audit history + diff onto the admin-edit archetype** — *landed on this
branch.* `AuditHistoryPanel` is now the sheet's `.audit` list: one row per
change, expandable **in place** to the diff. It was a table whose "View change"
column linked out to `/admin/audit/{id}/diff`, so the diff was always one page
away from the thing it described. The permalink survives, in the expanded body.

Pairing the snapshots costs nothing — the entries load newest-first, so the
state *after* entry `i` is entry `i-1`'s "before". No second query.

**The diff stays field-level.** The handoff's `.diff` is line-level with two
line-number gutters; ours is field-level, because the inputs are JSON snapshots
of a row and naming the field beats pointing at a line. The sheet's own example
writes a changed line as `"idRangeFrom": 50000 -> 50100` — exactly the shape a
field diff produces — so the rows read the same; they just have no gutters, and
the `+`/`-`/`~` marker still comes free from `.diff__ln--*::before`.

**A pre-existing lie, fixed rather than carried over.** Each audit row stores
the state *before* its change, so the newest row has nothing to diff against —
and `Compute(before, null)` renders every field as *removed*. The old page did
this too, but behind a click-through where few people went; expanding in place
puts it top of the list. That case now shows the recorded state and says nothing
newer exists.

**6d. Recipe detail + the Cookbook's loose ends** — *landed on this branch.*
Four things the maintainer caught on the staging instance, all one slice: the
Cookbook migration had stopped at the browser.

- **The generator preview floated 57px low** (divergence 7). Measured, not
  guessed — `getBoundingClientRect` on `.gen__form` vs `.gen__aside` in the
  running app. Affected both generator screens; drift is now 0 and the aside
  still sticks when scrolled.
- **Long chips escaped the browse cards.** `.code` is `white-space: nowrap`,
  right for what the handoff puts in one — an id, a version number, a short
  keyword. Our first chip is the minimum-version label and BC spells those out
  in full ("Min: Business Central 2024 release wave 2"), so it was wider than
  the card. The tag row's chips now wrap; truncating would have eaten the
  release name, which is the part being read.
- **`RecipeDetail` was never migrated** — still rounded `.rcd-tag` pills, a 26px
  `h1`, and the whole `.codeblock` / `.rd-*` family. Now archetype 3: the
  handoff has no recipe screen, so it is *composed* from its detail pieces
  rather than copied — `.detail-head`, `.meta-row` for the facts that used to
  sit in a rail card, `.code-block` per file, `.code` for keywords. The rail
  keeps only what a rail is for (jumping between files) and appears once there
  is more than one to jump between.
- **The admin table's keyword cell blew the layout open.** Unbounded, its
  max-content width drove the table past the viewport, which squeezed Title to
  its min-content — one word per line — and pushed Last updated off the edge.
  Capping the chip row caps the cell's contribution, so the keywords wrap
  instead of the title.

Two things followed from the work rather than from the report. **#536 is
closed**: `.rtype` was the last rounded object here at `border-radius: 7px`, and
the redesign's whole shape argument is that the round lozenge was the one curve
it removed. It keeps its own tinted colour — a *type* is not a status, so it
does not borrow `.status-pill`'s 3px state keyline — but takes the square
`--r-badge` and, more importantly, `.status-pill`'s 20px height: on the new
detail head a type badge and a Deprecated pill sit side by side, and any height
difference between them reads as a mistake. Both measure 20px at the same top.
And the file bar had been rendering **"1 lines"**, invisible until the block
moved somewhere it could be read.

Our line-numbered, `.tok-*`-highlighted code body is kept over the handoff's
`<b>`/`<i>` scheme: theirs is a prototype stand-in for real AL highlighting,
and the gutter is why `.code-block pre`'s own padding and scroll had to move to
the wrapper. `tools.css` is 6.4KB lighter; the whole `.codeblock` / `.cb-*` /
`.rd-*` / `.rcd-tag` / `.rail-*` family is gone, with `.tok-*` (shared with
`Account.razor`) and `.rtype` kept.

**14a. Object Explorer landing onto the list archetype** — *landed on this
branch.* The one slice of PR 14 that needs no scoping conversation: the release
picker is a browser, not a power tool. Its category tabs map onto `.pill-tabs`
exactly as the Cookbook's type filter does, counts included, and the release
cards onto `.browse-card`.

The page had already been restyled once, against an *older* design study
(`.design/explorer-cookbook/styles/screens2.css`) — rounded 8–11px cards, a
segmented tab bar, a bespoke hero. That is precisely what this migration exists
to retire, so the previous pass is not a reason to leave it alone. 222 lines of
`rel-*` / `rh-*` / `rc-*` / `rg-*` / `src-tabs` went; `tools.css` is at **4,559**.

Two things have no component in the handoff and are composed from its pieces
rather than given a private look: the **latest-release hero** (a `.card` whose
head carries `.section-label` + title + a status pill, over a `.meta-row` of
facts and the two actions) and the **version timeline** (`.section-label`, a
hairline rule, a count). The old timeline's leading dot went — it was the only
instance of that decoration in an otherwise square system.

Judgment calls worth naming:

- **The Ready pill was `--live`, with a pulsing dot.** A release that finished
  importing is not *live*; the pulse implied something was still happening. Now
  `--success` with a check.
- **Tab icons kept**, against Cookbook's icon-less `.pill-tabs`. Microsoft /
  Third-party / C/AL are three similar words and the glyph is the only thing
  that separates them at a glance. `.pill-tab` has no icon slot but nothing in
  it objects to one.
- **"Import a package" added to the page head** (Editor/Admin only). It was
  previously reachable only from the empty state — the one situation where you
  have nothing to import *into*. The archetype has an actions slot; this is what
  it is for. Still the outline style: `Explore objects` is the page's one
  primary.
- **`.oe-release-repos` moved to `OeReleaseDetail.razor.css`.** It had been
  sitting in the *landing* block despite belonging to a different page, and
  would have been deleted with it. Caught by diffing the removed block's class
  names against what the markup still renders — worth repeating on every
  deletion this size.

**4. Shell** — *landed on this branch, out of order.* Planned fourth and taken
after 8a, because every later PR sits inside it: leaving it unmigrated meant
each one inheriting the same scroll-container mismatch that produced divergence
7 on the generators.

`shell.css` is a byte-identical copy of the handoff. The grid is 2×2 with named
areas (`"brand top" / "nav content"`), so the brand became a sibling of the nav
rather than a child, and the old `.main-col` wrapper is gone — the top bar is
its own cell. Renames: `.app-shell`→`.app`, `.sidebar`→`.app__nav`,
`.top-bar`→`.app__top`, `.content`→`.app__content`, `.nav-link`→`.nav-item`,
`.nav-section`→`.nav-group`, `.nav-sublist`→`.nav-sub`, `.sidebar-footer`→
`.app__nav-foot`, `.user-button`→`.user-btn`, `.storage-bar`→`.quota`,
`.theme-toggle__btn--active`→`.is-active` (also in `theme.js`, which toggles
it). `base.css` fell 1,095 → 772 lines.

The `<ul>/<li>` scaffolding went with it: `.nav-group` *is* the grid, so the
lists sat between it and `.nav-item` and both the row gap and the active keyline
landed on the wrong box.

**Two bugs the port produced, neither visible in the markup.**

- **`margin-inline: auto` is not the no-op it was.** The old `.content` was a
  block, where auto inline margins do nothing to an auto-width child; the
  handoff's `.app__content-inner` is a **grid**, where they make the item shrink
  to fit-content and centre. Every page collapsed to its content width, and any
  `.page` that landed under 1080px silently tripped the container query in
  `pages-forms.css` — so the generator's preview pane stacked under the form
  again, 1,493px down. The transition rule now pins `width: 100%` first.
- **`.nav-link-btn` beat `.brand__toggle`.** Putting both on the drawer's close
  button gave it `display: grid` from the later rule, overriding the
  `display: none` that hides it above the drawer breakpoint — a ✕ on every
  desktop page. Same shape as the `.ra__menu` collision in [#537](https://github.com/mtaanquist/ALDevToolbox/issues/537),
  found the same way: by looking at a screenshot, not the CSS.

**The drawer needed an opener.** `shell.css` hides the nav below 700px and shows
it again on `.app.is-drawer-open`, but ships only `.brand__toggle` — which lives
inside `.app__brand`, itself hidden until the drawer is already open. That is
the close control; there was nothing to open with. Rather than ship a nav that
cannot be reached, `wwwroot/shell-drawer.js` (delegated, idempotent, so it
survives enhanced navigation without making the layout interactive) toggles the
class, with a hamburger in `.app__top-lead` at the same breakpoint.

**Divergence 8: expandable nav groups render permanently `.is-open`, without the
caret.** The handoff's `.nav-parent` collapses; ours never has, and collapsing
would hide navigation as a side effect of a restyle — a behaviour change the
work did not call for. A caret that never toggles is worse than none, so it is
omitted rather than rendered inert. The indent and left rule are unchanged, so a
group looks the same open. Revisit as its own change if the sidebar gets long
enough to want it.

`ReconnectModal` keeps Blazor's `components-reconnect-*` class names — its
reconnect JS drives them — but is restyled onto the `.reconnect__card` look:
squared, warning keyline on top, danger once the rejoin has failed. Three
hardcoded hex values went with it. `StorageBar` lost its own two.

Verified by sweeping all 34 routes under the new shell (no horizontal overflow,
no console errors, shell present on each), plus the drawer opening, closing on
Escape, and closing on nav-click. Renames: `.app-shell` → `.app`, `.sidebar` → `.app__nav`,
`.top-bar` → `.app__top`, `.content` → `.app__content`, `.nav-link` →
`.nav-item`, `.nav-section__label` → `.nav-group__label`, `.user-button` →
`.user-btn`, `.sidebar-brand__*` → `.brand__*`. Pull `Shell.dc.html` to diff
against.

**8b. The four global audit pages** — *landed on this branch.* The rest of PR
8's audit half: `/admin/audit`, `/site-admin/audit` and both `/…/{id}/diff`
permalinks, onto the same `.audit` list 8a gave `AuditHistoryPanel`. The two are
the same list with a different filter, so they should not look like different
things.

**A row here does not expand, unlike the panel's.** The panel's entries are all
one entity, so the state after entry *i* is entry *i-1*'s snapshot and pairing is
free. This list is cross-entity and paginated: neighbouring rows are unrelated,
so an expansion would cost a query each. Rows link to the diff page, which
already pairs properly.

That retires the **"Snapshot / Show JSON"** column, which printed the raw
before-state as a `<details>` blob. The page it now links to shows before *and*
after with the changed fields marked, so nothing is lost — and the row is a
sentence ("kirsten.jensen@example.com deleted Catalogue entry #42") rather than
seven columns to reassemble. On the SiteAdmin page the organisation joins that
sentence instead of holding its own column.

Both diff pages move onto archetype 3's pieces: `.detail-head` for the title
row, `.meta-row` for the facts that were a `<dl>`, `.status-pill` for the action
badge that was a bespoke rounded `.audit-action`.

Two additive classes, composed because the handoff has neither: **`.filter-grid`**
(its `.filter-bar` is a search plus tabs; these pages filter on five or six
fields, which is a form) and **`.pager`** (its list archetype renders the whole
result set; four of ours page). The shared `AuditVerb` / `AuditActionPill` /
`FriendlyAuditType` helpers moved to `AdminPageHelpers` on their third call site.

`admin.css` drops 91 lines — the whole `.audit-table` / `.audit-snapshot` /
`.audit-action` / `.audit-pagination` / `.audit-pager` / `.audit-diff__meta` /
`.audit-page` family, every user of which was one of these four pages.

Two bugs the screenshots caught that the class-count probe did not:

- **`#42in Default`** on the SiteAdmin list. Razor strips the whitespace between
  `</code>` and an `@if`, so the id ran into the next word. The space has to live
  *inside* the `<text>` block. The verify script now greps rendered rows for a
  character butting into a capital.
- **A black box around every page title.** `<FocusOnNavigate Selector="h1">` in
  `Routes.razor` focuses the heading after each navigation so a screen reader
  announces the new page — right, and kept. But it stamps `tabindex="-1"`, and on
  a full page load Chrome then matches `:focus-visible` and paints its own
  `outline: auto` in near-black. Measured first: in-app *mouse* navigation leaves
  focus on `<body>` and is clean; a fresh load and keyboard navigation both ring.
  The ring is now suppressed on `h1[tabindex="-1"]` only — the "never remove
  focus rings" rule protects keyboard-operable things, and a heading outside the
  tab order is not one. Interactive rings are untouched, checked by tabbing.
  **This one is app-wide, not audit-specific.**

**Design-review follow-ups: the toast, and the three Cookbook issues** —
*landed on this branch.* Not an archetype PR — the fallout from running the
`design-review` subagent over the migrated recipe detail page, plus the audit it
prompted. Three commits.

**The toast (`ToastHost`).** The recipe page and the Translator had written the
same centred dark pill twice, each with its own copy of a cancel-and-restart
dismiss timer. Both are now the design system's `.toast` in a fixed
`.toast-stack`, behind one shared component. Two bugs fell out: both toasts were
rendering **full-viewport-width** (bug class 2, third instance — see above), and
the Translator flashed a green tick for all fifteen of its messages including
"Couldn't record that vote", so failures and no-ops now take `--danger` and
`--warn`. That also closed the `.toast` entry in #537 — `components.css` was
leaking a border and a width onto the legacy pill, which is moot now the pill
is gone.

**[#539](https://github.com/mtaanquist/ALDevToolbox/issues/539) — the download
modal.** The customer name was required and the button stayed disabled, so
anyone downloading for a demo typed `test`. The interesting part was the
*premise*: single-file recipes are almost always taken with per-file **Copy**,
which never opened the modal, so the attribution was already systematically
blind to the recipes people used most — the required field was buying less than
it cost. Copies are now recorded too (once per visit, no customer), the name is
optional with the reason given as copy rather than as a control, `customer_name`
is nullable and renders as "Not recorded", and a `source` column keeps the admin
panel from becoming a wall of anonymous rows. Follow-up
[#541](https://github.com/mtaanquist/ALDevToolbox/issues/541): most Pipelines
projects *are* a customer, so that box should be suggesting them.

**[#540](https://github.com/mtaanquist/ALDevToolbox/issues/540) — keyword
chips.** Mono said "identifier", chip shape said "filter", clicking did nothing.
They link to `/cookbook?q=<keyword>` now (which works because `SearchAsync`
already matched on keywords — only the affordance was missing) and the browser
page reads `?q=` the same way `PipelinesBrowser` does. On the *card* they stay
labels: the whole card is already an anchor. Added a **`.tag`** component to the
design system — `.code`'s box in the UI face, free to wrap, squared like
everything else — and pushed it upstream.

**[#537](https://github.com/mtaanquist/ALDevToolbox/issues/537) — the audit is
now a test.** Two of its original six were false positives (it compared property
*names*, so a legacy `padding` shorthand looked like it did not override a
component `padding-right`). `.btn--lg`'s legacy rule is deleted — its only two
call sites are the Generate buttons, both migrated. Three reviewed exceptions
remain in the test's allow-list, and the test fails if that list grows *or* goes
stale.

**8c. The whole admin edit-form family** — *landed on this branch, in four
commits.* 25 files, 305 stale refs to zero. `tools.css` 4,548 → 3,758,
`base.css` 880 → 794, `admin.css` 569 → 421.

- **8c-1** the Administration family (9 files) and `PillTabs` off the legacy
  pill, which pulled in the SiteAdmin, Object Explorer and Import section
  headers. Also the **form-scaffolding bridge**: see bug class 1 above.
- **8c-2** the two row-table catalogues onto `.data-table`, not `.sub-rows`.
- **8c-3** the module editor, the recursive folder/file tree, and the four
  template pages, retiring `.preview-card`, `.folder-editor`, `.extension-editor`,
  `.org-file-*`, `.mustache-*` and the CodeMirror mount frame.
- **8c-4** the list, bulk and dashboard pages, plus the tools home (it shared
  `.tile` with the admin dashboard, so neither could retire alone), and a
  rule-aware deletion pass over the legacy sheets.

Bugs found on the way that were not styling: a control that rendered **two of
itself** (the logo picker's native `<input type=file>` had never had a rule, so
the browser's own "Choose File / No file chosen" sat inside our button), a link
that 403'd for the audience it was written for (org admins sent to a SiteAdmin
route for personal access tokens), and a checkbox that promised something that
could not happen (ticked *and* disabled, and a disabled input posts nothing).
Follow-up [#543](https://github.com/mtaanquist/ALDevToolbox/issues/543): the two
overlapping forms on Add a user.

**15a. The confirm dialog off the legacy overlay** — *landed on this branch.*
The first of the six slices in "PR 15" above, and not Pipelines work at all: it
is the shared component the gap's four dialogs sit on. `.confirm-modal*` (54
lines of `base.css`) deleted; `ConfirmDialog.razor`, the four Pipelines dialogs
and `RecipeDetail`'s download modal all on `.modal-layer` / `.modal-backdrop` /
`.confirm-dialog`. Another whole family off the ported-but-unused list (#549).

The panel gains a head glyph and, for a destructive action, the danger tint —
both **derived from `ConfirmButtonClass`** rather than a new parameter, because
a caller that already asked for a red confirm button has said the action is
destructive and eleven call sites should not have to repeat it.

Three things this turned up that the markup does not show:

- **A row-action menu drew over the modal.** `.ra__pop` sat at `z-index: 80`
  against the layer's 50, so "Compare with..." in a results-row kebab opened the
  release picker *underneath* the menu it was picked from — lit, un-dimmed and
  still clickable over the scrim. Fixed by bringing `.ra__pop` down to 30, the
  design system's own value for `.ra__menu`, rather than inventing a bigger
  number for the layer: 50 is the right value *in the design scale* (menus 30,
  drawer 40/41, modal 50, reconnect 60), and the legacy sheet was the thing out
  of scale. `ModalLayerTests` now pins both ends of that ordering.
- **The dialog never took focus, and so Escape never worked.** Pre-existing, and
  the port did not cause it: the markup relied on the `autofocus` attribute,
  which browsers honour when an element arrives with the document and ignore
  when Blazor inserts it later. Focus stayed on the trigger, Tab walked the page
  behind the scrim, the keydown handler on the layer never fired, and a screen
  reader was never told a dialog had opened — while the two focus sentinels
  flanking the buttons sat there working perfectly on a cycle nothing ever
  entered. One `OnAfterRenderAsync` fixes it. Found by driving the page, not by
  reading it; three separate rounds of review had passed over this component.
- **`ConfirmDialogTests` reported "the dialog never opened" for an unregistered
  icon.** The ported head always renders a glyph, so `IconCatalog` became a
  required service for a test class that had never needed one — and a render
  that throws in bUnit surfaces as empty markup, which reads exactly like a
  logic bug in `OpenAsync`.

The backdrop needed a wrapper the design system does not have (divergence 56):
its dialogs are demonstrated inside a review frame, so nothing owns "centre this
over a page". `.modal-layer` plus a `--wide` variant and body-paragraph margins
went upstream with it.

**15b. The `.ra` row-action menus (#529)** — *landed on this branch.* The second
prerequisite slice. `.ra__menu` had named two different things — the
absolutely-positioned popup in `components.css`, the `<details>` wrapper in
`tools.css` — and `tools.css` loads second, so the app was running on a
comment-documented reset of `position`, `display`, `top`, `right` and `z-index`
holding every kebab in the app in place. One name means one thing again.

152 lines of `tools.css` gone (`tools.css` 2,900 → 2,751), including the whole
`.ra__sub` / `.ra__item--parent` / `.ra__item--leaf` sub-menu family, which had
been dead since the inline release submenu was replaced by the shared picker
dialog for hanging the page on a large catalogue. Three call sites — the shared
`RowActionsMenu` split button and the solo kebabs on the two browsers — all now
render exactly what `ComponentsPanel.dc.html` shows, including its own answer for
the solo variant: `btn btn--icon btn--sm`, no bespoke class at all.

**This is a behaviour change, not only a class swap,** and worth being explicit
about: the menu was a native `<details>` and is now `.ra.is-open`, toggled by
`row-actions-menu.js`. It no longer opens with JavaScript off. That fallback was
always half a fallback — the script already owned close-on-outside-click,
Escape, and one-open-at-a-time, so with JS off the menu opened and could only be
closed by clicking the trigger again — and one of the three call sites
(`OeObjectResults`, static SSR) was the only place it meant anything. Keeping
`<details>` would have meant a third element between `.ra` and `.ra__menu` and
a divergence for the disclosure state, to preserve a menu that could open but
not close.

The trigger is found by `data-ra-toggle`, an attribute rather than a class, so
a restyle cannot silently unbind it — the #562 trap from the other side.

Four `ComponentCollisionTests` allow-list entries all reading "retires with
#529" came out with it, and the class doc's live example is now history rather
than a live bug.

**15b-review. Five reports from a pass over the Object Explorer** — *landed on
this branch.* All five reproduced; two of them turned out to be one cause, and
one was a design-layer bug affecting every table in the app.

- **A short file's editor status bar floated mid-pane.** `.oe__centre` is the
  archetype's three-row column (`auto minmax(0,1fr) auto` — tab strip, code,
  footer) and the source viewer renders only the middle one. A single child
  lands in the FIRST row, which is `auto`, so the editor sized to its content: a
  23-line file drew its pane, and the status bar CodeMirror hangs at its foot,
  200px above the bottom with empty background beneath. Long files filled the
  row and hid it. Scoped to the one-child case rather than editing the
  archetype: `.oe__centre:has(> .source-viewer__code:only-child)`.
- **The results row's split button became the handoff's kebab.** Two reports,
  one fix. `ComponentsPanel.dc.html` reserves the split button for a toolbar
  (`Publish` + caret) and puts `btn btn--icon btn--sm` in a
  `.data-table__actions` cell — and `OeObjectResults`' own file comment already
  said "the kebab lives in `.data-table__actions`", so it had drifted. It also
  answers the crowding: a 32px `.btn` in a 40px row left 4px above and below.
  Nothing was lost by folding "View source" into the menu, because the row's
  Name cell is already a link to the same file.
- **Four `.data-table` column modifiers had never applied.** `.data-table th,
  .data-table td` sets `text-align: left` at (0,1,1); `.data-table__actions`,
  `__num`, `__col-state` and `__col-check` are (0,1,0) and lose. Invisible while
  the cell's content fills it — which is why swapping a 200px split button for a
  26px kebab is what finally showed it, with the kebab pinned to the left of the
  cell and 162px of space beside it. **It had been found once before** and
  patched in `base.css` for `__num` alone (`.page .data-table .data-table__num`),
  which left the other three broken and recorded the cause where the sheet that
  owned it could not see it. Fixed upstream by scoping all four under
  `.data-table` — (0,2,0) beats (0,1,1) on the class count — and the legacy
  bridge deleted. `DataTableModifierTests` pins it as a *specificity* contract,
  and fails if a selector is re-flattened.
- **The release filter row could not fit on one line at any width.** Five
  controls plus a 348px scope-tab strip overflowed 1100px, so the namespace
  input wrapped, the search sat capped at its max-width with a gap beside it,
  and `Clear filters` landed alone on a second row at `btn--sm`'s 26px against
  everything else's 32px. Two changes: an **Options** dropdown (the
  `kind-filter` pattern, now shared as `.fdrop`) holding the namespace box and
  the include-base toggle, rendered **always** and disabled when the current tab
  has nothing to put in it; and the **scope tabs lifted onto their own row**,
  which is where the archetype puts them anyway — `PageList.dc.html` fills
  `.filter-bar` with filters and puts its pill tabs *after* the spacer as a view
  switch, never a scope picker at the head. One row at 1280–1920px, and the
  search now grows to its 380px cap instead of being squeezed.
  The button carries a count (`Options · 1`) when something behind it is set:
  a filter you cannot see is a filter you cannot explain.
- **`.pill-tab__count` read as an exponent.** "Microsoft ⁶". The tab centres the
  label's and the count's *boxes*, which is not the same as aligning their
  baselines — 11px mono beside 13px sans, and the mono face's tall ascent lifts
  the digits. Line-height does not move it; only an optical correction does.
  One pixel, pushed upstream (divergence 60).

**15f-a. `ProjectDetail`'s left rail becomes header tabs** — *landed on this
branch.* Approved by the maintainer as the faithful call. Archetype 7 has no
vertical nav in its component inventory at all, and every other settings family
in the app already moved: `Account.razor` records the same reasoning ("the rail
had nothing to port onto"), as do `/site-admin/settings/*` and
`/admin/administration/*`. This was the last one holding out.

`.set-grid`, `.set-nav`, `.set-nav-item`, `.set-nav-divider`, `.sn-label`,
`.sn-badge`, `.sn-dot` and the rail's `.cap-label` variant are gone from
`tools.css` — 18 rules — along with the dead `@media (max-width: 880px)` that
collapsed the two-column grid, and the `.acc-*` block's comment claiming Account
still shares a `.set-grid` rail with this page.

**The tabs switch page state rather than navigating**, which is where this
diverges from Account: that page's tabs are real `?section=` links, and being
bookmarkable is worth more there than it is here. This page has **one Save
covering both General and Repositories**, so a real navigation between those two
tabs would silently discard whatever the user had typed. `HeaderTabs` gained an
optional `OnSelected`; the row is byte-identical either way, only the element
differs. Verified by driving it: an edit typed into Name survives a hop to
Repositories and back, with the "Unsaved changes" hint still up.

Two things the rail carried that a plain tab row does not: a repository count
and a dot for the Business Central connection. The count is already in the page
head's sub-line. The dot is a **deliberate loss** rather than a badge bolted
onto a row the design system renders as plain text — worth revisiting only if
someone misses it.

Still open on this page (the rest of 15f): the panel internals — `.set-panel`,
`.set-sec-head`, `.set-subhead`, `.set-foot`, `.set-pill`, `.set-empty` — and
the `.env-*` / `.audit-*` / `.pipe-*` families. And a consistency question worth
settling once: Account **dropped** its Danger tab and put the destructive
setting at the foot of Profile on `.setting--danger` + `.setting__lock`; this
page keeps a Danger zone tab. One of the two should change.

**5+. One tool per PR**, each pulling its archetype CSS from the design project
and deleting its slice of `tools.css`. Suggested order — cheapest proof first,
then best leverage, then the hard ones:

| # | Surface | Archetype | Why here |
| --- | --- | --- | --- |
| 5 | Piper, Compare | 11 (diff) | Small scoped CSS; proves the loop end to end |
| 6 | Templates, Cookbook, Projects, Releases, Pipelines browsers | 2 (list) | One archetype, five pages — the biggest `tools.css` deletion |
| 7 | New Workspace / New Extension | 5 (generator) | The signature screen; live preview + sticky pane |
| 8 | Admin CRUD + audit history | 6 | **Done** (8a–8c). `AuditHistoryPanel`, `AuditDiffViewer`, the `.diff` block, then every admin edit form |
| 9 | Administration / site-admin settings | 7 | Sub-nav header tabs; many small forms |
| 10 | Admin + site-admin dashboards | 4 | Where `.cue` tiles would land. **The admin dashboard and the tools home moved in 8c-4** onto `.tool-grid`; what is left here is the SiteAdmin one |
| 11 | Auth pages | auth card | Self-contained, no shell; `auth.css` is nearly clean already |
| 12 | Docs, MCP setup, 404/500 | 12-14 | Light surfaces, mostly prose |
| 13 | Translator | 9 (grid, compact) | Power tool; `.tgrid` is a full rewrite of the grid |
| 14 | Object Explorer | 10 (source viewer, compact) | Largest surface; three panes, tabs, refs, resizers. **Landing page done (14a)** — it is a list, not a power tool. What is left is the source viewer, which is the part that needs scoping. |

Every user-facing PR still owes the **UX definition of done** checklist from
CLAUDE.md and a `design-review` pass — a restyle is exactly when jargon and bad
empty states survive unnoticed, because the page "looks finished".
