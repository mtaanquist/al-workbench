using ALDevToolbox.Services.ObjectExplorer.Projects;
namespace ALDevToolbox.Startup;

/// <summary>
/// Object Explorer and everything filed under it: release import, projects and
/// pipelines, project builds, Business Central SaaS delivery and upgrades, and
/// the source/reference read side.
/// </summary>
public static class ObjectExplorerRegistration
{
    /// <summary>Registers the Object Explorer services, queues, workers and HTTP clients.</summary>
    public static IServiceCollection AddObjectExplorer(this IServiceCollection services)
    {
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.TranslationImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.CallSiteReferenceEmitter>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.CalImportService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.DvdDownloadService>();
        // Resolve + download Microsoft OnPrem artifacts straight from the CDN, and the
        // coordinator both the Artifacts tab and the auto-import scheduler call.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.BcArtifactService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.ArtifactReleaseImporter>();
        // In-process hand-off + worker for the DVD-scale imports (folder-ZIP upload,
        // URL download) so the admin isn't held on the page while they ingest.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportQueue>();
        // Owns the on-disk AL compiler volume; singleton so its provisioning gate is shared.
        // AL_COMPILER_* read once here rather than in the provisioner's
        // constructor; see #733 and Services/Configuration/.
        services.AddSingleton(sp => ALDevToolbox.Services.Configuration.AlCompilerOptions
            .FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Projects.AlCompilerProvisioner>();
        // Dependency symbols from Microsoft's public AppSource / MSSymbols feeds, cached on
        // the same app-altool volume; singleton so its cache gate is shared (#901).
        services.AddSingleton(sp => ALDevToolbox.Services.Configuration.AlSymbolFeedOptions
            .FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Projects.AlSymbolFeedResolver>();
        // Business Central artifacts project builds downloaded, kept on the same volume so
        // the next build of that version skips the download; singleton for its gates (#1140).
        services.AddSingleton(sp => ALDevToolbox.Services.Configuration.BcArtifactCacheOptions
            .FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Import.BcArtifactCache>();
        // Redirects are followed by the resolver itself (the .nupkg answers 303 to a blob
        // URL), so it can refuse a hop off HTTPS; the handler must not follow them first.
        services.AddHttpClient(ALDevToolbox.Services.ObjectExplorer.Projects.AlSymbolFeedResolver.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromMinutes(5);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.PersistedImportJobs>();
        // The upload form's policy: which ingest path a submission takes, what gets
        // staged to disk, and what goes on the queue. The endpoints only read the
        // form and redirect on the outcome.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportRequestService>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportWorker>();
        // Builds have their own line (#1137). Every possible worker is registered, and the
        // queue's limit decides how many build at once: the site setting, else
        // OE_BUILD_CONCURRENCY, changed at runtime without a restart (#1164).
        services.AddSingleton(_ => new ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue(
            ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue
                .Concurrency(Environment.GetEnvironmentVariable("OE_BUILD_CONCURRENCY"))));
        // What the container reports about processor and memory, and the latest warning
        // that builds were short of either (#1169).
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Import.ContainerResources>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Import.BuildResourceState>();
        for (var slot = 1; slot <= ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue.MaxConcurrency; slot++)
        {
            var workerSlot = slot;
            services.AddSingleton<IHostedService>(sp =>
                ActivatorUtilities.CreateInstance<ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildWorker>(sp, workerSlot));
        }
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Import.ReleaseManagementService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectCustomerInfoService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectFollowService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.CustomerModuleService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.PipelineService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.ReleasePipelineService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.ProjectAccess>();
        // Business Central SaaS delivery: connection config + the API seams. The token
        // cache is a singleton (shared in-memory bearer cache, like the compiler gate);
        // the clients are thin HTTP seams; the connection service is request-scoped.
        // See .design/saas-delivery.md.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        // Singleton so a panel read is shared across requests rather than per-request. See
        // BcPanelCache for why the window is short and why the caller must gate access first.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient, ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient, ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        // The delivery worker only needs a token from the connection service; expose that
        // narrow seam so the publish orchestration is testable without the OAuth round-trip.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IDeliveryTokenSource>(
            sp => sp.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>());
        // SaaS delivery (manual publish): the create/run orchestration is scoped; the queue is
        // a singleton hand-off to the hosted worker, mirroring ProjectDiscoveryQueue/Worker. The
        // worker runs the upload→install→poll publish off the request thread. No external queue.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryService>();
        // The cross-solution delivery feed behind list_recent_deployments; read-only.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryFeedService>();
        // The Pipelines dashboard at /pipelines (#955): the two lists' numbers on one page; read-only.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.PipelinesDashboardService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryQueue>();
        // Deployments to different environments run side by side (#1139).
        for (var slot = 1; slot <= ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryWorker.Lanes; slot++)
        {
            var workerSlot = slot;
            services.AddSingleton<IHostedService>(sp =>
                ActivatorUtilities.CreateInstance<ALDevToolbox.Services.ObjectExplorer.Delivery.DeliveryWorker>(sp, workerSlot));
        }
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ArtifactService>();
        // Project-build pipeline: the compile/ingest service, its release coordinator,
        // and the (stateless) external-process seam for git + alc.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectBuildService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.CloneCredentialResolver>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectBuildImporter>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.PreviewCheckService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.PushBuildService>();
        // Whether a pipeline's last build is still what its watched branch holds (#963).
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.BuildFreshnessService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.IProcessRunner, ALDevToolbox.Services.ObjectExplorer.ProcessRunner>();
        // Background warm of the per-project discovered-extensions cache (the pipeline
        // editor's checklist): an in-process queue + worker, mirroring the release-import
        // pair. In-memory dedupe, no external dependency.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryQueue>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryService>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Projects.ProjectDiscoveryWorker>();
        // Background re-read of a project's BC environments, which re-mirrors the next
        // platform update per environment. Same in-process queue + worker shape; fed by the
        // nightly sweep (see BackgroundWorkerRegistration) and by an on-demand refresh.
        // See .design/saas-delivery.md.
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshQueue>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentRefreshWorker>();
        // The read side of the Upgrades page: the cross-project fleet list, plus the on-demand
        // hand-off into the refresh queue above. Request-scoped like every project-scoped read.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeFleetService>();
        // The write side: the upgrade actions the team asks for now or books for an agreed slot,
        // their per-environment activity feed, and the worker that fires the booked ones. The
        // worker polls the table rather than a channel, so a slot survives a restart.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionService>();
        services.AddHostedService<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionWorker>();
        // Planned upgrades (#984): the named waves the team moves, starts and checks
        // together. Reads the fleet through UpgradeFleetService, so it is scoped like it.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.EnvironmentUpgradeService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.TranslationQueryService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ReleaseComparisonService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectSearchService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceQueryService>();
        // The project-visibility fence both source surfaces share, and the two
        // halves of the source page: the file viewer and the explorer tree.
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.SourceVisibility>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.SourceViewerService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ExplorerTreeService>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceResolver>();
        services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Explore.ReferenceSessionService>();
        services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Explore.ObjectExplorerLinks>();
        // Business Central delivery client (token + Admin Center + automation APIs).
        // Fixed public Microsoft hosts (login.microsoftonline.com,
        // api.businesscentral.dynamics.com), so no SSRF guard is needed — just a bounded
        // timeout. The per-request bearer + URL are set by the BC clients. See
        // .design/saas-delivery.md.
        services.AddTransient<ALDevToolbox.Services.ObjectExplorer.Bc.BcThrottleHandler>();
        services.AddHttpClient(ALDevToolbox.Services.ObjectExplorer.Bc.BcConstants.HttpClientName, client =>
        {
            // Above the throttle handler's longest wait plus two attempts.
            client.Timeout = TimeSpan.FromSeconds(180);
        })
        // A read Business Central throttles is retried once after the wait it asked for.
        .AddHttpMessageHandler<ALDevToolbox.Services.ObjectExplorer.Bc.BcThrottleHandler>();

        // DVD download client for the Object Explorer "import release from URL" flow.
        // Same SSRF guard as the CIMD client (dial only publicly routable IPs), but
        // redirects are allowed: Microsoft download URLs commonly 302 to a CDN, and the
        // ConnectCallback re-checks every hop's IP so a redirect still can't reach an
        // internal target. The long timeout covers the multi-GB body.
        services.AddHttpClient(nameof(ALDevToolbox.Services.ObjectExplorer.Import.DvdDownloadService), client =>
            {
                client.Timeout = TimeSpan.FromMinutes(20);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                ConnectCallback = ALDevToolbox.Services.OAuth.SsrfGuard.ConnectAsync,
                // SocketsHttpHandler defaults PooledConnectionLifetime to infinite, so
                // a cached TCP connection that's silently gone half-dead (CDN edge
                // dropped state, NAT timeout, …) gets reused for the next request and
                // stalls mid-body. Recycle after 2 minutes so DVD imports — which are
                // far apart and intolerant of stale state — always dial a fresh
                // connection. This is the documented best-practice setting; see
                // https://learn.microsoft.com/dotnet/fundamentals/networking/http/httpclient-guidelines.
                // The range-resume retry in CopyWithRetriesAsync also leans on this:
                // when a body stalls, the retry's new SendAsync gets a fresh
                // connection rather than the same stuck pipe.
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            });
        return services;
    }
}
