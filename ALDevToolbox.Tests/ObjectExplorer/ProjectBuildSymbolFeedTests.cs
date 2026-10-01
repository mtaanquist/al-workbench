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

        await act.Should().ThrowAsync<InvalidOperationException>("nothing was cloned, so nothing was found to build");
        _tools.Clones.Should().BeEmpty();
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
    public async Task A_next_version_build_replaces_a_stale_preview_parent_with_the_current_insider_build()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var staleId = await SeedPreviewAsync("bc-insider:30.0:dk", "30.0.1.1", DateTime.UtcNow.AddDays(-20));

        await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == staleId)).DeletedAt.Should().NotBeNull();
        var current = await read.OeReleases.AsNoTracking().SingleAsync(r => r.DedupKey == "bc-insider:30.0:dk" && r.DeletedAt == null);
        current.Id.Should().NotBe(staleId);
        current.IsPrerelease.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_preview_parent_is_replaced_at_once_and_never_adopted()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var failedId = await SeedPreviewAsync("bc-insider:30.0:dk", null, DateTime.UtcNow, status: "failed");

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        outcome.ParentReleaseId.Should().NotBe(failedId);
        await using var read = _db.NewContext();
        (await read.OeReleases.AsNoTracking().SingleAsync(r => r.Id == failedId)).DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_recent_preview_parent_is_kept_even_when_a_newer_insider_build_exists()
    {
        var (projectId, releaseId, _) = await SeedAsync();
        var recentId = await SeedPreviewAsync("bc-insider:30.0:dk", "30.0.1.1", DateTime.UtcNow.AddDays(-2));

        var outcome = await BuildAsync(projectId, releaseId, BcBuildTarget.NextMajor);

        outcome.ParentReleaseId.Should().Be(recentId);
    }

    // ── Harness ────────────────────────────────────────────────────────

    private async Task<ProjectBuildOutcome> BuildAsync(int projectId, int releaseId,
        BcBuildTarget target = BcBuildTarget.Current, AlCompilerProvisioner? compiler = null)
    {
        await using var ctx = _db.NewContext();
        var translations = new TranslationImportService(ctx, _db.OrgContext,
            new ALDevToolbox.Services.Translation.TranslationMemoryService(
                ctx, _db.OrgContext, NullLogger<ALDevToolbox.Services.Translation.TranslationMemoryService>.Instance),
            NullLogger<TranslationImportService>.Instance);
        var importer = new ReleaseImportService(ctx, _db.OrgContext, _db.NewQuotaGuard(ctx), translations,
            new CallSiteReferenceEmitter(ctx, NullLogger<CallSiteReferenceEmitter>.Instance),
            NullLogger<ReleaseImportService>.Instance);
        var service = new ProjectBuildService(
            ctx, _db.OrgContext,
            new BcArtifactService(_http, ctx, _db.OrgContext, NullLogger<BcArtifactService>.Instance),
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
            TimeProvider.System,
            NullLogger<ProjectBuildService>.Instance);
        return await service.BuildAsync(projectId, releaseId, new ProjectBuildOptions(InstallationToken: "installation-token", Target: target));
    }

    private async Task<(int ProjectId, int ReleaseId, int BuildId)> SeedAsync()
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
        seed.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = "Business Central 29.0 (DK)",
            DedupKey = "bc-onprem:29.0:dk",
            Kind = "first_party",
            Status = "ready",
            ImportedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
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

        /// <summary>The argument list of every <c>git clone</c> run.</summary>
        public List<IReadOnlyList<string>> Clones { get; } = new();

        /// <summary>Per compiled extension: the version of each dependency the compiler found in the cache.</summary>
        public Dictionary<string, Dictionary<string, string>> SeenVersions { get; } = new();

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
        {
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
