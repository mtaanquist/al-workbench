using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells the person down to check an environment on a planned upgrade that it has
/// reached the target version (issue #1047), or the person who planned the upgrade
/// when nobody is assigned. Each line is told once: the stamp on it is claimed before
/// sending, so whichever read noticed the new version, and however often the sweep
/// runs, nobody hears it twice. See <c>.design/notifications.md</c>.
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
                        && l.Environment!.Version != null && l.Environment.MissingSince == null)
            .Select(l => new
            {
                l.Id,
                l.UpgradeId,
                l.OrganizationId,
                Assigned = l.AssigneeUserId != null,
                Recipient = l.AssigneeUserId ?? l.Upgrade!.CreatedByUserId,
                l.Environment!.Version,
                l.Upgrade!.TargetVersion,
                UpgradeName = l.Upgrade.Name,
                EnvironmentName = l.Environment.Name,
                SolutionName = l.Project!.Name,
            })
            .ToListAsync(ct);
        var ready = candidates.Where(c => EnvironmentUpgradeLineState.IsOnTarget(c.Version, c.TargetVersion)).ToList();
        if (ready.Count == 0) return 0;

        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == ready[0].OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(ct);
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
                (renderer, person, token) => EnvironmentReadyToCheckEmail.RenderAsync(
                    renderer, person.DisplayName, organizationName, line.SolutionName, line.EnvironmentName, line.Version!,
                    line.UpgradeName, line.Assigned, _notifications.Link(path)!,
                    _notifications.Link(NotificationService.SettingsPath)!, token)),
                ct);
            sent++;
        }
        _logger.LogInformation("Told {Count} people an environment is ready to check.", sent);
        return sent;
    }
}
