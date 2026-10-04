using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>One person's choice for one <see cref="NotificationCategory"/>.</summary>
/// <param name="InApp">Listed on their Notifications page.</param>
/// <param name="Email">When they are emailed.</param>
public sealed record NotificationChoice(bool InApp, NotificationDelivery Email);

/// <summary>
/// Each person's choice, per <see cref="NotificationCategory"/>, of whether
/// they see it in the app, and when they are emailed: immediately, in a daily
/// or weekly digest, or not at all. Read and written from the Notifications
/// section of the account page, and read by <see cref="NotificationService"/>
/// for each event's recipients. See <c>.design/notifications.md</c>.
/// </summary>
public sealed class NotificationPreferenceService
{
    /// <summary>
    /// When a person is emailed before they choose. Immediately, because those
    /// notifications go to someone who caused or owns the thing they are about,
    /// and the email links to the place to turn it off. Followed solutions are
    /// the exception: everyone on a solution's People list follows it without
    /// asking, so their email is opt-in and they hear in the app only.
    /// </summary>
    public static NotificationDelivery DefaultDeliveryFor(NotificationCategory category) =>
        category == NotificationCategory.Solutions ? NotificationDelivery.Off : NotificationDelivery.Immediately;

    /// <summary>The choice of someone who has not made one: shown in the app, emailed as <see cref="DefaultDeliveryFor"/> says.</summary>
    public static NotificationChoice DefaultFor(NotificationCategory category) => new(InApp: true, DefaultDeliveryFor(category));

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
    public async Task<Dictionary<NotificationCategory, NotificationChoice>> GetForCurrentUserAsync(CancellationToken ct = default)
    {
        var userId = RequireUserId();
        var stored = await _db.UserNotificationSettings.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.Category, s => new NotificationChoice(s.InApp, s.Delivery), ct);
        return Enum.GetValues<NotificationCategory>()
            .ToDictionary(c => c, c => stored.GetValueOrDefault(c, DefaultFor(c)));
    }

    /// <summary>Saves when the signed-in person is emailed about one category.</summary>
    public async Task SetEmailForCurrentUserAsync(
        NotificationCategory category, NotificationDelivery delivery, CancellationToken ct = default)
    {
        RequireKnown(category);
        if (!Enum.IsDefined(delivery))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Delivery"] = "Pick when to be emailed: immediately, in a daily or weekly digest, or not at all.",
            });
        }

        var userId = RequireUserId();
        var orgId = RequireOrganizationId();
        var now = _clock.GetUtcNow().UtcDateTime;
        // One statement, so two quick changes (two tabs, a double click) cannot
        // both insert and trip the unique (user_id, category) index. Nothing is
        // tracked either, so a failed save leaves the page's context clean for
        // the next one.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_notification_settings (user_id, organization_id, category, delivery, in_app, updated_at)
            VALUES ({userId}, {orgId}, {category.ToString()}, {delivery.ToString()}, {true}, {now})
            ON CONFLICT (user_id, category)
            DO UPDATE SET delivery = EXCLUDED.delivery, updated_at = EXCLUDED.updated_at
            """, ct);
        _logger.LogInformation(
            "User {UserId} set {Category} emails to {Delivery}.", userId, category, delivery);
    }

    /// <summary>Saves whether the signed-in person sees one category on their Notifications page.</summary>
    public async Task SetInAppForCurrentUserAsync(
        NotificationCategory category, bool inApp, CancellationToken ct = default)
    {
        RequireKnown(category);
        var userId = RequireUserId();
        var orgId = RequireOrganizationId();
        var now = _clock.GetUtcNow().UtcDateTime;
        // The same single-statement upsert as the email choice, leaving that alone.
        await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_notification_settings (user_id, organization_id, category, delivery, in_app, updated_at)
            VALUES ({userId}, {orgId}, {category.ToString()}, {DefaultDeliveryFor(category).ToString()}, {inApp}, {now})
            ON CONFLICT (user_id, category)
            DO UPDATE SET in_app = EXCLUDED.in_app, updated_at = EXCLUDED.updated_at
            """, ct);
        _logger.LogInformation(
            "User {UserId} turned {Category} in-app notifications {State}.", userId, category, inApp ? "on" : "off");
    }

    /// <summary>
    /// Each listed person's choice for one category, defaults filled in. Only
    /// people in the current organisation come back: the query filter applies.
    /// </summary>
    public async Task<Dictionary<int, NotificationChoice>> GetForUsersAsync(
        IReadOnlyCollection<int> userIds, NotificationCategory category, CancellationToken ct = default)
    {
        var stored = await _db.UserNotificationSettings.AsNoTracking()
            .Where(s => s.Category == category && userIds.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => new NotificationChoice(s.InApp, s.Delivery), ct);
        return userIds.Distinct().ToDictionary(id => id, id => stored.GetValueOrDefault(id, DefaultFor(category)));
    }

    private static void RequireKnown(NotificationCategory category)
    {
        if (!Enum.IsDefined(category))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Category"] = "Pick one of the notification types listed.",
            });
        }
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    private int RequireUserId() => _orgContext.CurrentUserId
        ?? throw new InvalidOperationException("No signed-in user; notification settings belong to one.");
}
