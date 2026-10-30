using System.Security.Claims;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Palette;
using ALDevToolbox.Services.Palette.Sources;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Tests.Palette;

/// <summary>
/// The palette's Release pipelines source (#885): the targets on
/// <c>/releases</c>, found by their name, their Solution and their environment.
/// A release pipeline inherits its Solution's visibility, so the harness runs
/// over a world where every Solution has one.
/// </summary>
public sealed class ReleasePipelinePaletteSourceTests : PaletteSourceVisibilityTestBase
{
    protected override IPaletteSource CreateSource(PaletteSourceUnderTest context) =>
        new ReleasePipelinePaletteSource(context.Db, context.Access, Db.NewToolEnablement(context.Db));

    /// <summary>One environment called Production per Solution, and a release pipeline aimed at it.</summary>
    protected override async Task SeedRowAsync(AppDbContext ctx, PaletteSourceSeed seed)
    {
        await AddReleasePipelineAsync(ctx, seed.OrganizationId, seed.ProjectId, "Ship to production", "Production");
    }

    protected override IEnumerable<(string Description, ClaimsPrincipal Principal)> PrincipalsThatGetNothing()
    {
        foreach (var entry in base.PrincipalsThatGetNothing()) yield return entry;
        yield return ("a member whose organisation has switched Releases off",
            RecipePaletteSourceTests.MemberPrincipal(ToolKey.Releases));
    }

    [Fact]
    public async Task A_release_pipeline_is_found_by_its_own_name()
    {
        await SeedWorldAsync();

        var results = await SearchAsync("ship");

        var row = results.Should().ContainSingle().Subject;
        row.Kind.Should().Be("release-pipeline");
        row.Title.Should().Be("Ship to production");
        row.Subtitle.Should().Be($"{VisibleName} - Production");
        row.Href.Should().MatchRegex(@"^/pipelines/deployments/\d+$");
    }

    [Fact]
    public async Task A_release_pipeline_is_found_by_its_solution_and_environment_together()
    {
        await SeedWorldAsync();
        await AddAsync("Weekly drop", "Sandbox-UAT");

        var results = await SearchAsync("cronus uat");

        results.Should().ContainSingle().Which.Title.Should().Be("Weekly drop",
            "the environment's name is in the subtitle, and every term has to match somewhere");
    }

    [Fact]
    public async Task A_release_pipeline_is_found_by_its_solutions_short_name()
    {
        await SeedWorldAsync();

        var results = await SearchAsync(VisibleShortName);

        var row = results.Should().ContainSingle().Subject;
        row.Title.Should().Be("Ship to production");
        row.ShortName.Should().BeNull("an exact short name lifts the Solution itself, never its release pipeline");
    }

    [Fact]
    public async Task A_pipeline_aimed_at_an_environment_that_is_gone_says_so()
    {
        await SeedWorldAsync();
        await AddAsync("Old target", "Retired", missing: true);

        var results = await SearchAsync("old target");

        results.Should().ContainSingle().Which.Subtitle.Should().Be($"{VisibleName} - Retired - no longer present");
    }

    [Fact]
    public async Task A_pipeline_aimed_at_a_failed_environment_says_so()
    {
        await SeedWorldAsync();
        await AddAsync("Broken target", "Staging", status: "UpgradingFailed");

        var results = await SearchAsync("broken target");

        results.Should().ContainSingle().Which.Subtitle.Should()
            .Be($"{VisibleName} - Staging - failed in Business Central");
    }

    [Fact]
    public async Task A_deleted_release_pipeline_is_left_out()
    {
        await SeedWorldAsync();
        await AddAsync("Removed target", "Test", deletedAt: DateTime.UtcNow);

        var results = await SearchAsync("removed target");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task An_archived_release_pipeline_is_left_out()
    {
        await SeedWorldAsync();
        await AddAsync("Retired target", "Test", archivedAt: DateTime.UtcNow);

        var results = await SearchAsync("retired target");

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task A_deleted_solutions_release_pipeline_is_left_out()
    {
        await SeedWorldAsync();
        await using (var ctx = Db.NewContext())
        {
            var project = await ctx.OeProjects.SingleAsync(p => p.Id == VisibleProjectId);
            project.DeletedAt = DateTime.UtcNow;
            await ctx.SaveChangesAsync();
        }

        var results = await SearchAsync("ship");

        results.Should().BeEmpty("a deleted Solution takes its release pipelines with it");
    }

    [Fact]
    public void The_link_points_at_a_page_that_exists() =>
        RecipePaletteSourceTests.RouteTemplates().Should().Contain("/pipelines/deployments/{Id:int}",
            "the palette's link is a dead end unless a page claims that route");

    [Fact]
    public async Task An_ordinary_member_passes_the_gate()
    {
        await using var ctx = Db.NewContext();
        var source = new ReleasePipelinePaletteSource(
            ctx, new ProjectAccess(ctx, Db.OrgContext), Db.NewToolEnablement(ctx));

        (await source.IsAvailableAsync(CallerPrincipal(), CancellationToken.None)).Should().BeTrue();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    private async Task AddAsync(
        string name, string environmentName, bool missing = false,
        string status = BcEnvironmentStatus.Active, DateTime? deletedAt = null, DateTime? archivedAt = null)
    {
        await using var ctx = Db.NewContext();
        await AddReleasePipelineAsync(
            ctx, TestDb.DefaultOrgId, VisibleProjectId, name, environmentName, missing, status, deletedAt, archivedAt);
    }

    private static async Task AddReleasePipelineAsync(
        AppDbContext ctx, int organizationId, int projectId, string name, string environmentName,
        bool missing = false, string status = BcEnvironmentStatus.Active, DateTime? deletedAt = null, DateTime? archivedAt = null)
    {
        var now = DateTime.UtcNow;
        var environment = new OeProjectEnvironment
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = environmentName,
            Type = "Production",
            Status = status,
            Version = "28.2.41125.0",
            FetchedAt = now,
            MissingSince = missing ? now : null,
        };
        var buildPipeline = new OePipeline
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = $"Build for {name}",
            CreatedAt = now,
            UpdatedAt = now,
        };
        ctx.OeProjectEnvironments.Add(environment);
        ctx.OePipelines.Add(buildPipeline);
        await ctx.SaveChangesAsync();

        ctx.OeReleasePipelines.Add(new OeReleasePipeline
        {
            OrganizationId = organizationId,
            ProjectId = projectId,
            Name = name,
            BuildPipelineId = buildPipeline.Id,
            ProjectEnvironmentId = environment.Id,
            DeletedAt = deletedAt,
            DisabledAt = archivedAt,
            ArchivedAt = archivedAt,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await ctx.SaveChangesAsync();
    }
}
