using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Projects;

/// <summary>
/// In-process hand-off from a request (the pipeline editor's Refresh, or a
/// repo-change on a project) to <see cref="ProjectDiscoveryWorker"/>, which warms
/// the per-project discovered-extensions cache off the request thread. A small
/// bounded <see cref="System.Threading.Channels.Channel{T}"/> — not an external queue — keeps the "no
/// external services" fence intact, mirroring <see cref="ReleaseImportQueue"/>.
///
/// <para>
/// In-flight state is tracked <em>in memory only</em> (an
/// in-memory set of project ids), never
/// persisted — a discovery is a disposable cache warm, so a restart simply drops
/// any queued/running entry rather than stranding a "discovering" flag. The flag
/// also dedupes: enqueuing a project that's already queued/running is a no-op, so
/// several pipeline editors refreshing the same project coalesce into one clone.
/// A request for a project whose discovery is already running is held and run once
/// it finishes, so a repository added mid-run is not missed.
/// </para>
/// </summary>
public sealed class ProjectDiscoveryQueue : JobQueue<ProjectDiscoveryJob, int>
{
    // A modest bound: discovery is cheap (a blobless app.json clone) and the
    // worker processes one project at a time, so a deep backlog isn't expected.
    // A request that lands while the solution's discovery is running is about a
    // repository list the running clone may already have read, so it runs again
    // afterwards rather than being folded in (#1263).
    // Given the maintenance flag (production DI always does), a discovery queued
    // before a restore replaced the database is dropped rather than run (#1538).
    public ProjectDiscoveryQueue(Operations.MaintenanceModeState? maintenance = null)
        : base(capacity: 64, keySelector: job => job.ProjectId, rerunIfRequestedWhileRunning: true, maintenance: maintenance) { }
}

/// <summary>
/// A queued cache-warming discovery for one project, run by
/// <see cref="ProjectDiscoveryWorker"/> under the requesting user's captured
/// <see cref="AmbientOrganizationScope.OrganizationIdentity">identity</see> — required
/// because <see cref="Account.UserRepositoryTokenService"/> resolves the repo PAT for
/// the acting user off-request.
/// </summary>
public sealed record ProjectDiscoveryJob(int ProjectId, AmbientOrganizationScope.OrganizationIdentity Identity);
