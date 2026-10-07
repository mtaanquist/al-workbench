using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Configuration;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="ProjectBuildService.BuildAsync"/> end to end with its outside world
/// faked: git and <c>alc</c> behind <see cref="IProcessRunner"/>, the artifact CDN
/// and the symbol feeds behind one HTTP handler. The fake compiler succeeds when
/// every dependency its <c>app.json</c> declares is in the package cache, and
/// fails the way <c>alc</c> does when one is not - which is what makes "the feed
/// supplied it" observable as "it compiled". Issue #901, Parts 2 and 3 - the
/// latter being a PTE resolved from another solution's retained build output.
/// </summary>
public sealed class ProjectBuildSymbolFeedTests : IDisposable
{
    private const string ArtifactVersion = "29.0.1.2";
    private const string NextMajorVersion = "30.0.7.8";
    private static readonly string[] InsiderVersions = ["31.0.1.1", NextMajorVersion, "29.1.3.4"];
    private const string CoreId = "4b915d7e-c02a-435f-85ab-649086c1e002";
    private const string CoreSymbols = "ContiniaSoftware.ContiniaCore.symbols." + CoreId;
    private const string PteId = "dddddddd-0000-0000-0000-000000000004";

    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-feed-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSymbolFeeds _http = new();
    private readonly FakeToolchain _tools;
    private readonly FakePackage _core;
    private readonly ReleaseIngests _ingests = new();

    public ProjectBuildSymbolFeedTests()
    {
        Directory.CreateDirectory(_root);
        var alc = Path.Combine(_root, "alc");
        File.WriteAllText(alc, "fake");
        _tools = new FakeToolchain(alc);
        _http.Fallback = ArtifactCdn;
        _core = _http.Add("appsource", CoreSymbols, CoreId, "Continia Core", "29.0.0.199323", application: "29.0.0");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // Three extensions in one repository: one needs an AppSource app, one needs
    // a PTE nobody publishes, one needs nothing outside the build.
    private static readonly FakeExtension[] Extensions =
    [
        new("continia-ext", "11111111-0000-0000-0000-000000000001", "CRONUS Continia Extension", [(CoreId, "Continia Core", "25.0.0.0")]),
        new("pte-ext", "22222222-0000-0000-0000-000000000002", "CRONUS PTE Extension", [(PteId, "Someone's PTE", "1.0.0.0")]),
        new("base-ext", "33333333-0000-0000-0000-000000000003", "CRONUS Base Extension", []),
    ];

    [Fact]
    public async Task An_extension_whose_dependency_the_feed_serves_compiles_and_one_nobody_serves_fails_alone()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS Continia Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        Status(outcome, "CRONUS Base Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Failed);
        _tools.SeenVersions["CRONUS Continia Extension"][CoreId].Should().Be("29.0.0.199323");

        var log = await SymbolsLogAsync(buildId);
        log.Should().Contain("Resolved Continia Core 29.0.0.199323 from the AppSource symbol feed.");
        log.Should().Contain($"Could not resolve Someone's PTE ({PteId}) 1.0.0.0 or later")
            .And.Contain("AppSource symbol feed").And.Contain("Microsoft symbol feed");
    }

    [Fact]
    public async Task An_unreachable_feed_fails_only_the_extension_that_needed_it()
    {
        _http.Down.Add("appsource");
        _http.Down.Add("mssymbols");
        var (projectId, releaseId, buildId) = await SeedAsync();

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS Continia Extension").Should().Be(ProjectBuildResultStatus.Failed);
        Status(outcome, "CRONUS Base Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        outcome.Uploads.Should().ContainSingle("the base extension still compiled and goes on to ingest");
        (await SymbolsLogAsync(buildId)).Should().Contain("could not be reached");
    }

    [Fact]
    public async Task A_stored_upload_beats_the_feed()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await using (var seed = _db.NewContext())
        {
            // Same app, an older build, under the very file name the feed would use.
            var content = SyntheticApp.Build(CoreId, "Continia Core", "Continia", "28.0.0.7");
            seed.OeProjectSymbols.Add(new OeProjectSymbol
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                FileName = "Test_ContiniaCore_29.0.0.199323.app",
                Content = content,
                ContentLength = content.Length,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS Continia Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        _tools.SeenVersions["CRONUS Continia Extension"][CoreId].Should().Be("28.0.0.7", "the upload is the deliberate override");
        _http.Requests.Should().NotContain(r => r.Contains(CoreId, StringComparison.OrdinalIgnoreCase),
            "an app a stored upload supplies is never fetched");
    }

    // ── The pipeline's watched branch (#963) ───────────────────────────

    [Fact]
    public async Task A_manual_build_clones_the_branch_its_pipeline_watches()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SetBuildAsync(buildId, branch: "release/29", trigger: ProjectBuildTrigger.Manual);

        await BuildAsync(projectId, releaseId);

        var clone = _tools.Clones.Should().ContainSingle().Subject;
        var at = clone.ToList().IndexOf("--branch");
        at.Should().BeGreaterThan(0, "the watched branch is checked out, not the default");
        clone[at + 1].Should().Be("release/29");
        clone[^2].Should().Be("https://github.com/cronus/extensions", "the branch goes before the repository, as an option");
    }

    [Fact]
    public async Task A_manual_build_with_no_branch_clones_the_default_branch()
    {
        var (projectId, releaseId, _) = await SeedAsync();

        await BuildAsync(projectId, releaseId);

        _tools.Clones.Should().ContainSingle().Which.Should().NotContain("--branch");
    }

    [Fact]
    public async Task A_build_of_the_default_branch_records_which_branch_that_was()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();

        await BuildAsync(projectId, releaseId);

        await using var read = _db.NewContext();
        var build = await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        build.Branch.Should().BeNull("the branch rule still reads a null as the default branch");
        build.DefaultBranch.Should().Be("main");
    }

    [Fact]
    public async Task A_build_of_a_named_branch_records_no_default_branch()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SetBuildAsync(buildId, branch: "release/29", trigger: ProjectBuildTrigger.Manual);

        await BuildAsync(projectId, releaseId);

        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.SingleAsync(b => b.Id == buildId)).DefaultBranch.Should().BeNull();
    }

    [Fact]
    public async Task A_pull_request_build_ignores_the_branch_and_keeps_its_own_head()
    {
        // Its Branch is the head ref, a provenance label; the other repositories
        // of the solution may not have that branch at all.
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SetBuildAsync(buildId, branch: "feature/vat", trigger: ProjectBuildTrigger.PullRequest);

        await BuildAsync(projectId, releaseId);

        _tools.Clones.Should().ContainSingle().Which.Should().NotContain("--branch");
    }

    [Fact]
    public async Task A_stored_branch_git_would_refuse_never_reaches_the_command_line()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SetBuildAsync(buildId, branch: "--upload-pack=touch /tmp/x", trigger: ProjectBuildTrigger.Manual);

        var act = () => BuildAsync(projectId, releaseId);

        (await act.Should().ThrowAsync<InvalidOperationException>("nothing was cloned, so nothing was found to build"))
            .WithMessage("*branch name is not one git accepts*", "the build says why nothing was cloned, not that the repository has no extensions");
        _tools.Clones.Should().BeEmpty();
    }

    // ── Running the same build again (#1110) ───────────────────────────

    [Fact]
    public async Task A_first_run_builds_where_the_branch_is_now()
    {
        var (projectId, releaseId, _) = await SeedAsync();

        await BuildAsync(projectId, releaseId);

        _tools.Checkouts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_second_run_of_the_same_build_checks_out_the_commit_its_first_run_built()
    {
        // Retry, symbol recovery and a restart resume all run the same build again; its
        // number, and every app's version, belongs to the code the first run cloned.
        const string FirstRun = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        var (projectId, releaseId, buildId) = await SeedAsync();
        await using (var seed = _db.NewContext())
        {
            var repo = await seed.OeProjectRepositories.SingleAsync(r => r.ProjectId == projectId);
            seed.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repo.Id,
                RepoUrl = repo.Url, RepoDisplayName = repo.DisplayName, CommitHash = FirstRun,
            });
            await seed.SaveChangesAsync();
        }

        await BuildAsync(projectId, releaseId);

        _tools.Checkouts.Should().Equal(FirstRun);
    }

    private async Task SetBuildAsync(int buildId, string branch, string trigger)
    {
        await using var seed = _db.NewContext();
        var build = await seed.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        build.Branch = branch;
        build.Trigger = trigger;
        await seed.SaveChangesAsync();
    }

    // ── Part 3: our own PTEs from the builds we already hold ───────────

    [Fact]
    public async Task A_PTE_another_Public_solution_built_resolves_from_its_build()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        var siblingBuild = await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.2.0.0");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Compiled,
            "nothing was committed or uploaded, and solution Contoso's build holds the PTE");
        _tools.SeenVersions["CRONUS PTE Extension"][PteId].Should().Be("1.2.0.0");
        var log = await SymbolsLogAsync(buildId);
        log.Should().Contain($"Resolved Someone's PTE 1.2.0.0 from solution Contoso's build #{siblingBuild}.");
        log.Should().NotContain("Could not resolve Someone's PTE", "the feed's miss is not the last word when a build had it");
    }

    [Fact]
    public async Task A_Read_only_solution_is_a_source_too()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.ReadOnly, "1.2.0.0");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Compiled);
    }

    [Fact]
    public async Task A_Private_solution_never_supplies_another_solution()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Private, "1.2.0.0");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Failed,
            "the build has no person behind it, so a Private solution's builds stay its own");
        (await SymbolsLogAsync(buildId)).Should()
            .Contain($"Could not resolve Someone's PTE ({PteId}) 1.0.0.0 or later")
            .And.Contain("no successful build of this solution or of a Public or Read-only solution has it");
    }

    [Fact]
    public async Task A_Private_solution_still_resolves_from_its_own_earlier_builds()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await using (var seed = _db.NewContext())
        {
            var project = await seed.OeProjects.SingleAsync(p => p.Id == projectId);
            project.Visibility = ProjectVisibility.Private;
            await seed.SaveChangesAsync();
        }
        var earlier = await SeedSiblingBuildAsync("CRONUS", ProjectVisibility.Private, "1.2.0.0", projectId: projectId);

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        (await SymbolsLogAsync(buildId)).Should().Contain($"from this solution's build #{earlier}.");
    }

    [Fact]
    public async Task Another_organisations_build_is_never_a_source()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await SeedSiblingBuildAsync("Elsewhere", ProjectVisibility.Public, "1.2.0.0", organizationId: TestDb.OtherOrgId);

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Failed,
            "the organisation's query filter scopes the lookup, and nothing here goes round it");
    }

    [Fact]
    public async Task The_version_floor_skips_an_older_artifact_for_a_newer_one()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        // The newest build carries a version below the floor; an older build carries one above it.
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.1.0.0", finishedAt: DateTime.UtcNow.AddDays(-2));
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "0.9.0.0", finishedAt: DateTime.UtcNow.AddDays(-1));

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        _tools.SeenVersions["CRONUS PTE Extension"][PteId].Should().Be("1.1.0.0", "0.9.0.0 is below the app.json floor of 1.0.0.0");
    }

    [Fact]
    public async Task Only_versions_below_the_floor_resolve_nothing()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "0.9.0.0");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Failed);
    }

    [Fact]
    public async Task A_failed_build_a_pull_request_build_or_one_on_a_newer_Business_Central_is_not_a_source()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.2.0.0", status: ProjectBuildStatus.Failed);
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.3.0.0", trigger: ProjectBuildTrigger.PullRequest);
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.4.0.0", bcVersion: "30.0");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Failed);
    }

    [Fact]
    public async Task An_app_this_build_compiles_is_never_taken_from_an_earlier_build()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        _tools.Extensions =
        [
            new("base-ext", "33333333-0000-0000-0000-000000000003", "CRONUS Base Extension", []),
            new("on-base", "44444444-0000-0000-0000-000000000004", "CRONUS On Base", [("33333333-0000-0000-0000-000000000003", "CRONUS Base Extension", "1.0.0.0")]),
        ];
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "5.0.0.0",
            appId: "33333333-0000-0000-0000-000000000003", appName: "CRONUS Base Extension");

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS On Base").Should().Be(ProjectBuildResultStatus.Compiled);
        _tools.SeenVersions["CRONUS On Base"]["33333333-0000-0000-0000-000000000003"].Should().Be("1.0.0.0",
            "the sibling this build compiles is the one its dependents see");
        (await SymbolsLogAsync(buildId)).Should().NotContain("Resolved CRONUS Base Extension");
    }

    [Fact]
    public async Task A_test_app_in_a_folder_of_any_name_is_not_built_and_the_log_says_why()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        _tools.Extensions =
        [
            new("base-ext", "33333333-0000-0000-0000-000000000003", "CRONUS Base Extension", []),
            // MyApp.Test is not a test folder by name; its dependency on Library Assert gives it away (#1130).
            new("MyApp.Test", "55555555-0000-0000-0000-000000000005", "CRONUS Base Extension Tests",
                [("33333333-0000-0000-0000-000000000003", "CRONUS Base Extension", "1.0.0.0"),
                 ("dd0be2ea-f733-4d65-bb34-a28f4624fb14", "Library Assert", "29.0.0.0")]),
        ];

        var outcome = await BuildAsync(projectId, releaseId);

        outcome.Results.Select(r => r.AppName).Should().Equal("CRONUS Base Extension");
        _tools.SeenVersions.Keys.Should().NotContain("CRONUS Base Extension Tests");
        await using var read = _db.NewContext();
        var buildLog = string.Join("\n", await read.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId && l.Section == "Build").Select(l => l.Content).ToListAsync());
        buildLog.Should().Contain("Not built: CRONUS Base Extension Tests. It depends on Microsoft's test framework");
    }

    [Fact]
    public async Task What_a_resolved_PTE_depends_on_is_fetched_from_the_feed()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        // Only the PTE extension, so nothing else asks the feed for Continia Core.
        _tools.Extensions = [Extensions[1]];
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.2.0.0",
            dependencies: [(CoreId, "Continia Core", "25.0.0.0")]);

        var outcome = await BuildAsync(projectId, releaseId);

        Status(outcome, "CRONUS PTE Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        var log = await SymbolsLogAsync(buildId);
        log.Should().Contain("Resolved Someone's PTE 1.2.0.0 from solution Contoso's build");
        log.Should().Contain("Resolved Continia Core 29.0.0.199323 from the AppSource symbol feed.");
    }

    [Fact]
    public async Task The_feed_beats_an_earlier_build()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "30.0.0.0", appId: CoreId, appName: "Continia Core");

        var outcome = await BuildAsync(projectId, releaseId);

        _tools.SeenVersions["CRONUS Continia Extension"][CoreId].Should().Be("29.0.0.199323", "a published package wins over our own build of it");
        (await SymbolsLogAsync(buildId)).Should().NotContain("Resolved Continia Core 30.0.0.0");
    }

    [Fact]
    public async Task A_stored_upload_beats_an_earlier_build()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await SeedSiblingBuildAsync("Contoso", ProjectVisibility.Public, "1.2.0.0");
        await using (var seed = _db.NewContext())
        {
            var content = SyntheticApp.Build(PteId, "Someone's PTE", "Vendor", "1.0.0.5");
            seed.OeProjectSymbols.Add(new OeProjectSymbol
            {
                OrganizationId = TestDb.DefaultOrgId,
                ProjectId = projectId,
                FileName = "Vendor_Someone's PTE_1.0.0.5.app",
                Content = content,
                ContentLength = content.Length,
                CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        var outcome = await BuildAsync(projectId, releaseId);

        _tools.SeenVersions["CRONUS PTE Extension"][PteId].Should().Be("1.0.0.5", "the upload is the deliberate override");
        (await SymbolsLogAsync(buildId)).Should().NotContain("from solution Contoso's build");
    }

    [Fact]
    public async Task A_compiled_artifact_is_stamped_with_its_app_id()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();

        await BuildAsync(projectId, releaseId);

        await using var read = _db.NewContext();
        var stamped = await read.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId)
            .Select(a => new { a.AppName, a.AppId })
            .ToListAsync();
        stamped.Should().ContainSingle(a => a.AppName == "CRONUS Base Extension")
            .Which.AppId.Should().Be("33333333-0000-0000-0000-000000000003");
    }

    /// <summary>
    /// A finished build of another (or this) solution that retained one
    /// <c>.app</c>. Returns the build id.
    /// </summary>
    private async Task<int> SeedSiblingBuildAsync(
        string solution, ProjectVisibility visibility, string version,
        string appId = PteId, string appName = "Someone's PTE",
        IReadOnlyList<(string Id, string Name, string Version)>? dependencies = null,
        int organizationId = TestDb.DefaultOrgId, int? projectId = null,
        string status = ProjectBuildStatus.Ready, string trigger = ProjectBuildTrigger.Manual,
        string bcVersion = "29.0", DateTime? finishedAt = null)
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        // A second build of the same solution reuses it; names are unique per organisation.
        projectId ??= organizationId == TestDb.DefaultOrgId
            ? await seed.OeProjects.Where(p => p.Name == solution).Select(p => (int?)p.Id).FirstOrDefaultAsync()
            : null;
        if (projectId is null)
        {
            var project = new OeProject
            {
                OrganizationId = organizationId,
                Name = solution,
                Visibility = visibility,
                CreatedAt = now,
                UpdatedAt = now,
            };
            seed.OeProjects.Add(project);
            await seed.SaveChangesAsync();
            projectId = project.Id;
        }

        var content = SyntheticApp.Build(appId, appName, "Vendor", version, dependencies);
        var build = new OeProjectBuild
        {
            OrganizationId = organizationId,
            ProjectId = projectId.Value,
            Status = status,
            Trigger = trigger,
            BcVersion = bcVersion,
            StartedAt = (finishedAt ?? now).AddMinutes(-5),
            FinishedAt = finishedAt ?? now,
        };
        build.Artifacts.Add(new OeProjectBuildArtifact
        {
            OrganizationId = organizationId,
            AppId = appId,
            FileName = $"Vendor_{appName.Replace(" ", string.Empty)}_{version}.app",
            AppName = appName,
            AppVersion = version,
            SizeBytes = content.LongLength,
            Content = content,
            CreatedAt = now,
        });
        seed.OeProjectBuilds.Add(build);
        await seed.SaveChangesAsync();
        return build.Id;
    }

    // ── Publishing only what changed (#1094) ───────────────────────────

    private const string BaseId = "33333333-0000-0000-0000-000000000003";
    private const string SalesId = "44444444-0000-0000-0000-000000000004";
    private const string PriorSha = "1111111111111111111111111111111111111111";
    private const string HeadSha = "2222222222222222222222222222222222222222";

    // Two extensions in one repository, the second built on the first.
    private static readonly FakeExtension[] Pair =
    [
        new("base-ext", BaseId, "CRONUS Base Extension", []),
        new("sales-ext", SalesId, "CRONUS Sales Extension", [(BaseId, "CRONUS Base Extension", "1.0.0.0")]),
    ];

    [Fact]
    public async Task An_unchanged_extension_carries_its_earlier_app_and_a_changed_one_gets_a_new_number()
    {
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        _tools.Diffs["sales-ext"] = "sales-ext/src/Sales.Codeunit.al\n";

        await BuildAsync(projectId, releaseId);

        var artifacts = await ArtifactsAsync(buildId);
        var carried = artifacts.Single(a => a.AppId == BaseId);
        carried.CarriedFromBuildId.Should().Be(priorId);
        carried.AppVersion.Should().Be("1.0.500.0", "an unchanged app keeps the version it was deployed as");
        carried.Content.Should().Equal(await PriorContentAsync(priorId, BaseId), "the very same .app is delivered again");
        var fresh = artifacts.Single(a => a.AppId == SalesId);
        fresh.CarriedFromBuildId.Should().BeNull();
        fresh.AppVersion.Should().Be($"1.0.{buildId}.0");
        _tools.SeenVersions["CRONUS Sales Extension"][BaseId].Should().Be("1.0.500.0",
            "the changed app compiles against the version of its sibling that is deployed beside it");
        _tools.SeenVersions.Should().NotContainKey("CRONUS Base Extension", "an unchanged app is not compiled again (#1140)");
        (await LogAsync(buildId, "Compile: CRONUS Base Extension")).Should().Contain("Not compiled").And.Contain($"build #{priorId}");

        var log = await LogAsync(buildId, "Changes");
        log.Should().Contain($"CRONUS Base Extension: unchanged since build #{priorId}, so it keeps 1.0.500.0")
            .And.Contain($"CRONUS Sales Extension: changed since build #{priorId}.");
        (await LogAsync(buildId, "Version")).Should().Contain("CRONUS Sales Extension").And.NotContain("CRONUS Base Extension");
    }

    [Fact]
    public async Task An_extension_built_on_a_changed_one_is_published_again()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        _tools.Diffs["base-ext"] = "base-ext/app.json\n";

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == null);
        (await LogAsync(buildId, "Changes")).Should().Contain("CRONUS Sales Extension: rebuilt, because CRONUS Base Extension changed.");
        _tools.DiffedFolders.Should().Equal(["base-ext"], "a dependent of a changed app needs no diff");
    }

    [Fact]
    public async Task Nothing_is_carried_when_git_cannot_compare_the_commits()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        _tools.DiffFails = true;

        await BuildAsync(projectId, releaseId);

        var artifacts = await ArtifactsAsync(buildId);
        artifacts.Should().HaveCount(2).And.OnlyContain(a => a.CarriedFromBuildId == null && a.AppVersion == $"1.0.{buildId}.0");
        (await LogAsync(buildId, "Changes")).Should().Contain("could not be compared");
    }

    [Fact]
    public async Task Every_extension_is_published_when_the_pipeline_has_the_option_off()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().HaveCount(2).And.OnlyContain(a => a.CarriedFromBuildId == null);
        _tools.DiffedFolders.Should().BeEmpty();
        (await LogAsync(buildId, "Changes")).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_pipelines_build_is_never_the_baseline()
    {
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        await using (var seed = _db.NewContext())
        {
            var other = new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "test",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            seed.OePipelines.Add(other);
            await seed.SaveChangesAsync();
            (await seed.OeProjectBuilds.SingleAsync(b => b.Id == priorId)).PipelineId = other.Id;
            await seed.SaveChangesAsync();
        }

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == null);
        (await LogAsync(buildId, "Changes")).Should().Contain("no earlier build of this pipeline produced it");
    }

    [Fact]
    public async Task A_baseline_that_recorded_its_commits_twice_still_works()
    {
        // A rebuilt or restarted build writes its commit rows again.
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        await using (var seed = _db.NewContext())
        {
            var row = await seed.OeProjectBuildRepoCommits.AsNoTracking().SingleAsync(c => c.ProjectBuildId == priorId);
            seed.OeProjectBuildRepoCommits.Add(new OeProjectBuildRepoCommit
            {
                OrganizationId = row.OrganizationId, ProjectBuildId = priorId, ProjectRepositoryId = row.ProjectRepositoryId,
                RepoUrl = row.RepoUrl, RepoDisplayName = row.RepoDisplayName, CommitHash = PriorSha,
            });
            await seed.SaveChangesAsync();
        }

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == priorId);
    }

    [Fact]
    public async Task An_app_whose_build_never_reached_a_GitHub_release_is_built_again()
    {
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true, publishesToGitHub: true);

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == null);
        (await LogAsync(buildId, "Changes")).Should().Contain($"build #{priorId} did not publish it to a GitHub release");
    }

    [Fact]
    public async Task An_app_from_a_build_that_did_reach_a_GitHub_release_is_carried()
    {
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true, publishesToGitHub: true, priorReleaseTag: "v1.0.500.0");

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == priorId);
    }

    [Fact]
    public async Task An_app_compiled_before_the_sibling_it_depends_on_was_last_rebuilt_is_built_again()
    {
        // Sales was last built in the older build; Base alone in the newer one. Sales's
        // .app was compiled against the older Base, so it does not travel with the new one.
        var (projectId, releaseId, buildId, olderId) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        int newerId;
        await using (var seed = _db.NewContext())
        {
            var older = await seed.OeProjectBuilds.Include(b => b.Artifacts).SingleAsync(b => b.Id == olderId);
            var repoId = await seed.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
            var newer = PriorBuild(projectId, older.PipelineId!.Value, DateTime.UtcNow.AddMinutes(-30), [Pair[0]], "1.0.600.0");
            seed.OeProjectBuilds.Add(newer);
            await seed.SaveChangesAsync();
            seed.OeProjectBuildRepoCommits.Add(Commit(newer.Id, repoId, PriorSha));
            await seed.SaveChangesAsync();
            newerId = newer.Id;
        }

        await BuildAsync(projectId, releaseId);

        var artifacts = await ArtifactsAsync(buildId);
        artifacts.Single(a => a.AppId == BaseId).CarriedFromBuildId.Should().Be(newerId);
        artifacts.Single(a => a.AppId == SalesId).CarriedFromBuildId.Should().BeNull();
        (await LogAsync(buildId, "Changes")).Should().Contain("CRONUS Sales Extension: rebuilt, because CRONUS Base Extension changed.");
    }

    [Fact]
    public async Task An_app_built_for_another_Business_Central_version_is_built_again()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: true, priorBcVersion: "28.5");

        await BuildAsync(projectId, releaseId);

        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == null);
        (await LogAsync(buildId, "Changes")).Should().Contain("targeted Business Central 28.5 and this one targets 29.0");
    }

    [Fact]
    public async Task A_stored_commit_that_is_not_a_commit_id_never_reaches_git()
    {
        var (projectId, releaseId, buildId, priorId) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        await using (var seed = _db.NewContext())
        {
            await seed.OeProjectBuildRepoCommits.Where(c => c.ProjectBuildId == priorId)
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.CommitHash, "--output=/tmp/x"));
        }

        await BuildAsync(projectId, releaseId);

        _tools.DiffedFolders.Should().BeEmpty();
        (await ArtifactsAsync(buildId)).Should().OnlyContain(a => a.CarriedFromBuildId == null);
    }

    // ── The changelog baseline ──────────────────────────────────────────

    // The build worker is shared by every build and import, so one process that never
    // exits would stop all of them until a restart (#1132).
    [Fact]
    public async Task Every_process_a_build_starts_has_a_time_limit()
    {
        var (projectId, releaseId, _, _) = await SeedChangedOnlyAsync(changedAppsOnly: true);
        _tools.Diffs["sales-ext"] = "sales-ext/src/Sales.Codeunit.al\n";
        _tools.Ancestors.Add(PriorSha);

        await BuildAsync(projectId, releaseId);

        _tools.Requests.Should().Contain(r => r.FileName == _tools.AlcPath || r.FileName == "dotnet");
        _tools.Requests.Should().Contain(r => r.Arguments.Contains("diff"));
        _tools.Requests.Should().Contain(r => r.Arguments.Contains("log"));
        _tools.Requests.Should().OnlyContain(r => r.Timeout != null && r.Timeout > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("45", 45)]
    [InlineData(null, 30)]
    [InlineData("", 30)]
    [InlineData("0", 30)]
    [InlineData("-5", 30)]
    [InlineData("soon", 30)]
    public void A_time_limit_override_must_be_a_positive_number_of_minutes(string? raw, int expectedMinutes)
    {
        ProjectBuildService.MinutesOrDefault(raw, 30).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public async Task The_changelog_is_measured_from_the_same_pipelines_last_build()
    {
        // Another pipeline of the solution watches another branch and built since; its
        // commit isn't in this pipeline's single-branch clone.
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);
        await SeedOtherBuildAsync(projectId, pipelineName: "test", OtherSha);
        _tools.Ancestors.Add(PriorSha);
        _tools.LogOutput = "2222222\u001fCRONUS Developer\u001f2026-10-01T00:00:00+00:00\u001fAdd the sales report\n";

        await BuildAsync(projectId, releaseId);

        _tools.LoggedRanges.Should().Equal([$"{PriorSha}..HEAD"]);
        (await ChangelogAsync(buildId)).Should().Equal(["Add the sales report"]);
    }

    [Theory]
    [InlineData(ProjectBuildTarget.NextMajor, ProjectBuildTrigger.Manual)]
    [InlineData(ProjectBuildTarget.Current, ProjectBuildTrigger.PullRequest)]
    public async Task A_preview_or_pull_request_build_is_never_the_changelog_baseline(string target, string trigger)
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);
        await SeedOtherBuildAsync(projectId, pipelineName: null, OtherSha, target, trigger);
        _tools.Ancestors.Add(PriorSha);

        await BuildAsync(projectId, releaseId);

        _tools.LoggedRanges.Should().Equal([$"{PriorSha}..HEAD"]);
        (await ChangelogAsync(buildId)).Should().Equal(["No new commits since the last successful build."]);
    }

    [Fact]
    public async Task A_previous_commit_that_left_the_branch_says_so()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);

        await BuildAsync(projectId, releaseId);

        _tools.LoggedRanges.Should().BeEmpty();
        (await ChangelogAsync(buildId)).Should().ContainSingle().Which.Should().Contain("is no longer in history");
    }

    [Fact]
    public async Task A_retried_build_records_its_commits_and_changelog_once()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);
        _tools.Ancestors.Add(PriorSha);
        await using (var seed = _db.NewContext())
        {
            // What the first, interrupted attempt left behind.
            var repoId = await seed.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
            seed.OeProjectBuildRepoCommits.Add(Commit(buildId, repoId, HeadSha));
            seed.OeProjectBuildCommits.Add(new OeProjectBuildCommit
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = buildId, ProjectRepositoryId = repoId,
                Message = "No new commits since the last successful build.",
            });
            await seed.SaveChangesAsync();
        }

        await BuildAsync(projectId, releaseId);

        (await ChangelogAsync(buildId)).Should().ContainSingle();
        await using var read = _db.NewContext();
        (await read.OeProjectBuildRepoCommits.CountAsync(c => c.ProjectBuildId == buildId)).Should().Be(1);
    }

    [Fact]
    public async Task A_pull_request_build_points_at_the_pull_request_for_its_commits()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);
        _tools.Ancestors.Add(PriorSha);
        await using (var seed = _db.NewContext())
        {
            await seed.OeProjectBuilds.Where(b => b.Id == buildId)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.Trigger, ProjectBuildTrigger.PullRequest));
        }

        await BuildAsync(projectId, releaseId);

        _tools.LoggedRanges.Should().BeEmpty();
        (await ChangelogAsync(buildId)).Should().Equal(["Pull request build: its commits are listed on the pull request."]);
    }

    [Fact]
    public async Task A_pipeline_moved_to_another_branch_starts_its_changelog_over()
    {
        var (projectId, releaseId, buildId, _) = await SeedChangedOnlyAsync(changedAppsOnly: false);
        _tools.Ancestors.Add(PriorSha);
        await using (var seed = _db.NewContext())
        {
            await seed.OeProjectBuilds.Where(b => b.Id == buildId)
                .ExecuteUpdateAsync(u => u.SetProperty(b => b.Branch, "develop"));
        }

        await BuildAsync(projectId, releaseId);

        (await ChangelogAsync(buildId)).Should().ContainSingle().Which.Should().StartWith("First build of this repository");
    }

    private const string OtherSha = "3333333333333333333333333333333333333333";

    /// <summary>
    /// A finished build newer than the seeded prior one at <paramref name="sha"/>: of a new
    /// pipeline called <paramref name="pipelineName"/>, or of the seeded pipeline when null.
    /// </summary>
    private async Task SeedOtherBuildAsync(int projectId, string? pipelineName, string sha,
        string target = ProjectBuildTarget.Current, string trigger = ProjectBuildTrigger.Manual)
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var pipelineId = await seed.OePipelines.Where(p => p.ProjectId == projectId).Select(p => p.Id).SingleAsync();
        if (pipelineName is not null)
        {
            var other = new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = pipelineName,
                CreatedAt = now, UpdatedAt = now,
            };
            seed.OePipelines.Add(other);
            await seed.SaveChangesAsync();
            pipelineId = other.Id;
        }
        var build = PriorBuild(projectId, pipelineId, now.AddMinutes(-30), [], "1.0.600.0");
        build.BcTarget = target;
        build.Trigger = trigger;
        seed.OeProjectBuilds.Add(build);
        await seed.SaveChangesAsync();
        var repoId = await seed.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
        seed.OeProjectBuildRepoCommits.Add(Commit(build.Id, repoId, sha));
        await seed.SaveChangesAsync();
    }

    private async Task<List<string>> ChangelogAsync(int buildId)
    {
        await using var read = _db.NewContext();
        return await read.OeProjectBuildCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId).OrderBy(c => c.Ordering).Select(c => c.Message).ToListAsync();
    }

    /// <summary>
    /// A pipeline with numbering on, an earlier finished build of it that produced
    /// both extensions as 1.0.500.0 at <see cref="PriorSha"/>, and the queued build at
    /// <see cref="HeadSha"/>.
    /// </summary>
    private async Task<(int ProjectId, int ReleaseId, int BuildId, int PriorBuildId)> SeedChangedOnlyAsync(
        bool changedAppsOnly, bool publishesToGitHub = false, string? priorReleaseTag = null, string priorBcVersion = "29.0")
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        _tools.Extensions = Pair;
        _tools.HeadSha = HeadSha;
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var repoId = await seed.OeProjectRepositories.Where(r => r.ProjectId == projectId).Select(r => r.Id).SingleAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = "main",
            AutoVersion = true,
            ChangedAppsOnly = changedAppsOnly,
            GithubReleaseRepositoryId = publishesToGitHub ? repoId : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        seed.OePipelines.Add(pipeline);
        await seed.SaveChangesAsync();

        var prior = PriorBuild(projectId, pipeline.Id, now.AddHours(-1), Pair, "1.0.500.0", priorBcVersion);
        prior.GithubReleaseTag = priorReleaseTag;
        seed.OeProjectBuilds.Add(prior);
        await seed.SaveChangesAsync();
        seed.OeProjectBuildRepoCommits.Add(Commit(prior.Id, repoId, PriorSha));
        var build = await seed.OeProjectBuilds.SingleAsync(b => b.Id == buildId);
        build.PipelineId = pipeline.Id;
        build.Trigger = ProjectBuildTrigger.Manual;
        await seed.SaveChangesAsync();
        return (projectId, releaseId, buildId, prior.Id);
    }

    private static OeProjectBuild PriorBuild(int projectId, int pipelineId, DateTime startedAt, IEnumerable<FakeExtension> apps, string version, string bcVersion = "29.0")
    {
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipelineId,
            Status = ProjectBuildStatus.Ready,
            Trigger = ProjectBuildTrigger.Manual,
            BcVersion = bcVersion,
            StartedAt = startedAt,
            FinishedAt = startedAt.AddMinutes(5),
        };
        foreach (var ext in apps)
        {
            var content = SyntheticApp.Build(ext.Id, ext.Name, "CRONUS", version);
            build.Artifacts.Add(new OeProjectBuildArtifact
            {
                OrganizationId = TestDb.DefaultOrgId,
                AppId = ext.Id,
                FileName = $"CRONUS_{ext.Name.Replace(" ", string.Empty)}_{version}.app",
                AppName = ext.Name,
                AppVersion = version,
                SizeBytes = content.LongLength,
                Content = content,
                CreatedAt = startedAt,
            });
        }
        return build;
    }

    private static OeProjectBuildRepoCommit Commit(int buildId, int repoId, string sha) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        ProjectBuildId = buildId,
        ProjectRepositoryId = repoId,
        RepoUrl = "https://github.com/cronus/extensions",
        RepoDisplayName = "cronus/extensions",
        CommitHash = sha,
    };

    private async Task<List<OeProjectBuildArtifact>> ArtifactsAsync(int buildId)
    {
        await using var read = _db.NewContext();
        return await read.OeProjectBuildArtifacts.AsNoTracking().Where(a => a.ProjectBuildId == buildId).ToListAsync();
    }

    private async Task<byte[]> PriorContentAsync(int buildId, string appId)
    {
        await using var read = _db.NewContext();
        return await read.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == buildId && a.AppId == appId).Select(a => a.Content).SingleAsync();
    }

    private async Task<string> LogAsync(int buildId, string section)
    {
        await using var read = _db.NewContext();
        var sections = await read.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId && l.Section == section)
            .Select(l => l.Content)
            .ToListAsync();
        return string.Join("\n", sections);
    }

    // ── Part 4: the vendor package lands in the Object Explorer ──────────

    [Fact]
    public async Task The_vendor_package_is_ingested_once_and_linked_to_every_build_that_resolved_it()
    {
        _core.SymbolReferenceJson = ReleaseDependencyChainTests.Symbols(("Codeunits", 70000, "Continia Document Handler", "Run"));
        var (projectId, firstRelease, firstBuild) = await SeedAsync();

        await BuildAsync(projectId, firstRelease);
        var (secondRelease, _) = await AddBuildAsync(projectId);
        await BuildAsync(projectId, secondRelease);

        await using var read = _db.NewContext();
        var microsoftId = await read.OeReleases.AsNoTracking()
            .Where(r => r.DedupKey == "bc-onprem:29.0:dk").Select(r => r.Id).SingleAsync();
        var vendor = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Kind == "third_party");
        vendor.DedupKey.Should().Be($"symbols:{CoreId}:29.0.0.199323");
        vendor.Label.Should().Be("Continia Core 29.0.0.199323 (symbols)");
        vendor.ParentReleaseId.Should().Be(microsoftId, "the vendor sits on the Microsoft release the build resolved");
        vendor.Status.Should().Be("ready");
        (await read.OeModuleObjects.AsNoTracking().Where(o => o.Module!.ReleaseId == vendor.Id).Select(o => o.Name).ToListAsync())
            .Should().Equal("Continia Document Handler");
        (await LinksAsync(firstRelease)).Should().Equal(vendor.Id);
        (await LinksAsync(secondRelease)).Should().Equal(vendor.Id);
        (await SymbolsLogAsync(firstBuild)).Should().Contain("Added Continia Core 29.0.0.199323 (symbols) to the Object Explorer.");
    }

    [Fact]
    public async Task A_rebuild_replaces_the_releases_dependency_links()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        await BuildAsync(projectId, releaseId);
        await using (var seed = _db.NewContext())
        {
            // A link an earlier build left that this one no longer needs.
            var stale = await seed.OeReleases.Where(r => r.DedupKey == "bc-onprem:29.0:dk").Select(r => r.Id).SingleAsync();
            seed.OeReleaseDependencies.Add(new OeReleaseDependency
            {
                OrganizationId = TestDb.DefaultOrgId, ReleaseId = releaseId, DependencyReleaseId = stale, CreatedAt = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await BuildAsync(projectId, releaseId);

        await using var read = _db.NewContext();
        var vendorId = await read.OeReleases.AsNoTracking().Where(r => r.Kind == "third_party").Select(r => r.Id).SingleAsync();
        (await LinksAsync(releaseId)).Should().Equal(vendorId);
    }

    private async Task<List<int>> LinksAsync(int releaseId)
    {
        await using var read = _db.NewContext();
        return await read.OeReleaseDependencies.AsNoTracking()
            .Where(d => d.ReleaseId == releaseId).Select(d => d.DependencyReleaseId).ToListAsync();
    }

    /// <summary>A second pipeline build of the same solution, into its own project release.</summary>
    private async Task<(int ReleaseId, int BuildId)> AddBuildAsync(int projectId)
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "CRONUS",
            Kind = "project",
            Status = "ingesting",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        seed.OeReleases.Add(release);
        await seed.SaveChangesAsync();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            ReleaseId = release.Id,
            Status = ProjectBuildStatus.Queued,
            StartedAt = now,
        };
        seed.OeProjectBuilds.Add(build);
        await seed.SaveChangesAsync();
        return (release.Id, build.Id);
    }

    // ── Building against the next version (#993) ──────────────────────

    private AlCompilerProvisioner FeedCompilers(FakeNuGet nuget) =>
        new(nuget, NullLogger<AlCompilerProvisioner>.Instance,
            new AlCompilerOptions { InstallDirectory = Path.Combine(_root, "altool") });

    private async Task<int> SeedPreviewAsync(string dedupKey, string? bcVersion, DateTime importedAt, string status = "ready")
    {
        await using var seed = _db.NewContext();
        var preview = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 30.0 (DK) Preview",
            DedupKey = dedupKey,
            Kind = "first_party",
            Status = status,
            IsPrerelease = true,
            BcVersion = bcVersion,
            ImportedAt = importedAt,
            CreatedAt = importedAt,
            UpdatedAt = importedAt,
        };
        seed.OeReleases.Add(preview);
        await seed.SaveChangesAsync();
        return preview.Id;
    }

    [Fact]
    public async Task A_current_build_keeps_the_shipped_artifact_and_the_stable_compiler()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var nuget = new FakeNuGet("18.0.41.62505", "30.0.42.32495-beta");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.Current, FeedCompilers(nuget));

        outcome.BcVersion.Should().Be("29.0");
        outcome.FinalLabel.Should().Be("CRONUS on BC 29.0");
        _tools.CompilersRun.Should().NotBeEmpty().And.OnlyContain(p => p.Contains("/18.0.41.62505/"));
        _http.Requests.Should().NotContain(u => u.Contains(BcArtifactIndex.InsiderCdnHost));
        nuget.Downloads.Should().Equal("18.0.41.62505");
    }

    [Fact]
    public async Task A_next_major_build_compiles_against_the_insider_artifact_with_the_beta_compiler()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        var previewId = await SeedPreviewAsync("bc-insider:30.0:dk", NextMajorVersion, DateTime.UtcNow);
        var nuget = new FakeNuGet("18.0.41.62505", "30.0.42.32495-beta");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor, FeedCompilers(nuget));

        Status(outcome, "CRONUS Base Extension").Should().Be(ProjectBuildResultStatus.Compiled);
        outcome.BcVersion.Should().Be("30.0");
        outcome.FinalLabel.Should().Be("CRONUS on BC 30.0");
        outcome.ParentReleaseId.Should().Be(previewId, "the build parents onto the catalogue's preview of that version");
        outcome.IsPreview.Should().BeTrue("the worker skips the Object Explorer index for it (#1140)");
        _tools.CompilersRun.Should().NotBeEmpty().And.OnlyContain(p => p.Contains("/30.0.42.32495-beta/"));
        _http.Requests.Should().Contain($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/{NextMajorVersion}/dk");
        // The feed picks third-party symbols for the target version, not the manifests' 29.0.
        _tools.SeenVersions["CRONUS Continia Extension"][CoreId].Should().Be("29.0.0.199323");

        await using var read = _db.NewContext();
        var buildLog = string.Join("\n", await read.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId && l.Section == "Build").Select(l => l.Content).ToListAsync());
        buildLog.Should().Contain($"preview build {NextMajorVersion} (dk)").And.Contain("AL compiler 30.0.42.32495-beta");
    }

    [Fact]
    public async Task A_build_started_from_a_next_major_pipeline_takes_its_target_off_the_build_row()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            // What StartBuildAsync snapshots from a pipeline set to Next major (#994).
            await ctx.OeProjectBuilds.Where(b => b.Id == buildId)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMajor));
        }
        var nuget = new FakeNuGet("18.0.41.62505", "30.0.42.32495-beta");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.Current, FeedCompilers(nuget));

        outcome.BcVersion.Should().Be("30.0");
        _http.Requests.Should().Contain($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/{NextMajorVersion}/dk");
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == buildId))
            .BcArtifactVersion.Should().Be(NextMajorVersion, "the exact preview build is recorded on the build");
    }

    [Fact]
    public async Task An_explicit_next_version_target_is_written_onto_the_build_row()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();

        await BuildAsync(projectId, releaseId, BcBuildTarget.NextMinor);

        // Every guard (publishing, deploying, symbols) reads the row.
        await using var read = _db.NewContext();
        (await read.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == buildId))
            .BcTarget.Should().Be(ProjectBuildTarget.NextMinor);
    }

    [Fact]
    public async Task A_current_build_records_the_exact_business_central_build_it_used()
    {
        var (projectId, releaseId, buildId) = await SeedAsync();

        var outcome = await BuildAsync(projectId, releaseId);

        await using var read = _db.NewContext();
        var stored = (await read.OeProjectBuilds.AsNoTracking().SingleAsync(b => b.Id == buildId)).BcArtifactVersion;
        stored.Should().NotBeNullOrEmpty().And.StartWith(outcome.BcVersion + ".");
    }

    [Fact]
    public async Task A_next_minor_build_resolves_the_next_minor_with_the_stable_compiler()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var nuget = new FakeNuGet("18.0.41.62505", "30.0.42.32495-beta");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMinor, FeedCompilers(nuget));

        outcome.BcVersion.Should().Be("29.1");
        _tools.CompilersRun.Should().NotBeEmpty().And.OnlyContain(p => p.Contains("/18.0.41.62505/"));
        _http.Requests.Should().Contain($"https://{BcArtifactIndex.InsiderCdnHost}/sandbox/29.1.3.4/dk");
    }

    [Fact]
    public async Task A_next_version_build_leaves_refreshing_a_stale_preview_parent_to_the_daily_sweep()
    {
        // It indexes nothing of its own (#1140), so importing 2 GB of preview to parent
        // onto would buy it nothing; it links what is there.
        var (projectId, releaseId, _) = await SeedAsync();
        var staleId = await SeedPreviewAsync("bc-insider:30.0:dk", "30.0.1.1", DateTime.UtcNow.AddDays(-20));

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        outcome.ParentReleaseId.Should().Be(staleId);
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().CountAsync(r => r.DedupKey == "bc-insider:30.0:dk")).Should().Be(1);
    }

    [Fact]
    public async Task A_next_version_build_never_adopts_a_failed_preview_parent_nor_imports_one()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var failedId = await SeedPreviewAsync("bc-insider:30.0:dk", null, DateTime.UtcNow, status: "failed");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        outcome.ParentReleaseId.Should().BeNull();
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == failedId)).DeletedAt.Should().BeNull();
        (await read.OeReleases.AsNoTracking().CountAsync(r => r.DedupKey == "bc-insider:30.0:dk")).Should().Be(1);
    }

    [Fact]
    public async Task A_recent_preview_parent_is_kept_even_when_a_newer_insider_build_exists()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var recentId = await SeedPreviewAsync("bc-insider:30.0:dk", "30.0.1.1", DateTime.UtcNow.AddDays(-2));

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        outcome.ParentReleaseId.Should().Be(recentId);
    }

    // ── A parent import nobody finishes (#1180) ────────────────────────

    private const string ParentDedupKey = "bc-onprem:29.0:dk";

    [Fact]
    public async Task A_parent_left_importing_with_nothing_working_on_it_is_failed_rather_than_waited_on()
    {
        var (projectId, releaseId, _) = await SeedAsync(parentStatus: "ingesting");
        var clock = new SkippingClock();

        var outcome = await BuildAsync(projectId, releaseId, clock: clock).WaitAsync(TimeSpan.FromMinutes(2));

        outcome.ParentReleaseId.Should().BeNull("a half-imported parent would drop this build's references into it");
        clock.Waited.Should().BeLessThan(TimeSpan.FromMinutes(1), "nothing is importing it, so there is nothing to wait for");
        await using var read = _db.NewContext();
        var parent = await read.OeReleases.AsNoTracking().SingleAsync(r => r.DedupKey == ParentDedupKey);
        parent.Status.Should().Be("failed");
        parent.StatusMessage.Should().Be(ProjectBuildService.AbandonedImportMessage);
    }

    [Fact]
    public async Task A_build_stopped_during_its_parent_import_fails_that_import_instead_of_leaving_it_importing()
    {
        var (projectId, releaseId, _) = await SeedAsync(parentStatus: null);
        using var cts = new CancellationTokenSource();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        // Holding the import gate keeps the build waiting inside the parent import it
        // has just started, which is where a newer push or a shutdown can stop it.
        Task<ProjectBuildOutcome> build;
        int parentId;
        using (await _ingests.EnterHeavyAsync(timeout.Token))
        {
            build = BuildAsync(projectId, releaseId, ct: cts.Token);
            parentId = await ParentCreatedAsync().WaitAsync(TimeSpan.FromMinutes(2));
            _ingests.IsRunning(parentId).Should().BeTrue("a build waiting on it must not take it for abandoned");

            await cts.CancelAsync();
            var act = () => build;
            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        _ingests.IsRunning(parentId).Should().BeFalse();
        await using var read = _db.NewContext();
        var parent = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == parentId);
        parent.Status.Should().Be("failed");
        parent.StatusMessage.Should().Be(ProjectBuildService.StoppedImportMessage);
    }

    [Fact]
    public async Task A_failed_parent_is_imported_again_rather_than_handed_to_the_build()
    {
        var (projectId, releaseId, _) = await SeedAsync(parentStatus: "failed");
        int failedId;
        await using (var seeded = _db.NewContext())
        {
            failedId = await seeded.OeReleases.AsNoTracking().Where(r => r.DedupKey == ParentDedupKey).Select(r => r.Id).SingleAsync();
        }

        var outcome = await BuildAsync(projectId, releaseId);

        outcome.ParentReleaseId.Should().NotBeNull().And.NotBe(failedId);
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == failedId)).DeletedAt.Should().NotBeNull();
        var parent = await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == outcome.ParentReleaseId);
        parent.DedupKey.Should().Be(ParentDedupKey);
        parent.Status.Should().Be("ready");
    }

    private async Task<int> ParentCreatedAsync()
    {
        while (true)
        {
            await using (var read = _db.NewContext())
            {
                var id = await read.OeReleases.AsNoTracking()
                    .Where(r => r.DedupKey == ParentDedupKey)
                    .Select(r => (int?)r.Id)
                    .FirstOrDefaultAsync();
                if (id is { } found && _ingests.IsRunning(found)) return found;
            }
            await Task.Delay(50);
        }
    }

    // ── Harness ────────────────────────────────────────────────────────

    private async Task<ProjectBuildOutcome> BuildAsync(int projectId, int releaseId,
        BcBuildTarget target = BcBuildTarget.Current, AlCompilerProvisioner? compiler = null,
        TimeProvider? clock = null, CancellationToken ct = default)
    {
        await using var ctx = _db.NewContext();
        var translations = new TranslationImportService(ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations,
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance, ingests: _ingests);
        var service = new ProjectBuildService(
            ctx, _db.OrgContext, new ProjectAccess(ctx, _db.OrgContext),
            new BcArtifactService(_http, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance),
            new BcArtifactCache(new BcArtifactCacheOptions { Directory = Path.Combine(_root, "artifact-cache") },
                NullLogger<BcArtifactCache>.Instance),
            importer,
            compiler ?? new AlCompilerProvisioner(_http, NullLogger<AlCompilerProvisioner>.Instance,
                new AlCompilerOptions { ExplicitAlcPath = _tools.AlcPath }),
            new AlSymbolFeedResolver(_http, NullLogger<AlSymbolFeedResolver>.Instance, new AlSymbolFeedOptions
            {
                AppSourceFeedUrl = FakeSymbolFeeds.AppSourceIndex,
                MicrosoftFeedUrl = FakeSymbolFeeds.MicrosoftIndex,
                CacheDirectory = Path.Combine(_root, "symbol-cache"),
            }),
            // Never reached: a build started with an installation token clones as the installation.
            null!,
            _tools,
            clock ?? TimeProvider.System,
            NullLogger<ProjectBuildService>.Instance);
        return await service.BuildAsync(projectId, releaseId, new ProjectBuildOptions(InstallationToken: "installation-token", Target: target), ct);
    }

    /// <param name="parentStatus">The Microsoft release the build parents onto, or null for none.</param>
    private async Task<(int ProjectId, int ReleaseId, int BuildId)> SeedAsync(string? parentStatus = "ready")
    {
        await using var seed = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS",
            DefaultArtifactCountry = "dk",
            CreatedAt = now,
            UpdatedAt = now,
        };
        project.Repositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId,
            Provider = RepositoryProvider.GitHub,
            Url = "https://github.com/cronus/extensions",
            DisplayName = "cronus/extensions",
        });
        seed.OeProjects.Add(project);
        // The Microsoft release the build parents onto already exists, so the
        // build does not try to ingest the (empty) fake artifact.
        if (parentStatus is not null)
        {
            seed.OeReleases.Add(new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId,
                Label = "Business Central 29.0 (DK)",
                DedupKey = ParentDedupKey,
                Kind = "first_party",
                Status = parentStatus,
                ImportedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "CRONUS",
            Kind = "project",
            Status = "ingesting",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        seed.OeReleases.Add(release);
        await seed.SaveChangesAsync();

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = project.Id,
            ReleaseId = release.Id,
            Status = ProjectBuildStatus.Queued,
            StartedAt = now,
        };
        seed.OeProjectBuilds.Add(build);
        await seed.SaveChangesAsync();
        _tools.Extensions = Extensions;
        return (project.Id, release.Id, build.Id);
    }

    private async Task<string> SymbolsLogAsync(int buildId)
    {
        await using var read = _db.NewContext();
        var sections = await read.OeProjectBuildLogs.AsNoTracking()
            .Where(l => l.ProjectBuildId == buildId && l.Section == "Symbols")
            .Select(l => l.Content)
            .ToListAsync();
        return string.Join("\n", sections);
    }

    private static string Status(ProjectBuildOutcome outcome, string appName) =>
        outcome.Results.Single(r => r.AppName == appName).Status;

    /// <summary>
    /// The Business Central artifact CDNs: the public one naming one shipped
    /// version, the insider one naming the next minor, the next major and the one
    /// after it, and empty application/platform zips for each.
    /// </summary>
    private static HttpResponseMessage? ArtifactCdn(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        string[] versions;
        if (uri.Host == BcArtifactIndex.CdnHost) versions = [ArtifactVersion];
        else if (uri.Host == BcArtifactIndex.InsiderCdnHost) versions = InsiderVersions;
        else return null;
        var path = uri.AbsolutePath;
        if (path.EndsWith("/indexes/dk.json", StringComparison.Ordinal) || path.EndsWith("/indexes/platform.json", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(versions.Select(v => new { Version = v }))),
            };
        }
        if (versions.Any(v => path.EndsWith($"/{v}/dk", StringComparison.Ordinal) || path.EndsWith($"/{v}/platform", StringComparison.Ordinal)))
        {
            using var ms = new MemoryStream();
            using (new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) { }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ms.ToArray()) };
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private sealed record FakeExtension(string Folder, string Id, string Name, IReadOnlyList<(string Id, string Name, string Version)> Dependencies);

    /// <summary>git and alc. Clone writes the extensions; alc compiles when every dependency is in the package cache.</summary>
    private sealed class FakeToolchain : IProcessRunner
    {
        public FakeToolchain(string alcPath) => AlcPath = alcPath;

        public string AlcPath { get; }
        public IReadOnlyList<FakeExtension> Extensions { get; set; } = [];

        /// <summary>The compiler each compile ran: the apphost path, or the <c>alc.dll</c> <c>dotnet</c> was handed.</summary>
        public List<string> CompilersRun { get; } = new();

        /// <summary>Every process the build started, in order.</summary>
        public List<ProcessRunRequest> Requests { get; } = new();

        /// <summary>The argument list of every <c>git clone</c> run.</summary>
        public List<IReadOnlyList<string>> Clones { get; } = new();

        /// <summary>Per compiled extension: the version of each dependency the compiler found in the cache.</summary>
        public Dictionary<string, Dictionary<string, string>> SeenVersions { get; } = new();

        /// <summary>The commit every clone lands on; null makes <c>git show</c> fail, as before.</summary>
        public string? HeadSha { get; set; }

        /// <summary>What <c>git diff --name-only</c> prints per extension folder; a folder not named prints nothing.</summary>
        public Dictionary<string, string> Diffs { get; } = new(StringComparer.Ordinal);

        /// <summary>Makes every <c>git diff</c> fail, as it does when the earlier commit is not in the clone.</summary>
        public bool DiffFails { get; set; }

        /// <summary>The folder of every <c>git diff</c> run.</summary>
        public List<string> DiffedFolders { get; } = new();

        /// <summary>The commit of every <c>git checkout --detach</c> run.</summary>
        public List<string> Checkouts { get; } = new();

        /// <summary>The commits <c>git merge-base --is-ancestor</c> finds in the clone's history; any other fails it.</summary>
        public HashSet<string> Ancestors { get; } = new(StringComparer.Ordinal);

        /// <summary>What <c>git log</c> prints for the changelog.</summary>
        public string LogOutput { get; set; } = string.Empty;

        /// <summary>The range of every changelog <c>git log</c> run.</summary>
        public List<string> LoggedRanges { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (request.FileName == AlcPath)
            {
                CompilersRun.Add(AlcPath);
                return Task.FromResult(Compile(request.Arguments));
            }
            if (request.FileName == "dotnet" && request.Arguments.Count > 0 && request.Arguments[0].EndsWith("alc.dll", StringComparison.Ordinal))
            {
                CompilersRun.Add(request.Arguments[0]);
                return Task.FromResult(Compile(request.Arguments.Skip(1).ToList()));
            }
            if (request.Arguments.Count > 0 && request.Arguments[0] == "clone")
            {
                Clones.Add(request.Arguments.ToList());
                var dest = request.Arguments[^1];
                foreach (var ext in Extensions)
                {
                    var dir = Path.Combine(dest, ext.Folder);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "app.json"), JsonSerializer.Serialize(new
                    {
                        id = ext.Id,
                        name = ext.Name,
                        publisher = "CRONUS",
                        version = "1.0.0.0",
                        application = "29.0.0.0",
                        dependencies = ext.Dependencies.Select(d => new { id = d.Id, name = d.Name, publisher = "Vendor", version = d.Version }),
                    }));
                }
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            if (request.Arguments.Contains("fetch"))
            {
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            if (request.Arguments.Contains("checkout") && request.Arguments.Contains("--detach"))
            {
                Checkouts.Add(request.Arguments[^1]);
                return Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty));
            }
            if (request.Arguments.Contains("show") && HeadSha is not null)
            {
                return Task.FromResult(new ProcessRunResult(0, $"{HeadSha}\t2026-10-01T00:00:00+00:00\n", string.Empty));
            }
            if (request.Arguments.Contains("diff"))
            {
                var folder = request.Arguments[^1].Replace(":(literal)", string.Empty, StringComparison.Ordinal);
                DiffedFolders.Add(folder);
                return Task.FromResult(DiffFails
                    ? new ProcessRunResult(128, string.Empty, "fatal: bad object")
                    : new ProcessRunResult(0, Diffs.GetValueOrDefault(folder, string.Empty), string.Empty));
            }
            if (request.Arguments.Contains("merge-base"))
            {
                var ancestor = request.Arguments[request.Arguments.ToList().IndexOf("--is-ancestor") + 1];
                return Task.FromResult(Ancestors.Contains(ancestor)
                    ? new ProcessRunResult(0, string.Empty, string.Empty)
                    : new ProcessRunResult(1, string.Empty, string.Empty));
            }
            if (request.Arguments.Contains("log"))
            {
                LoggedRanges.Add(request.Arguments[^1]);
                return Task.FromResult(new ProcessRunResult(0, LogOutput, string.Empty));
            }
            // The branch a clone without --branch landed on: its default branch.
            if (request.Arguments.Contains("symbolic-ref"))
            {
                return Task.FromResult(new ProcessRunResult(0, "main\n", string.Empty));
            }
            // `git show` for provenance: not needed here.
            return Task.FromResult(new ProcessRunResult(1, string.Empty, "not a real repository"));
        }

        private ProcessRunResult Compile(IReadOnlyList<string> args)
        {
            string Arg(string name) => args.Single(a => a.StartsWith(name, StringComparison.Ordinal))[name.Length..];
            var project = Arg("/project:");
            var cache = Arg("/packagecachepath:");
            var output = Arg("/out:");
            var manifest = AppJsonManifestParser.Parse(File.ReadAllText(Path.Combine(project, "app.json")))!;

            var inCache = Directory.EnumerateFiles(cache, "*.app")
                .Select(AppPackageReader.TryReadManifest)
                .Where(m => m is not null)
                .ToDictionary(m => m!.AppId.ToString(), m => m!.Version, StringComparer.OrdinalIgnoreCase);
            var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dep in manifest.Dependencies)
            {
                if (!inCache.TryGetValue(dep.Id, out var version))
                {
                    return new ProcessRunResult(1,
                        $"error AL1022: The package containing the app '{dep.Name}' by 'Vendor' with id '{dep.Id}' could not be found.",
                        string.Empty);
                }
                seen[dep.Id] = version;
            }
            SeenVersions[manifest.Name] = seen;
            File.WriteAllBytes(output, SyntheticApp.Build(manifest.Id, manifest.Name, manifest.Publisher, manifest.Version));
            return new ProcessRunResult(0, "Compilation succeeded.", string.Empty);
        }
    }
}
