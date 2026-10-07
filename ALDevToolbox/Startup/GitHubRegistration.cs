using ALDevToolbox.Services.GitHub;

namespace ALDevToolbox.Startup;

/// <summary>
/// The GitHub App integration: the per-organisation connection, the per-user
/// account link, and the REST client that acts as both. See
/// <c>.design/github-integration.md</c>.
/// </summary>
public static class GitHubRegistration
{
    /// <summary>Registers the GitHub services and the typed API client.</summary>
    public static IServiceCollection AddGitHub(this IServiceCollection services)
    {
        services.AddScoped<GitHubConnectionService>();
        services.AddScoped<GitHubAccessService>();
        // The read gate the repository picker and every GitHub feature share,
        // and the one write that rides it (issue #623).
        services.AddScoped<GitHubRepositoryService>();
        services.AddScoped<GitHubExtensionDeliveryService>();
        // Creating a repository and filling it with a generated workspace (#622).
        services.AddScoped<GitHubWorkspaceRepositoryService>();
        // The per-organisation standards every created repository gets: the
        // files, and the branch ruleset (#628).
        services.AddScoped<GitHubRepositoryStandardsService>();
        // The Translator's round trip: list a repository's XLIFF files, open
        // one, save it back as a pull request (issue #625).
        services.AddScoped<GitHubTranslationService>();
        // Putting a Cookbook recipe into a repository as a pull request, and
        // updating every repository that has taken it (issue #626).
        services.AddScoped<GitHubRecipeDeliveryService>();
        // Publishing a build's .app files as a Release, and staging a Release's
        // files back as a build so they can be deployed (issue #632).
        services.AddScoped<GitHubReleaseService>();
        // Finding the organisation's AL repositories that no solution tracks yet
        // (#629). Scoped like the rest: the nightly scheduler opens its own scope
        // per organisation, and the Solutions panel opens one per read.
        services.AddScoped<RepositoryDiscoveryService>();
        // Which tracked repositories still target an older Business Central than
        // the newest imported release, and the pull requests that move them on
        // (#630). Scoped: the release import enters an organisation scope and
        // calls the scan; the Solutions panel opens one per read.
        services.AddScoped<DependencyDriftService>();
        // The pull-request compile gate (#627): the check-run half is scoped
        // because it reads the build rows, the queue is a singleton because the
        // anonymous webhook endpoint and the worker both hold it, and the worker
        // is the hosted service that drains it.
        services.AddScoped<GitHubCheckRunService>();
        services.AddSingleton<GitHubWebhookQueue>();
        // The endpoint's slots for reading a push body past a megabyte (#1126, #1174).
        services.AddSingleton<GitHubWebhookBodyGate>();
        services.AddHostedService<GitHubPullRequestBuildWorker>();
        // Branch watching (#963): the same worker records push and merged-pull-
        // request deliveries through this, inside the organisation it resolved.
        services.AddScoped<GitHubBranchActivityService>();
        // Typed client on a fixed public host (api.github.com), so no SSRF
        // guard is needed - just a bounded timeout and the headers GitHub
        // requires on every request. Authorization is set per request, because
        // the same client carries both the App JWT and installation tokens.
        services.AddHttpClient<GitHubAppClient>(client =>
        {
            client.BaseAddress = new Uri(GitHubAppClient.ApiBaseUrl);
            // No client-wide timeout: it would apply to a Release asset transfer
            // as well as to a metadata read. GitHubAppClient sets a deadline per
            // call instead - thirty seconds for an ordinary call, longer for the
            // two that move a file.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            // GitHub rejects requests without a User-Agent, and pins API
            // behaviour to a dated version rather than "whatever shipped today".
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ALDevToolbox");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        })
        // Redirects are answers here, not detours. GitHub replies 302 to "is
        // this person in the organisation" when the asker is not a member, and
        // 301 to "does this repository exist" when it was renamed; following
        // either would turn a clear answer into whatever the next hop said.
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        return services;
    }
}
