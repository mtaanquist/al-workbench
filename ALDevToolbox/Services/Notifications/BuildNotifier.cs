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
    /// <paramref name="releaseId"/> when it changed its pipeline's state. Never
    /// throws, except when <paramref name="ct"/> itself is cancelled: the build
    /// has already finished, and a notification must not undo that.
    /// </summary>
    public async Task BuildFinishedAsync(int releaseId, CancellationToken ct = default)
    {
        try
        {
            await NotifyAsync(releaseId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
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
                b.ProjectId,
                b.PipelineId,
                b.Trigger,
                b.BcTarget,
                b.Status,
                b.BcVersion,
                b.FailureMessage,
                b.StartedByUserId,
                b.FinishedAt,
                SolutionName = b.Project!.Name,
                PipelineName = b.Pipeline!.Name,
                PipelineCreatedBy = b.Pipeline!.CreatedByUserId,
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

        // The finished build of the same kind before this one, by finish time:
        // after a restart, builds can finish out of the order they started in.
        var finishedAt = build.FinishedAt ?? DateTime.MaxValue;
        var previous = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId
                && b.Trigger == build.Trigger
                && b.BcTarget == build.BcTarget
                && b.Id != build.Id
                && b.FinishedAt != null
                && (b.FinishedAt < finishedAt || (b.FinishedAt == finishedAt && b.Id < build.Id))
                && (b.Status == ProjectBuildStatus.Ready || b.Status == ProjectBuildStatus.Failed))
            .OrderByDescending(b => b.FinishedAt)
            .ThenByDescending(b => b.Id)
            .Select(b => new { b.Status, b.ReleaseId })
            .FirstOrDefaultAsync(ct);

        var failedApps = await FailedAppsAsync(build.BcTarget, build.Status, releaseId, ct);
        var failed = build.Status == ProjectBuildStatus.Failed || failedApps.Count > 0;
        bool? previousFailed = previous is null
            ? null
            : previous.Status == ProjectBuildStatus.Failed
              || (previous.ReleaseId is { } previousRelease
                  && (await FailedAppsAsync(build.BcTarget, previous.Status, previousRelease, ct)).Count > 0);
        if (!IsChange(failed, previousFailed)) return;

        var nightly = build.Trigger == ProjectBuildTrigger.PreviewCheck;
        // StartedByUserId is the person the build ran as: whoever pressed Build,
        // or whoever had the nightly check on when it was queued.
        var recipients = new[] { build.StartedByUserId, build.PipelineCreatedBy }
            .OfType<int>()
            .Distinct()
            .ToList();
        if (recipients.Count == 0) return;

        var organizationName = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == build.OrganizationId)
            .Select(o => o.Name)
            .FirstAsync(ct);
        var target = ProjectBuildTarget.IsPreview(build.BcTarget) ? ProjectBuildTarget.Label(build.BcTarget) : null;
        var buildPath = $"/pipelines/{pipelineId}?build={build.Id}";
        var subject = BuildNotificationEmail.SubjectFor(failed, nightly, build.SolutionName, build.PipelineName, target);
        // The extensions that failed say more than the build's summary line,
        // which for a partly failed build is empty.
        var failure = !failed ? null
            : failedApps.Count > 0 ? string.Join("\n", failedApps)
            : build.FailureMessage;

        await _notifications.NotifyAsync(new Notification(
            NotificationCategory.Builds,
            recipients,
            new NotificationSummary(
                subject,
                string.IsNullOrWhiteSpace(failure) ? null : FirstLine(failure),
                buildPath,
                build.SolutionName),
            (renderer, recipient, token) => BuildNotificationEmail.RenderAsync(
                renderer, recipient.DisplayName, organizationName, build.SolutionName, build.PipelineName,
                failed, nightly, target, build.BcVersion, failure,
                _notifications.Link(buildPath)!, _notifications.Link(NotificationService.SettingsPath)!, token),
            ProjectId: build.ProjectId),
            ct);
    }

    /// <summary>
    /// "Name: message" for each extension that failed in a ready build against
    /// an upcoming version. Such a build is a failed check everywhere else in
    /// the product (the pipeline page, the dashboard), so it is one here too.
    /// A ready build against the current version counts as a success, as it
    /// does on the pipeline page.
    /// </summary>
    private async Task<List<string>> FailedAppsAsync(string bcTarget, string status, int releaseId, CancellationToken ct)
    {
        if (!ProjectBuildTarget.IsPreview(bcTarget) || status != ProjectBuildStatus.Ready) return [];
        var rows = await _db.OeProjectBuildResults.AsNoTracking()
            .Where(r => r.ReleaseId == releaseId && r.Status == ProjectBuildResultStatus.Failed)
            .OrderBy(r => r.AppName)
            .Select(r => new { r.AppName, r.Message })
            .ToListAsync(ct);
        return rows
            .Select(r => string.IsNullOrWhiteSpace(r.Message) ? r.AppName : $"{r.AppName}: {FirstLine(r.Message)}")
            .ToList();
    }

    /// <summary>
    /// Whether a build's result is news: a failure after a success (or as the
    /// first build), or a success after a failure. A first build that works is
    /// what people expect, so it is not.
    /// </summary>
    internal static bool IsChange(bool failed, bool? previousFailed) => failed
        ? previousFailed != true
        : previousFailed == true;

    private static string FirstLine(string message)
    {
        var line = message.Trim().Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200].TrimEnd() + "..." : line;
    }
}
