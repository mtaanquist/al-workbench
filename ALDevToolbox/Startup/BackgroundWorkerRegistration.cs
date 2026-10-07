using ALDevToolbox.Services;
using ALDevToolbox.Services.Backups;

namespace ALDevToolbox.Startup;

/// <summary>
/// The polled schedulers. Queue-backed workers are registered next to the
/// services they drain (see Services/Workers/ for the shared base classes).
/// </summary>
public static class BackgroundWorkerRegistration
{
    /// <summary>Registers the scheduled background work.</summary>
    public static IServiceCollection AddBackgroundWorkers(this IServiceCollection services, bool singleTenantMode)
    {
        // Every scheduler below honours its own DISABLE_* opt-out inside the service
        // (see Services/Workers/PolledScheduler.cs), so registration is unconditional
        // except where a second condition applies.
        services.AddHostedService<BackupScheduler>();
        // Daily VACUUM over the Object Explorer content tables.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Import.ObjectExplorerVacuumScheduler>();
        // Refreshes per-org storage snapshots so StorageBar reads a cached row rather
        // than counting every tenanted table on each navigation.
        // Single-tenant mode hides the StorageBar entirely, so there's nothing to feed —
        // skip the timer there too.
        if (!singleTenantMode)
        {
            services.AddHostedService<ALDevToolbox.Services.Organizations.UsageSnapshotScheduler>();
        }
        // Daily import of new Microsoft OnPrem releases for orgs that opted in
        // (OrganizationSettings.AutoImportReleasesEnabled); runs in single- and
        // multi-tenant alike.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseAutoImportScheduler>();
        // Enqueues scheduled SaaS deliveries when due, and fails restart-orphaned ones on its
        // first sweep.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryScheduler>();
        // Nightly preview check: builds each opted-in pipeline against the next minor
        // and next major Business Central previews, as the person who turned it on.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Projects.PreviewCheckScheduler>();
        // Nightly sweep that re-reads every BC-connected project's environments, keeping the
        // mirrored next-platform-update columns fresh for the fleet view.
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshScheduler>();
        // Nightly pass over every GitHub-connected organisation's tracked repositories,
        // learning from the translation files that have changed since the last one.
        services.AddHostedService<ALDevToolbox.Services.Translation.TranslationMemoryIngestScheduler>();
        // Mirrors Microsoft's BCQuality knowledge base into Postgres for the MCP
        // tools: a first ingest shortly after startup, then daily. With the refresh
        // disabled the tools report an empty knowledge base rather than failing.
        // See .design/bcquality.md.
        services.AddHostedService<ALDevToolbox.Services.BcQuality.BcQualityRefreshScheduler>();
        // Daily sweep of each org's connected GitHub organisation for AL repositories
        // no solution tracks yet, so the Solutions page can offer them without probing
        // GitHub on a page render. See .design/github-integration-phase2.md.
        services.AddHostedService<ALDevToolbox.Services.GitHub.RepositoryDiscoveryScheduler>();
        // Asks GitHub to resend webhook deliveries the endpoint refused or never
        // answered, and closes pull-request builds a restart cut short (#1121).
        services.AddHostedService<ALDevToolbox.Services.GitHub.GitHubWebhookRecoveryScheduler>();
        // Periodic prune of old login_attempts rows so the table doesn't grow
        // unbounded (the rate-limiter only reads a ~15-minute window). See issue #403.
        services.AddHostedService<ALDevToolbox.Services.Account.LoginAttemptPruneScheduler>();
        // Sends the queued transactional email, retries what fails, and prunes
        // what it no longer needs to keep. See issue #790.
        services.AddHostedService<ALDevToolbox.Services.EmailOutboxScheduler>();
        // Sends the daily and weekly notification digests, and drops items that
        // waited too long. See .design/notifications.md.
        services.AddHostedService<ALDevToolbox.Services.Notifications.NotificationDigestScheduler>();
        // Adds each newly shipped Business Central release wave to every org's
        // application-version catalogue, read daily off the Microsoft symbol feed.
        services.AddHostedService<ALDevToolbox.Services.Templates.ApplicationVersionSyncScheduler>();
        return services;
    }
}
