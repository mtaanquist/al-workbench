using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.Operations;
using ALDevToolbox.Services.Workers;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.GitHub;

/// <summary>
/// Picks up what GitHub's webhook leaves on the floor (#1121).
///
/// <para><strong>Refused deliveries are asked for again.</strong> GitHub does not
/// resend a delivery it logged as failed: the 503 the endpoint answers while its
/// queue is full, a 413, a 5xx, or no answer at all while the app was down all
/// stay failed until somebody presses Redeliver. Every five minutes this reads the
/// App's delivery log and does that for each <c>push</c> or <c>pull_request</c>
/// event none of whose attempts got through. The push path already ignores a push
/// it has seen or one older than what it recorded. The pull-request path does not,
/// so a pull-request delivery is resent only while its head is still the open pull
/// request's head on GitHub.</para>
///
/// <para><strong>Pull-request builds a restart cut short are closed.</strong> They
/// are deliberately not resumed (see
/// <see cref="ObjectExplorer.Projects.ProjectBuildImporter.StartPullRequestBuildAsync"/>),
/// which used to leave the build row queued and the check run spinning on the pull
/// request for good. The first pass after startup fails them and completes their
/// check runs as neutral.</para>
///
/// <para>The delivery log belongs to the App, not to any organisation, so the resend
/// half reads no tenant data at all. The orphan half walks <c>organizations</c> (no
/// tenant filter) and works inside each organisation's
/// <see cref="AmbientOrganizationScope"/>, the same shape as
/// <see cref="RepositoryDiscoveryScheduler"/>: there is no <c>IgnoreQueryFilters()</c>
/// here. Opt out with <c>DISABLE_GITHUB_WEBHOOK_RECOVERY_SCHEDULER=1</c>. See
/// <c>.design/github-integration-phase2.md</c>.</para>
/// </summary>
public sealed class GitHubWebhookRecoveryScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    /// <summary>How far back GitHub lets a delivery be redelivered, and so how far back the log is read.</summary>
    internal static readonly TimeSpan Lookback = TimeSpan.FromDays(3);

    /// <summary>
    /// How far each sweep reaches back past where the previous one started, so a
    /// delivery GitHub logged a moment late is still seen.
    /// </summary>
    internal static readonly TimeSpan Overlap = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The most log pages one sweep reads. A hundred deliveries a page; the first
    /// sweep after a restart reads up to three days, and a busy GitHub organisation
    /// can log more than this in that time, so the newest are the ones covered.
    /// </summary>
    internal const int MaxPages = 20;

    /// <summary>How many times one event is asked for before it is left alone.</summary>
    internal const int MaxAttempts = 5;

    /// <summary>What the build and its check run say when a restart cut a pull-request build short.</summary>
    internal const string RestartedMessage =
        "The workbench restarted before this build finished. Push to the pull request again to rebuild it.";

    private readonly IServiceProvider _services;
    private readonly GitHubWebhookQueue _queue;
    private readonly TimeProvider _clock;
    private readonly ILogger<GitHubWebhookRecoveryScheduler> _logger;

    /// <summary>
    /// When this process started, near enough. Hosted services are constructed
    /// before the server listens, so a pull-request build started before this was
    /// queued in memory by an earlier process and nothing will ever run it.
    /// </summary>
    private readonly DateTime _startedAt;

    private bool _orphansClosed;
    private DateTime? _lastSweepAt;

    // Per event (the delivery guid GitHub keeps across redeliveries): how many
    // times this process asked for it, and when it last did, so the map can be
    // pruned past the lookback.
    private readonly Dictionary<string, (int Count, DateTime LastAt)> _attempts = new(StringComparer.Ordinal);

    // Log entries already acted on. A sweep's overlap reads the same failed entry
    // twice; the second read must not ask again before the first resend is logged.
    private readonly Dictionary<long, DateTime> _resentEntries = new();

    public GitHubWebhookRecoveryScheduler(
        IServiceProvider services,
        GitHubWebhookQueue queue,
        TimeProvider clock,
        ILogger<GitHubWebhookRecoveryScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(GitHubWebhookRecoveryScheduler),
            pollInterval: PollInterval,
            // Twenty log pages and a resend each for the failures: GitHub calls at
            // thirty seconds' deadline apiece, so ten minutes is a slow GitHub, not a hang.
            maxActiveDuration: TimeSpan.FromMinutes(10),
            maxIdleSilence: TimeSpan.FromMinutes(20),
            disableEnvVar: "DISABLE_GITHUB_WEBHOOK_RECOVERY_SCHEDULER")
    {
        _services = services;
        _queue = queue;
        _clock = clock;
        _logger = logger;
        _startedAt = clock.GetUtcNow().UtcDateTime;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        if (!_orphansClosed)
        {
            await CloseOrphanedPullRequestBuildsAsync(ct).ConfigureAwait(false);
            _orphansClosed = true;
        }
        await RedeliverFailedAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One pass over the App's delivery log, asking GitHub to resend every
    /// <c>push</c> and <c>pull_request</c> event that has not got through. Returns
    /// how many were asked for. Internal so a test can drive it without the poll loop.
    /// </summary>
    internal async Task<int> RedeliverFailedAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<SystemSettingsService>();
        // No App means no log to read; no secret means every delivery is refused
        // with 401, and resending would only be refused again.
        if (await settings.ResolveGitHubAppAsync(ct).ConfigureAwait(false) is null
            || await settings.ResolveGitHubWebhookSecretAsync(ct).ConfigureAwait(false) is null)
        {
            return 0;
        }

        // A resend lands in the same queue. While it is still well behind, the
        // resend would be refused again and spend one of the event's attempts.
        if (_queue.Backlog > GitHubWebhookQueue.Capacity / 2)
        {
            _logger.LogInformation(
                "Not asking GitHub to resend webhook deliveries yet: {Backlog} are still waiting in the queue.", _queue.Backlog);
            return 0;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var oldest = now - Lookback;
        var since = _lastSweepAt is { } last && last - Overlap > oldest ? last - Overlap : oldest;

        var github = scope.ServiceProvider.GetRequiredService<GitHubAppClient>();
        var log = new List<GitHubHookDelivery>();
        string? cursor = null;
        var pages = 0;
        var complete = false;
        while (pages < MaxPages)
        {
            var page = await github.ListHookDeliveriesAsync(cursor, ct).ConfigureAwait(false);
            pages++;
            log.AddRange(page.Deliveries.Where(d => d.DeliveredAt >= since));
            if (page.NextCursor is null || page.Deliveries.Count == 0 || page.Deliveries.Min(d => d.DeliveredAt) < since)
            {
                complete = true;
                break;
            }
            cursor = page.NextCursor;
        }
        if (!complete)
        {
            _logger.LogWarning(
                "Read {Pages} pages of GitHub's webhook delivery log back to {Oldest:O} without reaching {Since:O}; failures older than that are not resent.",
                pages, log.Count > 0 ? log.Min(d => d.DeliveredAt) : now, since);
        }

        // An event got through when any of its attempts did; the rest are what to resend.
        var delivered = log.Where(d => d.Succeeded).Select(d => d.Guid).ToHashSet(StringComparer.Ordinal);
        var due = log
            .Where(d => IsResentEvent(d) && IsWorthResending(d.StatusCode) && !delivered.Contains(d.Guid))
            .GroupBy(d => d.Guid, StringComparer.Ordinal)
            // GitHub resends the same payload whichever attempt is named, so the
            // newest one stands for the event, ordered by when the event first
            // failed: GitHub handles each resend on its own, so this is the order
            // they are asked for in, not a promise about arrival.
            .Select(g => (Delivery: g.MaxBy(d => d.DeliveredAt)!, FirstAt: g.Min(d => d.DeliveredAt)))
            .Where(e => !_resentEntries.ContainsKey(e.Delivery.Id))
            .Where(e => !_attempts.TryGetValue(e.Delivery.Guid, out var tried) || tried.Count < MaxAttempts)
            .OrderBy(e => e.FirstAt)
            .Select(e => e.Delivery)
            .ToList();

        // Only as many as the queue has room for: a resend refused again spends
        // one of the event's attempts for nothing. The rest wait for the next sweep.
        var room = Math.Max(0, GitHubWebhookQueue.Capacity / 2 - _queue.Backlog);
        var resent = 0;
        var asked = 0;
        var leftOver = false;
        foreach (var delivery in due)
        {
            if (asked >= room)
            {
                leftOver = true;
                break;
            }
            ct.ThrowIfCancellationRequested();

            // A pull request whose head has moved on since this delivery must not
            // be resent: the endpoint would take the older head as the newest,
            // cancel the build of the real one and compile code nobody reviews.
            if (IsPullRequestBuild(delivery))
            {
                bool? current;
                try
                {
                    current = await IsStillThePullRequestHeadAsync(github, delivery, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is GitHubApiException or GitHubAppNotConfiguredException or HttpRequestException)
                {
                    // Not knowing is not a yes; the next sweep asks again.
                    _logger.LogWarning(ex,
                        "Could not tell whether the pull_request delivery {Guid} is still current; not resent this time.", delivery.Guid);
                    leftOver = true;
                    continue;
                }
                if (current != true)
                {
                    _logger.LogInformation(
                        "Not resending the pull_request delivery {Guid}: the pull request has moved on or is closed.", delivery.Guid);
                    // Settled for good: its head will not become current again.
                    _attempts[delivery.Guid] = (MaxAttempts, now);
                    continue;
                }
            }

            asked++;
            var count = (_attempts.TryGetValue(delivery.Guid, out var tried) ? tried.Count : 0) + 1;
            _attempts[delivery.Guid] = (count, now);
            _resentEntries[delivery.Id] = now;
            try
            {
                await github.RedeliverHookDeliveryAsync(delivery.Id, ct).ConfigureAwait(false);
                resent++;
                _logger.LogInformation(
                    "Asked GitHub to resend a {Event} delivery ({Guid}) we answered {Status} at {DeliveredAt:O} (attempt {Attempt} of {MaxAttempts}).",
                    delivery.Event, delivery.Guid, delivery.StatusCode, delivery.DeliveredAt, count, MaxAttempts);
            }
            catch (GitHubApiException ex)
            {
                // One delivery GitHub will not resend (too old, say) is not the others'.
                _logger.LogWarning(ex,
                    "GitHub would not resend the {Event} delivery {Guid} ({DeliveryId}).",
                    delivery.Event, delivery.Guid, delivery.Id);
            }
        }

        // The next sweep only reads back to here, so it moves on only when this one
        // read the whole window and asked for everything it found. Otherwise the
        // next sweep reads the same window again.
        if (complete && !leftOver)
        {
            _lastSweepAt = now;
        }
        Prune(oldest);
        return resent;
    }

    /// <summary>The pull-request actions the endpoint builds on.</summary>
    private static readonly HashSet<string> PullRequestBuildActions =
        new(StringComparer.OrdinalIgnoreCase) { "opened", "synchronize", "reopened" };

    /// <summary>
    /// The deliveries the endpoint acts on: every push, the pull-request actions it
    /// builds, and <c>closed</c> (a merge is recorded). A label, an edit or a review
    /// request is answered 204, so resending one would spend a call on nothing.
    /// </summary>
    private static bool IsResentEvent(GitHubHookDelivery delivery) =>
        string.Equals(delivery.Event, "push", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(delivery.Event, "pull_request", StringComparison.OrdinalIgnoreCase)
            && (IsPullRequestBuild(delivery) || string.Equals(delivery.Action, "closed", StringComparison.OrdinalIgnoreCase)));

    private static bool IsPullRequestBuild(GitHubHookDelivery delivery) =>
        string.Equals(delivery.Event, "pull_request", StringComparison.OrdinalIgnoreCase)
        && delivery.Action is { } action && PullRequestBuildActions.Contains(action);

    /// <summary>
    /// Whether the head <paramref name="delivery"/> carried is still the open pull
    /// request's head on GitHub; null when the delivery or the pull request is gone.
    /// </summary>
    private static async Task<bool?> IsStillThePullRequestHeadAsync(
        GitHubAppClient github, GitHubHookDelivery delivery, CancellationToken ct)
    {
        var sent = await github.GetHookPullRequestDeliveryAsync(delivery.Id, ct).ConfigureAwait(false);
        if (sent?.RepositoryFullName.Split('/') is not [var owner, var repo]) return null;
        var token = await github.GetInstallationTokenAsync(sent.InstallationId, ct).ConfigureAwait(false);
        var now = await github.GetPullRequestHeadAsync(token, owner, repo, sent.Number, ct).ConfigureAwait(false);
        if (now is null) return null;
        return now.IsOpen && string.Equals(now.HeadSha, sent.HeadSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a delivery answered <paramref name="statusCode"/> could get through
    /// if sent again: no answer at all, a server error, a timeout, rate limiting, or
    /// a body over the size cap (a later version may take it). Any other 4xx is a
    /// delivery we read and turned away on purpose, and would be turned away again.
    /// </summary>
    internal static bool IsWorthResending(int statusCode) =>
        statusCode is 0 or 408 or 413 or 429 || statusCode >= 500;

    private void Prune(DateTime oldest)
    {
        foreach (var guid in _attempts.Where(a => a.Value.LastAt < oldest).Select(a => a.Key).ToList())
        {
            _attempts.Remove(guid);
        }
        foreach (var id in _resentEntries.Where(e => e.Value < oldest).Select(e => e.Key).ToList())
        {
            _resentEntries.Remove(id);
        }
    }

    /// <summary>
    /// Fails every pull-request build an earlier process left queued or building,
    /// and completes its check run as neutral. Returns how many builds were closed.
    /// Internal so a test can drive it without the poll loop.
    /// </summary>
    internal async Task<int> CloseOrphanedPullRequestBuildsAsync(CancellationToken ct)
    {
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // Which organisations exist: organizations carries no tenant filter,
            // so this needs no bypass.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        var closed = 0;
        foreach (var (orgId, isSystem) in orgs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var ambient = AmbientOrganizationScope.Enter(
                    AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
                await using var scope = _services.CreateAsyncScope();
                closed += await CloseOrphansInCurrentOrganizationAsync(scope.ServiceProvider, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not close interrupted pull-request builds for organisation {OrgId}.", orgId);
            }
        }
        return closed;
    }

    private async Task<int> CloseOrphansInCurrentOrganizationAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var orphans = await db.OeProjectBuilds
            .Where(b => b.Trigger == ProjectBuildTrigger.PullRequest
                        && (b.Status == ProjectBuildStatus.Queued || b.Status == ProjectBuildStatus.Building)
                        && b.StartedAt < _startedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        if (orphans.Count == 0) return 0;

        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var build in orphans)
        {
            build.Status = ProjectBuildStatus.Failed;
            build.FailureMessage = RestartedMessage;
            build.FinishedAt = now;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogWarning(
            "Closed {Count} pull-request build(s) a restart interrupted in organisation {OrgId}.",
            orphans.Count, orphans[0].OrganizationId);

        var withRuns = orphans.Where(b => b.CheckRunId is not null).ToList();
        if (withRuns.Count == 0) return orphans.Count;

        var status = await services.GetRequiredService<GitHubConnectionService>().GetStatusAsync(ct).ConfigureAwait(false);
        if (status.InstallationId is not long installationId)
        {
            _logger.LogInformation(
                "Left {Count} check run(s) open: organisation {OrgId} no longer has GitHub connected.",
                withRuns.Count, orphans[0].OrganizationId);
            return orphans.Count;
        }

        var projectIds = withRuns.Select(b => b.ProjectId).Distinct().ToList();
        var repositories = await db.OeProjectRepositories.AsNoTracking()
            .Where(r => projectIds.Contains(r.ProjectId))
            .Select(r => new { r.Id, r.ProjectId, r.Url })
            .ToListAsync(ct).ConfigureAwait(false);

        var checks = services.GetRequiredService<GitHubCheckRunService>();
        foreach (var build in withRuns)
        {
            // The repository the pull request is on. Builds from before that was
            // recorded fall back to the solution's only repository; with several,
            // there is no telling which one holds the run.
            var url = build.HeadRepositoryId is { } repositoryId
                ? repositories.FirstOrDefault(r => r.Id == repositoryId)?.Url
                : repositories.Where(r => r.ProjectId == build.ProjectId).ToList() is { Count: 1 } only ? only[0].Url : null;
            if (DependencyDriftService.ToFullName(url) is not { } fullName)
            {
                _logger.LogInformation(
                    "Left the check run of interrupted build {BuildId} open: its repository is not known.", build.Id);
                continue;
            }
            await checks.AbandonAsync(
                installationId, fullName, build.CheckRunId!.Value, RestartedMessage, ct,
                title: "The build was interrupted").ConfigureAwait(false);
        }
        return orphans.Count;
    }
}
