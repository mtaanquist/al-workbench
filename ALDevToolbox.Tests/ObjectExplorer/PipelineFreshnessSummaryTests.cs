using ALDevToolbox.Services.ObjectExplorer.Projects;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// <see cref="PipelineFreshnessSummary.From"/> (#964): how a pipeline's repositories fold
/// into the one sentence the Builds list, the dashboard and the pipeline page say.
/// </summary>
public sealed class PipelineFreshnessSummaryTests
{
    private static readonly DateTime Pushed = new(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Any_repository_ahead_makes_the_pipeline_ahead_and_the_counts_add_up()
    {
        var summary = Fold(118,
            Repo(BuildFreshnessState.UpToDate),
            Repo(BuildFreshnessState.Ahead, commits: 2, merged: 1, pushedAt: Pushed.AddHours(-1)),
            Repo(BuildFreshnessState.Ahead, commits: 3, pushedAt: Pushed));

        summary.Headline.Should().Be(PipelineFreshnessHeadline.Ahead);
        summary.IsAhead.Should().BeTrue();
        summary.CommitCount.Should().Be(5);
        summary.MergedPullRequests.Should().Be(1);
        summary.PushedAt.Should().Be(Pushed, "the newest push is the one to show");
        summary.LastBuildId.Should().Be(118);
        summary.Branch.Should().Be("main");
    }

    [Fact]
    public void A_force_push_or_a_partial_commit_list_gives_no_count()
    {
        Fold(118, Repo(BuildFreshnessState.Ahead, commits: 2, forced: true)).Should()
            .Match<PipelineFreshnessSummary>(s => s.CommitCount == null && s.Forced);
        Fold(118, Repo(BuildFreshnessState.Ahead, commits: 10, complete: false)).CommitCount.Should().BeNull();
    }

    [Fact]
    public void Unknown_repositories_say_nothing_either_way()
    {
        Fold(118, Repo(BuildFreshnessState.UpToDate), Repo(BuildFreshnessState.Unknown))
            .Headline.Should().Be(PipelineFreshnessHeadline.UpToDate);
        Fold(118, Repo(BuildFreshnessState.Unknown))
            .Headline.Should().Be(PipelineFreshnessHeadline.None, "no push stored means no claim");
    }

    [Fact]
    public void A_deleted_branch_outranks_never_built_which_outranks_up_to_date()
    {
        Fold(118, Repo(BuildFreshnessState.UpToDate), Repo(BuildFreshnessState.BranchGone))
            .Headline.Should().Be(PipelineFreshnessHeadline.BranchGone);
        Fold(null, Repo(BuildFreshnessState.NeverBuilt))
            .Headline.Should().Be(PipelineFreshnessHeadline.NeverBuilt);
        var missing = Fold(118, Repo(BuildFreshnessState.UpToDate), Repo(BuildFreshnessState.NeverBuilt, name: "cronus-sales"));
        missing.Headline.Should().Be(PipelineFreshnessHeadline.NotInLastBuild, "a repository added since the build is not never-built");
        missing.RepositoryName.Should().Be("cronus-sales");
    }

    [Fact]
    public void A_branch_whose_new_commits_are_already_being_built_says_so_and_is_not_ahead()
    {
        var building = Fold(118, Repo(BuildFreshnessState.Ahead, commits: 1) with { BeingBuilt = true });
        building.Headline.Should().Be(PipelineFreshnessHeadline.Building);
        building.IsAhead.Should().BeFalse();

        // One repository being built does not cover another that is also ahead.
        Fold(118,
                Repo(BuildFreshnessState.Ahead, commits: 1) with { BeingBuilt = true },
                Repo(BuildFreshnessState.Ahead, commits: 2, name: "cronus-sales"))
            .Headline.Should().Be(PipelineFreshnessHeadline.Ahead);
    }

    [Fact]
    public void A_pipeline_with_no_repository_says_nothing()
    {
        Fold(null).Headline.Should().Be(PipelineFreshnessHeadline.None);
    }

    private static PipelineFreshnessSummary Fold(int? lastBuildId, params RepositoryFreshness[] repos) =>
        PipelineFreshnessSummary.From(new PipelineFreshness(1, "main", lastBuildId, null, repos));

    private static RepositoryFreshness Repo(
        BuildFreshnessState state, int commits = 0, int merged = 0, bool forced = false, bool complete = true,
        DateTime? pushedAt = null, string name = "cronus-base") =>
        new(1, name, "main", state, "abc", pushedAt ?? Pushed, "erik", forced, "def",
            Enumerable.Range(1, merged).Select(n => new MergedPullRequestSummary(n, "Title", "erik", Pushed, "")).ToList(),
            Enumerable.Range(1, commits).Select(n => new CommitSummary(n.ToString(), "Message")).ToList(),
            complete);
}
