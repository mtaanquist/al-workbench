using ALDevToolbox.Services;
using ALDevToolbox.Services.Migration;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.Migration;

/// <summary>
/// The in-memory record of Migrate repositories previews (#1699): how many may
/// run, how long a finished one stays, and that a job ending without an outcome
/// is not left showing as running.
/// </summary>
public sealed class RepositoryMigrationQueueTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public void At_most_the_capacity_runs_across_the_server()
    {
        var queue = new RepositoryMigrationQueue(_clock);
        for (var user = 1; user <= RepositoryMigrationQueue.Capacity; user++)
        {
            queue.TryStart(Job(user), "cronus/Sales", "https://dev.azure.com/cronus/Sales", out _).Should().NotBeNull();
        }

        queue.TryStart(Job(99), "cronus/Sales", "https://dev.azure.com/cronus/Sales", out var refusal).Should().BeNull();
        refusal.Should().Contain("busy");
    }

    [Fact]
    public void A_finished_preview_is_kept_for_two_hours_and_a_running_one_until_it_ends()
    {
        var queue = new RepositoryMigrationQueue(_clock);
        var finished = Job(1);
        var running = Job(2);
        queue.TryStart(finished, "p", "u", out _);
        queue.TryStart(running, "p", "u", out _);
        queue.MarkDone(finished.Id, new MigrationSourceAnalysis([], []));

        _clock.Advance(RepositoryMigrationQueue.RetainFinishedFor + TimeSpan.FromMinutes(1));
        queue.TryStart(Job(3), "p", "u", out _); // admission evicts

        queue.Get(finished.Id, TestDb.DefaultOrgId, 1).Should().BeNull();
        queue.Get(running.Id, TestDb.DefaultOrgId, 2).Should().NotBeNull();
    }

    [Fact]
    public void Fail_if_active_fails_an_unfinished_preview_and_leaves_a_finished_one()
    {
        var queue = new RepositoryMigrationQueue(_clock);
        var stuck = Job(1);
        var done = Job(2);
        queue.TryStart(stuck, "p", "u", out _);
        queue.TryStart(done, "p", "u", out _);
        queue.MarkRunning(stuck.Id);
        queue.MarkDone(done.Id, new MigrationSourceAnalysis([], []));

        queue.FailIfActive(stuck.Id, "stopped");
        queue.FailIfActive(done.Id, "stopped");

        queue.Get(stuck.Id, TestDb.DefaultOrgId, 1)!.Should().BeEquivalentTo(new { Status = MigrationPreviewStatus.Failed, Error = "stopped" });
        queue.Get(done.Id, TestDb.DefaultOrgId, 2)!.Status.Should().Be(MigrationPreviewStatus.Done);
    }

    private static MigrationPreviewJob Job(int userId) => new(
        Guid.NewGuid(),
        new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, userId, false, false),
        new AzureDevOpsProject("cronus", "Sales"),
        [new AzureDevOpsRepository("r1", "cronus-base", "https://dev.azure.com/cronus/Sales/_git/cronus-base",
            "https://dev.azure.com/cronus/Sales/_git/cronus-base", "main", 1, false)]);
}
