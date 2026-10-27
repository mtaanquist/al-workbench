using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>What happened to a deployment, as its notification email tells it.</summary>
public enum DeploymentOutcome
{
    WaitingForApproval,
    Deployed,
    /// <summary>Business Central accepted the apps and installs them on its own schedule.</summary>
    HandedOff,
    Failed,
}

/// <summary>
/// Tells people about deployments (issue #1036): one waiting for approval goes
/// to the solution's followers who look after its deployments, straight away
/// whatever their digest choice; a finished one goes to them and to whoever
/// started or approved it (#1463). See <c>.design/notifications.md</c>.
/// </summary>
public sealed class DeploymentNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ProjectFollowService _follows;
    private readonly ILogger<DeploymentNotifier> _logger;

    public DeploymentNotifier(AppDbContext db, NotificationService notifications, ProjectFollowService follows, ILogger<DeploymentNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _follows = follows;
        _logger = logger;
    }

    /// <summary>
    /// Notifies about deployments <paramref name="deliveryIds"/>, just prepared
    /// and waiting for approval. Never throws, except when <paramref name="ct"/>
    /// itself is cancelled.
    /// </summary>
    public async Task ProposedAsync(IReadOnlyList<int> deliveryIds, CancellationToken ct = default)
    {
        foreach (var id in deliveryIds) await NotifyAsync(id, ct);
    }

    /// <summary>
    /// Notifies about deployment <paramref name="deliveryId"/> as it stands:
    /// waiting for approval, deployed, handed off or failed; anything else sends
    /// nothing. Never throws, except when <paramref name="ct"/> itself is
    /// cancelled: the deployment's outcome is already recorded.
    /// </summary>
    public async Task NotifyAsync(int deliveryId, CancellationToken ct = default)
    {
        try
        {
            await SendAsync(deliveryId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not send notifications for deployment {DeliveryId}.", deliveryId);
        }
    }

    private async Task SendAsync(int deliveryId, CancellationToken ct)
    {
        var delivery = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new
            {
                d.Id,
                d.ProjectId,
                d.Status,
                d.ReleasePipelineId,
                d.EnvironmentName,
                d.FailureMessage,
                d.TriggeredByUserId,
                d.DeployedWithoutApproval,
                SolutionName = d.Project!.Name,
                SolutionOwner = d.Project!.CreatedByUserId,
                PipelineName = d.ReleasePipeline!.Name,
                Apps = d.Results.OrderBy(r => r.Ordering).Select(r => r.AppName + " " + r.AppVersion).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (delivery is null || OutcomeFor(delivery.Status) is not { } outcome) return;

        var waiting = outcome == DeploymentOutcome.WaitingForApproval;
        // The followers who look after deployments hear about every one (#1463). A
        // finished one also goes to the person who started, approved or rescheduled it.
        // One deployed without approval runs as whoever turned that on, who did nothing
        // this time (moving one Business Central holds writes a new, approved deployment
        // for the person who moved it, so they still hear); nor does one that was waiting
        // or was refused because the person it ran as lost access (#1125), since they no
        // longer hear about the solution.
        var schedulerLostAccess = delivery.FailureMessage == DeliveryService.SchedulerLostAccess;
        var actor = waiting || schedulerLostAccess || delivery.DeployedWithoutApproval ? null : delivery.TriggeredByUserId;
        var followers = await _follows.ListFollowerIdsAsync(delivery.ProjectId, NotificationAudience.Deployments, ct);
        var recipients = await NotificationAudience.RecipientsAsync(
            _db, delivery.ProjectId, [actor], followers, delivery.SolutionOwner, ct);
        if (recipients.Count == 0) return;

        var pipelinePath = $"/pipelines/deployments/{delivery.ReleasePipelineId}";
        var failure = outcome == DeploymentOutcome.Failed && !string.IsNullOrWhiteSpace(delivery.FailureMessage)
            ? delivery.FailureMessage.Trim()
            : null;

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Deployments,
            recipients,
            new NotificationSummary(
                DeploymentNotificationEmail.SubjectFor(outcome, delivery.SolutionName, delivery.EnvironmentName),
                failure is null ? null : NotificationText.FirstLine(failure),
                pipelinePath,
                delivery.SolutionName),
            (email, token) => DeploymentNotificationEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, outcome, delivery.SolutionName,
                delivery.PipelineName, delivery.EnvironmentName, delivery.Apps, failure,
                email.ItemUrl, email.SettingsUrl, token),
            Urgent: waiting,
            ProjectId: delivery.ProjectId,
            // Only the request asks for anything; once approved or dismissed it is done.
            Subject: waiting ? NotificationSubject.Delivery(delivery.Id) : null),
            ct);
    }

    internal static DeploymentOutcome? OutcomeFor(string status) => status switch
    {
        ProjectDeliveryStatus.Proposed => DeploymentOutcome.WaitingForApproval,
        ProjectDeliveryStatus.Deployed => DeploymentOutcome.Deployed,
        ProjectDeliveryStatus.HandedOff => DeploymentOutcome.HandedOff,
        ProjectDeliveryStatus.Failed => DeploymentOutcome.Failed,
        _ => null,
    };
}
