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
  scheduled backup tick. It only ever deletes this installation's own
  backups: another installation's, and those of an installation this one
  took over (see *Taking over another installation's backups*), are left
  alone.

Off-site retention is *independent* from local retention. The local
`BackupRetentionCount` keeps the N most recent unpinned files on disk;
off-site `RetentionDays` keeps objects for a calendar window in the
bucket. Tune them separately.

## Restoring the whole site from off-site storage

Everything below happens in the app as a SiteAdmin, under **Site
administration → Backup & storage**. No shelling into the container, no
SQL by hand.

1. Open the **Whole site** tab. **Copies kept off-site** lists the newest
   backups in off-site storage, with their size and whether a copy is
   already on this server. If the list is empty, take a backup and upload
   it, or wait for the nightly one.
2. Click **Download** on the backup you want. The download runs in the
   background: a **Downloading from off-site storage** panel shows its
   progress, and it keeps going if you close the page.
3. When it finishes the page reloads, and the backup appears under
   **Copies kept on this server**, pinned so it is not cleaned up before
   you restore it.
4. Click **Restore** on it and confirm. The site goes into maintenance
   mode until the restore is done. Restoring replaces everything,
   including user accounts.

### Rebuilding after losing the server

If the server is gone (lost host, lost `pg-data`, lost `app-backups`):

0. **Before the first start on the new server, put the old `app-keys`
   volume back if you still have it** (or at least its `deployment-id`
   file). That keeps the stored secrets readable after the restore, and
   the new server is then the same installation as far as its backups
   are concerned, so the rest of this section is the ordinary restore
   above.
1. Start the stack on the new server, pointed at the same storage. Set
   `BOOTSTRAP_ADMIN_EMAIL` / `BOOTSTRAP_ADMIN_PASSWORD` so you can sign in
   to the empty site.
2. Sign in as that admin. Under **Site administration → Settings →
   Off-site**, enter the same storage, bucket, prefix and credentials the
   old server used.
3. Open **Backup & storage → Whole site**. Without the old `app-keys`,
   the old server's backups are marked **Another installation**. While
   this installation is still new, each of them has a **Recover** button.
   Click **Recover** on the one you want and confirm. That only downloads
   it.
4. When the download finishes, click **Restore** on it under **Copies
   kept on this server** and confirm. This replaces everything on the
   new server with the backup, and from now on this installation accepts
   the old one's backups as restorable here (see *Taking over another
   installation's backups* below).
5. Sign in again with an account from the backup. The bootstrap admin
   you used in step 2 is gone.
6. **Without the old `app-keys`, re-enter the stored secrets.** They are
   encrypted with the key ring on that volume, so the restored copies
   cannot be read on the new server. Off-site storage in particular reads
   as not configured until you re-enter its access key and secret under
   **Settings → Off-site**. Re-enter the SMTP password, Microsoft sign-in
   secret and GitHub App credentials too, if you use them.
7. To bring back one organisation from a later snapshot, carry on with
   the next section.

**When Recover is not offered.** Recover only appears, and Restore of a
recovered backup only works, on an installation that is still new: only
the default organisation, no user besides the first administrator, no
solutions and no releases. Backups the new server has already taken of
itself don't count. The reason for the rule: a backup from another installation replaces
everything, and the stamp on each backup exists to stop a neighbour's
backup in the same storage being restored over real data.

### Taking over another installation's backups

Restoring a recovered backup records the old installation in the file
`deployment-id.former`, next to `deployment-id` on the `app-keys`
volume. From then on:

- its backups and organisation snapshots in off-site storage can be
  downloaded and restored here, and are no longer marked "Another
  installation";
- automatic clean-up never deletes them - only this installation's own
  backups are cleaned up. Delete the old ones in your storage provider
  when you no longer need them;
- the **Whole site** tab shows **Recovered from another installation**
  with a **Remove** button. Removing it makes the old installation's
  backups "Another installation" again. Don't delete
  `deployment-id.former` by hand while you still need those snapshots.

An installation takes over at most one other installation this way.

## Restoring one organisation from off-site storage

The **Per organisation** tab works the same way for organisation
snapshots.

1. **Snapshots kept off-site** lists the newest snapshots of each
   organisation, grouped by organisation.
2. Click **Download** on the one you want and wait for the download to
   finish.
3. It appears under **Snapshots kept on this server**, pinned. Click
   **Restore** on it and confirm. That organisation's data is replaced
   with the snapshot; nothing else on the site changes.

The organisation has to exist on the site already. On a rebuilt server,
restore the whole-site backup first; that brings the organisation back.
A snapshot whose organisation is missing is marked **Not on this site**.

### How it works

- Downloads run on a background worker, one at a time, and are written
  to `<name>.partial` and renamed only when complete, so a crash
  mid-download never leaves a half-written backup that looks restorable.
  Download jobs are kept in memory: after a restart, start the download
  again. A finished or failed job stays on the page for an hour.
- An organisation snapshot carries a manifest naming its organisation
  and the installation that took it. The download refuses one whose
  manifest names a different organisation than its location in storage,
  or another installation.
- Each backup is stamped with the uploading installation's id (the
  `deployment-id` file on `app-keys`). Backups without a stamp predate
  stamping and are treated as this installation's unless another
  installation's backups share the prefix.

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
- **The off-site list says "Already on this server" instead of offering
  Download.** That's the same collision guard. The copy is already
  there: restore it from **Copies kept on this server**.
- **A snapshot on the Per organisation tab is marked "Not on this
  site".** This site has no organisation with that name in storage.
  Restore the whole-site backup first so the organisation exists, then
  come back for the snapshot. An organisation counts as present as soon
  as it exists; it does not need a local snapshot of its own.
- **A backup is marked "Another installation", or a download fails with
  "created by a different deployment".** The backup carries another
  installation's stamp. If another installation shares the bucket and
  prefix, that is the guard working: give each installation its own
  prefix. If it is the installation you are rebuilding, use **Recover**
  on a new installation (see *Rebuilding after losing the server*).
- **`app-keys` was lost but the database survived.** The installation
  is not new, so Recover is not offered, and its own earlier backups now
  read as another installation's. Put the old `deployment-id` file back
  if you have a copy. If not, read the old id from any earlier object's
  metadata in your storage console (`x-amz-meta-deployment-id` on S3,
  `deploymentid` on Azure), write it as the only line of
  `deployment-id.former` next to `deployment-id` on the `app-keys`
  volume, and restart the app. That file holds one id; if it already
  holds another, remove that one on the **Whole site** tab first.
- **Not every object is listed.** The **Whole site** tab lists the newest
  100 backups, and the **Per organisation** tab the newest 14 snapshots
  of each organisation (fewer each when there are many organisations).
  Both look at the whole prefix and sort by date before cutting, so the
  most recent are always there; older ones are still in storage and can
  be fetched with your storage provider's tools.
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
- Under **Site administration → Backup & storage**, the off-site list on
  the **Whole site** tab and on the **Per organisation** tab shows the
  matching objects; clicking **Download** in either runs a progress bar
  to completion and produces a new copy on this server.
