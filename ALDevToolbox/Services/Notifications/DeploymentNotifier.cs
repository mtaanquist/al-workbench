using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
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
/// to the solution's owner and the deployment pipeline's creator, straight
/// away whatever their digest choice; a finished one goes to whoever started
/// or approved it. See <c>.design/notifications.md</c>.
/// </summary>
public sealed class DeploymentNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<DeploymentNotifier> _logger;

    public DeploymentNotifier(AppDbContext db, NotificationService notifications, ILogger<DeploymentNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
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
                SolutionName = d.Project!.Name,
                SolutionOwner = d.Project!.CreatedByUserId,
                PipelineName = d.ReleasePipeline!.Name,
                PipelineCreator = d.ReleasePipeline!.CreatedByUserId,
                Apps = d.Results.OrderBy(r => r.Ordering).Select(r => r.AppName + " " + r.AppVersion).ToList(),
            })
            .FirstOrDefaultAsync(ct);
        if (delivery is null || OutcomeFor(delivery.Status) is not { } outcome) return;

        var waiting = outcome == DeploymentOutcome.WaitingForApproval;
        // A finished deployment goes to the person it ran as. One that ran on a
        // schedule with nobody behind it goes to the pipeline's creator instead.
        int?[] candidates = waiting
            ? [delivery.SolutionOwner, delivery.PipelineCreator]
            : [delivery.TriggeredByUserId ?? delivery.PipelineCreator];
        var recipients = candidates.OfType<int>().Distinct().ToList();
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
                failure is null ? null : FirstLine(failure),
                pipelinePath,
                delivery.SolutionName),
            (email, token) => DeploymentNotificationEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, outcome, delivery.SolutionName,
                delivery.PipelineName, delivery.EnvironmentName, delivery.Apps, failure,
                email.ItemUrl, email.SettingsUrl, token),
            Urgent: waiting,
            ProjectId: delivery.ProjectId),
            ct);
    }

    private static string FirstLine(string message)
    {
        var line = message.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200].TrimEnd() + "..." : line;
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
