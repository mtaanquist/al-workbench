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
    /// A change someone scheduled on an environment ran, failed, or could not be confirmed;
    /// or an environment they are to check on a planned upgrade is on the target version.
    /// </summary>
    Upgrades,
    /// <summary>Business Central schedules or moves an update for a solution the person follows.</summary>
    Solutions,
}

/// <summary>
/// The one place a <see cref="NotificationCategory"/> gets its visible name, so the
/// account page and the digest email headings cannot drift apart.
/// </summary>
public static class NotificationCategories
{
    /// <summary>The category's name as people read it: a settings row, a digest heading.</summary>
    public static string Label(NotificationCategory category) => category switch
    {
        NotificationCategory.Builds => "Builds",
        NotificationCategory.Deployments => "Deployments",
        NotificationCategory.Upgrades => "Upgrades",
        NotificationCategory.Solutions => "Solutions you follow",
        _ => category.ToString(),
    };
}
