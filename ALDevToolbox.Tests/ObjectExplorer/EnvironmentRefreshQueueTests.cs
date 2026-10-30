using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The in-memory dedupe / in-flight contract of <see cref="EnvironmentRefreshQueue"/>:
/// the nightly sweep and an on-demand refresh of the same project coalesce into one set
/// of round trips against the customer's tenant, and the in-flight flag reflects enqueue
/// to <see cref="EnvironmentRefreshQueue.Complete"/>.
/// </summary>
public sealed class EnvironmentRefreshQueueTests
{
    private static EnvironmentRefreshJob Job(int projectId) =>
        new(projectId, new AmbientOrganizationScope.OrganizationIdentity(
            TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false));

    [Fact]
    public async Task Enqueue_marks_the_project_in_flight()
    {
        var queue = new EnvironmentRefreshQueue();

        var enqueued = await queue.EnqueueAsync(Job(42));

        enqueued.Should().BeTrue();
        queue.IsInFlight(42).Should().BeTrue();
        queue.IsInFlight(99).Should().BeFalse("only the enqueued project is in flight");
    }

    [Fact]
    public async Task Enqueue_dedupes_an_already_in_flight_project()
    {
        var queue = new EnvironmentRefreshQueue();

        (await queue.EnqueueAsync(Job(42))).Should().BeTrue();
        (await queue.EnqueueAsync(Job(42))).Should().BeFalse("a refresh is already queued/running for this project");

        // The first job is still the only one on the channel — the duplicate was dropped.
        queue.Reader.TryRead(out var first).Should().BeTrue();
        first!.ProjectId.Should().Be(42);
        queue.Reader.TryRead(out _).Should().BeFalse("the duplicate enqueue wrote nothing");
    }

    [Fact]
    public async Task Complete_clears_the_flag_and_allows_re_enqueue()
    {
        var queue = new EnvironmentRefreshQueue();
        await queue.EnqueueAsync(Job(42));

        queue.Complete(42);

        queue.IsInFlight(42).Should().BeFalse();
        (await queue.EnqueueAsync(Job(42))).Should().BeTrue("once finished, the project can be refreshed again");
    }
    [Fact]
    public void A_project_Business_Central_asked_us_to_leave_alone_stays_deferred_until_the_time_passes()
    {
        var queue = new EnvironmentRefreshQueue();
        var now = new DateTime(2026, 10, 29, 3, 20, 0, DateTimeKind.Utc);

        queue.DeferUntil(42, now.AddMinutes(10));

        queue.DeferredUntil(42, now).Should().Be(now.AddMinutes(10));
        queue.DeferredUntil(99, now).Should().BeNull("only the throttled project waits");
        queue.DeferredUntil(42, now.AddMinutes(10)).Should().BeNull("the wait is over at the time it named");
    }

    [Fact]
    public void A_shorter_wait_never_cuts_a_longer_one_short()
    {
        var queue = new EnvironmentRefreshQueue();
        var now = new DateTime(2026, 10, 29, 3, 20, 0, DateTimeKind.Utc);

        queue.DeferUntil(42, now.AddMinutes(30));
        queue.DeferUntil(42, now.AddMinutes(5));

        queue.DeferredUntil(42, now.AddMinutes(10)).Should().Be(now.AddMinutes(30));
    }
}
