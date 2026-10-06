using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Services.Tools;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Pipelines dashboard at <c>/pipelines</c> (#955), in the sheet's four states:
/// populated, half empty (builds but no deployment pipeline), empty, and loading. Built
/// to <c>.design/handoff/PipelinesBody.dc.html</c>.
///
/// <para>Named user: an ops engineer opening the workbench in the morning who wants one
/// page that says what built, what shipped, what failed and what is waiting.</para>
/// </summary>
public sealed class PipelinesDashboardTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly FakeToolAvailability _tools = new();
    private readonly GatedFactory _factory;
    private readonly DateTime _now = DateTime.UtcNow;

    public PipelinesDashboardTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _factory = new GatedFactory(_db.NewContextFactory());
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        // Registered ahead of AddDisplayTimeZone, which keeps a factory already there: the
        // gate is how the loading test holds the page before its first read completes.
        _ctx.Services.AddSingleton<IDbContextFactory<AppDbContext>>(_factory);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<BuildFreshnessService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<DeliveryFeedService>();
        _ctx.Services.AddScoped<PipelinesDashboardService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton<IToolAvailability>(_tools);
        // The two editor dialogs the page hosts need their chains to resolve, though
        // nothing here opens one.
        _ctx.Services.AddHttpClient();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionService>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        _ctx.Services.AddSingleton(TimeProvider.System);
        _db.AddStorageServices(_ctx.Services);
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _db.OrgContext.IsSiteAdmin = true;
    }

    public void Dispose()
    {
        _factory.Open();
        _ctx.Dispose();
        _db.WaitForQueriesToSettle();
        _db.Dispose();
    }

    [Fact]
    public async Task Populated_the_eight_tiles_link_into_the_lists_and_the_cards_say_what_happened()
    {
        var s = await SeedFleetAsync();

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            var tiles = cut.FindAll(".cue-grid a.cue");
            tiles.Select(t => t.QuerySelector(".cue__label")!.TextContent.Trim()).Should().Equal(
                "Build pipelines", "Builds this week", "Failed builds", "Ready to build",
                "Deployment pipelines", "Shipping now", "Failed deployments", "Waiting for approval");
            tiles.Select(t => t.QuerySelector(".cue__value")!.TextContent.Trim()).Should().Equal(
                "1", "3", "1", "0", "3", "1", "1", "1");

            tiles[2].ClassList.Should().Contain("cue--attention");
            tiles[2].GetAttribute("href").Should().Be("/pipelines/builds?show=failed");
            tiles[3].ClassList.Should().NotContain("pd-cue--warning", "no branch has moved past its build");
            tiles[5].GetAttribute("href").Should().Be("/pipelines/deployments?show=shipping");
            tiles[5].QuerySelector(".cue__foot")!.TextContent.Should().Contain("App 2 of 3 to CRONUS - UAT");
            tiles[6].ClassList.Should().Contain("cue--attention");
            tiles[7].ClassList.Should().Contain("pd-cue--warning");
            tiles[7].GetAttribute("href").Should().Be("/pipelines/deployments?show=attention");

            var attention = cut.FindAll(".dash-cols .card")[0].QuerySelectorAll(".activity__row");
            var texts = attention.Select(r => r.QuerySelector(".activity__text")!.TextContent.Trim()).ToList();
            texts.Should().Contain($"Build #{s.FailedBuild} failed on CRONUS Base - main");
            texts.Should().Contain("Deployment to CRONUS - Test failed while installing CRONUS Sales");
            texts.Should().Contain($"Build #{s.ReadyBuild} is ready to deploy to CRONUS - Sandbox");
            var approval = attention.Single(r => r.TextContent.Contains("is ready to deploy"));
            approval.GetAttribute("href").Should().Be($"/pipelines/deployments/{s.Waiting}",
                "Review opens the deployment pipeline page, where approving happens");
            approval.QuerySelector(".btn")!.TextContent.Trim().Should().Be("Review");

            var activity = cut.FindAll(".dash-cols .card")[1].QuerySelectorAll("a.activity__row");
            activity.Should().NotBeEmpty();
            activity[0].TextContent.Should().Contain($"Build #{s.ReadyBuild} is deploying to CRONUS - UAT",
                "the newest thing is the deployment that started minutes ago");
            activity[0].QuerySelector(".activity__sub")!.TextContent.Trim().Should().Be("App 2 of 3");
            activity.Should().Contain(r => r.QuerySelector(".activity__avatar")!.TextContent.Trim() == "PR"
                                           && r.TextContent.Contains("For pull request #42"));
            cut.FindAll(".dash-cols .card")[1].QuerySelectorAll(".card__head a.btn").Select(a => a.TextContent.Trim())
                .Should().Equal("View all builds", "View all deployments");

            cut.FindAll(".btn--primary").Should().BeEmpty("the populated page has no primary action");
            cut.FindAll(".page-head__actions a.btn").Select(b => b.TextContent.Trim())
                .Should().Equal("New build pipeline", "New deployment pipeline");
            cut.FindAll(".page-head__actions a.btn").Select(b => b.GetAttribute("href"))
                .Should().Equal("/pipelines/new?returnUrl=%2Fpipelines", "/pipelines/deployments/new?returnUrl=%2Fpipelines");
            cut.Find(".page-head__sub").TextContent.Should().Contain("2 failing, 1 waiting for approval.");
            tiles[2].QuerySelector(".cue__foot")!.TextContent.Should().Contain("Latest failed");
        });
    }

    [Fact]
    public async Task Half_empty_builds_but_no_deployment_pipeline_says_what_is_missing()
    {
        var projectId = await SeedSolutionAsync();
        var pipeline = await SeedBuildPipelineAsync(projectId);
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddHours(-2));

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            var tiles = cut.FindAll(".cue-grid a.cue");
            tiles.Should().HaveCount(8);
            tiles.Skip(4).Select(t => t.QuerySelector(".cue__foot")!.TextContent.Trim())
                .Should().AllBe("No deployment pipeline yet");
            var quiet = cut.Find(".activity__row.pd-attn--quiet");
            quiet.TextContent.Should().Contain("Deployments are not set up. Create a deployment pipeline");
            quiet.GetAttribute("href").Should().Be("/pipelines/deployments");
            cut.FindAll("a.btn").Select(a => a.TextContent.Trim()).Should().NotContain("View all deployments");
            cut.Find(".page-head__sub").TextContent.Should().Contain("No deployment pipeline yet.");
        });
    }

    [Fact]
    public async Task The_failed_builds_tile_says_how_many_of_its_failures_are_preview_checks()
    {
        var projectId = await SeedSolutionAsync();
        var broken = await SeedBuildPipelineAsync(projectId);
        await SeedBuildAsync(projectId, broken, ProjectBuildStatus.Failed, _now.AddHours(-2));
        int checkedPipeline;
        await using (var ctx = _db.NewContext())
        {
            var pipeline = new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "CRONUS Retail",
                PreviewCheck = true, CreatedAt = _now, UpdatedAt = _now,
            };
            ctx.OePipelines.Add(pipeline);
            await ctx.SaveChangesAsync();
            checkedPipeline = pipeline.Id;
        }
        await SeedBuildAsync(projectId, checkedPipeline, ProjectBuildStatus.Ready, _now.AddHours(-3));
        await using (var ctx = _db.NewContext())
        {
            // Its own build is green; last night's check against the next major is not.
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = checkedPipeline,
                Status = ProjectBuildStatus.Failed, BcTarget = ProjectBuildTarget.NextMajor,
                Trigger = ProjectBuildTrigger.PreviewCheck,
                StartedAt = _now.AddHours(-1), FinishedAt = _now.AddMinutes(-56),
            });
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            var failed = cut.FindAll(".cue-grid a.cue")
                .Single(t => t.QuerySelector(".cue__label")!.TextContent.Trim() == "Failed builds");
            failed.QuerySelector(".cue__value")!.TextContent.Trim().Should().Be("2");
            failed.QuerySelector(".cue__foot")!.TextContent.Trim().Should().Be("2 failed (1 in a preview check)");
            failed.ClassList.Should().Contain("cue--attention");
        });
    }

    [Fact]
    public async Task Empty_offers_the_first_build_pipeline_as_the_one_primary_button()
    {
        await SeedSolutionAsync();

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No pipelines yet");
            cut.Find(".empty-state__text").TextContent.Should().Contain("when you press Build, when new")
                .And.NotContain("every push");
            cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Should().Contain("New build pipeline");
            cut.FindAll(".empty-state__action a.btn").Select(b => b.TextContent.Trim())
                .Should().Equal("New build pipeline", "New deployment pipeline");
            cut.FindAll(".page-head__actions").Should().BeEmpty("the empty state carries the next step itself");
            cut.FindAll(".cue-grid").Should().BeEmpty();
        });
    }

    [Fact]
    public void Without_a_solution_the_next_step_is_to_create_one()
    {
        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No solutions yet");
            cut.Find(".empty-state__action a.btn--primary").GetAttribute("href").Should().Be("/solutions/new");
        });
    }

    [Fact]
    public async Task Loading_shows_the_loading_block_where_the_tiles_go()
    {
        await SeedSolutionAsync();
        _factory.Close();

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".loading-block").TextContent.Should().Contain("Loading builds and deployments...");
            cut.FindAll(".cue-grid").Should().BeEmpty();
            cut.FindAll(".page-head__actions").Should().BeEmpty("nothing is known yet to act on");
        });

        _factory.Open();
        cut.WaitForAssertion(() => cut.FindAll(".loading-block").Should().BeEmpty());
    }

    [Fact]
    public async Task With_deployment_pipelines_switched_off_only_the_build_half_is_drawn()
    {
        await SeedFleetAsync();
        _tools.Disabled.Add(ToolKey.Releases);

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".cue-grid a.cue").Select(t => t.QuerySelector(".cue__label")!.TextContent.Trim())
                .Should().Equal("Build pipelines", "Builds this week", "Failed builds", "Ready to build");
            cut.FindAll("a[href^='/pipelines/deployments']").Should().BeEmpty();
            cut.FindAll(".page-head__actions a.btn").Select(b => b.TextContent.Trim()).Should().Equal("New build pipeline");
        });
    }

    [Fact]
    public async Task A_branch_ahead_of_its_last_build_is_a_ready_to_build_tile_and_an_attention_row()
    {
        var projectId = await SeedSolutionAsync();
        var pipeline = await SeedBuildPipelineAsync(projectId);
        var repo = await FreshnessSeed.AddRepositoryAsync(_db, projectId);
        await FreshnessSeed.AddBuildAsync(_db, projectId, pipeline, repo);
        await FreshnessSeed.AddHeadAsync(_db, repo, "main", FreshnessSeed.Newest,
            commits: [FreshnessSeed.Built, FreshnessSeed.Newer, FreshnessSeed.Newest]);

        var cut = _ctx.Render<PipelinesDashboard>();

        cut.WaitForAssertion(() =>
        {
            var tile = cut.FindAll(".cue-grid a.cue").Single(t => t.QuerySelector(".cue__label")!.TextContent.Trim() == "Ready to build");
            tile.QuerySelector(".cue__value")!.TextContent.Trim().Should().Be("1");
            tile.GetAttribute("href").Should().Be("/pipelines/builds?show=ready", "the tile lands on the list's Ready to build tab");
            tile.ClassList.Should().Contain("pd-cue--warning");
            tile.QuerySelector(".cue__foot")!.TextContent.Should().Contain("Latest push");

            var row = cut.FindAll(".dash-cols .card")[0].QuerySelectorAll("a.activity__row")
                .Single(r => r.TextContent.Contains("ahead of its last build"));
            row.QuerySelector(".activity__text")!.TextContent.Trim().Should().Be("CRONUS Base is 2 commits ahead of its last build");
            row.QuerySelector(".activity__sub")!.TextContent.Trim().Should().Be("CRONUS - main");
            row.GetAttribute("href").Should().Be($"/pipelines/{pipeline}", "Build is on the pipeline's page");
            row.ClassList.Should().Contain("is-warning");
            cut.Find(".page-head__sub").TextContent.Should().NotContain("failing");
        });
    }

    [Fact]
    public async Task Merged_pull_requests_are_named_and_a_pipeline_already_building_is_not_ready()
    {
        var projectId = await SeedSolutionAsync();
        var pipeline = await SeedBuildPipelineAsync(projectId);
        var repo = await FreshnessSeed.AddRepositoryAsync(_db, projectId);
        await FreshnessSeed.AddBuildAsync(_db, projectId, pipeline, repo);
        await FreshnessSeed.AddHeadAsync(_db, repo, "main", FreshnessSeed.Newest,
            commits: [FreshnessSeed.Built, FreshnessSeed.Newer, FreshnessSeed.Newest]);
        await FreshnessSeed.AddMergedAsync(_db, repo, 12, "Post VAT on prepayments", FreshnessSeed.Newer);
        await FreshnessSeed.AddMergedAsync(_db, repo, 13, "Round VAT per line", FreshnessSeed.Newest);

        var cut = _ctx.Render<PipelinesDashboard>();
        cut.WaitForAssertion(() =>
            cut.FindAll(".activity__text").Select(t => t.TextContent.Trim())
                .Should().Contain("CRONUS Base has 2 pull requests merged since its last build"));

        // A build of it is now queued: that build is the answer, so it stops asking for one.
        await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Queued, _now);
        var again = _ctx.Render<PipelinesDashboard>();
        again.WaitForAssertion(() =>
        {
            again.FindAll(".cue-grid a.cue").Single(t => t.QuerySelector(".cue__label")!.TextContent.Trim() == "Ready to build")
                .QuerySelector(".cue__value")!.TextContent.Trim().Should().Be("0");
            again.FindAll(".activity__text").Select(t => t.TextContent.Trim())
                .Should().NotContain(t => t.Contains("merged since its last build"));
        });
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record Fleet(int ProjectId, int ReadyBuild, int FailedBuild, int Waiting);

    /// <summary>
    /// One solution with one build pipeline (a ready build, a pull-request build and a
    /// failed newest build) and three deployment pipelines: one installing app 2 of 3, one
    /// whose last deployment failed on its second app, and one holding a prepared deployment.
    /// </summary>
    private async Task<Fleet> SeedFleetAsync()
    {
        var projectId = await SeedSolutionAsync();
        var pipeline = await SeedBuildPipelineAsync(projectId);
        var ready = await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Ready, _now.AddDays(-1));
        await using var ctx = _db.NewContext();
        ctx.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipeline,
            Status = ProjectBuildStatus.Ready, Trigger = ProjectBuildTrigger.PullRequest, PullRequestNumber = 42,
            Branch = "feature/x", StartedAt = _now.AddHours(-3), FinishedAt = _now.AddHours(-3).AddMinutes(4),
        });
        await ctx.SaveChangesAsync();
        var failed = await SeedBuildAsync(projectId, pipeline, ProjectBuildStatus.Failed, _now.AddHours(-1), "main");

        var uat = Environment(projectId, "UAT");
        var test = Environment(projectId, "Test");
        var sandbox = Environment(projectId, "Sandbox");
        ctx.OeProjectEnvironments.AddRange(uat, test, sandbox);
        await ctx.SaveChangesAsync();
        var live = ReleasePipeline(projectId, "CRONUS to UAT", pipeline, uat.Id);
        var failing = ReleasePipeline(projectId, "CRONUS to Test", pipeline, test.Id);
        var waiting = ReleasePipeline(projectId, "CRONUS to Sandbox", pipeline, sandbox.Id);
        ctx.OeReleasePipelines.AddRange(live, failing, waiting);
        await ctx.SaveChangesAsync();

        ctx.OeProjectDeliveries.AddRange(
            Delivery(projectId, live.Id, ready, "UAT", ProjectDeliveryStatus.Installing, _now.AddMinutes(-3), null,
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Installing, ProjectDeliveryResultStatus.Pending),
            Delivery(projectId, failing.Id, ready, "Test", ProjectDeliveryStatus.Failed, _now.AddHours(-5), _now.AddHours(-5).AddMinutes(2),
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed),
            Delivery(projectId, waiting.Id, ready, "Sandbox", ProjectDeliveryStatus.Proposed, _now.AddDays(-2), null));
        await ctx.SaveChangesAsync();

        return new Fleet(projectId, ready, failed, waiting.Id);
    }

    private async Task<int> SeedSolutionAsync()
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS", CreatedAt = _now, UpdatedAt = _now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    private async Task<int> SeedBuildPipelineAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "CRONUS Base", CreatedAt = _now, UpdatedAt = _now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        return pipeline.Id;
    }

    private async Task<int> SeedBuildAsync(int projectId, int pipelineId, string status, DateTime startedAt, string? branch = null)
    {
        await using var ctx = _db.NewContext();
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId, Status = status,
            Branch = branch, StartedAt = startedAt, FinishedAt = startedAt.AddMinutes(4),
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private OeProjectEnvironment Environment(int projectId, string name) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = "Sandbox",
        Status = "Active", FetchedAt = _now,
    };

    private OeReleasePipeline ReleasePipeline(int projectId, string name, int buildPipelineId, int environmentId) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name,
        BuildPipelineId = buildPipelineId, ProjectEnvironmentId = environmentId,
        DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
        CreatedAt = _now, UpdatedAt = _now,
    };

    private static OeProjectDelivery Delivery(
        int projectId, int releasePipelineId, int buildId, string environment, string status, DateTime at, DateTime? finishedAt,
        params string[] apps)
    {
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, ReleasePipelineId = releasePipelineId,
            ProjectBuildId = buildId, EnvironmentName = environment, Status = status,
            ScheduledFor = at, StartedAt = status == ProjectDeliveryStatus.Proposed ? null : at, FinishedAt = finishedAt,
            CreatedAt = at, UpdatedAt = finishedAt ?? at,
        };
        string[] names = ["CRONUS Base", "CRONUS Sales", "CRONUS Reports"];
        for (var i = 0; i < apps.Length; i++)
        {
            delivery.Results.Add(new OeProjectDeliveryResult
            {
                OrganizationId = TestDb.DefaultOrgId, Ordering = i, AppName = names[i], AppVersion = "1.0.0.0",
                Status = apps[i], CreatedAt = at, UpdatedAt = at,
            });
        }
        return delivery;
    }

    private sealed class FakeToolAvailability : IToolAvailability
    {
        public HashSet<ToolKey> Disabled { get; } = new();
        public bool IsSiteEnabled(ToolKey key) => !Disabled.Contains(key);
    }

    /// <summary>
    /// A context factory that can be held shut. The page's first read is the display time
    /// zone, which comes through here, so a closed gate keeps the page in its loading state
    /// for as long as the test needs to look at it.
    /// </summary>
    private sealed class GatedFactory : IDbContextFactory<AppDbContext>
    {
        private readonly IDbContextFactory<AppDbContext> _inner;
        private TaskCompletionSource _gate = CompletedGate();

        public GatedFactory(IDbContextFactory<AppDbContext> inner) => _inner = inner;

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate.TrySetResult();

        public AppDbContext CreateDbContext() => _inner.CreateDbContext();

        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            await _gate.Task.WaitAsync(cancellationToken);
            return _inner.CreateDbContext();
        }

        private static TaskCompletionSource CompletedGate()
        {
            var done = new TaskCompletionSource();
            done.SetResult();
            return done;
        }
    }
}
