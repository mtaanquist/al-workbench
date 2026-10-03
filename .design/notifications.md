# Notifications

Notifications tell people something happened to work they started or look after: a build
pipeline broke, a deployment waits for approval. They are listed in the app and emailed.
Phase 2 of the email work (milestone "E-mail notifications") added the emails, phase 3 the
in-app list; the message design is `email.md`.

## Decisions

Agreed with the maintainer on 2026-10-03:

- **Recipients** are the person who triggered or owns the thing. Phase 4 adds people who
  follow a solution. No organisation-wide broadcasts.
- **State changes only.** A pipeline that fails every night sends one email when it starts
  failing and one when it works again, not one per night.
- **Each person chooses per category**, like GitHub: whether it shows in the app (on or
  off), and when it is emailed (Immediately, Daily digest, Weekly digest or Off). A category
  with no stored choice is in the app and Immediately, so a category added later starts on
  for everyone without a backfill.
- **Customer-facing email is out of scope.** These emails go to colleagues only.

## Categories

| Category | Events | Issue |
| --- | --- | --- |
| Builds | Build failed, build working again (manual builds and nightly checks; pull request builds are left to GitHub) | #1035 |
| Deployments | Waiting for approval, deployed, failed | #1036 |

Phase 4 adds Upgrades and Solutions.

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
- **Access is not re-checked.** Someone who started a build or created the pipeline keeps
  getting its emails after losing access to a private solution. Accepted for now; the
  recipients are people who set the build up.
- The email shows the failed extensions (or the build's own message when it failed as a
  whole) and links to the build on the pipeline page; the digest entry carries the first
  line.

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

As with builds, access is not re-checked: a pipeline creator who has lost access to a
private solution still gets its emails. Accepted for now.

Each deployment is its own event, so there is no "only on change" rule here.

## Following a solution (#1048)

A solution's own events (Business Central update dates, #1049) go to its followers. The
owner and the people on the solution's People list follow by default, without a stored
row, so someone added to the list later starts following with nothing to backfill. Anyone
who can see a solution can follow it from the Follow button in its header, and anyone can
stop; stopping stores a row that overrides the default. A follower who can no longer see a
Private solution (taken off its team) is left out of its notifications but keeps the
choice, so it applies again if access comes back. Disabled accounts are left out.

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
