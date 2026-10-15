using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The discovery worker's half of the #1263 re-run. <see cref="ProjectDiscoveryQueue"/>
/// keeps a request that arrives while its solution's discovery is <em>running</em>, but
/// it only knows the job is running because the worker marks it so as it picks the job
/// up. <c>WorkerBaseTests</c> covers the queue by calling <c>Start</c> by hand; this
/// drives the real worker, so dropping its <c>OnJobStarting</c> override (or the base
/// class no longer calling it) fails here (#1348).
/// </summary>
public sealed class ProjectDiscoveryWorkerTests
{
    private const int ProjectId = 4711;

    [Fact]
    public async Task A_request_made_while_a_discovery_runs_runs_again_once_it_finishes()
    {
        var queue = new ProjectDiscoveryQueue();
        var runs = 0;
        using var firstRunStarted = new SemaphoreSlim(0);
        using var releaseFirstRun = new SemaphoreSlim(0);
        using var secondRunStarted = new SemaphoreSlim(0);

        // The worker resolves ProjectBuildService first thing in each job, so a factory
        // for it is where a run can be observed and held. It throws rather than doing a
        // real discovery: the worker logs that and still finishes the job, which is all
        // this test is about.
        var services = new ServiceCollection()
            .AddScoped<ProjectBuildService>(_ =>
            {
                if (Interlocked.Increment(ref runs) == 1)
                {
                    firstRunStarted.Release();
                    releaseFirstRun.Wait(TimeSpan.FromSeconds(10));
                }
                else
                {
                    secondRunStarted.Release();
                }
                throw new InvalidOperationException("No discovery in this test.");
            })
            .BuildServiceProvider();
        await using var _ = services;

        var worker = new ProjectDiscoveryWorker(
            queue, services, NullLogger<ProjectDiscoveryWorker>.Instance, new WorkerHeartbeatRegistry());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        (await queue.EnqueueAsync(Job())).Should().BeTrue();
        await Task.Run(() => worker.StartAsync(cts.Token), cts.Token);
        try
        {
            (await firstRunStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue("the worker picked the job up");

            // A repository added to the solution while the clone is underway.
            (await queue.EnqueueAsync(Job())).Should().BeFalse("the request is held for after the running discovery");
            queue.TryEnqueue(Job()).Should().BeFalse("a second request collapses into the same re-run");

            releaseFirstRun.Release();
            (await secondRunStarted.WaitAsync(TimeSpan.FromSeconds(10))).Should()
                .BeTrue("the request made during the run is not lost");

            while (queue.IsInFlight(ProjectId) && !cts.IsCancellationRequested) await Task.Delay(10, cts.Token);
            queue.IsInFlight(ProjectId).Should().BeFalse("nothing arrived during the re-run, so the gate reopens");
            Volatile.Read(ref runs).Should().Be(2, "the held requests ran once, not twice");
        }
        finally
        {
            releaseFirstRun.Release();
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static ProjectDiscoveryJob Job() => new(ProjectId,
        new AmbientOrganizationScope.OrganizationIdentity(TestDb.DefaultOrgId, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false));
}
