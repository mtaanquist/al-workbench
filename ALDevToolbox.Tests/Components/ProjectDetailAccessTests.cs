using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.Infrastructure;
using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The Access tab on the project detail page — the switch that turns project
/// visibility on (<c>.design/teams-and-visibility.md</c>, slice 3). Its named
/// user is a BC consultant who owns the CRONUS engagement project and wants it
/// restricted to the NDA team.
///
/// <para>Three things are pinned: the tab exists only for someone who can manage
/// the project, a save round-trips through <c>SetAccessAsync</c>, and choosing
/// Public clears the team picks rather than leaving a disabled control holding
/// values that the service would then refuse.</para>
/// </summary>
public sealed class ProjectDetailAccessTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();
    private readonly BunitJSModuleInterop _tabs;

    private const int OwnerUserId = 9600;
    private const int OutsiderUserId = 9601;

    public ProjectDetailAccessTests()
    {
        var auth = _ctx.AddAuthorization();
        // A tab click rewrites the address in place rather than navigating.
        _tabs = _ctx.JSInterop.SetupModule("./Components/Pages/Projects/ProjectDetail.razor.js");
        _tabs.SetupVoid("showTab", _ => true);
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<ProjectFollowService>();
        // The follow confirmation reads the person's own Solutions choice.
        _ctx.Services.AddScoped<ALDevToolbox.Services.Notifications.NotificationPreferenceService>();
        _ctx.Services.AddSingleton(new ALDevToolbox.Endpoints.PublicOrigin("https://workbench.example"));
        _ctx.Services.AddSingleton<ALDevToolbox.Services.IEmailService, ALDevToolbox.Tests.Infrastructure.CapturingEmailService>();
        _ctx.Services.AddScoped<ProjectCustomerInfoService>();
        _ctx.Services.AddScoped<CustomerModuleService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<TeamService>();
        // ProjectDetail loads the Business Central tab's connection for anyone who
        // can manage the project, so its chain has to resolve even though this test
        // never touches BC. The clients are never called.
        _ctx.Services.AddHttpClient();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcTokenService>();
        _ctx.Services.AddSingleton<ALDevToolbox.Services.ObjectExplorer.Bc.BcPanelCache>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAdminClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAdminClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.IBcAppManagementClient,
            ALDevToolbox.Services.ObjectExplorer.Bc.BcAppManagementClient>();
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.ProjectConnectionService>();
        // The environment panel's update history (issue #657 Stage 4b).
        _ctx.Services.AddSingleton(TimeProvider.System);
        _ctx.Services.AddScoped<ALDevToolbox.Services.ObjectExplorer.Bc.UpgradeActionService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddScoped<RepositoryProviderPolicyService>();
        _db.AddStorageServices(_ctx.Services);
        // The Repositories tab offers a GitHub picker when one can be offered
        // (issue #624), so the page's chain has to resolve. With no handler every
        // call to GitHub throws, which is the "not set up" state these tests want.
        _db.AddGitHubServices(_ctx.Services);
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Http.IHttpContextAccessor>(
            new Microsoft.AspNetCore.Http.HttpContextAccessor());
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@example.com"), NewUser(OutsiderUserId, "nils@example.com"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
        // These are about the settings tabs; an existing solution opens on Customer, and
        // the page takes its tab from the address.
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo(Microsoft.AspNetCore.Components.NavigationManagerExtensions.GetUriWithQueryParameter(nav, "tab", "general"));
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
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

    private async Task<(int ProjectId, int TeamId)> SeedAsync(bool withTeam = true)
    {
        await using var ctx = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            CreatedByUserId = OwnerUserId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeProjects.Add(project);
        if (!withTeam)
        {
            await ctx.SaveChangesAsync();
            return (project.Id, 0);
        }

        var team = new Team
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "NDA team",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        ctx.Teams.Add(team);
        await ctx.SaveChangesAsync();
        return (project.Id, team.Id);
    }

    /// <summary>
    /// The page tells the command palette what it is about (#887), so the
    /// palette never has to read the address. See .design/command-palette.md,
    /// "Where you are".
    /// </summary>
    [Fact]
    public async Task The_page_tells_the_palette_which_solution_it_is()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));

        // Drawn once the solution has loaded, not before.
        var marker = cut.WaitForElement("[data-palette-context]");
        marker.GetAttribute("data-palette-context").Should().Be($"solution:{projectId}");
        marker.GetAttribute("data-palette-href").Should().Be($"/solutions/{projectId}");
        marker.HasAttribute("hidden").Should().BeTrue();
    }

    [Fact]
    public void A_solution_being_created_tells_the_palette_nothing()
    {
        var cut = _ctx.Render<ProjectDetail>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Create solution"));
        cut.FindAll("[data-palette-context]").Should().BeEmpty("there is no record to be about yet");
    }

    /// <summary>
    /// A page that is already built can be handed a new tab as new parameters, rather
    /// than built afresh - so a new tab in the address has to move the page, not only
    /// the first one.
    /// </summary>
    [Fact]
    public async Task A_new_tab_in_the_address_moves_the_page_to_it()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Customer"));
        // Let the Customer tab finish its reads before leaving it: switching away
        // disposes the tab but not its chain of queries, and a slow CI runner can
        // still be inside it at teardown.
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));

        // What an enhanced navigation does to a page it keeps: new parameters,
        // same component.
        cut.Render(p => p.Add(c => c.TabName, "repositories"));
        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Repositories"));

        // And back to the bare address, which is Customer's.
        cut.Render(p => p.Add(c => c.TabName, (string?)null));
        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Customer"));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));
    }

    /// <summary>
    /// Each tab of a saved solution has its own address (#1077), and choosing one puts
    /// it in the bar so it can be copied. Customer, the default, is the bare one.
    /// </summary>
    [Fact]
    public async Task Each_tab_puts_its_own_address_in_the_bar()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        cut.WaitForAssertion(() => cut.FindAll(".settings__tabs button").Should().HaveCount(7));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));

        var labels = new[] { "General", "Repositories", "Business Central", "Pipelines", "Symbols", "Access", "Customer" };
        foreach (var label in labels)
        {
            await ClickTabAsync(cut, label);
            cut.WaitForAssertion(() => ActiveTab(cut).Should().Be(label));
            // Each tab reads on the page's context as it opens; leaving before it has
            // finished is a race this test is not about.
            _db.WaitForQueriesToSettle();
        }

        cut.WaitForAssertion(() => Addresses().Should().HaveCount(labels.Length));
        Addresses().Should().Equal(
                $"/solutions/{projectId}/general",
                $"/solutions/{projectId}/repositories",
                $"/solutions/{projectId}/bc",
                $"/solutions/{projectId}/pipelines",
                $"/solutions/{projectId}/symbols",
                $"/solutions/{projectId}/access",
                $"/solutions/{projectId}");
    }

    /// <summary>
    /// What someone typed on General is still there after a look at Repositories:
    /// one Save covers both, and moving between them is not a navigation, which would
    /// build the page afresh.
    /// </summary>
    [Fact]
    public async Task Unsaved_edits_survive_moving_between_tabs()
    {
        var (projectId, _) = await SeedAsync();

        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/solutions/{projectId}/general");
        var cut = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.SolutionId, projectId)
            .Add(c => c.TabName, "general"));
        var name = cut.WaitForElement("#proj-name");
        await cut.InvokeAsync(() => name.Change("CRONUS Danmark"));

        await ClickTabAsync(cut, "Repositories");
        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Repositories"));
        await ClickTabAsync(cut, "General");

        cut.WaitForAssertion(() =>
            cut.Find("#proj-name").GetAttribute("value").Should().Be("CRONUS Danmark"));
        cut.Markup.Should().Contain("Unsaved changes");
        // The address followed the tabs by being rewritten, not navigated to.
        cut.WaitForAssertion(() => Addresses().Should().Equal(
            $"/solutions/{projectId}/repositories", $"/solutions/{projectId}/general"));
    }

    /// <summary>The addresses tab clicks have put in the bar, oldest first.</summary>
    private List<string?> Addresses() =>
        _tabs.Invocations["showTab"].Select(i => (string?)i.Arguments[0]).Where(a => a is not null).ToList();

    private static async Task ClickTabAsync(IRenderedComponent<ProjectDetail> cut, string label)
    {
        var tab = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == label);
        await cut.InvokeAsync(() => tab.Click());
    }

    /// <summary>
    /// The old ?tab= links live on in emails and bookmarks; they forward to the path
    /// form so the page has one address per tab.
    /// </summary>
    [Theory]
    [InlineData("bc", "/bc")]
    [InlineData("repositories", "/repositories")]
    [InlineData("customer", "")]
    [InlineData("nonsense", "")]
    public async Task An_old_tab_link_forwards_to_the_tab_address(string tab, string suffix)
    {
        var (projectId, _) = await SeedAsync();
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/solutions/{projectId}?tab={tab}");

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));

        cut.WaitForAssertion(() => new Uri(nav.Uri).PathAndQuery.Should().Be($"/solutions/{projectId}{suffix}"));
    }

    /// <summary>
    /// A name that is not a tab, Customer's own name, or a different spelling of a tab
    /// settles on the one address that tab has, rather than a 404.
    /// </summary>
    [Theory]
    [InlineData("customer", "")]
    [InlineData("nonsense", "")]
    [InlineData("BC", "/bc")]
    public async Task A_tab_name_settles_on_its_one_address(string tab, string suffix)
    {
        var (projectId, _) = await SeedAsync();
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/solutions/{projectId}/{tab}");

        var cut = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.SolutionId, projectId)
            .Add(c => c.TabName, tab));

        cut.WaitForAssertion(() => new Uri(nav.Uri).PathAndQuery.Should().Be($"/solutions/{projectId}{suffix}"));
    }

    /// <summary>
    /// A link to a tab this person does not get - here Access, for someone who can only
    /// look - opens Customer at its own address instead of an empty page.
    /// </summary>
    [Fact]
    public async Task A_tab_this_person_cannot_open_lands_on_customer()
    {
        var (projectId, _) = await SeedAsync();
        _db.OrgContext.CurrentUserId = OutsiderUserId;
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/solutions/{projectId}/access");

        var cut = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.SolutionId, projectId)
            .Add(c => c.TabName, "access"));

        cut.WaitForAssertion(() => new Uri(nav.Uri).PathAndQuery.Should().Be($"/solutions/{projectId}"));
        cut.FindAll(".module-card__title").Should().BeEmpty("the Access tab is not drawn for them");
    }

    /// <summary>
    /// A private solution's not-found page says nothing about whether the slug exists:
    /// a tab name that would otherwise be tidied is left alone for someone who cannot
    /// see the solution, exactly as for a slug that names nothing.
    /// </summary>
    [Fact]
    public async Task A_tab_on_a_private_solution_says_nothing_to_an_outsider()
    {
        var (projectId, _) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
            project.Slug = "cronus-denmark";
            project.Visibility = ProjectVisibility.Private;
            await ctx.SaveChangesAsync();
        }
        _db.OrgContext.CurrentUserId = OutsiderUserId;
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/solutions/cronus-denmark/nonsense?tab=bc");

        var cut = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.Slug, "cronus-denmark")
            .Add(c => c.TabName, "nonsense"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("This solution doesn't exist"));
        new Uri(nav.Uri).PathAndQuery.Should().Be("/solutions/cronus-denmark/nonsense?tab=bc");
    }

    /// <summary>
    /// With a slug, the tabs use the readable address, and a numeric one with a tab
    /// forwards to it keeping the tab.
    /// </summary>
    [Fact]
    public async Task The_readable_address_carries_the_tab()
    {
        var (projectId, _) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == projectId);
            project.Slug = "cronus-denmark";
            await ctx.SaveChangesAsync();
        }
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo($"/solutions/{projectId}/pipelines");

        var numbered = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.SolutionId, projectId)
            .Add(c => c.TabName, "pipelines"));
        numbered.WaitForAssertion(() => new Uri(nav.Uri).PathAndQuery.Should().Be("/solutions/cronus-denmark/pipelines"));

        var cut = _ctx.Render<ProjectDetail>(p => p
            .Add(c => c.Slug, "cronus-denmark")
            .Add(c => c.TabName, "pipelines"));
        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Pipelines"));
        await ClickTabAsync(cut, "Repositories");
        cut.WaitForAssertion(() => Addresses().Should().Contain("/solutions/cronus-denmark/repositories"));
    }

    /// <summary>The create form has no address to put a tab in; ?tab= still opens one.</summary>
    [Fact]
    public void The_create_form_opens_on_a_tab_and_leaves_the_address_alone()
    {
        var nav = _ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.NavigateTo("/solutions/new?tab=access");

        var cut = _ctx.Render<ProjectDetail>();

        cut.WaitForAssertion(() => ActiveTab(cut).Should().Be("Access"));
        Addresses().Should().BeEmpty();
        new Uri(nav.Uri).PathAndQuery.Should().Be("/solutions/new?tab=access");
    }

    private static string? ActiveTab(IRenderedComponent<ProjectDetail> cut) =>
        cut.FindAll(".settings__tabs .header-tab.is-active")
            .Select(t => t.TextContent.Trim())
            .FirstOrDefault();

    [Fact]
    public async Task Someone_who_cannot_manage_the_project_gets_no_access_tab()
    {
        var (projectId, _) = await SeedAsync();
        _db.OrgContext.CurrentUserId = OutsiderUserId;

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));

        cut.WaitForAssertion(() =>
            cut.FindAll(".settings__tabs button, .settings__tabs a")
                .Select(t => t.TextContent.Trim())
                .Should().NotContain("Access"));
    }

    [Fact]
    public async Task The_owner_gets_the_access_tab_with_the_three_choices()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        var labels = cut.FindAll(".module-card__title").Select(t => t.TextContent.Trim()).ToList();
        labels.Should().Contain(new[] { "Public", "View-only for everyone else", "Private" });
        // Only ever the one outline save on this tab - Generate stays the app's primary.
        cut.FindAll(".settings__body .btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Saving_private_with_a_team_round_trips_through_the_service()
    {
        var (projectId, teamId) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        // Pick Private, then tick the NDA team, then save.
        await PickAsync(cut, "Private");
        cut.WaitForState(() => cut.FindAll("input[type=checkbox]").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll("input[type=checkbox]")[0].Change(true));
        await ClickSaveAccessAsync(cut);

        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
            .Visibility.Should().Be(ProjectVisibility.Private);
        (await verify.OeProjectTeams.AsNoTracking().Where(t => t.ProjectId == projectId).Select(t => t.TeamId)
            .ToListAsync()).Should().Equal(teamId);
    }

    [Fact]
    public async Task Choosing_public_clears_the_team_picks_so_the_save_is_not_refused()
    {
        var (projectId, teamId) = await SeedAsync();
        // Start Private-with-team, the state the user is undoing.
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        await PickAsync(cut, "Public");
        await ClickSaveAccessAsync(cut);

        cut.FindAll(".field-error").Should().BeEmpty("clearing the picks is what keeps this save legal");
        await using var verify = _db.NewContext();
        (await verify.OeProjects.AsNoTracking().FirstAsync(p => p.Id == projectId))
            .Visibility.Should().Be(ProjectVisibility.Public);
        (await verify.OeProjectTeams.AsNoTracking().AnyAsync(t => t.ProjectId == projectId)).Should().BeFalse();
    }

    [Fact]
    public async Task Private_with_no_team_ticked_shows_the_services_error_next_to_the_teams()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");
        await ClickSaveAccessAsync(cut);

        cut.Find(".field-error").TextContent.Should().Contain("at least one team");
    }

    [Fact]
    public async Task With_no_teams_in_the_org_a_non_admin_is_pointed_at_the_teams_page()
    {
        var (projectId, _) = await SeedAsync(withTeam: false);

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        cut.Find(".empty-state__title").TextContent.Trim().Should().Be("No teams yet");
        // A link to somewhere they can act, not a dead end: /teams is open to
        // every signed-in user.
        cut.Find(".empty-state__text a").GetAttribute("href").Should().Be("/teams");
        cut.FindAll(".empty-state__text a[href='/admin/administration/teams']")
            .Should().BeEmpty("only an admin gets the create path");
    }

    /// <summary>
    /// The blocker this review caught: with no teams in the org, the restricted
    /// levels used to stay selectable, and saving one produced a service error
    /// keyed "Teams" that had nowhere to render - a silent no-op. Both halves are
    /// pinned here: the levels are unreachable, and there is no save to press.
    /// </summary>
    [Fact]
    public async Task With_no_teams_the_restricted_levels_are_unavailable_and_there_is_nothing_to_save()
    {
        var (projectId, _) = await SeedAsync(withTeam: false);

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        foreach (var label in new[] { "View-only for everyone else", "Private" })
        {
            var card = cut.FindAll("label.module-card")
                .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == label);
            card.QuerySelector("input[type=radio]")!.HasAttribute("disabled")
                .Should().BeTrue($"{label} can't be saved without a team");
            card.TextContent.Should().Contain("Create a team first");
        }

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Save access"));
    }

    [Fact]
    public async Task Save_is_disabled_until_something_changes_and_discard_puts_it_back()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        SaveButton(cut).HasAttribute("disabled").Should().BeTrue("nothing has changed yet");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Discard changes"));

        await PickAsync(cut, "Private");
        SaveButton(cut).HasAttribute("disabled").Should().BeFalse();

        var discard = cut.FindAll("button").First(b => b.TextContent.Contains("Discard changes"));
        await cut.InvokeAsync(() => discard.Click());

        SaveButton(cut).HasAttribute("disabled").Should().BeTrue("discard restored the loaded state");
        cut.FindAll("label.module-card.is-selected")
            .Single().QuerySelector(".module-card__title")!.TextContent.Trim().Should().Be("Public");
    }

    [Fact]
    public async Task Public_collapses_the_team_list_to_one_line_and_a_restricted_level_brings_it_back()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        cut.FindAll("input[type=checkbox]").Should().BeEmpty("a public solution has no teams to pick");
        cut.Markup.Should().Contain("Teams don't apply to a public solution.");

        await PickAsync(cut, "Private");
        cut.FindAll("input[type=checkbox]").Should().ContainSingle("the list is the affordance");
        cut.Markup.Should().Contain("Teams with access");
    }

    [Fact]
    public async Task A_team_card_names_who_is_on_it()
    {
        var (projectId, teamId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = OwnerUserId,
                IsManager = true, CreatedAt = DateTime.UtcNow,
            });
            ctx.TeamMembers.Add(new TeamMember
            {
                OrganizationId = TestDb.DefaultOrgId, TeamId = teamId, UserId = OutsiderUserId,
                CreatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");

        var card = cut.FindAll("label.module-card")
            .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == "NDA team");
        // Managers first - the person a reader checks for runs the account.
        card.TextContent.Should().Contain("owner@example.com");
    }

    [Fact]
    public async Task The_projects_visibility_shows_beside_its_name_on_every_tab()
    {
        var (projectId, teamId) = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var access = new ProjectAccess(ctx, _db.OrgContext);
            var discovery = new ProjectDiscoveryService(ctx, _db.OrgContext, access, new ProjectDiscoveryQueue(),
                NullLogger<ProjectDiscoveryService>.Instance);
            await new ProjectService(ctx, _db.OrgContext, access, discovery, NullLogger<ProjectService>.Instance)
                .SetAccessAsync(projectId, ProjectVisibility.Private, new[] { teamId });
        }

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        cut.WaitForAssertion(() =>
            cut.Find(".detail-head__title-row .status-pill").TextContent.Trim().Should().Be("Private"));
    }

    [Fact]
    public async Task A_public_project_gets_no_badge()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        await OpenAccessTabAsync(cut);

        cut.FindAll(".detail-head__title-row .status-pill").Should().BeEmpty();
    }

    [Fact]
    public async Task The_owner_follows_by_default_and_can_stop()
    {
        var (projectId, _) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        cut.WaitForAssertion(() => FollowButton(cut).TextContent.Should().Contain("Following"));
        // The Customer tab reads on the page's context too; clicking during that read
        // is a race this test is not about.
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));

        await cut.InvokeAsync(() => FollowButton(cut).Click());

        cut.WaitForAssertion(() => FollowButton(cut).TextContent.Trim().Should().Be("Follow"));
        FollowButton(cut).ClassList.Should().NotContain("btn--primary", "Save stays the only primary button");
        cut.Markup.Should().Contain("no longer following");
        cut.Markup.Should().NotContain("Choose how you hear about it", "there is nothing to hear about after stopping");
    }

    [Fact]
    public async Task Following_again_says_how_the_person_will_hear_from_their_own_choice()
    {
        var (projectId, _) = await SeedAsync();
        var cut = _ctx.Render<ProjectDetail>(p => p.Add(c => c.SolutionId, projectId));
        cut.WaitForAssertion(() => FollowButton(cut).TextContent.Should().Contain("Following"));
        // The Customer tab reads on the page's context too; clicking during that read
        // is a race this test is not about.
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));
        await cut.InvokeAsync(() => FollowButton(cut).Click());
        cut.WaitForAssertion(() => FollowButton(cut).TextContent.Trim().Should().Be("Follow"));

        // Default for followed solutions: listed in the app, no email.
        await cut.InvokeAsync(() => FollowButton(cut).Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("you get no email about it"));
        cut.Markup.Should().Contain("Choose how you hear about it");

        await using (var scope = _ctx.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ALDevToolbox.Services.Notifications.NotificationPreferenceService>()
                .SetEmailForCurrentUserAsync(NotificationCategory.Solutions, NotificationDelivery.Weekly);
        }
        await cut.InvokeAsync(() => FollowButton(cut).Click());
        cut.WaitForAssertion(() => FollowButton(cut).TextContent.Trim().Should().Be("Follow"));
        await cut.InvokeAsync(() => FollowButton(cut).Click());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("in a weekly email digest"));
    }

    private static IElement FollowButton(IRenderedComponent<ProjectDetail> cut) =>
        cut.Find("button[data-follow]");

    private static IElement SaveButton(IRenderedComponent<ProjectDetail> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Save access"));

    // ── Helpers ─────────────────────────────────────────────────────────

    // ── Choosing the level while creating ─────────────────────────────────

    /// <summary>
    /// The level is picked on the create form, so a customer solution is never Public
    /// for the minute between being created and being narrowed - and Public now means
    /// everyone in the organisation can change it. The tab has no save of its own
    /// there: Create solution writes the level with the rest.
    /// </summary>
    [Fact]
    public async Task Creating_a_solution_offers_the_level_and_has_no_save_of_its_own()
    {
        await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>();
        await OpenAccessTabAsync(cut);

        cut.FindAll(".module-card__title").Select(t => t.TextContent.Trim())
            .Should().Contain(new[] { "Public", "View-only for everyone else", "Private" });
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain("Save access", "Create solution writes the level with the rest");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().Contain("Create solution", "the page's own primary stays reachable from this tab");
    }

    /// <summary>
    /// End to end through the form: pick Private, tick the team, fill the name, create -
    /// and the solution exists at that level, never at another one first.
    /// </summary>
    [Fact]
    public async Task A_solution_created_as_private_is_private_from_the_moment_it_exists()
    {
        var (_, teamId) = await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>();
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");
        cut.WaitForState(() => cut.FindAll("input[type=checkbox]").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll("input[type=checkbox]")[0].Change(true));

        // Back to General for the name, the way somebody filling this in would.
        var general = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == "General");
        await cut.InvokeAsync(() => general.Click());
        cut.WaitForState(() => cut.FindAll("input#proj-name").Count > 0);
        await cut.InvokeAsync(() => cut.Find("input#proj-name").Change("CRONUS Sweden"));
        // The base to compile against is required too, so the create gets that far.
        await cut.InvokeAsync(() => cut.Find("input#proj-country").Change("dk"));

        var create = cut.FindAll("button").First(b => b.TextContent.Trim() == "Create solution");
        await cut.InvokeAsync(() => create.Click());

        await using var verify = _db.NewContext();
        OeProject? created = null;
        cut.WaitForAssertion(() =>
        {
            created = verify.OeProjects.AsNoTracking().FirstOrDefault(p => p.Name == "CRONUS Sweden");
            created.Should().NotBeNull();
        });
        created!.Visibility.Should().Be(ProjectVisibility.Private);
        (await verify.OeProjectTeams.AsNoTracking()
            .Where(t => t.ProjectId == created.Id).Select(t => t.TeamId).ToListAsync())
            .Should().Equal(teamId);
    }

    /// <summary>
    /// The tab is rebuilt whenever somebody switches away and back, so the answer has to
    /// live on the page rather than in the tab. Somebody filling this form in will move
    /// between General and Access more than once; a pick that quietly reverted to Public
    /// in between is the whole failure this form exists to prevent.
    /// </summary>
    [Fact]
    public async Task A_level_picked_while_creating_survives_leaving_the_tab_and_coming_back()
    {
        await SeedAsync();

        var cut = _ctx.Render<ProjectDetail>();
        await OpenAccessTabAsync(cut);
        await PickAsync(cut, "Private");
        cut.WaitForState(() => cut.FindAll("input[type=checkbox]").Count > 0);
        await cut.InvokeAsync(() => cut.FindAll("input[type=checkbox]")[0].Change(true));

        var general = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == "General");
        await cut.InvokeAsync(() => general.Click());
        cut.WaitForState(() => cut.FindAll("input#proj-name").Count > 0);
        // The tab they are on says what is currently chosen, so the decision is visible
        // without going back for it.
        cut.Markup.Should().Contain("Only the teams you picked can see this solution.");

        await OpenAccessTabAsync(cut);

        var privateCard = cut.FindAll("label.module-card")
            .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == "Private");
        privateCard.QuerySelector("input[type=radio]")!.HasAttribute("checked").Should().BeTrue();
        cut.FindAll("input[type=checkbox]")[0].HasAttribute("checked")
            .Should().BeTrue("the team ticked before the detour is still ticked");
    }

    private static async Task OpenAccessTabAsync(IRenderedComponent<ProjectDetail> cut)
    {
        cut.WaitForState(() => cut.FindAll(".settings__tabs button")
            .Any(t => t.TextContent.Trim() == "Access"));
        // A saved solution opens on Customer: let it finish reading first. Leaving
        // disposes the tab but not its chain of queries, which would then meet this
        // tab's on the shared context.
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Loading customer details"));
        var tab = cut.FindAll(".settings__tabs button").First(t => t.TextContent.Trim() == "Access");
        await cut.InvokeAsync(() => tab.Click());
    }

    private static async Task PickAsync(IRenderedComponent<ProjectDetail> cut, string label)
    {
        var card = cut.FindAll("label.module-card")
            .First(c => c.QuerySelector(".module-card__title")!.TextContent.Trim() == label);
        await cut.InvokeAsync(() => card.QuerySelector("input[type=radio]")!.Change(true));
    }

    /// <summary>
    /// Clicks "Save access" and waits for the page to say what happened. The
    /// handler is async, so the click returns before the write lands — without
    /// the wait every assertion below would race it.
    /// </summary>
    private static async Task ClickSaveAccessAsync(IRenderedComponent<ProjectDetail> cut)
    {
        var save = SaveButton(cut);
        await cut.InvokeAsync(() => save.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        cut.WaitForState(() => cut.FindAll(".alert--success").Any() || cut.FindAll(".field-error").Any());
    }
}
