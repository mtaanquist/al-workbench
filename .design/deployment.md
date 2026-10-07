# Deployment

## Target environment

The app runs as two Docker containers (app + Postgres) sitting behind whatever ingress the company uses (Traefik, nginx, etc.). The previous "single container, single volume" posture relaxed in P4.16 — see `architecture.md`. The compose file in the repo (`compose.yaml`) is the canonical deployment shape.

## Dockerfile

A multi-stage build producing a self-contained image:

The repo ships a working `Dockerfile` at the root; that's the canonical build. Key points:

- The image carries the application binaries and embedded `Resources/` only. There is no on-disk seed directory — the singleton system org owns the canonical templates at runtime.
- The app reads `ConnectionStrings__DefaultConnection` to find the database. There is no on-disk DB; persistence is the sibling `db` compose service backed by the `pg-data` named volume.
- Port 8080 inside the container, mapped however the host wants.
- Both base images are pinned by digest (`FROM ...@sha256:...`, with the tag they were resolved from in the comment above each `FROM`), so a rebuild of a given commit produces the same image. Bump them deliberately with `docker buildx imagetools inspect mcr.microsoft.com/dotnet/aspnet:10.0`.
- **The app runs as the non-root `app` user** shipped by the `mcr.microsoft.com/dotnet/aspnet` images. This container clones customer repositories, provisions the AL compiler from NuGet and runs it over that source, and holds the database credentials in its environment, so it does not run as uid 0. The Dockerfile creates and `chown`s the three volume mount points (`/var/lib/aldevtoolbox/{dp-keys,backups,altool}`) before the `USER` line so Docker seeds freshly created named volumes with the right ownership. The compose service additionally drops all Linux capabilities and sets `no-new-privileges`. `read_only: true` is *not* set: the build pipeline stages repository clones and multi-GB `.app` / DVD downloads under `/tmp`, and a tmpfs there would charge all of that to RAM. Existing installs whose volumes were created by the old root image need a one-off `chown` — see *Upgrades* below.

## docker-compose example

The canonical compose file lives at the repo root (`compose.yaml`). It defines two services — `aldevtoolbox` (the app) and `db` (`postgres:18-alpine`) — wired together with a healthcheck-gated `depends_on`. The app reads `ConnectionStrings__DefaultConnection`, which compose builds from `POSTGRES_USER` / `POSTGRES_PASSWORD` / `POSTGRES_DB`. `POSTGRES_PASSWORD` has no default — compose refuses to start without it rather than silently using a well-known one. See `README.md` for the quick-start invocation.

## Environment variables

**`README.md` carries the single table of every variable the app reads** (under *Run in Docker*). That list used to be duplicated here and the two copies drifted, so this document deliberately keeps no second one — document a new variable in the README table only.

The variables that encode a deployment *decision* rather than plumbing are discussed below in prose: `SINGLE_TENANT_MODE` and the `SINGLE_TENANT_*` seeds under *Single-tenant mode*, `PUBLIC_BASE_URL` and `AllowedHosts` under *Public origin and `AllowedHosts`*, and `POSTGRES_PASSWORD` (required, no default) above.

If `ConnectionStrings__DefaultConnection` is unset the app fails to start with a clear error.

## First-run behaviour

On first start against an empty database:

1. EF Core migrations run, creating all tables.
2. The migration seeds the **Default** organisation row and stamps it with `is_system = true` — making it the singleton system org other organisations fork from.
3. The bootstrap admin (from `BOOTSTRAP_ADMIN_*` env vars) is created in the Default org with `is_site_admin = true`.
4. The app starts serving requests.

The Default org's template catalogue starts empty. SiteAdmins author canonical templates via the regular `/admin/templates` pages; other organisations fork them at import time via `TemplateImportService` (wired to the "From the site catalogue" section of `/admin/templates`).

On subsequent starts the app applies any pending migrations and starts serving.

## Single-tenant mode

`SINGLE_TENANT_MODE=1` adapts the app for a company hosting it internally for one organisation. The multi-tenant machinery becomes noise in that shape, so the flag hides and disables it:

- **Storage quotas** — the SiteAdmin "Storage quotas" settings tab and the per-org "Storage" page are removed, the sidebar capacity bar is hidden, the usage-snapshot scheduler is skipped, and `StorageQuotaGuard` never blocks a write (no invisible limits). The quota and storage endpoints 404.
- **Tenant snapshots** — the "Tenant snapshots" tab and its endpoints are removed and `BackupScheduler` skips the per-tenant snapshot loop. The system-level `pg_dump` ("Database" tab) is unaffected and keeps running.
- **Signup** — the org-name/short-ID fields are hidden and `AccountService` refuses to provision a new organisation. Existing-org onboarding (claimed email domain, admin invite) still works; self-service org creation does not.

What's deliberately kept: the cross-org Site Admin user/audit views and `pg_dump` backups. The lone organisation **is** the Default/system org, so the per-org Administration and template-authoring pages — normally hidden in the system org — are surfaced again, letting that one org manage its own content.

**First-run seeding.** Because self-service org creation is off, the org needs to come up configured. On a fresh database (the same window the bootstrap admin uses) the app reads three optional env vars and applies them to the Default org: `SINGLE_TENANT_ORG_NAME`, `SINGLE_TENANT_ORG_SLUG`, and `SINGLE_TENANT_EMAIL_DOMAINS`. Seeding a domain also flips the org's auto-join-verified-domain-users setting on, so staff who verify an email at that domain join as active users without an admin pre-claiming the domain in the UI. Auto-join applies to the **verified (SMTP) signup path** — with SMTP off, domain-matched signups still land Pending for admin approval. After the first run these vars are ignored (like `BOOTSTRAP_ADMIN_*`); later edits go through `/admin/administration/identity`.

**One-way switch.** Single-tenant is a deployment-time choice. Because the single tenant is the system org and self-service org creation is disabled, there is **no in-place path back to multi-tenant** — if you need multi-tenant later, stand up a fresh installation. This is intentional: we don't carry a single→multi migration path.

The flag is read once at boot (an immutable singleton, like the scheduler opt-outs); flip it and restart.

## Backups

The entire state of the app is in the Postgres database in the `pg-data` named volume. Back this up.

Backup is `pg_dump -Fc` against the `db` service. M18 ships a built-in backup hosted service that runs `pg_dump` on a schedule and writes to `/var/lib/aldevtoolbox/backups`; until then, an external cron job is the simplest robust approach. The in-app "Export to TOML" remains useful for human-readable snapshots of the catalogue, but it does not capture audit history.

## Upgrades

Standard:

1. Build new image.
2. Stop container.
3. Start new container against the same volume.

### Upgrading an install that ran as root (one-off)

Named volumes created by an older image are owned by `root`, and the non-root app cannot write to them. Do the one-off chown after pulling the new image and before starting it:

```bash
docker compose down
docker run --rm \
  -v aldevtoolbox_app-keys:/v/dp-keys \
  -v aldevtoolbox_app-backups:/v/backups \
  -v aldevtoolbox_app-altool:/v/altool \
  alpine chown -R 1654:1654 /v/dp-keys /v/backups /v/altool
docker compose up -d
```

(`1654` is the `app` uid/gid in the .NET images; the volume names carry the compose project prefix — check `docker volume ls` if yours differs. The `app-altool` contents are disposable, so deleting that volume instead is also fine.)

Migrations run on startup. If a migration is destructive (drops a column, etc.), back up first. Migration testing in CI against a copy of the production DB is a good practice but not required for v1.

## Monitoring

The app exposes three operator endpoints (wired in `Program.cs`):

```csharp
app.MapHealthChecks("/healthz", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("healthz")
});
app.MapHealthChecks("/readyz", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("readyz")
});
```

- `/healthz` — liveness: `200` when the database is reachable **and** the Data Protection key ring round-trips; `503` otherwise. The Dockerfile `HEALTHCHECK` polls this.
- `/readyz` — readiness: only green once startup work (migrations + first-run seed + bootstrap admin) has finished. Gate reverse-proxy traffic on it.
- `/healthz/workers` — background-worker liveness (imports, schedulers): `200` while every registered worker is beating, `503` when one is stalled. Deliberately separate from `/healthz` so a slow background job never triggers a container restart — wire it to alerting, not to the `HEALTHCHECK`.

## Logs

Standard ASP.NET Core logging to stdout. Whatever log aggregation the company uses (Loki, ELK, Datadog) picks them up via Docker logs.

Recommended log levels:

- `Microsoft`: Warning
- `Microsoft.EntityFrameworkCore`: Warning (Information if debugging migrations)
- `AlWorkspaceBuilder`: Information

## Resource sizing

This is a small tool. The app container needs ~256MB RAM and 0.5 CPU; the Postgres container at this load is comfortable in 512MB / 0.5 CPU. The expected steady state is dozens of templates per organisation and a few thousand audit log rows.

Builds are the other exception, and the one to size for once there are many pipelines. Project and pull request builds run "Pipeline builds that run at once" at a time (Site administration → Settings → Builds; empty means `OE_BUILD_CONCURRENCY`, default `2`), on their own queue, apart from release imports. Requests a person is waiting on (Build, Retry, pull request checks) start first, then builds on push, then the nightly preview checks; a pipeline's builds against one Business Central version run one at a time, in order. Each running build wants roughly one CPU for the compiler and about 1 to 1.5 GB of memory for the compile and the Object Explorer ingest, and the database takes the ingest's writes. So scaling builds is three settings changed together, not one: raise the build setting (it applies at once, no restart; lowering it lets running builds finish), raise the app container's `cpus` / `memory` limits in `compose.yaml` to match (it ships with 4 CPUs and 4 GB, which suits the default of 2), and give the `db` service more CPU and memory (with `shared_buffers` / `work_mem`) as the concurrency grows. A bigger VM alone changes nothing until those limits move. The settings tab shows the processors (logical ones, so each hyperthread counts) and memory the container has and recommends max(1, min(processors - 1, memory in GB)); it never sets the value itself. Stay on one app instance: the build queue, the schedulers and the startup reconcile all assume they are the only copy.

The other exception is Object Explorer at full-catalogue scale. The find-references / dependency queries walk the recursive release-ancestry chain and hash-join `oe_module_references`, which pushes Postgres into a **parallel** plan. Parallel workers allocate dynamic-shared-memory segments in `/dev/shm`, and Docker's default 64MB `/dev/shm` is too small — the query aborts with SQLSTATE `53100` ("could not resize shared memory segment ... No space left on device"). This is `/dev/shm` exhaustion, not the `pg-data` volume filling up. `compose.yaml` therefore sets `shm_size: 256mb` on the `db` service; any other deployment shape (Kubernetes, a hand-rolled `docker run`) must do the equivalent — mount a `/dev/shm` of at least ~256MB — or those queries will fail under load.

## TLS

The container should *not* terminate TLS itself. Run it behind a reverse proxy (Traefik, nginx, Caddy) that handles certificates. Set `app.UseForwardedHeaders()` to handle the `X-Forwarded-Proto` header so cookies get the `Secure` flag correctly.

For operators who don't already run an ingress, `compose.yaml` ships an **optional, commented-out `caddy` service** (with a `Caddyfile` at the repo root) that fronts the app on 80/443 and provisions Let's Encrypt certificates automatically — uncomment it, set `SITE_ADDRESS` + `ACME_EMAIL`, and uncomment the `caddy-data` / `caddy-config` volumes. It's a convenience, not a new fence: bring-your-own Traefik/nginx is unchanged, and the app still terminates HTTP only. Caddy preserves the inbound `Host` header and sets `X-Forwarded-Proto`, so request-derived absolute URLs (email links, OAuth issuer) resolve to the public `https://` domain with no extra config; passkeys still need `Auth__WebAuthn__RpId` / `OriginsCsv` set to that domain. See `README.md` → "HTTPS with Caddy (optional)".

### Public origin and `AllowedHosts`

Email links that carry a credential — password reset, magic link, invite accept, email-change confirm, signup verification — used to be built by interpolating the inbound request's `Host` header. Anyone could therefore have a genuine reset email delivered to a victim with the token pointing at their own site. Two settings close that, and a deployment should set both:

- **`PUBLIC_BASE_URL`** — the public origin those links are built from. Once set the request `Host` is ignored for link building (and for the OAuth protected-resource metadata document). Unset, the old request-derived behaviour stays for compatibility and a warning is logged at startup.
- **`AllowedHosts`** — `appsettings.json` ships `*` so a fresh checkout runs anywhere; set it per deployment (env var `AllowedHosts`, semicolon-separated) to the real host names, and Kestrel's host-filtering middleware refuses a foreign `Host` before any handler runs.

`PUBLIC_BASE_URL` is what makes the links correct; `AllowedHosts` is what stops the forged request reaching the handler at all.

## What's deliberately not here

- Horizontal scaling. Blazor Server's SignalR connections are sticky to the server; one instance is enough for this load.
- An external queue or worker process. Generation is synchronous and finishes in under a second, and it stays that way. Heavier work (build imports, discovery, deliveries, environment refreshes, off-site restores) runs off the request thread through in-process channel-backed queues inside the same container — no broker, no separate worker deployment.

Multi-tenancy, by contrast, *is* here: every editable entity is scoped to an organisation. A single-org deployment turns the multi-tenant surfaces off with `SINGLE_TENANT_MODE=1` (above), which hides them without relaxing the isolation itself.
