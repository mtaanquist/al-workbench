using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Email;

namespace ALDevToolbox.Services.Notifications;

/// <summary>Who a notification email is addressed to, for its greeting.</summary>
public sealed record NotificationRecipient(int UserId, string DisplayName, string Email);

/// <summary>
/// The one-line form of a notification, kept for a daily or weekly digest
/// when the recipient chose one instead of an email per event.
/// </summary>
public sealed record NotificationDigestEntry(string Title, string? Detail, string Url, string? SolutionName);

/// <summary>
/// Something happened that people should hear about. The caller decides who
/// (<see cref="RecipientUserIds"/>) and what the email says; the
/// <see cref="NotificationService"/> decides, per recipient, whether it is
/// emailed now, kept for a digest or dropped.
/// </summary>
/// <param name="RenderAsync">Renders the email for one recipient, so it can greet them by name.</param>
/// <param name="Urgent">
/// Someone is waiting on the recipient (a deployment waiting for approval), so
/// a digest choice is treated as Immediately; only Off stops it.
/// </param>
public sealed record Notification(
    NotificationCategory Category,
    IReadOnlyCollection<int> RecipientUserIds,
    NotificationDigestEntry Digest,
    Func<EmailRenderer, NotificationRecipient, CancellationToken, Task<EmailContent>> RenderAsync,
    bool Urgent = false);
