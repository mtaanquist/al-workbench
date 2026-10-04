using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells the person down to check an environment on a planned upgrade that it has
/// reached the target version (issue #1047), or the person who planned the upgrade
/// when nobody is assigned. Each line is told at most once per assignee and target:
/// the stamp on it is claimed before sending, so however often the sweep runs nobody
/// hears it twice. A crash between the claim and the send loses that notice, which is
/// the better failure for an advisory email than a duplicate. Changing the assignee or
/// the target clears the stamp (<see cref="EnvironmentUpgradeService"/>). See
/// <c>.design/notifications.md</c>.
/// </summary>
public sealed class UpgradeCheckNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgradeCheckNotifier> _logger;

    public UpgradeCheckNotifier(AppDbContext db, NotificationService notifications, TimeProvider clock, ILogger<UpgradeCheckNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Notifies about every unchecked line of an open upgrade in the current organisation
    /// whose environment is now on the target version. Returns how many were announced.
    /// Never throws, except when <paramref name="ct"/> itself is cancelled.
    /// </summary>
    public async Task<int> NotifyReadyToCheckAsync(CancellationToken ct = default)
    {
        try
        {
            return await SendAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send ready-to-check notifications.");
            return 0;
        }
    }

    private async Task<int> SendAsync(CancellationToken ct)
    {
        var candidates = await _db.OeEnvironmentUpgradeLines.AsNoTracking()
            .Where(l => l.IsOpen && l.CheckedAt == null && l.UpdatedNotifiedAt == null
                        && l.Project!.DeletedAt == null
                        && l.Environment!.Version != null && l.Environment.MissingSince == null
                        && l.Environment.SoftDeletedOn == null)
            .Select(l => new
            {
                l.Id,
                l.ProjectId,
                l.UpgradeId,
                Assigned = l.AssigneeUserId != null,
                Recipient = l.AssigneeUserId ?? l.Upgrade!.CreatedByUserId,
                l.Environment!.Version,
                l.Environment.Status,
                l.Environment.BcNextUpdateStatus,
                l.Upgrade!.TargetVersion,
                UpgradeName = l.Upgrade.Name,
                EnvironmentName = l.Environment.Name,
                SolutionName = l.Project!.Name,
            })
            .ToListAsync(ct);
        // The page's rule for Updated: on target and no longer busy. A later sweep picks up
        // a line still running.
        var ready = candidates
            .Where(c => !EnvironmentUpgradeLineState.IsUpdating(c.Status, c.BcNextUpdateStatus)
                        && EnvironmentUpgradeLineState.IsOnTarget(c.Version, c.TargetVersion))
            .ToList();
        if (ready.Count == 0) return 0;

        var sent = 0;
        foreach (var line in ready)
        {
            // Claim first, so a second sweep or a second instance never tells them twice.
            var now = _clock.GetUtcNow().UtcDateTime;
            var claimed = await _db.OeEnvironmentUpgradeLines
                .Where(l => l.Id == line.Id && l.UpdatedNotifiedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.UpdatedNotifiedAt, now), ct);
            if (claimed == 0 || line.Recipient is not { } recipient) continue;

            var path = $"/upgrades/{line.UpgradeId}";
            await _notifications.NotifyAsync(new Notification(
                NotificationCategory.Upgrades,
                [recipient],
                new NotificationSummary(
                    EnvironmentReadyToCheckEmail.SubjectFor(line.SolutionName, line.EnvironmentName, line.Version!),
                    line.UpgradeName,
                    path,
                    line.SolutionName),
                (email, token) => EnvironmentReadyToCheckEmail.RenderAsync(
                    email.Renderer, email.Recipient.DisplayName, email.OrganizationName, line.SolutionName,
                    line.EnvironmentName, line.Version!, line.UpgradeName, line.Assigned, email.ItemUrl, email.SettingsUrl,
                    token),
                ProjectId: line.ProjectId),
                ct);
            sent++;
        }
        _logger.LogInformation("Told {Count} people an environment is ready to check.", sent);
        return sent;
    }
}
