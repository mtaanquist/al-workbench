using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>How a scheduled change ended, as its notification tells it.</summary>
public enum UpgradeActionResult
{
    Done,
    Failed,
    /// <summary>Sent to Business Central, but nobody saw it finish: the person should check.</summary>
    Unconfirmed,
    /// <summary>A booked install, or the rest of a batch, came up after its window closed and now waits for the next opening.</summary>
    Moved,
}

/// <summary>
/// Tells the person who scheduled a change on an environment how it went, once the
/// worker has run it (issue #1046). Changes made on the spot are not announced: the
/// person saw the answer on the page. See <c>.design/notifications.md</c>.
/// </summary>
public sealed class UpgradeActionNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<UpgradeActionNotifier> _logger;

    public UpgradeActionNotifier(AppDbContext db, NotificationService notifications, ILogger<UpgradeActionNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Notifies about the scheduled change <paramref name="actionId"/> as it stands: sent
    /// or failed. A change still pending, cancelled, or sent but waiting to be asked about
    /// again sends nothing; the second look announces it instead. Never throws, except
    /// when <paramref name="ct"/> itself is cancelled: the outcome is already recorded.
    /// </summary>
    /// <param name="unconfirmed">
    /// The worker sent it but could not confirm it finished. The row cannot say so on its
    /// own (it is recorded as sent), so the worker, which knows, passes it in.
    /// </param>
    public async Task NotifyAsync(int actionId, bool unconfirmed = false, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(actionId, unconfirmed, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send notifications for upgrade action {ActionId}.", actionId);
        }
    }

    private async Task SendAsync(int actionId, bool unconfirmed, CancellationToken ct)
    {
        var action = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => new
            {
                a.ProjectId,
                a.EnvironmentId,
                a.Kind,
                a.Status,
                a.ConfirmationDue,
                a.RequestedByUserId,
                a.TargetVersion,
                a.AppName,
                a.PackageFileName,
                a.Outcome,
                SolutionName = a.Project!.Name,
                EnvironmentName = a.Environment!.Name,
            })
            .FirstOrDefaultAsync(ct);
        if (action is null
            || action.RequestedByUserId is not { } recipient
            || action.ConfirmationDue
            || action.Status is not (UpgradeActionStatus.Sent or UpgradeActionStatus.Failed))
        {
            return;
        }

        var result = action.Status == UpgradeActionStatus.Failed ? UpgradeActionResult.Failed
            : unconfirmed ? UpgradeActionResult.Unconfirmed
            : UpgradeActionResult.Done;
        var what = Describe(action.Kind, action.TargetVersion, action.PackageFileName ?? action.AppName);
        var path = $"/environments/{action.EnvironmentId}/history";

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Upgrades,
            [recipient],
            new NotificationSummary(
                UpgradeActionEmail.SubjectFor(result, what, action.SolutionName, action.EnvironmentName),
                // One line, like every other notifier's detail: a failure outcome carries
                // Business Central's whole reason. The email below keeps it in full.
                action.Outcome is null ? null : NotificationText.FirstLine(action.Outcome),
                path,
                action.SolutionName),
            (email, token) => UpgradeActionEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, result, what, action.SolutionName,
                action.EnvironmentName, action.Outcome, email.ItemUrl, email.SettingsUrl, token),
            ProjectId: action.ProjectId),
            ct);
    }

    /// <summary>
    /// Tells the person who booked <paramref name="actionId"/> that it, and the rest of
    /// its batch still waiting (<paramref name="moved"/> rows in all), came up after its
    /// window closed and was moved to the next opening (#1575). Without it a part-done
    /// batch reads as done from the notifications alone. Never throws, as
    /// <see cref="NotifyAsync"/>.
    /// </summary>
    /// <param name="message">The plain-words line: what was moved and when it now runs.</param>
    public async Task NotifyMovedAsync(int actionId, int moved, string message, CancellationToken ct = default)
    {
        try
        {
            var action = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.Id == actionId)
                .Select(a => new
                {
                    a.ProjectId,
                    a.EnvironmentId,
                    a.Kind,
                    a.RequestedByUserId,
                    a.TargetVersion,
                    a.AppName,
                    a.PackageFileName,
                    SolutionName = a.Project!.Name,
                    EnvironmentName = a.Environment!.Name,
                })
                .FirstOrDefaultAsync(ct);
            if (action?.RequestedByUserId is not { } recipient) return;

            var what = moved > 1
                ? action.Kind == UpgradeActionKind.UpdateApp ? $"Update {moved} apps" : $"Install {moved} apps"
                : Describe(action.Kind, action.TargetVersion, action.PackageFileName ?? action.AppName);
            var path = $"/environments/{action.EnvironmentId}/history";
            const UpgradeActionResult result = UpgradeActionResult.Moved;

            await _notifications.NotifyAsync(new Notification(
                NotificationCategory.Upgrades,
                [recipient],
                new NotificationSummary(
                    UpgradeActionEmail.SubjectFor(result, what, action.SolutionName, action.EnvironmentName),
                    message,
                    path,
                    action.SolutionName),
                (email, token) => UpgradeActionEmail.RenderAsync(
                    email.Renderer, email.Recipient.DisplayName, email.OrganizationName, result, what, action.SolutionName,
                    action.EnvironmentName, message, email.ItemUrl, email.SettingsUrl, token),
                ProjectId: action.ProjectId),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send the moved notice for upgrade action {ActionId}.", actionId);
        }
    }

    /// <summary>What was scheduled, as a short imperative: "Install CRONUS Coffee.app".</summary>
    internal static string Describe(UpgradeActionKind kind, string? targetVersion, string? appName) => kind switch
    {
        UpgradeActionKind.PushDateToLatest => "Move the update to the latest date",
        UpgradeActionKind.RunNow => "Start the update",
        UpgradeActionKind.UploadApp => $"Install {appName ?? "an app"}",
        UpgradeActionKind.UpdateApp => targetVersion is null ? $"Update {appName ?? "an app"}" : $"Update {appName ?? "an app"} to {targetVersion}",
        UpgradeActionKind.SelectVersion => targetVersion is null ? "Set the next version" : $"Set the next version to {targetVersion}",
        _ => "Scheduled change",
    };
}
