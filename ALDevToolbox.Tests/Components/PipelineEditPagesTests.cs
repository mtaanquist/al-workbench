using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The build and deployment pipeline editors as pages (#1080), which they became once
/// the options outgrew a modal.
///
/// <para>Named user: a BC consultant setting up how a customer's extensions are built
/// and where they go. What is pinned here is what the page adds over the modal it
/// replaced: the settings grouped into named sections, saving that lands on the
/// pipeline, a Cancel that goes back where the person came from (and only ever inside
/// the app), and the states a URL can reach that a button never could - a pipeline
/// that is gone, and one the person may look at but not change. The fields themselves
/// are covered in <see cref="GitHubReleaseDialogsTests"/>.</para>
/// </summary>
public sealed class PipelineEditPagesTests : IDisposable
{
    private const int OwnerUserId = 10801;
    private const int ColleagueUserId = 10802;

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public PipelineEditPagesTests()
    {
        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddSingleton<ALDevToolbox.Services.Tools.IToolAvailability>(TestDb.EverythingEnabled());
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddScoped<OrganizationConfigService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
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

        using var seed = _db.NewContext();
        seed.Users.AddRange(NewUser(OwnerUserId, "owner@cronus.example"), NewUser(ColleagueUserId, "nils@cronus.example"));
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = OwnerUserId;
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    private NavigationManager Nav => _ctx.Services.GetRequiredService<NavigationManager>();

    // ── Build pipelines ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_build_pipeline_page_groups_its_settings_into_named_sections()
    {
        var seed = await SeedAsync();

        var cut = RenderNewPipeline(seed.ProjectId);

        cut.FindAll(".edit-col > .card .card__title").Select(t => t.TextContent.Trim())
            .Should().Equal("Source", "Extensions", "When it builds", "Versions and releases", "Name");
        cut.Find("#pe-solution-name").TextContent.Should().Be("CRONUS A/S", "the solution in the address is fixed, not picked");
        cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Create build pipeline");
    }

    [Fact]
    public async Task Creating_a_build_pipeline_saves_it_and_opens_it()
    {
        var seed = await SeedAsync();

        var cut = RenderNewPipeline(seed.ProjectId);
        cut.Find("#pe-branch").Input("release/28.2");
        cut.Find("#pe-preview-check").Change(true);
        cut.Find(".page-head .btn--primary").Click();

        int id = 0;
        cut.WaitForAssertion(() =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(Nav.ToBaseRelativePath(Nav.Uri), @"^pipelines/(\d+)$");
            match.Success.Should().BeTrue(Nav.Uri);
            id = int.Parse(match.Groups[1].Value);
        });
        await using var read = _db.NewContext();
        var saved = await read.OePipelines.AsNoTracking().SingleAsync(p => p.Id == id);
        saved.Branch.Should().Be("release/28.2");
        saved.PreviewCheck.Should().BeTrue();
        saved.Name.Should().Be("release/28.2");
    }

    [Fact]
    public async Task Editing_a_build_pipeline_saves_over_it_and_returns_to_it()
    {
        var seed = await SeedAsync();

        var cut = _ctx.Render<PipelineEdit>(p => p.Add(x => x.PipelineId, seed.PipelineId));
        cut.WaitForAssertion(() => cut.Find(".pe-picker"));
        cut.FindAll(".btn--primary").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Save build pipeline");
        cut.Find("#pe-changed-apps-only").HasAttribute("checked").Should().BeTrue("only changed extensions are published unless turned off");
        cut.Find("#pe-branch").Input("test");
        cut.Find("#pe-changed-apps-only").Change(false);
        cut.Find(".page-head .btn--primary").Click();

        cut.WaitForAssertion(() => Nav.ToBaseRelativePath(Nav.Uri).Should().Be($"pipelines/{seed.PipelineId}"));
        await using var read = _db.NewContext();
        var saved = await read.OePipelines.AsNoTracking().SingleAsync(p => p.Id == seed.PipelineId);
        saved.Branch.Should().Be("test");
        saved.ChangedAppsOnly.Should().BeFalse();
    }

    [Fact]
    public async Task A_typed_build_pipeline_name_says_that_changing_the_branch_or_extensions_resets_it()
    {
        var seed = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var pipeline = await ctx.OePipelines.SingleAsync(p => p.Id == seed.PipelineId);
            pipeline.NameIsCustom = true;
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<PipelineEdit>(p => p.Add(x => x.PipelineId, seed.PipelineId));

        cut.WaitForAssertion(() => cut.Find("#pe-name").GetAttribute("value").Should().Be("Nightly"));
        cut.Find("#pe-name-hint").TextContent.Should().Be(
            "If you change the branch or the extensions, this name is replaced with the automatic one. Leave it blank to use the automatic name now.");
    }

    [Fact]
    public async Task A_pipeline_that_is_gone_says_so_instead_of_drawing_a_form()
    {
        await SeedAsync();

        var cut = _ctx.Render<PipelineEdit>(p => p.Add(x => x.PipelineId, 999_999));

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Trim().Should().Be("This pipeline doesn't exist"));
        cut.FindAll("form").Should().BeEmpty();
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_is_told_so_and_cannot_save()
    {
        // Everyone can see a read-only solution; only its owner (or an admin) changes it.
        var seed = await SeedAsync(visibility: ProjectVisibility.ReadOnly);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = _ctx.Render<PipelineEdit>(p => p.Add(x => x.PipelineId, seed.PipelineId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Only people who manage the solution can change this solution's pipelines."));
        cut.FindAll("form").Should().BeEmpty();
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_build_pipeline_for_a_solution_the_person_cannot_manage_is_refused_up_front()
    {
        var seed = await SeedAsync(visibility: ProjectVisibility.ReadOnly);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        Nav.NavigateTo($"/pipelines/new?solution={seed.ProjectId}");
        var cut = _ctx.Render<PipelineEdit>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Only people who manage the solution can add pipelines to CRONUS A/S."));
        cut.FindAll(".pe-picker").Should().BeEmpty();
        cut.FindAll("#pe-branch").Should().BeEmpty("there is nothing to fill in that could be saved");
        cut.FindAll(".edit-col > .card .card__title").Select(t => t.TextContent.Trim()).Should().Equal("Source", "Extensions");
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Theory]
    [InlineData("/solutions/1/pipelines", "/solutions/1/pipelines")]
    [InlineData("//evil.example/phish", "/pipelines/builds")]
    [InlineData("https://evil.example/phish", "/pipelines/builds")]
    [InlineData("/\\evil.example", "/pipelines/builds")]
    public async Task Cancel_goes_back_where_the_person_came_from_but_never_off_the_site(string returnUrl, string expected)
    {
        var seed = await SeedAsync();

        Nav.NavigateTo($"/pipelines/new?solution={seed.ProjectId}&returnUrl={Uri.EscapeDataString(returnUrl)}");
        var cut = _ctx.Render<PipelineEdit>();

        cut.WaitForAssertion(() => cut.Find(".page-head__actions a.btn").GetAttribute("href").Should().Be(expected));
    }

    [Fact]
    public async Task Without_a_solution_in_the_address_the_page_offers_the_solutions_to_pick_from()
    {
        await SeedAsync();

        Nav.NavigateTo("/pipelines/new");
        var cut = _ctx.Render<PipelineEdit>();

        cut.WaitForAssertion(() => cut.FindAll("#pe-solution option").Select(o => o.TextContent.Trim())
            .Should().Equal("Choose a solution...", "CRONUS A/S"));
        cut.Markup.Should().Contain("Choose a solution to see the extensions you can build.");
        cut.Find(".page-head .btn--primary").HasAttribute("disabled").Should().BeTrue();
    }

    // ── Deployment pipelines ────────────────────────────────────────────────

    [Fact]
    public async Task The_deployment_pipeline_page_groups_its_settings_into_named_sections()
    {
        var seed = await SeedAsync();

        Nav.NavigateTo($"/pipelines/deployments/new?solution={seed.ProjectId}&buildPipeline={seed.PipelineId}");
        var cut = _ctx.Render<ReleasePipelineEdit>();

        cut.WaitForAssertion(() => cut.FindAll(".edit-col > .card .card__title").Select(t => t.TextContent.Trim())
            .Should().Equal("Source", "Target", "Installing", "Automation", "Name"));
        cut.Find("#rpe-build").GetAttribute("value").Should().Be(seed.PipelineId.ToString(), "the build pipeline in the address is filled in");
    }

    [Fact]
    public async Task A_build_pipeline_of_another_solution_in_the_address_is_not_filled_in()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync("Other A/S");

        Nav.NavigateTo($"/pipelines/deployments/new?solution={seed.ProjectId}&buildPipeline={other.PipelineId}");
        var cut = _ctx.Render<ReleasePipelineEdit>();

        cut.WaitForAssertion(() => cut.Find("#rpe-build").GetAttribute("value").Should().Be("0"));
    }

    [Fact]
    public async Task A_deployment_pipeline_whose_build_pipeline_was_deleted_asks_for_another_before_saving()
    {
        var seed = await SeedAsync();
        var rpId = await SeedDeploymentPipelineAsync(seed);
        await using (var db = _db.NewContext())
        {
            db.OePipelines.Add(new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "main",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
            await db.OePipelines.Where(p => p.Id == seed.PipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.DeletedAt, DateTime.UtcNow));
        }

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));

        cut.WaitForAssertion(() => cut.Find("#rpe-build").GetAttribute("value").Should().Be("0"));
        cut.Find("#rpe-build").ParentElement!.ParentElement!.TextContent.Should().Contain("\"Nightly\" was deleted. Choose the build pipeline to deploy from now.");
        cut.Find(".page-head .btn--primary").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Editing_a_deployment_pipeline_saves_over_it_and_returns_to_it()
    {
        var seed = await SeedAsync();
        var rpId = await SeedDeploymentPipelineAsync(seed);

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));
        cut.WaitForAssertion(() => cut.Find("#rpe-prepare"));
        cut.Find("#rpe-prepare").Change(true);
        cut.Find(".page-head .btn--primary").Click();

        cut.WaitForAssertion(() => Nav.ToBaseRelativePath(Nav.Uri).Should().Be($"pipelines/deployments/{rpId}"));
        await using var read = _db.NewContext();
        (await read.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == rpId)).PrepareReleaseOnNewBuild.Should().BeTrue();
    }

    [Fact]
    public async Task A_typed_deployment_pipeline_name_says_that_changing_the_source_or_environment_resets_it()
    {
        var seed = await SeedAsync();
        var rpId = await SeedDeploymentPipelineAsync(seed);
        await using (var ctx = _db.NewContext())
        {
            var rp = await ctx.OeReleasePipelines.SingleAsync(r => r.Id == rpId);
            rp.Name = "Nightly to Test - CRONUS";
            rp.NameIsCustom = true;
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));

        cut.WaitForAssertion(() => cut.Find("#rpe-name-hint").TextContent.Should().Be(
            "If you change where the apps come from or the target environment, this name is replaced with the automatic one. Leave it blank to use the automatic name now."));
    }

    [Fact]
    public async Task A_sandbox_pipeline_can_be_set_to_deploy_new_builds_without_approval()
    {
        var seed = await SeedAsync();
        var rpId = await SeedDeploymentPipelineAsync(seed);

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));
        cut.WaitForAssertion(() => cut.Find("#rpe-prepare"));
        cut.FindAll("#rpe-without-approval").Should().BeEmpty("it only follows a prepared deployment");
        cut.Find("#rpe-prepare").Change(true);
        // The solution's environments load after the form, so the sandbox may arrive later.
        cut.WaitForAssertion(() => cut.Find("#rpe-without-approval"));
        cut.Find("#rpe-without-approval").Change(true);
        cut.Find(".page-head .btn--primary").Click();

        cut.WaitForAssertion(() => Nav.ToBaseRelativePath(Nav.Uri).Should().Be($"pipelines/deployments/{rpId}"));
        await using var read = _db.NewContext();
        var rp = await read.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Id == rpId);
        rp.DeployWithoutApproval.Should().BeTrue();
        rp.DeployWithoutApprovalByUserId.Should().Be(OwnerUserId);
    }

    [Fact]
    public async Task A_production_pipeline_is_never_offered_deploying_without_approval()
    {
        var seed = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == seed.TestEnvId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Type, "Production"));
        }
        var rpId = await SeedDeploymentPipelineAsync(seed);

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));
        cut.WaitForAssertion(() => cut.Find("#rpe-prepare"));
        cut.Find("#rpe-prepare").Change(true);

        // Once the environments have loaded, the page says why the option isn't there.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("choose a sandbox as the target environment"));
        cut.FindAll("#rpe-without-approval").Should().BeEmpty();
    }

    [Fact]
    public async Task A_deployment_pipeline_that_is_gone_says_so_instead_of_drawing_a_form()
    {
        await SeedAsync();

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, 999_999));

        cut.WaitForAssertion(() => cut.Find(".empty-state__title").TextContent.Trim().Should().Be("This deployment pipeline doesn't exist"));
        cut.FindAll("form").Should().BeEmpty();
    }

    [Fact]
    public async Task A_new_deployment_pipeline_for_a_solution_the_person_cannot_manage_is_refused_up_front()
    {
        var seed = await SeedAsync(visibility: ProjectVisibility.ReadOnly);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        Nav.NavigateTo($"/pipelines/deployments/new?solution={seed.ProjectId}");
        var cut = _ctx.Render<ReleasePipelineEdit>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Only people who manage the solution can add deployment pipelines to CRONUS A/S."));
        cut.FindAll("#rpe-env").Should().BeEmpty();
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_cannot_edit_its_deployment_pipelines()
    {
        var seed = await SeedAsync(visibility: ProjectVisibility.ReadOnly);
        var rpId = await SeedDeploymentPipelineAsync(seed);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, rpId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Only people who manage the solution can change this solution's deployment pipelines."));
        cut.FindAll("form").Should().BeEmpty();
        cut.FindAll(".btn--primary").Should().BeEmpty();
    }

    [Fact]
    public async Task Save_says_what_is_still_missing_on_a_new_deployment_pipeline()
    {
        var seed = await SeedAsync();

        Nav.NavigateTo($"/pipelines/deployments/new?solution={seed.ProjectId}&buildPipeline={seed.PipelineId}");
        var cut = _ctx.Render<ReleasePipelineEdit>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Choose a target environment to continue."));
        cut.Find(".page-head .btn--primary").HasAttribute("disabled").Should().BeTrue();
    }

    // --- rendering and seeding -----------------------------------------------

    private IRenderedComponent<PipelineEdit> RenderNewPipeline(int projectId)
    {
        Nav.NavigateTo($"/pipelines/new?solution={projectId}");
        var cut = _ctx.Render<PipelineEdit>();
        cut.WaitForAssertion(() => cut.Find(".pe-picker"));
        return cut;
    }

    private sealed record Seed(int ProjectId, int PipelineId, int TestEnvId);

    /// <summary>A solution the owner owns, already discovered, with one build pipeline and a sandbox.</summary>
    private async Task<Seed> SeedAsync(string name = "CRONUS A/S", ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = name, CreatedAt = now, UpdatedAt = now,
            CreatedByUserId = OwnerUserId, Visibility = visibility,
            DiscoveredAt = now,
            DiscoveredExtensionsJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new DiscoveredExtension(Guid.NewGuid().ToString(), "CRONUS Base", "CRONUS", "28.2.0.0",
                    "https://dev.azure.com/cronus/base/_git/base", "base"),
            }),
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Nightly",
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        var env = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Name = "Test", Type = "Sandbox", FetchedAt = now,
        };
        ctx.OeProjectEnvironments.Add(env);
        await ctx.SaveChangesAsync();
        return new Seed(project.Id, pipeline.Id, env.Id);
    }

    private async Task<int> SeedDeploymentPipelineAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        var rp = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "Nightly to Test",
            BuildPipelineId = seed.PipelineId, ProjectEnvironmentId = seed.TestEnvId,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        ctx.OeReleasePipelines.Add(rp);
        await ctx.SaveChangesAsync();
        return rp.Id;
    }

    private static User NewUser(int id, string email) => new()
    {
        Id = id, OrganizationId = TestDb.DefaultOrgId, Email = email, DisplayName = email,
        PasswordHash = "x", Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
    };
}
