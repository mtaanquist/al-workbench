using Microsoft.AspNetCore.Components;
using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// "Release..." on a successful build's row of the build pipeline page (#938): the
/// shortcut .design/saas-delivery.md describes beside the release pipeline's own
/// Release action. Named user: a BC consultant looking at a green build who wants it
/// in the customer's environment without hunting for the release pipeline first.
///
/// <para>The row resolves the target and hands over to the same ReleaseBuildDialog the
/// release pipeline's page opens, with the row's build selected - so everything that
/// dialog enforces (the Production acknowledgement, the schedule, the secret warning)
/// still stands. Nothing here releases: Business Central is never reached.</para>
/// </summary>
public sealed class PipelineBuildsReleaseTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    private const int OwnerUserId = 9380;
    private const int ColleagueUserId = 9381;

    public PipelineBuildsReleaseTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("owner@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString)
                .AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ArtifactService>();
        _ctx.Services.AddScoped<PipelineService>();
        _ctx.Services.AddScoped<BuildFreshnessService>();
        _ctx.Services.AddScoped<ReleasePipelineService>();
        _ctx.Services.AddScoped<ProjectDiscoveryService>();
        _ctx.Services.AddScoped<ProjectService>();
        _ctx.Services.AddSingleton(new ProjectDiscoveryQueue());
        // The Build button's service. Nothing here builds, so only what it keeps
        // for itself is real.
        _ctx.Services.AddScoped(sp => new ProjectBuildImporter(
            null!, new ALDevToolbox.Services.ObjectExplorer.Import.ReleaseImportQueue(), null!,
            sp.GetRequiredService<ALDevToolbox.Data.AppDbContext>(), _db.OrgContext,
            sp.GetRequiredService<ProjectAccess>(), null!, TimeProvider.System,
            NullLogger<ProjectBuildImporter>.Instance));
        // The release dialog and the release pipeline editor, and the Business Central
        // connection they read the secret expiry and environments from. The clients are
        // never called.
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

    private sealed record Seed(int ProjectId, int PipelineId, int OlderBuildId, int NewerBuildId, int ProductionEnvId, int SandboxEnvId);

    /// <summary>
    /// A solution with one build pipeline and two successful builds (so "the row's
    /// build" and "the latest build" can differ), and two environments. Release
    /// pipelines are added per test.
    /// </summary>
    private async Task<Seed> SeedAsync(ProjectVisibility visibility = ProjectVisibility.Public)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = "CRONUS Denmark",
            CreatedByUserId = OwnerUserId,
            Visibility = visibility,
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjects.Add(project);
        await ctx.SaveChangesAsync();

        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "CRONUS App",
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OePipelines.Add(pipeline);
        var production = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "Production", Type = "Production",
            FetchedAt = now,
        };
        var sandbox = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = project.Id, Name = "UAT", Type = "Sandbox",
            FetchedAt = now,
        };
        ctx.OeProjectEnvironments.AddRange(production, sandbox);
        await ctx.SaveChangesAsync();

        var older = await SeedBuildAsync(ctx, project.Id, pipeline.Id, now.AddDays(-2));
        var newer = await SeedBuildAsync(ctx, project.Id, pipeline.Id, now.AddHours(-1));
        return new Seed(project.Id, pipeline.Id, older, newer, production.Id, sandbox.Id);
    }

    private static async Task<int> SeedBuildAsync(ALDevToolbox.Data.AppDbContext ctx, int projectId, int pipelineId, DateTime at)
    {
        var build = new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = projectId, PipelineId = pipelineId,
            Status = ProjectBuildStatus.Ready, StartedAt = at, FinishedAt = at.AddMinutes(2),
        };
        ctx.OeProjectBuilds.Add(build);
        await ctx.SaveChangesAsync();
        ctx.OeProjectBuildArtifacts.Add(new OeProjectBuildArtifact
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectBuildId = build.Id,
            FileName = "CRONUS Sales Extension_1.0.0.0.app", AppName = "CRONUS Sales Extension", AppVersion = "1.0.0.0",
            SizeBytes = 3, Content = new byte[] { 1, 2, 3 }, CreatedAt = at,
        });
        await ctx.SaveChangesAsync();
        return build.Id;
    }

    private async Task<int> SeedReleasePipelineAsync(Seed seed, string name, int envId)
    {
        await using var ctx = _db.NewContext();
        var now = DateTime.UtcNow;
        var rp = new OeReleasePipeline
        {
            OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, Name = name,
            BuildPipelineId = seed.PipelineId, ProjectEnvironmentId = envId,
            DeploymentSchedule = BcDeploymentSchedule.Immediate, SchemaSyncMode = BcSyncMode.Add,
            CreatedAt = now, UpdatedAt = now,
        };
        ctx.OeReleasePipelines.Add(rp);
        await ctx.SaveChangesAsync();
        return rp.Id;
    }

    private IRenderedComponent<PipelineBuilds> RenderPage(Seed seed)
    {
        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(c => c.PipelineId, seed.PipelineId));
        cut.WaitForAssertion(() => cut.FindAll(".data-table tbody tr").Should().HaveCount(2));
        return cut;
    }

    private static string ReleaseButton(int buildId) => $"button[aria-label='Deploy build #{buildId}']";

    /// <summary>
    /// Acts once, inside the wait, then asserts until the render catches up. Each step
    /// here replaces the dialog it clicked in, so a retry that clicked again would find
    /// nothing to click - hence the guard rather than a bare click in the lambda.
    /// </summary>
    private static void ActThen(IRenderedComponent<PipelineBuilds> cut, Action act, Action assert)
    {
        var acted = false;
        cut.WaitForAssertion(() =>
        {
            if (!acted)
            {
                act();
                acted = true;
            }
            assert();
        });
    }

    [Fact]
    public async Task With_one_release_pipeline_the_row_opens_the_release_with_that_build_selected()
    {
        var seed = await SeedAsync();
        await SeedReleasePipelineAsync(seed, "CRONUS App → Production", seed.ProductionEnvId);
        var cut = RenderPage(seed);

        // The older build, so "selected" can't be the dialog's own default of the latest.
        ActThen(cut,
            () => cut.Find(ReleaseButton(seed.OlderBuildId)).Click(),
            () => cut.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS Denmark — Production"));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#pb-rel-title").Should().BeEmpty("one target is a given, so nothing asks which");
            cut.Find("#rb-build").GetAttribute("value").Should().Be(seed.OlderBuildId.ToString());
            cut.Markup.Should().Contain("This is the build you chose.");
            // The dialog's own safeguards travel with it.
            cut.FindAll(".check--ack").Should().ContainSingle();
        });
    }

    [Fact]
    public async Task With_several_release_pipelines_the_row_asks_which_one_first()
    {
        var seed = await SeedAsync();
        await SeedReleasePipelineAsync(seed, "CRONUS App → Production", seed.ProductionEnvId);
        await SeedReleasePipelineAsync(seed, "CRONUS App → UAT", seed.SandboxEnvId);
        var cut = RenderPage(seed);

        ActThen(cut,
            () => cut.Find(ReleaseButton(seed.NewerBuildId)).Click(),
            () => cut.Find("#pb-rel-title").TextContent.Should().StartWith($"Deploy build #{seed.NewerBuildId} from "));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".modal-layer .sub-row__name").Select(n => n.TextContent.Trim())
                .Should().Equal("CRONUS App → Production", "CRONUS App → UAT");
            cut.FindAll("#rb-title").Should().BeEmpty();
        });

        ActThen(cut,
            () => cut.Find("button[aria-label='Deploy through CRONUS App → UAT']").Click(),
            () => cut.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS Denmark — UAT"));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("#pb-rel-title").Should().BeEmpty();
            cut.Find("#rb-build").GetAttribute("value").Should().Be(seed.NewerBuildId.ToString());
            cut.FindAll(".check--ack").Should().BeEmpty("a sandbox asks for no acknowledgement");
        });

        // Releasing goes through the dialog's own path, and the page says where to follow it.
        // No Business Central connection here, so who is online can't be checked; the dialog
        // asks, and the second press goes ahead.
        ActThen(cut,
            () => cut.Find(".modal-layer .btn--primary").Click(),
            () => cut.Find(".modal-layer .btn--primary").TextContent.Should().Contain("Deploy anyway"));
        ActThen(cut,
            () => cut.Find(".modal-layer .btn--primary").Click(),
            () => cut.Find(".alert").TextContent.Should().Contain($"Build #{seed.NewerBuildId} is lined up to install into UAT."));
        cut.Find(".alert a").GetAttribute("href").Should().StartWith("/pipelines/deployments/");

        await using var ctx = _db.NewContext();
        (await ctx.OeProjectDeliveries.AsNoTracking().SingleAsync()).ProjectBuildId.Should().Be(seed.NewerBuildId);
    }

    [Fact]
    public async Task With_no_release_pipeline_the_row_offers_to_set_one_up_and_then_carries_on_to_the_release()
    {
        var seed = await SeedAsync();
        var cut = RenderPage(seed);

        ActThen(cut,
            () => cut.Find(ReleaseButton(seed.OlderBuildId)).Click(),
            () => cut.Markup.Should().Contain("CRONUS App isn't set up to install anywhere yet."));

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        cut.Find(".confirm-dialog__actions .btn--primary").Click();
        // Setting one up is its own page, with this build pipeline as the source.
        cut.WaitForAssertion(() => nav.ToBaseRelativePath(nav.Uri).Should().Be(
            $"pipelines/deployments/new?solution={seed.ProjectId}&buildPipeline={seed.PipelineId}&deploy={seed.OlderBuildId}" +
            $"&returnUrl=%2Fpipelines%2F{seed.PipelineId}"));

        var editor = _ctx.Render<ReleasePipelineEdit>();
        editor.WaitForAssertion(() =>
        {
            editor.Find("#rpe-build").GetAttribute("value").Should().Be(seed.PipelineId.ToString());
            editor.Markup.Should().Contain($"After you create it, you choose when build #{seed.OlderBuildId} installs.");
            editor.Find(".page-head__actions a.btn").GetAttribute("href").Should().Be($"/pipelines/{seed.PipelineId}");
        });
        editor.Find("#rpe-env").Change(seed.SandboxEnvId.ToString());
        editor.Find(".page-head .btn--primary").Click();

        // Saving carries on to deploying the build it was set up for.
        int releasePipelineId = 0;
        editor.WaitForAssertion(() =>
        {
            var match = System.Text.RegularExpressions.Regex.Match(nav.ToBaseRelativePath(nav.Uri), @"^pipelines/deployments/(\d+)\?deploy=(\d+)$");
            match.Success.Should().BeTrue(nav.Uri);
            match.Groups[2].Value.Should().Be(seed.OlderBuildId.ToString());
            releasePipelineId = int.Parse(match.Groups[1].Value);
        });

        var detail = _ctx.Render<ReleasePipelineDetail>(p => p.Add(x => x.Id, releasePipelineId));
        detail.WaitForAssertion(() =>
        {
            detail.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS Denmark — UAT");
            detail.Find("#rb-build").GetAttribute("value").Should().Be(seed.OlderBuildId.ToString());
        });
        nav.ToBaseRelativePath(nav.Uri).Should().Be($"pipelines/deployments/{releasePipelineId}",
            "a reload must not open Deploy a second time");

        await using var ctx = _db.NewContext();
        var created = await ctx.OeReleasePipelines.AsNoTracking().SingleAsync();
        created.BuildPipelineId.Should().Be(seed.PipelineId);
        created.Name.Should().Be("CRONUS App to UAT");
    }

    /// <summary>
    /// At phone width the head's actions wrap under the title rather than running off the
    /// right edge (#978). bunit cannot measure layout, so this pins what the layout hangs
    /// on: the page class app.css's wrap rules select, around the head's actions, with
    /// Build still the first of them.
    /// </summary>
    [Fact]
    public async Task The_head_actions_sit_in_the_wrapping_head_with_Build_first()
    {
        var seed = await SeedAsync();
        var cut = RenderPage(seed);

        cut.WaitForAssertion(() =>
        {
            var actions = cut.Find(".page.pb-page > .detail-head > .page-head__actions");
            actions.Children[0].TextContent.Trim().Should().Be("Build");
            actions.Children[0].ClassList.Should().Contain("btn--primary");
        });
    }

    [Fact]
    public async Task Build_is_disabled_and_says_so_while_a_build_is_running()
    {
        var seed = await SeedAsync();
        await using (var ctx = _db.NewContext())
        {
            var release = new OeRelease
            {
                OrganizationId = TestDb.DefaultOrgId, Label = "CRONUS", BcVersion = "",
                DedupKey = Guid.NewGuid().ToString(), Kind = "project", Status = "ingesting",
                ImportedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow,
            };
            ctx.OeReleases.Add(release);
            await ctx.SaveChangesAsync();
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = seed.ProjectId, PipelineId = seed.PipelineId,
                ReleaseId = release.Id, Status = ProjectBuildStatus.Building, StartedAt = DateTime.UtcNow,
            });
            await ctx.SaveChangesAsync();
        }

        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(c => c.PipelineId, seed.PipelineId));

        cut.WaitForAssertion(() =>
        {
            var build = cut.Find(".page.pb-page > .detail-head > .page-head__actions").Children[0];
            build.TextContent.Trim().Should().Be("Build running");
            build.HasAttribute("disabled").Should().BeTrue();
        });
    }

    [Fact]
    public async Task Someone_who_cannot_manage_the_solution_gets_no_release_action()
    {
        var seed = await SeedAsync(ProjectVisibility.ReadOnly);
        await SeedReleasePipelineAsync(seed, "CRONUS App → Production", seed.ProductionEnvId);
        _db.OrgContext.CurrentUserId = ColleagueUserId;

        var cut = RenderPage(seed);

        cut.WaitForAssertion(() =>
        {
            // They can still download - only the release is withheld.
            cut.FindAll(".data-table__actions a").Should().HaveCount(2);
            cut.FindAll(".data-table__actions button").Should().BeEmpty();
        });
    }

    // ── Nightly preview check (#994) ────────────────────────────────────────

    /// <summary>
    /// The pipeline has the check on, and the newer build becomes last night's check
    /// against the next major. The older build stays the pipeline's own.
    /// </summary>
    private async Task MakeNewerAPreviewCheckAsync(Seed seed)
    {
        await using var ctx = _db.NewContext();
        await ctx.OePipelines.Where(p => p.Id == seed.PipelineId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PreviewCheck, true));
        await ctx.OeProjectBuilds.Where(b => b.Id == seed.NewerBuildId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.BcTarget, ProjectBuildTarget.NextMajor)
                .SetProperty(b => b.Trigger, ProjectBuildTrigger.PreviewCheck)
                .SetProperty(b => b.BcVersion, "29.0")
                .SetProperty(b => b.BcArtifactVersion, "29.0.52914.0"));
        await ctx.OeProjectBuilds.Where(b => b.Id == seed.OlderBuildId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.BcVersion, "28.4")
                .SetProperty(b => b.BcArtifactVersion, "28.4.47110.0"));
    }

    [Fact]
    public async Task The_latest_build_stays_the_pipelines_own_and_the_check_result_sits_beside_it()
    {
        var seed = await SeedAsync();
        await MakeNewerAPreviewCheckAsync(seed);

        var cut = RenderPage(seed);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".detail-head .status-pill").Select(p => p.TextContent.Trim()).Should().NotContain("Preview build");
            cut.Find(".pb-topline").TextContent.Should().Contain("on BC 28.4.47110.0");
            var result = cut.Find(".pcs a.pcs__item");
            result.TextContent.Trim().Should().Be("Next major: Passed");
            result.GetAttribute("href").Should().Be($"/pipelines/{seed.PipelineId}?build={seed.NewerBuildId}");
        });
    }

    [Fact]
    public async Task Following_a_check_result_shows_that_build_with_a_Preview_build_pill()
    {
        var seed = await SeedAsync();
        await MakeNewerAPreviewCheckAsync(seed);

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(nav.GetUriWithQueryParameter("build", seed.NewerBuildId));
        var cut = _ctx.Render<PipelineBuilds>(p => p.Add(c => c.PipelineId, seed.PipelineId));

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".detail-head .status-pill").Select(p => p.TextContent.Trim()).Should().Contain("Preview build");
            cut.Find(".pb-topline").TextContent.Should().Contain("on BC 29.0.52914.0");
            cut.Markup.Should().Contain($"Next major preview build #{seed.NewerBuildId}");
            cut.Markup.Should().Contain("Back to the latest build");
        });
    }

    [Fact]
    public async Task A_check_builds_row_says_so_and_offers_no_deploy()
    {
        var seed = await SeedAsync();
        await SeedReleasePipelineAsync(seed, "CRONUS App → Production", seed.ProductionEnvId);
        await MakeNewerAPreviewCheckAsync(seed);

        var cut = RenderPage(seed);

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".data-table tbody tr");
            rows[0].TextContent.Should().Contain("Next major preview build").And.Contain("on BC 29.0.52914.0");
            rows[1].TextContent.Should().NotContain("preview").And.Contain("on BC 28.4.47110.0");
            cut.FindAll(ReleaseButton(seed.NewerBuildId)).Should().BeEmpty("a preview build cannot be deployed");
            cut.FindAll(ReleaseButton(seed.OlderBuildId)).Should().ContainSingle();
        });
    }

    [Fact]
    public async Task The_deploy_dialog_leaves_preview_builds_out_of_its_list()
    {
        var seed = await SeedAsync();
        await SeedReleasePipelineAsync(seed, "CRONUS App → Production", seed.ProductionEnvId);
        await MakeNewerAPreviewCheckAsync(seed);
        var cut = RenderPage(seed);

        ActThen(cut,
            () => cut.Find(ReleaseButton(seed.OlderBuildId)).Click(),
            () => cut.Find("#rb-title").TextContent.Should().Be("Deploy to CRONUS Denmark — Production"));

        cut.WaitForAssertion(() =>
            cut.FindAll("#rb-build option").Select(o => o.GetAttribute("value"))
                .Should().Equal(seed.OlderBuildId.ToString()));
    }

    [Fact]
    public async Task A_pipeline_without_the_check_shows_neither_the_pill_nor_the_check()
    {
        var seed = await SeedAsync();

        var cut = RenderPage(seed);

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".detail-head .status-pill").Select(p => p.TextContent.Trim()).Should().NotContain("Preview build");
            cut.Markup.Should().NotContain("Preview check");
        });
    }

    private sealed class UnusedTokenSource : IDeliveryTokenSource
    {
        public Task<BcDeliveryContext> AcquireDeliveryContextAsync(int projectId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
