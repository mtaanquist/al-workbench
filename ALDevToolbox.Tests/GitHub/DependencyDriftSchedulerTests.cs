using System.Net;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The nightly drift pass (issue #1104): when it runs, and what a solution shows when
/// the person its update pull requests are opened as is gone.
/// </summary>
public sealed class DependencyDriftSchedulerTests : IDisposable
{
    private static readonly DateTime Tonight = new(2026, 10, 8, DependencyDriftScheduler.SweepHourUtc, 10, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void It_runs_once_inside_its_hour()
    {
        var today = DateOnly.FromDateTime(Tonight);
        DependencyDriftScheduler.IsDue(Tonight, null).Should().BeTrue();
        DependencyDriftScheduler.IsDue(Tonight, today).Should().BeFalse("it already ran tonight");
        DependencyDriftScheduler.IsDue(Tonight.AddHours(1), null).Should().BeFalse("it is past the hour");
        DependencyDriftScheduler.IsDue(Tonight.AddDays(1), today).Should().BeTrue();
    }

    [Theory]
    [InlineData(UserStatus.Disabled)]
    [InlineData(null)]
    public async Task A_solution_whose_person_is_gone_says_so(UserStatus? ownerStatus)
    {
        var projectId = await SeedAsync(ownerStatus);

        (await NewScheduler().SweepAsync(CancellationToken.None)).Should().Be(0);

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).AutoUpdatePullRequestsBlocked
            .Should().Be(DependencyDriftService.AutomaticNoOwnerMessage);
    }

    [Fact]
    public async Task A_run_that_goes_through_clears_what_held_the_last_one_up()
    {
        var projectId = await SeedAsync(UserStatus.Active, blocked: "stale");
        _db.OrgContext.CurrentUserId = await OwnerIdAsync(projectId);

        await NewScheduler().SweepAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).AutoUpdatePullRequestsBlocked
            .Should().BeNull("nothing was behind and nothing stopped the run");
    }

    [Fact]
    public async Task A_run_that_failed_unexpectedly_keeps_what_the_solution_said_before()
    {
        // No signed-in person reaches the service, which it refuses outright - a failure
        // for the log, not a reason to show on the solution or to clear the old one.
        var projectId = await SeedAsync(UserStatus.Active, blocked: "stale");
        _db.OrgContext.CurrentUserId = null;

        await NewScheduler().SweepAsync(CancellationToken.None);

        await using var read = _db.NewContext();
        (await read.OeProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).AutoUpdatePullRequestsBlocked
            .Should().Be("stale");
    }

    [Fact]
    public async Task A_rate_limited_person_stops_for_the_night_across_their_solutions_and_neither_says_it_is_stuck()
    {
        const string repoA = "cronus-dk/payment-import";
        const string repoB = "cronus-dk/warehouse-ext";
        var first = await SeedAsync(UserStatus.Active, blocked: "stale", repository: repoA);
        var ownerId = await OwnerIdAsync(first);
        var second = await SeedSolutionAsync("CRONUS warehouse", repoB, ownerId, blocked: "stale too");
        _db.OrgContext.CurrentUserId = ownerId;
        await ConnectGitHubAsync();

        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(repoA, repoB))
            .On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        foreach (var name in new[] { repoA, repoB })
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name))
                .On(HttpMethod.Get, $"/repos/{name}/git/trees/main", HttpStatusCode.OK,
                    "{\"sha\":\"tree\",\"truncated\":false,\"tree\":[{\"path\":\"app.json\",\"type\":\"blob\",\"sha\":\"b\"}]}")
                .On(HttpMethod.Get, $"/repos/{name}/contents/app.json", HttpStatusCode.OK,
                    FakeGitHubApi.FileContentsJson("app.json", BehindManifest))
                .On(HttpMethod.Get, $"/repos/{name}/pulls", HttpStatusCode.OK, "[]")
                .On(HttpMethod.Get, $"/repos/{name}/git/ref/heads/aldt/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}")
                .On(HttpMethod.Get, $"/repos/{name}/git/ref/heads/main", HttpStatusCode.OK, "{\"object\":{\"sha\":\"main-head\"}}")
                .On(HttpMethod.Post, $"/repos/{name}/git/blobs", HttpStatusCode.Forbidden,
                    "{\"message\":\"You have exceeded a secondary rate limit.\"}");
        }

        (await NewScheduler(api).SweepAsync(CancellationToken.None)).Should().Be(0);

        api.Calls.Should().Contain(c => c.StartsWith("POST") && c.Contains($"/repos/{repoA}/git/blobs"));
        api.Calls.Should().NotContain(c => c.Contains($"/repos/{repoB}/pulls"),
            "the person's other solution waits for the next night");
        await using var read = _db.NewContext();
        var blocked = await read.OeProjects.AsNoTracking()
            .Where(p => p.Id == first || p.Id == second)
            .ToDictionaryAsync(p => p.Id, p => p.AutoUpdatePullRequestsBlocked);
        blocked[first].Should().Be("stale");
        blocked[second].Should().Be("stale too");
    }

    private const long InstallationId = 42;

    /// <summary>A manifest a wave behind the 28.2 production environment the solutions run.</summary>
    private const string BehindManifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS",
         "version":"1.0.0.0","application":"27.0.0.0","platform":"27.0.0.0"}
        """;

    private async Task<int> OwnerIdAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.OeProjects.AsNoTracking().SingleAsync(p => p.Id == projectId)).AutoUpdatePullRequestsByUserId!.Value;
    }

    /// <summary>GitHub set up on the server, the organisation connected, and the person in scope linked.</summary>
    private async Task ConnectGitHubAsync()
    {
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new ALDevToolbox.Services.Operations.GitHubAppInput(
                AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
                ClientSecret: "s3cr3t", ClearClientSecret: false,
                PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
        }
        await using (var ctx = _db.NewContext())
        {
            ctx.OrganizationSettings.Add(new OrganizationSettings
            {
                OrganizationId = TestDb.DefaultOrgId,
                GitHubInstallationId = InstallationId,
                GitHubOrgLogin = "cronus-dk",
                GitHubConnectedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var oauth = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var linkCtx = _db.NewContext();
        await _db.NewGitHubAccessService(linkCtx, _db.NewGitHubAppClient(linkCtx, oauth)).LinkAsync("the-code");
    }

    /// <summary>A second solution with automatic update pull requests on, live on 28.2, opened as <paramref name="ownerId"/>.</summary>
    private async Task<int> SeedSolutionAsync(string name, string repository, int? ownerId, string? blocked)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name,
            DefaultArtifactCountry = "dk",
            AutoUpdatePullRequests = true,
            AutoUpdatePullRequestsByUserId = ownerId,
            AutoUpdatePullRequestsBlocked = blocked,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Repositories =
            [
                new OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = ALDevToolbox.Domain.ValueObjects.RepositoryProvider.GitHub,
                    Url = $"https://github.com/{repository}",
                    DisplayName = repository.Split('/')[^1],
                },
            ],
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            Name = "Production",
            Type = "Production",
            Version = "28.2.45123.0",
            FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>A shipped release to scan against, and a solution with automatic update pull requests on.</summary>
    private async Task<int> SeedAsync(UserStatus? ownerStatus, string? blocked = null, string? repository = null)
    {
        await using var ctx = _db.NewContext();
        int? ownerId = null;
        if (ownerStatus is { } status)
        {
            var owner = new User
            {
                OrganizationId = TestDb.DefaultOrgId,
                Email = "gone@cronus.example",
                DisplayName = "Gone",
                PasswordHash = "x",
                Role = UserRole.User,
                Status = status,
                CreatedAt = DateTime.UtcNow,
            };
            ctx.Users.Add(owner);
            await ctx.SaveChangesAsync();
            ownerId = owner.Id;
        }
        ctx.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 28.2 (DK)",
            Kind = "first_party",
            Status = "ready",
            BcVersion = "28.2.50931.51727",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return await SeedSolutionAsync("CRONUS A/S", repository ?? "cronus-dk/payment-import", ownerId, blocked);
    }

    /// <summary>
    /// The scheduler over a provider that hands out the fixture's drift service, talking
    /// to <paramref name="api"/> - by default a GitHub that answers nothing.
    /// </summary>
    private DependencyDriftScheduler NewScheduler(FakeGitHubApi? api = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped(sp =>
        {
            var ctx = sp.GetRequiredService<AppDbContext>();
            var client = _db.NewGitHubAppClient(ctx, api ?? new FakeGitHubApi());
            var drift = _db.NewDependencyDriftService(ctx, client, _db.NewGitHubAccessService(ctx, client));
            drift.PauseAsync = (_, _) => Task.CompletedTask;
            return drift;
        });
        return new DependencyDriftScheduler(
            services.BuildServiceProvider(),
            TimeProvider.System,
            NullLogger<DependencyDriftScheduler>.Instance,
            new WorkerHeartbeatRegistry());
    }
}
