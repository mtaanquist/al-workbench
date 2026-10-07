using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Endpoints;

/// <summary>
/// Runs at startup, after the host has been built: applies pending EF
/// migrations, ensures the singleton system org exists, and creates the
/// bootstrap SiteAdmin from BOOTSTRAP_ADMIN_* env vars when the database is
/// empty. Flips /readyz to green only once everything has finished.
/// </summary>
internal static class StartupTasks
{
    /// <summary>
    /// Command timeout for the one-time startup migration step. Generous on
    /// purpose: a large index build or data backfill can run for minutes on a
    /// production-sized database, far past the 30s default. A one-shot ceiling —
    /// startup stays /readyz-red until it finishes and ApplicationStopping aborts
    /// it on shutdown — so a generous bound has no steady-state cost.
    /// </summary>
    private static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromMinutes(30);

    public static async Task RunAsync(WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var stopping = app.Lifetime.ApplicationStopping;
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var auth = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.Account.AuthService>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

        // Migrations inherit the default 30s command timeout, which a one-time
        // large-index or backfill migration blows past on a production-sized
        // database — e.g. building the Object Explorer's pg_trgm GIN indexes over
        // a multi-million-row oe_module_symbols. That times the command out,
        // rolls the migration back, and crash-loops startup. Give the migration
        // step a generous ceiling (it's a one-shot, gated by /readyz and aborted
        // by ApplicationStopping on shutdown), then restore the default for the
        // fast seed/bootstrap work below. See issue with the v7.0.0 trigram
        // indexes (#447/#448).
        db.Database.SetCommandTimeout(MigrationCommandTimeout);
        await db.Database.MigrateAsync(stopping);
        db.Database.SetCommandTimeout((int?)null);

        // Resolve the singleton system org. Look it up by IsSystem rather than
        // a fixed slug: single-tenant first-run seeding (SINGLE_TENANT_ORG_SLUG)
        // can rename the slug, and keying on "default" here would miss the
        // renamed org on the next boot and try to INSERT a second is_system row
        // — violating ix_organizations_is_system_singleton in a crash loop.
        var defaultOrg = await EnsureSystemOrganizationAsync(db, stopping);

        // Seed the platform-default workspace files only for orgs that have
        // none yet. Once an org has any files, its admins own that list:
        // re-running a per-path backfill here would resurrect platform
        // defaults they deliberately removed. Genuinely new platform files are
        // propagated to existing orgs via a one-off migration backfill (see
        // BackfillAppJsonAsIncludedFile), not on every boot.
        // organizations is the tenant table itself and carries no query filter,
        // so this every-org read needs no bypass.
        var allOrgIds = await db.Organizations
            .Select(o => o.Id)
            .ToListAsync(stopping);
        var orgIdsWithFiles = (await db.OrganizationFiles
            // Same category 3 read: which orgs already have platform files.
            .IgnoreQueryFilters()
            .Select(f => f.OrganizationId)
            .Distinct()
            .ToListAsync(stopping))
            .ToHashSet();
        foreach (var orgId in allOrgIds)
        {
            if (orgIdsWithFiles.Contains(orgId)) continue;
            await PlatformOrganizationFileSeeder.EnsureForOrganizationAsync(db, orgId, DateTime.UtcNow, stopping);
        }
        try
        {
            await db.SaveChangesAsync(stopping);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // A concurrent startup won the race and already seeded these
            // org-default files between our "which orgs have files" read and
            // this write. The unique (organization_id, path) index rejects our
            // duplicate inserts — but the rows now exist, so the seed goal is
            // met. Detach the losing inserts so the later SaveChangesAsync
            // calls in this method aren't re-poisoned by them, then continue.
            // This is a single-container app in production, but parallel
            // WebApplicationFactory test hosts (and would-be multi-replica
            // boots) share one database and can overlap here.
            foreach (var entry in db.ChangeTracker.Entries<OrganizationFile>().ToList())
            {
                entry.State = EntityState.Detached;
            }
            logger.LogInformation(
                "Platform org-file seed raced with a concurrent startup; the other writer won. Continuing.");
        }

        // Bootstrap admin: only runs once, when there are no users in the
        // database. After that the env vars are read but ignored (logged).
        var bootstrapEmail = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_EMAIL");
        var bootstrapPassword = Environment.GetEnvironmentVariable("BOOTSTRAP_ADMIN_PASSWORD");
        // Fence category 3 (startup, no request org): existence-only probe for the
        // bootstrap-admin first-run check.
        var anyUsers = await db.Users.IgnoreQueryFilters().AnyAsync(stopping);
        if (!anyUsers && !string.IsNullOrWhiteSpace(bootstrapEmail) && !string.IsNullOrWhiteSpace(bootstrapPassword))
        {
            var hash = auth.HashPassword(bootstrapPassword);
            db.Users.Add(new User
            {
                OrganizationId = defaultOrg.Id,
                Email = bootstrapEmail.Trim().ToLowerInvariant(),
                DisplayName = "Administrator",
                PasswordHash = hash,
                Role = UserRole.Admin,
                Status = UserStatus.Active,
                // The bootstrap admin is the first hosting operator;
                // subsequent SiteAdmins are promoted from /site-admin/users.
                IsSiteAdmin = true,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(stopping);
            logger.LogInformation("Bootstrap SiteAdmin {Email} created in Default organisation.", bootstrapEmail);
        }
        else if (anyUsers && (!string.IsNullOrWhiteSpace(bootstrapEmail) || !string.IsNullOrWhiteSpace(bootstrapPassword)))
        {
            logger.LogWarning(
                "BOOTSTRAP_ADMIN_EMAIL / BOOTSTRAP_ADMIN_PASSWORD are set but at least one user already exists. "
                + "These environment variables only take effect on a fresh database; remove them once the bootstrap account is in place.");
        }

        // Single-tenant first-run seeding: name the Default (system) org and
        // claim its email domain(s) from env vars so the one organisation comes
        // up configured. Same fresh-database window as the bootstrap admin —
        // self-service org creation is off, so this is how the org gets its
        // identity. See Services/SingleTenant/SingleTenantSeeder.cs.
        var singleTenant = scope.ServiceProvider
            .GetRequiredService<ALDevToolbox.Services.SingleTenant.ISingleTenantMode>();
        if (!anyUsers && singleTenant.IsEnabled)
        {
            await ALDevToolbox.Services.SingleTenant.SingleTenantSeeder.SeedAsync(
                db,
                orgName: Environment.GetEnvironmentVariable("SINGLE_TENANT_ORG_NAME"),
                orgSlug: Environment.GetEnvironmentVariable("SINGLE_TENANT_ORG_SLUG"),
                emailDomainsCsv: Environment.GetEnvironmentVariable("SINGLE_TENANT_EMAIL_DOMAINS"),
                now: DateTime.UtcNow,
                logger: logger,
                ct: stopping);
        }

        // Reconcile durable import-job rows: re-enqueue URL downloads that were
        // queued or running at restart (the download is idempotent and we have
        // the URL on the row); flip staged-zip jobs to failed because their
        // temp file lives in container-local /tmp and is gone. The reconciler
        // returns the jobs to re-enqueue here; we push them through the queue
        // so the worker picks them up like a fresh submission.
        // Before anything is resumed: no worker is running yet, so every build clone and
        // download in the temp folder belongs to a job the last process never finished (#1133).
        ALDevToolbox.Services.ObjectExplorer.Import.LeftoverImportTempFiles.Sweep(Path.GetTempPath(), logger);

        var persistedJobs = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Import.PersistedImportJobs>();
        var queue = scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportQueue>();
        var resumable = await persistedJobs.ReconcileOnStartupAsync(stopping);
        // Not enqueued here: this runs before app.Run(), so the worker that drains the
        // bounded queue has not started yet and a 17th job would wait forever (#1107).
        ALDevToolbox.Services.ObjectExplorer.Import.ResumedImportJobs.QueueWhenStarted(app.Lifetime, queue, resumable, logger);

        // Belt-and-suspenders: any OeRelease row still left in "ingesting"
        // WITHOUT a re-queued durable job (synchronous individual-file imports
        // that crashed mid-way, or pre-table-existing in-flight rows) gets the
        // generic "interrupted by restart" treatment so the list page doesn't
        // strand them. Excluding releases with a queued job row is important —
        // the reconciler above just re-enqueued those for resume; flipping
        // them to failed here would silently undo the resume. Cross-org by
        // design — same blessed startup-maintenance category as the
        // migration/seed/bootstrap steps above, never a request.
        var resumableReleaseIds = await db.OeImportJobs
            .IgnoreQueryFilters()
            .Where(j => j.Status == "queued")
            .Select(j => j.ReleaseId)
            .ToListAsync(stopping);
        var stranded = await db.OeReleases
            // Same category 3 read: the stranded releases matching the ids above.
            .IgnoreQueryFilters()
            .Where(r => r.Status == "ingesting" && !resumableReleaseIds.Contains(r.Id))
            .ToListAsync(stopping);
        if (stranded.Count > 0)
        {
            foreach (var release in stranded)
            {
                release.Status = "failed";
                release.StatusMessage = "Import was interrupted by a server restart. Re-import to try again.";
                release.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(stopping);
            logger.LogWarning("Marked {Count} interrupted release import(s) as failed on startup.", stranded.Count);
        }

        // Prime the in-memory MCP toggle from the singleton system_settings
        // row before any request can read it. Resolved from the root provider
        // because McpAvailabilityState is a singleton — see
        // Services/Mcp/IMcpAvailability.cs for why the cache exists.
        var mcpEnabled = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.McpEnabled)
            .FirstOrDefaultAsync(stopping);
        app.Services.GetRequiredService<ALDevToolbox.Services.Mcp.McpAvailabilityState>().Set(mcpEnabled);

        // Prime the in-memory per-tool site toggles the same way, so the sidebar
        // and the route-access gate see the disabled set before the first
        // request. See Services/Tools/IToolAvailability.cs.
        var disabledTools = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DisabledTools)
            .FirstOrDefaultAsync(stopping);
        app.Services.GetRequiredService<ALDevToolbox.Services.Tools.ToolAvailabilityState>()
            .Set(ALDevToolbox.Domain.Tools.ToolCatalog.ParseDisabled(disabledTools));

        // Flip /readyz to green now that migrations, seed and bootstrap have
        // all run. Resolved from the root service provider so the flag
        // survives the scope's disposal.
        app.Services.GetRequiredService<StartupReadinessState>().MarkReady();
        logger.LogInformation("Startup complete; /readyz is now green.");

        // Stamp the app id on build artifacts retained before the column existed
        // (#901, Part 3). Off the startup path: it reads every such package once,
        // and nothing needs it before a build looks up a dependency - a build that
        // runs first only misses those older artifacts as candidates.
        _ = Task.Run(async () =>
        {
            try
            {
                await ALDevToolbox.Services.ObjectExplorer.Projects.BuildArtifactAppIdBackfill
                    .RunAsync(app.Services, logger, stopping);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                // Shutting down mid-pass; the next start picks up where this left off.
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Stamping app ids on build artifacts failed; retried on the next start.");
            }
        }, stopping);
    }

    /// <summary>
    /// Idempotently resolves the singleton system organisation, creating or
    /// stamping it as needed. Keyed on <see cref="Organization.IsSystem"/> —
    /// not on the literal <c>default</c> slug — because single-tenant first-run
    /// seeding can rename that slug; a slug-keyed lookup would miss the renamed
    /// org on a later boot and attempt a second <c>is_system</c> insert, which
    /// the <c>ix_organizations_is_system_singleton</c> unique index rejects.
    /// Only when no system org exists does it adopt an existing <c>default</c>
    /// org (the test <c>EnsureCreated</c> path) or create a fresh one.
    /// </summary>
    internal static async Task<Organization> EnsureSystemOrganizationAsync(AppDbContext db, CancellationToken ct)
    {
        var systemOrg = await db.Organizations
            .FirstOrDefaultAsync(o => o.IsSystem, ct);
        if (systemOrg is not null) return systemOrg;

        // No system org yet. Adopt an existing "default"-slug org (created
        // without the flag by the test EnsureCreated path) or create one.
        systemOrg = await db.Organizations
            .FirstOrDefaultAsync(o => o.Slug == "default", ct);
        if (systemOrg is null)
        {
            systemOrg = new Organization
            {
                Name = "Default",
                Slug = "default",
                IsPending = false,
                IsSystem = true,
                CreatedAt = DateTime.UtcNow,
            };
            db.Organizations.Add(systemOrg);
        }
        else
        {
            systemOrg.IsSystem = true;
        }
        await db.SaveChangesAsync(ct);
        return systemOrg;
    }
}
