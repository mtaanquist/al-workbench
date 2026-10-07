using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// Dependency drift (issue #630): which tracked repositories still target an
/// older Business Central than the release that has just been imported, and the
/// pull requests that move them on.
///
/// <para>Four rules carry the feature. Only a repository that is <em>behind</em>
/// is proposed, so nobody is offered a pull request that changes nothing. The
/// scan reads on the organisation's installation token and the pull request is
/// written on the acting person's own - the credential split every GitHub
/// feature here obeys. The commit changes only the values that moved, so the
/// diff is reviewable. And a finding that somebody has since fixed by hand
/// disappears on the next scan rather than lingering as a to-do.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.</para>
/// </summary>
public sealed class DependencyDriftServiceTests : IDisposable
{
    private const int UserId = 6300;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";
    private const string RepoB = "cronus-dk/warehouse-ext";
    private const string SystemAppId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f";

    /// <summary>A manifest a wave behind, formatted the way a person keeps it.</summary>
    private const string BehindManifest = """
        {
          "id": "1c0ffee0-0000-4000-8000-000000000001",
          "name": "Payment Import",
          "publisher": "CRONUS",
          "version": "1.0.0.0",
          "application": "27.0.0.0",
          "platform": "27.0.0.0",
          "dependencies": [
            {
              "id": "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
              "name": "System Application",
              "publisher": "Microsoft",
              "version": "27.0.0.0"
            }
          ],
          "idRanges": [ { "from": 50000, "to": 50099 } ]
        }
        """;

    /// <summary>
    /// The same dependency twice, once under the modern <c>id</c> and once under
    /// the old <c>appId</c>. Real app.json files carry this after a merge, and it
    /// used to produce two findings for one field - a unique-index violation that
    /// took the whole organisation's scan down with it.
    /// </summary>
    private const string DuplicateDependencyManifest = """
        {
          "id": "1c0ffee0-0000-4000-8000-000000000003",
          "name": "Payment Import",
          "publisher": "CRONUS",
          "version": "1.0.0.0",
          "application": "27.0.0.0",
          "platform": "27.0.0.0",
          "dependencies": [
            {
              "id": "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
              "name": "System Application",
              "publisher": "Microsoft",
              "version": "27.0.0.0"
            },
            {
              "appId": "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
              "name": "System Application",
              "publisher": "Microsoft",
              "version": "27.0.0.0"
            }
          ],
          "idRanges": [ { "from": 50000, "to": 50099 } ]
        }
        """;

    private const string CurrentManifest = """
        {"id":"1c0ffee0-0000-4000-8000-000000000002","name":"Warehouse Extras","publisher":"CRONUS",
         "version":"2.0.0.0","application":"28.2.0.0","platform":"28.0.0.0"}
        """;

    private readonly TestDb _db = new();

    public DependencyDriftServiceTests()
    {
        using var ctx = _db.NewContext();
        ctx.Users.Add(new User
        {
            Id = UserId,
            OrganizationId = TestDb.DefaultOrgId,
            Email = "consultant@cronus.example",
            DisplayName = "consultant@cronus.example",
            PasswordHash = "x",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTime.UtcNow,
        });
        ctx.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
    }

    public void Dispose() => _db.Dispose();

    // ── The scan ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_dependency_named_twice_in_one_manifest_is_recorded_once()
    {
        // One row per (repository, file, field) is what the unique index says.
        // Before the dedupe this threw on save and lost every other repository's
        // findings with it.
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var api = BaseApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", DuplicateDependencyManifest));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var found = await service.ScanForReleaseAsync(releaseId);

        found.Should().Be(3, "application, platform, and the dependency once");
        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryDrift.AsNoTracking().ToListAsync();
        rows.Select(r => r.Field).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task The_application_the_platform_and_a_behind_dependency_are_all_recorded()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var (service, ctx) = NewService(ScannableApi(RepoA));
        await using var _ = ctx;

        var found = await service.ScanForReleaseAsync(releaseId);

        found.Should().Be(3);
        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryDrift.OrderBy(d => d.Field).ToListAsync();
        rows.Select(r => r.Field).Should().Equal(
            "application", "dependency:" + SystemAppId, "platform");
        rows.Should().OnlyContain(r => r.Repository == RepoA && r.Path == "app.json" && r.ReleaseId == releaseId);
        // The application is a minimum, so the release's four-part build number
        // is not what a person would have typed.
        rows[0].Current.Should().Be("27.0.0.0");
        rows[0].Proposed.Should().Be("28.2.0.0");
        rows[2].Proposed.Should().Be("28.0.0.0", "the platform comes from the release's System module");
    }

    [Fact]
    public async Task A_manifest_already_on_the_new_version_is_not_proposed_anything()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoB);
        var releaseId = await SeedReleaseAsync();
        var (service, ctx) = NewService(ScannableApi(RepoB));
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(releaseId)).Should().Be(0);
    }

    [Fact]
    public async Task A_manifest_with_no_application_is_left_alone()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var api = BaseApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json",
                    """{"id":"x","name":"No target","publisher":"CRONUS","version":"1.0.0.0"}"""));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(releaseId)).Should().Be(0);
    }

    [Fact]
    public async Task A_repository_no_solution_tracks_is_not_even_read()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var api = ScannableApi(RepoA, RepoB);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.ScanForReleaseAsync(releaseId);

        api.Calls.Should().NotContain(c => c.Contains(RepoB));
    }

    [Fact]
    public async Task The_scan_reads_on_the_organisations_installation_token()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var api = ScannableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        await service.ScanForReleaseAsync(releaseId);

        api.Credentials
            .Where(c => c.Call.Contains("/git/trees/") || c.Call.Contains("/contents/"))
            .Should().NotBeEmpty()
            .And.OnlyContain(c => c.Token == "ghs_installation");
    }

    [Fact]
    public async Task A_scan_after_the_repository_was_fixed_clears_what_it_used_to_say()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();

        var (first, ctx1) = NewService(ScannableApi(RepoA));
        await using (ctx1) (await first.ScanForReleaseAsync(releaseId)).Should().Be(3);

        // Somebody bumped it by hand; the same manifest path now reads current.
        var fixedApi = BaseApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", CurrentManifest));
        var (second, ctx2) = NewService(fixedApi);
        await using (ctx2) (await second.ScanForReleaseAsync(releaseId)).Should().Be(0);

        await using var read = _db.NewContext();
        (await read.GitHubRepositoryDrift.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_pipeline_build_release_is_not_something_repositories_can_be_behind()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync(kind: "project");
        var api = ScannableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(releaseId)).Should().Be(0);
        api.Calls.Should().BeEmpty("nothing about a pipeline build is worth a call to GitHub");
    }

    [Fact]
    public async Task One_organisations_findings_are_invisible_to_another()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var releaseId = await SeedReleaseAsync();
        var (service, ctx) = NewService(ScannableApi(RepoA));
        await using (ctx) await service.ScanForReleaseAsync(releaseId);

        _db.OrgContext.CurrentOrganizationId = TestDb.OtherOrgId;
        try
        {
            await using var read = _db.NewContext();
            (await read.GitHubRepositoryDrift.CountAsync()).Should().Be(0);
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    // ── Measured against production (issue #1081) ───────────────────────

    [Fact]
    public async Task A_repository_is_measured_against_its_solutions_production_environment_not_the_release()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        var projectId = await SeedSolutionAsync(RepoA, withProduction: false);
        var environmentId = await SeedEnvironmentAsync(projectId, "Production", "28.0.45123.0");
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        var (service, ctx) = NewService(ScannableApi(RepoA));
        await using var _ = ctx;

        await service.ScanForReleaseAsync(releaseId);

        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryDrift.AsNoTracking().ToListAsync();
        rows.Single(r => r.Field == "application").Proposed.Should().Be("28.0.0.0", "production runs 28.0, not the imported 29.0");
        rows.Single(r => r.Field == "platform").Proposed.Should().Be("28.0.0.0");
        rows.Where(r => r.Field is "application" or "platform").Should().OnlyContain(r => r.EnvironmentId == environmentId);
    }

    [Fact]
    public async Task A_repository_on_the_version_production_runs_is_not_behind_a_newer_release()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoB, withProduction: false);
        await SeedEnvironmentAsync(projectId, "Production", "28.2.45123.0");
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        var (service, ctx) = NewService(ScannableApi(RepoB));
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(releaseId)).Should().Be(0);
    }

    [Fact]
    public async Task The_first_live_production_environment_by_name_is_the_one_used()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA, withProduction: false);
        await SeedEnvironmentAsync(projectId, "Sandbox", "29.0.1.0", type: "Sandbox");
        await SeedEnvironmentAsync(projectId, "AAA-Deleted", "29.0.1.0", softDeletedOn: DateTime.UtcNow);
        await SeedEnvironmentAsync(projectId, "Prod-B", "28.1.1.0");
        var chosen = await SeedEnvironmentAsync(projectId, "prod-a", "27.5.1.0", type: "production");
        var releaseId = await SeedReleaseAsync();
        var (service, ctx) = NewService(ScannableApi(RepoA));
        await using var _ = ctx;

        await service.ScanForReleaseAsync(releaseId);

        await using var read = _db.NewContext();
        var application = await read.GitHubRepositoryDrift.AsNoTracking().SingleAsync(r => r.Field == "application");
        application.EnvironmentId.Should().Be(chosen);
        application.Proposed.Should().Be("27.5.0.0");
    }

    [Fact]
    public async Task A_solution_with_no_production_environment_is_measured_against_its_sandbox()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA, withProduction: false);
        await SeedEnvironmentAsync(projectId, "Production", null);
        var sandbox = await SeedEnvironmentAsync(projectId, "Sandbox", "28.1.45123.0", type: "Sandbox");
        var releaseId = await SeedReleaseAsync();
        var (service, ctx) = NewService(ScannableApi(RepoA));
        await using var _ = ctx;

        await service.ScanForReleaseAsync(releaseId);

        await using var read = _db.NewContext();
        var application = await read.GitHubRepositoryDrift.AsNoTracking().SingleAsync(r => r.Field == "application");
        application.EnvironmentId.Should().Be(sandbox, "a production environment with no version says nothing");
        application.Proposed.Should().Be("28.1.0.0");

        var summary = await service.GetSummaryAsync();
        summary.Repositories.Single().EnvironmentType.Should().Be("Sandbox");
    }

    [Fact]
    public async Task A_solution_with_no_environment_is_not_live_yet_and_is_not_checked()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA, withProduction: false);
        var releaseId = await SeedReleaseAsync();
        var api = ScannableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(releaseId)).Should().Be(0, "a newer release is no reason to move a customer who is not live");
        api.Calls.Should().NotContain(c => c.Contains($"/repos/{RepoA}/"));
    }

    [Fact]
    public async Task The_summary_says_a_repository_was_measured_against_production()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA, name: "CRONUS A/S", withProduction: false);
        await SeedEnvironmentAsync(projectId, "Production", "28.0.45123.0");
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        var (scanner, scanCtx) = NewService(ScannableApi(RepoA));
        await using (scanCtx) await scanner.ScanForReleaseAsync(releaseId);
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        var summary = await service.GetSummaryAsync();

        summary.TargetVersion.Should().BeNull("nothing was measured against a release");
        var repo = summary.Repositories.Should().ContainSingle().Subject;
        repo.EnvironmentType.Should().Be("Production");
        repo.TargetApplication.Should().Be("28.0");
        repo.Environment.Should().Be("Production");
        repo.Solution.Should().Be("CRONUS A/S");
    }

    [Fact]
    public async Task The_pull_request_targets_productions_version_and_says_so()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA, withProduction: false);
        await SeedEnvironmentAsync(projectId, "Production", "28.2.45123.0");
        // An earlier release, so a compare link would exist for 29.0.
        await SeedReleaseAsync();
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        var (scanner, scanCtx) = NewService(ScannableApi(RepoA));
        await using (scanCtx) await scanner.ScanForReleaseAsync(releaseId);
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = (await service.OpenUpdatePullRequestsAsync([RepoA])).Single();

        result.Refusal.Should().BeNull();
        var body = api.Bodies.Single(b => b.Call.StartsWith("POST") && b.Call.EndsWith("/pulls")).Body;
        body.Should().Contain("Target Business Central 28.2")
            .And.Contain("Measured against the production environment Production, which was on Business Central 28.2 when checked.")
            .And.NotContain("What changed between the two releases", "the release is 29.0, not what this moves to");
    }

    [Fact]
    public async Task Platform_only_drift_against_production_targets_productions_version_not_the_release()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA, withProduction: false);
        await SeedEnvironmentAsync(projectId, "Production", "28.0.45123.0");
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        const string platformBehind = """
            {"id":"1c0ffee0-0000-4000-8000-000000000009","name":"Payment Import","publisher":"CRONUS",
             "version":"1.0.0.0","application":"28.0.0.0","platform":"27.0.0.0"}
            """;
        var scanApi = BaseApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA))
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", platformBehind));
        var (scanner, scanCtx) = NewService(scanApi);
        await using (scanCtx) (await scanner.ScanForReleaseAsync(releaseId)).Should().Be(1);

        var api = WritableApi(RepoA, manifest: platformBehind, branch: "aldt/bump-bc-28.0");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = (await service.OpenUpdatePullRequestsAsync([RepoA])).Single();

        result.Refusal.Should().BeNull();
        var body = api.Bodies.Single(b => b.Call.StartsWith("POST") && b.Call.EndsWith("/pulls")).Body;
        body.Should().Contain("Target Business Central 28.0").And.NotContain("29.0");
    }

    [Fact]
    public async Task A_private_solutions_environment_is_not_named_to_someone_who_cannot_see_it()
    {
        // The repository is tracked by a Private solution (older, with production) and a
        // Public one without. The Private one's environment is the yardstick, but its
        // name must not reach a viewer who is only on the Public one - nor GitHub.
        await ReadyAsync();
        var hidden = await SeedSolutionAsync(RepoA, ProjectVisibility.Private, name: "CRONUS secret", withProduction: false);
        await SeedEnvironmentAsync(hidden, "CRONUS-PROD", "28.2.45123.0");
        await SeedSolutionAsync(RepoA, name: "CRONUS A/S", withProduction: false);
        var releaseId = await SeedReleaseAsync(bcVersion: "29.0.55190.0");
        var (scanner, scanCtx) = NewService(ScannableApi(RepoA));
        await using (scanCtx) await scanner.ScanForReleaseAsync(releaseId);
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var repo = (await service.GetSummaryAsync()).Repositories.Should().ContainSingle().Subject;
        repo.EnvironmentType.Should().Be("Production");
        repo.Environment.Should().BeNull();
        repo.Solution.Should().BeNull();

        (await service.OpenUpdatePullRequestsAsync([RepoA])).Single().Refusal.Should().BeNull();
        var body = api.Bodies.Single(b => b.Call.StartsWith("POST") && b.Call.EndsWith("/pulls")).Body;
        body.Should().NotContain("CRONUS-PROD").And.Contain("production environment, which was on Business Central 28.2");
    }

    // ── What the panel reads ─────────────────────────────────────────────

    [Fact]
    public async Task The_summary_says_what_moves_and_how_many_repositories_sit_on_each_version()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        var summary = await service.GetSummaryAsync();

        summary.IsEmpty.Should().BeFalse();
        summary.TargetVersion.Should().Be("28.2");
        summary.Groups.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new DependencyDriftGroup("27.0", 1));
        var repo = summary.Repositories.Should().ContainSingle().Subject;
        repo.HtmlUrl.Should().Be($"https://github.com/{RepoA}");
        var file = repo.Files.Should().ContainSingle().Subject;
        file.Changes.Select(c => c.Label).Should().Contain("Business Central application")
            .And.Contain("System Application", "a dependency is named, not spelled as a GUID");
    }

    [Fact]
    public async Task A_repository_of_a_solution_the_viewer_cannot_see_is_not_named_to_them()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA, ProjectVisibility.Private);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        (await service.GetSummaryAsync()).IsEmpty.Should().BeTrue();
    }

    // ── The pull requests ────────────────────────────────────────────────

    [Fact]
    public async Task The_commit_changes_only_the_values_that_moved_and_goes_out_as_the_person()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var results = await service.OpenUpdatePullRequestsAsync([RepoA]);

        var result = results.Should().ContainSingle().Subject;
        result.Refusal.Should().BeNull();
        result.PullRequest!.Number.Should().Be(77);
        result.IsNewPullRequest.Should().BeTrue();

        var committed = CommittedBlob(api);
        committed.Should().Be(BehindManifest
            .Replace("\"application\": \"27.0.0.0\"", "\"application\": \"28.2.0.0\"")
            .Replace("\"platform\": \"27.0.0.0\"", "\"platform\": \"28.0.0.0\"")
            .Replace("\"version\": \"27.0.0.0\"", "\"version\": \"28.2.0.0\""),
            "everything the person maintains - the key order, the indentation, the id ranges - is left as it was");

        // The write is theirs; only the earlier scan is the organisation's.
        api.Credentials
            .Where(c => c.Call.Contains("/git/") || c.Call.Contains("/pulls"))
            .Should().OnlyContain(c => c.Token == "ghu_access");
    }

    [Fact]
    public async Task A_second_run_joins_the_pull_request_that_is_already_open()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA, openPullRequest: true);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = (await service.OpenUpdatePullRequestsAsync([RepoA])).Single();

        result.IsNewPullRequest.Should().BeFalse();
        result.PullRequest!.HeadBranch.Should().Be("aldt/bump-bc-28.2");
        api.Calls.Should().NotContain(c => c.StartsWith("POST") && c.EndsWith("/pulls"));
        api.Calls.Should().Contain(c => c.StartsWith("PATCH"), "the branch already under review is moved on, not replaced");
    }

    [Fact]
    public async Task A_repository_the_person_cannot_open_is_refused_and_the_other_one_still_gets_its_pull_request()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedSolutionAsync(RepoB, name: "CRONUS warehouse");
        var releaseId = await SeedReleaseAsync();
        await SeedDriftAsync(RepoA, releaseId);
        await SeedDriftAsync(RepoB, releaseId);
        // RepoB answers 404 to this person, which is what GitHub says about a
        // repository they cannot see rather than one that is gone.
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var results = await service.OpenUpdatePullRequestsAsync([RepoA, RepoB]);

        results.Should().HaveCount(2);
        results.Single(r => r.Repository == RepoA).PullRequest.Should().NotBeNull();
        results.Single(r => r.Repository == RepoB).Refusal
            .Should().Contain("not one the workbench can offer you");
    }

    [Fact]
    public async Task Nothing_to_change_is_said_rather_than_committing_an_empty_pull_request()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        // The repository has moved on since the scan: the manifest already reads
        // the version the finding proposes.
        var api = WritableApi(RepoA, manifest: CurrentManifest);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = (await service.OpenUpdatePullRequestsAsync([RepoA])).Single();

        result.PullRequest.Should().BeNull();
        result.Refusal.Should().Contain("already up to date");
        api.Calls.Should().NotContain(c => c.Contains("/git/blobs"));
    }

    [Fact]
    public async Task Somebody_who_has_not_linked_their_github_account_is_told_that_rather_than_refused_the_repository()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        var result = (await service.OpenUpdatePullRequestsAsync([RepoA])).Single();

        result.Refusal.Should().Contain("Connect your own GitHub account first");
    }

    [Fact]
    public async Task A_preview_release_is_never_a_drift_target()
    {
        // A preview going ready must not wipe the findings and tell every solution
        // it is behind a version customers can't run; nor may "Check again" pick it
        // as the newest first-party release just because its number is highest.
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        var shipped = await SeedReleaseAsync();
        var preview = await SeedReleaseAsync(prerelease: true, bcVersion: "29.0.55190.0");
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        (await service.ScanForReleaseAsync(preview)).Should().Be(0);
        (await service.NewestFirstPartyReleaseIdAsync()).Should().Be(shipped);
    }

    // ── Opening them on their own (issue #1104) ──────────────────────────

    [Fact]
    public async Task A_pull_request_opened_by_hand_is_remembered_by_version()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(WritableApi(RepoA));
        await using var _ = ctx;

        (await service.OpenUpdatePullRequestsAsync([RepoA])).Single().Refusal.Should().BeNull();

        await using var read = _db.NewContext();
        var record = await read.GitHubUpdatePullRequests.AsNoTracking().SingleAsync();
        record.Repository.Should().Be(RepoA);
        record.Version.Should().Be("28.2");
        record.PullRequestNumber.Should().Be(77);
        record.IsAutomatic.Should().BeFalse();
    }

    [Fact]
    public async Task The_automatic_run_opens_the_pull_request_as_the_person_and_remembers_it()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var result = await service.OpenAutomaticPullRequestsAsync(projectId);

        result.Should().Be(new AutomaticUpdatePullRequests(1, null));
        api.Calls.Should().Contain(c => c.StartsWith("POST") && c.EndsWith($"/repos/{RepoA}/pulls"));
        api.Credentials.Where(c => c.Call.Contains("/pulls")).Should().OnlyContain(c => c.Token == "ghu_access");
        await using var read = _db.NewContext();
        (await read.GitHubUpdatePullRequests.SingleAsync()).IsAutomatic.Should().BeTrue();
    }

    [Fact]
    public async Task The_automatic_run_does_nothing_for_a_solution_that_has_it_off()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Should().Be(new AutomaticUpdatePullRequests(0, null));
        api.Calls.Should().NotContain(c => c.Contains("/pulls"));
    }

    [Fact]
    public async Task A_version_already_offered_is_not_offered_again_even_when_its_branch_is_gone()
    {
        // Somebody closed last week's pull request and GitHub deleted its branch; only
        // the workbench's own record says the version was offered and turned down.
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        await SeedOfferedAsync(RepoA, "28.2", 70, automatic: true);
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Should().Be(new AutomaticUpdatePullRequests(0, null));
        api.Calls.Should().NotContain(c => c.StartsWith("POST"));
    }

    [Fact]
    public async Task A_closed_pull_request_whose_branch_was_kept_is_not_stepped_past_by_the_automatic_run()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA)
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/ref/heads/aldt/bump-bc-28.2", HttpStatusCode.OK,
                "{\"object\":{\"sha\":\"old-branch\"}}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Should().Be(new AutomaticUpdatePullRequests(0, null));
        api.Calls.Should().NotContain(c => c.Contains("bump-bc-28.2-2"));
        api.Calls.Should().NotContain(c => c.StartsWith("POST") && c.EndsWith("/pulls"));
    }

    [Fact]
    public async Task A_newer_version_closes_the_older_automatic_pull_request_with_a_note()
    {
        await ReadyAsync();
        await SeedCatalogueAsync("28.2.0.0");
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        await SeedOfferedAsync(RepoA, "28.0", 70, automatic: true);
        await SeedOfferedAsync(RepoA, "27.5", 60, automatic: false);
        var api = WritableApi(RepoA)
            .On(HttpMethod.Get, $"/repos/{RepoA}/pulls/70", HttpStatusCode.OK, "{\"number\":70,\"state\":\"open\"}")
            .On(HttpMethod.Post, $"/repos/{RepoA}/issues/70/comments", HttpStatusCode.Created, "{\"id\":1}")
            .On(HttpMethod.Patch, $"/repos/{RepoA}/pulls/70", HttpStatusCode.OK,
                $"{{\"number\":70,\"html_url\":\"https://github.com/{RepoA}/pull/70\"}}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Opened.Should().Be(1);

        api.Bodies.Single(b => b.Call.EndsWith("/issues/70/comments")).Body
            .Should().Contain("Superseded by #77, which targets Business Central 28.2.");
        api.Bodies.Single(b => b.Call.EndsWith("/pulls/70")).Body.Should().Contain("closed");
        api.Calls.Should().NotContain(c => c.Contains("/60"), "a pull request a person opened is theirs to close");
        await using var read = _db.NewContext();
        var records = await read.GitHubUpdatePullRequests.AsNoTracking().ToDictionaryAsync(r => r.PullRequestNumber);
        records[70].SupersededAt.Should().NotBeNull();
        records[60].SupersededAt.Should().BeNull();
    }

    [Fact]
    public async Task An_older_automatic_pull_request_that_was_already_merged_is_left_alone()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        await SeedOfferedAsync(RepoA, "28.0", 70, automatic: true);
        var api = WritableApi(RepoA)
            .On(HttpMethod.Get, $"/repos/{RepoA}/pulls/70", HttpStatusCode.OK, "{\"number\":70,\"state\":\"closed\"}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Opened.Should().Be(1);

        api.Calls.Should().NotContain(c => c.Contains("/issues/70/comments") || c.StartsWith("PATCH") && c.EndsWith("/pulls/70"));
        await using var read = _db.NewContext();
        (await read.GitHubUpdatePullRequests.AsNoTracking().SingleAsync(r => r.PullRequestNumber == 70))
            .SupersededAt.Should().NotBeNull("it is no longer one to close");
    }

    [Fact]
    public async Task A_pull_request_opened_by_hand_also_replaces_an_older_automatic_one()
    {
        await ReadyAsync();
        await SeedSolutionAsync(RepoA);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        await SeedOfferedAsync(RepoA, "28.0", 70, automatic: true);
        var api = WritableApi(RepoA)
            .On(HttpMethod.Get, $"/repos/{RepoA}/pulls/70", HttpStatusCode.OK, "{\"number\":70,\"state\":\"open\"}")
            .On(HttpMethod.Post, $"/repos/{RepoA}/issues/70/comments", HttpStatusCode.Created, "{\"id\":1}")
            .On(HttpMethod.Patch, $"/repos/{RepoA}/pulls/70", HttpStatusCode.OK, "{\"number\":70}");
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenUpdatePullRequestsAsync([RepoA])).Single().Refusal.Should().BeNull();

        api.Calls.Should().Contain(c => c.StartsWith("PATCH") && c.EndsWith("/pulls/70"));
    }

    [Fact]
    public async Task The_automatic_run_finds_the_findings_however_the_solution_spelled_the_repository()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA.ToUpperInvariant());
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var api = WritableApi(RepoA);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Should().Be(new AutomaticUpdatePullRequests(1, null));
    }

    [Fact]
    public async Task The_automatic_run_adds_to_its_open_pull_request_rather_than_skipping_the_version()
    {
        await ReadyAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        await SeedOfferedAsync(RepoA, "28.2", 77, automatic: true);
        var api = WritableApi(RepoA, openPullRequest: true);
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Should().Be(new AutomaticUpdatePullRequests(0, null));
        api.Calls.Should().Contain(c => c.StartsWith("PATCH") && c.Contains("/git/refs/heads/aldt/bump-bc-28.2"));
    }

    [Fact]
    public async Task The_automatic_run_says_so_when_the_person_has_no_GitHub_account_connected()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        var projectId = await SeedSolutionAsync(RepoA);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        var result = await service.OpenAutomaticPullRequestsAsync(projectId);

        result.Opened.Should().Be(0);
        result.Blocked.Should().Contain("has not connected their GitHub account");
    }

    [Fact]
    public async Task The_automatic_run_says_so_when_the_person_can_no_longer_manage_the_solution()
    {
        await ReadyAsync();
        // A Private solution with no owner: only an admin or an assigned team manages it.
        var projectId = await SeedSolutionAsync(RepoA, ProjectVisibility.Private);
        await EnableAutoUpdatesAsync(projectId);
        await SeedDriftAsync(RepoA, await SeedReleaseAsync());
        var (service, ctx) = NewService(BaseApi());
        await using var _ = ctx;

        (await service.OpenAutomaticPullRequestsAsync(projectId)).Blocked
            .Should().Be(DependencyDriftService.AutomaticNoAccessMessage);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private (DependencyDriftService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewDependencyDriftService(ctx, client, access, publicOrigin: "https://workbench.cronus.example"), ctx);
    }

    /// <summary>The body of the one blob the run pushed.</summary>
    private static string CommittedBlob(FakeGitHubApi api)
    {
        var body = api.Bodies.Single(b => b.Call.Contains("/git/blobs")).Body;
        using var document = System.Text.Json.JsonDocument.Parse(body);
        return System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!));
    }

    private static string TreeJson(params (string Path, string Type)[] entries) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', entries.Select(e =>
            $"{{\"path\":\"{e.Path}\",\"type\":\"{e.Type}\",\"sha\":\"blob-{e.Path.GetHashCode():x}\"}}"))
        + "]}";

    /// <summary>Enough of GitHub to mint an installation token.</summary>
    private static FakeGitHubApi BaseApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson());

    /// <summary>The installation lists <paramref name="repositories"/>, and each answers with a manifest.</summary>
    private static FakeGitHubApi ScannableApi(params string[] repositories)
    {
        var api = BaseApi()
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(repositories));
        foreach (var name in repositories)
        {
            var manifest = name == RepoB ? CurrentManifest : BehindManifest;
            api.On(HttpMethod.Get, $"/repos/{name}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
               .On(HttpMethod.Get, $"/repos/{name}/contents/app.json", HttpStatusCode.OK,
                   FakeGitHubApi.FileContentsJson("app.json", manifest));
        }
        return api;
    }

    /// <summary>
    /// A GitHub the acting person can open <paramref name="fullName"/> on and
    /// commit to. Everything else answers 404, which is what GitHub says about a
    /// repository somebody cannot see.
    /// </summary>
    private static FakeGitHubApi WritableApi(
        string fullName, bool openPullRequest = false, string? manifest = null, string branch = "aldt/bump-bc-28.2")
    {
        var api = BaseApi();
        api.On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        api.On(HttpMethod.Get, $"/repos/{fullName}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(fullName));
        api.On(HttpMethod.Get, $"/repos/{fullName}/pulls", HttpStatusCode.OK,
            openPullRequest
                ? $"[{{\"number\":77,\"html_url\":\"https://github.com/{fullName}/pull/77\"}}]"
                : "[]");
        api.On(HttpMethod.Get, $"/repos/{fullName}/git/ref/heads/{branch}",
            openPullRequest ? HttpStatusCode.OK : HttpStatusCode.NotFound,
            openPullRequest ? "{\"object\":{\"sha\":\"branch-head\"}}" : "{\"message\":\"Not Found\"}");
        api.On(HttpMethod.Get, $"/repos/{fullName}/git/ref/heads/main", HttpStatusCode.OK,
            "{\"object\":{\"sha\":\"main-head\"}}");
        api.On(HttpMethod.Get, $"/repos/{fullName}/contents/app.json", HttpStatusCode.OK,
            FakeGitHubApi.FileContentsJson("app.json", manifest ?? BehindManifest));
        api.On(HttpMethod.Get, $"/repos/{fullName}/git/commits/", HttpStatusCode.OK,
            "{\"sha\":\"parent\",\"tree\":{\"sha\":\"base-tree\"}}");
        api.On(HttpMethod.Post, $"/repos/{fullName}/git/blobs", HttpStatusCode.Created,
            FakeGitHubApi.ShaJson("new-blob"));
        api.On(HttpMethod.Post, $"/repos/{fullName}/git/trees", HttpStatusCode.Created,
            FakeGitHubApi.ShaJson("new-tree"));
        api.On(HttpMethod.Post, $"/repos/{fullName}/git/commits", HttpStatusCode.Created,
            FakeGitHubApi.ShaJson("new-commit"));
        api.On(HttpMethod.Post, $"/repos/{fullName}/git/refs", HttpStatusCode.Created,
            FakeGitHubApi.ShaJson("new-ref"));
        api.On(HttpMethod.Patch, $"/repos/{fullName}/git/refs/heads/{branch}", HttpStatusCode.OK,
            FakeGitHubApi.ShaJson("moved-ref"));
        api.On(HttpMethod.Post, $"/repos/{fullName}/pulls", HttpStatusCode.Created,
            $"{{\"number\":77,\"html_url\":\"https://github.com/{fullName}/pull/77\"}}");
        return api;
    }

    /// <summary>A first-party release with the Base Application and System modules a scan reads.</summary>
    private async Task<int> SeedReleaseAsync(string kind = "first_party", bool prerelease = false, string bcVersion = "28.2.50931.51727")
    {
        await using var ctx = _db.NewContext();
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = prerelease ? $"Business Central {bcVersion[..4]} (DK) Preview" : "Business Central 28.2 (DK)",
            Kind = kind,
            Status = "ready",
            BcVersion = bcVersion,
            IsPrerelease = prerelease,
            DedupKey = kind != "first_party" ? null
                : prerelease ? $"bc-insider:{bcVersion[..4]}:dk" : $"bc-onprem:{bcVersion[..4]}:dk",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleases.Add(release);
        await ctx.SaveChangesAsync();

        ctx.OeModules.Add(new ALDevToolbox.Domain.Entities.ObjectExplorer.OeModule
        {
            OrganizationId = TestDb.DefaultOrgId,
            ReleaseId = release.Id,
            AppId = Guid.NewGuid(),
            Name = "System",
            Publisher = "Microsoft",
            Version = "28.0.31234.0",
        });
        await ctx.SaveChangesAsync();
        return release.Id;
    }

    /// <summary>A solution tracking one repository, as somebody would have pasted its URL.</summary>
    /// <param name="withProduction">
    /// Whether the solution is live, with a production environment on 28.2 - what most
    /// tests measure against. Off for the tests that seed their own environments.
    /// </param>
    private async Task<int> SeedSolutionAsync(
        string fullName, ProjectVisibility visibility = ProjectVisibility.Public, string? name = null,
        bool withProduction = true)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name ?? "CRONUS A/S payments",
            DefaultArtifactCountry = "dk",
            Visibility = visibility,
            CreatedByUserId = visibility == ProjectVisibility.Private ? null : UserId,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories =
            [
                new OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = RepositoryProvider.GitHub,
                    Url = $"https://github.com/{fullName}",
                    DisplayName = fullName.Split('/')[^1],
                },
            ],
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        if (withProduction) await SeedEnvironmentAsync(project.Id, "Production", "28.2.45123.0");
        return project.Id;
    }

    /// <summary>One Business Central environment of a solution, as the last refresh left it.</summary>
    private async Task<int> SeedEnvironmentAsync(
        int projectId, string name, string? version, string type = "Production", DateTime? softDeletedOn = null)
    {
        await using var ctx = _db.NewContext();
        var environment = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Version = version,
            SoftDeletedOn = softDeletedOn,
            FetchedAt = DateTime.UtcNow,
        };
        ctx.OeProjectEnvironments.Add(environment);
        await ctx.SaveChangesAsync();
        return environment.Id;
    }

    /// <summary>The three findings a scan of <see cref="BehindManifest"/> leaves.</summary>
    private async Task SeedDriftAsync(string fullName, int releaseId)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        foreach (var (field, current, proposed) in new[]
        {
            ("application", "27.0.0.0", "28.2.0.0"),
            ("platform", "27.0.0.0", "28.0.0.0"),
            ("dependency:" + SystemAppId, "27.0.0.0", "28.2.0.0"),
        })
        {
            ctx.GitHubRepositoryDrift.Add(new GitHubRepositoryDrift
            {
                OrganizationId = TestDb.DefaultOrgId,
                Repository = fullName,
                Path = "app.json",
                Field = field,
                Current = current,
                Proposed = proposed,
                ReleaseId = releaseId,
                DetectedAt = now,
            });
        }
        await ctx.SaveChangesAsync();
    }

    private async Task EnableAutoUpdatesAsync(int projectId, int userId = UserId)
    {
        await using var ctx = _db.NewContext();
        await ctx.OeProjects.Where(p => p.Id == projectId).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.AutoUpdatePullRequests, true)
            .SetProperty(p => p.AutoUpdatePullRequestsByUserId, (int?)userId));
    }

    private async Task SeedOfferedAsync(string fullName, string version, int number, bool automatic)
    {
        await using var ctx = _db.NewContext();
        ctx.GitHubUpdatePullRequests.Add(new GitHubUpdatePullRequest
        {
            OrganizationId = TestDb.DefaultOrgId,
            Repository = fullName.ToLowerInvariant(),
            Version = version,
            PullRequestNumber = number,
            HtmlUrl = $"https://github.com/{fullName}/pull/{number}",
            IsAutomatic = automatic,
            OpenedAt = DateTime.UtcNow.AddDays(-7),
        });
        await ctx.SaveChangesAsync();
    }

    private async Task SeedCatalogueAsync(string systemApplicationVersion)
    {
        await using var ctx = _db.NewContext();
        ctx.WellKnownDependencies.Add(new WellKnownDependency
        {
            OrganizationId = TestDb.DefaultOrgId,
            DepId = SystemAppId,
            DepName = "System Application",
            DepPublisher = "Microsoft",
            DepVersionDefault = systemApplicationVersion,
            CreatedAt = DateTime.UtcNow,
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

    private async Task ConnectOrganisationAsync()
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    private async Task LinkAsync()
    {
        var api = new FakeGitHubApi()
            .On(HttpMethod.Post, "login/oauth/access_token", HttpStatusCode.OK, FakeGitHubApi.TokenJson())
            .On(HttpMethod.Get, "/user", HttpStatusCode.OK, FakeGitHubApi.UserJson());
        await using var ctx = _db.NewContext();
        var access = _db.NewGitHubAccessService(ctx, _db.NewGitHubAppClient(ctx, api));
        await access.LinkAsync("the-code");
    }

    /// <summary>Deployment configured, organisation connected, user linked.</summary>
    private async Task ReadyAsync()
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync();
        await LinkAsync();
    }
}
