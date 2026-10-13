using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// Pins the Solutions list: its columns (where the customer runs, the version they are
/// on, when something last reached production, who may see it), the row as the link
/// that opens the customer info beside the list, and the rail that is kept while
/// nothing is chosen. See .design/solution-customer-info.md.
/// </summary>
public sealed class ProjectsBrowserTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private readonly Microsoft.AspNetCore.Http.HttpContextAccessor _http = new();

    /// <summary>The page is a plain GET page and reads its address from the request.</summary>
    private void RequestWith(string query) => _http.HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
    {
        Request = { QueryString = new Microsoft.AspNetCore.Http.QueryString(query) },
    };

    public ProjectsBrowserTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<CustomerModuleService>();
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(_http);
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void An_org_with_no_projects_gets_the_first_run_empty_state_and_its_create_action()
    {
        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find(".card .empty-state__title").TextContent.Trim().Should().Be("No solutions yet");
            cut.Find(".empty-state__action a").GetAttribute("href").Should().Be("/solutions/new");
        });
    }

    [Fact]
    public async Task Each_row_is_a_link_that_opens_its_customer_info_and_the_name_still_opens_the_solution()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await IdOfAsync("CRONUS Denmark");
        RequestWith("?q=cronus&module=");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("tbody tr");
            var select = row.QuerySelector("a.sol-list__select")!;
            select.GetAttribute("href").Should().Be($"/solutions?q=cronus&selected={id}", "choosing keeps the search the person had");
            select.GetAttribute("aria-label").Should().Be("Show customer info for CRONUS Denmark");
            select.HasAttribute("aria-current").Should().BeFalse("nothing is chosen yet");
            row.QuerySelector("a.sol-list__name")!.GetAttribute("href").Should().Be($"/solutions/{id}");
            row.QuerySelectorAll("a a").Should().BeEmpty("a link inside a link is invalid and browsers split it");
            row.QuerySelectorAll(".btn").Should().BeEmpty("the row is the way in; it carries no buttons");
            cut.FindAll("table .status-pill, .data-table__state--icon").Should().BeEmpty("the list no longer reports build status");
        });
    }

    [Fact]
    public async Task The_short_name_sits_beside_the_name_when_there_is_one()
    {
        await SeedProjectAsync("CRONUS Denmark", status: null, bcVersion: null);
        await SeedProjectAsync("CRONUS Norway", status: null, bcVersion: null);
        await using (var db = _db.NewContext())
        {
            (await db.OeProjects.SingleAsync(p => p.Name == "CRONUS Denmark")).ShortName = "CRD";
            await db.SaveChangesAsync();
        }

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            rows[0].QuerySelector(".sol-list__short")!.TextContent.Should().Be("CRD");
            rows[1].QuerySelector(".sol-list__short").Should().BeNull();
        });
    }

    [Fact]
    public async Task Visibility_reads_in_the_words_the_access_tab_uses()
    {
        await SeedProjectAsync("CRONUS Denmark", status: null, bcVersion: null);
        await SeedProjectAsync("CRONUS Norway", status: null, bcVersion: null);
        await using (var db = _db.NewContext())
        {
            (await db.OeProjects.SingleAsync(p => p.Name == "CRONUS Norway")).Visibility = ProjectVisibility.ReadOnly;
            await db.SaveChangesAsync();
        }

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            rows[0].Children[4].TextContent.Should().Be("Public");
            rows[1].Children[4].TextContent.Should().Be("Read-only");
        });
    }

    [Fact]
    public async Task Last_shipped_links_to_the_release_pipeline_says_never_when_nothing_has_and_marks_a_handed_off_one()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await SeedProjectAsync("CRONUS Sweden", status: null, bcVersion: null);
        var deployedPipeline = await ShipAsync("CRONUS Denmark", ProjectDeliveryStatus.Deployed, new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        await ShipAsync("CRONUS Norway", ProjectDeliveryStatus.HandedOff, new DateTime(2026, 9, 12, 12, 0, 0, DateTimeKind.Utc));

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tbody tr");
            var deployed = rows[0].Children[3].QuerySelector("a.sol-list__shipped")!;
            deployed.GetAttribute("href").Should().Be($"/pipelines/deployments/{deployedPipeline}");
            deployed.TextContent.Trim().Should().Be("10 Sep 2026");
            deployed.ClassList.Should().NotContain("sol-list__shipped--handed-off");

            var handedOff = rows[1].Children[3].QuerySelector("a.sol-list__shipped")!;
            handedOff.ClassList.Should().Contain("sol-list__shipped--handed-off");
            handedOff.GetAttribute("title").Should().Contain("later update window");
            handedOff.TextContent.Should().Contain("installs later", "the tone alone would say nothing on a call or to a screen reader");

            rows[2].Children[3].TextContent.Trim().Should().Be("Never");
        });
    }

    /// <summary>A release pipeline to a Production environment and one finished delivery through it.</summary>
    private async Task<int> ShipAsync(string name, string status, DateTime finishedAt)
    {
        await using var db = _db.NewContext();
        var project = await db.OeProjects.SingleAsync(p => p.Name == name);
        var build = await db.OeProjectBuilds.FirstAsync(b => b.ProjectId == project.Id);
        var environment = new OeProjectEnvironment
        {
            OrganizationId = project.OrganizationId, ProjectId = project.Id, Name = "Production", Type = "Production", FetchedAt = DateTime.UtcNow,
        };
        db.OeProjectEnvironments.Add(environment);
        await db.SaveChangesAsync();
        var pipeline = new OeReleasePipeline
        {
            OrganizationId = project.OrganizationId, ProjectId = project.Id, Name = "To production",
            ProjectEnvironmentId = environment.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.OeReleasePipelines.Add(pipeline);
        await db.SaveChangesAsync();
        db.OeProjectDeliveries.Add(new OeProjectDelivery
        {
            OrganizationId = project.OrganizationId, ProjectId = project.Id, ReleasePipelineId = pipeline.Id, ProjectBuildId = build.Id,
            EnvironmentName = "Production", Status = status, ScheduledFor = finishedAt, FinishedAt = finishedAt,
            CreatedAt = finishedAt, UpdatedAt = finishedAt,
        });
        await db.SaveChangesAsync();
        return pipeline.Id;
    }

    private async Task<int> IdOfAsync(string name)
    {
        await using var db = _db.NewContext();
        return (await db.OeProjects.SingleAsync(p => p.Name == name)).Id;
    }

    /// <summary>
    /// The one row this page renders for something the viewer may not open: a
    /// private project they are not on the team for. It has to say the project
    /// exists (so it doesn't seem to vanish) and nothing else about it, and it must
    /// not look clickable. See <c>.design/teams-and-visibility.md</c>.
    /// </summary>
    [Fact]
    public async Task A_private_project_the_viewer_is_not_on_renders_as_a_locked_unclickable_row()
    {
        await SeedProjectAsync("CRONUS Denmark", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await MakePrivateAsync("CRONUS Denmark");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("tbody tr.projects__locked");
            row.TextContent.Should().Contain("CRONUS Denmark");
            row.TextContent.Should().Contain("Private — visible to its team");
            row.QuerySelectorAll("a").Should().BeEmpty("there is nothing behind the name for this viewer");
            cut.Markup.Should().NotContain("26.0", "a build version reports activity on the customer");
            row.Children.Should().HaveCount(2, "the name, then one cell saying why there is nothing else");
        });
    }

    /// <summary>
    /// Marks a seeded project private with a team nobody is on — the state a viewer
    /// with no grant sees. Written straight to the database because the UI that sets
    /// visibility lands in a later slice.
    /// </summary>
    private async Task MakePrivateAsync(string name)
    {
        await using var db = _db.NewContext();
        var project = await db.OeProjects.FirstAsync(p => p.Name == name);
        var team = new ALDevToolbox.Domain.Entities.Team
        {
            OrganizationId = project.OrganizationId,
            Name = "Nordics",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Teams.Add(team);
        await db.SaveChangesAsync();

        project.Visibility = ProjectVisibility.Private;
        db.OeProjectTeams.Add(new OeProjectTeam
        {
            OrganizationId = project.OrganizationId,
            ProjectId = project.Id,
            TeamId = team.Id,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedProjectAsync(string name, string? status, string? bcVersion)
    {
        await using var db = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = _db.OrgContext.CurrentOrganizationId!.Value,
            Name = name,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.OeProjects.Add(project);
        await db.SaveChangesAsync();

        if (status is not null)
        {
            // A pipeline's build: only those carry the row's status. A build
            // with no pipeline (a pull-request check) is deliberately not one.
            var pipeline = new OePipeline
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                Name = "Default",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            db.OePipelines.Add(pipeline);
            await db.SaveChangesAsync();
            db.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = project.OrganizationId,
                ProjectId = project.Id,
                PipelineId = pipeline.Id,
                Status = status,
                BcVersion = bcVersion,
                StartedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
    }

    // ── The customer summary beside the list (.design/solution-customer-info.md) ──

    private async Task<int> DescribeAsync(string name)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Name == name);
        project.HostingType = ProjectHostingType.CustomerHardware;
        project.BcVersion = "NAV 2018 CU12";
        project.AccessDescription = "VPN, then remote desktop to CRONUS-BC01.";
        ctx.OeProjectContacts.Add(new OeProjectContact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Type = ProjectContactType.Customer,
            Name = "Annette Hill", Phone = "+45 12 34 56 78", CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return project.Id;
    }

    [Fact]
    public async Task The_list_says_where_each_customer_runs_and_keeps_the_rest_for_the_summary()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        await DescribeAsync("CRONUS Norway");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("thead th").Select(h => h.TextContent.Trim()).Should().Equal(
                "Solution", "Hosted by", "BC version", "Last shipped", "Visibility");
            var cells = cut.Find("tbody tr").Children;
            cells[1].TextContent.Should().Be("Customer's hardware");
            cells[2].TextContent.Should().Be("NAV 2018 CU12");
            cut.Markup.Should().NotContain("Annette Hill");
        });
    }

    [Fact]
    public async Task With_nothing_chosen_the_rail_is_kept_and_asks_for_a_solution()
    {
        await SeedProjectAsync("CRONUS Norway", status: null, bcVersion: null);

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var rail = cut.Find(".detail-body__aside");
            rail.GetAttribute("aria-label").Should().Be("Customer info");
            rail.QuerySelector(".empty-state__title")!.TextContent.Should().Be("Choose a solution to see its customer info");
            cut.FindAll("tbody tr.is-selected").Should().BeEmpty();
            cut.FindAll("input[type=hidden][name=selected]").Should().BeEmpty("there is no choice to carry");
        });
    }

    [Fact]
    public async Task A_search_keeps_the_chosen_solution()
    {
        await SeedProjectAsync("CRONUS Norway", status: null, bcVersion: null);
        var id = await IdOfAsync("CRONUS Norway");
        RequestWith($"?selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            var hidden = cut.Find("form input[type=hidden][name=selected]");
            hidden.GetAttribute("value").Should().Be(id.ToString());
            hidden.Closest("form")!.QuerySelector("input[name=q]").Should().NotBeNull("it rides along with the search");
        });
    }

    /// <summary>
    /// Connects a described customer and gives it a Production environment Business
    /// Central has reported on (#907). The secret is not a real ciphertext: nothing
    /// on the list decrypts.
    /// </summary>
    private async Task ConnectAsync(int projectId)
    {
        await using var ctx = _db.NewContext();
        var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
        project.HostingType = ProjectHostingType.MicrosoftCloud;
        project.BcTenantId = Guid.NewGuid();
        project.BcClientId = "11111111-2222-3333-4444-555555555555";
        project.BcClientSecretEncrypted = "not-a-real-ciphertext";
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, Name = "Production", Type = "Production",
            Version = "26.1.30000.0", WebClientLoginUrl = "https://businesscentral.dynamics.com/tenant/Production",
            FetchedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task A_connected_customers_version_and_address_are_the_ones_business_central_reports()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await DescribeAsync("CRONUS Norway");
        await ConnectAsync(id);
        RequestWith($"?selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").Children[2].TextContent.Should().Be("26.1.30000.0");
            var rail = cut.Find(".detail-body__aside");
            rail.TextContent.Should().Contain("26.1.30000.0").And.NotContain("NAV 2018 CU12");
            rail.QuerySelector("a[href='https://businesscentral.dynamics.com/tenant/Production']").Should().NotBeNull();
        });
    }

    [Fact]
    public async Task Choosing_summary_opens_that_customers_essentials_beside_the_list_as_an_address()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await DescribeAsync("CRONUS Norway");
        RequestWith($"?q=cronus&selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr").ClassList.Should().Contain("is-selected");
            var rail = cut.Find(".detail-body__aside");
            rail.TextContent.Should().Contain("CRONUS Norway").And.Contain("VPN, then remote desktop")
                .And.Contain("Annette Hill").And.Contain("The customer, on their own hardware");
            rail.QuerySelector("a[href^='tel:']")!.GetAttribute("href").Should().Be("tel:+4512345678");
            rail.QuerySelector("a[aria-label='Close customer info']")!.GetAttribute("href").Should().Be("/solutions?q=cronus",
                "closing keeps the search the person had");
            var select = cut.Find("a[aria-label='Show customer info for CRONUS Norway']");
            select.GetAttribute("href").Should().Be($"/solutions?q=cronus&selected={id}");
            select.GetAttribute("aria-current").Should().Be("true");
            rail.QuerySelectorAll("input, select, textarea").Should().BeEmpty("the summary never edits; the Customer tab does");
        });
    }

    [Fact]
    public async Task A_private_solution_the_viewer_is_not_on_cannot_be_summarised_by_putting_its_id_in_the_address()
    {
        await SeedProjectAsync("CRONUS Norway", ProjectBuildStatus.Ready, bcVersion: "26.0");
        var id = await DescribeAsync("CRONUS Norway");
        await MakePrivateAsync("CRONUS Norway");
        RequestWith($"?selected={id}");

        var cut = _ctx.Render<ProjectsBrowser>();

        cut.WaitForAssertion(() =>
        {
            cut.Find("tbody tr.projects__locked").Should().NotBeNull();
            cut.Find(".detail-body__aside .empty-state__title").TextContent.Should().Be(
                "Choose a solution to see its customer info", "the id is ignored, not answered");
            cut.Markup.Should().NotContain("Annette Hill").And.NotContain("NAV 2018");
        });
    }

    /// <summary>
    /// A search narrows the one list the page already read for its total (#1263): it
    /// used to read every solution twice, once for the count and once for the rows.
    /// The two renders must cost the same number of queries.
    /// </summary>
    [Fact]
    public async Task A_search_filters_the_list_without_reading_it_twice()
    {
        await SeedProjectAsync("CRONUS Denmark", status: null, bcVersion: null);
        await SeedProjectAsync("CRONUS Norway", status: null, bcVersion: null);
        _db.WaitForQueriesToSettle();

        var before = _db.CommandTracker.Total;
        var all = _ctx.Render<ProjectsBrowser>();
        all.WaitForAssertion(() => all.FindAll("tbody tr").Should().HaveCount(2));
        _db.WaitForQueriesToSettle();
        var unfiltered = _db.CommandTracker.Total - before;

        RequestWith("?q=norway");
        before = _db.CommandTracker.Total;
        var cut = _ctx.Render<ProjectsBrowser>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle()
            .Which.TextContent.Should().Contain("CRONUS Norway"));
        _db.WaitForQueriesToSettle();

        (_db.CommandTracker.Total - before).Should().Be(unfiltered, "the search filters in memory");
    }
}
