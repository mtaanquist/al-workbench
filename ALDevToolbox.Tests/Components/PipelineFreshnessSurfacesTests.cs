using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ALDevToolbox.Tests.Infrastructure.FreshnessSeed;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Whether a build pipeline's branch has moved past its last build, where a person looks
/// at a pipeline (#964): the line under the name on the Builds list, with Build beside an
/// ahead row and the "Ready to build" tab, and the Branch card on the pipeline's page.
/// The rows are the ones <c>BuildFreshnessService</c> reads, seeded as its own tests seed
/// them.
///
/// <para>Named user: a BC consultant opening the Builds list in the morning to see which
/// customers need a build before today's deployments.</para>
/// </summary>
public sealed class PipelineFreshnessSurfacesTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int OwnerUserId = 9640;
    private const int ColleagueUserId = 9641;

    public PipelineFreshnessSurfacesTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddSingleton<ALDevToolbox.Services.Tools.IToolAvailability>(TestDb.EverythingEnabled());
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<BuildFreshnessService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        // The Build button's service. Nothing here builds, so only what it keeps for
        // itself is real.
        _ctx.Services.AddScoped(sp => new ProjectBuildImporter(
            null!, null!, new ALDevToolbox.Services.ObjectExplorer.Import.ProjectBuildQueue(), null!,
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), _db.OrgContext,
            sp.GetRequiredService<ProjectAccess>(), null!, TimeProvider.System,
            NullLogger<ProjectBuildImporter>.Instance));
        // The dialogs both pages host, and the Business Central connection behind them.
        // Nothing here opens one.
        _ctx.Services.AddHttpClient();
        _ctx.Services.AddSingleton<BcTokenService>();
        _ctx.Services.AddSingleton<BcPanelCache>();
        _ctx.Services.AddScoped<IBcAdminClient, UnreachableAdminClient>();
        _ctx.Services.AddScoped<IBcAppManagementClient, UnreachableAppManagementClient>();
        _ctx.Services.AddScoped<ProjectConnectionService>();
        _ctx.Services.AddSingleton<IDeliveryTokenSource, UnusedTokenSource>();
        _ctx.Services.AddSingleton(new DeliveryQueue());
        _ctx.Services.AddScoped<DeliveryService>();
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _db.AddStorageServices(_ctx.Services);
        _db.AddGitHubServices(_ctx.Services);
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(ColleagueUserId, "nils@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    // ── The Builds list ──────────────────────────────────────────────────────

    [Fact]
    public async Task List_an_ahead_pipeline_names_the_merged_pull_requests_offers_Build_and_counts_as_ready()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        var build = await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Built, Newer, Newest]);
        await AddMergedAsync(_db, s.RepositoryId, 12, "Post VAT on prepayments", Newer);
        await AddMergedAsync(_db, s.RepositoryId, 13, "Round VAT per line", Newest);
        var current = await SeedPipelineAsync("CRONUS Sales");
        await AddBuildAsync(_db, current.ProjectId, current.PipelineId, current.RepositoryId);
        await AddHeadAsync(_db, current.RepositoryId, "main", Built);

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = Row(cut, "CRONUS Base");
            row.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be($"2 pull requests merged since build #{build}");
            row.ClassList.Should().Contain("pl-ahead", "an ahead row takes the warning keyline");
            row.QuerySelector("button[aria-label='Build CRONUS Base']").Should().NotBeNull();

            var other = Row(cut, "CRONUS Sales");
            other.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be("Up to date with main");
            other.QuerySelector("button[aria-label^='Build']").Should().BeNull("an up-to-date pipeline has nothing to build");
            other.ClassList.Should().NotContain("pl-ahead");

            cut.FindAll(".btn--primary").Should().ContainSingle("the row's Build is an outline button");
        });

        cut.WaitForAssertion(() =>
            cut.FindAll(".pill-tab").Single(t => t.TextContent.Contains("Ready to build")).Click());
        cut.WaitForAssertion(() =>
            cut.FindAll("tbody tr").Select(r => r.QuerySelector(".cell-stack__main")!.TextContent.Trim())
                .Should().Equal("CRONUS Base"));
    }

    [Fact]
    public async Task List_a_pipeline_ahead_without_pull_requests_counts_the_commits_and_says_when()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Built, Newer, Newest],
            pushedAt: DateTime.UtcNow.AddHours(-2));

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var line = Row(cut, "CRONUS Base").QuerySelector(".branch-state")!;
            line.TextContent.Trim().Should().StartWith("2 commits ahead on main, ").And.Contain("hours ago");
            line.QuerySelector("time").Should().NotBeNull("the time goes through Timestamp");
        });
    }

    [Fact]
    public async Task List_after_a_force_push_says_so_instead_of_a_count()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        var build = await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Newer, Newest], forced: true);

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
            Row(cut, "CRONUS Base").QuerySelector(".branch-state")!.TextContent.Trim()
                .Should().Be($"main was force-pushed since build #{build}"));
    }

    [Fact]
    public async Task List_never_built_gone_and_unknown_pipelines_say_so_or_nothing()
    {
        var never = await SeedPipelineAsync("CRONUS Never");
        await AddHeadAsync(_db, never.RepositoryId, "main", Newest);
        var gone = await SeedPipelineAsync("CRONUS Gone");
        await AddBuildAsync(_db, gone.ProjectId, gone.PipelineId, gone.RepositoryId);
        await AddHeadAsync(_db, gone.RepositoryId, "main", Built, deletedAt: DateTime.UtcNow);
        var unknown = await SeedPipelineAsync("CRONUS Quiet");
        await AddBuildAsync(_db, unknown.ProjectId, unknown.PipelineId, unknown.RepositoryId);

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            Row(cut, "CRONUS Never").QuerySelector(".branch-state")!.TextContent.Trim().Should().Be("No successful build yet");
            Row(cut, "CRONUS Gone").QuerySelector(".branch-state")!.TextContent.Trim().Should().Be("Branch main no longer exists");
            Row(cut, "CRONUS Quiet").QuerySelector(".branch-state").Should().BeNull("with no push stored there is nothing to say");
            cut.FindAll(".pill-tab").Should().NotContain(t => t.TextContent.Contains("Ready to build"));
            cut.FindAll("tbody button[aria-label^='Build']").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task List_someone_who_cannot_build_the_solution_sees_the_line_but_no_Build()
    {
        var s = await SeedPipelineAsync("CRONUS Base", ProjectVisibility.ReadOnly);
        await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Built, Newest]);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = Row(cut, "CRONUS Base");
            row.QuerySelector(".branch-state")!.TextContent.Trim().Should().StartWith("1 commit ahead on main");
            row.QuerySelector("button[aria-label^='Build']").Should().BeNull();
        });
    }

    [Fact]
    public async Task List_opens_on_the_ready_tab_from_the_dashboard_link()
    {
        var ahead = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, ahead.ProjectId, ahead.PipelineId, ahead.RepositoryId);
        await AddHeadAsync(_db, ahead.RepositoryId, "main", Newest, commits: [Built, Newest]);
        await SeedPipelineAsync("CRONUS Sales");

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/pipelines/builds?show=ready");

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".pill-tab.is-active").TextContent.Should().Contain("Ready to build");
            cut.FindAll("tbody tr").Should().ContainSingle();
        });
    }

    // ── The Solution filter (#1078) ──────────────────────────────────────────

    [Fact]
    public async Task List_the_solution_select_narrows_the_rows_and_the_tab_counts_to_one_solution()
    {
        var ahead = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, ahead.ProjectId, ahead.PipelineId, ahead.RepositoryId);
        await AddHeadAsync(_db, ahead.RepositoryId, "main", Newest, commits: [Built, Newest]);
        var sales = await SeedPipelineAsync("CRONUS Sales");

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("select[aria-label='Solution'] option").Select(o => o.TextContent.Trim())
                .Should().Equal("All solutions", "CRONUS Base solution", "CRONUS Sales solution");
            cut.FindAll("tbody tr").Should().HaveCount(2);
        });

        cut.Find("select[aria-label='Solution']").Change(sales.ProjectId.ToString());

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Select(r => r.QuerySelector(".cell-stack__main")!.TextContent.Trim())
                .Should().Equal("CRONUS Sales");
            cut.Find(".pill-tab.is-active .pill-tab__count").TextContent.Trim().Should().Be("1");
            _ctx.Services.GetRequiredService<NavigationManager>().Uri
                .Should().EndWith($"/pipelines/builds?solution={sales.ProjectId}", "the choice is in the URL, so it can be shared");
            cut.FindAll(".pill-tab").Should().NotContain(t => t.TextContent.Contains("Ready to build"),
                "the ahead pipeline belongs to the other solution");
        });

        cut.Find("select[aria-label='Solution']").Change("");

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
    }

    [Fact]
    public async Task List_opens_on_the_solution_from_the_link_and_clears_back_to_all()
    {
        var ahead = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, ahead.ProjectId, ahead.PipelineId, ahead.RepositoryId);
        await AddHeadAsync(_db, ahead.RepositoryId, "main", Newest, commits: [Built, Newest]);
        var sales = await SeedPipelineAsync("CRONUS Sales");

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/pipelines/builds?show=ready&solution={sales.ProjectId}");

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("select[aria-label='Solution']").GetAttribute("value").Should().Be(sales.ProjectId.ToString());
            cut.FindAll("tbody tr").Should().BeEmpty("the ahead pipeline belongs to the other solution");
            cut.Find(".pill-tab.is-active").TextContent.Should().Contain("Ready to build", "the active tab stays visible at zero");
            cut.Find(".empty-state__title").TextContent.Trim()
                .Should().Be("Every CRONUS Sales solution pipeline has a build of its latest changes");
        });

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Show all CRONUS Sales solution pipelines").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Select(r => r.QuerySelector(".cell-stack__main")!.TextContent.Trim())
                .Should().Equal("CRONUS Sales");
            cut.Find("select[aria-label='Solution']").GetAttribute("value").Should().Be(sales.ProjectId.ToString());
        });

        cut.Find(".pill-tab.is-active").TextContent.Should().Contain("All");

        // Clear filters, from the same empty state, lets go of the solution too.
        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/pipelines/builds?show=ready&solution={sales.ProjectId}");
        cut = _ctx.Render<PipelinesBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().BeEmpty());
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Clear filters").Click();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Should().HaveCount(2);
            cut.Find("select[aria-label='Solution']").GetAttribute("value").Should().BeNullOrEmpty();
        });
    }

    [Fact]
    public async Task List_a_search_keeps_the_solution_and_Clear_search_keeps_it_too()
    {
        var sales = await SeedPipelineAsync("CRONUS Sales");
        await SeedPipelineAsync("CRONUS Sales Extra");
        await SeedPipelineAsync("CRONUS Base");

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/pipelines/builds?q=Sales&solution={sales.ProjectId}");

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("tbody tr").Select(r => r.QuerySelector(".cell-stack__main")!.TextContent.Trim())
                .Should().Equal("CRONUS Sales");
            cut.Find("form input[type=hidden][name=solution]").GetAttribute("value").Should().Be(sales.ProjectId.ToString(),
                "submitting another search keeps the solution");
            cut.FindAll("a").Single(a => a.TextContent.Trim() == "Clear search").GetAttribute("href")
                .Should().EndWith($"/pipelines/builds?solution={sales.ProjectId}");
        });
    }

    [Fact]
    public async Task List_a_link_to_a_solution_without_build_pipelines_shows_all_of_them()
    {
        await SeedPipelineAsync("CRONUS Base");

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/pipelines/builds?solution=999999");

        var cut = _ctx.Render<PipelinesBrowser>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
    }

    // ── The pipeline page ────────────────────────────────────────────────────

    [Fact]
    public async Task Page_an_ahead_pipeline_lists_the_merged_pull_requests_linking_to_GitHub()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        var build = await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Built, Newer, Newest]);
        await AddMergedAsync(_db, s.RepositoryId, 12, "Post VAT on prepayments", Newer);
        await AddMergedAsync(_db, s.RepositoryId, 13, "Round VAT per line", Newest);

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(x => x.PipelineId, s.PipelineId));

        cut.WaitForAssertion(() =>
        {
            var card = BranchCard(cut);
            card.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be($"2 pull requests merged since build #{build}");
            card.TextContent.Should().Contain($"in build #{build}").And.Contain("pushed by erik");
            card.QuerySelector($"a[href='https://github.com/cronus-dk/cronus-base/commit/{Newest}']")!.TextContent.Trim()
                .Should().Be("3333333", "the head links to its commit on GitHub");
            card.QuerySelector($"a[href='https://github.com/cronus-dk/cronus-base/commit/{Built}']").Should().NotBeNull();
            card.QuerySelectorAll(".pb-chg__list a").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"))).Should().Equal(
                ("#13 Round VAT per line", "https://github.com/cronus-dk/cronus-base/pull/13"),
                ("#12 Post VAT on prepayments", "https://github.com/cronus-dk/cronus-base/pull/12"));
            card.TextContent.Should().Contain("2 pull requests merged since build");
            cut.FindAll(".btn--primary").Select(b => b.TextContent.Trim()).Should().Equal("Build");
        });
    }

    [Fact]
    public async Task Page_an_up_to_date_pipeline_says_so_with_nothing_listed()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Built);

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(x => x.PipelineId, s.PipelineId));

        cut.WaitForAssertion(() =>
        {
            var card = BranchCard(cut);
            card.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be("Up to date with main");
            card.QuerySelectorAll(".pb-chg__list").Should().BeEmpty();
        });
    }

    [Fact]
    public async Task Page_a_pipeline_whose_builds_all_failed_is_never_built()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId, status: ProjectBuildStatus.Failed);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest);

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(x => x.PipelineId, s.PipelineId));

        cut.WaitForAssertion(() =>
        {
            var card = BranchCard(cut);
            card.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be("No successful build yet");
            card.TextContent.Should().Contain("Not built yet");
        });
    }

    [Fact]
    public async Task Page_after_a_force_push_lists_the_latest_commits_and_links_the_full_comparison()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        var build = await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);
        await AddHeadAsync(_db, s.RepositoryId, "main", Newest, commits: [Newer, Newest], forced: true);

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(x => x.PipelineId, s.PipelineId));

        cut.WaitForAssertion(() =>
        {
            var card = BranchCard(cut);
            card.QuerySelector(".branch-state")!.TextContent.Trim().Should().Be($"main was force-pushed since build #{build}");
            card.QuerySelectorAll(".pb-chg__list li").Select(li => li.TextContent.Trim()).Should().Equal(
                "Fix VAT rounding on 3333333 3333333", "Fix VAT rounding on 2222222 2222222");
            card.TextContent.Should().Contain("Latest commits").And.NotContain("Commits since");
            card.QuerySelector($"a[href='https://github.com/cronus-dk/cronus-base/compare/{Built}...{Newest}']")!
                .TextContent.Trim().Should().Be("See every change on GitHub");
        });
    }

    [Fact]
    public async Task Page_with_no_push_stored_has_no_branch_card()
    {
        var s = await SeedPipelineAsync("CRONUS Base");
        await AddBuildAsync(_db, s.ProjectId, s.PipelineId, s.RepositoryId);

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(x => x.PipelineId, s.PipelineId));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".card__title").Select(t => t.TextContent.Trim()).Should().Contain("Latest build").And.NotContain("Branch");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record Seeded(int ProjectId, int RepositoryId, int PipelineId);

    /// <summary>A solution of its own with one GitHub repository and one build pipeline watching main.</summary>
    private async Task<Seeded> SeedPipelineAsync(string name, ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = name + " solution",
            CreatedByUserId = OwnerUserId,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = name, Branch = "main",
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        await ctx.SaveChangesAsync();
        var repository = await AddRepositoryAsync(_db, project.Id);
        return new Seeded(project.Id, repository, pipeline.Id);
    }

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<PipelinesBrowser> cut, string pipelineName) =>
        cut.FindAll("tbody tr").Single(r => r.QuerySelector(".cell-stack__main")?.TextContent.Trim() == pipelineName);

    private static AngleSharp.Dom.IElement BranchCard(IRenderedComponent<PipelineBuilds> cut) =>
        cut.FindAll("section.card").Single(c => c.QuerySelector(".card__title")?.TextContent.Trim() == "Branch");

    private sealed class UnusedTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        PasswordHash = "x",
        DisplayName = email,
        Role = UserRole.User,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };
}
