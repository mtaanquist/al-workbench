namespace ALDevToolbox.Domain.Entities;

/// <summary>When a person is emailed about one <see cref="NotificationCategory"/>.</summary>
public enum NotificationDelivery
{
    Immediately,
    Daily,
    Weekly,
    Off,
}
