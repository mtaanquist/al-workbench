# Off-site backups (S3-compatible or Azure Blob)

The in-app backup scheduler already writes `pg_dump` files and per-tenant
snapshot ZIPs to the `app-backups` named volume (see
[operator-runbook.md](operator-runbook.md)). Off-site backup adds a
second, disaster-recovery copy by uploading every scheduled full backup
*and* every scheduled per-tenant snapshot to a remote object store and
pruning objects past a configurable retention window.

Two backends are supported, selected by the **Provider** dropdown on the
settings form:

- **S3-compatible** — tested against AWS S3 and MinIO; any S3-compatible
  server (Backblaze B2, Wasabi, Cloudflare R2, Hetzner Object Storage,
  Ceph RGW, SeaweedFS, …) should work — most just need **Force path-style
  addressing** turned on and an endpoint URL.
- **Azure Blob Storage** — a storage account + container, authenticated
  with the **account name** and an **account key**. Region and path-style
  addressing don't apply and are ignored.

The two backends share everything else: object-key layout, the
deployment-id fingerprint guard, staging-and-rename on download, retention
prune, and the DR catalogues. The rest of this doc reads "bucket" for the
S3 case; for Azure substitute "container" — they're the same field.

## What ends up off-site, and what doesn't

| Backup kind                                           | Lives on `app-backups` volume | Uploaded off-site |
|-------------------------------------------------------|:----------------------------:|:-----------------:|
| Full backup, **Scheduled** (`BackupKind.Scheduled`)   | Yes                           | Yes (automatic, after each scheduled run) |
| Full backup, **Ad-hoc** (`BackupKind.AdHoc`)          | Yes                           | Only when SiteAdmin clicks **Upload** on the row |
| Per-tenant logical snapshot, **Scheduled**            | Yes                           | Yes (automatic, after each scheduled per-tenant run) |
| Per-tenant logical snapshot, **Ad-hoc**               | Yes                           | Only when SiteAdmin clicks **Upload** on the row |

Whole-DB dumps live at `<prefix><filename>.dump`. Per-tenant snapshots
namespace under `<prefix>tenants/<slug>/<filename>.tenant.zip` so the
two catalogues stay separable and a DR restore can pull either back
down independently.

Both access keys are encrypted at rest with the ASP.NET Core Data
Protection key ring backed by the `app-keys` volume. Losing `app-keys`
makes the stored keys undecryptable and silently disables off-site uploads
until the SiteAdmin re-enters them.

## One-time setup

1. **Create a bucket and a dedicated access key.**

   - **AWS S3:** create an IAM user with a policy scoped to the bucket
     covering `s3:GetBucketLocation`, `s3:ListBucket`, `s3:PutObject`,
     `s3:GetObject`, and `s3:DeleteObject`. Generate an access key pair.
   - **MinIO / S3-compatible:** create a service account with read/write
     on the bucket. Note the endpoint URL (e.g. `https://minio.example.com`).

2. **Open `/site-admin/settings`** as a SiteAdmin. Go to the **Off-site
   backups** tab, pick the **Provider**, and fill in:

   | Field                         | What to enter                                                                 |
   |-------------------------------|-------------------------------------------------------------------------------|
   | Provider                      | `S3-compatible` or `Azure Blob Storage`.                                       |
   | Endpoint                      | Blank for AWS / the Azure default host. URL for MinIO / other S3-compatible (`https://s3.example.com`) or an Azure custom / Azurite blob endpoint. |
   | Region                        | The bucket region (`eu-west-1`, `us-east-1`, …). S3 only — ignored for Azure. |
   | Bucket / Container            | The bucket name (S3) or container name (Azure). Required.                     |
   | Prefix                        | Optional key prefix, e.g. `aldevtoolbox/prod/`. Leave blank to write to the bucket root. |
   | Access key id / Storage account name | S3 access key id, or the Azure **storage account name**. Leave blank on subsequent edits to keep the stored value; tick **Clear stored …** to wipe. |
   | Secret access key / Account key | S3 secret access key, or the Azure **account key**. Same keep-blank-to-preserve behaviour. |
   | Force path-style addressing   | Tick for MinIO and most non-AWS providers. Leave unticked for AWS S3. S3 only — ignored for Azure. |
   | Retention (days)              | Objects under the prefix older than this are deleted on every prune pass. 1–3650. |
   | Upload scheduled backups …    | Tick to actually start uploading. Leave unticked to "save the credentials but stay off".|

   Save.

   For **Azure Blob Storage** specifically: create a storage account and a
   container scoped to these backups, then take an access key from the
   account's **Access keys** blade (or rotate one in for this use). Enter
   the account name in **Storage account name** and the key in **Account
   key**. The default endpoint `https://<account>.blob.core.windows.net`
   is derived from the account name — only set **Endpoint** for a
   sovereign cloud or the Azurite emulator.

3. **Click `Test off-site connection`.** The button HEADs the bucket using
   the stored credentials. Look for `OK: Connected to bucket '<name>'.` in
   the banner. `FAIL:` shows the underlying S3 error code and message —
   common ones below.

4. (Optional) From `/site-admin/backups`, take an **ad-hoc** backup and
   click the **Upload** icon on the row. The row gains an `Off-site` badge
   when the upload succeeds. Open the bucket externally (AWS console,
   `mc ls`, `aws s3 ls`) to confirm the object key matches
   `<prefix>aldevtoolbox-<UTC-timestamp>-adhoc.dump`.

That's it. From the next scheduled-backup tick onward, every scheduled
full backup uploads automatically and prune runs at the end of the tick.
Manual ad-hoc backups are uploaded only on click.

## What "Upload" and "Prune" actually do

- **Upload** (`OffsiteBackupService.UploadAsync`): streams the local
  `pg_dump` file from the `app-backups` volume to
  `s3://<bucket>/<prefix><filename>`. On success, stamps the `backups`
  row with `offsite_uploaded_at` and `offsite_object_key` so the UI can
  show the badge and link.
- **Prune** (`OffsiteBackupService.PruneAsync`): lists objects under the
  configured prefix and deletes ones whose `LastModified` is older than
  `RetentionDays`. Idempotent — safe to re-run. Runs after every
  scheduled backup tick.

Off-site retention is *independent* from local retention. The local
`BackupRetentionCount` keeps the N most recent unpinned files on disk;
off-site `RetentionDays` keeps objects for a calendar window in the
bucket. Tune them separately.

## Restoring from an off-site copy (whole-DB)

`/site-admin/backups` lists every dump-shaped object under the configured
prefix in an **Off-site catalogue** section. The flow is fully UI-driven
— no shelling into the container, no SQL by hand.

1. Open `/site-admin/backups` as a SiteAdmin. Below the local backups
   table you'll see **Off-site catalogue** with each remote object's file
   name, last-modified timestamp, size, and whether a local row with the
   same file name already exists. If the catalogue is empty, take a
   scheduled or ad-hoc backup first and upload it.

2. Pick the row you want and click its **Download** icon
   (archive-restore). The button posts to
   `/site-admin/backups/offsite/download`, which enqueues a job on the
   background worker and redirects you straight back to the page.

3. An **Off-site downloads** panel appears at the top of the page with a
   live progress bar — bytes downloaded over total size, refreshed every
   second by a small in-page script polling
   `/site-admin/backups/offsite/jobs/{id}`. The download runs in a
   `BackgroundService`, so the bar keeps moving even if you close the
   browser tab. The status flips to **Completed** when the dump lands
   on the `app-backups` volume.

4. When every active job is terminal the page reloads. The downloaded
   file appears in the local backups table with an `Off-site` badge,
   pinned automatically so retention can't prune it before you've
   restored.

5. Click **Restore** on the new row and confirm. The app enters
   maintenance mode for the duration; non-SiteAdmin requests get a
   static maintenance page until `pg_restore` finishes.

If the deployment is completely gone (lost host, lost `pg-data`, lost
`app-backups`), the recovery path is just as short:

1. Stand up the compose stack on a fresh host pointed at the same
   bucket. Set `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` so
   you can sign in against the empty database; migrations run against an
   empty `pg-data` and the bucket is untouched.
2. Sign in as the bootstrap admin. Configure off-site under
   `/site-admin/settings` with the same bucket, prefix, and credentials.
3. Open `/site-admin/backups`. The catalogue lists the newest objects
   that survived the loss. Every dump is stamped with the id of the
   installation that uploaded it (the `deployment-id` file on the
   `app-keys` volume), and the new host has a new id, so the lost
   installation's dumps are tagged **Another installation**. While this
   installation is still fresh (no organisation besides the default one,
   and no user besides the bootstrap admin) each of them carries a
   **Recover** button instead of the plain download. Click **Recover** on
   the dump you want and confirm, wait for the progress bar to finish,
   click **Restore**, confirm.
4. The bootstrap user's identity will be replaced by whatever the dump
   carried — sign in again with the original credentials.

**What Recover changes.** It records the lost installation's id in
`deployment-id.trusted`, next to `deployment-id` on the `app-keys`
volume. From then on this installation treats every off-site object
stamped with that id as its own: the per-tenant snapshots can be pulled
back and restored after the whole-database restore, and retention prunes
the lost installation's objects as they age out. This installation's own
id does not change, and its new uploads carry it. Recover is refused
once the installation holds organisations or users of its own, because
restoring another installation's dump over real data is exactly what the
stamp is there to stop.

**If you still have the old `app-keys` volume** (or a copy of its
`deployment-id` file), put it back before the first start on the new
host. The new host then has the old id, its dumps are not "another
installation" at all, and Recover is not needed. Carrying `app-keys`
over also keeps the Data Protection key ring, which is what keeps the
stored SMTP, off-site, Microsoft sign-in and GitHub secrets readable
after the restore.

## Restoring a per-tenant snapshot from off-site

`/site-admin/tenant-backups` carries an identical **Off-site catalogue**
section, populated from objects under `<prefix>tenants/<slug>/`. The flow
mirrors the whole-DB one:

1. Pick a row in the catalogue. The page shows the slug, the snapshot
   filename, last-modified time, size, and whether a local snapshot
   with the same name already exists.

2. Click the **Download** icon. The page posts to
   `/site-admin/tenant-backups/offsite/download`, enqueues a job, and
   redirects with the same in-page progress bar as the whole-DB flow.

3. On completion, a new row appears under the tenant's section of the
   local snapshots table — auto-pinned and stamped with `Off-site`. The
   row's `SchemaVersion`, `CreatedAt`, and `OrganizationId` come from
   the snapshot's `manifest.json`, not from the object key — so the
   download refuses if the manifest's slug or org id doesn't match
   what's locally known.

4. Click **Restore** on the row and confirm. The org's rows are wiped
   and replayed from the snapshot, exactly as if the snapshot had been
   created locally.

**Pre-requisite for per-tenant DR.** The local deployment must already
know about the org (by slug). If you've just stood up a fresh stack
after losing `pg-data`, restore the whole-DB dump first — that brings
the `organizations` row back. Then come here and pull the per-tenant
ZIP. The download button is disabled with a tooltip when no local
organisation has that slug. If the whole-DB dump was restored with
**Recover**, the lost installation's per-tenant snapshots are accepted
as this installation's own.

### Job lifetime and host restarts

Restore jobs (whole-DB and per-tenant alike) live in memory on the
running container. If the host crashes or you redeploy mid-download,
the job is lost — but the download writes to `<filename>.partial` and
is only renamed to the final name on success, so a half-finished file
never gets registered as a restorable row. Just re-queue the download
from the catalogue.

Terminal jobs (Completed, Failed) linger on the page for an hour so a
SiteAdmin who refreshed at the wrong moment can still see the outcome,
then evict themselves on the next page render.

Jobs are kept on the page they were enqueued from — whole-DB downloads
on `/site-admin/backups`, per-tenant downloads on
`/site-admin/tenant-backups`. They share the same in-memory tracker and
the same JSON status endpoint, but the UI filters by job kind so each
page only shows the work that landed there.

## Disabling or rotating credentials

- **Pause uploads** without losing credentials: untick **Upload scheduled
  backups …**, save. The credentials stay encrypted in the DB; nothing
  uploads until you tick it again.
- **Rotate the secret key**: paste the new secret into **Secret access key**,
  save. The new ciphertext replaces the old one. Leaving the field blank
  keeps the current value, so a partial edit to e.g. the prefix won't
  wipe credentials by accident.
- **Wipe credentials**: tick **Clear stored access key** and **Clear stored
  secret key**, save. Uploads stop on the next scheduler tick.

## Troubleshooting

Symptoms → things to check.

- **Test connection returns `FAIL: S3 error: PermanentRedirect`.** The
  region doesn't match the bucket. Either set the correct region or use
  the bucket's regional endpoint URL.
- **Test connection returns `FAIL: S3 error: SignatureDoesNotMatch`.**
  Wrong secret key, or the clock on the host is skewed by more than 15
  minutes — S3 rejects requests with a stale timestamp.
- **Test connection returns `FAIL: S3 error: AccessDenied` on
  `GetBucketLocation`.** The IAM policy is missing
  `s3:GetBucketLocation`. Either grant it, or use a different identity
  with broader bucket-level read permission for the test.
- **Test connection passes, but uploads silently don't happen.** Open
  `/site-admin/backups` and confirm the backup row has no `Off-site`
  badge. Most common cause: **Upload scheduled backups …** is unticked.
  Next: the local file is missing under
  `/var/lib/aldevtoolbox/backups/` (the upload refuses rather than
  uploading nothing); check `docker compose logs aldevtoolbox` for
  `Refusing to upload`.
- **Uploads worked, then stopped after a redeploy.** The `app-keys`
  volume was lost or replaced — the encrypted credentials no longer
  decrypt. Look for
  `Failed to decrypt off-site credentials` in the logs. Re-enter the
  access key and secret on `/site-admin/settings` and save.
- **MinIO returns `XAmzContentSHA256Mismatch` on uploads.** Some older
  S3-compatible servers can't verify the streaming payload signature
  the SDK sends. The service sends `DisablePayloadSigning = true`
  whenever **Force path-style addressing** is ticked, which is the
  usual workaround; if you hit this with path-style off, tick path-style.
- **Prune runs but nothing is deleted.** Confirm `RetentionDays` is set
  and that the prefix matches the upload path — `ListObjectsV2` filters
  on `Prefix`, so retention can only see what's under it.
- **Download job stuck in Queued.** The single-threaded restore worker
  is serialising — only one download runs at a time. The earlier job
  finishes first, then the next moves to Running. If no other job is
  active, check `docker compose logs aldevtoolbox` for
  `OffsiteRestoreWorker` errors.
- **Download fails with "A local backup named '...' already exists".**
  Either delete or rename the local row (unpin first if needed), or
  rename the off-site object out of the way. The service refuses to
  overwrite a row that may already be on disk.
- **Catalogue says "Already local: Yes" but the Download button is
  disabled.** That's the same collision-prevention guard. The local row
  is already there — restore from it directly.
- **Per-tenant catalogue says "(unknown)" next to the slug and the
  Download button is disabled.** The deployment doesn't have an
  organisation with that slug. Restore the whole-DB dump first so the
  `organizations` row exists, then come back to pull the per-tenant
  snapshot. An organisation counts as known as soon as it exists; it
  does not need a local snapshot of its own.
- **Download fails with "created by a different deployment" or the
  catalogue tags a dump "Another installation".** The object carries
  another installation's `deployment-id` stamp. If another installation
  shares the bucket and prefix, that is the guard working: give each
  installation its own prefix. If it is the installation you are
  rebuilding, use **Recover** on a fresh installation (see the
  disaster-recovery steps above). If `app-keys` was lost while `pg-data`
  survived, the installation is not fresh and Recover is not offered:
  read the lost id from the object's metadata in your storage console
  (`x-amz-meta-deployment-id` on S3, `deploymentid` on Azure), add it on
  a line of its own to `deployment-id.trusted` in the `app-keys` volume,
  and restart the app.
- **The catalogue does not show every object.** The database catalogue
  shows the newest 100 dumps, and the per-tenant catalogue the newest 14
  snapshots of each organisation. Both list the whole prefix and sort by
  date before cutting, so the most recent are always there; older ones
  are in the bucket and can be fetched with your storage tool.
- **Per-tenant download fails with "Snapshot manifest names org … but
  the object key is under …".** The object's key path and the
  snapshot's manifest don't agree on which org it belongs to.
  Investigate the bucket — the snapshot may have been moved between
  prefixes, which the restore refuses for safety.
- **A bucket policy that requires server-side encryption rejects uploads
  with `AccessDenied`.** The service does not set `ServerSideEncryption`
  on the `PutObject` request. Either relax the policy, or configure
  bucket-default encryption so the server applies it on receipt.

## Acceptance check

After setup, all five of the following should be true:

- `/site-admin/settings` → **Test off-site connection** → `OK: Connected …`.
- The next scheduled backup row on `/site-admin/backups` shows the
  `Off-site` badge within ~30 s of the scheduled time.
- The next scheduled per-tenant snapshot row on
  `/site-admin/tenant-backups` shows the `Off-site` badge in the same
  window.
- An external listing (`aws s3 ls --recursive`, `mc ls --recursive`,
  AWS console) shows the whole-DB dump at `<prefix><filename>.dump` and
  per-tenant ZIPs at `<prefix>tenants/<slug>/<filename>.tenant.zip`.
- Each catalogue (whole-DB on `/site-admin/backups`, per-tenant on
  `/site-admin/tenant-backups`) lists its corresponding objects;
  clicking **Download** in either runs a progress bar to completion
  and produces a new local row carrying the `Off-site` badge.
