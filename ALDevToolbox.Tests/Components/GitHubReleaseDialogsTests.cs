using System.Net;
using System.Security.Cryptography;
using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Components.Shared;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Tests.GitHub;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Organizations;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The two editors GitHub Releases changed (issue #632), rendered.
///
/// <para>Named user: a consultant whose customer wants every shipped <c>.app</c> on the
/// repository's Releases page, and who sometimes has to redeploy a version the workbench
/// did not build. What is pinned here is that neither editor shows them any GitHub
/// machinery until it can act - an organisation on Azure DevOps, or one that has not
/// connected GitHub, sees the dialogs exactly as they were - and that when the choice
/// is offered, choosing it swaps the field underneath rather than adding a second one
/// that contradicts it.</para>
///
/// <para>A screenshot is not possible in this environment, so these renders are the
/// evidence for the "looked at it rendered" check.</para>
/// </summary>
public sealed class GitHubReleaseDialogsTests : IDisposable
{
    private const int UserId = 9632;
    private const long InstallationId = 42;
    private const string OrgLogin = "cronus-dk";
    private const string Repo = OrgLogin + "/cronus-customer";

    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public GitHubReleaseDialogsTests()
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
        // The release editor lists the solution's Business Central environments; the
        // chain has to resolve for it to render, though nothing here calls out.
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
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
        _ctx.Services.AddSingleton(NullLoggerFactory.Instance);
        _ctx.Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>),
            typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));

        using var seed = _db.NewContext();
        seed.Users.Add(new User
        {
            Id = UserId, OrganizationId = TestDb.DefaultOrgId, Email = "consultant@cronus.example",
            DisplayName = "consultant@cronus.example", PasswordHash = "x",
            Role = UserRole.User, Status = UserStatus.Active, CreatedAt = DateTime.UtcNow,
        });
        seed.SaveChanges();
        _db.OrgContext.CurrentUserId = UserId;
        _db.OrgContext.IsSiteAdmin = true; // manage rights on an ownerless solution
    }

    public void Dispose()
    {
        _db.WaitForQueriesToSettle();
        _ctx.Dispose();
        _db.Dispose();
    }

    // ── The build pipeline's publishing option ──────────────────────────────

    [Fact]
    public async Task The_pipeline_editor_offers_the_solutions_github_repositories_to_publish_to()
    {
        var seed = await SeedAsync();
        await ConnectOrganisationAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewPipeline(seed.ProjectId);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Publish successful builds to GitHub"));
        var options = cut.FindAll("#pe-release-repo option").Select(o => o.TextContent.Trim()).ToList();
        // Not publishing is the default, and it is a choice you can see, not an absence.
        options.Should().Equal("Don't publish releases", Repo);
    }

    [Fact]
    public async Task An_organisation_with_no_github_connection_sees_no_publishing_option_at_all()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewPipeline(seed.ProjectId);

        cut.FindAll("#pe-release-repo").Should().BeEmpty();
        cut.Markup.Should().NotContain("GitHub Release");
    }

    [Fact]
    public async Task A_solution_on_github_with_nothing_connected_is_told_who_can_connect_it()
    {
        var seed = await SeedAsync();
        await ConfigureDeploymentAppAsync(); // the app exists; this organisation has not connected
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewPipeline(seed.ProjectId);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Publish successful builds to GitHub"));
        cut.Find("#pe-release-repo").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("GitHub isn't connected for this organisation yet");
    }

    // ── The release pipeline's artifact source ──────────────────────────────

    // ── Nightly preview check (#994) ────────────────────────────────────────

    [Fact]
    public async Task The_pipeline_editor_offers_the_nightly_preview_check_off_by_default()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewPipeline(seed.ProjectId);

        var box = cut.Find("#pe-preview-check");
        box.HasAttribute("checked").Should().BeFalse("a new pipeline only builds what the extensions declare");
        box.Closest("label")!.TextContent.Trim().Should().Be("Check against upcoming Business Central versions every night");
        var caption = box.Closest(".field")!.QuerySelector(".field__hint")!.TextContent.Trim();
        caption.Should().Be("Builds against Microsoft's previews of the next minor and next major Business Central versions, to catch breaking changes before they reach your customers. These preview builds can't be deployed; their results show beside the pipeline in the pipelines list.");
        // The caption says what the person needs, not how the tool does it.
        caption.Should().NotContainAny("insider", "artifact", "compiler", "scheduler");
    }

    [Fact]
    public async Task Editing_a_pipeline_with_the_check_on_shows_it_ticked()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        await using (var ctx = _db.NewContext())
        {
            await ctx.OePipelines.Where(p => p.Id == seed.PipelineId)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.PreviewCheck, true));
        }

        var cut = _ctx.Render<PipelineEdit>(p => p.Add(x => x.PipelineId, seed.PipelineId));

        cut.WaitForAssertion(() => cut.Find("#pe-preview-check").HasAttribute("checked").Should().BeTrue());
    }

    [Fact]
    public async Task The_release_editor_offers_the_two_sources_and_swaps_the_field_underneath()
    {
        var seed = await SeedAsync();
        await ConnectOrganisationAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        // Builds are the default, so the build-pipeline picker is what is showing.
        cut.FindAll("#rpe-build").Should().ContainSingle();
        cut.FindAll("#rpe-repo").Should().BeEmpty();

        // The page can still re-render from its own loading after the first paint, which
        // retires the handler a found element points at; pick the source again until the
        // change lands on the current render.
        cut.WaitForAssertion(() =>
        {
            cut.InvokeAsync(() => cut.Find("#rpe-source").Change(ReleaseArtifactSource.GithubRelease)).GetAwaiter().GetResult();
            cut.FindAll("#rpe-build").Should().BeEmpty();
        });

        // One source at a time: the field it replaces is gone, not merely ignored.
        cut.FindAll("#rpe-build").Should().BeEmpty();
        cut.FindAll("#rpe-repo option").Select(o => o.TextContent.Trim())
            .Should().Equal("Choose a repository...", Repo);
    }

    [Fact]
    public async Task Without_a_github_connection_the_release_editor_is_exactly_what_it_was()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        cut.FindAll("#rpe-source").Should().BeEmpty();
        cut.FindAll("#rpe-build").Should().ContainSingle();
    }

    // ── The generated name ───────────────────────────────────────────────────
    //
    // Named user: a consultant setting up their first deployment pipeline, who should
    // not have to invent a name for something the form already knows - what it
    // deploys, and where to. See .design/artifacts.md, "Pipeline names".

    [Fact]
    public async Task The_name_is_shown_once_the_source_and_the_environment_are_both_chosen()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        // Half a choice is not enough to name anything.
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-build").Change(seed.PipelineId.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("-");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-env").Change(production.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("Nightly to Production");
        });
        cut.FindAll("#rpe-name").Should().BeEmpty("the name is not typed unless it is taken");
    }

    [Fact]
    public async Task Changing_the_environment_changes_the_name()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");
        var test = await EnvironmentIdAsync("Test");

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-build").Change(seed.PipelineId.ToString());
            cut.Find("#rpe-env").Change(production.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("Nightly to Production");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-env").Change(test.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("Nightly to Test");
        });
    }

    [Fact]
    public async Task A_taken_name_opens_a_name_field_and_the_name_typed_there_is_saved()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleasePipelines.Add(new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "Nightly to Production",
                BuildPipelineId = seed.PipelineId, ProjectEnvironmentId = production,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-build").Change(seed.PipelineId.ToString());
            cut.Find("#rpe-env").Change(production.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("Nightly to Production");
        });
        cut.Find(".page-head .btn--primary").Click();
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-name");
            cut.Find(".field-error").TextContent.Should().Contain("already called 'Nightly to Production'");
        });

        cut.Find("#rpe-name").Change("CRONUS go-live");
        cut.Find(".page-head .btn--primary").Click();
        cut.WaitForAssertion(() => _ctx.Services.GetRequiredService<NavigationManager>().Uri
            .Should().MatchRegex(@"/pipelines/deployments/\d+$", "saving opens the new pipeline"));

        await using var read = _db.NewContext();
        var typed = await read.OeReleasePipelines.AsNoTracking().SingleAsync(r => r.Name == "CRONUS go-live");
        typed.NameIsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task A_github_repository_is_named_by_its_display_name()
    {
        var seed = await SeedAsync();
        await ConnectOrganisationAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        cut.WaitForAssertion(() => cut.Find("#rpe-source").Change(ReleaseArtifactSource.GithubRelease));
        cut.WaitForAssertion(() =>
        {
            var repoId = cut.FindAll("#rpe-repo option").Last().GetAttribute("value")!;
            cut.Find("#rpe-repo").Change(repoId);
            cut.Find("#rpe-env").Change(production.ToString());
            cut.Find("#rpe-generated-name").TextContent.Should().Be("cronus-customer releases to Production");
        });
    }

    [Fact]
    public async Task Editing_a_pipeline_with_a_typed_name_shows_that_name_for_editing()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");
        var test = await EnvironmentIdAsync("Test");
        await using (var ctx = _db.NewContext())
        {
            ctx.OeReleasePipelines.Add(new OeReleasePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = "Go-live", NameIsCustom = true,
                BuildPipelineId = seed.PipelineId, ProjectEnvironmentId = production,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }
        var row = (await _ctx.Services.GetRequiredService<ReleasePipelineService>()
            .ListReleasePipelinesAsync(seed.ProjectId)).Single();

        var cut = _ctx.Render<ReleasePipelineEdit>(p => p.Add(x => x.Id, row.Id));

        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-env").Change(test.ToString());
            cut.Find("#rpe-env").GetAttribute("value").Should().Be(test.ToString());
        });
        cut.WaitForAssertion(() => cut.Find("#rpe-name").GetAttribute("value").Should().Be("Go-live"));
    }

    [Fact]
    public async Task The_delivery_window_choice_follows_the_chosen_environment()
    {
        var seed = await SeedAsync();
        _db.AddGitHubServices(_ctx.Services, new FakeGitHubApi());
        var production = await EnvironmentIdAsync("Production");
        var test = await EnvironmentIdAsync("Test");
        await using (var ctx = _db.NewContext())
        {
            await ctx.OeProjectEnvironments.Where(e => e.Id == production)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.UpdateWindowStart, new TimeOnly(22, 0))
                    .SetProperty(e => e.UpdateWindowEnd, new TimeOnly(4, 0)));
        }

        var cut = RenderNewDeploymentPipeline(seed.ProjectId);

        const string windowOption = $"#rpe-schedule option[value='{BcDeploymentSchedule.OurDeliveryWindow}']";
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-build").Change(seed.PipelineId.ToString());
            cut.Find("#rpe-env").Change(production.ToString());
            var option = cut.Find(windowOption);
            option.TextContent.Should().Be("In Production's delivery window");
            option.HasAttribute("disabled").Should().BeFalse();
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-schedule").Change(BcDeploymentSchedule.OurDeliveryWindow);
            cut.Markup.Should().Contain("22:00-04:00");
        });
        cut.WaitForAssertion(() =>
        {
            cut.Find("#rpe-env").Change(test.ToString());
            var option = cut.Find(windowOption);
            option.TextContent.Should().Be("In Test's delivery window (none set yet)");
            option.HasAttribute("disabled").Should().BeTrue();
            cut.Find($"a[href='/environments/{test}']").TextContent.Should().ContainEquivalentOf("set one on the environment's page");
            cut.Find(".page-head .btn--primary").HasAttribute("disabled").Should().BeTrue(
                "a pipeline cannot be saved to install in a window its environment does not have");
        });
    }

    // --- rendering ------------------------------------------------------------

    private IRenderedComponent<PipelineEdit> RenderNewPipeline(int projectId)
    {
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"/pipelines/new?solution={projectId}");
        var cut = _ctx.Render<PipelineEdit>();
        // The repositories load before the extensions, so once the checklist is up the
        // publishing field has its final shape.
        cut.WaitForAssertion(() => cut.Find(".pe-picker"));
        return cut;
    }

    private IRenderedComponent<ReleasePipelineEdit> RenderNewDeploymentPipeline(int projectId)
    {
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo($"/pipelines/deployments/new?solution={projectId}");
        var cut = _ctx.Render<ReleasePipelineEdit>();
        cut.WaitForAssertion(() => cut.Find("#rpe-env"));
        return cut;
    }

    // --- seeding -------------------------------------------------------------

    private sealed record Seed(int ProjectId, int PipelineId);

    private async Task<Seed> SeedAsync()
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId, Name = "CRONUS A/S", CreatedAt = now, UpdatedAt = now,
            // Discovered already, so the build pipeline page settles on its checklist
            // instead of waiting on a discovery nothing here would run.
            DiscoveredAt = now,
            DiscoveredExtensionsJson = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new DiscoveredExtension("11111111-1111-1111-1111-111111111111", "CRONUS Base", "CRONUS", "28.2.0.0",
                    $"https://github.com/{Repo}.git", "cronus-customer"),
            }),
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        ctx.OeProjectRepositories.Add(new OeProjectRepository
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Provider = RepositoryProvider.GitHub, Url = $"https://github.com/{Repo}.git",
            DisplayName = "cronus-customer",
        });
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Nightly",
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Name = "Production", Type = "Production", FetchedAt = now,
        });
        ctx.OeProjectEnvironments.Add(new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id,
            Name = "Test", Type = "Sandbox", FetchedAt = now,
        });
        await ctx.SaveChangesAsync();
        return new Seed(project.Id, pipeline.Id);
    }

    private async Task<int> EnvironmentIdAsync(string name)
    {
        await using var ctx = _db.NewContext();
        return await ctx.OeProjectEnvironments.Where(e => e.Name == name).Select(e => e.Id).SingleAsync();
    }

    /// <summary>The deployment-wide app, with no organisation connected to it.</summary>
    private async Task ConfigureDeploymentAppAsync()
    {
        using var rsa = RSA.Create(2048);
        await _db.NewSystemSettingsService(_db.NewContext()).SaveGitHubAppAsync(new GitHubAppInput(
            AppId: "123456", AppSlug: "al-workbench", ClientId: "Iv1.cronus",
            ClientSecret: "s3cr3t", ClearClientSecret: false,
            PrivateKeyPem: rsa.ExportRSAPrivateKeyPem(), ClearPrivateKey: false));
    }

    private async Task ConnectOrganisationAsync()
    {
        await ConfigureDeploymentAppAsync();

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
}
