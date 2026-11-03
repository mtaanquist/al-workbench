using ALDevToolbox.Services.Migration;

namespace ALDevToolbox.Startup;

/// <summary>Migrate Repositories: moving Azure DevOps repositories into one templated GitHub repository (#1698).</summary>
public static class MigrationRegistration
{
    /// <summary>Registers the Azure DevOps client, the preview queue and its worker.</summary>
    public static IServiceCollection AddRepositoryMigration(this IServiceCollection services)
    {
        // Typed client on a fixed host, so the address an Admin pastes only ever
        // picks a path there. Redirects stay off: Azure DevOps answers a refused
        // token with a redirect to its sign-in page, which is an answer, not a detour.
        services.AddHttpClient<AzureDevOpsClient>(client =>
        {
            client.BaseAddress = new Uri($"https://{AzureDevOpsProject.Host}");
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox");
        })
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<RepositoryMigrationQueue>();
        services.AddScoped<MigrationWorkspaceBuilder>();
        services.AddScoped<RepositoryMigrationPreviewer>();
        services.AddScoped<RepositoryMigrationService>();
        services.AddHostedService<RepositoryMigrationWorker>();
        return services;
    }
}
