using System.Globalization;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells a Solution's followers when the Business Central licence on one of its
/// on-premises environments is about to expire, or has (#1369): once 30 days ahead, once
/// 7 days ahead and once on the day it expires. The expiry is the one the gateway last
/// read from the linked service tier (<c>gateway_instance_licences</c>).
///
/// <para>Each threshold is claimed on the environment row with a compare-and-set
/// (<see cref="OeProjectEnvironment.LicenseExpiryNoticeFor"/> and
/// <see cref="OeProjectEnvironment.LicenseExpiryNoticeDays"/>) before anything is sent, so
/// a threshold sends once however often the sweep runs, and a licence first seen inside a
/// threshold sends only the nearest one. A different expiry date (a new licence) starts
/// again from the top. Runs inside one organisation's scope, under the query filter.
/// See <c>.design/on-prem-gateway.md</c>, "Licences, as built".</para>
/// </summary>
public sealed class LicenceExpiryNotifier
{
    /// <summary>The thresholds, in days before expiry, largest first. 0 is "has expired".</summary>
    public static readonly IReadOnlyList<int> Thresholds = [30, 7, 0];

    /// <summary>How an expiry date reads in the notification, e.g. "Sat 14 Nov 2026".</summary>
    private const string DateFormat = "ddd d MMM yyyy";

    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ProjectFollowService _follows;
    private readonly TimeProvider _clock;
    private readonly ILogger<LicenceExpiryNotifier> _logger;

    public LicenceExpiryNotifier(
        AppDbContext db,
        NotificationService notifications,
        ProjectFollowService follows,
        TimeProvider clock,
        ILogger<LicenceExpiryNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _follows = follows;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// The threshold <paramref name="daysLeft"/> falls in: 0 once expired, 7 within a week,
    /// 30 within a month; null further out.
    /// </summary>
    public static int? ThresholdFor(int daysLeft) =>
        daysLeft <= 0 ? 0 : Thresholds.Where(t => t > 0 && daysLeft <= t).Cast<int?>().LastOrDefault();

    /// <summary>
    /// Checks every linked on-premises environment of the organisation in scope and sends
    /// the notices that are due. Returns how many went out. A failure for one environment
    /// is logged and the rest carry on.
    /// </summary>
    public async Task<int> NotifyDueAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var rows = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises && e.MissingSince == null && e.GatewayInstanceId != null)
            .Select(e => new
            {
                e.Id,
                e.ProjectId,
                e.Name,
                SolutionName = e.Project!.Name,
                ExpiresOn = e.GatewayInstance!.Licence == null ? null : e.GatewayInstance.Licence.ExpiresOn,
                e.LicenseExpiryNoticeFor,
                e.LicenseExpiryNoticeDays,
            })
            .ToListAsync(ct);

        var sent = 0;
        foreach (var row in rows)
        {
            try
            {
                if (row.ExpiresOn is not { } expires)
                {
                    continue;
                }
                var threshold = ThresholdFor(expires.DayNumber - today.DayNumber);
                var sameLicence = row.LicenseExpiryNoticeFor == expires;
                if (threshold is null)
                {
                    // A new licence further out than any threshold: forget the old notices
                    // so the new one's thresholds send in their turn.
                    if (row.LicenseExpiryNoticeFor is not null && !sameLicence)
                    {
                        await ClaimAsync(row.Id, row.LicenseExpiryNoticeFor, row.LicenseExpiryNoticeDays, null, null, ct);
                    }
                    continue;
                }
                if (sameLicence && row.LicenseExpiryNoticeDays is { } done && done <= threshold)
                {
                    continue;
                }
                if (!await ClaimAsync(row.Id, row.LicenseExpiryNoticeFor, row.LicenseExpiryNoticeDays, expires, threshold, ct))
                {
                    continue;
                }
                await SendAsync(row.Id, row.ProjectId, row.Name, row.SolutionName, expires, expires.DayNumber - today.DayNumber, ct);
                sent++;
                _logger.LogInformation(
                    "Sent the {Threshold}-day licence expiry notice for environment {EnvironmentId} (expires {ExpiresOn}).",
                    threshold, row.Id, expires);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Could not send the licence expiry notice for environment {EnvironmentId}.", row.Id);
            }
        }
        return sent;
    }

    /// <summary>
    /// Moves the environment's notice state from what was read to the new one, only if
    /// nobody else has moved it since. True when this call won.
    /// </summary>
    private async Task<bool> ClaimAsync(int environmentId, DateOnly? wasFor, int? wasDays, DateOnly? noticeFor, int? noticeDays, CancellationToken ct) =>
        await _db.OeProjectEnvironments
            .Where(e => e.Id == environmentId
                        && e.LicenseExpiryNoticeFor == wasFor
                        && e.LicenseExpiryNoticeDays == wasDays)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.LicenseExpiryNoticeFor, noticeFor)
                .SetProperty(e => e.LicenseExpiryNoticeDays, noticeDays), ct) == 1;

    private async Task SendAsync(
        int environmentId, int projectId, string environmentName, string solutionName, DateOnly expires, int daysLeft, CancellationToken ct)
    {
        var recipients = await _follows.ListFollowerIdsAsync(projectId, ct);
        if (recipients.Count == 0) return;
        var date = expires.ToString(DateFormat, CultureInfo.InvariantCulture);
        var path = $"/environments/{environmentId}";
        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Solutions,
            recipients,
            new NotificationSummary(
                LicenceExpiryEmail.SubjectFor(solutionName, environmentName, daysLeft),
                daysLeft <= 0 ? $"Expired on {date}." : $"Expires on {date}.",
                path,
                solutionName),
            (email, token) => LicenceExpiryEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, solutionName, environmentName,
                date, daysLeft, email.ItemUrl, email.SettingsUrl, token),
            ProjectId: projectId),
            ct);
    }
}
