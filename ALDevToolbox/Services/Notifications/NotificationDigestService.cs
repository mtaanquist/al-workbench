using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Endpoints;
using ALDevToolbox.Services.Email;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>One kind of notification in a digest, under its heading.</summary>
public sealed record DigestSection(string Heading, IReadOnlyList<NotificationDigestEntry> Items);

/// <summary>
/// Sends the daily and weekly digests for the current organisation (issue
/// #1037): every kept notification older than the last cut-off, one email per
/// person and digest, and the items deleted once it is on the outbox. See
/// <c>.design/notifications.md</c>.
///
/// <para>
/// Holds no state between runs. The cut-offs are fixed times (06:00 UTC daily,
/// Monday 06:00 UTC weekly) and sent items are deleted, so a run at any time
/// after a cut-off sends what is due and nothing twice: a restart, or a whole
/// day down, only delays a digest.
/// </para>
/// </summary>
public sealed class NotificationDigestService
{
    /// <summary>The UTC hour digests go out, ahead of the European working day.</summary>
    public const int DigestHourUtc = 6;

    /// <summary>How long an item waits for a digest that cannot be sent (email not set up) before it is dropped.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(30);

    /// <summary>How long after a cut-off a run waits before using it.</summary>
    internal static readonly TimeSpan CutoffGrace = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _db;
    private readonly IEmailService _email;
    private readonly EmailRenderer _renderer;
    private readonly PublicOrigin _origin;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationDigestService> _logger;

    public NotificationDigestService(
        AppDbContext db,
        IEmailService email,
        EmailRenderer renderer,
        PublicOrigin origin,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<NotificationDigestService> logger)
    {
        _db = db;
        _email = email;
        _renderer = renderer;
        _origin = origin;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The latest daily cut-off at or before <paramref name="nowUtc"/>.</summary>
    internal static DateTime DailyCutoff(DateTime nowUtc)
    {
        var today = nowUtc.Date.AddHours(DigestHourUtc);
        return today <= nowUtc ? today : today.AddDays(-1);
    }

    /// <summary>The latest weekly cut-off (a Monday) at or before <paramref name="nowUtc"/>.</summary>
    internal static DateTime WeeklyCutoff(DateTime nowUtc)
    {
        var sinceMonday = ((int)nowUtc.DayOfWeek + 6) % 7;
        var monday = nowUtc.Date.AddDays(-sinceMonday).AddHours(DigestHourUtc);
        return monday <= nowUtc ? monday : monday.AddDays(-7);
    }

    /// <summary>
    /// Sends every digest that is due in the current organisation and prunes
    /// items older than <see cref="KeepFor"/>. Returns how many digests went
    /// on the outbox. A digest that fails to queue keeps its items for the next
    /// run.
    /// </summary>
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; the digest runs inside one.");
        var now = _clock.GetUtcNow().UtcDateTime;

        var expiry = now - KeepFor;
        var pruned = await _db.NotificationDigestItems.Where(i => i.CreatedAt < expiry).ExecuteDeleteAsync(ct);
        if (pruned > 0)
        {
            _logger.LogInformation("Dropped {Count} digest items older than {Days} days.", pruned, KeepFor.Days);
        }

        // Without a public address the links go nowhere, and without email
        // nothing can be sent; the items wait, up to KeepFor.
        if (_origin.Configured is not { } origin || !await _email.IsConfiguredAsync(ct)) return 0;

        // A few minutes' grace, so an item stamped just before a cut-off but
        // saved just after it is not left for a second digest of its own.
        var daily = DailyCutoff(now - CutoffGrace);
        var weekly = WeeklyCutoff(now - CutoffGrace);
        var items = await _db.NotificationDigestItems.AsNoTracking()
            .Where(i => (i.Delivery == NotificationDelivery.Daily && i.CreatedAt < daily)
                || (i.Delivery == NotificationDelivery.Weekly && i.CreatedAt < weekly))
            .OrderBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .ToListAsync(ct);
        if (items.Count == 0) return 0;

        var userIds = items.Select(i => i.UserId).Distinct().ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.Status == UserStatus.Active)
            .ToDictionaryAsync(u => u.Id, u => new NotificationRecipient(u.Id, u.DisplayName, u.Email), ct);
        // Someone who has since turned a kind off does not get the items kept before.
        var off = (await _db.UserNotificationSettings.AsNoTracking()
                .Where(s => userIds.Contains(s.UserId) && s.Delivery == NotificationDelivery.Off)
                .Select(s => new { s.UserId, s.Category })
                .ToListAsync(ct))
            .Select(s => (s.UserId, s.Category))
            .ToHashSet();
        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => o.Name)
            .FirstAsync(ct);
        var settingsUrl = origin + NotificationService.SettingsPath;

        var sent = 0;
        var dropped = new List<int>();
        foreach (var digest in items.GroupBy(i => (i.UserId, i.Delivery)))
        {
            var wanted = digest.Where(i => !off.Contains((i.UserId, i.Category))).ToList();
            if (!users.TryGetValue(digest.Key.UserId, out var recipient) || wanted.Count == 0)
            {
                dropped.AddRange(digest.Select(i => i.Id));
                continue;
            }

            var sections = wanted
                .GroupBy(i => i.Category)
                .OrderBy(g => g.Key)
                .Select(g => new DigestSection(
                    Heading(g.Key),
                    g.Select(i => new NotificationDigestEntry(i.Title, i.Detail, i.Url, i.SolutionName)).ToList()))
                .ToList();
            var ids = digest.Select(i => i.Id).ToList();
            EmailContent content;
            try
            {
                content = await DigestEmail.RenderAsync(_renderer, recipient.DisplayName, organizationName,
                    digest.Key.Delivery == NotificationDelivery.Weekly, sections, settingsUrl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "The {Delivery} digest for user {UserId} could not be rendered; it will be tried again.",
                    digest.Key.Delivery, digest.Key.UserId);
                continue;
            }

            // Delete first, inside a transaction, then queue, then commit. A queue
            // that fails rolls the delete back, so the items wait for the next run;
            // only a failed commit after a successful queue could send a digest twice.
            // The commit ignores ct, so a shutdown arriving mid-way cannot open that
            // window either.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await _db.NotificationDigestItems.Where(i => ids.Contains(i.Id)).ExecuteDeleteAsync(ct);
            try
            {
                await _email.SendAsync(recipient.Email, content, EmailPurpose.NotificationDigest, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogWarning(ex, "The {Delivery} digest for user {UserId} could not be queued; it will be tried again.",
                    digest.Key.Delivery, digest.Key.UserId);
                continue;
            }
            await transaction.CommitAsync(CancellationToken.None);
            sent++;
        }

        if (dropped.Count > 0)
        {
            await _db.NotificationDigestItems.Where(i => dropped.Contains(i.Id)).ExecuteDeleteAsync(ct);
        }
        _logger.LogInformation("Sent {Sent} notification digests; dropped {Dropped} items nobody wants now.", sent, dropped.Count);
        return sent;
    }

    private static string Heading(NotificationCategory category) => category switch
    {
        NotificationCategory.Builds => "Builds",
        NotificationCategory.Deployments => "Deployments",
        _ => category.ToString(),
    };
}
