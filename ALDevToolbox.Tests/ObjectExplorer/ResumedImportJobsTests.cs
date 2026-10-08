using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The jobs the startup reconcile hands back go onto the bounded import queue only
/// once the host has started, so a restart with more jobs than the queue holds no
/// longer blocks startup forever (#1107).
/// </summary>
public sealed class ResumedImportJobsTests
{
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: 1, UserId: null, IsSiteAdmin: false, IsSystemOrganization: true);

    private static List<ReleaseImportJob> Jobs(int count) =>
        Enumerable.Range(1, count)
            .Select(i => new ReleaseImportJob(i, Identity, new ReleaseImportSource.Url($"https://example.com/{i}.zip"), JobRowId: i))
            .ToList();

    [Fact]
    public async Task More_jobs_than_the_queue_holds_do_not_block_and_all_arrive_in_order_once_started()
    {
        var queue = new ReleaseImportQueue();
        var lifetime = new FakeLifetime();
        var jobs = Jobs(40);

        // Returns at once: nothing is written before the host has started.
        ResumedImportJobs.QueueWhenStarted(lifetime, queue, new ProjectBuildQueue(), jobs, NullLogger.Instance);
        queue.Reader.TryRead(out _).Should().BeFalse();

        lifetime.Start();

        var drained = new List<int>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (drained.Count < jobs.Count)
        {
            var job = await queue.Reader.ReadAsync(timeout.Token);
            drained.Add(job.ReleaseId);
        }
        drained.Should().Equal(jobs.Select(j => j.ReleaseId));
    }

    // Builds have their own queue, which never makes a writer wait, so they go back
    // on it before the host has started (#1137).
    [Fact]
    public void Builds_go_back_on_the_build_queue_at_once()
    {
        var queue = new ReleaseImportQueue();
        var builds = new ProjectBuildQueue();
        var jobs = Enumerable.Range(1, 40)
            .Select(i => new ReleaseImportJob(i, Identity, new ReleaseImportSource.ProjectBuild(i), JobRowId: i))
            .Append(new ReleaseImportJob(99, Identity, new ReleaseImportSource.Url("https://example.com/dvd.zip"), JobRowId: 99))
            .ToList();

        ResumedImportJobs.QueueWhenStarted(new FakeLifetime(), queue, builds, jobs, NullLogger.Instance);

        builds.WaitingCount.Should().Be(40);
        queue.Reader.TryRead(out _).Should().BeFalse("imports still wait for the host to start");
    }

    [Fact]
    public async Task Shutting_down_while_the_queue_is_full_stops_quietly()
    {
        var queue = new ReleaseImportQueue();
        using var stopping = new CancellationTokenSource();
        var run = ResumedImportJobs.EnqueueAllAsync(queue, Jobs(20), NullLogger.Instance, stopping.Token);

        // Sixteen fit; the seventeenth waits for a reader that never comes.
        run.IsCompleted.Should().BeFalse();
        await stopping.CancelAsync();

        await run.WaitAsync(TimeSpan.FromSeconds(10));
        run.IsCompletedSuccessfully.Should().BeTrue();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;

        public void Start() => _started.Cancel();
        public void StopApplication() => _stopping.Cancel();
    }
}
