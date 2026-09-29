# Environment updates — the Upgrades fleet page

> **Status: shipped** ([#657](https://github.com/mtaanquist/ALDevToolbox/issues/657), stages 1–4b).
> The page is `Components/Pages/Upgrades/UpgradesPage.razor`; the services are
> `UpgradeFleetService` (read), `UpgradeActionService` (request/cancel/history),
> `UpgradeActionWorker` (booked slots) and the three write methods on
> `ProjectConnectionService`, all under `Services/ObjectExplorer/Bc/`. The mirror lives in
> `bc_next_update_*` columns and `bc_offered_versions` on `OeProjectEnvironment`; the
> actions and the history are one table, `oe_environment_upgrade_actions`
> (`EnvironmentUpgradeAction`). The grant is
> `team_members.manages_updates` — see [`teams-and-visibility.md`](./teams-and-visibility.md).
>
> This doc is the record of intent for the tool; where a detail has drifted, the code is the
> source of truth. Everything about *publishing builds* to an environment is a different
> flow and lives in [`saas-delivery.md`](./saas-delivery.md).

## Goal, and the named user

The upgrade team decides *when* around a hundred customers take a Business Central
platform update. Twice a quarter they open each customer's admin center and push the
scheduled update date out to the latest date Microsoft still allows, buying everybody time
before the release lands. Separately, a customer agrees a slot — "tonight at 20:00" — and
that environment is told to update then, whatever its own update window says.

Both were a hundred admin-center visits per sweep. `/upgrades` is one table over every
environment of every customer the viewer can see, with the same two moves as bulk actions
over a checkbox selection. A third came later (#960): the week a minor lands, the team wants
every customer queued for it - "everyone goes to 29.2" - which was a visit to each
environment's own page.

The named user is **a member of the upgrade team scheduling platform updates for a hundred
customers, who knows nothing about this codebase**. Everything the page says is written for
them: no class names, no column names, no API vocabulary.

## The grant

Acting on an environment requires `team_members.manages_updates` in one of the project's
assigned teams; org Admin and SiteAdmin may act everywhere. The reasoning — why a per-
membership flag rather than a fourth `UserRole`, why it is a different axis from managing
the team, the project, or owning it, and why it deliberately never enters the sign-in
claims — is `teams-and-visibility.md`'s *The environment-update grant* section, and is not
repeated here.

What matters at this end: `ProjectAccess` is the only authority. `CanUseEnvironmentOps`
gates the sidebar entry and the page without naming a project;
`UpdateOpsProjectPredicate` answers per row inside a list query;
`EnsureCanManageEnvironmentUpdatesAsync` re-checks on every write — including a write the
worker fires hours later. Because the flag is not in the cookie, a grant taken away this
afternoon is gone by the next page load rather than the next sign-in.

## The mirror — what the page lists from

A fleet page that asked Business Central per row would make a hundred round trips to draw
one table. So the *next platform update* for each environment is mirrored onto its row:
seven nullable `bc_next_update_*` columns holding the version, the type and status verbatim
as the API spells them, the scheduled date, the latest date the update can still be pushed
to, whether it ignores Microsoft's update window, and when the mirror last succeeded.
Opening `/upgrades` makes no call to Business Central at all.

**What is on offer.** Beside the one update, the same read keeps every version Business
Central offers the environment - its `available` updates, newest first - in
`bc_offered_versions` (a text array). The version change below needs all of them: to list
what can be picked, and to say which environments are not offered a version yet. Null means
the list has never been read, which a row mirrored before the column existed shows until
its next refresh; the preview then says the live re-read decides rather than guessing.

**Selection rule.** The *selected* update when the customer has picked a slot — that is the
answer even when a newer version is on offer. Otherwise the newest `Available` one,
compared numerically per segment (a string compare puts `10.1` before `9.2` and would
mirror last year's update as the next). Otherwise the six value columns are cleared: an
environment with nothing on offer shows nothing rather than a stale version. An unreleased
version is never a candidate — it carries no date to schedule.

**Freshness.** The mirror rides the same per-environment loop as the update-window mirror,
one updates call per environment, with the same failure isolation: one environment's
refusal costs neither the environment list nor the other environments' answers, and leaves
the previous mirror and its age intact rather than blanking it.
`bc_next_update_fetched_at` is stamped on every *successful* read, **including one that
found nothing** — "nothing is scheduled" and "we never asked" are different facts and the
page says which it has.

Four things fill it. A consultant's Refresh on the project's Business Central tab; a
nightly sweep (`EnvironmentRefreshScheduler`, a fixed quiet UTC hour, `DeliveryScheduler`'s
shape) that offers every BC-connected project to the in-process
`EnvironmentRefreshQueue`/`Worker` pair so the fleet is fresh each morning without anyone
opening a project; and the page's own **Refresh** command, which feeds
the same queue so a sweep and a hand-triggered refresh coalesce. Rather than telling the
reader to reload after that, the page polls itself every 20 seconds for up to three
minutes, on the renderer's synchronisation context so a tick cannot collide with a click on
the circuit's one `AppDbContext`; a **Reload now** button in the same notice is there for
anyone who doesn't want to wait. The sweep takes a
non-user-gated refresh path (the `AcquireDeliveryContextAsync` precedent) and never stamps
`bc_connection_verified_at` — a refresh nobody asked for must not present itself as the
consultant's own connection test.

The fourth is the Environments list's **Refresh every 5 minutes** switch (#983), for
somebody keeping the list open on a second screen through a release week. It is off on every
visit and remembered nowhere (`?auto=1` in the address opens the page with it on); on, it
takes the same path as Refresh every five minutes and shows the answers through the same
20-second re-read. Two things keep it a guest:

- *A freshness gate, on the server.* Every successful read of a solution's environments
  stamps `oe_projects.bc_environments_fetched_at`, and an unforced refresh request leaves
  out a solution read less than four minutes ago - four, not five, so a five-minute tick
  never lands just inside the window and skips a round. The queue's dedupe only coalesces
  requests while a job is queued or running; the gate is what makes ten open pages cost
  the same as one, because the organisation asks about each customer at most once per
  window however many people are watching. The page says "already up to date" when a
  whole round was fresh. A hand-pressed Refresh, on this page or on Upgrades, is forced
  and bypasses the gate: the person has decided the rows are too old.
- *A two-hour stop.* The page cannot tell a hidden tab from a watched one without script,
  so it bounds the time instead: after two hours the switch turns itself off and the strip
  says so. A tick is skipped while a Refresh or its re-reads are running, and while the
  delivery-window dialog is working, since they share the page's database context.

At its busiest that is twelve rounds an hour per customer, each four plus three per
environment requests (below) - about 130 an hour for a customer with three environments,
and for a hundred customers about a thousand requests every five minutes from the one
worker, under two minutes at its one-at-a-time pace.

**The sweep is a guest on somebody else's API, and behaves like one.** Microsoft documents
no limits for the admin center API, so there is nothing to pace against in advance; what we
can do is be unhurried and do as we are told.

- *One request at a time.* One worker drains the queue, and a solution's calls - a token,
  the environment list, then the update window, the next update and the installed apps for
  each environment, and the tenant's storage (four plus three per environment) - go out one
  after another. A customer
  is its own Microsoft tenant, so each sees a handful of requests a night.
- *A breath between customers.* The worker waits a second before the next solution. Nobody
  is waiting on the sweep; a hundred customers cost under two minutes.
- *Not on the hour.* The sweep starts a random few minutes into its hour (up to forty,
  drawn once per process), because everything else in the world fires at 03:00 sharp.
- *Told to slow down, it slows down.* `BcThrottleHandler`, on the shared Business Central
  HTTP client, retries a **read** answered with 429 (or a 503 that names a wait) once,
  after the `Retry-After` it was given, capped at a minute. A write is never re-sent on
  our own initiative. Still throttled after that is an ordinary failed read: the customer
  keeps last night's mirror.
- *A watched update is read on its own.* The Upgrades page's watch after Start update reads
  one environment twice every ten seconds, bounded to ten environments a tick and slower above
  that, and only until the update ends or 45 minutes pass - see "The page".
- *Parallelism was considered and left out.* Several customers at once would be safe for
  the same per-tenant reason and would shorten the run, but it means a degree-of-parallelism
  knob on `QueueDrainWorker`, which every worker inherits. The worker now logs each run -
  solutions, requests, elapsed - so that decision can be made on a measurement.

**Storage.** The same refresh reads the tenant's storage in two calls per customer
(`/environments/usedstorage` and `/environments/quotas`), not per environment: every
environment's database size, and the one allowance they share. That shape decides the
display. The Environments list shows each environment's own size, and under it a bar for
the *customer's whole tenant* against its allowance - repeated on each of the customer's
rows - because the tenant is what runs out. The bar is amber from 80% and red at or over
100%; Business Central lets a tenant go over, so red is a state rather than a ceiling, the
bar stops at full and the words carry the rest ("Customer at 115% of 80 GB - over its
allowance"). A customer at or over their allowance counts under **Needs attention**; one
that is merely filling up does not. The environment page shows the size in its meta row and
the same sentence as an alert, which also says that deleting a sandbox frees room. A size
Business Central could not work out (it reports -1) is left blank, and an allowance of zero
or none is "not read", never "full". Stored as `oe_project_environments.bc_database_kb`
and `oe_projects.bc_storage_quota_kb` / `bc_storage_fetched_at`; a failed read keeps the
last figures. That makes the sweep four requests per solution plus three per environment.

The queue holds 256 solutions and **waits** when full rather than dropping, so nothing is
lost past that size; the scheduler restarts its heartbeat's active clock on every job it
gets in, so waiting for a slot does not read as a stall on `/healthz/workers`, while a
worker that has really stopped still does.

The **full** updates list is still fetched for the environment panel rather than read from
the mirror: the mirror is one row for listing many environments, not a replacement for the
detail a consultant opens on purpose. That fetch is cached briefly once made — see "The
environment panel" in `saas-delivery.md`.

## The three writes

All three re-read the environment's updates live first (so the page and the write can never
disagree about which update is meant), and all three re-mirror the row from a fresh read
afterwards so the table shows the change without waiting for the nightly sweep. A failed
re-read costs the freshness, never the write. The two date moves act on the update the
selection rule picks and are gated on the environment-updates grant rather than on managing
the project; the version change picks the update itself.

- **Push the date to the latest** sets the date as late as Business Central will take it.
  The `latestSelectableDateTime` the updates read gives back is an *exclusive* bound — a
  value of midnight UTC means "before that day", which is why the admin center's own picker
  stops the day before — so a midnight bound is turned into the previous day and only a
  bound carrying a time of day is sent as it stands. The write is then verified rather than
  trusted: the workbench recorded the move as done while the date stayed exactly where it was,
  so the re-read that re-mirrors the row is also what proves the date changed, and an
  unchanged date fails the action. The test is whether the date *moved*, not whether it
  landed on the day we asked for — Business Central stores it at the start of the customer's
  update window, which for a window opening after midnight UTC is the following day. That is
  also why the refusals compare calendar days: it refuses when there is no update on offer,
  when Business Central gave the update no latest date, and when the date already sits on or
  past that day. The same reading decides what the fleet page shows as the latest allowed,
  so the page and the admin center agree.
- **Update now** sets the date to the current moment and is the *only* operation that ever
  ignores the environment's update window — a customer who has agreed a slot is asking for
  the upgrade regardless of their window, and nothing else has the right to take that
  protection away. Refuses only when there is nothing on offer.

- **Change the next version** (#960) selects a different version as the environment's next
  update: "everyone goes to 29.2", forward from an earlier version or back from a later one.
  It is `SelectTargetVersionAsync`, the same write the environment page's "Next Business
  Central update" setting has always used, now also run over a selection from the Upgrades
  page. It refuses a version the live read does not offer (not rolled out to that region or
  tenant yet, or one the environment can only reach through an earlier major), and it
  refuses while an update is running, which Microsoft owns. It normally sends **no date**:
  Business Central keeps or assigns one inside the new version's rollout, and the latest
  possible date changes with the version, so moving the date is a second step afterwards.
  The exception (#980) is a target update that already carries a date in the past (or
  within five minutes of now, which will have passed by the time the write lands):
  Business Central refuses to select it until the date is changed, so a date goes with the
  selection - the customer's current slot when it is still ahead and inside the new
  version's latest date, so the agreed day survives the change, otherwise that latest date
  (where Move dates would put it). With no latest date to fall back on it refuses and sends
  the person to the admin centre. It never ignores the update window.
  Like the date push, the re-read is also the proof - a re-read that still shows another
  version selected fails the action rather than recording it as done. Gated on managing the
  project *or* the grant, because picking the version was open to a solution's managers
  before the fleet action existed; the fleet action itself asks for the grant, like the
  other two. The preview sorts each selected row from the mirror alone
  (`UpgradeFleetService.PreviewSelectVersion`): will change (forward, or back), already on
  it (the environment runs the version or a later one, by numeric segment), already chosen,
  not offered, update under way (Microsoft's, or one of our own actions waiting to fire),
  no access, and gone. The picker's list is every version the selected rows are offered, as
  major.minor, newest first, with how many rows each applies to
  (`UpgradeFleetService.OfferedVersions`).

Each refusal is a `PlanValidationException` the fleet page shows against that one row, not
a failure of the batch.

### The wire shape

All three go through the same `PATCH .../environments/{family}/{name}/updates/{targetVersion}`.
The version change sends `selected` and `targetVersionType` alone, unless its target carries
a past date (see above); the two date moves add a `scheduleDetails` object alongside them. The two scheduling fields go **inside**
that object, where the updates read also returns them. Sent at the top level they are
ignored with a 200, which is how the first version of this failed to move any date:

| Field | Shape | Sent when |
|---|---|---|
| `selected` | JSON boolean, always `true` | always — a date set on an update the customer had not picked selects it in the same request |
| `targetVersionType` | string, verbatim from the updates read | when the read gave one |
| `scheduleDetails.selectedDateTime` | ISO-8601 in **UTC** (`yyyy-MM-ddTHH:mm:ssZ`) | only when the caller is moving the date, or by the version change when its target carries a past date (the current slot if still allowed, else the latest date); omitting it leaves the customer's existing slot alone |
| `scheduleDetails.ignoreUpdateWindow` | **a real JSON boolean** | only by "update now" |

`ignoreUpdateWindow` is a boolean and not the string `"true"` the Microsoft 365 licence
endpoint documents, because this body already carries `selected` as a boolean and the same
endpoint reads both flags back as booleans. **If Business Central ever refuses it, the
string form is the first thing to try** — that is the documented fallback, and this API
family has drifted on exactly this before, which is why every flag is read back
case-insensitively and no logic keys on localized text.

**Don't write a concrete Admin Center API version into this doc.** It lives in exactly one
place, `BcConstants.AdminApiVersion`, watched by `.github/workflows/bc-api-version.yml`;
naming a version in prose is how the last one rotted eight releases behind. Same rule as
`saas-delivery.md`.

## Actions and history — one table

Every move is one row in `oe_environment_upgrade_actions`, and those rows **are** the
per-environment activity feed; there is no second log behind it. A row carries the customer,
the environment, the kind (push-to-latest / run-now / select-version, and the one-off writes
recorded beside them), the version a select-version asked for (`target_version`, null for
every other kind), a status, who asked and when (as a
denormalised `"name <email>"` string, so the history still names them after the account is
gone), the fire time, when it was sent, the outcome in plain words, and who cancelled it.
The table is deliberately **not** in `AuditInterceptor`'s audited map: it is itself a log,
and auditing a log records every event twice.

Status is `Pending → Sent | Failed | Cancelled`.

**Immediate is a direct send.** "As soon as possible" calls Business Central on the request
thread and the row is written in its finished state, `Sent` or `Failed`, in the same
operation. There is no worker hop and nothing to cancel, because by the time the row exists
the change has already landed or been refused. A refusal writes a `Failed` row *and*
rethrows, so the page shows its usual per-row message while the feed keeps the attempts that
came to nothing.

**A booked slot is a `Pending` row and nothing else.** "At a time we agreed" writes the row
with its fire time and calls nobody. Nothing is enqueued: `UpgradeActionWorker` finds due
rows by polling the table every 30 seconds, so a slot booked for tonight survives this
afternoon's deploy, which an in-memory channel would not. Only "update now" offers a slot;
push-to-latest is housekeeping ahead of a release and is always immediate, though it records
its rows the same way so one feed reads uniformly. The version change is immediate on the page
too; the service and the worker would carry a booked one, version and all, but no page offers
it yet. The worker's per-org enumeration is the
one cross-org read, and it needs no bypass — the organisations table carries no tenant
filter; per-org work stays inside the filter.

**The race rule.** Cancel works until the worker sends. The worker claims a row by stamping
`sent_at` while it is still `Pending`; a cancel is an `UPDATE ... WHERE status = 'Pending'
AND sent_at IS NULL`. Both sides are the same compare-and-set
`DeliveryService.RunDeliveryAsync` uses, so exactly one wins and the loser is told in words
— a cancel that arrives too late says the action has already run rather than appearing to
work, and a send that arrives after a cancel never touches the tenant. There is deliberately
no version column: with a token the loser would have to re-read and work out what the new
state meant, which is the question the `WHERE` clause already answers. A row left
claimed-but-unfinished by a restart is failed on the worker's first sweep after it, never retried — we
know the send started and not whether it landed.

**Each booked row fires as the person who booked it.** The worker enters the ambient org
scope with the requester's user id (the `DeliveryWorker` precedent), which buys two things
at once: the audit row names them rather than "unknown", and the grant is re-checked as
theirs at fire time, so somebody taken off the upgrade team during the afternoon does not
get their evening slot fired anyway. The writes re-read the environment live, so an update
applied or withdrawn in the meantime, a blocked environment, or rotated credentials land the
row as `Failed` with the reason in the feed rather than guessing. One row's failure never
stops the sweep.

**An entry's headline says which thing was asked for.** Booking an update and starting one
are opposite claims, so the feed compares the fire time to the request time and titles the
entry accordingly: a booking stays "Booked the update" whether it is still waiting, has since
run, or was called off; an immediate send says "Started the update", and one that was refused
says it tried. Failure outcomes are composed in the past tense at the moment they are stored,
with any trailing "try again" advice dropped — the refusals are worded for somebody standing
at the form, and a history entry read a week later must not claim an environment is still busy.

**Reading the feed is a visibility question**, not an ops one: anyone who can see the customer
can read it (`EnsureCanViewAsync`), and only Cancel needs the grant. It shows the newest 50
entries for one environment, and one component (`Components/Shared/EnvironmentActivityFeed`)
renders it in both places — the Upgrades page's per-row Activity panel and the environment
panel on a project's Business Central tab — so the history cannot read differently depending
on which page somebody opened. Its empty state says nothing has been done to this environment
yet.

## Planned upgrades: a header with lines

> **Status: engine only** ([#984](https://github.com/mtaanquist/al-workbench/issues/984), sub-issue B).
> `EnvironmentUpgradeService` and `EnvironmentUpgradeLineState` under
> `Services/ObjectExplorer/Bc/`; no page yet. The agent surface exists (sub-issue F): the
> read-only MCP tools `list_planned_upgrades` and `get_upgrade` in `DeliverTools`, and
> Upgrade as a command-palette result kind (`UpgradePaletteSource`, opening `/upgrades/{id}`).

The flat table is the right tool for ad hoc work and the wrong one for a wave. The team agrees
the same evening slot with eight customers, starts them at 20:00, and the next morning wants
exactly those eight with a tick beside each one they have checked. So a batch is now a thing
with a name: an **upgrade** ("28.5 in November 2026") holding **lines**, one per environment,
in the Business Central header-and-lines shape the team asked for.

**The tables.** `oe_environment_upgrades` is the header: a name, a target release as
Major.Minor, an optional planned slot (advisory - nothing fires from it), a note, who made it
and when, and who marked it done and when (`closed_at` / `closed_by`, null while open). The
people are stored twice, as a nullable user id (`SET NULL`) and a denormalised
`"name <email>"` string, for the same reason the action rows do it: the record has to name
them after the account is gone. There is no soft delete. An upgrade can be deleted outright
only while no action row carries its id; after that it is part of the record and the way out
is marking it done.

`oe_environment_upgrade_lines` holds the environment, its solution (denormalised, so the
visibility join runs on the line's own `project_id`), who is to check it, and the check itself -
who ticked it, when, and a short note ("posting OK, reports OK", at most 500 characters).
Unticking clears the who and the when and keeps the note. A line can be taken off while nothing
has been done to that environment from this upgrade.

`oe_environment_upgrade_actions` gains a nullable `upgrade_id`. An action run from an upgrade
carries it; an ad hoc one from the fleet table or an environment's page keeps null. An action
may only claim an upgrade that is open and has that environment on it. The history reads the
upgrade's name through it, so an entry can say which wave it belonged to.

**One open upgrade per environment.** Two open waves both claiming the same customer is the
confusion the feature exists to remove. The rule is about the *parent* being open, and a
Postgres index cannot filter on another table's column, so the line carries a copy of that
fact - `is_open`, set false when the upgrade is marked done and true again on reopen - and a
unique index on `environment_id` filtered on it holds the rule. The service checks first so a
refusal can name the other upgrade; the index is what holds when two people race. A second,
unfiltered index on `environment_id` covers the foreign key's cascade, which the filtered one
would not (see CLAUDE.md on zero-scan indexes). Adding is judged per environment, like the
fleet actions: one that cannot go on does not cost the others.

**The grant, and the join.** Anyone in the organisation may create an upgrade and edit its
name, target, slot and note; the header holds no customer data. Everything that touches a line
needs the environment-updates grant on that line's solution - adding, taking off, assigning,
checking - and the moves that change every line at once (marking done, reopening, starting
the leftovers, deleting) need it on every solution the upgrade touches. Lines are read only
through `VisibleProjectPredicate`, as the fleet is: a line from a solution the viewer cannot
see is left out of the upgrade and out of its counts, not shown blank.

**The derived states.** A line's state is never stored. `EnvironmentUpgradeLineState.Derive`
works it out from the fleet row (the mirror) and the action rows carrying this upgrade's id,
and the first rule that matches wins:

1. **Checked** - somebody ticked it. A person's word outranks the mirror.
2. **Running** - Business Central is busy with the environment: its state is busy, or its next
   update says it is running. The same test the page's watch after Start update uses (#982).
3. **Updated** - the environment is on the target release or a later one, Major.Minor compared
   numerically per segment.
4. **Failed** - the latest action from this upgrade failed, or the environment is in one of
   Business Central's `*Failed` states. The mirror keeps no separate "the last update failed"
   fact, so an update that ran and left the environment running on the old version reads cold
   as not started; only the page's watch, having seen it busy, can call that a failure.
5. **Booked** - a Start update from this upgrade is waiting for its slot, or the latest action
   is a Start update Business Central accepted and has not picked up yet.
6. **Date moved** - the latest action this upgrade sent was a date move that worked.
7. Otherwise **Planned** - which is also where a cancelled booking lands.

The upgrade's own status follows from its lines: **Done** once marked done, whatever the lines
say; **In progress** while anything is booked or running, or while the wave is part way
through; **Updated** once every line is updated, failed or checked, waiting for somebody to
call it done; **Planned** while nothing has gone further than a moved date. The list counts
each open upgrade's lines by state, so the morning after reads from the list.

**Done, reopen, and the leftovers.** Marking an upgrade done stamps who and when, releases its
environments for another open upgrade, and says how many lines were still unchecked so the
page can ask first. Done upgrades are the archive, searchable by name and target release and
read-only apart from reopening. Reopening is refused when any of its environments has
meanwhile gone on another open upgrade, naming them. **New upgrade from the leftovers** makes
an open upgrade with the same target and note whose lines are the ones that failed or were
never started (Failed, Planned, Date moved). It needs the source done first: while the source
is open it still holds those environments, and taking them off it quietly would rewrite a wave
somebody is still working.

## The page

One table, one row per non-missing environment of every project the viewer can see: the
customer, its state as a glyph, the environment over its type, the version it is on, the
mirrored next update (version, when, and a marker when it ignores Microsoft's window), the
latest date that update can still be pushed to, how old the mirror is, and a row menu. The
solution's short name follows its name, as on the Solutions list (#966). Above it sits one
sticky command bar: a search that filters as you type and matches the solution, its short name
and the environment, a view select (all, update waiting, and each environment type with and
without an update waiting), and the commands - search first, then the filter, then the
commands, the order Solutions and Environments use (#966). The filters live in the address (`q`, `type`, `waiting`); loading, empty and populated states as
usual. The layout is the design's archetype 15 - see "The Upgrades page, against its designed
sheet" below.

**The selection outlives the search.** The team builds one evening's batch by finding each
customer by short name in turn, so the search and the view never untick a row (#985) -
ticking three customers and then searching for a fourth used to lose the first three. Only a
re-read of the fleet drops a tick, for an environment that is gone or that the viewer may no
longer act on. Because part of the selection can then be off screen, the bar says so once
anything is ticked: "8 selected, 5 shown" (just "8 selected" when every tick is on screen),
then **Show selected** and **Clear selection**. Show selected replaces the search and the view
rather than narrowing them: the reader asked to see the selection, and a search left in the
box from finding the last customer must not hide the other seven. Changing the search or the
view afterwards ends it, with every tick kept. The header box keeps meaning "every row shown":
it ticks or unticks the rows on screen and leaves a hidden tick alone. The commands act on
the whole selection, on screen or not, and every confirm names each environment before
anything is sent, so a hidden tick is seen before it is acted on. The Environments list
works the same way; both share `SelectionSet`.

**The join is the guard.** `OeProjectEnvironment` has no visibility rule of its own — it
inherits its project's. `UpgradeFleetService.ListFleetAsync` therefore reaches the
environments table *through* `VisibleProjectPredicate`, and any future query that lists
environments must do the same rather than reading the DbSet directly. "May act" is computed
in the same query from `UpdateOpsProjectPredicate`, so a fleet of a hundred costs one round
trip; a row the viewer may see but not act on shows a lock instead of a checkbox. The org
fence sits underneath both.

**Three actions, each in its own voice.** Each runs over the checkbox selection - or, from a row's own
menu, over that one row, leaving the ticked rows as they were - behind a confirm that
lists every selected environment with what will happen to it and — grouped at the bottom
under its own heading — the ones that will be passed over and why.

- **Move dates** previews each date and the date it moves to. It is the page's one primary
  button: it is what the team comes here to do, a hundred at a time.
- **Start update...** is the sterner one, and its dialog is where that is said; in the bar it
  is a plain button, as the sheet has it. The sheet calls it "Update now", but the dialog also
  books an update for a later slot, and nobody wanting tonight at 20:00 presses a button called
  "now"; the dots say a dialog follows. The dialog says plainly that Microsoft will start the
  updates whatever the
  environment's update window says, counts the production environments in the selection just
  above the gate, and holds its confirm button disabled until the person types "update".
- **Change the next version...** (#960) opens a picker of the versions Business Central last
  offered the selected rows, newest first, each "for N of the selected"; nothing is picked
  for the person unless only one version is on offer. Picking one previews every row under
  the groups of "The three writes" - Will change (with "28.5 to 29.2" or "30.0 back to 29.2"),
  Already on it, Already chosen, Not offered, Update under way, No access, Missing or being
  deleted - says once that Business Central sets the date and Move dates is the way to push
  it out afterwards, counts the production rows, and asks for the same typed "update". The
  run is immediate (no booking in this version) and sends only the rows the preview put under
  Will change; the others are reported on their rows with the preview's reason, so a row
  with a booked update never has its version changed under the booking.

That dialog re-voices itself on the choice inside it, because immediate and booked carry
opposite promises. Immediately keeps the danger button, the typed word, and "once it starts
you cannot stop it". A booking gets the normal button, no typed word, a confirm label carrying
the time, and the sentence that makes it safe — cancellable from the Upgrades page until it
runs. Saying "you cannot stop it" over an action with a Cancel button would be a plain
contradiction, so `ConfirmDialog` learned to keep tracking its parameters while open (only for
callers that opened it on its own parameters) and to accept a caller-owned `ConfirmDisabled`,
which is what holds the button while the picked time has already passed for some customer.

**Times belong to the customer.** A slot is picked and displayed in the project's own
Business Central time zone, named explicitly beside the picker, and stored UTC. Across a
selection spanning zones the same wall clock is read *per customer* — "20:00 in each
customer's own time zone" — which is what "tonight at eight" means to the person who agreed
it, and the dialog says so rather than quietly picking one zone for everybody. Because a
`datetime-local` field renders in the browser's locale, the booking is echoed under the field
in the page's own 24-hour format, naming the zones, and *that* sentence settles what was
picked. A time already past is refused per customer **by name**, since the reason only some
are past is that they are in another country.

**Running a batch.** The run is sequential in the page's own circuit, reports per row as it
goes, never lets one environment's refusal end the batch, and can be stopped between
environments (never mid-write). Afterwards the page re-reads the fleet so the rows show the
re-mirrored truth. The summary sits in a sticky bar above the table — findable after a long
batch has scrolled — and takes a warning tone whenever anything was skipped or failed, because
that is not a neutral outcome. Two kinds of per-row refusal are told apart by the
`PlanValidationException` field key rather than by reading the sentence: an `Environment` key
means the customer's connection needs attention somewhere else, so the row links to the project
and the summary says so; an `Update` key means this particular update can't move, which its own
message already explains. A genuine failure never shows raw exception text — the row says
Business Central didn't accept the change and links to the project, and the detail goes to the
log.

**Watching an update through (#982).** After an immediate Start update the person often
stays on the page to see it finish before calling the customer back, so the page watches it
rather than asking them to press Refresh. An environment joins the watch when its Start update
lands as sent, and on load when its mirrored state is busy (`BcEnvironmentStatus`'s Busy group)
or its next update is running - so a slot the worker fired at 20:00 is watched too when somebody
opens the page - and only on rows the viewer may act on, because the re-read asks for the same
grant. Every ten seconds the page calls `ProjectConnectionService.RefreshEnvironmentAsync` for
each watched environment: two reads against that one tenant (the environment by name, and its
updates list), mapped onto the row by the same helpers the environment list and the sweep use,
stamped, and the panel cache for that environment invalidated. The service hands back what the
row now says, so a tick costs the page no fleet query of its own. The row shows "Updating...
started 6 minutes ago" under the next update while it is watched. It leaves the watch when the
environment is running again and the update is no longer under way: "Updated to 29.2" when it is
now on the version it was going to, else "Update failed" with a link to the environment's
Operations tab. A running environment still on the old version before Business Central was ever
seen busy has not picked the update up yet, and stays watched. The watch stops, with a line
saying so and that Refresh starts it again, after 45 minutes, after three reads in a row with no
answer, or at once when the environment is gone or its connection needs setting up. It is a
timer of its own beside the 20-second database poll above (which it leaves alone), with the same
rules: ticks on the renderer's synchronisation context, none while a run or a dialog is open,
stopped when the page goes. **As a guest** it is fine: two requests every ten seconds for one
update, against the one tenant whose update was just started, is a small load and a short one:
it ends with the update. The cap bounds the rest - a tick
reads at most ten environments, longest-waiting first, and with more than ten watched the tick
slows to thirty seconds, so fifty updates started at once cost at most twenty requests every
thirty seconds, spread over fifty tenants, not six hundred a minute. A throttled read is retried
once by `BcThrottleHandler` like any other.

**A booking is visible on the fleet row itself**, not only in the batch result that made it
(which a reload discards). One booking shows the whole fact — when, in whose time, who booked
it — with a Cancel beside it; several show the nearest and a count. Either way that marker *is*
the disclosure that opens the history, so "Update history" in the row menu is a second door
and never the only one. Confirming an update-now over an environment that already has a booking waiting groups it
under "Already booked" in the preview, with what it is booked for: the run still acts on it, and
adding a second booking is a thing to notice before the click.

**Audit.** Each of the three writes records an audit row, and this is the one place in the
application that writes to `audit_log` outside `AuditInterceptor`. It has to be: the writes land
on the customer's tenant and touch no row of ours that the interceptor watches — and the
re-mirror afterwards is deliberately outside `AuditInterceptor.EnvironmentSettingColumns`,
because the nightly sweep writes those same columns and would otherwise fill the log with rows
nobody made. The entry is an `OeProjectEnvironment` row keyed by the environment id, and its
snapshot keeps the log's "state before the change" contract — the update as we read it, plus a
plain-words `Action` naming which of the three writes it was, since the audit model records rows
changing and these are events. The actor is resolved from the database rather than from claims,
because a Blazor circuit has no `HttpContext` for the interceptor's own lookup to read. A refused
row writes nothing: nothing changed. For a booked action the audit row is written at send time,
by the worker, so the log records what actually reached Microsoft while the activity feed records
the whole request-and-cancel story.

## Deleted environments, and bringing one back

A customer who deletes a Business Central environment does not lose it at once. Microsoft
soft-deletes it: the environment still answers from the admin center API, carrying the day
it was deleted, the day it stops being recoverable, and the customer's stated reason, and
until that second day it can be brought back with everything it held. Business Central also
renames it on the way out, which is what the fold in `UpsertEnvironmentsAsync` is for — see
"soft_deleted_on and missing_since" in [`saas-delivery.md`](./saas-delivery.md).

**It is not one more row with a red state.** Nothing can be published to it, updated on it
or rescheduled for it, so listing it beside the live environments makes a fleet look both
bigger and sicker than it is; and "needs attention" is a list of things somebody has to go
and do, which a deletion somebody already decided on is not. So:

- The **Environments list** hides deleted environments from All, Update scheduled and Needs
  attention, and gives them a **Deleted** view of their own with its count. That view is
  offered only when there is something in it — a view that is always empty is one people
  learn to ignore, which is exactly the view that has to be noticed on the fortnight it
  isn't. In it, the Next update column becomes **Gone for good** and carries the deadline in
  the line the version would have had ("Gone for good on 4 Oct 2026", or plainly that
  Business Central hasn't given a date), with how long is left under it ("3 days left to
  bring it back") — a date alone makes somebody scanning a hundred rows do the arithmetic per
  row to find the customer who needs a call today, which is the question the view exists to
  answer. Recover leads the row menu, in place of Upload an app, and a line above the table
  says these can be brought back: the pill's tooltip says the same, but a tooltip does not
  exist on touch and never appears on a keyboard, so it can never be the only place the
  meaning lives.
- The **Upgrades page** doesn't list them at all. They are dropped in
  `UpgradeFleetService.ListFleetAsync`, not in the page, so the counts, the checkbox
  selection and both bulk actions agree without each having to remember. The Environments
  list asks for them back with `includeSoftDeleted`.
- The **environment's own page** keeps working — the links from the Deleted view have to
  land somewhere — and leads with a danger alert saying when it was deleted, when it is gone
  for good *in the list's exact words* (the two are read in the same minute, and on a
  fortnight's window a day either way is a customer's data), how long is left, and that
  Business Central refuses an install, an update or a settings change until it is back.
  Someone who may act gets Recover; someone who may not is told who to ask, as every other
  locked part of that page does. The alert is first because it changes what everything under
  it means: the version, the two windows and the app lists are all the state the environment
  was in on the day it was deleted.
- The **Modules card** on a solution's Customer tab reads the installed apps from the
  customer's production environment, and never picks a deleted one — its app list is what
  was installed the day it was deleted, which is not what the customer runs now. The
  Solutions list's module filter reads the same environments, so the two cannot disagree
  about who has a module.

**The mirror.** `soft_deleted_on`, `hard_delete_pending_on` and `delete_reason` sit on
`oe_project_environments` beside the rest of the fetched detail, written by the same refresh
and **cleared by it** when the environment is live again: "no longer deleted" is a fact the
mirror has to be able to state, or a recovered environment would sit in the Deleted view for
ever. Microsoft returns those three PascalCase beside camelCase neighbours, so the admin
client reads every property case-insensitively — a case-sensitive lookup read each deleted
environment as one with no dates at all, silently. `BcEnvironmentStatus.IsSoftDeleted` is the
one place the status is compared, and `EnvironmentQueries.NotSoftDeleted` the one place the
same question is asked in SQL.

**Recover** is `POST .../environments/{family}/{name}/recover` with no body. It is a write to
the customer's tenant and carries the four things every such write does: it is gated on
managing the solution (`ResolveEnvironmentAsync`), it sits behind a confirm that names the
environment and its customer and says out loud when it is a production one, it records
"Recovered the environment" in that environment's Workbench history, and a `BcApiException`
reaches the page as a sentence — the two codes Microsoft documents here, an environment
already being recovered and one whose state forbids it, are told apart rather than both
arriving as "the API refused it". An environment that was never deleted is refused before
anything is sent.

One verb, everywhere: the menu item, the dialog's title and its button all say **recover**,
which is what the admin centre calls it. Never *restore* — in Business Central that is the
point-in-time restore of a live environment, and somebody would reasonably ask which point
in time.

Business Central *schedules* the recovery rather than doing it there and then, so the write
is followed by a re-read of the customer's environments: the row moves to `Recovering` and
then out of the Deleted view on its own. A failed re-read costs the freshness, never the
write.

**Deliberately not built.** Deleting an environment, renaming one and restoring one to a
point in time all stay in the admin centre. Recover is here because it is the one of them
with a deadline — a fortnight, after which nobody can do it at all — and because the workbench
is where a deleted environment is noticed. Copying one is here for the opposite reason: it
has no deadline and is simply the thing an ops engineer does most often, which is the next
section.

## Copying an environment

The most common errand this page's reader would otherwise open the admin centre for: make a
copy of an environment, almost always a customer's production into a fresh sandbox, to try
an update or reproduce a problem on their real data. Named user: the same consultant or ops
engineer who manages the customer's solution. It is one `POST
.../environments/{family}/{source}/copy` carrying the new environment's name and its type,
and it is the one write here that *adds* something to the customer's tenant — it counts
against their storage allowance, and a production copy against their licences.

It carries the four things every tenant write does: it is gated on managing the solution
(`ResolveEnvironmentAsync`), it sits behind a confirm that names the environment and its
customer, it records "Copied the environment" in the **source** environment's Workbench
history — the one that existed when it was asked for, and the one somebody later asks where
the sandbox came from — and a `BcApiException` reaches the page as a sentence. Every code
Microsoft documents for this endpoint is told apart, because they are different situations
with different next steps: a name already taken, a name against the rules, a tenant out of
environments, out of storage, or already making one, a source that has gone, and a source
whose uploaded extensions clash with developer extensions in the copy.

**Two things are refused before anything is sent.** A source the customer has deleted, which
has nothing to copy until it is back; and a source Business Central is not reporting as
ready, because an environment part-way through an update is a moving target and a copy of
one is a copy of a moment nobody can name. That second reading is `BcEnvironmentStatus`'s,
the same one the delivery gate makes — ready, or a status we have no opinion about.

**The name rules are Microsoft's and live in one place.** `BcEnvironmentName` holds them:
start with a letter, then letters, digits, dashes and underscores, fewer than thirty
characters. The service is the source of truth and the dialog mirrors them in `pattern` and
`maxlength`, so the browser answers first and the rule is on screen as the person types —
Business Central's own refusal arrives minutes later as `environmentNameNotValid`, which is
too late to be help. A name the solution already has is refused from our own mirror,
case-insensitively, before a round trip.

**The dialog says what the copy is, not how the copy works.** It defaults the name to
`<source>-Copy` and the type to Sandbox, which is the rarer-is-dearer way round: a
production copy turns the confirm red and says it costs the customer a licence and one of
the production environments they are allowed. Copying production into a sandbox says plainly
that the sandbox will hold the customer's real data, because a sandbox is not a blank
environment and everyone let into it can read all of it. A customer at or over their storage
allowance is **warned and not blocked** — Microsoft decides whether there is room, and by
the time somebody reads the warning capacity may have been added.

**Nothing waits for it.** Business Central schedules the copy and takes its time: the new
environment appears in the environments list as `Preparing` and turns `Active` when it is
ready, which can be an hour later. The write is followed by the same re-read Recover does,
so the new environment shows up as soon as Microsoft lists it — and **its absence from that
read is not an error**, which is the whole reason the dialog and the success notice both say
where to go and watch instead: the source environment's Operations tab, which is Business
Central's own record of what it is doing. No polling beyond that, and no job row of ours.

**Copy is the one row-menu entry offered only to a manager.** Its neighbours on the
Environments list are one click and a refusal; this one asks for a name and two decisions
first, and taking all of that back with "you may not" is a worse answer than never having
asked. `UpgradeFleetRow.CanManage` carries that answer, computed as a subquery over
`ProjectAccess.ManageProjectPredicate` in the same round trip as `CanAct` — never a
substitute for the service-side check, which is made on every write regardless.

## Sessions

The classic support call: "posting has been running for an hour and everything is locked."
Until now the fix meant opening the customer's admin centre, finding the environment,
opening its Sessions page and cancelling the one that is stuck. Named user: a support
consultant or an ops engineer who manages the customer's solution, on the phone to the
customer while they do it. The **Sessions** tab on the environment's own page is that
errand, and ending a session is the write behind it.

It is one read and one write on Microsoft's documented session endpoints:
`GET .../environments/{family}/{name}/sessions` and `DELETE .../sessions/{sessionId}`.
Session ids are integers. The read is gated on managing the solution, like every other read
that spends the customer's credentials; the write carries the four things every tenant write
carries — the same gate, a confirm that names the environment and says when it is a
production one, a line in the environment's Workbench history (`UpgradeActionKind.CancelSession`,
a text column, no migration), and a `BcApiException` that reaches the page as a sentence.
Microsoft documents no error codes of its own for the DELETE, so the one worth telling apart
is the status: a **404 is a session that ended between the list and the click**, which is the
likeliest failure of all.

**Nothing about a session is stored.** A user id and what that person is doing in their
employer's system is personal data with no reason to outlive the screen it is on, so it is
read live, shown, and forgotten — no cache, no mirror column, no table. Leaving the tab
drops the list. The one thing that lasts is the history line, and it keeps to the same
rule: *"Ended session 47 on Production."* - the session's number and where, with who asked
for it beside it as on every history line, and nothing about whose session it was or what
it was running (maintainer's decision, 2026-09-21; the first version named both). The
service still re-reads the live list before it deletes, because that turns "already gone"
into a sentence rather than a wire 404.

**This tab is live where Operations is not.** Both are read live rather than cached, but an
operations list that is two minutes old is still *true* — the entries in it happened. A
sessions list that is two minutes old is *wrong*: the person it names may have signed out,
and the one holding the lock may have signed in since. So while the tab is open it keeps
itself current, and the page says so rather than leaving rows to move unexplained.

- **It reads the moment it is opened**, by a click or by landing on `/environments/{id}/sessions`
  directly. There is no first-run state with a button on it: a Sessions tab waiting to be
  told to read is a tab showing a wrong answer. Coming back to it later in the same visit
  re-reads rather than restoring what was there.
- **Every 30 seconds, for 10 minutes.** Thirty seconds is short enough that the list matches
  what the customer is describing and long enough that nobody watches rows flicker. Ten
  minutes is the length of the phone call it was built for; past that, a browser tab
  somebody forgot must not read a customer's tenant all night. One request every thirty
  seconds against one tenant is a fine guest (see "The sweep is a guest on somebody else's
  API"); an unbounded one is not. When it stops it says so in the card, and **Refresh**
  starts it again.
- **The card has its own Refresh**, beside a line saying how old the list is
  (`RelativeTime`), because the freshness of *this* list is part of the answer. The page's
  contextual Refresh in the freshness strip works on this tab too; the card's is the obvious
  one.
- **A tick never overlaps anything.** It is skipped while a read or a write is in flight and
  while the confirm dialog is open — the row somebody is about to end must not move or vanish
  between reading it and pressing the button. It runs through `InvokeAsync`, on the
  renderer's synchronisation context, so a tick cannot collide with a click on the circuit's
  one `AppDbContext`; that is the mechanism the Upgrades page already polls with and
  deliberately not a second one. It stops on leaving the tab and on dispose.
- **A failed automatic re-read keeps the list**, with a quiet line saying it could not be
  updated. Replacing a list somebody is reading out to a customer with an error card is the
  worse answer. A failed *first* read is still the unreadable state.

**"Long-running" is ours, and it is a cue rather than a verdict.** Business Central marks
nothing, so the row the caller is looking for has to be made findable here: the list is
ordered by `currentOperationDuration` descending (then by who has been signed in longest),
and a session that has been in the same operation for **five minutes** is marked. A person
clicking through the web client finishes an operation in well under a second, so a minute
already means work rather than somebody thinking; five is where a consultant would start
looking, and it is a round number to hold in the head. It marks a row and orders the list —
it never hides one and never decides anything, because a job queue task legitimately runs
for hours. The rule is written under the table so nobody has to guess what the colour means.

**One verb, everywhere.** The row button, the question and the answer all say *end*, never
*cancel*: a row labelled "Cancel session" whose dialog then says "End the session" makes
somebody stop and wonder whether they are the same act, and the dialog cannot say "cancel"
because its other button already does. Business Central's own admin centre says cancel; the
confirm's sentence carries the meaning either way.

**Two things about the payload are worth knowing.** Microsoft types `currentOperationDuration`
as a `long` and names no unit, so the parser reads a number as milliseconds and a string as a
time span, and that is the one field here not checked against a live tenant. And the API
marks nothing as belonging to an app registration or to the system, so **no row is hidden
from Cancel** — guessing which sessions are "ours" would be inventing a rule Microsoft has
not written. Our own admin-centre calls are not Business Central sessions and never appear.
Client types are worded in `BcSessionDisplay`, which gives every value Microsoft has today a
phrase a consultant would say out loud ("Web client", "Web service (OData)", "Job queue") and
spaces out one they add tomorrow rather than showing the wire token — the treatment
`BcEnvironmentOperationDisplay` gives an operation.

**Deliberately not built.** No history of who was signed in (that is the personal data the
tab exists not to keep), no ending several sessions at once, and no telemetry.

## The Environments list, against its designed sheet

`/environments` is the read-only view of the same fleet rows, designed as archetype 2a in
`.design/handoff/PageEnvironmentsList.dc.html`. It follows the sheet: a glyph-only state cell
with the word on `aria-label` / `title`, no status column, "Now on", a semibold next version
over its date, skeleton rows under the real header while loading, and the count in `.pager`.
With nothing scheduled, the line under Next update names any state that is not plainly
running, as the sheet does - that keeps the word on screen, since four states share two
glyphs and a title does not exist on touch.

Where it still differs, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Export the list" and a primary "Refresh from Business Central" in the page head | Refresh in the freshness strip only | There is no export. Refresh sits beside the age it fixes, and a second copy in the head would be the same button twice. Recorded upstream in the design project's `briefs/2026-09-port-corrections.md`, with the freshness copy, the "Solution" column name and the unread-row glyph. |
| A row menu: Open environment, Open in Business Central, Refresh this environment | The first two, plus Upload an app and Copy, or - on a deleted environment - Recover | A refresh is per solution, not per environment, and the freshness strip already does it. The other three are writes the sheet does not draw; see "Deleted environments" and "Copying an environment". Copy is the only one shown to managers alone, for the reason given there. **Needs a design pass upstream.** |
| No row selection | A tick box per row (none on a deleted environment), a header box for every row shown, and the list's bulk bar with **Set delivery window...** once something is ticked | Setting the same window on thirty customers is otherwise thirty page visits (#961). The ticks follow the Upgrades sheet. A tab, filter or search that takes a ticked row off the screen leaves it ticked (#985), as on the Upgrades page - see "The page"; only a re-read drops the tick of an environment that has gone or been deleted. The header box ticks and unticks the rows shown and nothing else. The dialog lists every ticked environment by name, shown or not, before it writes, and is described in `saas-delivery.md`, "Update window". The solution cell also carries the short name, as the Solutions list does, and the search and the solution filter match it (#966). **No sheet draws the dialog; it needs a design pass upstream.** |
| No counter | "8 selected, 5 shown" in the bulk bar, then **Show selected** and **Clear selection** | Business Central's own lists show no counter, which is why the sheet has none; but a selection that can be off screen needs one, and a way to see it. Show selected replaces the tab, search and filters while it is on, and changing any of them ends it (#985). **Needs a design pass upstream.** |
| Three views | A fourth, **Deleted**, when there is one | The sheet has no notion of an environment that is deleted but recoverable. See "Deleted environments" above. |
| Sortable Customer and Next update headers | Fixed order | Not built. Follow-up. |
| Previous / Next | Count only | The whole set is rendered; buttons that can never be enabled are noise. |
| No auto-refresh | A **Refresh every 5 minutes** switch in the freshness strip, off by default | #983: an ops engineer watching the list through a release week. It stops itself after two hours, and a server-side freshness gate keeps several open pages from multiplying the calls; see "Freshness" above. **No sheet draws the switch; it needs a design pass upstream.** |

## The Upgrades page, against its designed sheet

`/upgrades` is archetype 15, the actionable list, in `.design/handoff/PageUpgrades.dc.html`. It
follows the sheet: crumbs, the time-zone rule as a clause of the subtitle, one `.cmdbar` whose
selection commands are plainly disabled until rows are ticked (no instruction, as in Business
Central's own lists; the counter is a departure, below), `.check` boxes in a `data-table__col-check` column with
`is-indeterminate` on the header and `is-selected` on the row, the same glyph-only state cell
and state wording as the Environments list (`FleetRowState` serves both), `.cell-stack` cells,
"Now on" and "Latest possible date", bare dates, and the row's commands in one `.ra` menu with
Update history first.

Where it still differs, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Customer" | "Solution" | The house name for the record; see CLAUDE.md. |
| "Update now" | "Start update..." (and "Start this update..." in the row menu) | The dialog behind it also books a later slot, which "now" hides. Maintainer's decision, 2026-09-19; to be recorded upstream in `briefs/2026-09-port-corrections.md`. |
| A fixed view list: Production / Sandbox, each with "update waiting" | The same list built from the environment types actually present | Business Central reports the type as text, and a fleet with no sandboxes should not offer one. |
| An overflow menu: delivery window, two exports, fleet-wide history, cancel the scheduled update | No overflow menu | None of the five exists yet. A booking is cancelled from its own marker or from the history. An empty kebab is worse than none; add it with the first entry. |
| Every row has a checkbox | A padlock instead, on rows of a team the viewer is not on, with a legend under the table | The sheet has no notion of a row you may see but not change. Their row menu holds history only. |
| Nothing under the next update's date | The out-of-window warning, a booking marker with its Cancel, and the live per-row result of a run | Behaviour the sheet does not draw. They sit under the date because a booked slot and Business Central's date are two answers to one question. |
| Sortable Customer and Next update headers; Previous / Next | Fixed order; count only | As on the Environments list. |
| The table directly in the page | The table in a box that scrolls sideways, with the checkbox, state and Solution columns pinned | The page container clips rather than scrolls (#574), and nine columns do not fit a narrow window. |
| The view select first in the bar, then the search | The search first, then the view select, then the commands | The order Solutions and Environments use, with the same search width and F3. Maintainer's decision, 2026-09-24 (#966). |
| Two fleet commands | A third, **Change the next version...**, beside them in the bar and in the row menu | #960. No sheet: it reuses the bar's disabled-until-ticked button and the other dialogs' preview (grouped rows under `upg-preview__head` headings, the count line, the typed word). **Needs a design pass upstream.** |
| Nothing while an update runs | "Updating... started 6 minutes ago" under the next update while the page watches it, then "Updated to 29.2", or "Update failed" with a link to the environment's Operations tab | #982: the team sits on the page to see a started update finish. No sheet draws a live row; it reuses the row-result line (`upg-note`) and its spinner. **Needs a design pass upstream.** |
| The solution name alone | The name, then the short name in the Solutions list's quieter tone | #966: customers are called by their abbreviation in daily speech; the search matches it too. |
| No counter in the bar | Once a row is ticked, "8 selected, 5 shown", **Show selected** and **Clear selection** between the view select and the commands | The search and the view no longer untick rows (#985), so part of a selection can be off screen, and a bar that stayed silent about it would let a command act on customers nobody can see. Business Central's lists have no counter because their selection cannot hide; this one can. Described under "The page". **Needs a design pass upstream.** |

Row menus anywhere in the app now open upwards when there is no room under them
(`row-actions-menu.js` sets the sheet's `.ra--up`), which this table needed for its last rows.

## The environment's own page, against its designed sheet

`/environments/{id}` is `.design/handoff/PageEnvironmentDetail.dc.html` on the `DetailPage`
frame (#809). Named user: an ops engineer with a fleet of customer environments, who would
otherwise open each customer's admin centre. It follows the sheet top to bottom: crumbs
through the solution, a `detail-head` with the state pill and the type as a `.tag`, the
freshness strip, the six-item `.meta-row`, the Updates card's `.kv-grid` with the info alert
when the two windows overlap, then Apps (Scheduled installs, Installed apps, AppSource
updates waiting - ready ones first, "Waits for N" with the prerequisites as `.tag`s) and
Environment settings as a `.setting-list` ending in the `setting--danger` row.

**Two readings share the page.** The head, the meta row and the Updates card come from our
own mirror, reached through `UpgradeFleetService.GetEnvironmentAsync` - the same
visible-projects join as the fleet, so an id from a solution the viewer cannot see answers
exactly like an id that does not exist. Apps and Environment settings are the existing
cached panel read (`ProjectConnectionService.GetEnvironmentPanelAsync`; no second fetch
path), and Operations and Sessions are their own live reads.

**Both readings follow the solution's visibility, not the manage axis.** The page is
gated on what `.design/teams-and-visibility.md` calls view: everyone in the organisation on
a Public or Read-only solution, its assigned teams on a Private one. Reading what is
installed, what Business Central has been doing, and who is signed in is reading, and a
consultant on a Public solution needs those answers as much as its owner does - the
solution being open is precisely the statement that they may have them. This *replaces* the
manage gate these reads shipped with, which had the effect nobody intended: a solution
deliberately made open to the whole organisation still showed four of its five tabs only to
its owner and the org admins, because a Public solution has no teams by construction. The
service-side gate is `EnvironmentGate.View`, and `ProjectConnectionServiceTests` pins both
halves - a colleague reads a Public solution's environment, somebody outside a Private
one's teams reads none of it.

**What acts still needs managing the solution.** Every setting, an app update, an upload, a
copy, a recovery, cancelling a scheduled install, ending a session, and a forced Refresh of
the cached panel: all of them stay with the owner, an org admin, or an assigned team. That
covers the ops team either way, since the environment-updates grant is only ever held
through an assigned team. The page draws this as absence rather than as locked controls - a
colleague sees the settings' values without their selects, app tables without their action
column, and the session list without its End session buttons. The two tabs that are read
live on every visit (Operations and Sessions) keep their Refresh for everyone, because it
asks for nothing they are not already being given; the panel's Refresh, which makes the
customer's tenant answer again, does not.

History (its own tab) sits outside both, because it is our record and must survive a tenant
that will not answer.

The inline "Environment details" panel on the solution's Business Central tab is retired;
its button goes here. The tab keeps the connection and the delivery window, and
`/solutions/{id}?tab=bc` opens on it so this page can send people there.

Where it differs from the sheet, and why:

| The sheet has | We have | Why |
| --- | --- | --- |
| "Nothing on this page is stored by the workbench." | "Apps and settings read from Business Central {age}. Version and update dates last checked {age}." | The sheet's sentence is not true for us: the environment row and its next update are mirrored, and the app lists are cached for fifteen minutes. The strip says which half is which. |
| An overflow menu: Copy environment ID, Open the admin centre, Export the app list, Remove from this solution | "Open the admin centre" as a second button; no menu | We mirror no Business Central environment id, there is no export, and environments are mirrored from Business Central rather than attached by hand, so nothing can be removed. One entry is not a menu. |
| Refresh for everyone | Refresh for everyone on Operations and Sessions, for people who manage the solution on Overview and Apps | Those two tabs are read live on every visit anyway, so their Refresh gives a reader nothing new; the other two are cached for everybody, and making the customer's tenant answer again is a manager's call. |
| A region in the subtitle and a country in the meta row | Both, when Business Central reported them | `location_name` and `country_code` are mirrored; an environment read before they were captured shows a dash. |
| Overlap alert naming the overlapping hours | The alert without the hours, linking to where the delivery window is set | The two windows can be in different time zones and the overlap moves with daylight saving; `BcUpdateWindow.Overlaps` answers yes or no. |
| "Reschedule the update" as a button in the Updates card | "Change the next version", with a down arrow, linking to the "Next Business Central update" setting further down the Overview tab | One control writes the version, and it carries the warning and the lock line; a second one in the card would skip both. The label says it goes down the page, because a button that scrolls reads as a button that did nothing. |
| "Blocked" in the filter, "Waits for N" on the pill | "Waiting" and "Waits for N" | One word for one state on one card. |
| "Scheduled for" in UTC | The customer's local time first, UTC second | The two windows beside it are local, and whether they collide is what the card is for. |
| An empty Scheduled installs card that only warns about Extension Management | It says what puts a row there and links to Releases; the warning moves under the populated table | The first-run state has to name the next step. |
| Next-update options as version and date pairs | Versions, with the one already queued marked | Business Central offers versions; the date comes from the customer's update window once a version is chosen. Moving the date is what Upgrades is for. |
| A sortable App header on the waiting updates | Fixed order, ready first | The order is the point of the table. |
| Cadence saves from the select; Microsoft 365 is a switch | The same, each behind a confirm | They write to the customer's tenant. Declining puts the control back. |
| Nothing after Environment settings | Update history | Who moved this environment's dates and what is still booked; the same feed the Upgrades page shows. |
| A read-only list of waiting AppSource updates | An **Update** button on the rows that are ready | What #809's report asked for, and the maintainer's decision on #841 to build it without a sheet. Ready rows only: a waiting row names its prerequisites instead, which is the next step. The confirm names the app, both versions, the environment and whether it is production, and asks when - the environment's next update window by default, or now. `ProjectConnectionService.UpdateAppAsync` re-reads the waiting list before it writes and refuses an app that is not on it, a version Business Central is not offering, or an app that still waits for another; dependencies are never pulled along. Manage-gated (it writes to the customer's tenant, though the list it acts on is read by anyone who can see the solution); logged, not audited, as it touches no row of ours. **Not yet tried against a live tenant** - the request shape is from Microsoft's documentation of `POST .../apps/{appId}/update`. Needs a design pass upstream. |
| The result of a write beside its control | One result line under the head | The writes are spread down a long page and each re-reads everything; the top is where the eye is afterwards. |
| Nothing about copying the environment | **Copy this environment...** as a third outline button in the head, and **Copy...** in the Environments list's row menu; both open the same dialog | The sheet draws a page that only reads and adjusts. Copying is the errand this page's reader would otherwise open the admin centre for, and it is the one write here that adds an environment to the customer's tenant; see "Copying an environment" above. Not shown on a deleted environment, which has nothing to copy. **Not yet tried against a live tenant** - the request shape is from Microsoft's documentation of `POST .../copy`. **There is no sheet for this; it needs a design pass upstream.** |
| Nothing about a deleted environment | A danger alert above the meta row, with **Recover this environment** | The sheet draws a live environment. A deleted one changes what every number under it means, and it has a deadline; see "Deleted environments" above. |
| One long page: Updates, Apps, Environment settings | Five tabs under the meta row - **Overview** (the Updates card, both windows, the three settings), **Apps** (scheduled installs, installed apps, AppSource updates waiting, Upload an app), **Operations**, **Sessions**, **Workbench history** | The page had grown past what the sheet drew (uploads, app updates, the delivery window, history) and the thing looked for was a long scroll away. The head, the freshness strip, the result line and the meta row stay above the tabs because they are true on every one. The tabs are real links (`/environments/{id}/apps`), so one can be bookmarked and Back works; the page reads the environment once per id, and a change of tab reads only what that tab shows - Overview and Apps share the one cached panel, Operations has its own read, History asks Business Central nothing. Refresh re-reads the open tab - and Sessions, the one live tab, keeps itself current besides; see "Sessions" above. Maintainer's decision, 2026-09-20; needs a design pass upstream. |
| No operations list | **Operations**: Business Central's own record for the environment (`GET .../environments/{name}/operations`) - app installs, updates and uninstalls, platform updates, restarts, renames, setting changes - newest first, each as a sentence with a status, who started it, when (the solution's time zone) and how long it took; a failure carries Business Central's message | Workbench history is what *we* did from here (named so at the tab strip, where the choice between the two is made); an update started in the admin centre, or one Microsoft ran overnight, is only in Business Central's record. Two tabs rather than one merged timeline until there is real data to judge a merge by. Read live on every visit, never cached: the point is to watch something finish. Read-only, and gated on seeing the solution like the panel. Operation types and statuses are worded in `BcEnvironmentOperationDisplay`; one Microsoft adds later is spaced out into words rather than shown as the wire token. **Not yet tried against a live tenant** - the shape is from Microsoft's documentation. |
| No notion of who is signed in | **Sessions**: who has a session open on the environment right now (`GET .../environments/{name}/sessions`) - who, how they got in, since when, what they are running and for how long - with **End session** per row behind a confirm | The errand this page's reader would otherwise open the admin centre for while a customer is on the phone with everything locked. The one live tab: it reads on arrival, re-reads every 30 seconds for 10 minutes, and says so. Nothing about a session is stored. Read by anyone who can see the solution; ending one is manage-gated, and the history line names whose session it was and what it was running. **Not yet tried against a live tenant** - the shape is from Microsoft's documentation of the session endpoints, and `currentOperationDuration` is documented as a bare `long` with no unit. See "Sessions" above. **There is no sheet for this; it needs a design pass upstream.** |
| Scheduled installs drawn only as an empty state | A table with a Cancel install action when there are any | The write exists and a booked install has to be reachable from somewhere. |

## Deliberately out of scope

- **No MCP tools for the fleet actions.** These writes land on customers' production tenants
  behind a typed confirmation; that is not a surface to hand an agent. The environment
  *reads* of the mirror are the exception, since #912: `list_environments`,
  `get_environment`, `list_environment_history` and `list_upgrades` read what the sweep
  and Refresh stored, each fact with the time it was read; `list_planned_upgrades` and
  `get_upgrade` (#984) read the planned upgrades the same way, and nothing on the agent
  surface creates, fills, starts or checks one. Sessions and Business
  Central's own operations log stay web-only - they are live reads made with the
  customer's credentials. See `.design/saas-delivery.md`, "MCP parity".
- **A per-batch table after all, for a different reason (#984).** This bullet used to say there
  was none: the action rows plus the on-page results were the whole record of a sweep, to be
  revisited only if losing a batch to a disconnect mid-run turned out to bite. It never did.
  What changed was the other half of the job: the batch is the unit the team *plans and checks*
  by, not only the unit that runs, and a hundred flat rows gave the morning after nothing to
  hold on to. So there is now a header-and-lines pair (see "Planned upgrades: a header with
  lines"). It is still not a job table: nothing queues on it and nothing fires from it. The
  actions stay one row each in `oe_environment_upgrade_actions`, now carrying the upgrade they
  came from, and a run over an upgrade's lines is still the page's own loop with per-row
  results.
- **No "move the date back" cancel.** Cancel stops one of *our* pending actions before it is
  sent. Once Business Central has the new date, changing it again is another action, not an undo —
  and once an update has actually started, Microsoft owns it.
- **No change to the delivery flow.** Publishing builds to an environment is a separate tool with
  a separate schedule; the two-windows separation — our delivery slot versus Microsoft's update
  window — stands exactly as [`saas-delivery.md`](./saas-delivery.md) sets it out, and the update
  window this tool can override is Microsoft's.
- **Claims and the cookie pipeline are untouched**, by the grant's design.
