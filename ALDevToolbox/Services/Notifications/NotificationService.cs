using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Sends a <see cref="Notification"/> to the people it names, each the way
/// they chose for its category (<see cref="NotificationPreferenceService"/>):
/// listed on their Notifications page, and an email on the outbox now, an item
/// kept for their digest, or no email. See <c>.design/notifications.md</c>.
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
    /// worse than none, so no email is sent or kept for a digest; startup
    /// already warns about it. Notifications are still listed in the app,
    /// whose links need no host.
    /// </summary>
    public bool IsEnabled => _origin.IsConfigured;

    /// <summary>An absolute link to <paramref name="pathAndQuery"/>, or null when <see cref="IsEnabled"/> is false.</summary>
    public string? Link(string pathAndQuery) =>
        _origin.Configured is { } origin ? origin + pathAndQuery : null;

    /// <summary>
    /// Lists <paramref name="notification"/> in the app and emails it, keeps it
    /// for a digest, or does not email it, for each recipient. Disabled and
    /// pending accounts get nothing.
    /// </summary>
    public async Task NotifyAsync(Notification notification, CancellationToken ct = default)
    {
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
        var url = Link(notification.Summary.Path);
        var emailReady = url is not null && await _email.IsConfiguredAsync(ct);
        if (url is null)
        {
            _logger.LogDebug("No email for a {Category} notification: no {EnvVar} configured.",
                notification.Category, PublicOrigin.EnvVarName);
        }
        var now = _clock.GetUtcNow().UtcDateTime;
        var listed = new List<UserNotification>();
        var digestItems = new List<NotificationDigestItem>();
        var sent = 0;

        foreach (var recipient in recipients)
        {
            var choice = choices[recipient.UserId];
            if (choice.InApp)
            {
                listed.Add(new UserNotification
                {
                    UserId = recipient.UserId,
                    OrganizationId = orgId,
                    Category = notification.Category,
                    Title = notification.Summary.Title,
                    Detail = notification.Summary.Detail,
                    Path = notification.Summary.Path,
                    SolutionName = notification.Summary.SolutionName,
                    CreatedAt = now,
                });
            }

            if (url is null) continue;
            var email = choice.Email;
            if (notification.Urgent && email is NotificationDelivery.Daily or NotificationDelivery.Weekly)
            {
                email = NotificationDelivery.Immediately;
            }
            switch (email)
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
                        Delivery = email,
                        Title = notification.Summary.Title,
                        Detail = notification.Summary.Detail,
                        Url = url,
                        SolutionName = notification.Summary.SolutionName,
                        CreatedAt = now,
                    });
                    break;
            }
        }

        var listedCount = await StoreAsync(db => db.UserNotifications.AddRange(listed), listed.Count,
            "in-app", notification.Category, ct);
        var kept = await StoreAsync(db => db.NotificationDigestItems.AddRange(digestItems), digestItems.Count,
            "digest", notification.Category, ct);
        _logger.LogInformation(
            "{Category} notification: {Listed} listed in the app, {Sent} emailed, {Kept} kept for a digest, of {Recipients} recipients.",
            notification.Category, listedCount, sent, kept, recipients.Count);
    }

    /// <summary>
    /// Stores in-app rows or digest items through a context of their own, never
    /// the caller's: a worker calling mid-way must not have its pending changes
    /// saved here, nor be left holding rows that failed to save. Same reason as
    /// <see cref="EmailOutbox"/>. The two kinds save separately, so a problem
    /// with one does not lose the other.
    /// </summary>
    private async Task<int> StoreAsync(
        Action<AppDbContext> add, int count, string kind, NotificationCategory category, CancellationToken ct)
    {
        if (count == 0) return 0;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            add(db);
            await db.SaveChangesAsync(ct);
            return count;
        }
        catch (DbUpdateException ex)
        {
            _logger.LogWarning(ex, "Could not store {Count} {Kind} {Category} notifications.", count, kind, category);
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
