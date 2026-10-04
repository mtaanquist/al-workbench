namespace ALDevToolbox.Domain.Entities;

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
