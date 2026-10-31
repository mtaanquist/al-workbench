using ALDevToolbox.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ALDevToolbox.Services.ObjectExplorer.Import;

namespace ALDevToolbox.Startup;

/// <summary>The EF Core context and its audit and maintenance interceptors.</summary>
public static class DatabaseRegistration
{
    /// <summary>Registers <see cref="AppDbContext"/> against the configured Postgres.</summary>
    public static IServiceCollection AddAppDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        // Postgres connection string (M16). `ConnectionStrings__DefaultConnection`
        // is the deployment knob; compose.yml builds it from POSTGRES_* env vars and
        // passes it through. There is no fallback DSN — failing fast surfaces a
        // missing config sooner than discovering it at first query.
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. Set ConnectionStrings__DefaultConnection.");
        services.AddScoped<AuditInterceptor>();
        services.AddScoped<MaintenanceDatabaseGuard>();
        // The model snapshot stays a hand-rolled affair (the InitialCreate designer
        // file's BuildTargetModel is intentionally empty), so EF's pending-model-
        // changes guard would fire on every MigrateAsync. Real schema drift still
        // surfaces when the migration itself runs.
        // No MaxBatchSize override: the Npgsql default (1000) applies. The old cap of
        // 100 existed because oe_module_files carried the source text inline (50 KB a
        // row), so a full batch built a multi-megabyte command. That blob moved to the
        // content-addressed oe_file_contents table, which
        // OeIngestHelpers.UpsertFileContentsAsync writes with a single raw unnest
        // INSERT that never enters an EF batch. What is left on the EF ingest path is
        // narrow, high-volume rows (oe_module_references / _symbols / _variables /
        // _objects), where the cap only multiplied the round-trips by ten. See #688.

        // Registered as a *factory* rather than a plain AddDbContext (#741).
        // AddDbContextFactory also TryAdds AppDbContext itself as a scoped
        // service, so every existing constructor injection keeps the one
        // per-circuit context it has always had. The factory exists for the
        // read-only audit reads behind <AuditHistoryPanel>, which run
        // concurrently with the page's own save: a DbContext allows a single
        // operation at a time, so sharing the circuit's context between the
        // panel's load and the form's UpdateAsync threw "A second operation was
        // started on this context instance". AuditService opens its own
        // short-lived context per read instead; see Services/AuditService.cs.
        //
        // The lifetime must be Scoped: AppDbContext's constructor takes the
        // scoped IOrganizationContext (the tenant fence), and the options lambda
        // resolves the scoped AuditInterceptor. A singleton factory could
        // resolve neither.
        services.AddDbContextFactory<AppDbContext>((sp, options) =>
            options
                .UseNpgsql(connectionString, npgsql => npgsql
                    .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .AddInterceptors(
                    sp.GetRequiredService<AuditInterceptor>(),
                    // Keeps ordinary work off the database during a restore (#1571).
                    sp.GetRequiredService<MaintenanceDatabaseGuard>()),
            ServiceLifetime.Scoped);
        return services;
    }
}
