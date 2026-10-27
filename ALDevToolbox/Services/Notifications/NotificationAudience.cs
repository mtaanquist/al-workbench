using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

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
    /// The people who did the work, if anyone did, and the followers it concerns. When
    /// that leaves nobody (everyone with the role unfollowed, or nobody holds it) the
    /// solution's owner is told, so a failure is never announced to no one. The people
    /// who did the work are checked the way followers already are, active and still able
    /// to see the solution, before deciding that, since the notification would drop them
    /// later and leave nobody.
    /// </summary>
    internal static async Task<List<int>> RecipientsAsync(
        AppDbContext db, int projectId, IEnumerable<int?> involved, IReadOnlyCollection<int> followers, int? owner,
        CancellationToken ct)
    {
        var people = involved.OfType<int>().Except(followers).Distinct().ToList();
        var active = people.Count == 0 ? [] : await db.Users.AsNoTracking()
            .Where(u => people.Contains(u.Id) && u.Status == UserStatus.Active)
            .Select(u => u.Id)
            .ToListAsync(ct);
        var reachable = await ProjectAccess.WhoCanViewAsync(db, projectId, active, ct);
        var recipients = reachable.Concat(followers).Distinct().ToList();
        if (recipients.Count == 0 && owner is { } fallback) recipients.Add(fallback);
        return recipients;
    }
}
