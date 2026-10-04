using System.Globalization;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells a solution's followers when a refresh finds an environment's next Business
/// Central update newly scheduled, moved, or a week from the latest date it can be
/// postponed to (issue #1049). See <c>.design/notifications.md</c>.
/// </summary>
public sealed class EnvironmentUpdateNotifier
{
    /// <summary>How an update date reads in the notification, e.g. "Tue 14 Apr 2026".</summary>
    private const string DateFormat = "ddd d MMM yyyy";

    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ProjectFollowService _follows;
    private readonly ILogger<EnvironmentUpdateNotifier> _logger;

    public EnvironmentUpdateNotifier(
        AppDbContext db,
        NotificationService notifications,
        ProjectFollowService follows,
        ILogger<EnvironmentUpdateNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _follows = follows;
        _logger = logger;
    }

    /// <summary>
    /// Notifies the followers of each change's solution. Never throws, except when
    /// <paramref name="ct"/> itself is cancelled: the refresh has already been saved.
    /// </summary>
    public async Task NotifyAsync(IReadOnlyList<BcUpdateScheduleChange> changes, CancellationToken ct = default)
    {
        foreach (var change in changes)
        {
            try
            {
                await SendAsync(change, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not send update notifications for environment {EnvironmentId}.", change.EnvironmentId);
            }
        }
    }

    private async Task SendAsync(BcUpdateScheduleChange change, CancellationToken ct)
    {
        var recipients = await _follows.ListFollowerIdsAsync(change.ProjectId, ct);
        if (recipients.Count == 0) return;

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == change.EnvironmentId)
            .Select(e => new
            {
                e.Name,
                e.BcUpdateWindowTimeZoneIana,
                SolutionName = e.Project!.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (env is null) return;

        var date = FormatDate(change.Date, env.BcUpdateWindowTimeZoneIana);
        var latest = change.LatestDate is { } l ? FormatDate(l, env.BcUpdateWindowTimeZoneIana) : null;
        var previous = change.PreviousDate is { } p ? FormatDate(p, env.BcUpdateWindowTimeZoneIana) : null;
        var path = $"/environments/{change.EnvironmentId}";

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Solutions,
            recipients,
            new NotificationSummary(
                EnvironmentUpdateEmail.SubjectFor(change.Kind, env.SolutionName, env.Name, change.Version, date, latest),
                null,
                path,
                env.SolutionName),
            (email, token) => EnvironmentUpdateEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, change.Kind, env.SolutionName, env.Name,
                change.Version, date, latest, previous, email.ItemUrl, email.SettingsUrl, token),
            ProjectId: change.ProjectId),
            ct);
    }

    /// <summary>
    /// The day in the environment's own update-window time zone, where Business Central
    /// reports one, since that is the day the customer agreed to; otherwise UTC, said so.
    /// </summary>
    internal static string FormatDate(DateTime utc, string? ianaZone)
    {
        var when = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        if (ianaZone is { Length: > 0 })
        {
            try
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(when, TimeZoneInfo.FindSystemTimeZoneById(ianaZone));
                return local.ToString(DateFormat, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Falls through to UTC.
            }
        }
        return when.ToString(DateFormat, CultureInfo.InvariantCulture) + " (UTC)";
    }
}
