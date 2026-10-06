using System.Security.Claims;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.SingleTenant;
using ALDevToolbox.Tests.Builders;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// What the palette opens with before anything is typed (#887): the context
/// block of the page you are on, and the recents you may still open. See
/// <c>.design/command-palette.md</c>, "Where you are" and "Where you have been".
///
/// <para>Written in the spirit of <see cref="PaletteSourceVisibilityTestBase"/>:
/// most cases assert absence, so each one sits beside a case proving the same
/// caller does get the thing when they may. The two rules under test are the
/// issue's own: context entries respect exactly the gates of the tabs they
/// point at, and a recent the caller can no longer open is dropped - not shown
/// locked, not echoed back.</para>
/// </summary>
public sealed class PaletteContextServiceTests : IDisposable
{
    private const int MemberId = 9500;
    private const int OwnerId = 9501;
    private const int AdminId = 9502;
    private const int EditorId = 9503;

    private static readonly Guid TenantId = Guid.Parse("7c4f1c55-0d4b-4b0e-9a57-2f3c1a0f9e11");

    private readonly TestDb _db = new();

    private int _publicId;
    private int _readOnlyId;
    private int _privateId;
    private int _onPremId;
    private int _otherOrgId;
    private int _productionId;
    private int _sandboxId;
    private int _missingEnvId;
    private int _deletedEnvId;
    private int _privateEnvId;
    private int _pipelineId;
    private int _openReleaseId;
    private int _privateReleaseId;
    private int _recipeId;

    public PaletteContextServiceTests()
    {
        SeedAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _db.Dispose();

    // ── Where you are: a solution ───────────────────────────────────────

    [Fact]
    public async Task A_member_on_a_public_solution_gets_its_tabs_but_not_Access()
    {
        // Public means everyone in the organisation manages it - so Business
        // Central and Pipelines are theirs - but who may see it stays with its
        // owner and the admins, which is the page's own rule for the Access tab.
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_publicId}");

        result.Context.Should().NotBeNull();
        result.Context!.Label.Should().Be("CRONUS Coffee A/S");
        Titles(result.Context, "tab").Should().Equal(
            "Customer", "General", "Repositories", "Business Central", "Pipelines", "Symbols");
        result.Context.Items.Should().OnlyContain(i => !i.Href.Contains("tab=access"));
    }

    [Fact]
    public async Task The_owner_gets_the_Access_tab_too()
    {
        var result = await ResolveAsync(OwnerId, "User", at: $"solution:{_publicId}");

        Titles(result.Context!, "tab").Should().Equal(
            "Customer", "General", "Repositories", "Business Central", "Pipelines", "Symbols", "Access");
        result.Context!.Items.Single(i => i.Title == "Access").Href.Should().Be($"/solutions/{_publicId}/access");
    }

    [Fact]
    public async Task An_admin_gets_the_Access_tab_on_a_solution_they_do_not_own()
    {
        var result = await ResolveAsync(AdminId, "Admin", at: $"solution:{_publicId}");

        Titles(result.Context!, "tab").Should().Contain("Access");
    }

    [Fact]
    public async Task An_editor_who_does_not_own_the_solution_gets_no_Access_entry()
    {
        // Editor is a content-authoring role, not an organisation admin: the
        // Access tab is the owner's and the admins', and the palette may not be a
        // side door to a tab the page would not draw.
        var result = await ResolveAsync(EditorId, "Editor", at: $"solution:{_publicId}");

        Titles(result.Context!, "tab").Should().NotContain("Access");
        Titles(result.Context!, "tab").Should().Contain("Pipelines", "the Editor still manages a Public solution");
    }

    [Fact]
    public async Task A_read_only_solution_offers_a_non_manager_only_the_tabs_they_can_open()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_readOnlyId}");

        Titles(result.Context!, "tab").Should().Equal("Customer", "General", "Repositories");
    }

    [Fact]
    public async Task An_on_premises_solution_has_no_Business_Central_tab()
    {
        var result = await ResolveAsync(OwnerId, "User", at: $"solution:{_onPremId}");

        Titles(result.Context!, "tab").Should().Equal("Customer", "General", "Repositories", "Pipelines", "Symbols", "Access");
    }

    [Fact]
    public async Task A_solution_lists_its_live_environments_and_its_latest_build()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_publicId}");

        var environments = result.Context!.Items.Where(i => i.Kind == "environment").ToList();
        environments.Select(e => e.Href).Should().Equal(
            [$"/environments/{_productionId}", $"/environments/{_sandboxId}"],
            "one Business Central no longer reports, and one the customer deleted, are not places to go");
        environments[0].Subtitle.Should().NotContain("CRONUS Coffee", "the solution is the heading these rows sit under");

        var build = result.Context.Items.Should().ContainSingle(i => i.Kind == "build").Subject;
        build.Title.Should().Be("Latest build");
        build.Href.Should().Be($"/pipelines/{_pipelineId}");
        build.Subtitle.Should().StartWith("Nightly - Ready - ");
    }

    [Fact]
    public async Task The_latest_build_is_left_out_when_Pipelines_is_switched_off()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_publicId}", disabled: ToolKey.Pipelines);

        result.Context!.Items.Should().NotContain(i => i.Kind == "build");
    }

    [Fact]
    public async Task A_private_solution_the_caller_is_not_on_has_no_context_at_all()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_privateId}");

        result.Context.Should().BeNull("a page the caller cannot open has nothing to offer, not even its name");

        var owner = await ResolveAsync(OwnerId, "User", at: $"solution:{_privateId}");
        owner.Context!.Label.Should().Be("Contoso Holdings", "the same request from someone on it does answer");
    }

    [Fact]
    public async Task Another_organisations_solution_has_no_context()
    {
        var result = await ResolveAsync(AdminId, "Admin", at: $"solution:{_otherOrgId}");

        result.Context.Should().BeNull();
    }

    [Fact]
    public async Task No_context_when_Solutions_is_switched_off()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"solution:{_publicId}", disabled: ToolKey.Projects);

        result.Context.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("solution")]
    [InlineData("solution:")]
    [InlineData("solution:0")]
    [InlineData("solution:-1")]
    [InlineData("solution:1 ")]
    [InlineData("SOLUTION:1")]
    [InlineData("release:1")]
    [InlineData("solution:1234567890")]
    [InlineData("/solutions/1")]
    public void Only_the_exact_shape_parses_as_a_context(string at)
    {
        PaletteContextRef.TryParse(at, out _).Should().BeFalse();
    }

    // ── Where you are: an environment ───────────────────────────────────

    [Fact]
    public async Task An_environment_offers_its_tabs_the_way_out_to_Microsoft_and_its_solution()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"environment:{_productionId}");

        result.Context!.Label.Should().Be("Production");
        result.Context.Items.Select(i => (i.Kind, i.Title, i.Href)).Should().Equal(
            ("tab", "Overview", $"/environments/{_productionId}"),
            ("tab", "Apps", $"/environments/{_productionId}/apps"),
            ("tab", "Operations", $"/environments/{_productionId}/operations"),
            ("tab", "Sessions", $"/environments/{_productionId}/sessions"),
            ("tab", "Workbench history", $"/environments/{_productionId}/history"),
            ("external", "Open in Business Central", $"https://businesscentral.dynamics.com/{TenantId:D}/Production"),
            ("external", "Open the admin centre", $"https://businesscentral.dynamics.com/{TenantId:D}/admin"),
            ("solution", "CRONUS Coffee A/S", $"/solutions/{_publicId}"));
    }

    [Fact]
    public async Task The_links_to_Microsoft_are_left_out_when_the_tenant_is_not_known()
    {
        // The page only draws its two buttons when it can build the address; the
        // palette offers them under exactly the same condition.
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.FindAsync(_publicId);
            project!.BcTenantId = null;
            await ctx.SaveChangesAsync();
        }

        var result = await ResolveAsync(MemberId, "User", at: $"environment:{_productionId}");

        result.Context!.Items.Should().NotContain(i => i.Kind == "external");
    }

    [Fact]
    public async Task An_environment_of_a_private_solution_the_caller_is_not_on_has_no_context()
    {
        var result = await ResolveAsync(MemberId, "User", at: $"environment:{_privateEnvId}");

        result.Context.Should().BeNull();
    }

    // ── Where you have been ─────────────────────────────────────────────

    [Fact]
    public async Task Recents_come_back_in_the_order_asked_with_the_servers_own_titles()
    {
        var result = await ResolveAsync(MemberId, "User", recents:
        [
            $"/environments/{_productionId}",
            "/translator",
            $"/solutions/{_publicId}",
            $"/object-explorer/release/{_openReleaseId}",
            $"/cookbook/{_recipeId}",
        ]);

        result.Recents.Select(r => (r.Kind, r.Title, r.Href)).Should().Equal(
            ("environment", "Production", $"/environments/{_productionId}"),
            ("goto", "Translator", "/translator"),
            ("solution", "CRONUS Coffee A/S", $"/solutions/{_publicId}"),
            ("release", "Base Application 26.0", $"/object-explorer/release/{_openReleaseId}"),
            ("recipe", "Posting routine", $"/cookbook/{_recipeId}"));
    }

    [Fact]
    public async Task A_recent_to_a_private_solution_the_caller_is_not_on_is_dropped_with_everything_under_it()
    {
        var result = await ResolveAsync(MemberId, "User", recents:
        [
            $"/solutions/{_privateId}",
            $"/environments/{_privateEnvId}",
            $"/object-explorer/release/{_privateReleaseId}",
            $"/solutions/{_publicId}",
        ]);

        result.Recents.Select(r => r.Href).Should().Equal($"/solutions/{_publicId}");
        foreach (var item in result.Recents)
        {
            item.Title.Should().NotContainEquivalentOf("Contoso");
            (item.Subtitle ?? string.Empty).Should().NotContainEquivalentOf("Contoso");
        }
    }

    [Fact]
    public async Task A_solution_that_turns_private_drops_out_of_recents_on_the_next_check()
    {
        var before = await ResolveAsync(MemberId, "User", recents: [$"/solutions/{_publicId}"]);
        before.Recents.Should().ContainSingle();

        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.FindAsync(_publicId);
            project!.Visibility = ProjectVisibility.Private;
            await ctx.SaveChangesAsync();
        }

        var after = await ResolveAsync(MemberId, "User", recents: [$"/solutions/{_publicId}"]);
        after.Recents.Should().BeEmpty("a recent is re-checked every time it is offered, not trusted from storage");
    }

    [Fact]
    public async Task A_deleted_environment_keeps_its_recent_because_it_keeps_its_page()
    {
        // The environment page still opens for one the customer deleted - it is
        // where somebody goes to recover it - so the recent is still somewhere
        // to go. It is only left out of the solution's context block.
        var result = await ResolveAsync(MemberId, "User", recents: [$"/environments/{_deletedEnvId}"]);

        result.Recents.Select(r => r.Href).Should().Equal($"/environments/{_deletedEnvId}");
    }

    [Fact]
    public async Task Deleted_and_other_organisation_records_are_dropped()
    {
        await using (var ctx = _db.NewContext())
        {
            var project = await ctx.OeProjects.FindAsync(_readOnlyId);
            project!.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var result = await ResolveAsync(AdminId, "Admin", recents:
        [
            $"/solutions/{_readOnlyId}",
            $"/solutions/{_otherOrgId}",
            $"/environments/{_missingEnvId}",
            "/solutions/999999",
        ]);

        result.Recents.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://evil.example/solutions/1")]
    [InlineData("//evil.example")]
    [InlineData("/solutions/1?tab=access")]
    [InlineData("/solutions/1/")]
    [InlineData("/solutions/abc")]
    [InlineData("/solutions/01")]
    [InlineData("/solutions/1/apps")]
    [InlineData("/admin/templates/7")]
    [InlineData("/nowhere")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public async Task An_href_that_is_not_one_of_ours_is_never_echoed_back(string href)
    {
        // The id in the record links is swapped for the real one, so a case
        // that should be refused on shape is not refused merely for naming a
        // record that does not exist.
        var sent = href.Replace("/1", $"/{_publicId}", StringComparison.Ordinal);

        var result = await ResolveAsync(AdminId, "Admin", recents: [sent]);

        result.Recents.Should().BeEmpty();
    }

    [Fact]
    public async Task A_page_from_Go_to_the_callers_role_does_not_reach_is_dropped()
    {
        var member = await ResolveAsync(MemberId, "User", recents: ["/admin/audit", "/admin/templates", "/translator"]);
        member.Recents.Select(r => r.Href).Should().Equal("/translator");

        var admin = await ResolveAsync(AdminId, "Admin", recents: ["/admin/audit", "/admin/templates", "/translator"]);
        admin.Recents.Select(r => r.Href).Should().Equal("/admin/audit", "/admin/templates", "/translator");
    }

    [Fact]
    public async Task A_page_whose_tool_is_switched_off_is_dropped()
    {
        var result = await ResolveAsync(MemberId, "User", recents: ["/translator", $"/cookbook/{_recipeId}"],
            disabled: ToolKey.Translator);

        result.Recents.Select(r => r.Href).Should().Equal($"/cookbook/{_recipeId}");
    }

    [Fact]
    public async Task At_most_eight_distinct_recents_are_read()
    {
        var asked = Enumerable.Repeat("/translator", 3)
            .Concat(["/", "/templates", "/cookbook", "/object-explorer", "/diff", "/piper", "/solutions", "/environments", "/teams"])
            .ToList();

        var result = await ResolveAsync(MemberId, "User", recents: asked);

        result.Recents.Select(r => r.Href).Should().Equal(
            "/translator", "/", "/templates", "/cookbook", "/object-explorer", "/diff", "/piper", "/solutions");
    }

    [Fact]
    public async Task An_anonymous_caller_gets_nothing()
    {
        await using var ctx = _db.NewContext();
        var service = NewService(ctx);

        var result = await service.ResolveAsync(
            new ClaimsPrincipal(new ClaimsIdentity()), $"solution:{_publicId}", [$"/solutions/{_publicId}"],
            CancellationToken.None);

        result.Context.Should().BeNull();
        result.Recents.Should().BeEmpty();
    }

    // ── Plumbing ────────────────────────────────────────────────────────

    private static IEnumerable<string> Titles(PaletteContextBlock block, string kind) =>
        block.Items.Where(i => i.Kind == kind).Select(i => i.Title);

    private async Task<PaletteContextResult> ResolveAsync(
        int userId, string role, string? at = null, IReadOnlyList<string>? recents = null, ToolKey? disabled = null)
    {
        _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        _db.OrgContext.CurrentUserId = userId;
        _db.OrgContext.IsSiteAdmin = false;

        var claims = new List<Claim>
        {
            new(HttpOrganizationContext.UserIdClaim, userId.ToString()),
            new(HttpOrganizationContext.OrganizationIdClaim, TestDb.DefaultOrgId.ToString()),
            new(ClaimTypes.Role, role),
        };
        if (disabled is { } tool) claims.Add(new Claim(EndpointHelpers.DisabledToolsClaim, tool.ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));

        await using var ctx = _db.NewContext();
        return await NewService(ctx).ResolveAsync(principal, at, recents, CancellationToken.None);
    }

    private PaletteContextService NewService(ALDevToolbox.Data.AppDbContext ctx)
    {
        var access = new ProjectAccess(ctx, _db.OrgContext);
        return new PaletteContextService(
            ctx,
            access,
            _db.NewToolEnablement(ctx),
            TestDb.EverythingEnabled(),
            _db.OrgContext,
            new SingleTenantModeState(false),
            new UpgradeFleetService(ctx, _db.OrgContext, access, new EnvironmentRefreshQueue(),
                NullLogger<UpgradeFleetService>.Instance),
            NullLogger<PaletteContextService>.Instance);
    }

    private async Task SeedAsync()
    {
        await using (var ctx = _db.NewContext())
        {
            ctx.Users.AddRange(
                NewUser(MemberId, "member@cronus.test", UserRole.User),
                NewUser(OwnerId, "owner@cronus.test", UserRole.User),
                NewUser(AdminId, "admin@cronus.test", UserRole.Admin),
                NewUser(EditorId, "editor@cronus.test", UserRole.Editor));
            await ctx.SaveChangesAsync();
        }

        _publicId = await AddProjectAsync(TestDb.DefaultOrgId, "CRONUS Coffee A/S", ProjectVisibility.Public, tenant: TenantId);
        _readOnlyId = await AddProjectAsync(TestDb.DefaultOrgId, "CRONUS Tea", ProjectVisibility.ReadOnly);
        _privateId = await AddProjectAsync(TestDb.DefaultOrgId, "Contoso Holdings", ProjectVisibility.Private);
        _onPremId = await AddProjectAsync(TestDb.DefaultOrgId, "CRONUS Onsite", ProjectVisibility.Public,
            hosting: ProjectHostingType.CustomerHardware);
        _otherOrgId = await AddProjectAsync(TestDb.OtherOrgId, "Fabrikam Overseas", ProjectVisibility.Public);

        _productionId = await AddEnvironmentAsync(_publicId, "Production");
        _sandboxId = await AddEnvironmentAsync(_publicId, "Sandbox", type: "Sandbox");
        _missingEnvId = await AddEnvironmentAsync(_publicId, "Gone", missing: true);
        _deletedEnvId = await AddEnvironmentAsync(_publicId, "Deleted", status: BcEnvironmentStatus.SoftDeleted);
        _privateEnvId = await AddEnvironmentAsync(_privateId, "Production");

        await using (var ctx = _db.NewContext())
        {
            var pipeline = new OePipeline
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = _publicId, Name = "Nightly",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            ctx.OePipelines.Add(pipeline);
            await ctx.SaveChangesAsync();
            _pipelineId = pipeline.Id;

            ctx.OeProjectBuilds.AddRange(
                new OeProjectBuild
                {
                    OrganizationId = TestDb.DefaultOrgId, ProjectId = _publicId, PipelineId = pipeline.Id,
                    Status = ProjectBuildStatus.Failed, StartedAt = DateTime.UtcNow.AddDays(-2),
                },
                new OeProjectBuild
                {
                    OrganizationId = TestDb.DefaultOrgId, ProjectId = _publicId, PipelineId = pipeline.Id,
                    Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow.AddHours(-1),
                });

            var open = NewRelease("Base Application 26.0");
            var hidden = NewRelease("Contoso Holdings 1.0.0.0");
            ctx.OeReleases.AddRange(open, hidden);
            await ctx.SaveChangesAsync();
            _openReleaseId = open.Id;
            _privateReleaseId = hidden.Id;

            // What ties a release to a solution, and so to its visibility.
            ctx.OeProjectBuilds.Add(new OeProjectBuild
            {
                OrganizationId = TestDb.DefaultOrgId, ProjectId = _privateId, ReleaseId = hidden.Id,
                Status = ProjectBuildStatus.Ready, StartedAt = DateTime.UtcNow,
            });

            var recipe = RecipeBuilder.Default("Posting routine", organizationId: TestDb.DefaultOrgId)
                .WithFile("Recipe.al", "// recipe");
            ctx.Recipes.Add(recipe);
            await ctx.SaveChangesAsync();
            _recipeId = recipe.Id;
        }
    }

    private static OeRelease NewRelease(string label) => new()
    {
        OrganizationId = TestDb.DefaultOrgId,
        Label = label,
        BcVersion = "26.0.1.1",
        Kind = "project",
        Status = "ready",
        ImportedAt = DateTime.UtcNow,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };

    private async Task<int> AddProjectAsync(
        int organizationId, string name, ProjectVisibility visibility,
        Guid? tenant = null, ProjectHostingType? hosting = null)
    {
        var previous = _db.OrgContext.CurrentOrganizationId;
        _db.OrgContext.CurrentOrganizationId = organizationId;
        try
        {
            await using var ctx = _db.NewContext();
            var project = new OeProject
            {
                OrganizationId = organizationId,
                Name = name,
                DefaultArtifactCountry = "dk",
                CreatedByUserId = organizationId == TestDb.DefaultOrgId ? OwnerId : null,
                Visibility = visibility,
                BcTenantId = tenant,
                HostingType = hosting,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            ctx.OeProjects.Add(project);
            await ctx.SaveChangesAsync();
            return project.Id;
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = previous;
        }
    }

    private async Task<int> AddEnvironmentAsync(
        int projectId, string name, string type = "Production",
        string? status = BcEnvironmentStatus.Active, bool missing = false)
    {
        await using var ctx = _db.NewContext();
        var environment = new OeProjectEnvironment
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = name,
            Type = type,
            Status = status,
            Version = "28.2.41125.0",
            FetchedAt = DateTime.UtcNow,
            MissingSince = missing ? DateTime.UtcNow : null,
            SoftDeletedOn = BcEnvironmentStatus.IsSoftDeleted(status) ? DateTime.UtcNow : null,
        };
        ctx.OeProjectEnvironments.Add(environment);
        await ctx.SaveChangesAsync();
        return environment.Id;
    }

    private static User NewUser(int id, string email, UserRole role) => new()
    {
        Id = id,
        OrganizationId = TestDb.DefaultOrgId,
        Email = email,
        DisplayName = email,
        PasswordHash = "x",
        Role = role,
        Status = UserStatus.Active,
        CreatedAt = DateTime.UtcNow,
    };
}
