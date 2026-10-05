# Notifications

Notifications tell people something happened to work they started or look after: a build
pipeline broke, a deployment waits for approval. They are listed in the app and emailed.
Phase 2 of the email work (milestone "E-mail notifications") added the emails, phase 3 the
in-app list, phase 4 the ready-to-check notice, following a solution and Business Central
update dates; the message design is `email.md`.

## Decisions

Agreed with the maintainer on 2026-10-03:

- **Recipients** are the person who triggered or owns the thing, and for a solution's own
  events the people who follow it. No organisation-wide broadcasts.
- **State changes only.** A pipeline that fails every night sends one email when it starts
  failing and one when it works again, not one per night.
- **Each person chooses per category**, like GitHub: whether it shows in the app (on or
  off), and when it is emailed (Immediately, Daily digest, Weekly digest or Off). A category
  with no stored choice is in the app and Immediately, so a category added later starts on
  for everyone without a backfill. Solutions is the exception: its email starts Off. People
  follow a solution without asking (the owner and its People list), so its email is opt-in
  to avoid suddenly mailing a whole team (Mads, 2026-10-03); it still shows in the app.
- **Customer-facing email is out of scope.** These emails go to colleagues only.

## Categories

| Category | Events | Issue |
| --- | --- | --- |
| Builds | Build failed, build working again (manual builds and nightly checks; pull request builds are left to GitHub) | #1035 |
| Deployments | Waiting for approval, deployed, failed | #1036 |
| Upgrades | A change someone scheduled on an environment ran, failed or could not be confirmed; an environment someone is to check on a planned upgrade reached the target version (ready to check) | #1046, #1047 |
| Solutions | Update scheduled, update moved, latest date a week away, for solutions you follow | #1049 |

## How it works

- `NotificationPreferenceService` stores the choices (`user_notification_settings`, one row
  per person and category). The account page's Notifications section edits them; each
  choice saves on its own.
- A caller (a worker, usually) builds a `Notification`: its category, the recipient user
  ids, a one-line summary (title, detail, the path of the page it is about, solution), and
  a function that renders the email for one recipient.
  `NotificationService.NotifyAsync` then, per active recipient in the current
  organisation:
  - In app on: stores the summary in `user_notifications` (#1042), whatever the email
    choice;
  - Email Immediately: renders the email and puts it on the outbox, labelled with the
    category's email purpose so the SiteAdmin Delivery tab names it;
  - Email Daily or Weekly: stores the summary, with an absolute link, in `notification_digest_items` for the
    digest sender (#1037);
  - Email Off: no email.
- An urgent notification (`Urgent = true`: a deployment waiting for approval) treats a
  digest choice as Immediately, because someone is waiting on the recipient. Only Off stops
  the email, and the account page says so.
- It never throws for a failed send: the notification is a side effect of work that
  already succeeded.
- No email is sent or kept for a digest when `PUBLIC_BASE_URL` is unset. A background
  sender has no request host to fall back on, and an email whose links point nowhere is
  worse than none. The startup warning says so. The in-app list still fills: it stores
  paths, not addresses.
- In-app rows and digest items are saved through a context of their own, so the calling
  worker's pending changes are never saved with them. The two save separately, so a
  problem with one kind does not lose the other.
- In-app notifications are deleted after 30 days, by the digest scheduler's run for each
  organisation (it already prunes digest items on the same window).
- Every notification email passes `SettingsUrl` to `EmailLayout`, which puts a "Change
  which emails you get" link in the footer, pointing at `/account?section=notifications`.
- Every notification email has an `EmailPreviews` entry like any other email.

## In the app (#1042, #1043)

- Every notification is stored per recipient in `user_notifications` unless they turned
  In app off for its category. Rows hold a path within the app, not an address.
- A bell in the top bar links to `/notifications` and shows the unread count (99+ above
  99). The shell is static, so the count is the one at page load; there is no live push,
  which would need a held connection on every page.
- `/notifications` lists the person's own notifications newest first (at most 200; they
  are pruned after 30 days anyway), unread ones in bold with a dot. Opening one goes
  through `/notifications/{id}/open`, which marks it read and redirects to its page;
  "Mark all as read" posts to `/notifications/read-all`. Both redirect, so the count is
  current on the next page.
- A notification also counts as opened when the person is on the page it is about, most
  often having followed the link in its email: the bell, which renders on every page,
  marks their unread ones whose stored path matches the page's path and query exactly.
  It only writes when something is unread. Requests (below) are left alone: several share
  a page, and looking at it is not doing it.
- A notification that asks for something (a deployment waiting for approval, an
  environment ready to check) carries a `subject` naming it (`delivery:12`,
  `upgrade-line:34`). Once that is done, by anyone and by any route, every recipient's
  copy is marked read: approving or dismissing the deployment, a newer build replacing
  it, or its deployment pipeline being deleted; ticking the environment's check, assigning
  it to someone else (the new checker is told afresh), taking it off the upgrade, or
  marking the upgrade done or deleting it. Unticking a check, or reopening the upgrade,
  does not bring the notice back. Digest items carry the same subject and are deleted
  then, so a digest sent later does not ask for something already done.
- `InAppNotificationService` names the signed-in user in every query on top of the
  organisation filter, and reads through the context factory because the count renders
  in the layout beside the page's own queries.

## Builds (#1035)

`BuildNotifier.BuildFinishedAsync` runs from the release import worker once a pipeline
build is marked ready or failed.

- **What counts as a change.** The build is compared with the build of the same pipeline,
  trigger (manual or nightly check) and Business Central target that finished last before
  it (by finish time, since builds can finish out of order after a restart). A failure
  after a success, or as the first build, is news; a success is news only after a failure.
  Keeping the triggers and targets apart means a nightly next-major failure does not hide
  behind a manual current-version success, and the other way round.
- **What counts as failed.** A failed build, and also a ready build against an upcoming
  version in which any extension failed: the pipeline page and the dashboard call that a
  failed check, so the email does too. A ready build against the current version counts as
  working, as it does on the pipeline page.
- **Skipped:** pull request builds (GitHub shows the result on the pull request) and builds
  outside a pipeline (GitHub release imports).
- **Recipients:** the person the build ran as (whoever pressed Build, or whoever had the
  nightly check on when it was queued) and the pipeline's creator.
- **Access is re-checked** on every notification (see "Who can see it" below).
- The email shows the failed extensions (or the build's own message when it failed as a
  whole) and links to the build on the pipeline page; the digest entry carries the first
  line.

## Who can see it

Every notification names the solution it is about, and `NotificationService` leaves out any
recipient who can no longer see that solution, with the same rule as the solution page: the
owner, an org Admin, a SiteAdmin or a member of one of its teams for a Private solution,
everyone otherwise. So someone taken off a Private solution's team stops hearing about its
builds, deployments, environment changes and upgrades, and nothing new is listed on their
Notifications page either. The check runs when an event is sent and again later: in-app
notifications and digest items store the solution, so the Notifications page and the header
count leave out the ones about a solution the person can no longer see, and the digest drops
them before it is sent (Mads, 2026-10-04). Rows written before the solution was stored carry
none and stay until they expire. It also means a change they booked that
fails because they lost access is not announced to them. Their notification settings are
untouched, so it applies again if access comes back.

## Deployments (#1036)

`DeploymentNotifier` is called from three places:

- After a build prepared deployments waiting for approval (release import worker), with the
  ids it prepared. Goes to the solution's owner and the deployment pipeline's creator,
  marked urgent, so a digest choice still gets it straight away (Off still stops it).
- After a run the delivery worker claimed ends deployed, accepted by Business Central for a
  scheduled install, or failed. Goes to the person it ran as (who started it, or who
  approved a prepared one), or the pipeline's creator when nobody did. A run that found the
  deployment already claimed sends nothing, so nothing is announced twice.
- After the delivery scheduler fails deployments a restart cut off, for each one: the
  person behind it needs to hear about that failure as much as any other.

Each deployment is its own event, so there is no "only on change" rule here.

## Upgrades (#1046)

`UpgradeActionNotifier` tells the person who scheduled a change on an environment (an app
install or update, a new update date, a version choice, starting the update) how it went,
once the upgrade action worker has settled it. It is called wherever the worker writes a
final state: after a run, for the apps in a batch skipped because an earlier one failed,
after the second look at an install nobody saw finish, and for the changes a restart cut
off. An install still waiting for that second look sends nothing yet, so each change is
announced once. An install Business Central took but nobody saw finish is announced as
"Not confirmed: ..." rather than "Done": the row records it as sent, so the worker, which knows,
tells the notifier. Changes made on the spot are not announced; the person saw the result
on the page. Each app in a batch is its own notification. Failures are not urgent, like
deployments. The link goes to the environment's history tab. A booking someone else
cancels is not announced to the person who made it; considered and left out for now.

## Ready to check (#1047)

On a planned upgrade, the person assigned to check an environment (or whoever planned the
upgrade when nobody is) hears in the Upgrades category once the environment is on the
target version, with a link to the planned upgrade. The upgrade action worker looks every
sweep, in each organisation, for unchecked lines of open upgrades whose environment's
mirrored version has reached the target, by the same Major.Minor rule the page uses for
"Updated". Each line carries `updated_notified_at`, claimed before sending, so it is told
once whichever refresh noticed the new version. A line added after its environment was
already updated is told on the next sweep, which is still news to its checker.

A line is not told while the environment is still busy (the page shows Running), nor when
its solution is in the bin or Business Central deleted the environment. Assigning someone
new, or changing the upgrade's target, clears the stamp so the new checker or the new
target is announced; reopening a closed upgrade stamps lines already on target, and the
migration that added the column stamped those already on target then, so neither sends
old news. A crash between the claim and the send loses that one notice: at most once is
the better failure for an advisory email than a duplicate.

## Following a solution (#1048)

A solution's own events (Business Central update dates, #1049) go to its followers. The
owner and the people on the solution's People list follow by default, without a stored
row, so someone added to the list later starts following with nothing to backfill. Anyone
who can see a solution can follow it from the Follow button in its header, and anyone can
stop; stopping stores a row that overrides the default. A follower who can no longer see a
Private solution (taken off its team) is left out of its notifications but keeps the
choice, so it applies again if access comes back. Disabled accounts are left out.

## Business Central update dates (#1049)

When a refresh (the scheduled environment refresh, Refresh or Test connection on the
solution, the refresh after recovering or copying an environment, or the environment page
re-reading an update it watches) finds an environment's next update
changed, the solution's followers hear about it in the Solutions category: a newly
scheduled update (a version not seen before, or a date where there was none), a moved date,
or the latest date the update can be postponed to coming within a week. At most one per
read, in that order; when a new or moved date and the latest date coming close land on the
same read, the latest-date notice is not sent separately, but the email names the latest
date anyway. A move counts only when the day changes in the environment's update-window time
zone, since the notice names days. Nothing is said while an update is under way or once its
date has come. Nothing is said on an environment's first read, which has nothing to
compare with, and nothing about a date the app moved itself: the write path stores the new
date straight away, so the next read finds nothing new. Two known gaps, accepted: when the
re-read after the app's own change fails, the next refresh announces that change as news;
and two refreshes of the same environment overlapping can both announce one change. The latest-date notice needs no
stored flag: it fires on the read where the date first comes within the week, which the
previous read's own time says. Dates are shown in the environment's update-window time
zone when Business Central reports one, otherwise in UTC, said so.

## Digests (#1037)

`NotificationDigestScheduler` (a `PolledScheduler`, `DISABLE_NOTIFICATION_DIGEST_SCHEDULER`)
polls every 15 minutes and runs `NotificationDigestService.SendDueAsync` per organisation.

- **Cut-offs:** daily at 06:00 UTC, weekly on Monday at 06:00 UTC. A run sends every kept
  item created before the latest cut-off for its digest: one email per person and digest,
  grouped by category, oldest first.
- **No state.** A digest's items are deleted in the same transaction that queues it
  (delete, queue, commit), so any run after a cut-off sends what is due and nothing twice;
  only a commit failing after the queue succeeded could repeat one. A restart or a day down
  only delays a digest. Runs wait five minutes past a cut-off, so an item saved across it
  does not get a digest of its own.
- **Current choices win.** Items for a category the person has since turned Off, or for a
  person no longer active, are dropped unsent. A digest that fails to queue keeps its items
  for the next run.
- **Nothing to send with.** Without `PUBLIC_BASE_URL` or email set up, items wait; any item
  older than 30 days is dropped on every run, sent or not. With the scheduler disabled
  nothing runs, so items wait until it is turned back on.
- **It is also the cleanup.** The same run prunes in-app notifications older than 30 days,
  so `DISABLE_NOTIFICATION_DIGEST_SCHEDULER` stops that too: with it set, neither digest
  items nor in-app notifications are ever deleted, and the Notifications page keeps
  showing its newest 200 whatever their age.
