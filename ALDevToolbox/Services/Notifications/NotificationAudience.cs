using ALDevToolbox.Domain.Entities.ObjectExplorer;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Which of a solution's followers hear about its builds and its deployments, by their
/// roles on its People list (#1463). A follower with no role, not being on the list,
/// hears about both. See <c>.design/notifications.md</c>.
/// </summary>
public static class NotificationAudience
{
    /// <summary>Builds and nightly checks starting to fail or working again.</summary>
    public static readonly IReadOnlyCollection<ProjectPersonRole> Builds =
        [ProjectPersonRole.Developer, ProjectPersonRole.Architect];

    /// <summary>Deployments waiting for approval, and how they ended.</summary>
    public static readonly IReadOnlyCollection<ProjectPersonRole> Deployments =
        [ProjectPersonRole.ProjectLeader, ProjectPersonRole.Consultant];

    /// <summary>
    /// The person who did the work, if anyone did, and the followers it concerns. When
    /// that leaves nobody (everyone with the role unfollowed, or nobody holds it) the
    /// solution's owner is told, so a failure is never announced to no one.
    /// </summary>
    internal static List<int> Recipients(int? actor, IEnumerable<int> followers, int? owner)
    {
        IEnumerable<int?> candidates = [actor, .. followers.Select(f => (int?)f)];
        var recipients = candidates.OfType<int>().Distinct().ToList();
        if (recipients.Count == 0 && owner is { } fallback) recipients.Add(fallback);
        return recipients;
    }
}
