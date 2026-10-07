using ALDevToolbox.Services.ObjectExplorer.Delivery;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// "The default branch" against a named branch in the deployment branch rule (#1129):
/// equal when the default branch is that name, and compared as written when nothing
/// says which branch the default is.
/// </summary>
public sealed class DeploymentBranchRuleTests
{
    private static readonly string[] Main = ["main"];
    private static readonly string[] None = [];

    [Theory]
    [InlineData(null, "main", true)]
    [InlineData("main", null, true)]
    [InlineData(null, "develop", false)]
    [InlineData("develop", null, false)]
    public void The_default_branch_is_the_branch_it_names(string? allowed, string? built, bool expected) =>
        DeploymentBranchRule.Allows(allowed, built, buildDefaultBranches: None, solutionDefaultBranches: Main)
            .Should().Be(expected);

    [Fact]
    public void What_the_build_found_wins_over_what_github_last_said()
    {
        DeploymentBranchRule.Allows("master", null, buildDefaultBranches: ["master"], solutionDefaultBranches: Main)
            .Should().BeTrue();
    }

    [Fact]
    public void Repositories_that_disagree_on_their_default_branch_have_no_single_one()
    {
        DeploymentBranchRule.Allows("main", null, buildDefaultBranches: ["main", "master"], solutionDefaultBranches: None)
            .Should().BeFalse();
    }

    [Fact]
    public void With_nothing_known_the_names_are_compared_as_written()
    {
        DeploymentBranchRule.Allows(null, "main", None, None).Should().BeFalse();
        DeploymentBranchRule.Allows(null, null, None, None).Should().BeTrue();
    }

    [Fact]
    public void The_recorded_default_branches_are_split_on_commas()
    {
        DeploymentBranchRule.SplitRecorded("main, master").Should().Equal("main", "master");
        DeploymentBranchRule.SplitRecorded(null).Should().BeEmpty();
    }
}
