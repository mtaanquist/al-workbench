using System.Collections.Concurrent;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// One verified delivery the worker has to act on, as <c>POST /github/webhook</c>
/// read it off GitHub's payload. Three kinds: a pull-request head to compile
/// (<see cref="GitHubPullRequestJob"/>), a branch that moved
/// (<see cref="GitHubPushJob"/>), and a pull request that merged
/// (<see cref="GitHubMergedPullRequestJob"/>).
///
/// <para>Everything here is what GitHub said, not what the workbench believes: the
/// endpoint never touches the database, so a delivery whose signature checked out
/// costs one channel write and nothing else. The worker is where the installation
/// is resolved back to an organisation and where anything is trusted.</para>
/// </summary>
public abstract record GitHubWebhookJob(
    long InstallationId,
    string RepositoryFullName,
    string CloneUrl,
    string DeliveryId);

/// <summary>
/// One pull-request head the workbench has been asked to compile, as
/// <c>POST /github/webhook</c> read it off GitHub's <c>pull_request</c> delivery.
/// <see cref="UpdatedAt"/> is the pull request's own <c>updated_at</c>, which moves
/// forward with every push, so two deliveries can be put in order whichever
/// arrived first (#1120).
///
/// <para><see cref="IsMemberFork"/> marks the one kind of pull request whose head
/// lives somewhere else and is still built: one opened by a member or owner of the
/// organisation, from that person's own fork. GitHub's <c>author_association</c> is
/// what says so, and the delivery carrying it was HMAC-verified - but it is still
/// only what GitHub said when the pull request was opened, so the worker asks the
/// membership question again with the installation token before anything is cloned.
/// <see cref="AuthorLogin"/> is who to ask about, and who the check run names as the
/// source of the code.</para>
/// </summary>
public sealed record GitHubPullRequestJob(
    long InstallationId,
    string RepositoryFullName,
    string CloneUrl,
    int PullRequestNumber,
    string HeadSha,
    string HeadRef,
    string BaseRef,
    string DeliveryId,
    string AuthorLogin = "",
    bool IsMemberFork = false,
    DateTimeOffset? UpdatedAt = null)
    : GitHubWebhookJob(InstallationId, RepositoryFullName, CloneUrl, DeliveryId)
{
    /// <summary>
    /// The pull request this job is about, as a key: one build at a time per
    /// <c>(installation, repository, pull request)</c>. Lower-cased because GitHub
    /// repository names are case-insensitive and two deliveries for the same pull
    /// request must not look like two subjects.
    /// </summary>
    public string Key => $"{InstallationId}:{RepositoryFullName}:{PullRequestNumber}".ToLowerInvariant();
}

/// <summary>
/// One commit a push carried: its id and its message, as GitHub listed it in the
/// payload's <c>commits[]</c>.
/// </summary>
public sealed record GitHubPushCommit(
    [property: System.Text.Json.Serialization.JsonPropertyName("sha")] string Sha,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

/// <summary>
/// A branch moved: GitHub's <c>push</c> delivery for a <c>refs/heads/</c> ref. Tag
/// pushes never become one of these. The worker records the new head so a pipeline
/// watching the branch can say it is behind, and starts a build on each pipeline that
/// builds on push. See <c>.design/github-integration-phase2.md</c>, "Branch watching"
/// (#963) and "Building on push" (#1079).
/// </summary>
/// <param name="Branch">The branch name, with <c>refs/heads/</c> taken off.</param>
/// <param name="HeadSha">The commit the branch now points at, or the one it pointed at before a delete.</param>
/// <param name="DefaultBranch">The repository's default branch as GitHub reported it on this delivery; empty when absent.</param>
/// <param name="Commits">The last ten of the push's <c>commits[]</c>, oldest first.</param>
/// <param name="CommitCount">How many commits the payload listed (GitHub caps the list at twenty).</param>
public sealed record GitHubPushJob(
    long InstallationId,
    string RepositoryFullName,
    string CloneUrl,
    string DeliveryId,
    string Branch,
    string HeadSha,
    string BeforeSha,
    string DefaultBranch,
    string PusherLogin,
    bool Forced,
    bool Deleted,
    DateTime PushedAt,
    int CommitCount,
    IReadOnlyList<GitHubPushCommit> Commits)
    : GitHubWebhookJob(InstallationId, RepositoryFullName, CloneUrl, DeliveryId);

/// <summary>
/// A pull request that merged: <c>pull_request</c> with action <c>closed</c> and
/// <c>merged: true</c>. Recorded so a pipeline can say which pull requests landed
/// on its branch since it last built (#963); nothing is built on it.
/// </summary>
public sealed record GitHubMergedPullRequestJob(
    long InstallationId,
    string RepositoryFullName,
    string CloneUrl,
    string DeliveryId,
    int Number,
    string Title,
    string BaseBranch,
    string MergeSha,
    DateTime MergedAt,
    string AuthorLogin)
    : GitHubWebhookJob(InstallationId, RepositoryFullName, CloneUrl, DeliveryId);

/// <summary>
/// The hand-off from the webhook endpoint to
/// <see cref="GitHubPullRequestBuildWorker"/>, plus the supersession bookkeeping
/// that keeps a pull request to one build at a time. Push and merged-pull-request
/// deliveries ride the same channel; the supersession half is only about
/// <see cref="GitHubPullRequestJob"/>.
///
/// <para>A push to an open pull request produces a <c>synchronize</c> delivery per
/// push, and a person who pushes three fixes in a minute would otherwise get three
/// builds of which only the last is about code that still exists. So each key
/// records the newest head SHA it has been told about: a job dequeued for an older
/// SHA is skipped, and a build already running for that key is cancelled through
/// the token this queue hands out. The dedupe gate the base class offers is the
/// wrong shape for that - it would coalesce the <em>new</em> head into the
/// <em>old</em> build, which is the opposite of what the reviewer wants to
/// see.</para>
///
/// <para>All of it is in memory. A restart drops queued deliveries, and GitHub
/// does not resend them by itself: GitHub logged them as delivered, so the next
/// push is the recovery for those. A delivery we refused or never answered is
/// resent by <see cref="GitHubWebhookRecoveryScheduler"/>, which also closes the
/// check run of a pull-request build a restart cut short (#1121). See
/// <c>.design/github-integration-phase2.md</c> (#627).</para>
/// </summary>
public sealed class GitHubWebhookQueue : JobQueue<GitHubWebhookJob>
{
    /// <summary>The newest head a pull request was announced at, and GitHub's time for it when the delivery carried one.</summary>
    private sealed record HeadMark(string Sha, DateTimeOffset? UpdatedAt);

    private readonly ConcurrentDictionary<string, HeadMark> _latestSha = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>How many deliveries the queue holds before the endpoint refuses more.</summary>
    public const int Capacity = 128;

    // Deliveries are tiny (one record of strings) and the worker is single-reader,
    // so the bound is about how deep a backlog is worth holding rather than memory.
    // A busy organisation pushing to a hundred pull requests at once still queues;
    // beyond that the endpoint refuses, and GitHubWebhookRecoveryScheduler asks
    // GitHub to resend once there is room (#1121).
    public GitHubWebhookQueue(TimeProvider clock) : base(capacity: Capacity) => _clock = clock;

    public GitHubWebhookQueue() : this(TimeProvider.System) { }

    /// <summary>How many deliveries are waiting for the worker right now.</summary>
    public int Backlog => Reader.Count;

    /// <summary>
    /// Queues <paramref name="job"/> if there is room, and answers false when
    /// there is not.
    ///
    /// <para>The webhook endpoint runs on a request thread that GitHub is timing.
    /// Waiting on a full channel would hold that request open behind a backlog of
    /// builds and eventually have GitHub give up on us anyway; refusing is both
    /// honest and cheaper. GitHub does not resend a refused delivery by itself;
    /// <see cref="GitHubWebhookRecoveryScheduler"/> asks it to once the backlog
    /// has drained (#1121).</para>
    /// </summary>
    public bool TryEnqueue(GitHubWebhookJob job) => Writer.TryWrite(job);

    /// <summary>
    /// Records <paramref name="headSha"/> as the newest head for
    /// <paramref name="key"/> and cancels any build still running for an older
    /// one. Called at enqueue time, before the job reaches the worker, so the
    /// in-flight build learns it has been superseded as early as GitHub told us.
    ///
    /// <para>Two deliveries for one pull request can be handled at the same moment,
    /// and GitHub does not promise their order. So the record only ever moves
    /// forward (#1120): a head whose <paramref name="updatedAt"/> is older than the
    /// one already recorded is left out, and the swap itself is a compare-and-set,
    /// so the older of two racing deliveries cannot win by writing last. Without a
    /// time on either side, or with the same time (GitHub's has whole seconds), the later
    /// announcement wins, as before. Returns whether
    /// <paramref name="headSha"/> is the newest head once this returns; the job it
    /// came with is then skipped by <see cref="IsLatest"/> if not.</para>
    /// </summary>
    public bool Announce(string key, string headSha, DateTimeOffset? updatedAt = null)
    {
        var mark = new HeadMark(headSha, updatedAt);
        HeadMark? previous;
        while (true)
        {
            if (!_latestSha.TryGetValue(key, out previous))
            {
                if (_latestSha.TryAdd(key, mark)) break;
                continue;
            }
            if (previous.UpdatedAt is { } recorded && updatedAt is { } offered && offered < recorded)
            {
                return string.Equals(previous.Sha, headSha, StringComparison.OrdinalIgnoreCase);
            }
            // A delivery without a time cannot be ordered, but it must not wipe the
            // time already known, or every older delivery after it would get through.
            var next = updatedAt is null && previous.UpdatedAt is not null ? mark with { UpdatedAt = previous.UpdatedAt } : mark;
            if (_latestSha.TryUpdate(key, next, previous)) break;
        }

        if (previous is not null && string.Equals(previous.Sha, headSha, StringComparison.OrdinalIgnoreCase)) return true;

        if (_running.TryGetValue(key, out var cts))
        {
            // Cancel, don't remove: the worker owns the registration and clears it
            // in its own finally, so removing here would strand a live token.
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* the build finished as we asked. */ }
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="headSha"/> is still the newest head this queue
    /// was told about for <paramref name="key"/>. A key it has never heard of is
    /// current by definition - that is a job whose bookkeeping a restart dropped,
    /// and refusing to build it would be worse than building it.
    /// </summary>
    public bool IsLatest(string key, string headSha) =>
        !_latestSha.TryGetValue(key, out var latest)
        || string.Equals(latest.Sha, headSha, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers <paramref name="cts"/> as the build in flight for
    /// <paramref name="key"/>, so a newer head can cancel it. The caller disposes
    /// the source and calls <see cref="EndBuild"/> when the build ends.
    /// </summary>
    public void BeginBuild(string key, CancellationTokenSource cts) => _running[key] = cts;

    /// <summary>
    /// Clears the in-flight registration for <paramref name="key"/> if
    /// <paramref name="cts"/> is still the one held, and forgets the newest-head
    /// record when <paramref name="headSha"/> is still that head.
    ///
    /// <para>The second half is what keeps the map from growing for the life of
    /// the process: every pull request the workbench ever built would otherwise
    /// leave an entry behind. It is only safe when the head just built is still
    /// the newest one - a newer head announced mid-build owns the entry, and
    /// dropping it would make the superseded build look current again. A head that
    /// carries GitHub's time is kept for <see cref="KeepDatedHeadsFor"/> instead, so
    /// an older delivery resent after the newer build finished is still recognised
    /// as older (#1120); entries past that age are dropped here.</para>
    /// </summary>
    public void EndBuild(string key, CancellationTokenSource cts, string? headSha = null)
    {
        ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_running)
            .Remove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
        if (headSha is null) return;
        if (_latestSha.TryGetValue(key, out var latest)
            && latest.UpdatedAt is null
            && string.Equals(latest.Sha, headSha, StringComparison.OrdinalIgnoreCase))
        {
            ((ICollection<KeyValuePair<string, HeadMark>>)_latestSha)
                .Remove(new KeyValuePair<string, HeadMark>(key, latest));
        }

        var cutoff = _clock.GetUtcNow() - KeepDatedHeadsFor;
        foreach (var entry in _latestSha)
        {
            if (entry.Value.UpdatedAt < cutoff && !_running.ContainsKey(entry.Key))
                ((ICollection<KeyValuePair<string, HeadMark>>)_latestSha).Remove(entry);
        }
    }

    /// <summary>
    /// How long a built head with GitHub's time is remembered after its build ends:
    /// as long as <see cref="GitHubWebhookRecoveryScheduler"/> looks back for
    /// deliveries to resend.
    /// </summary>
    internal static TimeSpan KeepDatedHeadsFor => GitHubWebhookRecoveryScheduler.Lookback;

    /// <summary>How many pull requests this queue is still holding a newest-head record for. Test seam.</summary>
    internal int TrackedHeadCount => _latestSha.Count;

    /// <summary>Forgets the newest-head record for <paramref name="key"/>. Test seam.</summary>
    internal void Forget(string key)
    {
        _latestSha.TryRemove(key, out _);
        _running.TryRemove(key, out _);
    }
}
