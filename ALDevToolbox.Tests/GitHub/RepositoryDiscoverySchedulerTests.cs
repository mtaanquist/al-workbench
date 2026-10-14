using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The nightly sweep behind repository discovery (issue #629).
///
/// <para>The rule worth a test here is the one the milestone promised not to
/// break: the sweep visits every organisation without escaping the tenant
/// filter. It enumerates <c>organizations</c> - a table that carries no filter
/// to escape - and then enters an <see cref="AmbientOrganizationScope"/> per
/// organisation, so each organisation's candidates are written and read exactly
/// as a request of that organisation's own would. Two connected organisations
/// with different repositories are the way to see that: each ends up with its
/// own rows and neither can see the other's.</para>
/// </summary>
public sealed class RepositoryDiscoverySchedulerTests : IDisposable
{
    private const int SecondOrgId = 4629;
    private const long FirstInstallationId = 42;
    private const long SecondInstallationId = 43;
    private const string FirstRepo = "cronus-dk/payment-import";
    private const string SecondRepo = "cronus-no/warehouse-ext";

    private const string Manifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS","version":"1.0.0.0"}
        """;

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task The_sweep_visits_every_connected_organisation_and_keeps_their_findings_apart()
    {
        await ConfigureDeploymentAsync();
        await SeedSecondOrganisationAsync();
        await ConnectAsync(TestDb.DefaultOrgId, FirstInstallationId, "cronus-dk");
        await ConnectAsync(SecondOrgId, SecondInstallationId, "cronus-no");

        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{FirstInstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson("ghs_first"))
            .On(HttpMethod.Post, $"/app/installations/{SecondInstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson("ghs_second"))
            // Which repositories come back depends on which installation asked,
            // and the token is the only thing that distinguishes the two calls -
            // which is the point: each organisation is swept as itself.
            .On(HttpMethod.Get, "/installation/repositories", request =>
                (HttpStatusCode.OK, FakeGitHubApi.InstallationRepositoriesJson(
                    request.Headers.Authorization?.Parameter == "ghs_first" ? FirstRepo : SecondRepo)))
            .On(HttpMethod.Get, $"/repos/{FirstRepo}/git/trees/main", HttpStatusCode.OK, TreeJson("app.json"))
            .On(HttpMethod.Get, $"/repos/{FirstRepo}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", Manifest))
            .On(HttpMethod.Get, $"/repos/{SecondRepo}/git/trees/main", HttpStatusCode.OK, TreeJson("app.json"))
            .On(HttpMethod.Get, $"/repos/{SecondRepo}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", Manifest));

        await using var provider = BuildProvider(api);
        var scheduler = new RepositoryDiscoveryScheduler(
            provider, TimeProvider.System,
            NullLogger<RepositoryDiscoveryScheduler>.Instance,
            new WorkerHeartbeatRegistry());

        var found = await scheduler.SweepAsync(CancellationToken.None);

        found.Should().Be(2);
        (await CandidatesOfAsync(TestDb.DefaultOrgId)).Should().Equal(FirstRepo);
        (await CandidatesOfAsync(SecondOrgId)).Should().Equal(SecondRepo);
    }

    [Fact]
    public async Task An_organisation_that_fails_does_not_stop_the_others()
    {
        await ConfigureDeploymentAsync();
        await SeedSecondOrganisationAsync();
        await ConnectAsync(TestDb.DefaultOrgId, FirstInstallationId, "cronus-dk");
        await ConnectAsync(SecondOrgId, SecondInstallationId, "cronus-no");

        var api = new FakeGitHubApi()
            // The first organisation's installation has been suspended on GitHub.
            .On(HttpMethod.Post, $"/app/installations/{FirstInstallationId}/access_tokens",
                HttpStatusCode.Forbidden, "{\"message\":\"This installation has been suspended\"}")
            .On(HttpMethod.Post, $"/app/installations/{SecondInstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson("ghs_second"))
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(SecondRepo))
            .On(HttpMethod.Get, $"/repos/{SecondRepo}/git/trees/main", HttpStatusCode.OK, TreeJson("app.json"))
            .On(HttpMethod.Get, $"/repos/{SecondRepo}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", Manifest));

        await using var provider = BuildProvider(api);
        var scheduler = new RepositoryDiscoveryScheduler(
            provider, TimeProvider.System,
            NullLogger<RepositoryDiscoveryScheduler>.Instance,
            new WorkerHeartbeatRegistry());

        var found = await scheduler.SweepAsync(CancellationToken.None);

        found.Should().Be(1);
        (await CandidatesOfAsync(SecondOrgId)).Should().Equal(SecondRepo);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    /// <summary>
    /// A container shaped like the app's own: the organisation in scope comes
    /// from <see cref="HttpOrganizationContext"/>, which with no request falls
    /// back to the ambient scope the scheduler enters.
    /// </summary>
    private ServiceProvider BuildProvider(FakeGitHubApi api)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        services.AddScoped<IOrganizationContext, HttpOrganizationContext>();
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        services.AddSingleton(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        _db.AddStorageServices(services);
        services.AddScoped<OrganizationConfigService>();
        services.AddScoped<ProjectAccess>();
        services.AddSingleton(new ProjectDiscoveryQueue());
        services.AddScoped<ProjectDiscoveryService>();
        services.AddScoped<ProjectService>();
        _db.AddRepositoryReadProbe(services);
        _db.AddGitHubServices(services, api);
        return services.BuildServiceProvider();
    }

    private static string TreeJson(params string[] paths) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', paths.Select(p => $"{{\"path\":\"{p}\",\"type\":\"blob\",\"sha\":\"blob-{p.GetHashCode():x}\"}}"))
        + "]}";

    /// <summary>
    /// One organisation's candidates, read as that organisation - the fixture's
    /// own context is scoped to the default org, and the query filter is exactly
    /// what this test wants left in force.
    /// </summary>
    private async Task<List<string>> CandidatesOfAsync(int organizationId)
    {
        var previous = _db.OrgContext.CurrentOrganizationId;
        _db.OrgContext.CurrentOrganizationId = organizationId;
        try
        {
            await using var ctx = _db.NewContext();
            return await ctx.GitHubRepositoryCandidates
                .OrderBy(c => c.FullName)
                .Select(c => c.FullName)
                .ToListAsync();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = previous;
        }
    }

    private async Task SeedSecondOrganisationAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.Organizations.Add(new Organization
        {
            Id = SecondOrgId,
            Name = "CRONUS Norge",
            Slug = "cronus-norge",
            IsPending = false,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ConnectAsync(int organizationId, long installationId, string orgLogin)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = organizationId,
            GitHubInstallationId = installationId,
            GitHubOrgLogin = orgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ConfigureDeploymentAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }
}
