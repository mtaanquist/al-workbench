using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Email;

namespace ALDevToolbox.Services.Notifications;

/// <summary>Who a notification email is addressed to, for its greeting.</summary>
public sealed record NotificationRecipient(int UserId, string DisplayName, string Email);

/// <summary>
/// The one-line form of a notification in a daily or weekly digest email.
/// </summary>
public sealed record NotificationDigestEntry(string Title, string? Detail, string Url, string? SolutionName);

/// <summary>
/// The one-line form of a notification: what the Notifications page lists and
/// what a digest email carries.
/// </summary>
/// <param name="Path">The page it is about, as a path within the app, e.g. <c>/pipelines/4?build=12</c>.</param>
public sealed record NotificationSummary(string Title, string? Detail, string Path, string? SolutionName);

/// <summary>
/// Something happened that people should hear about. The caller decides who
/// (<see cref="RecipientUserIds"/>) and what it says; the
/// <see cref="NotificationService"/> decides, per recipient, whether it is
/// listed in the app, and whether it is emailed now, kept for a digest or not
/// emailed.
/// </summary>
/// <param name="RenderAsync">
/// Renders the email for one recipient, so it can greet them by name. Only
/// called when PUBLIC_BASE_URL is set, so links in it may assume one.
/// </param>
/// <param name="Urgent">
/// Someone is waiting on the recipient (a deployment waiting for approval), so
/// a digest choice is treated as Immediately; only Off stops the email.
/// </param>
/// <param name="ProjectId">
/// The solution it is about. A recipient who can no longer see it (taken off a
/// Private solution's team) is left out, so its name and events stay private.
/// </param>
public sealed record Notification(
    NotificationCategory Category,
    IReadOnlyCollection<int> RecipientUserIds,
    NotificationSummary Summary,
    Func<EmailRenderer, NotificationRecipient, CancellationToken, Task<EmailContent>> RenderAsync,
    bool Urgent = false,
    int? ProjectId = null);
