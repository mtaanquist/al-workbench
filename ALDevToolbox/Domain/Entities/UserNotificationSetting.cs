namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// The kinds of thing a person can be notified about. Each is one row on the
/// Notifications section of the account page. See <c>.design/notifications.md</c>.
/// </summary>
public enum NotificationCategory
{
    /// <summary>A build pipeline starts failing, or works again after failing.</summary>
    Builds,

    /// <summary>A deployment waits for approval, is deployed, or fails.</summary>
    Deployments,

    /// <summary>
    /// A change someone booked on an environment ran or failed, or an environment
    /// they are to check after its update has been updated.
    /// </summary>
    Upgrades,
}

/// <summary>When a person is emailed about one <see cref="NotificationCategory"/>.</summary>
public enum NotificationDelivery
{
    Immediately,
    Daily,
    Weekly,
    Off,
}

/// <summary>
/// One person's choice for one category. A missing row means
/// <see cref="Services.Notifications.NotificationPreferenceService.DefaultDelivery"/>
/// and shown in the app, so a category added later starts on for everyone
/// without a backfill.
/// </summary>
public class UserNotificationSetting
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public NotificationCategory Category { get; set; }
    /// <summary>When the person is emailed.</summary>
    public NotificationDelivery Delivery { get; set; }

    /// <summary>Whether the notification is also listed on the person's Notifications page.</summary>
    public bool InApp { get; set; } = true;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A notification held back for someone's daily or weekly digest, written
/// instead of an email when they chose a digest for its category. The digest
/// sender reads these, sends one email per person and deletes them.
/// </summary>
public class NotificationDigestItem
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public NotificationCategory Category { get; set; }

    /// <summary><see cref="NotificationDelivery.Daily"/> or <see cref="NotificationDelivery.Weekly"/>, as chosen when the item was stored.</summary>
    public NotificationDelivery Delivery { get; set; }

    /// <summary>One line, e.g. "Build failed: CRONUS Coffee - Main".</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Optional second line, e.g. the first line of a failure message.</summary>
    public string? Detail { get; set; }

    /// <summary>Absolute link to the page the item is about.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The solution the item belongs to, for grouping in the digest. Null when it belongs to none.</summary>
    public string? SolutionName { get; set; }

    public DateTime CreatedAt { get; set; }
}

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
