namespace ALDevToolbox.Domain.ValueObjects;

/// <summary>
/// Thrown when the acting user isn't allowed to act on a project: to manage it (the
/// owner, an org Admin or SiteAdmin, an assigned team, or anyone when it is Public),
/// to see a Private one, or to use one of the narrower axes in <c>ProjectAccess</c>.
/// Gates the mutate/build/delete paths in the service layer (the source of truth);
/// the UI hides those affordances too, so this is defense-in-depth rather than the
/// primary guard. See <c>.design/teams-and-visibility.md</c>.
/// </summary>
public sealed class ProjectAccessDeniedException : Exception
{
    /// <summary>
    /// What the manage check says when it refuses. Shown to the person, so it names a
    /// Solution, and stays true at every level: it does not claim only the owner may act,
    /// because on a Read-only or Private solution its teams may too.
    /// </summary>
    public const string DefaultMessage =
        "You don't have permission to change this solution. Ask its owner, a member of its team or an administrator.";

    public ProjectAccessDeniedException(string message) : base(message)
    {
    }

    public ProjectAccessDeniedException()
        : base(DefaultMessage)
    {
    }
}
