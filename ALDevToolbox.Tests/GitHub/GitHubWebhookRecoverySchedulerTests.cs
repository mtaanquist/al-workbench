using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// What happens to webhook deliveries the endpoint could not take, and to
/// pull-request builds a restart cut short (#1121).
///
/// <para>GitHub does not resend a delivery it logged as failed, so before this
/// a full queue, an oversized push or a restart lost the push or the pull-request
/// check for good. The scheduler reads the App's delivery log and asks for each
/// failed event again; these tests pin which events it asks for, how often, and
/// when it holds back.</para>
/// </summary>
public sealed class GitHubWebhookRecoverySchedulerTests : IDisposable
{
    private const long InstallationId = 42;
    private const string Repository = "cronus-dk/customer-app";

    private readonly TestDb _db = new();
    private readonly GitHubWebhookQueue _queue = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Failed_push_and_pull_request_deliveries_are_asked_for_again_oldest_first()
    {
        await ConfigureDeploymentAsync();
        var now = DateTime.UtcNow;
        var api = ApiWithLog(
            Delivery(9, "guid-late-push", now.AddMinutes(-1), 503, "push"),
            // Refused, then got through on GitHub's own Redeliver button.
            Delivery(8, "guid-recovered", now.AddMinutes(-2), 200, "push", redelivery: true),
            Delivery(7, "guid-recovered", now.AddMinutes(-20), 503, "push"),
            // Answered 401: our secret did not match. Sending it again changes nothing.
            Delivery(6, "guid-bad-signature", now.AddMinutes(-25), 401, "push"),
            // Not an event the endpoint acts on.
            Delivery(5, "guid-issue", now.AddMinutes(-30), 503, "issues"),
            // Over the size cap, and nobody answered at all.
            Delivery(4, "guid-big-push", now.AddMinutes(-40), 413, "push"),
            Delivery(3, "guid-pr", now.AddMinutes(-50), 0, "pull_request", action: "synchronize"));

        await using var provider = BuildProvider(api);
        var resent = await NewScheduler(provider).RedeliverFailedAsync(CancellationToken.None);

        resent.Should().Be(3);
        RedeliveredIds(api).Should().Equal([3, 4, 9], "the oldest event first, so pushes arrive in order");
    }

    [Fact]
    public async Task The_log_is_read_page_by_page_until_it_reaches_what_is_too_old_to_resend()
    {
        await ConfigureDeploymentAsync();
        var now = DateTime.UtcNow;
        var api = PagedApi(now);

        await using var provider = BuildProvider(api);
        var scheduler = NewScheduler(provider);

        (await scheduler.RedeliverFailedAsync(CancellationToken.None)).Should().Be(2);
        RedeliveredIds(api).Should().Equal([1, 2], "the delivery past the three-day window is not asked for");
    }

    [Fact]
    public async Task A_failed_event_is_asked_for_once_per_sweep_and_given_up_after_the_limit()
    {
        await ConfigureDeploymentAsync();
        var now = DateTime.UtcNow;
        var attempts = 0;
        var api = new FakeGitHubApi()
            // Every resend is refused again, and shows up as a new log entry.
            .On(HttpMethod.Get, "/app/hook/deliveries", _ =>
            {
                var entries = Enumerable.Range(0, attempts + 1)
                    .Select(i => Delivery(100 + i, "guid-stuck", now.AddSeconds(i), 503, "push", redelivery: i > 0))
                    .Reverse()
                    .ToArray();
                return (HttpStatusCode.OK, LogJson(entries));
            })
            .On(HttpMethod.Post, "/app/hook/deliveries/", _ =>
            {
                attempts++;
                return (HttpStatusCode.Accepted, "{}");
            });

        await using var provider = BuildProvider(api);
        var scheduler = NewScheduler(provider);
        for (var sweep = 0; sweep < GitHubWebhookRecoveryScheduler.MaxAttempts + 3; sweep++)
        {
            await scheduler.RedeliverFailedAsync(CancellationToken.None);
        }

        attempts.Should().Be(GitHubWebhookRecoveryScheduler.MaxAttempts);
        RedeliveredIds(api).Should().OnlyHaveUniqueItems("an entry already acted on is never asked for twice");
    }

    [Fact]
    public async Task Nothing_is_asked_for_while_the_queue_is_still_well_behind()
    {
        // A resend lands in the same queue and would only be refused again.
        await ConfigureDeploymentAsync();
        var api = ApiWithLog(Delivery(1, "guid-push", DateTime.UtcNow.AddMinutes(-5), 503, "push"));
        var filler = new GitHubPullRequestJob(1, "a/b", "https://github.com/a/b.git", 1, "abc1234", "x", "main", "d");
        for (var i = 0; i <= GitHubWebhookQueue.Capacity / 2; i++) _queue.TryEnqueue(filler);

        await using var provider = BuildProvider(api);
        (await NewScheduler(provider).RedeliverFailedAsync(CancellationToken.None)).Should().Be(0);

        api.Calls.Should().BeEmpty("the delivery log is not even read");
    }

    [Fact]
    public async Task A_deployment_without_a_webhook_secret_asks_for_nothing()
    {
        await ConfigureDeploymentAsync(webhookSecret: null);
        var api = ApiWithLog(Delivery(1, "guid-push", DateTime.UtcNow.AddMinutes(-5), 401, "push"));

        await using var provider = BuildProvider(api);
        (await NewScheduler(provider).RedeliverFailedAsync(CancellationToken.None)).Should().Be(0);

        api.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(408, true)]
    [InlineData(413, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(404, false)]
    public void Only_answers_a_resend_could_change_are_resent(int status, bool resent) =>
        GitHubWebhookRecoveryScheduler.IsWorthResending(status).Should().Be(resent);

    [Fact]
    public void The_next_cursor_is_read_from_the_link_header()
    {
        GitHubAppClient.NextCursor(
            "<https://api.github.com/app/hook/deliveries?per_page=100&cursor=v1_123%3D>; rel=\"next\"")
            .Should().Be("v1_123=");
        GitHubAppClient.NextCursor(
            "<https://api.github.com/app/hook/deliveries?per_page=100&cursor=abc>; rel=\"prev\"")
            .Should().BeNull();
    }

    [Fact]
    public async Task A_pull_request_build_a_restart_cut_short_is_failed_and_its_check_run_closed()
    {
        await ConfigureDeploymentAsync();
        await ConnectAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson("ghs_installation"))
            .On(new HttpMethod("PATCH"), $"/repos/{Repository}/check-runs/555", HttpStatusCode.OK, "{\"id\":555}");
        var (projectId, repositoryId) = await SeedProjectAsync();
        var before = DateTime.UtcNow.AddMinutes(-10);
        var interrupted = await SeedBuildAsync(projectId, repositoryId, ProjectBuildTrigger.PullRequest, ProjectBuildStatus.Building, before, 555);
        var queued = await SeedBuildAsync(projectId, repositoryId, ProjectBuildTrigger.PullRequest, ProjectBuildStatus.Queued, before, null);
        // A manual build is resumed from its durable job row; not this sweep's business.
        var manual = await SeedBuildAsync(projectId, null, ProjectBuildTrigger.Manual, ProjectBuildStatus.Queued, before, null);

        await using var provider = BuildProvider(api);
        var scheduler = NewScheduler(provider);
        // Queued by this process after it started: still in memory, still going to run.
        var current = await SeedBuildAsync(projectId, repositoryId, ProjectBuildTrigger.PullRequest, ProjectBuildStatus.Queued, DateTime.UtcNow.AddMinutes(1), null);

        (await scheduler.CloseOrphanedPullRequestBuildsAsync(CancellationToken.None)).Should().Be(2);

        await using var ctx = _db.NewContext();
        var statuses = await ctx.OeProjectBuilds.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b);
        statuses[interrupted].Status.Should().Be(ProjectBuildStatus.Failed);
        statuses[interrupted].FailureMessage.Should().Be(GitHubWebhookRecoveryScheduler.RestartedMessage);
        statuses[queued].Status.Should().Be(ProjectBuildStatus.Failed);
        statuses[manual].Status.Should().Be(ProjectBuildStatus.Queued);
        statuses[current].Status.Should().Be(ProjectBuildStatus.Queued);

        var patch = JsonDocument.Parse(api.Bodies.Single(b => b.Call.StartsWith("PATCH")).Body).RootElement;
        patch.GetProperty("conclusion").GetString().Should().Be(GitHubCheckConclusion.Neutral);
        patch.GetProperty("output").GetProperty("title").GetString().Should().Be("The build was interrupted");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private GitHubWebhookRecoveryScheduler NewScheduler(IServiceProvider provider) =>
        new(provider, _queue, TimeProvider.System,
            NullLogger<GitHubWebhookRecoveryScheduler>.Instance, new WorkerHeartbeatRegistry());

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
        services.AddScoped<GitHubCheckRunService>();
        _db.AddGitHubServices(services, api);
        return services.BuildServiceProvider();
    }

    private static FakeGitHubApi PagedApi(DateTime now)
    {
        var first = LogJson(Delivery(2, "guid-newer", now.AddHours(-1), 503, "push"));
        var second = LogJson(
            Delivery(1, "guid-older", now.AddDays(-1), 502, "push"),
            // Older than GitHub lets anybody redeliver.
            Delivery(0, "guid-ancient", now.AddDays(-4), 503, "push"));
        return new FakeGitHubApi()
            .OnWithHeaders(HttpMethod.Get, "/app/hook/deliveries", request =>
                request.RequestUri!.Query.Contains("cursor=v1_page2")
                    // The last page names no next one, as GitHub's does not.
                    ? (HttpStatusCode.OK, second, [])
                    : (HttpStatusCode.OK, first,
                        [("Link", "<https://api.github.com/app/hook/deliveries?per_page=100&cursor=v1_page2>; rel=\"next\"")]))
            .On(HttpMethod.Post, "/app/hook/deliveries/", HttpStatusCode.Accepted, "{}");
    }

    private static FakeGitHubApi ApiWithLog(params string[] entries) =>
        new FakeGitHubApi()
            .On(HttpMethod.Get, "/app/hook/deliveries", HttpStatusCode.OK, LogJson(entries))
            .On(HttpMethod.Post, "/app/hook/deliveries/", HttpStatusCode.Accepted, "{}");

    private static List<long> RedeliveredIds(FakeGitHubApi api) =>
        api.Calls
            .Where(c => c.StartsWith("POST ") && c.EndsWith("/attempts"))
            .Select(c => long.Parse(c.Split('/')[^2]))
            .ToList();

    private static string LogJson(params string[] entries) => "[" + string.Join(',', entries) + "]";

    private static string Delivery(
        long id, string guid, DateTime deliveredAt, int status, string eventName,
        bool redelivery = false, string? action = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["guid"] = guid,
            ["delivered_at"] = deliveredAt.ToString("O"),
            ["redelivery"] = redelivery,
            ["duration"] = 0.1,
            ["status"] = status == 0 ? "Timed out" : status.ToString(),
            ["status_code"] = status,
            ["event"] = eventName,
            ["action"] = action,
            ["installation_id"] = InstallationId,
            ["repository_id"] = 1,
        });

    private async Task<(int ProjectId, int RepositoryId)> SeedProjectAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Retail",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var repository = new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Provider = RepositoryProvider.GitHub,
            Url = $"https://github.com/{Repository}.git",
            DisplayName = "customer-app",
        };
        ctx.OeProjectRepositories.Add(repository);
        // A second repository, so the check run can only be found by the one the build names.
        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus-dk/shared-library.git",
            DisplayName = "shared-library",
        });
        await ctx.SaveChangesAsync();
        return (project.Id, repository.Id);
    }

    private async Task<int> SeedBuildAsync(
        int projectId, int? repositoryId, string trigger, string status, DateTime startedAt, long? checkRunId)
    {
        await using var ctx = _db.NewContext();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Status = status,
            Trigger = trigger,
            PullRequestNumber = trigger == ProjectBuildTrigger.PullRequest ? 7 : null,
            HeadSha = trigger == ProjectBuildTrigger.PullRequest ? "abc1234" : null,
            HeadRepositoryId = repositoryId,
            CheckRunId = checkRunId,
            StartedAt = startedAt,
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private async Task ConnectAsync()
    {
        await using var ctx = _db.NewContext();
        var settings = await ctx.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == TestDb.DefaultOrgId);
        if (settings is null)
        {
            settings = new OrganizationSettings { OrganizationId = TestDb.DefaultOrgId, UpdatedAt = DateTime.UtcNow };
            ctx.OrganizationSettings.Add(settings);
        }
        settings.GitHubInstallationId = InstallationId;
        settings.GitHubOrgLogin = "cronus-dk";
        settings.GitHubConnectedAt = DateTime.UtcNow;
        await ctx.SaveChangesAsync();
    }

    private async Task ConfigureDeploymentAsync(string? webhookSecret = "hook-secret")
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false,
            WebhookSecret: webhookSecret, ClearWebhookSecret: false));
    }
}
