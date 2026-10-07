using ALDevToolbox.Services.ObjectExplorer.Import;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// In-process hand-off from a request (the "Deploy now" action) to
/// <see cref="DeliveryWorker"/>, which runs the BC publish off the request thread. A
/// small bounded <see cref="System.Threading.Channels.Channel{T}"/> — not an external broker — keeps the "no
/// external services" fence intact, mirroring <see cref="ProjectDiscoveryQueue"/> and
/// <see cref="ReleaseImportQueue"/>. The persisted <c>oe_project_deliveries</c> row is
/// the source of truth; this channel just carries the id + captured identity.
///
/// <para>
/// <see cref="DeliveryScheduler"/> enqueues a delivery when its scheduled time comes
/// (a deploy-now is simply due at once), and on its first sweep fails the rows a
/// restart interrupted. The in-memory dedupe keyed on delivery id stops the scheduler
/// and a double-click from enqueuing the same delivery twice.
/// </para>
/// <para>
/// Every caller uses <see cref="JobQueue{TJob,TKey}.TryEnqueue"/>: a due row stays
/// <c>scheduled</c> in the database, so one that doesn't fit is picked up by the
/// scheduler's next sweep, and neither a request nor the sweep ever waits on a full
/// queue (#1139). Several workers drain it, so it also holds the per-environment gate
/// that keeps two of them from deploying to one environment at once.
/// </para>
/// </summary>
public sealed class DeliveryQueue : JobQueue<DeliveryJob, int>
{
    private readonly KeyedGate<(string Tenant, string Environment)> _environments = new();

    public DeliveryQueue() : base(capacity: 64, keySelector: job => job.DeliveryId, singleReader: false) { }

    /// <summary>
    /// Claims the environment for one deployment, or null when another deployment to it is
    /// running in this process. Disposing the result frees it. The environment is the one in
    /// Business Central: two solutions connected to the same tenant share it, and the name is
    /// matched ignoring case, as the environment list is. A solution with no tenant yet keys
    /// on itself.
    /// </summary>
    public IDisposable? TryEnterEnvironment(Guid? tenantId, int projectId, string environmentName)
    {
        var key = (tenantId?.ToString() ?? $"solution:{projectId}", environmentName.ToUpperInvariant());
        return _environments.TryEnter(key);
    }
}

/// <summary>
/// A queued delivery run, executed by <see cref="DeliveryWorker"/> under the
/// triggering user's captured identity so the EF query filter and credential
/// resolution behave exactly as in the original request.
/// </summary>
public sealed record DeliveryJob(int DeliveryId, AmbientOrganizationScope.OrganizationIdentity Identity);
