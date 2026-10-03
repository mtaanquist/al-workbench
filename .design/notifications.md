# Notifications

Emails that tell people something happened to work they started or look after: a build
pipeline broke, a deployment waits for approval. Phase 2 of the email work (milestone
"E-mail notifications"); the message design is `email.md`.

## Decisions

Agreed with the maintainer on 2026-10-03:

- **Recipients** are the person who triggered or owns the thing. Phase 3 adds people who
  follow a solution. No organisation-wide broadcasts.
- **State changes only.** A pipeline that fails every night sends one email when it starts
  failing and one when it works again, not one per night.
- **Each person chooses per category** when they hear about it: Immediately, Daily digest,
  Weekly digest or Off. A category with no stored choice is Immediately, so a category
  added later starts on for everyone without a backfill.
- **Customer-facing email is out of scope.** These emails go to colleagues only.

## Categories

| Category | Events | Issue |
| --- | --- | --- |
| Builds | Build failed, build working again (manual builds and nightly checks; pull request builds are left to GitHub) | #1035 |
| Deployments | Waiting for approval, deployed, failed | #1036 |

Phase 3 adds Upgrades and Solutions.

## How it works

- `NotificationPreferenceService` stores the choices (`user_notification_settings`, one row
  per person and category). The account page's Notifications section edits them; each
  choice saves on its own.
- A caller (a worker, usually) builds a `Notification`: its category, the recipient user
  ids, a one-line digest entry, and a function that renders the email for one recipient.
  `NotificationService.NotifyAsync` then, per active recipient in the current
  organisation:
  - Immediately: renders the email and puts it on the outbox, labelled with the
    category's email purpose so the SiteAdmin Delivery tab names it;
  - Daily or Weekly: stores the digest entry in `notification_digest_items` for the
    digest sender (#1037);
  - Off: nothing.
- An urgent notification (`Urgent = true`: a deployment waiting for approval) treats a
  digest choice as Immediately, because someone is waiting on the recipient. Only Off stops
  it, and the account page says so.
- It never throws for a failed send: the notification is a side effect of work that
  already succeeded.
- Nothing is sent when `PUBLIC_BASE_URL` is unset. A background sender has no request
  host to fall back on, and a notification whose links point nowhere is worse than none.
  The startup warning says so.
- Every notification email passes `SettingsUrl` to `EmailLayout`, which puts a "Change
  which emails you get" link in the footer, pointing at `/account?section=notifications`.
- Every notification email has an `EmailPreviews` entry like any other email.

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

`DeploymentNotifier` has two entry points:

- `ProposedAsync`, from the release import worker after a build prepared deployments
  waiting for approval. Goes to the solution's owner and the deployment pipeline's creator,
  marked urgent, so a digest choice still gets it straight away.
- `FinishedAsync`, from the delivery worker after a run it claimed ends deployed, accepted
  by Business Central for a scheduled install, or failed. Goes to the person it ran as (who
  started it, or who approved a prepared one), or the pipeline's creator when nobody did.
  A run that found the deployment already claimed sends nothing, so nothing is announced
  twice. Deployments failed at startup because the app stopped mid-run are not announced.

Each deployment is its own event, so there is no "only on change" rule here.
