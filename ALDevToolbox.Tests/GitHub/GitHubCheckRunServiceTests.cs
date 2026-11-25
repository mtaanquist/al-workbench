using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// What the compile gate actually says on a pull request (issue #627), decided
/// from the build rows rather than from the client.
///
/// <para>Two things the first cut got wrong are pinned here. A solution can
/// track several repositories, and a pull request is about exactly one of them -
/// annotating another repository's file marks a line the pull request does not
/// contain, and failing the run for an error the pull request did not introduce
/// blocks a change that is fine. And a check run opened before a build that then
/// never started has to be closed, or it spins on the pull request until somebody
/// pushes again.</para>
/// </summary>
public sealed class GitHubCheckRunServiceTests : IDisposable
{
    private const long InstallationId = 42;
    private const string InstallationToken = "ghs_installation";
    private const string UnderReview = "cronus-dk/customer-app";
    private const string Elsewhere = "cronus-dk/shared-library";

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Only_the_repository_under_review_is_annotated_and_only_its_errors_fail_the_run()
    {
        await ConfigureDeploymentAsync();
        var seed = await SeedBuildAsync();
        await SeedDiagnosticAsync(seed, UnderReview, "App/Vat.al", 12, ProjectBuildDiagnosticSeverity.Warning, "AA0005");
        await SeedDiagnosticAsync(seed, Elsewhere, "Library/Old.al", 40, ProjectBuildDiagnosticSeverity.Error, "AL0118");

        var api = ApiWithChecks();
        await CompleteAsync(api, seed.ReleaseId);

        var body = JsonDocument.Parse(api.Bodies.Single(b => b.Call.StartsWith("PATCH")).Body).RootElement;
        body.GetProperty("conclusion").GetString().Should().Be(GitHubCheckConclusion.Success,
            "the error belongs to another repository of the solution, not to this pull request");

        var annotations = body.GetProperty("output").GetProperty("annotations");
        annotations.GetArrayLength().Should().Be(1);
        annotations[0].GetProperty("path").GetString().Should().Be("App/Vat.al");

        body.GetProperty("output").GetProperty("summary").GetString()
            .Should().Contain("1 error in other repositories of this solution");
    }

    [Fact]
    public async Task An_error_in_the_repository_under_review_still_fails_the_run()
    {
        await ConfigureDeploymentAsync();
        var seed = await SeedBuildAsync();
        await SeedDiagnosticAsync(seed, UnderReview, "App/Vat.al", 12, ProjectBuildDiagnosticSeverity.Error, "AL0118");

        var api = ApiWithChecks();
        await CompleteAsync(api, seed.ReleaseId);

        var body = JsonDocument.Parse(api.Bodies.Single(b => b.Call.StartsWith("PATCH")).Body).RootElement;
        body.GetProperty("conclusion").GetString().Should().Be(GitHubCheckConclusion.Failure);
    }

    [Fact]
    public async Task Annotations_are_capped_and_the_summary_says_how_many_were_left_out()
    {
        // Past a couple of hundred markers the Files tab is unreadable and every
        // further batch is another call on the organisation's rate limit.
        await ConfigureDeploymentAsync();
        var seed = await SeedBuildAsync();
        for (var i = 1; i <= 205; i++)
        {
            await SeedDiagnosticAsync(
                seed, UnderReview, $"App/File{i}.al", i, ProjectBuildDiagnosticSeverity.Warning, "AA0005");
        }

        var api = ApiWithChecks();
        await CompleteAsync(api, seed.ReleaseId);

        var patches = api.Bodies.Where(b => b.Call.StartsWith("PATCH")).ToList();
        patches.Should().HaveCount(4, "200 annotations at GitHub's cap of 50 per request is four calls");
        patches.Sum(p => JsonDocument.Parse(p.Body).RootElement
            .GetProperty("output").GetProperty("annotations").GetArrayLength())
            .Should().Be(200);
        JsonDocument.Parse(patches[0].Body).RootElement
            .GetProperty("output").GetProperty("summary").GetString()
            .Should().Contain("5 more");
    }

    [Fact]
    public async Task An_abandoned_run_is_completed_as_neutral_on_the_installation_token()
    {
        // The run is opened before the build is queued. A failure between the two
        // would otherwise leave the pull request with a tick spinning forever.
        await ConfigureDeploymentAsync();
        var api = ApiWithChecks();
        await using var ctx = _db.NewContext();
        var service = NewService(ctx, api);

        await service.AbandonAsync(InstallationId, UnderReview, 555, "The workbench could not start this build.");

        var patch = api.Bodies.Single(b => b.Call.StartsWith("PATCH"));
        var body = JsonDocument.Parse(patch.Body).RootElement;
        body.GetProperty("status").GetString().Should().Be("completed");
        body.GetProperty("conclusion").GetString().Should().Be(GitHubCheckConclusion.Neutral,
            "nothing was learned about the code, so a red X would be a claim we cannot support");
        body.GetProperty("output").GetProperty("summary").GetString()
            .Should().Contain("could not start");
        api.Credentials.Single(c => c.Call.StartsWith("PATCH")).Token.Should().Be(InstallationToken);
    }

    [Fact]
    public async Task A_check_run_GitHub_refuses_to_close_is_logged_rather_than_thrown()
    {
        // Everything this service does is best-effort: a missing tick is better
        // than a worker that falls over reporting one.
        await ConfigureDeploymentAsync();
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(new HttpMethod("PATCH"), $"/repos/{UnderReview}/check-runs/555",
                HttpStatusCode.Forbidden, "{\"message\":\"Resource not accessible by integration\"}");
        await using var ctx = _db.NewContext();

        var act = () => NewService(ctx, api).AbandonAsync(InstallationId, UnderReview, 555, "no reason");

        await act.Should().NotThrowAsync();
    }

    // --- Fixture -----------------------------------------------------------

    private sealed record Seed(int ProjectId, int ReleaseId, int BuildId, int UnderReviewRepositoryId, int ElsewhereRepositoryId);

    [Fact]
    public async Task A_build_of_a_member_fork_says_whose_fork_it_came_from()
    {
        // A reviewer looking at a green tick should be able to see that the code
        // was compiled from somebody's fork rather than from a branch of the
        // repository - the check run is where they are already looking.
        await ConfigureDeploymentAsync();
        var seed = await SeedBuildAsync();

        var api = ApiWithChecks();
        await using var ctx = _db.NewContext();
        await NewService(ctx, api).CompleteAsync(InstallationId, UnderReview, seed.ReleaseId, "erik");

        var body = JsonDocument.Parse(api.Bodies.Single(b => b.Call.StartsWith("PATCH")).Body).RootElement;
        body.GetProperty("output").GetProperty("summary").GetString()
            .Should().Contain("Built from erik's fork.");
    }

    [Fact]
    public async Task An_ordinary_pull_request_says_nothing_about_forks()
    {
        await ConfigureDeploymentAsync();
        var seed = await SeedBuildAsync();

        var api = ApiWithChecks();
        await CompleteAsync(api, seed.ReleaseId);

        var body = JsonDocument.Parse(api.Bodies.Single(b => b.Call.StartsWith("PATCH")).Body).RootElement;
        body.GetProperty("output").GetProperty("summary").GetString()
            .Should().NotContain("fork");
    }

    private async Task CompleteAsync(FakeGitHubApi api, int releaseId)
    {
        await using var ctx = _db.NewContext();
        await NewService(ctx, api).CompleteAsync(InstallationId, UnderReview, releaseId);
    }

    private GitHubCheckRunService NewService(Data.AppDbContext ctx, FakeGitHubApi api) =>
        new(ctx, _db.NewGitHubAppClient(ctx, api), new PublicOrigin(null),
            NullLogger<GitHubCheckRunService>.Instance);

    private static FakeGitHubApi ApiWithChecks() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson(InstallationToken))
            .On(new HttpMethod("PATCH"), $"/repos/{UnderReview}/check-runs/555",
                HttpStatusCode.OK, "{\"id\":555}");

    private async Task<Seed> SeedBuildAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Retail " + Guid.NewGuid().ToString("N")[..8],
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var repositories = new[] { UnderReview, Elsewhere }
            .Select(full => new OeProjectRepository
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = project.Id,
                Provider = RepositoryProvider.GitHub,
                Url = $"https://github.com/{full}.git",
                DisplayName = full.Split('/')[1],
            })
            .ToList();
        ctx.OeProjectRepositories.AddRange(repositories);

        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "CRONUS Retail " + Guid.NewGuid().ToString("N"),
            Kind = "project",
            Status = "ready",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            ReleaseId = release.Id,
            Status = ProjectBuildStatus.Ready,
            Trigger = ProjectBuildTrigger.PullRequest,
            CheckRunId = 555,
            BcVersion = "27.0",
            StartedAt = now,
        };
        ctx.OeProjectBuilds.Add(build);
        ctx.OeProjectBuildResults.Add(new OeProjectBuildResult
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppName = "Customer App",
            AppId = Guid.NewGuid().ToString(),
            Status = ProjectBuildResultStatus.Ingested,
            CreatedAt = now,
        });
        await ctx.SaveChangesAsync();

        return new Seed(project.Id, release.Id, build.Id, repositories[0].Id, repositories[1].Id);
    }

    private async Task SeedDiagnosticAsync(
        Seed seed, string repositoryFullName, string path, int line, string severity, string code)
    {
        await using var ctx = _db.NewContext();
        ctx.OeProjectBuildDiagnostics.Add(new OeProjectBuildDiagnostic
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectBuildId = seed.BuildId,
            ProjectRepositoryId = repositoryFullName == UnderReview
                ? seed.UnderReviewRepositoryId
                : seed.ElsewhereRepositoryId,
            Path = path,
            Line = line,
            Column = 1,
            Severity = severity,
            Code = code,
            Message = "something to say",
            Ordering = line,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task ConfigureDeploymentAsync()
    {
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: TestKeys.GitHubAppPem, ClearPrivateKey: false));
    }
}
