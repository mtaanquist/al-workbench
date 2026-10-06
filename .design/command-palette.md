# The command palette

Status: **the shell (#880), the search backbone (#881), the first sources (#882-#884), the
way in, the accessibility pass and the docs (#888), the performance work (#889), page
context and recents (#887) and the rest of the sources (#885: pipelines, release
pipelines, templates, teams, people and the docs pages) and commands (#886) are built.
BCQuality articles and translation files were looked at and left out, and so were typed
search modes - see "Deliberately left out".** This document is the outcome of #879.
Every decision below was made with the maintainer on 2026-09-21.

## Why

The app has grown a navigation tree that serves someone who is looking around and gets in
the way of someone who already knows where they are going. The palette is for the second
person: press one key on any page, type part of a name, press Enter.

### Named users

- **A consultant jumping to a customer's environment.** Knows the customer and the
  environment ("Contoso, production"). Today: Solutions, find the row, open it, the
  Business Central tab, the environment. With the palette: `con prod`, Enter.
- **A support person looking a customer up mid-call.** Knows the name, or the short name
  the team uses for them. Wants the Solution's Customer tab now.
- **A developer hopping between tools** - from the Translator to a release in Object
  Explorer, or to a cookbook recipe they half remember the title of.

None of them wants to learn anything. The palette has one input and a list; nothing in it
needs a caption.

## How it is global

`MainLayout` is a static server-rendered frame, and pages opt into a circuit one by one.
Several have none at all - `/solutions` and the docs pages are plain GET pages by design.
So "works on any page" rules out the obvious Blazor answer: an interactive island in the
layout would make every static page hold a circuit just so the palette can open, would
cost a round trip to open, and would be dead while a circuit reconnects.

Instead the palette is three parts:

1. **Razor renders the markup.** A static, non-interactive component in `MainLayout`
   renders the palette's skeleton, hidden: the dialog, the input, the list, the empty and
   error states, and one `<template>` per kind of result row. Icons come from
   `Icon.razor` like everywhere else, and the "Go to" list of tools is rendered
   server-side from the same role and feature gates the nav menu uses, so the palette
   never offers a page the nav would not. The Commands group is rendered the same way.
2. **One script in the shell.** A single dependency-free file beside `shell-drawer.js`.
   It owns the hotkey, open and close, focus, keyboard navigation, the fetch, and cloning
   templates into the list. It writes no HTML and chooses no class names - it fills
   `textContent` and `href` on clones of what Razor rendered. That keeps the design
   system in one place and takes markup injection off the table.
3. **Two endpoints, both data.** `GET /palette/search?q=`, a cookie-authenticated minimal
   API returning data, not HTML. A GET with no side effects, so no antiforgery token; it
   requires an authenticated user and returns 401 otherwise. Its sibling
   `GET /palette/context` answers what the palette shows before anything is typed - see
   "Where you are" - under the same rules.

Palette styles live in a global sheet, never a scoped `.razor.css`: Blazor's scope
attribute is not on script-cloned nodes, so scoped rules would silently not apply. The
ported sheets stay byte-locked; if the palette needs a component the design system does
not have, it goes through the design project like any other.

### The exception this makes

`CLAUDE.md` says "tiny `.razor.js` companion files only". The palette script is not tiny
and is not a companion. It is a deliberate, recorded exception, for the reason above: a
static layout cannot host an interactive component without taxing every page. It is
**not** a precedent for moving page behaviour into script. The limits that keep it an
exception: one file, no dependencies, no bundler, no build step, no HTML strings.

### Typed JavaScript, not TypeScript

The script is a plain `.js` file with `// @ts-check` at the top and JSDoc `@typedef`s for
the endpoint's response shape. That gets editor-time type checking without a compile
step, without Node in the Dockerfile, and without a second source of truth for what runs
in the browser. CI may run `tsc --noEmit --checkJs` over it as a check; nothing is ever
emitted.

## The hotkey

**Cmd+K on macOS, Ctrl+K everywhere else.** Nothing in the app binds it today:

| Where | Bound today |
| --- | --- |
| Translator | Ctrl/Cmd+S, Alt+letter, arrows on the splitter |
| Piper | Alt+1, Alt+2 |
| Every page with a search box | F3 (see below) |
| Object Explorer release page | Esc, Alt+letter |
| Source viewer | Ctrl/Cmd+F, Ctrl/Cmd+A, Ctrl/Cmd+Up/Down, Shift+F12, Ctrl/Cmd+click |
| Code editor | Ctrl/Cmd+click, CodeMirror's default keymap |

**F3 is the page's own search, Ctrl/Cmd+K is anywhere** (#902). F3 with no modifier puts
the cursor in the page's search box: `wwwroot/search-shortcut.js`, one `document` listener
in the `shell-drawer.js` idiom, focuses the first *visible* element marked
`data-page-search`, or the first input inside a `data-page-search-host` - which
`FilterBar` puts on its search wrapper, so every `ListPage` box has it for free. It stands
down inside a CodeMirror editor (where F3 is find-next), while the palette or one of our
dialogs is open, and on a page with no box, where the browser keeps its find-next. It
never falls through to the palette. The one per-page F3 handler this replaced, the
release page's, is gone; `PageSearchShortcutTests` keeps the marker on every search box,
so a renamed selector cannot kill the key silently again. Marked boxes carry
`aria-keyshortcuts="F3"` and say "(F3)" in their title; the user-facing line is on
`/docs/search`.

- It works while focus is in an input, a CodeMirror editor or the source viewer. The
  listener is on `document` in the capture phase, so an editor's own keymap never sees
  the keystroke.
- CodeMirror's default keymap binds Ctrl+K on macOS to delete-to-end-of-line. Because
  macOS gets Cmd only, that binding survives. The maintainer's position, should the two
  ever collide: the palette wins.
- Firefox on Windows and Linux uses Ctrl+K for its search bar. The page takes it, as
  GitHub and Slack do.
- It does **not** fire while one of our own dialogs is open. A `ConfirmDialog` naming a
  production environment is not something to be navigated away from by reflex.
- Pressing it while the palette is open closes it. Esc closes it. Focus returns to
  whatever had it.

## The other way in

Nobody finds a hotkey by accident, and on a phone or a tablet there is no hotkey to
find - so the palette also has a visible control in the top bar, and that control is
the only door at phone width.

It is drawn as the search field it stands in for, sitting left in `.app__top`: a
magnifier, "Search or jump to...", and the shortcut as key caps. Clicking it runs the
same `open()` the hotkey does, so the two cannot drift. It narrows with the shell -
the key caps go at rail width, where a tablet has no keyboard to hint at, and below the
drawer breakpoint it is the magnifier alone. The label is *clipped* there rather than
removed, so the icon-only button still announces as "Search or jump to..., Ctrl K".

`shell.css` is byte-locked and has no slot for it, so its styles live in
`MainLayout.razor.css`; scoped is safe here because this is the one part of the palette
Razor renders in place rather than the script cloning. The pattern goes upstream -
`.design/handoff/briefs/2026-09-command-palette.md` is the brief.

Razor renders "Ctrl", because the server cannot know what the person is typing on. The
script rewrites it to "Cmd" on a Mac, from the same `isMac()` that decides which
keystroke opens the palette - one decision, not two that could disagree.

## Sources

Each source of results is one class implementing one contract, registered in DI. The
palette knows none of them by name; adding a source is one class and one registration.
This clears the "no interface until the second implementation" bar on day one - the
first version shipped five, #885 added four more, and #984 added Upgrades.

A source provides:

- **An id and a label.** The label is its group heading: Solutions, Environments,
  Upgrades, Pipelines, Release pipelines, Releases, Recipes, Templates, Teams, Go to.
- **A gate.** The role check and feature gate its matching page already has. A caller who
  fails it never has that source asked.
- **A search.** Query terms and a limit in; candidate rows out - kind, title, subtitle,
  link, and the short name when the row has one. Those three text fields *are* what was
  matched against. No icon name: the icon is in the `<template>` the kind selects, so it
  never rides the JSON.
- **Searched-only text**, when a row needs it. A fourth field that is matched against and
  never shown, for the things support types mid-call that a result must not print back: a
  customer's Voice account number, their tenant id. A row found that way says in its
  subtitle *which* field matched, never what was in it, and the field itself is absent
  from the wire contract, so it cannot reach the browser even by accident. It is not a
  place for personal data - a contact's name or company may be matched on and named, a
  phone number or an email address is neither searched nor shown.

Sources return candidates, **not scores**. One shared function ranks everything, so
ranking cannot drift from source to source.

| Source | Searches | Lands on |
| --- | --- | --- |
| Solutions | name, short name; and the customer fields support types mid-call - a contact's name or company, the Voice account number, the tenant id | the Solution (its default tab, Customer) |
| Environments | environment name; the Solution's name as subtitle and its short name as searched-only text | the environment page |
| Upgrades | a planned upgrade's name, and its target release as subtitle ("to 28.5") followed by its status in the Upgrades page's words (Planned, In progress, Updated, Done); open ones and the archive, ranked together by the usual rule (so a done upgrade whose name starts with the query can sit above an open one; the status in the subtitle tells them apart). Gated like the sidebar's Upgrades entry, on the environment-updates grant | the upgrade's page, `/upgrades/{id}` |
| Pipelines | pipeline name; the Solution's name and the latest build ("Built 2 days ago", "No builds yet") as subtitle, its short name as searched-only text | the pipeline's builds |
| Release pipelines | release pipeline name; the Solution's name and the target environment's as subtitle - with the environment's trouble when it is gone or failed, in the Releases page's words - and the Solution's short name as searched-only text | the release pipeline |
| Releases | release label, BC version, country, and the name of the Solution whose build produced it | the release in Object Explorer |
| Recipes | recipe title and tags | the recipe |
| Templates | template name; its key and runtime as subtitle, and "Deprecated" when it is | the template's page |
| Teams | team name; its member count as subtitle, led by "Your team" for one the caller is on | the team's roster |
| People | a user's name - org Admins and SiteAdmins only; the subtitle is the role, never the email | the person's row on Administration's Users tab (`?user={id}`, which marks the row and focuses it); for a SiteAdmin who is not an org Admin, the site user list filtered to the name |
| Docs | a docs page's title and a one-line topic, and each section heading | the page, or the section's anchor |
| Go to | tool and page names | the page |

Four decisions in the #885 rows are worth their reasons:

- **"Release pipelines", not the page's heading "Releases".** That word already heads
  the Object Explorer group, and two groups under one heading say nothing about which is
  which. The Releases page calls each of its rows a release pipeline, so the word is
  still the page's.
- **Every team, not only the caller's.** `/teams` lists the teams you are on, but
  `/teams/{id}` opens any team's roster for anyone signed in - membership is not a secret
  inside an organisation - so any team is somewhere the caller can go. The gate is the
  sidebar's: signed in, nothing more. (#885 had guessed admins and team managers; the pages
  decide.)
- **Deprecated templates are offered.** The Templates list shows them, marked, and their
  page opens; deleted ones are left out as the list leaves them out. The gate is the
  browser's, not the admin pages'.
- **No template modules, and no builds as rows of their own.** A module has no page to
  land on. A build found by its number or branch needs a second row shape and a second
  landing page; the pipeline's subtitle carries the latest build meanwhile.

All four are small tables - a few rows per Solution, or per organisation - so each
projects the rows the caller may see and lets the ranking decide, as Solutions and
Environments do. None pre-filters with `ILIKE`.

**An Upgrade's status is words, not a pill (#984).** The planned-upgrades picker sheet
lists Upgrade as a result kind "with name, target and state pill". A palette row has a
title, a second line and its kind's icon, and nothing else rides the wire, so the status
is the last words of the second line ("to 28.5 - In progress"). A pill would be a new
field on the result contract and new drawing in the script for one source; not worth it
until a second kind wants one. The header belongs to the organisation, so the fence here
is on the status: it is derived only from the lines the caller can see, and no line's
solution is ever named in the row.

**Go to is the one that is not a DI source.** It has no database behind it and its
whole content is decided by the caller's roles and tool toggles, so Razor renders it
into the page and the script filters it in the browser, under the same every-term-must-match
rule. That is also what makes it survive a failed request: when the endpoint is
unreachable the palette says so and still jumps. The list itself is
`Domain/Navigation/NavDestinations.cs`, shared with the sidebar - `NavMenu.razor` keeps
its own markup (its groups collapse, nest and carry active states) and a test fails if
the two ever disagree about which pages exist. For the same reason Go to is **not capped**:
it is the browsable list of tools, which the empty query already shows whole.

Go to also breaks ties by **sidebar order, not by name**, which is the one place it
departs from the ranking below. Alphabetically, `trans` puts "Translation memory" above
"Translator" - so the three letters a consultant types for the tool in their sidebar land
them on the admin page that curates it. The sidebar's order is editorial (tools first,
then the pages that administer them) and it gets that pair, and the four like it
(Templates, Cookbook, Object Explorer, Audit log), right by construction. It is the
*shipped* order, from `NavDestinations`: a person who has rearranged their own sidebar
(#956) still gets the same tie-break, because the palette searches rather than browses.

Releases and Recipes search **names, titles and a recipe's tags only**, never objects,
source or recipe bodies - those tables are large and Object Explorer already has a search
built for them. A later version may add a "Search for '...' in Object Explorer" row that
carries the query over; the palette itself never runs that search.

A recipe's summary is left out on purpose, even though the Cookbook's own search reads it:
a palette row is a title and one line, so a row that matched on a sentence the reader
cannot see reads as a bug rather than as a result. Tags are in for the opposite reason -
the row can show the tag it matched on, and does: a recipe's subtitle is its minimum
application version and one tag, and that tag is the one a typed term matched when a term
matched one, otherwise the recipe's first. The Cookbook page's own search is unchanged and
still finds more than the palette does; the palette is for going somewhere, and the
Cookbook is where a wider search belongs.

Docs has no table behind it. Its pages and their section headings are a **hand-kept
catalogue** on the source, with a one-line topic per page so `mcp` finds the page whose
title is "Connect an AI assistant". A test renders every docs page and fails when a
catalogued heading is missing from it or one of its section headings is missing from the
catalogue, so the list cannot drift from the pages. People is the one source for admins
only; there is no page per person, so a row lands on the person's own row of the Users
table, marked by the system's selection keyline.

## The fence

The palette is a new read path across most of the app, so it is a new way to leak.

- **If you cannot open it, it is not returned.** Every source goes through the same
  visibility rules the pages use - the `ProjectAccess` predicates, the role gates, the
  organisation query filter. There is no `IgnoreQueryFilters()` anywhere in the palette.
- **A Private Solution the caller is not on does not appear at all**, and neither do its
  environments. The Solutions list shows such a Solution as a locked name, because a
  list is for seeing what exists; the palette is for going somewhere, and a row that
  cannot be opened is a dead end taking one of five places. The cost is accepted: someone
  searching for a customer they cannot see gets "no results", and the Solutions list is
  where they find out it exists and who to ask.
- Soft-deleted environments and deleted Solutions are not returned.
- **Tests prove it per source**: a user outside a Private Solution searches its exact
  name and gets nothing, for every source that touches Solutions.

## What it never does

**No palette command writes to a customer's tenant.** Every such write in the app sits
behind a confirm that names the environment and says when it is production; a palette
command is exactly how the wrong environment gets updated. A command like "Copy
environment" - if one is ever added - navigates to the page that owns the action and its
confirm. The commands #886 added keep this rule by construction - see "Commands" below.

## Commands

A few things are worth a keystroke without being a place: starting a new Solution, switching
the theme, copying the page's address, signing out. They are one more group, **Commands**,
found by ordinary text like everything else - `new sol`, `dark`, `sign out`.

**Commands are rendered and filtered exactly like Go to.** The list is a static catalogue,
`Domain/Navigation/PaletteCommands.cs`, beside `NavDestinations` and gated by the same
`NavGate` and tool toggles (one evaluator, `NavDestinations.Passes`, so a command and a page
cannot disagree about who may see them). `CommandPalette.razor` renders the ones the caller
passes into an inert `<template>`, each row carrying its own icon; the script filters them
under the every-term-must-match rule, ranks them like Go to (best tier, then catalogue order),
and draws them after the context block and Recent - local groups, so search results never
land above them - and before the search results and Go to. No endpoint, no server call.

**Before anything is typed, only three show**: New solution, Copy link to this page, and
Refresh where the page has one (`OnOpen` in the catalogue). The palette is opened to jump
somewhere, and all thirteen an Admin gets would push every Go to page below the fold; the
rest are one word away. Sign out in particular is never on the opening list, and sits last
in the group, so it is never one reflexive Enter from opening the box.

A navigate command's second line names where it lands ("Solutions", "Templates", "Your
account"), and an act command's says it happens here ("This page", "Appearance"), so the two
kinds read differently without a caption explaining them. The foot's hint says "Enter to
choose" rather than "to open", which was wrong for a command that acts in place.
Commands are not deduplicated against Go to: "New workspace" beside "Workspace" is two ways
to say one thing, and both are worth finding.

There are two kinds:

| Kind | What it is | The commands |
| --- | --- | --- |
| Navigate | An ordinary link to the page that owns the form | New solution, New workspace, New extension, Suggest a recipe (each behind its tool); Repository access (`/account/repos`); Import a Business Central release (Admins and Editors - named in full because "release" alone reads as the delivery area); Business Central app registration (Admins, where the organisation owns its settings) |
| Act | `data-command-action`, run by the script from a closed list | `theme:light`, `theme:dark`, `theme:system`, `copy-link`, `sign-out`, `refresh` |

**The act list is the whole list.** The script keeps its own copy and refuses any other
action id, even when the markup asks for one. Each is small and stays inside the app:

- **Theme** calls `theme.js`'s own `set` (published as `window.aldt.theme`), so there is one
  place that knows how a theme is stored and applied. The row for the current theme shows
  "Current" as its second line, read from the same file when the list is drawn.
- **Copy link** writes the page's address to the clipboard and shows "Copied" on its row
  (and says it to a screen reader) for a moment before the palette closes - the one act
  command that does not close at once, because a copy with no sign that it worked reads as
  a copy that failed. A refused clipboard says "Couldn't copy - use the address bar" on the
  row and leaves the palette open, so the sentence can be read.
- **Sign out** submits the top bar's own `.signout-form`, which carries the antiforgery
  token. The palette never builds a POST of its own.
- **Refresh** presses the page's own Refresh button, which a page marks with
  `data-page-refresh` - today the Environments list and an environment's page. The command
  is offered only while such a button is on the page and not mid-refresh, which is how it
  inherits the page's own condition for drawing it (an environment's Apps tab offers it to
  the people who manage the Solution, and to nobody else). A Refresh reads Business Central
  again; it writes nothing there.

Every act command closes the palette and returns focus before it acts, so what it does lands
on the page. Two tests hold the fence: every navigate command's link resolves to a routable
page, and every act command is on the sanctioned list - written out in the test rather than
read from the catalogue, so widening the list is a visible diff to the test as well as the
code - and the script's copy of the list matches it.

Two rows the issue listed are left out because Go to already has them, same label and same
link: **Account** and **Customer modules**. A command that repeats a page row word for word
adds a line and no way in.

### No typed modes

#886 proposed leading characters that narrow the search - `>` commands, `@` Solutions, `#`
releases, `/` pages - taught by the placeholder ("Search, or type > for commands") and
listed in the foot. They were not built, for two reasons:

- **A placeholder that teaches a prefix is a caption explaining a mechanic**, which
  `CLAUDE.md` rules out: if the UI needs one, the UI is wrong. The palette's promise is one
  input and a list, and nothing in it that needs learning. Commands are found by what they
  say, like everything else.
- **The palette does not need narrowing to be fast.** "Budget" has the numbers: the whole
  fan-out is about an eighth of its budget at a few hundred Solutions, and inside budget at
  four times that. A mode would buy speed nobody is short of, at the price of a syntax.
  The list stays short the same way it always has - grouping, the per-group caps, and
  every term having to match.

## Matching and ranking

The query is split on whitespace into terms. **Every term must match** somewhere in the
row's searched fields, in any order - so `con cof` finds "Contoso Coffee", `cof con` does
too, and `con prod` finds the Production environment of Contoso because an environment's
subtitle is its Solution. Matching ignores case and accents (`moller` finds "Møller").

Accent folding happens in memory, in `PaletteRanking`, because Postgres can only do it
in SQL through the `unaccent` extension and installing one is a migration, not something
a search box decides. A source over a small table of human-typed names (Solutions,
Environments) therefore projects its rows and lets the ranking decide, which folds
correctly. A source over a large table (Releases, Recipes) pre-filters with `ILIKE` per
term first, and that pre-filter is accent-sensitive: `møller` finds a release named for
Møller, `moller` does not. Accepted, and recorded here so the next person does not read
it as a bug.

Results are **grouped by source in a fixed order** - Solutions, Environments, Upgrades, Pipelines,
Release pipelines, Releases, Recipes, Templates, Teams, Go to - so a Solution and its environments do not shuffle against each other as
the user types. Within a group, best tier first, ties broken by name:

1. exact match on a Solution's short name - this row alone is lifted above all groups
2. the query is a prefix of the title or short name
3. every term matches at the start of a word
4. every term matches somewhere (substring)

**Caps:** five rows per group; eight when only one group has any.

**No typo tolerance in the first version.** Fuzzy matching on customer names produces
hits nobody can explain, and it cannot be an indexed query. Initials matching was
considered and dropped as unnecessary once short names are searched.

A row that matched on its short name shows the short name, so the user can see why it is
there.

### An empty query

Opening the palette without typing shows, in order: **the page you are on** (its context
block, headed by the record's name), **Recent**, **Commands** (#886), then the **Go to**
list (#887). Typing
filters the first three in the browser under the same every-term-must-match rule as Go to,
so they answer every keystroke at once; they stay above the search results, which arrive
underneath them without pushing anything that is already on screen. A link is drawn once:
a recent that is also in the context block, or a search result that is also a recent,
appears in the first place it would. Go to is not deduplicated against anything - it is
the whole tool list, and stays whole.

### Where you are

A Solution's page and an environment's page tell the palette what they are about. **The
page tells the shell; the palette never parses a URL.** Each renders one hidden element,
`Components/Shared/PaletteContext.razor`:

```html
<span hidden data-palette-context="solution:12" data-palette-href="/solutions/12"></span>
```

It is rendered by the page itself, from its route parameter, before the page's own frame.
The script looks it up each time the palette opens and never holds on to it, which is what
makes it right after an enhanced navigation (the page's DOM is swapped, the lookup comes
after) and on a static page alike. `data-palette-context` goes to the server verbatim and
only the server reads it (`PaletteContextRef`: a known kind, a colon, a positive id, and
nothing else). `data-palette-href` is the record's own page, which the script remembers as
a visit and leaves out of Recent while you are standing on it.

What the block offers is the page's own tabs under the page's own conditions - the
palette may not be a side door to a tab the page would not draw:

| On | Offers |
| --- | --- |
| A Solution | Customer, General, Repositories (anyone who can open it); Business Central (whoever manages it, not on-premises); Pipelines (whoever manages it); Access (its owner and the admins); its live environments (the Environments source's own filter, at most eight); its latest build, landing on that build's pipeline (behind the Pipelines tool) |
| An environment | Overview, Apps, Operations, Sessions, Workbench history; Open in Business Central and Open the admin centre, only when the tenant is known (the page's own condition for its two buttons); its Solution |

The tabs land on `?tab=` for a Solution and on the tab's own address for an environment.
The Solution page switches its tabs in place without changing the address, so a `?tab=`
arriving at a page that is already open moves it to that tab, and a navigation to another
Solution reloads it - enhanced navigation keeps the component and hands it new
parameters, so the page loads from `OnParametersSetAsync` rather than
`OnInitializedAsync`, as the environment page already did.

The two links to Microsoft are the only rows that leave the app. They are their own kind,
`external`, whose template opens them in a new tab like the page's own buttons do; the
script accepts an `https://` link for that kind and for no other.

A release page does not have a context block yet. Its natural rows ("its modules",
"Compare with...") are controls inside the page rather than addresses, and the page does
not prerender, so it is left for when there is a way to land on them.

### Where you have been

Recents are the last **eight** places this browser tab went: rows picked from the palette
that are places in their own right (a Solution, an environment, a release, a recipe, a Go
to page - not a tab of the page you were on), and the Solutions and environments visited
however you got there. They live in **`sessionStorage`** - per browser session, as the
Upgrades view keeps its state; no table, nothing server-side, nothing shared. Only links
are stored, never titles, so no customer name is written to the browser's storage.

**A recent is re-checked before it is shown, and one the caller can no longer open is
dropped, not shown locked.** Opening the palette sends the context and the recents in one
call - `GET /palette/context?at=solution:12&recent=/solutions/3&recent=/environments/9` -
and the server answers with the context block and the recents still openable, in the
order asked, titled by the server (so a renamed Solution shows its new name). Each recent
is recognised by exactly one strict route pattern - `/solutions/{id}`,
`/environments/{id}`, `/object-explorer/release/{id}`, `/cookbook/{id}`, or a Go to page's
exact address - and checked through the rule its page and its palette source use:
`ProjectAccess.VisibleProjectPredicate`, the environment page's own read,
`VisibleReleasePredicate`, the recipe filters, and for Go to pages the same gates the list
is rendered from. Anything that matches no pattern is dropped and never echoed back.
Recents the server declined are dropped from storage too.

The endpoint carries the search's fences: authenticated, a 401 rather than a redirect (the
`/palette` prefix), `no-store`, and the search's rate-limit bucket, since it fires on every
open. No `IgnoreQueryFilters()`.

The palette waits up to 200 ms for that answer before its first draw, so a local answer
lands before anything is on screen and the list does not draw Go to and then shove it
down; a slower one draws Go to first and slots the rest in above it, moving the selection
to the top only if the person has not moved it yet. **If the check fails, there is no
context and no recents** - nothing is shown that the server has not just vouched for - and
Go to still works. If storage is unavailable there are simply no recents.

**Clear recents** sits in the foot, and only while there are recents on screen.

## Budget

- **Server:** results in under 100 ms for an organisation with a few hundred Solutions.
  Sources run one after another, not in parallel - they share the request's `DbContext`,
  which allows one operation at a time. Each source is one `AsNoTracking()` query with a
  limit, projecting only the fields a row needs. Upgrades is the one exception, and not a
  small one: an open upgrade's status is derived from its lines and the fleet, so whenever
  an open upgrade is among the rows it returns, it calls
  `EnvironmentUpgradeService.ListOpenAsync`, which summarises *every* open upgrade - the
  headers, their lines through the visibility join, their action rows, and the whole
  visible fleet - on that debounced keystroke (`UpgradePaletteSource`). That is acceptable
  at today's sizes (a handful of open upgrades, a fleet of a few hundred environments) and
  is the first thing to look at if the palette ever shows up in traces. A done upgrade's
  status is fixed, so a search that lands only on the archive, or on nothing, stays at one
  query.
- **Browser:** 120 ms debounce after the last keystroke; the in-flight request is
  aborted when a new one starts; a late response for an old query is dropped. A
  one-character query is not sent - Go to filters locally.
- The endpoint is cheap by construction; if it ever shows up in traces, fix the slow
  source rather than adding a cache. It still carries a rate-limit policy beside the
  three in `OperationsRegistration`, because it is the one route a person can hit tens
  of times in a few seconds without meaning to: a token bucket sized well above what a
  120 ms debounce can produce, so typing never trips it and a script is still bounded.
  The partition is the caller's address rather than their user id - `UseRateLimiter`
  runs ahead of `UseAuthentication`, so there is no claim to read at that point.

### What it actually costs (#889)

Measured against a seeded organisation on the Postgres 18 the tests run against,
through `PaletteSearchService` itself rather than through the sources one at a time:
**500 Solutions** (varied names, short names and hosting; one in seven Private, half of
those reachable through a team the caller is on), **1,500 environments**,
**200 releases** (100 of them produced by Solution builds, so the link the Releases
source joins through is real, and 100 Microsoft artifact imports carrying a country in
their dedup key), **300 recipes** with tags, and **1,000 customer contacts**.

Every figure is p50 / p95 in milliseconds, 60 runs after 40 warm-up runs, on a warm
connection. The range in each cell spans seven queries - `cro`, `cronus prod`,
`annette`, `26.0 dk`, `posting`, a two-letter query and one that matches nothing - and
three callers: an org Admin (who bypasses visibility entirely), a member on a team, and
a member on none.

| Per request | p50 | p95 |
| --- | --- | --- |
| The access snapshot, before the first source | 1.0-1.1 | 1.1-1.4 |
| Solutions | 3.8-4.3 | 4.5-5.3 |
| Environments | 3.2-3.6 | 3.8-4.4 |
| Releases | 1.7-2.9 | 1.8-3.4 |
| Recipes | 0.9-1.2 | 1.1-1.8 |
| **The whole fan-out** | **11.4-12.9** | **12.4-14.1** |

The whole fan-out sits at about an eighth of its budget, so **every source keeps the
matching strategy it shipped with**, and no index was added:

- **Solutions - the projection stays, and there is no pre-filter.** 1.2 ms of the 4 ms
  is Postgres: one scan of 500 rows hash-joined to their contacts, all from cache. The
  rest is materialising the ~930 rows that come back and folding them in memory, which
  is what makes `moller` find Møller. An `ILIKE` pre-filter would save about a
  millisecond and cost that.
- **Environments - the same, for the same reason.** 1.5 ms in Postgres over 1,500 rows
  joined to the Solutions the caller may see.
- **Releases - the per-term `ILIKE` stays.** 0.7 ms in Postgres, most of the rest being
  one round trip and the plan. The newest-first `Take` keeps the read bounded whatever
  the term matches.
- **Recipes - the per-term `ILIKE` stays.** 0.3 ms in Postgres and the cheapest source
  in the fan-out.
- **Go to is not a source** and costs the server nothing: it is rendered into the page
  and filtered in the browser.

**`pg_trgm` was not installed and no index was added.** Every one of those queries reads
a few hundred to a few thousand rows out of cache, ordered by a column an index already
covers or sorted in memory in well under a millisecond; an index on a column an `ILIKE
'%term%'` cannot use would be an index on a guess.

### Where it degrades first

At **four times the fixture** (2,000 Solutions, 6,000 environments, 5,000 releases,
2,500 builds, 3,000 recipes) the whole fan-out is still inside budget - 49-62 ms p50,
53-69 ms p95 - but it stops being flat, and the source that moves is **Releases for a
caller who does not bypass visibility**: 2 ms becomes 26-37 ms, while the same query
for an Admin stays at 5 ms.

The cause is worth writing down, because it is not the rows: `VisibleReleasePredicate`
is an anti-join over the locked Solutions, and its row estimate inflates the plan's
*cost* past Postgres's `jit_above_cost` threshold. Postgres then JIT-compiles the
statement on every execution - 23 ms of emission on top of 10 ms of actual work, which
`EXPLAIN (ANALYZE)` reports line by line. The Admin's predicate is `_ => true`, there is
no anti-join, the estimate stays low, and no JIT happens.

Nothing is being done about it now: it is inside budget, the per-source slice below
sheds it if it ever is not, and the fix would be to the shared visibility predicate that
Object Explorer's own release list uses - a change to make on its own evidence, not as a
side effect of a search box.

### A slice per source

A source that cannot answer in **100 ms** is dropped from *that* response and never
waited on. The other sources still return, and the palette shows the groups it has.

- 100 ms is the whole budget handed to one source: spending it alone has already broken
  the promise at the top of this section. It is a fence against a source that is stuck,
  not a deadline that a busy one trips - the slowest source above is 5 ms at p95, and 39
  ms at four times the fixture.
- It is enforced with a `CancellationTokenSource` linked to the request's own token, so
  the SQL command is really cancelled rather than left running against a connection
  nobody is reading. A request the browser aborted is still a cancelled request, not a
  dropped source: it propagates, and the sources behind it are never asked.
- A drop logs one **Warning** naming the source and the elapsed time. Never the query -
  that line lands in the container log.
- Every search logs its per-source timings at **Debug**, on a line that carries no query
  text, so "the palette feels slow" can be answered with which source is slow without
  reading a customer's name over somebody's shoulder. What was typed is on the sibling
  Debug line, as before.

## States

The list pane always shows exactly one of: the context block, recents, commands and Go to (empty
query - any of the first two may be absent), results,
"No search results for '...'" when the search came back with nothing, or "Search is not
available right now" when the request failed. The last two never stack - a failed request
has already explained the silence, so saying nothing matched on top of it would be a
second sentence about the same thing. Either way the closest page is still offered as a
row under Go to, so the palette never ends on a dead end - which is also why the sentence
says "no search results" rather than "nothing matches": something below it plainly did.
While a request is in flight the previous results stay put - no spinner flicker on every
keystroke, and the selected row keeps its place when the new ones arrive.

Keyboard: Up/Down move, Home/End jump to the ends, Enter opens, Ctrl/Cmd+Enter opens in a
new tab, Esc closes and puts focus back where it was. Enter only opens a row while the
input has focus; on Close or Clear recents it is that button's own press. On an act
command Enter and Ctrl/Cmd+Enter both run it - there is no page to open in a new tab.

The foot carries those hints and **at most two things that are not hints**, together at
its end: **Clear recents**, shown only while there are recents on screen, and a link to
`/docs/search`, which explains what the palette finds, in the words someone who has never
used one would use. Both are quiet text in the link colour, so the foot keeps one voice.
At phone width the key hints go and those two stay; the docs page is also in the "Go to"
list, for anyone who never looks at the foot.

### What a screen reader gets

The dialog is a labelled `role="dialog"`, the input is a `role="combobox"` with
`aria-expanded` / `aria-controls` / `aria-activedescendant`, and the list is a
`role="listbox"` of `role="option"` rows. Three things make that actually work rather
than merely validate:

- **Groups are groups.** A heading dropped loose into a listbox is not something a
  listbox may own, and the grouping - which is the difference between "Contoso" the
  Solution and "Contoso" the environment - would be invisible. Each group's rows sit
  inside a `role="group"` carrying the label; the visible heading is `aria-hidden` so it
  is not read twice.
- **The two replacing states are spoken.** Following `aria-activedescendant` a reader
  hears the selected row and nothing else, so "No search results" and "Search is not
  available" - which replace the rows rather than being one - would land in silence. A
  permanent visually-hidden `role="status"` in the dialog carries them, and the result
  count when a query is present. It is in the page before it ever has text, which is what
  makes an announcement fire at all.
- **Focus stays inside.** Tab and Shift+Tab cycle the input, the Close button and the
  foot's two controls when they are on screen, and never leave the dialog. (It was two
  stops until #887 put a second control in the foot; a keyboard user has to be able to
  reach Clear recents.) The input suppresses its own ring and the panel draws one
  instead, so the borderless box still shows focus.

Contrast was measured against `tokens.css` rather than judged: the group heading, the
foot and the input's placeholder moved from `--ink-4` to `--ink-3`, because at 11-12px
they are small text and `--ink-4` measures 3.75-4.23:1 in the two themes. The subtitle was
already `--ink-3` (5.85:1 / 6.57:1) and stands. Motion needs nothing: the palette has no
transition of its own, and `tokens.css` already neutralises the app's under
`prefers-reduced-motion`.

The one thing left failing is **not the palette's**: the selected row's `--primary`
keyline is 2.45:1 against `--surface` in light, under the 3:1 WCAG asks of a state
indicator - and it is the system's own selection keyline, shared with the sidebar's active
item, `.data-table` and `.run-row`. Diverging in one sheet would be worse than the defect;
it is recorded in the design brief for a decision upstream.

## Deliberately left out

- Commands that write to a tenant, ever (see "What it never does").
- Typed search modes (`>`, `@`, `#`, `/`) - see "No typed modes" under "Commands".
- Searching inside releases, source, or recipe bodies. That includes searching a
  release's *objects* from the palette (`table 18`, `page customer card`), which
  #883 raised as a stretch: the palette would have to pick which release the
  objects came from, and Object Explorer's own search already answers that
  question once a release is open.
- Ordering a group by anything but the shared ranking. #883 asked for a
  Solution's releases newest-first; ranking breaks every tie by title instead,
  for the reason under "Matching and ranking" - one ranking function, or it
  drifts per source.
- Typo tolerance and initials.
- Server-side recents, pinned items, per-user ranking.
- Builds and deliveries as rows of their own; the audit log is a "Go to" entry, never a
  search target.
- **BCQuality articles** (#885). The mirror has no page in the web UI - it is read through
  the assistant tools only (`.design/bcquality.md`) - so a row would have nowhere to land.
  It becomes a source when an article page exists.
- **Translation files** (#885). The Translator's files live in GitHub, listed live with the
  caller's own GitHub token, and who may see a repository is GitHub's answer, not a
  predicate the palette can run inside one query. The organisation-wide list the
  translation memory keeps of those files would show a repository's paths to people who
  cannot open it, and the Translator cannot be opened on a file by address either.
- **People by email.** A person is found by name only, and the row never prints an address.
