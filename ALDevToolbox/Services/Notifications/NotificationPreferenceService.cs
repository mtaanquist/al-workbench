using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Each person's choice, per <see cref="NotificationCategory"/>, of when they
/// are emailed: immediately, in a daily or weekly digest, or not at all. Read
/// and written from the Notifications section of the account page, and read by
/// <see cref="NotificationService"/> for each event's recipients. See
/// <c>.design/notifications.md</c>.
/// </summary>
public sealed class NotificationPreferenceService
{
    /// <summary>
    /// What a person gets before they choose. On, because every notification
    /// goes to someone who caused or owns the thing it is about, and the email
    /// links to the place to turn it off.
    /// </summary>
    public const NotificationDelivery DefaultDelivery = NotificationDelivery.Immediately;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationPreferenceService> _logger;

    public NotificationPreferenceService(
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<NotificationPreferenceService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The signed-in person's choice for every category, defaults filled in.</summary>
    public async Task<Dictionary<NotificationCategory, NotificationDelivery>> GetForCurrentUserAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var stored = await _db.UserNotificationSettings.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.Category, s => s.Delivery, ct);
        return Enum.GetValues<NotificationCategory>()
            .ToDictionary(c => c, c => stored.GetValueOrDefault(c, DefaultDelivery));
    }

    /// <summary>Saves the signed-in person's choice for one category.</summary>
    public async Task SetForCurrentUserAsync(
        NotificationCategory category, NotificationDelivery delivery, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(category))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Category"] = "Pick one of the notification types listed.",
            });
        }
        if (!Enum.IsDefined(delivery))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Delivery"] = "Pick Immediately, Daily digest, Weekly digest or Off.",
            });
        }

        var userId = RequireUserId();
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");
        var row = await _db.UserNotificationSettings
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Category == category, ct);
        if (row is null)
        {
            row = new UserNotificationSetting { UserId = userId, OrganizationId = orgId, Category = category };
            _db.UserNotificationSettings.Add(row);
        }
        row.Delivery = delivery;
        row.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "User {UserId} set {Category} notifications to {Delivery}.", userId, category, delivery);
    }

    /// <summary>
    /// Each listed person's choice for one category, defaults filled in. Only
    /// people in the current organisation come back: the query filter applies.
    /// </summary>
    public async Task<Dictionary<int, NotificationDelivery>> GetForUsersAsync(
        IReadOnlyCollection<int> userIds, NotificationCategory category, CancellationToken ct = default)
    {
        var stored = await _db.UserNotificationSettings.AsNoTracking()
            .Where(s => s.Category == category && userIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.Delivery, ct);
        return userIds.Distinct().ToDictionary(id => id, id => stored.GetValueOrDefault(id, DefaultDelivery));
    }

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No signed-in user; notification settings belong to one.");
}
