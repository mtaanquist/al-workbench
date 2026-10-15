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
}
