namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// One person's choice for one category. A missing row means
/// <see cref="Services.Notifications.NotificationPreferenceService.DefaultDeliveryFor"/>
/// and shown in the app, so a category added later starts at its default for
/// everyone without a backfill.
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
