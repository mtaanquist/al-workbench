using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Bunit.TestDoubles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Releases list (#935): each release pipeline says what it is shipping now, what
/// it shipped last and what it ships next, most urgent first, with a band per release
/// in flight and tabs that narrow the list. Built to
/// <c>.design/handoff/ReleasePipelinesBody.dc.html</c>.
///
/// <para>Named user: a BC consultant looking after several customers' environments,
/// checking in on what is going out and what failed.</para>
/// </summary>
public sealed class ReleasePipelinesBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public ReleasePipelinesBrowserTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        // The editor dialog the page hosts lists Business Central environments; its
        // chain has to resolve for the page to render, though nothing here calls out.
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
        // The Reschedule dialog the list hosts deploys through the delivery service.
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IDeliveryTokenSource>(
            sp => sp.GetRequiredService<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>());
        _ctx.Services.AddScoped<DeliveryService>();
        _ctx.Services.AddSingleton(new DeliveryQueue());
        TestDb.AddToolServices(_ctx.Services);
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
        // The page polls while a release is in flight; disposing it first stops the
        // timer, so no tick starts a query after the database has gone.
        _ctx.Dispose();
        _db.WaitForQueriesToSettle();
        _db.Dispose();
    }

    [Fact]
    public void An_org_with_no_solutions_is_sent_to_create_one_first()
    {
        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card .empty-state__title").TextContent.Trim().Should().Be("No solutions yet");
            cut.Find(".empty-state__action a").GetAttribute("href").Should().Be("/solutions/new");
            var head = cut.Find(".page-head__actions button");
            head.HasAttribute("disabled").Should().BeTrue("there is nothing to attach a pipeline to yet");
            head.GetAttribute("title").Should().Contain("Create a solution first");
            cut.FindAll(".filter-bar").Should().BeEmpty("there is nothing to filter");
        });
    }

    [Fact]
    public async Task A_solution_without_pipelines_offers_the_first_one()
    {
        await SeedSolutionAsync("CRONUS International");

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card .empty-state__title").TextContent.Trim().Should().Be("No deployment pipelines yet");
            var action = cut.Find(".empty-state__action a.btn");
            action.TextContent.Should().Contain("New deployment pipeline");
            action.GetAttribute("href").Should().Be("/pipelines/deployments/new?returnUrl=%2Fpipelines%2Fdeployments");
            cut.FindAll(".page-head__actions .btn").Should().BeEmpty("the empty state carries the one next step");
        });
    }

    [Fact]
    public async Task A_show_query_opens_the_list_on_that_tab()
    {
        // The Pipelines dashboard's tiles (#955) link here pre-filtered.
        var s = await SeedFleetAsync();
        _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/pipelines/deployments?show=attention");

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".pill-tab.is-active").TextContent.Should().Contain("Needs attention");
            cut.FindAll("table.rp-list__wide tbody tr").Select(r => r.GetAttribute("data-pipeline"))
                .Should().Equal(s.Blocked.ToString(), s.Failed.ToString());
        });
    }

    [Fact]
    public async Task A_pipeline_whose_build_pipeline_was_deleted_says_so_instead_of_linking_to_it()
    {
        var s = await SeedFleetAsync();
        int buildPipelineId;
        await using (var db = _db.NewContext())
        {
            buildPipelineId = await db.OePipelines.Where(p => p.ProjectId == s.ProjectId).Select(p => p.Id).SingleAsync();
            await db.OePipelines.Where(p => p.Id == buildPipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
        }

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find($"table.rp-list__wide tbody tr[data-pipeline='{s.Quiet}']");
            row.TextContent.Should().Contain("Build pipeline deleted");
            row.QuerySelectorAll($"a[href='/pipelines/{buildPipelineId}']").Should().BeEmpty("the page behind it is gone");
        });
    }

    [Fact]
    public async Task A_scheduled_deployment_can_be_rescheduled_from_the_row_menu()
    {
        var s = await SeedFleetAsync();
        await using (var db = _db.NewContext())
        {
            var buildId = await db.OeProjectBuilds.Select(b => b.Id).FirstAsync();
            var waiting = Delivery(s.ProjectId, s.Quiet, buildId, ProjectDeliveryStatus.Scheduled, DateTime.UtcNow.AddDays(1), null);
            waiting.EnvironmentName = "Production";
            waiting.StartedAt = null;
            db.OeProjectDeliveries.Add(waiting);
            await db.SaveChangesAsync();
        }

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find($"table.rp-list__wide tbody tr[data-pipeline='{s.Quiet}']");
            row.QuerySelectorAll(".menu__item").Select(i => i.TextContent.Trim()).Should().Contain("Reschedule next deployment");
            cut.Find($"table.rp-list__wide tbody tr[data-pipeline='{s.Failed}']").QuerySelectorAll(".menu__item")
                .Select(i => i.TextContent.Trim()).Should().NotContain("Reschedule next deployment", "nothing is waiting there");
        });
        cut.Find($"table.rp-list__wide tbody tr[data-pipeline='{s.Quiet}'] .menu__item").Click();
        cut.WaitForAssertion(() => cut.Find("#rs-title").TextContent.Should().Be("Reschedule deployment"));
    }

    [Fact]
    public async Task Each_row_says_what_is_shipping_what_failed_and_what_is_blocked_most_urgent_first()
    {
        var s = await SeedFleetAsync();

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.rp-list__wide tbody tr");
            rows.Select(r => r.GetAttribute("data-pipeline")).Should().Equal(
                s.Live.ToString(), s.Blocked.ToString(), s.Failed.ToString(), s.Quiet.ToString());

            // Shipping now: the keyline, the spinning glyph and the app count.
            var live = rows[0];
            live.ClassList.Should().Contain("is-running");
            live.QuerySelector(".data-table__state--spin").Should().NotBeNull();
            live.Children[3].TextContent.Should().Contain("Installing app 2 of 3");

            // The environment is gone: nothing can run, and the next-release cell says why.
            var blocked = rows[1];
            blocked.ClassList.Should().Contain("is-failed");
            blocked.Children[2].TextContent.Should().Contain("Missing");
            blocked.Children[4].TextContent.Should().Contain("Blocked").And.Contain("Environment not found");
            blocked.Children[4].QuerySelector("a")!.GetAttribute("href").Should().Be($"/solutions/{s.ProjectId}/bc",
                "a dead target says where to fix it");

            // The last release failed, and on which app.
            var failed = rows[2];
            failed.ClassList.Should().Contain("is-failed");
            failed.Children[3].TextContent.Should().Contain("Failed on CRONUS Sales");
            failed.Children[3].QuerySelector(".cell-stack__sub time")!.GetAttribute("title").Should().EndWith(" UTC",
                "every relative time carries the exact time on hover");

            // Never released: says so, rather than a blank cell.
            rows[3].Children[3].TextContent.Should().Contain("Nothing deployed yet");
            rows[3].Children[4].TextContent.Should().Contain("None scheduled");

            // The environment and the solution are links (#932, the list half).
            live.Children[2].QuerySelector("a")!.GetAttribute("href").Should().Be($"/environments/{s.UatId}");
            live.Children[1].QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
                .Should().Equal($"/pipelines/deployments/{s.Live}", $"/solutions/{s.ProjectId}");
        });
    }

    [Fact]
    public async Task A_release_in_flight_gets_a_band_with_its_progress_and_a_way_in()
    {
        var s = await SeedFleetAsync();

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var band = cut.Find(".rp-live");
            band.GetAttribute("role").Should().Be("status");
            band.QuerySelector(".rp-live__name")!.TextContent.Should().Be("CRONUS to UAT");
            band.QuerySelector(".rp-live__text")!.TextContent.Should()
                .StartWith("Installing app 2 of 3 into UAT (Sandbox) for CRONUS International.")
                .And.Contain("the last deployment here took 6 minutes");
            band.QuerySelector(".rp-live__step")!.TextContent.Should().Be("1 of 3 apps done");
            band.QuerySelector("a.rp-live__open")!.GetAttribute("href").Should().Be($"/pipelines/deployments/{s.Live}");
            cut.FindAll(".rp-live").Should().HaveCount(1, "only the release in flight gets a band");
        });
    }

    [Fact]
    public async Task The_tabs_narrow_the_list_and_count_what_they_hold()
    {
        var s = await SeedFleetAsync();

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            var tabs = cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).ToList();
            tabs.Should().Equal("All4", "Shipping now1", "Needs attention2", "Scheduled0");
            cut.FindAll(".pill-tab").Single(t => t.TextContent.StartsWith("Needs attention")).Click();
        });

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("table.rp-list__wide tbody tr").Select(r => r.GetAttribute("data-pipeline"))
                .Should().Equal(s.Blocked.ToString(), s.Failed.ToString());
            cut.Find(".pill-tab.is-active").TextContent.Should().StartWith("Needs attention");
        });
    }

    [Fact]
    public async Task A_release_waiting_for_approval_is_counted_at_the_top_and_needs_attention()
    {
        var s = await SeedFleetAsync();
        await using (var db = _db.NewContext())
        {
            var buildId = await db.OeProjectBuilds.Where(b => b.ProjectId == s.ProjectId).Select(b => b.Id).FirstAsync();
            var waiting = Delivery(s.ProjectId, s.Quiet, buildId, ProjectDeliveryStatus.Proposed, DateTime.UtcNow.AddMinutes(-20), null,
                ProjectDeliveryResultStatus.Pending);
            db.OeProjectDeliveries.Add(waiting);
            await db.SaveChangesAsync();
        }

        var cut = _ctx.Render<ReleasePipelinesBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".alert--warn").TextContent.Should().Contain("1 deployment waiting for approval:").And.Contain("CRONUS to Production");
            cut.Find(".alert--warn a").GetAttribute("href").Should().Be($"/pipelines/deployments/{s.Quiet}");
            var row = cut.Find($"table.rp-list__wide tbody tr[data-pipeline='{s.Quiet}']");
            row.ClassList.Should().Contain("is-draft");
            row.QuerySelector(".data-table__state")!.GetAttribute("aria-label").Should().Be("Deployment waiting for approval");
            row.Children[4].TextContent.Should().Contain("Waiting for approval");
            cut.FindAll(".pill-tab").Select(t => t.TextContent.Trim()).Should().Contain("Needs attention3");
        });
    }

    // ── Seeding ──────────────────────────────────────────────────────────────

    private sealed record Fleet(int ProjectId, int UatId, int Live, int Failed, int Blocked, int Quiet);

    private async Task<int> SeedSolutionAsync(string name)
    {
        await using var db = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.OeProjects.Add(project);
        await db.SaveChangesAsync();
        return project.Id;
    }

    /// <summary>
    /// Four pipelines into one solution's environments: one installing right now (after
    /// a six-minute release that went fine), one whose last release failed, one aimed at
    /// an environment Business Central no longer has, and one never used.
    /// </summary>
    private async Task<Fleet> SeedFleetAsync()
    {
        var projectId = await SeedSolutionAsync("CRONUS International");
        await using var db = _db.NewContext();
        var buildPipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "CRONUS main",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.OePipelines.Add(buildPipeline);
        var uat = Environment(projectId, "UAT", "Sandbox");
        var test = Environment(projectId, "Test", "Sandbox");
        var gone = Environment(projectId, "CR", "Sandbox");
        gone.MissingSince = DateTime.UtcNow.AddDays(-2);
        var production = Environment(projectId, "Production", "Production");
        db.OeProjectEnvironments.AddRange(uat, test, gone, production);
        await db.SaveChangesAsync();

        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = buildPipeline.Id,
            Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow.AddDays(-1),
        };
        db.OeProjectBuilds.Add(build);
        var live = Pipeline(projectId, "CRONUS to UAT", buildPipeline.Id, uat.Id);
        var failed = Pipeline(projectId, "CRONUS to Test", buildPipeline.Id, test.Id);
        var blocked = Pipeline(projectId, "CRONUS to CR", buildPipeline.Id, gone.Id);
        var quiet = Pipeline(projectId, "CRONUS to Production", buildPipeline.Id, production.Id);
        db.OeReleasePipelines.AddRange(live, failed, blocked, quiet);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        db.OeProjectDeliveries.AddRange(
            Delivery(projectId, live.Id, build.Id, ProjectDeliveryStatus.Deployed, now.AddDays(-1), now.AddDays(-1).AddMinutes(6),
                ProjectDeliveryResultStatus.Completed),
            Delivery(projectId, live.Id, build.Id, ProjectDeliveryStatus.Installing, now.AddMinutes(-3), null,
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Installing, ProjectDeliveryResultStatus.Pending),
            Delivery(projectId, failed.Id, build.Id, ProjectDeliveryStatus.Failed, now.AddHours(-5), now.AddHours(-5).AddMinutes(2),
                ProjectDeliveryResultStatus.Completed, ProjectDeliveryResultStatus.Failed),
            Delivery(projectId, blocked.Id, build.Id, ProjectDeliveryStatus.Deployed, now.AddDays(-21), now.AddDays(-21).AddMinutes(4),
                ProjectDeliveryResultStatus.Completed));
        await db.SaveChangesAsync();

        return new Fleet(projectId, uat.Id, live.Id, failed.Id, blocked.Id, quiet.Id);
    }

    private static OeProjectEnvironment Environment(int projectId, string name, string type) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name, Type = type,
        Status = "Active", FetchedAt = DateTime.UtcNow,
    };

    private static OeReleasePipeline Pipeline(int projectId, string name, int buildPipelineId, int environmentId) => new()
    {
        OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = name,
        BuildPipelineId = buildPipelineId, ProjectEnvironmentId = environmentId,
        DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static OeProjectDelivery Delivery(
        int projectId, int releasePipelineId, int buildId, string status, DateTime startedAt, DateTime? finishedAt,
        params string[] apps)
    {
        var delivery = new OeProjectDelivery
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, ReleasePipelineId = releasePipelineId,
            ProjectBuildId = buildId, EnvironmentName = "UAT", Status = status,
            ScheduledFor = startedAt, StartedAt = startedAt, FinishedAt = finishedAt,
            CreatedAt = startedAt, UpdatedAt = finishedAt ?? startedAt,
        };
        string[] names = ["CRONUS Base", "CRONUS Sales", "CRONUS Reports"];
        for (var i = 0; i < apps.Length; i++)
        {
            delivery.Results.Add(new OeProjectDeliveryResult
            {
                OrganizationId = TestDb.DefaultOrgId, Ordering = i, AppName = names[i], AppVersion = "1.0.0.0",
                Status = apps[i], CreatedAt = startedAt, UpdatedAt = startedAt,
            });
        }
        return delivery;
    }
}
