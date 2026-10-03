using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Sends a <see cref="Notification"/> to the people it names, each the way
/// they chose for its category (<see cref="NotificationPreferenceService"/>):
/// an email on the outbox now, an item kept for their digest, or nothing.
/// See <c>.design/notifications.md</c>.
///
/// <para>
/// Called from background workers as well as requests, always inside an
/// organisation scope, so every read here stays behind the query filter: a
/// recipient id from another organisation is simply not found.
/// </para>
///
/// <para>
/// Never throws, except when <c>ct</c> itself is cancelled: a timeout
/// inside a send is a failed send, not a shutdown. A notification is a side effect of work
/// that already succeeded (a build finished, a deployment ran), and that work
/// must not fail because an email could not be queued.
/// </para>
/// </summary>
public sealed class NotificationService
{
    /// <summary>Where a notification email's footer sends someone to change what they get.</summary>
    public const string SettingsPath = "/account?section=notifications";

    private readonly AppDbContext _db;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly NotificationPreferenceService _preferences;
    private readonly IEmailService _email;
    private readonly EmailRenderer _renderer;
    private readonly PublicOrigin _origin;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationService> _logger;

    public NotificationService(
        AppDbContext db,
        IDbContextFactory<AppDbContext> dbFactory,
        NotificationPreferenceService preferences,
        IEmailService email,
        EmailRenderer renderer,
        PublicOrigin origin,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<NotificationService> logger)
    {
        _db = db;
        _dbFactory = dbFactory;
        _preferences = preferences;
        _email = email;
        _renderer = renderer;
        _origin = origin;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// False when PUBLIC_BASE_URL is not set. A background sender has no
    /// request to take a host from, and an email whose links point nowhere is
    /// worse than none, so nothing is sent; startup already warns about it.
    /// </summary>
    public bool IsEnabled => _origin.IsConfigured;

    /// <summary>An absolute link to <paramref name="pathAndQuery"/>, or null when <see cref="IsEnabled"/> is false.</summary>
    public string? Link(string pathAndQuery) =>
        _origin.Configured is { } origin ? origin + pathAndQuery : null;

    /// <summary>
    /// Emails, keeps for a digest, or drops <paramref name="notification"/> for
    /// each recipient. Disabled and pending accounts get nothing.
    /// </summary>
    public async Task NotifyAsync(Notification notification, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("Skipped a {Category} notification: no {EnvVar} configured.",
                notification.Category, PublicOrigin.EnvVarName);
            return;
        }
        var orgId = _orgContext.CurrentOrganizationId;
        if (orgId is null || notification.RecipientUserIds.Count == 0) return;

        try
        {
            await DeliverAsync(notification, orgId.Value, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Category} notification could not be delivered.", notification.Category);
        }
    }

    private async Task DeliverAsync(Notification notification, int orgId, CancellationToken ct)
    {
        var recipients = await _db.Users.AsNoTracking()
            .Where(u => notification.RecipientUserIds.Contains(u.Id) && u.Status == UserStatus.Active)
            .Select(u => new NotificationRecipient(u.Id, u.DisplayName, u.Email))
            .ToListAsync(ct);
        if (recipients.Count == 0) return;

        var choices = await _preferences.GetForUsersAsync(
            recipients.Select(r => r.UserId).ToList(), notification.Category, ct);
        var emailReady = await _email.IsConfiguredAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var digestItems = new List<NotificationDigestItem>();
        var sent = 0;

        foreach (var recipient in recipients)
        {
            var choice = choices[recipient.UserId];
            if (notification.Urgent && choice is NotificationDelivery.Daily or NotificationDelivery.Weekly)
            {
                choice = NotificationDelivery.Immediately;
            }
            switch (choice)
            {
                case NotificationDelivery.Immediately when emailReady:
                    if (await TrySendAsync(notification, recipient, ct)) sent++;
                    break;
                case NotificationDelivery.Daily or NotificationDelivery.Weekly:
                    digestItems.Add(new NotificationDigestItem
                    {
                        UserId = recipient.UserId,
                        OrganizationId = orgId,
                        Category = notification.Category,
                        Delivery = choice,
                        Title = notification.Digest.Title,
                        Detail = notification.Digest.Detail,
                        Url = notification.Digest.Url,
                        SolutionName = notification.Digest.SolutionName,
                        CreatedAt = now,
                    });
                    break;
            }
        }

        var kept = await KeepForDigestAsync(digestItems, notification.Category, ct);
        _logger.LogInformation(
            "{Category} notification: {Sent} emailed, {Kept} kept for a digest, of {Recipients} recipients.",
            notification.Category, sent, kept, recipients.Count);
    }

    /// <summary>
    /// Stores digest items through a context of their own, never the caller's:
    /// a worker calling mid-way must not have its pending changes saved here,
    /// nor be left holding items that failed to save. Same reason as
    /// <see cref="EmailOutbox"/>.
    /// </summary>
    private async Task<int> KeepForDigestAsync(
        List<NotificationDigestItem> items, NotificationCategory category, CancellationToken ct)
    {
        if (items.Count == 0) return 0;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.NotificationDigestItems.AddRange(items);
            await db.SaveChangesAsync(ct);
            return items.Count;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not keep {Count} {Category} notifications for the digest.",
                items.Count, category);
            return 0;
        }
    }

    private async Task<bool> TrySendAsync(Notification notification, NotificationRecipient recipient, CancellationToken ct)
    {
        try
        {
            var content = await notification.RenderAsync(_renderer, recipient, ct);
            await _email.SendAsync(recipient.Email, content, PurposeFor(notification.Category), ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "{Category} notification to user {UserId} could not be sent.",
                notification.Category, recipient.UserId);
            return false;
        }
    }

    /// <summary>The outbox label for a category, which the SiteAdmin Delivery tab shows.</summary>
    public static EmailPurpose PurposeFor(NotificationCategory category) => category switch
    {
        NotificationCategory.Builds => EmailPurpose.BuildNotification,
        NotificationCategory.Deployments => EmailPurpose.DeploymentNotification,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}
