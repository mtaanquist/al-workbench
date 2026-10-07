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
/// Repository discovery (issue #629): finding the AL repositories in the
/// connected GitHub organisation that no solution tracks yet.
///
/// <para>Three rules carry this feature, and they are what these tests pin. The
/// probe has to agree with the build about what an AL repository is, or the
/// panel offers repositories that would not compile and hides ones that would.
/// "Already tracked" has to be decided against every solution in the
/// organisation - a repository a Private solution tracks must not be offered,
/// because offering it says that solution exists. And what is <em>listed</em> is
/// narrowed to what the person looking can open on GitHub themselves, asked with
/// their own token.</para>
///
/// <para>GitHub is stood in for by <see cref="FakeGitHubApi"/>; see its note.</para>
/// </summary>
public sealed class RepositoryDiscoveryServiceTests : IDisposable
{
    private const int UserId = 6290;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string RepoA = "cronus-dk/payment-import";
    private const string RepoB = "cronus-dk/warehouse-ext";

    private const string ManifestA = """
        {"id":"1c0ffee0-0000-4000-8000-000000000001","name":"Payment Import","publisher":"CRONUS","version":"1.0.0.0"}
        """;

    private const string ManifestB = """
        {"id":"1c0ffee0-0000-4000-8000-000000000002","name":"Warehouse Extras","publisher":"CRONUS","version":"2.1.0.0"}
        """;

    private readonly TestDb _db = new();

    public RepositoryDiscoveryServiceTests()
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

    // ── The probe ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_manifest_at_the_root_and_one_a_folder_down_are_both_found()
    {
        await ReadyAsync();
        var api = SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK,
                TreeJson(("app.json", "blob"), ("src/Table.al", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK,
                TreeJson(("Warehouse/app.json", "blob"), ("README.md", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", ManifestA))
            .On(HttpMethod.Get, $"/repos/{RepoB}/contents/Warehouse/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("Warehouse/app.json", ManifestB));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var found = await service.SweepCurrentOrganisationAsync();

        found.Should().Be(2);
        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryCandidates.OrderBy(c => c.FullName).ToListAsync();
        rows.Select(r => r.FullName).Should().Equal(RepoA, RepoB);
        rows[0].AppName.Should().Be("Payment Import");
        rows[0].AppJsonPath.Should().Be("app.json");
        rows[1].AppJsonPath.Should().Be("Warehouse/app.json");
        rows[1].CloneUrl.Should().Be($"https://github.com/{RepoB}.git");
    }

    [Fact]
    public async Task A_manifest_only_inside_a_test_folder_or_deeper_down_is_not_an_al_repository()
    {
        await ReadyAsync();
        var api = SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK,
                TreeJson(
                    // A test extension is not what the solution ships, and the
                    // build prunes it too.
                    ("Payment Import Tests/app.json", "blob"),
                    // Two folders down is a vendored copy or a sample, not this
                    // repository's own extension.
                    ("samples/demo/app.json", "blob"),
                    (".alpackages/app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK, TreeJson());
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        var found = await service.SweepCurrentOrganisationAsync();

        found.Should().Be(0);
        await using var read = _db.NewContext();
        (await read.GitHubRepositoryCandidates.CountAsync()).Should().Be(0);
        // No manifest was worth reading, so none was fetched.
        api.Calls.Should().NotContain(c => c.Contains("/contents/"));
    }

    [Fact]
    public async Task A_test_app_outside_a_test_folder_does_not_name_the_repository()
    {
        // "App.Test" sorts before "Payment" and no folder rule catches it; its
        // dependency on the test framework does (#1193).
        await ReadyAsync();
        var api = SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK,
                TreeJson(("App.Test/app.json", "blob"), ("Payment/app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK, TreeJson())
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/App.Test/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("App.Test/app.json", """
                    {"id":"1c0ffee0-0000-4000-8000-000000000009","name":"Payment Import Test","publisher":"CRONUS","version":"1.0.0.0",
                     "dependencies":[{"id":"23de40a6-dfe8-4f80-80db-d70f83ce8caf","name":"Test Runner","publisher":"Microsoft","version":"27.0.0.0"}]}
                    """))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/Payment/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("Payment/app.json", ManifestA));
        var (service, ctx) = NewService(api);
        await using var _ = ctx;

        (await service.SweepCurrentOrganisationAsync()).Should().Be(1);
        await using var read = _db.NewContext();
        var row = await read.GitHubRepositoryCandidates.SingleAsync();
        row.AppName.Should().Be("Payment Import");
        row.AppJsonPath.Should().Be("Payment/app.json");
    }

    [Fact]
    public async Task The_probe_and_the_manifest_read_both_carry_the_installation_token()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(SingleAlRepositoryApi());
        await using var _ = ctx;
        var api = (FakeGitHubApi)_lastApi!;

        await service.SweepCurrentOrganisationAsync();

        // Listing an organisation's repositories and probing them is an act of
        // the organisation, not of any one person.
        api.Credentials
            .Where(c => c.Call.Contains("/git/trees/") || c.Call.Contains("/contents/"))
            .Should().OnlyContain(c => c.Token == "ghs_installation");
    }

    [Fact]
    public async Task A_repository_that_stops_matching_is_dropped_and_an_ignored_one_survives()
    {
        await ReadyAsync();
        // First sweep: both repositories hold an extension.
        var (first, ctx1) = NewService(SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", ManifestA))
            .On(HttpMethod.Get, $"/repos/{RepoB}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", ManifestB)));
        await using (ctx1) await first.SweepCurrentOrganisationAsync();

        // Somebody turns the first one down.
        var ignoredId = await CandidateIdAsync(RepoA);
        var (ignoring, ctx2) = NewService(SweepableApi());
        await using (ctx2) await ignoring.IgnoreAsync(ignoredId);

        // Second sweep: the second repository's app.json is gone.
        var (second, ctx3) = NewService(SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK, TreeJson(("README.md", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", ManifestA)));
        await using (ctx3) await second.SweepCurrentOrganisationAsync();

        await using var read = _db.NewContext();
        var rows = await read.GitHubRepositoryCandidates.ToListAsync();
        rows.Select(r => r.FullName).Should().Equal(RepoA);
        rows[0].IgnoredAt.Should().NotBeNull("a repository turned down once stays turned down");
        rows[0].IgnoredByUserId.Should().Be(UserId);
    }

    [Fact]
    public async Task An_organisation_with_no_github_connection_sweeps_nothing()
    {
        await ConfigureDeploymentAsync();
        var (service, ctx) = NewService(new FakeGitHubApi());
        await using var _ = ctx;

        (await service.SweepCurrentOrganisationAsync()).Should().Be(0);
    }

    // ── What the panel is offered ────────────────────────────────────────

    [Fact]
    public async Task A_repository_a_solution_already_tracks_is_not_offered_even_when_the_viewer_cannot_see_that_solution()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        // A Private solution, owned by somebody else, tracking the first
        // repository - by the URL as a person would have pasted it, without the
        // .git GitHub's own clone URL carries.
        await SeedPrivateSolutionAsync($"https://github.com/{RepoA}");

        var (service, ctx) = NewService(VisibleToUserApi(RepoA, RepoB));
        await using var _ = ctx;

        var view = await service.ListUntrackedAsync();

        view.Rows.Select(r => r.FullName).Should().Equal(RepoB);
        view.OrgLogin.Should().Be(OrgLogin);
    }

    [Fact]
    public async Task A_repository_the_viewer_cannot_open_on_github_is_neither_listed_nor_counted()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        var (service, ctx) = NewService(VisibleToUserApi(RepoB));
        await using var _ = ctx;

        var view = await service.ListUntrackedAsync();

        view.Rows.Select(r => r.FullName).Should().Equal(RepoB);
    }

    [Fact]
    public async Task An_ignored_candidate_is_not_offered()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        var (ignoring, ctx1) = NewService(SweepableApi());
        await using (ctx1) await ignoring.IgnoreAsync(await CandidateIdAsync(RepoA));

        var (service, ctx2) = NewService(VisibleToUserApi(RepoA, RepoB));
        await using var _ = ctx2;

        (await service.ListUntrackedAsync()).Rows.Select(r => r.FullName).Should().Equal(RepoB);
    }

    [Fact]
    public async Task Undoing_a_hide_offers_the_repository_again()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        var (ignoring, ctx1) = NewService(SweepableApi());
        await using (ctx1) await ignoring.IgnoreAsync(await CandidateIdAsync(RepoA));

        var (undoing, ctx2) = NewService(SweepableApi());
        await using (ctx2) await undoing.UnignoreAsync(RepoA);

        var (service, ctx3) = NewService(VisibleToUserApi(RepoA, RepoB));
        await using var _ = ctx3;
        (await service.ListUntrackedAsync()).Rows.Select(r => r.FullName).Should().Contain(RepoA);

        await using var read = _db.NewContext();
        var row = await read.GitHubRepositoryCandidates.SingleAsync(c => c.FullName == RepoA);
        row.IgnoredAt.Should().BeNull();
        row.IgnoredByUserId.Should().BeNull();
    }

    [Fact]
    public async Task Undoing_a_hide_on_a_repository_that_is_already_offered_changes_nothing()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();

        var (undoing, ctx1) = NewService(SweepableApi());
        await using (ctx1) await undoing.UnignoreAsync(RepoA);

        await using var read = _db.NewContext();
        (await read.GitHubRepositoryCandidates.CountAsync(c => c.IgnoredAt != null)).Should().Be(0);
    }

    [Fact]
    public async Task The_country_a_new_solution_is_offered_comes_from_the_organisations_own_default()
    {
        await ReadyAsync(autoImportCountry: "dk,w1");
        await SeedCandidatesAsync();

        var (service, ctx2) = NewService(VisibleToUserApi(RepoA, RepoB));
        await using var _ = ctx2;

        (await service.ListUntrackedAsync()).SuggestedCountry.Should().Be("dk");
    }

    // ── What a person decides ────────────────────────────────────────────

    [Fact]
    public async Task Tracking_creates_the_solution_with_the_repository_attached_and_forgets_the_candidate()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        var candidateId = await CandidateIdAsync(RepoA);
        var (service, ctx) = NewService(VisibleToUserApi(RepoA));
        await using var _ = ctx;

        var projectId = await service.TrackAsync(candidateId, "CRONUS A/S payments", "dk");

        await using var read = _db.NewContext();
        var project = await read.OeProjects.Include(p => p.Repositories).FirstAsync(p => p.Id == projectId);
        project.Name.Should().Be("CRONUS A/S payments");
        project.DefaultArtifactCountry.Should().Be("dk");
        var repository = project.Repositories.Should().ContainSingle().Subject;
        repository.Provider.Should().Be(RepositoryProvider.GitHub);
        repository.Url.Should().Be($"https://github.com/{RepoA}.git");
        repository.DisplayName.Should().Be("payment-import");
        (await read.GitHubRepositoryCandidates.AnyAsync(c => c.Id == candidateId))
            .Should().BeFalse("it is tracked now, so there is nothing left to offer");
    }

    [Fact]
    public async Task Tracking_without_a_country_is_refused_beside_the_field_and_creates_nothing()
    {
        await ReadyAsync();
        await SeedCandidatesAsync();
        var candidateId = await CandidateIdAsync(RepoA);
        var (service, ctx) = NewService(VisibleToUserApi(RepoA));
        await using var _ = ctx;

        var act = () => service.TrackAsync(candidateId, "CRONUS A/S payments", " ");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("DefaultArtifactCountry");
        await using var read = _db.NewContext();
        (await read.OeProjects.CountAsync()).Should().Be(0);
        (await read.GitHubRepositoryCandidates.AnyAsync(c => c.Id == candidateId)).Should().BeTrue();
    }

    [Fact]
    public async Task Tracking_a_repository_the_person_can_no_longer_open_is_refused()
    {
        // The list was narrowed to what this person can see when it was rendered,
        // but the id posted back is the client's and access can have gone since.
        // The answer is GitHub's, asked again, not the list's.
        await ReadyAsync();
        await SeedCandidatesAsync();
        var candidateId = await CandidateIdAsync(RepoA);
        var (service, ctx) = NewService(VisibleToUserApi(RepoB));
        await using var _ = ctx;

        var act = () => service.TrackAsync(candidateId, "CRONUS A/S payments", "dk");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
        await using var read = _db.NewContext();
        (await read.OeProjects.CountAsync()).Should().Be(0, "nothing is created for a repository the person cannot read");
        (await read.GitHubRepositoryCandidates.AnyAsync(c => c.Id == candidateId)).Should().BeTrue();
    }

    [Fact]
    public async Task Tracking_a_candidate_that_is_gone_is_refused_rather_than_thrown()
    {
        await ReadyAsync();
        var (service, ctx) = NewService(SweepableApi());
        await using var _ = ctx;

        var act = () => service.TrackAsync(987654, "CRONUS A/S payments", "dk");

        (await act.Should().ThrowAsync<PlanValidationException>())
            .Which.Errors.Should().ContainKey("Name");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────

    private HttpMessageHandler? _lastApi;

    private (RepositoryDiscoveryService Service, AppDbContext Context) NewService(FakeGitHubApi api)
    {
        _lastApi = api;
        var ctx = _db.NewContext();
        var client = _db.NewGitHubAppClient(ctx, api);
        var access = _db.NewGitHubAccessService(ctx, client);
        return (_db.NewRepositoryDiscoveryService(ctx, client, access), ctx);
    }

    private static string TreeJson(params (string Path, string Type)[] entries) =>
        "{\"sha\":\"tree-sha\",\"truncated\":false,\"tree\":["
        + string.Join(',', entries.Select(e =>
            $"{{\"path\":\"{e.Path}\",\"type\":\"{e.Type}\",\"sha\":\"blob-{e.Path.GetHashCode():x}\"}}"))
        + "]}";

    /// <summary>Enough of GitHub to mint an installation token and list the organisation's two repositories.</summary>
    private static FakeGitHubApi SweepableApi() =>
        new FakeGitHubApi()
            .On(HttpMethod.Post, $"/app/installations/{InstallationId}/access_tokens",
                HttpStatusCode.Created, FakeGitHubApi.InstallationTokenJson())
            .On(HttpMethod.Get, "/installation/repositories", HttpStatusCode.OK,
                FakeGitHubApi.InstallationRepositoriesJson(RepoA, RepoB));

    /// <summary>The same, plus one repository whose root manifest reads.</summary>
    private static FakeGitHubApi SingleAlRepositoryApi() =>
        SweepableApi()
            .On(HttpMethod.Get, $"/repos/{RepoA}/git/trees/main", HttpStatusCode.OK, TreeJson(("app.json", "blob")))
            .On(HttpMethod.Get, $"/repos/{RepoB}/git/trees/main", HttpStatusCode.OK, TreeJson())
            .On(HttpMethod.Get, $"/repos/{RepoA}/contents/app.json", HttpStatusCode.OK,
                FakeGitHubApi.FileContentsJson("app.json", ManifestA));

    /// <summary>
    /// A GitHub where the acting user can open exactly <paramref name="visible"/>
    /// - everything else answers 404, which is what GitHub says about a
    /// repository you cannot see rather than one that is gone.
    /// </summary>
    private static FakeGitHubApi VisibleToUserApi(params string[] visible)
    {
        var api = SweepableApi();
        api.On(HttpMethod.Get, "/repos/", HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");
        foreach (var name in visible)
        {
            api.On(HttpMethod.Get, $"/repos/{name}", HttpStatusCode.OK, FakeGitHubApi.RepositoryJson(name));
        }
        return api;
    }

    private async Task<int> CandidateIdAsync(string fullName)
    {
        await using var ctx = _db.NewContext();
        return (await ctx.GitHubRepositoryCandidates.FirstAsync(c => c.FullName == fullName)).Id;
    }

    /// <summary>Two candidates, as a sweep would have left them.</summary>
    private async Task SeedCandidatesAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        foreach (var (fullName, appName) in new[] { (RepoA, "Payment Import"), (RepoB, "Warehouse Extras") })
        {
            ctx.GitHubRepositoryCandidates.Add(new GitHubRepositoryCandidate
            {
                OrganizationId = TestDb.DefaultOrgId,
                FullName = fullName,
                HtmlUrl = $"https://github.com/{fullName}",
                CloneUrl = $"https://github.com/{fullName}.git",
                DefaultBranch = "main",
                AppName = appName,
                AppId = Guid.NewGuid().ToString(),
                AppJsonPath = "app.json",
                DiscoveredAt = now,
                LastSeenAt = now,
            });
        }
        await ctx.SaveChangesAsync();
    }

    /// <summary>A solution the acting user has no grant on, tracking one repository.</summary>
    private async Task SeedPrivateSolutionAsync(string url)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        ctx.OeProjects.Add(new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "Someone else's customer",
            DefaultArtifactCountry = "dk",
            Visibility = ProjectVisibility.Private,
            CreatedByUserId = null,
            CreatedAt = now,
            UpdatedAt = now,
            Repositories =
            [
                new OeProjectRepository
                {
                    OrganizationId = TestDb.DefaultOrgId,
                    Provider = RepositoryProvider.GitHub,
                    Url = url,
                    DisplayName = "payment-import",
                },
            ],
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

    private async Task ConnectOrganisationAsync(string? autoImportCountry = null)
    {
        await using var ctx = _db.NewContext();
        ctx.OrganizationSettings.Add(new OrganizationSettings
        {
            OrganizationId = TestDb.DefaultOrgId,
            GitHubInstallationId = InstallationId,
            GitHubOrgLogin = OrgLogin,
            GitHubConnectedAt = DateTime.UtcNow,
            AutoImportCountry = autoImportCountry,
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
    private async Task ReadyAsync(string? autoImportCountry = null)
    {
        await ConfigureDeploymentAsync();
        await ConnectOrganisationAsync(autoImportCountry);
        await LinkAsync();
    }
}
