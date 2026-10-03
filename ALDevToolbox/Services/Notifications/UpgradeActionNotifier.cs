using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

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
    public async Task NotifyAsync(int actionId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(actionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send notifications for upgrade action {ActionId}.", actionId);
        }
    }

    private async Task SendAsync(int actionId, CancellationToken ct)
    {
        var action = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => new
            {
                a.OrganizationId,
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

        var failed = action.Status == UpgradeActionStatus.Failed;
        var what = Describe(action.Kind, action.TargetVersion, action.PackageFileName ?? action.AppName);
        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == action.OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(ct);
        var path = $"/environments/{action.EnvironmentId}/history";

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Upgrades,
            [recipient],
            new NotificationSummary(
                UpgradeActionEmail.SubjectFor(failed, what, action.EnvironmentName),
                action.Outcome,
                path,
                action.SolutionName),
            (renderer, person, token) => UpgradeActionEmail.RenderAsync(
                renderer, person.DisplayName, organizationName, failed, what, action.SolutionName, action.EnvironmentName,
                action.Outcome, _notifications.Link(path)!, _notifications.Link(NotificationService.SettingsPath)!, token)),
            ct);
    }

    /// <summary>What was scheduled, as a short imperative: "Install CRONUS Coffee.app".</summary>
    internal static string Describe(UpgradeActionKind kind, string? targetVersion, string? appName) => kind switch
    {
        UpgradeActionKind.PushDateToLatest => "Move the update to the latest date",
        UpgradeActionKind.RunNow => "Start the update",
        UpgradeActionKind.UploadApp => $"Install {appName ?? "an app"}",
        UpgradeActionKind.UpdateApp => $"Update {appName ?? "an app"} to {targetVersion}",
        UpgradeActionKind.SelectVersion => $"Set the next version to {targetVersion}",
        _ => "Scheduled change",
    };
}
