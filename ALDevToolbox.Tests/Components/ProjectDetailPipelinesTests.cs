using ALDevToolbox.Components.Pages.Pipelines;
using ALDevToolbox.Components.Pages.Projects;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// A solution's Pipelines tab and the build runs table (#1551). The named user is a
/// consultant opening a solution to start a build: a disabled pipeline must say so
/// before they open it, and a rerun must not show its last attempt's times as its own.
/// </summary>
public sealed class ProjectDetailPipelinesTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public ProjectDetailPipelinesTests()
    {
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));
    }

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void A_disabled_pipeline_says_so_on_the_solutions_pipelines_tab()
    {
        var cut = _ctx.Render<ProjectDetailPipelines>(p => p
            .Add(c => c.Id, 1)
            .Add(c => c.Pipelines, new[]
            {
                new OePipeline { Id = 10, Name = "main", DisabledAt = DateTime.UtcNow },
                new OePipeline { Id = 11, Name = "release/26" },
            }));

        var rows = cut.FindAll(".sub-row");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("Disabled");
        rows[1].TextContent.Should().NotContain("Disabled");
    }

    private static readonly DateTime LastFinish = new(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);

    private static BuildRow Row(string status, DateTime? buildingStartedAt) => new(
        812, 1, status, null, "main", new DateTime(2026, 10, 1, 9, 50, 0, DateTimeKind.Utc), LastFinish,
        null, "Anna", 0, BuildingStartedAt: buildingStartedAt);

    [Fact]
    public void A_rerun_that_is_building_shows_no_duration_and_is_aged_from_its_own_start()
    {
        var rerunStart = LastFinish.AddMinutes(31);
        var running = Row(ProjectBuildStatus.Building, rerunStart);

        PipelineBuilds.Duration(running).Should().BeEmpty("the finish time is the last attempt's, not this one's");
        PipelineBuilds.ShownTime(running).Should().Be(rerunStart);
    }

    [Fact]
    public void A_finished_build_shows_its_duration_and_is_aged_from_its_finish()
    {
        var done = Row(ProjectBuildStatus.Failed, LastFinish.AddMinutes(-2));

        PipelineBuilds.Duration(done).Should().Be("2m 00s");
        PipelineBuilds.ShownTime(done).Should().Be(LastFinish);
    }
}
