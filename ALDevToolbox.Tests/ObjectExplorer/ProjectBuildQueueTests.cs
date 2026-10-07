using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services;
using ALDevToolbox.Services.ObjectExplorer.Import;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.ObjectExplorer;

/// <summary>
/// The order builds leave <see cref="ProjectBuildQueue"/> in when several workers drain
/// it (#1137): people first, then pushes, then preview checks; one build at a time per
/// pipeline and target, in the order they were queued.
/// </summary>
public sealed class ProjectBuildQueueTests
{
    private static readonly AmbientOrganizationScope.OrganizationIdentity Identity =
        new(OrganizationId: 1, UserId: null, IsSiteAdmin: false, IsSystemOrganization: false);

    private static ReleaseImportJob Build(int releaseId, int? pipelineId, string trigger, string target = ProjectBuildTarget.Current) =>
        new(releaseId, Identity, new ReleaseImportSource.ProjectBuild(1),
            BuildOrder: ProjectBuildOrder.For(pipelineId, target, trigger));

    private static List<int> TakeAll(ProjectBuildQueue queue)
    {
        var taken = new List<int>();
        while (queue.Reader.TryRead(out var job)) taken.Add(job.ReleaseId);
        return taken;
    }

    [Fact]
    public void A_build_somebody_waits_on_starts_before_pushes_and_preview_checks()
    {
        var queue = new ProjectBuildQueue();
        queue.Enqueue(Build(1, pipelineId: 1, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor));
        queue.Enqueue(Build(2, pipelineId: 2, ProjectBuildTrigger.Push));
        queue.Enqueue(Build(3, pipelineId: 3, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMajor));
        queue.Enqueue(Build(4, pipelineId: 4, ProjectBuildTrigger.Manual));
        queue.Enqueue(Build(5, pipelineId: null, ProjectBuildTrigger.PullRequest));

        TakeAll(queue).Should().Equal(4, 5, 2, 1, 3);
    }

    [Fact]
    public void Builds_of_one_pipeline_run_one_at_a_time_in_the_order_they_were_queued()
    {
        var queue = new ProjectBuildQueue();
        var first = Build(1, pipelineId: 7, ProjectBuildTrigger.Push);
        queue.Enqueue(first);
        queue.Enqueue(Build(2, pipelineId: 7, ProjectBuildTrigger.Push));
        // A manual build of the same pipeline outranks a push, but not one already queued ahead of it.
        queue.Enqueue(Build(3, pipelineId: 7, ProjectBuildTrigger.Manual));
        queue.Enqueue(Build(4, pipelineId: 8, ProjectBuildTrigger.Push));

        TakeAll(queue).Should().Equal(1, 4);

        queue.Complete(first);
        TakeAll(queue).Should().Equal(2);
    }

    [Fact]
    public void A_preview_check_runs_beside_its_pipelines_current_build()
    {
        var queue = new ProjectBuildQueue();
        queue.Enqueue(Build(1, pipelineId: 7, ProjectBuildTrigger.Manual));
        queue.Enqueue(Build(2, pipelineId: 7, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor));

        TakeAll(queue).Should().Equal(1, 2);
    }

    [Fact]
    public async Task A_waiting_worker_wakes_when_a_build_of_a_busy_pipeline_may_start()
    {
        var queue = new ProjectBuildQueue();
        var first = Build(1, pipelineId: 7, ProjectBuildTrigger.Manual);
        queue.Enqueue(first);
        queue.Enqueue(Build(2, pipelineId: 7, ProjectBuildTrigger.Push));
        queue.Reader.TryRead(out _).Should().BeTrue();

        var waiting = queue.Reader.WaitToReadAsync().AsTask();
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("the only waiting build belongs to a pipeline that is building");

        queue.Complete(first);
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        TakeAll(queue).Should().Equal(2);
    }

    [Fact]
    public async Task Several_workers_share_the_builds_and_none_runs_twice()
    {
        var queue = new ProjectBuildQueue();
        for (var i = 1; i <= 60; i++) queue.Enqueue(Build(i, pipelineId: i % 6, ProjectBuildTrigger.Push));

        var running = new HashSet<int>();
        var overlaps = new System.Collections.Concurrent.ConcurrentBag<int>();
        var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await queue.Reader.WaitToReadAsync(stop.Token);
                    if (!queue.Reader.TryRead(out var job)) continue;
                    var pipeline = job.ReleaseId % 6;
                    lock (running) if (!running.Add(pipeline)) overlaps.Add(pipeline);
                    await Task.Yield();
                    lock (running) running.Remove(pipeline);
                    seen.Add(job.ReleaseId);
                    queue.Complete(job);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped once every build was seen, or by the time limit.
            }
        })).ToList();

        while (seen.Count < 60 && !stop.IsCancellationRequested) await Task.Delay(10);
        await stop.CancelAsync();
        await Task.WhenAll(workers);

        overlaps.Should().BeEmpty("a pipeline's builds never overlap");
        seen.Should().BeEquivalentTo(Enumerable.Range(1, 60));
        queue.WaitingCount.Should().Be(0);
        queue.RunningCount.Should().Be(0);
    }

    [Fact]
    public void A_release_already_building_is_not_handed_out_again()
    {
        var queue = new ProjectBuildQueue();
        var first = Build(1, pipelineId: null, ProjectBuildTrigger.Manual);
        queue.Enqueue(first);
        queue.Enqueue(Build(1, pipelineId: null, ProjectBuildTrigger.Manual));

        TakeAll(queue).Should().Equal(1);
        queue.Complete(first);
        TakeAll(queue).Should().Equal(1);
    }

    [Fact]
    public void No_more_builds_start_than_the_limit_allows()
    {
        var queue = new ProjectBuildQueue(defaultLimit: 2);
        for (var i = 1; i <= 4; i++) queue.Enqueue(Build(i, pipelineId: i, ProjectBuildTrigger.Push));

        var taken = new List<ReleaseImportJob>();
        while (queue.Reader.TryRead(out var job)) taken.Add(job);

        taken.Select(j => j.ReleaseId).Should().Equal(1, 2);
        queue.RunningCount.Should().Be(2);

        queue.Complete(taken[0]);
        TakeAll(queue).Should().Equal(3);
    }

    [Fact]
    public void The_limit_keeps_the_order_people_first()
    {
        var queue = new ProjectBuildQueue(defaultLimit: 1);
        queue.Enqueue(Build(1, pipelineId: 1, ProjectBuildTrigger.PreviewCheck, ProjectBuildTarget.NextMinor));
        queue.Enqueue(Build(2, pipelineId: 2, ProjectBuildTrigger.Push));
        queue.Enqueue(Build(3, pipelineId: 3, ProjectBuildTrigger.Manual));

        queue.Reader.TryRead(out var first).Should().BeTrue();
        first!.ReleaseId.Should().Be(3);
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Raising_the_limit_wakes_a_waiting_worker()
    {
        var queue = new ProjectBuildQueue(defaultLimit: 1);
        queue.Enqueue(Build(1, pipelineId: 1, ProjectBuildTrigger.Manual));
        queue.Enqueue(Build(2, pipelineId: 2, ProjectBuildTrigger.Manual));
        queue.Reader.TryRead(out _).Should().BeTrue();

        var waiting = queue.Reader.WaitToReadAsync().AsTask();
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("one build is running and the limit is one");

        queue.SetLimit(2);

        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        TakeAll(queue).Should().Equal(2);
    }

    [Fact]
    public void Lowering_the_limit_leaves_running_builds_alone_and_holds_back_new_ones()
    {
        var queue = new ProjectBuildQueue(defaultLimit: 3);
        for (var i = 1; i <= 5; i++) queue.Enqueue(Build(i, pipelineId: i, ProjectBuildTrigger.Push));
        var running = new List<ReleaseImportJob>();
        while (queue.Reader.TryRead(out var job)) running.Add(job);
        running.Should().HaveCount(3);

        queue.SetLimit(1);

        queue.RunningCount.Should().Be(3, "lowering the limit never stops a running build");
        queue.Complete(running[0]);
        TakeAll(queue).Should().BeEmpty("two builds still run, more than the new limit of one");
        queue.Complete(running[1]);
        TakeAll(queue).Should().BeEmpty();
        queue.Complete(running[2]);
        TakeAll(queue).Should().Equal(4);
    }

    [Fact]
    public void A_saved_setting_wins_and_clearing_it_goes_back_to_the_default()
    {
        var queue = new ProjectBuildQueue(defaultLimit: ProjectBuildQueue.Concurrency("3"));
        queue.Limit.Should().Be(3);

        queue.ApplySetting(6);
        queue.Limit.Should().Be(6);

        queue.ApplySetting(null);
        queue.Limit.Should().Be(3);
    }

    [Theory]
    [InlineData(5, 2, 5)]
    [InlineData(null, 2, 2)]
    [InlineData(null, 7, 7)]
    [InlineData(1, 7, 1)]
    [InlineData(0, 2, 1)]
    [InlineData(40, 2, ProjectBuildQueue.MaxConcurrency)]
    public void The_effective_limit_is_the_setting_over_the_default(int? saved, int defaultLimit, int expected) =>
        ProjectBuildQueue.EffectiveLimit(saved, defaultLimit).Should().Be(expected);

    [Fact]
    public void Without_the_setting_or_the_variable_the_default_is_two() =>
        ProjectBuildQueue.EffectiveLimit(null, ProjectBuildQueue.Concurrency(null)).Should().Be(2);

    [Theory]
    [InlineData(null, ProjectBuildQueue.DefaultConcurrency)]
    [InlineData("", ProjectBuildQueue.DefaultConcurrency)]
    [InlineData("four", ProjectBuildQueue.DefaultConcurrency)]
    [InlineData("0", 1)]
    [InlineData("4", 4)]
    [InlineData("500", ProjectBuildQueue.MaxConcurrency)]
    public void Concurrency_comes_from_the_setting_within_bounds(string? raw, int expected) =>
        ProjectBuildQueue.Concurrency(raw).Should().Be(expected);

    // ── Standing aside while waiting on another import (#1180) ──────────

    private static ProjectBuildQueue OneAtATime(params int[] releaseIds)
    {
        var queue = new ProjectBuildQueue(defaultLimit: 1);
        foreach (var id in releaseIds) queue.Enqueue(Build(id, pipelineId: id, ProjectBuildTrigger.Push));
        return queue;
    }

    private static ReleaseImportJob Take(ProjectBuildQueue queue)
    {
        queue.Reader.TryRead(out var job).Should().BeTrue();
        return job!;
    }

    [Fact]
    public async Task A_build_waiting_on_another_import_lets_the_next_build_start_in_its_place()
    {
        var queue = OneAtATime(1, 2);
        Take(queue);
        TakeAll(queue).Should().BeEmpty("one build may run at once");

        var wait = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aside = queue.StepAsideAsync(1, () => wait.Task, CancellationToken.None);

        var second = Take(queue);
        second.ReleaseId.Should().Be(2);

        wait.SetResult("parent ready");
        await Task.Delay(50);
        aside.IsCompleted.Should().BeFalse("the build waits for a place before it carries on");

        queue.Complete(second);
        (await aside.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be("parent ready");
    }

    [Fact]
    public async Task A_wait_that_ends_at_once_keeps_the_build_its_place()
    {
        var queue = OneAtATime(1, 2, 3);
        Take(queue);

        (await queue.StepAsideAsync(1, () => Task.FromResult(true), CancellationToken.None)).Should().BeTrue();
        TakeAll(queue).Should().BeEmpty("build 1 holds the only place again");
    }

    [Fact]
    public async Task A_build_that_comes_back_while_its_place_is_taken_waits_ahead_of_new_builds()
    {
        var queue = OneAtATime(1, 2, 3);
        Take(queue);
        var wait = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aside = queue.StepAsideAsync(1, () => wait.Task, CancellationToken.None);
        var second = Take(queue);

        wait.SetResult(7);
        await Task.Delay(50);
        queue.Complete(second);

        (await aside.WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(7);
        TakeAll(queue).Should().BeEmpty("build 3 waits behind the build that came back");
    }

    [Fact]
    public async Task A_cancelled_build_takes_its_place_back_at_once()
    {
        var queue = OneAtATime(1, 2, 3);
        Take(queue);
        using var cts = new CancellationTokenSource();
        var wait = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aside = queue.StepAsideAsync(1, () => wait.Task.WaitAsync(cts.Token), cts.Token);
        Take(queue);

        await cts.CancelAsync();

        var act = () => aside.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        TakeAll(queue).Should().BeEmpty();
    }

    [Fact]
    public async Task A_lock_taken_while_standing_aside_is_let_go_when_the_build_is_cancelled_on_its_way_back()
    {
        var queue = OneAtATime(1, 2);
        Take(queue);
        using var cts = new CancellationTokenSource();
        var released = new Released();
        var wait = new TaskCompletionSource<IDisposable>(TaskCreationOptions.RunContinuationsAsynchronously);
        var aside = queue.StepAsideAsync(1, () => wait.Task, cts.Token);
        Take(queue);

        wait.SetResult(released);
        await Task.Delay(50);
        await cts.CancelAsync();

        var act = () => aside.WaitAsync(TimeSpan.FromSeconds(5));
        await act.Should().ThrowAsync<OperationCanceledException>();
        released.Disposed.Should().BeTrue("nobody else will ever release it");
    }

    [Fact]
    public async Task A_build_that_got_the_import_gate_comes_back_at_once_even_over_the_limit()
    {
        // Waiting for a place while holding the gate would hold up every other import.
        var queue = OneAtATime(1, 2, 3);
        Take(queue);
        var ingests = new ReleaseIngests();
        var held = ingests.TryEnterHeavy()!;
        var aside = queue.StepAsideAsync(1, () => ingests.EnterHeavyAsync(CancellationToken.None), CancellationToken.None, comeBackAtOnce: true);
        var second = Take(queue);

        held.Dispose();
        using var gate = await aside.WaitAsync(TimeSpan.FromSeconds(5));

        queue.RunningCount.Should().Be(2, "build 1 came back while build 2 still runs");
        TakeAll(queue).Should().BeEmpty("nothing more starts until the builds are under the limit again");
        queue.Complete(second);
        TakeAll(queue).Should().BeEmpty("build 1 alone fills the limit of one");
    }

    [Fact]
    public async Task A_release_the_queue_is_not_running_just_waits()
    {
        var queue = OneAtATime(1, 2);
        Take(queue);

        (await queue.StepAsideAsync(99, () => Task.FromResult(5), CancellationToken.None)).Should().Be(5);
        TakeAll(queue).Should().BeEmpty("nothing stood aside, so the limit still holds");
    }

    private sealed class Released : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
