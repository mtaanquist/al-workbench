namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A notification as the recipient sees it in the app: one row per person,
/// listed on their Notifications page and counted in the header until read.
/// Written whatever their email choice, unless they turned the category's
/// In app setting off, and deleted after 30 days. See <c>.design/notifications.md</c>.
/// </summary>
public class UserNotification
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public NotificationCategory Category { get; set; }

    /// <summary>One line, e.g. "Build failed: CRONUS Coffee - Main".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional second line, e.g. the first line of a failure message.</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// The page the notification is about, as a path within the app (no host),
    /// so it works without PUBLIC_BASE_URL and survives a change of address.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>The solution the notification belongs to. Null when it belongs to none.</summary>
    public string? SolutionName { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the person opened it or marked it read. Null while unread.</summary>
    public DateTime? ReadAt { get; set; }
}
