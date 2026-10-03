using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells people when a build pipeline starts failing or works again (issue
/// #1035). Called once a build has finished; sends only when the result
/// differs from the build before it, so a pipeline that fails every night
/// produces one email, not one per night. See <c>.design/notifications.md</c>.
/// </summary>
public sealed class BuildNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<BuildNotifier> _logger;

    public BuildNotifier(AppDbContext db, NotificationService notifications, ILogger<BuildNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Notifies about the finished build that produced release
    /// <paramref name="releaseId"/> when it changed its pipeline's state. Never throws (cancellation aside): the
    /// build has already finished, and a notification must not undo that.
    /// </summary>
    public async Task BuildFinishedAsync(int releaseId, CancellationToken ct = default)
    {
        if (!_notifications.IsEnabled) return;
        try
        {
            await NotifyAsync(releaseId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not work out notifications for the build of release {ReleaseId}.", releaseId);
        }
    }

    private async Task NotifyAsync(int releaseId, CancellationToken ct)
    {
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.ReleaseId == releaseId)
            .Select(b => new
            {
                b.Id,
                b.OrganizationId,
                b.PipelineId,
                b.Trigger,
                b.BcTarget,
                b.Status,
                b.BcVersion,
                b.FailureMessage,
                b.StartedByUserId,
                SolutionName = b.Project!.Name,
                PipelineName = b.Pipeline!.Name,
                PipelineCreatedBy = b.Pipeline!.CreatedByUserId,
                PreviewCheckBy = b.Pipeline!.PreviewCheckByUserId,
            })
            .FirstOrDefaultAsync(ct);
        // A pull request build already reports on the pull request itself, and
        // a build outside a pipeline (a GitHub release) has no state to change.
        if (build?.PipelineId is not { } pipelineId
            || build.Trigger == ProjectBuildTrigger.PullRequest
            || build.Status is not (ProjectBuildStatus.Ready or ProjectBuildStatus.Failed))
        {
            return;
        }

        var previous = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId
                && b.Trigger == build.Trigger
                && b.BcTarget == build.BcTarget
                && b.Id < build.Id
                && (b.Status == ProjectBuildStatus.Ready || b.Status == ProjectBuildStatus.Failed))
            .OrderByDescending(b => b.Id)
            .Select(b => b.Status)
            .FirstOrDefaultAsync(ct);
        var failed = build.Status == ProjectBuildStatus.Failed;
        if (!IsChange(failed, previous)) return;

        var nightly = build.Trigger == ProjectBuildTrigger.PreviewCheck;
        var recipients = new[] { nightly ? build.PreviewCheckBy : build.StartedByUserId, build.PipelineCreatedBy }
            .OfType<int>()
            .Distinct()
            .ToList();
        if (recipients.Count == 0) return;

        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == build.OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(ct);
        var target = ProjectBuildTarget.IsPreview(build.BcTarget) ? ProjectBuildTarget.Label(build.BcTarget) : null;
        var buildUrl = _notifications.Link($"/pipelines/{pipelineId}?build={build.Id}")!;
        var settingsUrl = _notifications.Link(NotificationService.SettingsPath)!;
        var subject = BuildNotificationEmail.SubjectFor(failed, nightly, build.SolutionName, build.PipelineName, target);

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Builds,
            recipients,
            new NotificationDigestEntry(
                subject,
                failed && !string.IsNullOrWhiteSpace(build.FailureMessage) ? FirstLine(build.FailureMessage) : null,
                buildUrl,
                build.SolutionName),
            (renderer, recipient, token) => BuildNotificationEmail.RenderAsync(
                renderer, recipient.DisplayName, organizationName, build.SolutionName, build.PipelineName,
                failed, nightly, target, build.BcVersion, build.FailureMessage, buildUrl, settingsUrl, token)),
            ct);
    }

    /// <summary>
    /// Whether a build's result is news: a failure after a success (or as the
    /// first build), or a success after a failure. A first build that works is
    /// what people expect, so it is not.
    /// </summary>
    internal static bool IsChange(bool failed, string? previousStatus) => failed
        ? previousStatus != ProjectBuildStatus.Failed
        : previousStatus == ProjectBuildStatus.Failed;

    private static string FirstLine(string message)
    {
        var line = message.Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200].TrimEnd() + "..." : line;
    }
}
