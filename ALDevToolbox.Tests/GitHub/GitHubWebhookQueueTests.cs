using ALDevToolbox.Services.GitHub;
using AwesomeAssertions;

namespace ALDevToolbox.Tests.GitHub;

/// <summary>
/// The supersession contract of <see cref="GitHubWebhookQueue"/> (issue #627):
/// one build per pull-request head at a time, an older head dropped when it is
/// dequeued, and an in-flight build cancelled the moment a newer head is
/// announced. This is the part of the compile gate that decides whether a
/// reviewer sees a tick about the commit they are looking at.
/// </summary>
public sealed class GitHubWebhookQueueTests
{
    /// <summary>The queue's own clock in the tests that age a head out, whatever today is.</summary>
    private static readonly DateTimeOffset Noon = new(2026, 3, 29, 12, 0, 0, TimeSpan.Zero);

    private static GitHubPullRequestJob Job(string sha, int number = 7) =>
        new(
            InstallationId: 42,
            RepositoryFullName: "cronus-dk/customer-app",
            CloneUrl: "https://github.com/cronus-dk/customer-app.git",
            PullRequestNumber: number,
            HeadSha: sha,
            HeadRef: "feature/vat",
            BaseRef: "main",
            DeliveryId: "delivery-" + sha);

    [Fact]
    public void The_key_ignores_case_so_two_spellings_are_one_pull_request()
    {
        var lower = Job("abc") with { RepositoryFullName = "cronus-dk/customer-app" };
        var upper = Job("abc") with { RepositoryFullName = "CRONUS-DK/Customer-App" };

        upper.Key.Should().Be(lower.Key);
    }

    [Fact]
    public void Different_pull_requests_are_different_keys() =>
        Job("abc", number: 7).Key.Should().NotBe(Job("abc", number: 8).Key);

    [Fact]
    public void Finishing_the_latest_head_forgets_it_so_the_map_does_not_grow_forever()
    {
        // Every pull request the workbench ever built would otherwise leave an
        // entry behind for the life of the process.
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        using var cts = new CancellationTokenSource();

        queue.Announce(job.Key, "aaa");
        queue.BeginBuild(job.Key, cts);
        queue.TrackedHeadCount.Should().Be(1);

        queue.EndBuild(job.Key, cts, "aaa");

        queue.TrackedHeadCount.Should().Be(0);
    }

    [Fact]
    public void Finishing_a_superseded_head_leaves_the_newer_one_recorded()
    {
        // The newer head owns the entry. Dropping it here would make the build
        // that has just been superseded look current again.
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        using var cts = new CancellationTokenSource();

        queue.Announce(job.Key, "aaa");
        queue.BeginBuild(job.Key, cts);
        queue.Announce(job.Key, "bbb");

        queue.EndBuild(job.Key, cts, "aaa");

        queue.TrackedHeadCount.Should().Be(1);
        queue.IsLatest(job.Key, "aaa").Should().BeFalse();
        queue.IsLatest(job.Key, "bbb").Should().BeTrue();
    }

    [Fact]
    public void A_full_queue_refuses_rather_than_waiting()
    {
        // The webhook endpoint runs on a request thread GitHub is timing, so a
        // full channel has to be an answer rather than a wait.
        var queue = new GitHubWebhookQueue();
        var written = 0;
        while (queue.TryEnqueue(Job("aaa", number: written + 1))) written++;

        written.Should().BeGreaterThan(0);
        queue.TryEnqueue(Job("zzz")).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_key_is_treated_as_current()
    {
        // A restart drops the bookkeeping. Refusing to build a job we have no
        // record of would be worse than building it.
        var queue = new GitHubWebhookQueue();

        queue.IsLatest("never-seen", "abc").Should().BeTrue();
    }

    [Fact]
    public void The_announced_head_is_the_latest_and_the_previous_one_is_not()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");

        queue.Announce(job.Key, "aaa");
        queue.IsLatest(job.Key, "aaa").Should().BeTrue();

        queue.Announce(job.Key, "bbb");
        queue.IsLatest(job.Key, "bbb").Should().BeTrue();
        queue.IsLatest(job.Key, "aaa").Should().BeFalse("a newer commit was pushed to the same pull request");
    }

    [Fact]
    public void Announcing_a_newer_head_cancels_the_build_running_for_the_older_one()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        using var running = new CancellationTokenSource();
        queue.BeginBuild(job.Key, running);

        queue.Announce(job.Key, "bbb");

        running.IsCancellationRequested.Should().BeTrue(
            "compiling a commit nobody is reviewing any more is work spent on the wrong answer");
    }

    [Fact]
    public void Re_announcing_the_same_head_does_not_cancel_the_build()
    {
        // GitHub redelivers; a redelivery is not a new commit.
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        using var running = new CancellationTokenSource();
        queue.BeginBuild(job.Key, running);
        queue.Announce(job.Key, "aaa");

        running.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void A_build_for_another_pull_request_is_untouched()
    {
        var queue = new GitHubWebhookQueue();
        var mine = Job("aaa", number: 7);
        var theirs = Job("zzz", number: 8);
        queue.Announce(mine.Key, "aaa");
        queue.Announce(theirs.Key, "zzz");

        using var otherBuild = new CancellationTokenSource();
        queue.BeginBuild(theirs.Key, otherBuild);

        queue.Announce(mine.Key, "bbb");

        otherBuild.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public void EndBuild_clears_the_registration_so_a_later_head_cancels_nothing()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        queue.Announce(job.Key, "aaa");

        var finished = new CancellationTokenSource();
        queue.BeginBuild(job.Key, finished);
        queue.EndBuild(job.Key, finished);
        finished.Dispose();

        // Cancelling a disposed source would throw; the queue must not try.
        var act = () => queue.Announce(job.Key, "bbb");
        act.Should().NotThrow();
    }

    [Fact]
    public void An_older_head_announced_after_a_newer_one_does_not_become_the_latest()
    {
        // GitHub does not promise order: the delivery for the earlier push can be
        // handled after the later one (#1120).
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        var at = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        using var running = new CancellationTokenSource();

        queue.Announce(job.Key, "bbb", at.AddMinutes(1)).Should().BeTrue();
        queue.BeginBuild(job.Key, running);

        queue.Announce(job.Key, "aaa", at).Should().BeFalse();

        queue.IsLatest(job.Key, "bbb").Should().BeTrue();
        queue.IsLatest(job.Key, "aaa").Should().BeFalse();
        running.IsCancellationRequested.Should().BeFalse("the build of the newer head keeps going");
    }

    [Fact]
    public void An_older_head_resent_after_the_newer_build_finished_is_still_older()
    {
        // A redelivery can come long after the newer head was built; forgetting the
        // head when its build ends would let the old commit build again.
        var clock = new ALDevToolbox.Tests.Auth.FakeTimeProvider(Noon);
        var queue = new GitHubWebhookQueue(clock);
        var job = Job("bbb");
        var at = Noon.AddMinutes(-10);
        using var cts = new CancellationTokenSource();
        queue.Announce(job.Key, "bbb", at.AddMinutes(1));
        queue.BeginBuild(job.Key, cts);
        queue.EndBuild(job.Key, cts, "bbb");

        queue.Announce(job.Key, "aaa", at).Should().BeFalse();

        queue.IsLatest(job.Key, "aaa").Should().BeFalse();
    }

    [Fact]
    public void A_dated_head_is_forgotten_once_it_is_older_than_any_resend()
    {
        var clock = new ALDevToolbox.Tests.Auth.FakeTimeProvider(Noon);
        var queue = new GitHubWebhookQueue(clock);
        var job = Job("aaa");
        var other = Job("zzz", number: 8);
        using var cts = new CancellationTokenSource();
        queue.Announce(job.Key, "aaa", Noon.AddMinutes(-1));
        queue.BeginBuild(job.Key, cts);
        queue.EndBuild(job.Key, cts, "aaa");
        queue.TrackedHeadCount.Should().Be(1, "a resend of an older head may still come");

        // Once the clock is past the look-back, the next build to end clears it out.
        clock.Advance(GitHubWebhookQueue.KeepDatedHeadsFor + TimeSpan.FromMinutes(1));
        using var later = new CancellationTokenSource();
        queue.Announce(other.Key, "zzz");
        queue.BeginBuild(other.Key, later);
        queue.EndBuild(other.Key, later, "zzz");

        queue.TrackedHeadCount.Should().Be(0);
    }

    [Fact]
    public void A_delivery_without_a_time_keeps_the_time_already_known()
    {
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        var at = DateTimeOffset.UtcNow.AddMinutes(-10);
        queue.Announce(job.Key, "bbb", at.AddMinutes(1));
        queue.Announce(job.Key, "ccc");

        queue.Announce(job.Key, "aaa", at).Should().BeFalse("it is still older than the last time GitHub gave");
        queue.IsLatest(job.Key, "ccc").Should().BeTrue();
    }

    [Fact]
    public void Two_heads_with_the_same_time_cannot_be_ordered_so_the_later_announcement_wins()
    {
        // GitHub's time has whole seconds; within one there is nothing to order by.
        var queue = new GitHubWebhookQueue();
        var job = Job("aaa");
        var at = DateTimeOffset.UtcNow;
        queue.Announce(job.Key, "aaa", at);

        queue.Announce(job.Key, "bbb", at).Should().BeTrue();
        queue.IsLatest(job.Key, "bbb").Should().BeTrue();
    }

    [Fact]
    public async Task Two_deliveries_announced_at_once_always_leave_the_newer_head_as_the_latest()
    {
        var at = new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);
        for (var round = 0; round < 200; round++)
        {
            var queue = new GitHubWebhookQueue();
            var key = Job("aaa").Key;
            using var start = new ManualResetEventSlim();
            var older = Task.Run(() => { start.Wait(); queue.Announce(key, "aaa", at); });
            var newer = Task.Run(() => { start.Wait(); queue.Announce(key, "bbb", at.AddSeconds(5)); });
            start.Set();
            await Task.WhenAll(older, newer);

            queue.IsLatest(key, "bbb").Should().BeTrue($"round {round}");
        }
    }

    [Fact]
    public async Task Enqueued_jobs_come_back_off_the_channel_in_order()
    {
        var queue = new GitHubWebhookQueue();

        await queue.EnqueueAsync(Job("aaa"));
        await queue.EnqueueAsync(Job("bbb"));

        queue.Reader.TryRead(out var first).Should().BeTrue();
        queue.Reader.TryRead(out var second).Should().BeTrue();
        ((GitHubPullRequestJob)first!).HeadSha.Should().Be("aaa");
        ((GitHubPullRequestJob)second!).HeadSha.Should().Be("bbb");
    }

    [Theory]
    [InlineData("https://github.com/cronus-dk/customer-app.git")]
    [InlineData("https://github.com/CRONUS-DK/Customer-App")]
    [InlineData("https://github.com/cronus-dk/customer-app/")]
    [InlineData("git@github.com:cronus-dk/customer-app.git")]
    public void Repository_urls_that_name_the_same_repository_normalise_to_one_value(string url) =>
        GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(url)
            .Should().Be("github.com/cronus-dk/customer-app");

    [Fact]
    public void A_different_repository_does_not_normalise_to_the_same_value() =>
        GitHubPullRequestBuildWorker.NormaliseRepositoryUrl("https://github.com/cronus-dk/other-app")
            .Should().NotBe(GitHubPullRequestBuildWorker.NormaliseRepositoryUrl(
                "https://github.com/cronus-dk/customer-app"));
}
