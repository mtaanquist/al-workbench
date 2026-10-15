using ALDevToolbox.Domain.Entities.ObjectExplorer;
using System.Linq.Expressions;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Predicates over <see cref="OeProjectEnvironment"/> that more than one query needs to
/// agree on. They live here rather than being retyped per query because getting one of
/// them subtly wrong is invisible: the page still renders, it just lists an environment
/// nobody can act on.
/// </summary>
public static class EnvironmentQueries
{
    /// <summary>
    /// An environment the customer has not deleted. Both signals are checked because
    /// either can arrive first, and the status compare is case-insensitive since
    /// Microsoft's casing is stored verbatim — the same reading as
    /// <see cref="BcEnvironmentStatus.IsSoftDeleted"/>, written as an expression because
    /// a method call cannot cross into SQL.
    /// <para>
    /// A deleted environment cannot be published to, updated, rescheduled or read for its
    /// installed apps, so every query that answers "which environment do we work with"
    /// starts from this. See <c>.design/environment-updates.md</c>, "Deleted environments".
    /// </para>
    /// </summary>
    public static readonly Expression<Func<OeProjectEnvironment, bool>> NotSoftDeleted =
        e => e.SoftDeletedOn == null && (e.Status == null || e.Status.ToUpper() != "SOFTDELETED");

    /// <summary>
    /// An environment in Business Central online, the only kind Microsoft's platform
    /// updates, update notices, deferred installs and Admin Center reads apply to. Every
    /// reader that is about those leaves linked on-premises service tiers out with it.
    /// See <c>.design/on-prem-gateway.md</c>, "Readers of OeProjectEnvironments".
    /// </summary>
    public static readonly Expression<Func<OeProjectEnvironment, bool>> Online =
        e => e.Kind == OeEnvironmentKind.Online;

    /// <summary>
    /// The rows that hold their name in a Solution - exactly the filter of the unique
    /// (project_id, name) index. An unlinked on-premises tier keeps its row but gives up
    /// its name, so a lookup by name must leave it out or it could pick the wrong one of
    /// two rows called "Production".
    /// </summary>
    public static readonly Expression<Func<OeProjectEnvironment, bool>> HoldsName =
        e => e.MissingSince == null || e.Kind == OeEnvironmentKind.Online;

    /// <summary>
    /// An online environment, or a linked on-premises service tier whose gateway still
    /// reports both the tier and its tenant. A tier the gateway stopped reporting keeps
    /// its last facts for the environment page to show as such, but they are no longer
    /// the customer's current version or modules. Pair it with <c>MissingSince == null</c>.
    /// </summary>
    public static readonly Expression<Func<OeProjectEnvironment, bool>> StillReported =
        e => e.Kind == OeEnvironmentKind.Online
             || (e.GatewayInstance != null
                 && e.GatewayInstance.MissingSince == null
                 && !e.GatewayInstance.Tenants.Any(t => t.TenantId == e.TenantId && t.MissingSince != null));
}
