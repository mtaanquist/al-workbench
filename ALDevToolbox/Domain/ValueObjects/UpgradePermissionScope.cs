namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Where a member's "Can schedule upgrades" grant (<see cref="Entities.TeamMember.ManagesUpdates"/>)
/// counts. Stored per org on <see cref="Entities.OrganizationSettings.UpgradePermissionScope"/>
/// as its name, so a new level is a new member here plus a case in
/// <c>ProjectAccess.CanManageEnvironmentUpdatesAsync</c> and
/// <c>ProjectAccess.UpdateOpsProjectPredicate</c>; both switch over every member and
/// throw on one they don't know. Org Admins and SiteAdmins may act everywhere at every
/// level. See <c>.design/teams-and-visibility.md</c>, "The environment-update grant".
/// </summary>
public enum UpgradePermissionScope
{
    /// <summary>
    /// The grant counts only on the solutions its own team is assigned to. A Public
    /// solution has no teams, so only admins run its upgrades. The default.
    /// </summary>
    AssignedTeams,

    /// <summary>
    /// Holding the grant on any team lets the member run upgrades on every solution
    /// they may manage: every Public solution, and the Read-only and Private ones
    /// whose teams they are on. For an organisation small enough that the team
    /// assignments don't follow who does upgrades.
    /// </summary>
    ManagedSolutions,
}
