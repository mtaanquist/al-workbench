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
                e.OrganizationId,
                e.BcUpdateWindowTimeZoneIana,
                SolutionName = e.Project!.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (env is null) return;

        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == env.OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(ct);
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
            (renderer, person, token) => EnvironmentUpdateEmail.RenderAsync(
                renderer, person.DisplayName, organizationName, change.Kind, env.SolutionName, env.Name, change.Version,
                date, latest, previous, _notifications.Link(path)!, _notifications.Link(NotificationService.SettingsPath)!, token)),
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
                return local.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
            }
            catch (TimeZoneNotFoundException)
            {
                // Falls through to UTC.
            }
        }
        return when.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) + " (UTC)";
    }
}
