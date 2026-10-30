using ALDevToolbox.Components.Pages.ObjectExplorer;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Explore;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// A symbols package a pipeline build pulled from the feeds with no source in it is
/// not a card on the Object Explorer releases page; it stays in the database for
/// reference resolution (#1092). A release an admin imported without source is still
/// listed, and so is a feed package that did carry source.
///
/// <para>Named user: a BC consultant opening the Third-party tab to look through a
/// vendor's code, or looking for a customer's own code by the customer's name.</para>
/// </summary>
public sealed class ReleasesBrowserViewTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly BunitContext _ctx = new();

    public ReleasesBrowserViewTests()
    {
        var auth = _ctx.AddAuthorization();
        auth.SetAuthorized("tester@example.com");

        _ctx.Services.AddSingleton<IOrganizationContext>(_db.OrgContext);
        _ctx.Services.AddDisplayTimeZone(_db);
        _ctx.Services.AddDbContext<ALDevToolbox.Data.AppDbContext>(opts =>
            opts.UseNpgsql(_db.ConnectionString).AddInterceptors(_db.CommandTracker));
        _ctx.Services.AddScoped<ProjectAccess>();
        _ctx.Services.AddScoped<ReferenceQueryService>();
        _ctx.Services.AddScoped<ObjectExplorerService>();
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
    public async Task An_empty_symbol_package_from_the_feeds_is_not_listed_or_counted()
    {
        await SeedAsync("Continia Banking - Import 28.6.0.395730 (symbols)", files: 0, dedupKey: "symbols:aaaa:28.6.0.395730");
        await SeedAsync("Continia Banking - PSP 28.6.0.395730 (symbols)", files: 4, dedupKey: "symbols:bbbb:28.6.0.395730");
        await SeedAsync("CRONUS Extension 1.0.0.0", files: 0, dedupKey: null);

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());
        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Third-party")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("CRONUS Extension 1.0.0.0", "an admin imported it, source or not");
            cut.Markup.Should().Contain("Continia Banking - PSP", "this package carried source");
            cut.Markup.Should().NotContain("Continia Banking - Import");
            cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Third-party"))
                .QuerySelector(".pill-tab__count")!.TextContent.Trim().Should().Be("2");
        });
    }

    [Fact]
    public async Task Solution_builds_have_their_own_tab_between_Microsoft_and_Third_party()
    {
        await SeedAsync("Continia Banking 28.6.0.0", files: 4, dedupKey: null);
        await SeedPipelineBuildAsync("CRONUS Denmark", "main");

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());

        cut.FindAll(".pill-tab").Select(t => t.QuerySelector("span")!.TextContent)
            .Should().StartWith(new[] { "Microsoft", "Solutions", "Third-party" });

        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Third-party")).Click();
        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Continia Banking");
            cut.Markup.Should().NotContain("CRONUS Denmark", "a solution's build is not a third-party app");
        });

        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Solutions")).Click();
        cut.WaitForAssertion(() =>
        {
            var row = cut.Find("table.oe-solutions tbody tr");
            row.TextContent.Should().Contain("CRONUS Denmark").And.Contain("main");
            cut.Markup.Should().NotContain("Continia Banking");
        });
    }

    [Fact]
    public async Task Search_looks_across_every_category_at_once()
    {
        await SeedAsync("Denmark Toolkit 1.0.0.0", files: 4, dedupKey: null);
        await SeedPipelineBuildAsync("CRONUS Denmark", "main");
        await SeedAsync("Continia Banking 28.6.0.0", files: 4, dedupKey: null);

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());

        // The page opens on Microsoft, which holds neither match.
        cut.Find("input[type=search]").Input("denmark");

        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".oe-group__head .section-label").Select(h => h.TextContent)
                .Should().Equal("Solutions", "Third-party");
            cut.Markup.Should().Contain("Denmark Toolkit").And.Contain("CRONUS Denmark");
            cut.Markup.Should().NotContain("Continia Banking");
            cut.FindAll(".pill-tab.is-active").Should().BeEmpty("the results are not one tab's");
            cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Solutions"))
                .QuerySelector(".pill-tab__count")!.TextContent.Trim().Should().Be("1");
        });
    }

    [Fact]
    public async Task Clicking_a_tab_while_searching_narrows_the_search_to_it()
    {
        await SeedAsync("Denmark Toolkit 1.0.0.0", files: 4, dedupKey: null);
        await SeedPipelineBuildAsync("CRONUS Denmark", "main");

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());
        cut.Find("input[type=search]").Input("denmark");
        cut.WaitForAssertion(() => cut.FindAll(".oe-group__head").Should().HaveCount(2));

        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Solutions")).Click();

        cut.WaitForAssertion(() =>
        {
            cut.Find("input[type=search]").GetAttribute("value").Should().Be("denmark", "the query the count came from is kept");
            cut.Find(".pill-tab.is-active").TextContent.Should().Contain("Solutions");
            cut.Markup.Should().Contain("CRONUS Denmark").And.NotContain("Denmark Toolkit");
        });

        // Typing again looks across every tab.
        cut.Find("input[type=search]").Input("denmar");
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".pill-tab.is-active").Should().BeEmpty();
            cut.Markup.Should().Contain("Denmark Toolkit").And.Contain("CRONUS Denmark");
        });
    }

    [Fact]
    public async Task A_solution_is_named_once_however_many_pipelines_it_has()
    {
        await SeedPipelineBuildAsync("CRONUS Denmark", "staging", "main");

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());
        cut.FindAll(".pill-tab").First(t => t.TextContent.Contains("Solutions")).Click();

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("table.oe-solutions tbody tr");
            rows.Should().HaveCount(2);
            rows[0].QuerySelector("a")!.TextContent.Should().Be("main");
            rows[0].QuerySelector("strong")!.TextContent.Should().Be("CRONUS Denmark");
            rows[1].QuerySelector("a")!.TextContent.Should().Be("staging");
            rows[1].QuerySelector("strong").Should().BeNull("the name is shown once per solution");
        });
    }

    [Fact]
    public async Task The_Solutions_tab_is_hidden_while_there_are_no_builds()
    {
        await SeedAsync("Continia Banking 28.6.0.0", files: 4, dedupKey: null);

        var cut = _ctx.Render<ReleasesBrowserView>();
        cut.WaitForAssertion(() => cut.FindAll(".pill-tab").Should().NotBeEmpty());

        cut.FindAll(".pill-tab").Select(t => t.QuerySelector("span")!.TextContent)
            .Should().Equal("Microsoft", "Third-party");
    }

    private async Task SeedPipelineBuildAsync(string solution, params string[] pipelineNames)
    {
        await using var db = _db.NewContext();
        var project = new OeProject
        {
            OrganizationId = TestDb.DefaultOrgId,
            Name = solution,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.OeProjects.Add(project);
        await db.SaveChangesAsync();
        foreach (var pipelineName in pipelineNames)
        {
            await SeedPipelineAsync(db, project.Id, solution, pipelineName);
        }
    }

    private static async Task SeedPipelineAsync(ALDevToolbox.Data.AppDbContext db, int projectId, string solution, string pipelineName)
    {
        var pipeline = new OePipeline
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            Name = pipelineName,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var release = new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = $"{solution} on BC 28.1",
            Kind = "project",
            Status = "ready",
            BcVersion = "28.1",
            SourceFileCount = 12,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.OePipelines.Add(pipeline);
        db.OeReleases.Add(release);
        await db.SaveChangesAsync();
        db.OeProjectBuilds.Add(new OeProjectBuild
        {
            OrganizationId = TestDb.DefaultOrgId,
            ProjectId = projectId,
            PipelineId = pipeline.Id,
            ReleaseId = release.Id,
            Status = ProjectBuildStatus.Ready,
            StartedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAsync(string label, int files, string? dedupKey)
    {
        await using var db = _db.NewContext();
        db.OeReleases.Add(new OeRelease
        {
            OrganizationId = TestDb.DefaultOrgId,
            Label = label,
            Kind = "third_party",
            Status = "ready",
            Publisher = "CRONUS",
            SourceFileCount = files,
            DedupKey = dedupKey,
            ImportedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
