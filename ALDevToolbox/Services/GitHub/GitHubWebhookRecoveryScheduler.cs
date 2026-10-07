using System.Net;
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
    /// can log more than this in that time, so the newest are read first and the
    /// rest over the following sweeps, each picking up where the last stopped.
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

    // When the last sweep that read the newest stretch of the log down to its
    // floor began: the next one reads back only to here, less the overlap.
    private DateTime? _readThrough;

    // Older stretches a sweep's page budget did not reach, newest first: the
    // cursor to resume from, how far back the log had been read when it stopped,
    // and how far back the stretch runs.
    private readonly List<(string Cursor, DateTime ReachedBack, DateTime Floor)> _gaps = new();

    // Events seen to have got through (guid, when seen), so a failure read in a
    // later sweep than its success is still recognised as delivered.
    private readonly Dictionary<string, DateTime> _delivered = new(StringComparer.Ordinal);

    // Failed entries a sweep read but did not act on (no room in the queue, or
    // GitHub was struggling), carried to the next sweep by id.
    private readonly Dictionary<long, GitHubHookDelivery> _pending = new();

    // Per event: how many resends GitHub could not take (a 5xx, a timeout, rate
    // limiting), and when it last refused. Such an event is asked for after the
    // others, so one delivery GitHub keeps failing on cannot hold the rest up.
    private readonly Dictionary<string, (int Count, DateTime LastAt)> _refusals = new(StringComparer.Ordinal);

    // Per event (the delivery guid GitHub keeps across redeliveries): how many
    // times this process asked for it, and when it last did, so the map can be
    // pruned past the lookback.
    private readonly Dictionary<string, (int Count, DateTime LastAt)> _attempts = new(StringComparer.Ordinal);

    // Log entries GitHub took a resend of. A sweep's overlap reads the same failed
    // entry twice; the second read must not ask again before the first resend is logged.
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
        var github = scope.ServiceProvider.GetRequiredService<GitHubAppClient>();
        var log = await ReadLogAsync(github, now, oldest, ct).ConfigureAwait(false);

        // An event got through when any of its attempts did. Remembered across
        // sweeps: the success and the failure it answers can be read sweeps apart.
        foreach (var delivery in log.Where(d => d.Succeeded))
        {
            _delivered[delivery.Guid] = now;
        }

        var due = log
            .Where(d => IsResentEvent(d) && IsWorthResending(d.StatusCode))
            // What an earlier sweep found but did not get to.
            .Concat(_pending.Values)
            .Where(d => d.DeliveredAt >= oldest && !_delivered.ContainsKey(d.Guid))
            .DistinctBy(d => d.Id)
            .GroupBy(d => d.Guid, StringComparer.Ordinal)
            // GitHub resends the same payload whichever attempt is named, so the
            // newest one stands for the event, ordered by when the event first
            // failed: GitHub handles each resend on its own, so this is the order
            // they are asked for in, not a promise about arrival.
            .Select(g => (Delivery: g.MaxBy(d => d.DeliveredAt)!, FirstAt: g.Min(d => d.DeliveredAt)))
            .Where(e => !_resentEntries.ContainsKey(e.Delivery.Id))
            .Where(e => !_attempts.TryGetValue(e.Delivery.Guid, out var tried) || tried.Count < MaxAttempts)
            // An event GitHub kept failing on goes behind the ones it has not, so a
            // single bad delivery cannot hold up every other resend.
            .OrderBy(e => _refusals.TryGetValue(e.Delivery.Guid, out var refused) ? refused.Count : 0)
            .ThenBy(e => e.FirstAt)
            .Select(e => e.Delivery)
            .ToList();

        // Only as many as the queue has room for: a resend refused again spends
        // one of the event's attempts for nothing. The rest wait for the next sweep,
        // carried by id rather than by reading their part of the log again. The
        // carried set is rebuilt here and swapped in at the end, so nothing in it is
        // lost if the loop is cut short.
        var room = Math.Max(0, GitHubWebhookQueue.Capacity / 2 - _queue.Backlog);
        var carried = new Dictionary<long, GitHubHookDelivery>();
        var resent = 0;
        var asked = 0;
        var refusedThisSweep = 0;
        var stop = false;
        var next = 0;
        try
        {
            for (; next < due.Count; next++)
            {
                var delivery = due[next];
                if (asked >= room || stop)
                {
                    carried[delivery.Id] = delivery;
                    continue;
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
                        carried[delivery.Id] = delivery;
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
                var refusal = await TryRedeliverAsync(github, delivery, ct).ConfigureAwait(false);
                switch (refusal)
                {
                    case null:
                        // Counted only once GitHub took it: the new attempt is in its log
                        // now, and this entry must not be asked for again before it shows.
                        var count = (_attempts.TryGetValue(delivery.Guid, out var tried) ? tried.Count : 0) + 1;
                        _attempts[delivery.Guid] = (count, now);
                        _resentEntries[delivery.Id] = now;
                        _refusals.Remove(delivery.Guid);
                        resent++;
                        _logger.LogInformation(
                            "Asked GitHub to resend a {Event} delivery ({Guid}) we answered {Status} at {DeliveredAt:O} (attempt {Attempt} of {MaxAttempts}).",
                            delivery.Event, delivery.Guid, delivery.StatusCode, delivery.DeliveredAt, count, MaxAttempts);
                        break;

                    case Refusal.Settled:
                        // GitHub will not resend this one (gone, or older than it keeps),
                        // and asking again will not change that.
                        _attempts[delivery.Guid] = (MaxAttempts, now);
                        _refusals.Remove(delivery.Guid);
                        break;

                    case Refusal.Systemic:
                        // The App itself was turned away, so every other resend would be
                        // too. No attempt was made, so none is counted.
                        carried[delivery.Id] = delivery;
                        stop = true;
                        break;

                    default:
                        // GitHub could not take it this time. Not counted as an attempt;
                        // it goes behind the others next sweep. One such answer can be
                        // about this delivery; a second, on another, says GitHub is
                        // struggling, and the rest wait for the next sweep.
                        carried[delivery.Id] = delivery;
                        _refusals[delivery.Guid] = (
                            (_refusals.TryGetValue(delivery.Guid, out var refused) ? refused.Count : 0) + 1, now);
                        stop = ++refusedThisSweep >= 2;
                        break;
                }
            }
        }
        finally
        {
            // Whatever the loop did not reach (cancelled, or an exception nobody
            // expected) is carried too, including the one it was working on.
            for (; next < due.Count; next++)
            {
                carried.TryAdd(due[next].Id, due[next]);
            }
            _pending.Clear();
            foreach (var (id, delivery) in carried) _pending[id] = delivery;
        }

        Prune(oldest);
        return resent;
    }

    /// <summary>How GitHub turned a resend or a log read away.</summary>
    public enum Refusal
    {
        /// <summary>About GitHub, not the delivery: a server error, a timeout, rate limiting or no answer. Worth asking again.</summary>
        Transient,

        /// <summary>About the App itself (a 401, or a 403 that is not rate limiting): every other call would be turned away too.</summary>
        Systemic,

        /// <summary>About this delivery or cursor, for good: gone, or older than GitHub keeps.</summary>
        Settled,
    }

    /// <summary>
    /// Asks GitHub to resend <paramref name="delivery"/>; null when it took it,
    /// otherwise how it refused.
    /// </summary>
    private async Task<Refusal?> TryRedeliverAsync(GitHubAppClient github, GitHubHookDelivery delivery, CancellationToken ct)
    {
        try
        {
            await github.RedeliverHookDeliveryAsync(delivery.Id, ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException or GitHubAppNotConfiguredException)
        {
            var refusal = ex switch
            {
                GitHubApiException api => ClassifyResendRefusal(api.StatusCode, api.Message),
                // The App registration went away mid-sweep: nothing else will get through either.
                GitHubAppNotConfiguredException => Refusal.Systemic,
                _ => Refusal.Transient,
            };
            _logger.LogWarning(ex,
                refusal == Refusal.Settled
                    ? "GitHub would not resend the {Event} delivery {Guid} ({DeliveryId}); not asking for it again."
                    : "GitHub could not take the resend of the {Event} delivery {Guid} ({DeliveryId}); asking again next time.",
                delivery.Event, delivery.Guid, delivery.Id);
            return refusal;
        }
    }

    /// <summary>
    /// How GitHub's answer to a resend is taken. Only a delivery GitHub no longer
    /// has (404, 410) or will not resend (422, older than it keeps) is settled; a
    /// 401 or a 403 that is not rate limiting is the App being turned away, and
    /// anything else is worth asking again later.
    /// </summary>
    internal static Refusal ClassifyResendRefusal(HttpStatusCode status, string message) =>
        status switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.UnprocessableEntity => Refusal.Settled,
            _ when IsAppRefused(status, message) => Refusal.Systemic,
            _ => Refusal.Transient,
        };

    /// <summary>
    /// How GitHub's refusal of a delivery-log page is taken. Only a 400, 404 or
    /// 422 says the cursor itself is no good, and gives its stretch up; anything
    /// else keeps the stretch to read again next sweep.
    /// </summary>
    internal static Refusal ClassifyLogRefusal(HttpStatusCode status, string message) =>
        status switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => Refusal.Settled,
            _ when IsAppRefused(status, message) => Refusal.Systemic,
            _ => Refusal.Transient,
        };

    private static bool IsAppRefused(HttpStatusCode status, string message) =>
        status == HttpStatusCode.Unauthorized
        || (status == HttpStatusCode.Forbidden && !message.Contains("rate limit", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads the part of the log no earlier sweep has: the newest stretch, down to
    /// where the last complete read of it began, then whatever older stretch a page
    /// budget left unread, resumed from where it stopped. At most
    /// <see cref="MaxPages"/> pages in all, so a busy log is read over several
    /// sweeps rather than the whole window being read again every five minutes.
    /// </summary>
    private async Task<List<GitHubHookDelivery>> ReadLogAsync(
        GitHubAppClient github, DateTime now, DateTime oldest, CancellationToken ct)
    {
        var log = new List<GitHubHookDelivery>();
        var floor = _readThrough is { } through && through - Overlap > oldest ? through - Overlap : oldest;
        var top = await ReadStretchAsync(github, cursor: null, floor, MaxPages, isGap: false, log, ct).ConfigureAwait(false);
        if (top.Failed)
        {
            // Nothing moves: the next sweep reads the same stretch again.
            return log;
        }
        if (top.StoppedAt is { } stoppedAt)
        {
            _gaps.Insert(0, (stoppedAt, top.ReachedBack, floor));
            _logger.LogInformation(
                "Read {Pages} pages of GitHub's webhook delivery log without reaching {Floor:O}; the rest is read over the next sweeps.",
                top.Pages, floor);
        }
        _readThrough = now;

        // A stretch whose cursor is already past the window holds nothing GitHub
        // would resend any more.
        _gaps.RemoveAll(g => g.ReachedBack < oldest);
        var pagesLeft = MaxPages - top.Pages;
        while (pagesLeft > 0 && _gaps.Count > 0)
        {
            var (cursor, _, gapFloor) = _gaps[0];
            var gap = await ReadStretchAsync(
                github, cursor, gapFloor > oldest ? gapFloor : oldest, pagesLeft, isGap: true, log, ct).ConfigureAwait(false);
            pagesLeft -= gap.Pages;
            if (gap.StoppedAt is { } next)
            {
                // Read part of the way (or failed partway): pick up from here next time.
                if (gap.Pages > 0) _gaps[0] = (next, gap.ReachedBack, gapFloor);
            }
            else
            {
                _gaps.RemoveAt(0);
            }
            if (gap.Failed) break;
        }
        if (pagesLeft <= 0 && _gaps.Count > 0)
        {
            _logger.LogWarning(
                "GitHub's webhook delivery log has {Gaps} older stretch(es) still unread back to {Floor:O}; failures in them are resent once the next sweeps reach them.",
                _gaps.Count, _gaps.Min(g => g.Floor));
        }
        return log;
    }

    /// <param name="Pages">How many pages were read.</param>
    /// <param name="StoppedAt">The cursor of the next page to read when the page budget ran out or a read failed; null when the stretch was read to its floor or given up.</param>
    /// <param name="ReachedBack">The oldest delivery on the last page read.</param>
    /// <param name="Failed">GitHub could not be read; what was read before that is kept.</param>
    private readonly record struct StretchRead(int Pages, string? StoppedAt, DateTime ReachedBack, bool Failed);

    /// <summary>
    /// Reads log pages from <paramref name="cursor"/> (null for the newest) until one
    /// reaches past <paramref name="floor"/>, the log ends, or <paramref name="maxPages"/>
    /// are read, adding every delivery at or after the floor to <paramref name="log"/>.
    /// <paramref name="isGap"/> says it resumes an older stretch from a saved cursor.
    /// </summary>
    private async Task<StretchRead> ReadStretchAsync(
        GitHubAppClient github, string? cursor, DateTime floor, int maxPages, bool isGap,
        List<GitHubHookDelivery> log, CancellationToken ct)
    {
        var pages = 0;
        var reachedBack = DateTime.MaxValue;
        while (pages < maxPages)
        {
            GitHubHookDeliveryPage page;
            try
            {
                page = await github.ListHookDeliveriesAsync(cursor, ct).ConfigureAwait(false);
            }
            catch (GitHubApiException ex) when (isGap && ClassifyLogRefusal(ex.StatusCode, ex.Message) == Refusal.Settled)
            {
                // GitHub no longer takes this cursor, so the stretch behind it cannot
                // be reached; give it up rather than ask for it every sweep.
                _logger.LogWarning(ex, "GitHub refused a page of its webhook delivery log; that stretch is skipped.");
                return new StretchRead(pages, null, reachedBack, Failed: false);
            }
            catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
            {
                // Anything else (GitHub struggling, or the App turned away) says
                // nothing about the cursor: the stretch is kept for the next sweep.
                _logger.LogWarning(ex, "Could not read GitHub's webhook delivery log; reading it again next time.");
                return new StretchRead(pages, cursor, reachedBack, Failed: true);
            }
            pages++;
            log.AddRange(page.Deliveries.Where(d => d.DeliveredAt >= floor));
            if (page.Deliveries.Count > 0) reachedBack = page.Deliveries.Min(d => d.DeliveredAt);
            if (page.NextCursor is null || page.Deliveries.Count == 0 || reachedBack < floor)
            {
                return new StretchRead(pages, null, reachedBack, Failed: false);
            }
            cursor = page.NextCursor;
        }
        return new StretchRead(pages, cursor, reachedBack, Failed: false);
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
    /// a body over the size cap (a later version may take it). A 403 is the endpoint
    /// refusing the sender's address (#1201); every entry in the App's log was sent by
    /// GitHub itself, so a 403 there is a proxy missing from <c>TRUSTED_PROXIES</c> or
    /// an address list older than GitHub's, and a resend after the fix gets through.
    /// Any other 4xx is a delivery we read and turned away on purpose, and would be
    /// turned away again.
    /// </summary>
    internal static bool IsWorthResending(int statusCode) =>
        statusCode is 0 or 403 or 408 or 413 or 429 || statusCode >= 500;

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
        foreach (var guid in _delivered.Where(d => d.Value < oldest).Select(d => d.Key).ToList())
        {
            _delivered.Remove(guid);
        }
        foreach (var guid in _refusals.Where(r => r.Value.LastAt < oldest).Select(r => r.Key).ToList())
        {
            _refusals.Remove(guid);
        }
    }

    /// <summary>
    /// Fails every pull-request build an earlier process left queued or building,
    /// and completes its check run as neutral. Returns how many builds were closed.
    /// Internal so a test can drive it without the poll loop.
    /// </summary>
    internal async Task<int> CloseOrphanedPullRequestBuildsAsync(CancellationToken ct)
    {
        var orgs = await SweptOrganizations.ListAsync(_services, ct).ConfigureAwait(false);

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
