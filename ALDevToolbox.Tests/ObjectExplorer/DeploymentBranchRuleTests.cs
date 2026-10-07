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
    [InlineData(null, "develop", false)]
    public void A_pipeline_allowing_the_default_branch_reads_it_as_the_solutions_default(string? allowed, string? built, bool expected) =>
        DeploymentBranchRule.Allows(allowed, built, buildDefaultBranches: None, solutionDefaultBranches: Main)
            .Should().Be(expected);

    [Theory]
    [InlineData("main", true)]
    [InlineData("develop", false)]
    public void A_default_branch_build_reads_as_the_branch_it_recorded(string allowed, bool expected) =>
        DeploymentBranchRule.Allows(allowed, null, buildDefaultBranches: Main, solutionDefaultBranches: None)
            .Should().Be(expected);

    [Fact]
    public void A_build_that_recorded_nothing_is_not_read_as_todays_default()
    {
        // The default may have been another branch when it was built.
        DeploymentBranchRule.Allows("main", null, buildDefaultBranches: None, solutionDefaultBranches: Main)
            .Should().BeFalse();
    }

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
        DeploymentBranchRule.SplitRecorded(" , main, ").Should().Equal("main");
        DeploymentBranchRule.SplitRecorded(null).Should().BeEmpty();
    }

    [Fact]
    public void A_refused_default_branch_build_names_the_branch_it_was_on()
    {
        DeploymentBranchRule.DescribeBuild(null, Main).Should().Be("the repositories' default branch (main)");
        DeploymentBranchRule.DescribeBuild(null, ["main", "master"]).Should().Be("the repositories' default branch");
        DeploymentBranchRule.DescribeBuild("release/25.0", Main).Should().Be("branch release/25.0");
    }
}
