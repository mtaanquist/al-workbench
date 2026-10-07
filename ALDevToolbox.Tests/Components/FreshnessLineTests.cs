using ALDevToolbox.Components.Shared;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.Components;

/// <summary>
/// The freshness line on its own, for the headline the page tests do not reach: a branch
/// that moved on while a build of its newest commit is already queued or running says
/// so, rather than asking for a build nobody needs to start (#1128).
/// </summary>
public sealed class FreshnessLineTests : IDisposable
{
    private readonly BunitContext _ctx = new();

    public FreshnessLineTests() =>
        _ctx.Services.AddSingleton(new IconCatalog(NullLogger<IconCatalog>.Instance));

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void A_branch_whose_newest_commit_is_being_built_says_so_and_is_not_ahead()
    {
        var summary = new PipelineFreshnessSummary(
            PipelineFreshnessHeadline.Building, "main", LastBuildId: 118, MergedPullRequests: 0,
            CommitCount: null, Forced: false, PushedAt: null);

        var cut = _ctx.Render<FreshnessLine>(p => p.Add(c => c.Summary, summary));

        var line = cut.Find(".branch-state");
        line.TextContent.Trim().Should().Be("Building the latest commit on main");
        line.GetAttribute("data-freshness").Should().Be(nameof(PipelineFreshnessHeadline.Building));
        line.ClassList.Should().NotContain("branch-state--ahead", "there is nothing for a person to start");
        cut.Find("svg").ClassList.Should().Contain("lucide-hammer");
    }

    [Fact]
    public void Without_a_branch_name_it_names_the_branch_in_general()
    {
        var summary = new PipelineFreshnessSummary(
            PipelineFreshnessHeadline.Building, Branch: null, LastBuildId: 118, MergedPullRequests: 0,
            CommitCount: null, Forced: false, PushedAt: null);

        var cut = _ctx.Render<FreshnessLine>(p => p.Add(c => c.Summary, summary));

        cut.Find(".branch-state").TextContent.Trim().Should().Be("Building the latest commit on the branch");
    }
}
