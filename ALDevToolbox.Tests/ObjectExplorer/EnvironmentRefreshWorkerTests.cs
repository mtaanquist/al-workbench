using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The worker half of the environment refresh (<see cref="EnvironmentRefreshQueueTests"/>
/// covers the queue). The worker resolves <see cref="ProjectConnectionService"/> first
/// thing in each job, so a factory for it is where a run is observed: it records the
/// organisation in scope and throws rather than calling Business Central.
/// </summary>
public sealed class EnvironmentRefreshWorkerTests
{
    private const int ProjectId = 4711;
    private const int OrganizationId = TestDb.OtherOrgId;

    private readonly EnvironmentRefreshQueue _queue = new();
    private readonly MaintenanceModeState _maintenance = new();
    private readonly List<int?> _ranUnder = [];

    /// <summary>
    /// Losing the release would wedge the solution's refresh for good: the queue's
    /// dedupe gate would keep refusing it every night.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_fails_frees_the_solution_to_be_queued_again()
    {
        await RunOneJobAsync();

        _ranUnder.Should().ContainSingle("the job ran");
        _queue.IsInFlight(ProjectId).Should().BeFalse();
        _queue.TryEnqueue(Job()).Should().BeTrue("the next refresh is not refused");
    }

    /// <summary>
    /// Off-request work has no organisation of its own, so the job's must be in scope
    /// when the service is resolved, or the tenant filter hides the solution.
    /// </summary>
    [Fact]
    public async Task A_refresh_runs_under_the_organisation_that_queued_it()
    {
        await RunOneJobAsync();

        _ranUnder.Should().Equal(OrganizationId);
    }

    /// <summary>
    /// A refresh writes into tables a restore may not have finished loading (#1321),
    /// so while maintenance mode is on the job is dropped, and still let go of.
    /// </summary>
    [Fact]
    public async Task A_refresh_queued_during_maintenance_is_dropped_and_frees_the_solution()
    {
        _maintenance.HoldAfterFailedRestore("Restoring backup x failed.");

        await RunOneJobAsync();

        _ranUnder.Should().BeEmpty("nothing is refreshed while the site is closed");
        _queue.TryEnqueue(Job()).Should().BeTrue("the next night's refresh is not refused");
    }

    /// <summary>
    /// A job that throws (here: every job) still closes the run when the queue empties,
    /// so the next night's run starts its counts and throttle breaths from zero.
    /// </summary>
    [Fact]
    public async Task A_refresh_that_throws_still_ends_the_run()
    {
        var worker = await RunOneJobAsync();

        _ranUnder.Should().ContainSingle("the job ran");
        worker.SolutionsInRun.Should().Be(0);
    }

    private async Task<EnvironmentRefreshWorker> RunOneJobAsync()
    {
        var services = new ServiceCollection()
            .AddScoped<ProjectConnectionService>(_ =>
            {
                lock (_ranUnder) _ranUnder.Add(AmbientOrganizationScope.Current?.OrganizationId);
                throw new InvalidOperationException("No Business Central in this test.");
            })
            .BuildServiceProvider();
        await using var _ = services;
        var worker = new EnvironmentRefreshWorker(
            _queue, services, NullLogger<EnvironmentRefreshWorker>.Instance, new WorkerHeartbeatRegistry(), _maintenance);

        (await _queue.EnqueueAsync(Job(), TestContext.Current.CancellationToken)).Should().BeTrue();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Without this a job still in flight at the deadline fell through, and the
            // test read what the worker had done before it had done it.
            SpinWait.SpinUntil(() => !_queue.IsInFlight(ProjectId), TimeSpan.FromSeconds(10))
                .Should().BeTrue("the worker has to finish the job before the test reads what it did");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
        return worker;
    }

    private static EnvironmentRefreshJob Job() => new(ProjectId,
        AmbientOrganizationScope.OrganizationIdentity.ForOrganization(OrganizationId, isSystem: false));
}
