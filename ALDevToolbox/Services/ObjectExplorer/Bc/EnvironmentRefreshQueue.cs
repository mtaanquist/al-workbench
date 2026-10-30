using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// In-process hand-off of "re-read this project's Business Central environments and
/// re-mirror their next platform update" to <see cref="EnvironmentRefreshWorker"/>. Fed
/// by the nightly <see cref="EnvironmentRefreshScheduler"/> sweep and by an on-demand
/// refresh from a page. A small bounded <see cref="System.Threading.Channels.Channel{T}"/> — not an external queue
/// — keeps the "no external services" fence intact, mirroring
/// <see cref="ProjectDiscoveryQueue"/>.
///
/// <para>
/// In-flight state is tracked <em>in memory only</em> (project ids), never persisted: a
/// refresh is a disposable cache warm, so a restart drops any queued entry rather than
/// stranding a flag, and the next nightly sweep picks the project up again. The flag also
/// dedupes — a sweep enqueuing a project someone just refreshed by hand coalesces into
/// the one already running rather than making the same round trips twice.
/// </para>
/// See <c>.design/saas-delivery.md</c> and issue #657.
/// </summary>
public sealed class EnvironmentRefreshQueue : JobQueue<EnvironmentRefreshJob, int>
{
    // A nightly sweep offers every BC-connected project at once, so the bound is set for
    // a fleet rather than for a single page action; the worker drains one at a time and a
    // full channel simply makes the scheduler wait.
    public EnvironmentRefreshQueue() : base(capacity: 256, keySelector: job => job.ProjectId) { }

    // Projects Business Central has asked us to leave alone until a given time (UTC),
    // from the Retry-After of a throttled read. In memory like the dedupe gate: a restart
    // forgets it, and the worst that costs is one more throttled answer.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, DateTime> _deferredUntil = new();

    /// <summary>
    /// Records that Business Central asked for no more requests about this project before
    /// <paramref name="untilUtc"/>. A later, shorter answer never shortens a longer wait
    /// already on record.
    /// </summary>
    public void DeferUntil(int projectId, DateTime untilUtc) =>
        _deferredUntil.AddOrUpdate(projectId, untilUtc, (_, existing) => existing > untilUtc ? existing : untilUtc);

    /// <summary>
    /// Until when the scheduled sweep should leave this project alone, or null when it is
    /// free to ask. A wait that has passed is forgotten here.
    /// </summary>
    public DateTime? DeferredUntil(int projectId, DateTime nowUtc)
    {
        if (!_deferredUntil.TryGetValue(projectId, out var until)) return null;
        if (until > nowUtc) return until;
        _deferredUntil.TryRemove(new KeyValuePair<int, DateTime>(projectId, until));
        return null;
    }
}

/// <summary>
/// A queued environment refresh for one project, run by
/// <see cref="EnvironmentRefreshWorker"/> under the captured
/// <see cref="AmbientOrganizationScope.OrganizationIdentity">identity</see> — required so
/// the EF query filter scopes the work to the project's own organisation off-request.
/// </summary>
public sealed record EnvironmentRefreshJob(int ProjectId, AmbientOrganizationScope.OrganizationIdentity Identity);
