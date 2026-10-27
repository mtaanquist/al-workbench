using System.Text.RegularExpressions;
using ALDevToolbox.Components.Email;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.GitHub;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Notifications;

/// <summary>
/// Tells people when a build pipeline starts failing or works again (issue
/// #1035). Called once a build has finished; sends only when the result
/// differs from the build before it, so a pipeline that fails every night
/// produces one email, not one per night. See <c>.design/notifications.md</c>.
/// </summary>
public sealed partial class BuildNotifier
{
    private readonly AppDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ProjectFollowService _follows;
    private readonly ILogger<BuildNotifier> _logger;

    public BuildNotifier(AppDbContext db, NotificationService notifications, ProjectFollowService follows, ILogger<BuildNotifier> logger)
    {
        _db = db;
        _notifications = notifications;
        _follows = follows;
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
                SolutionOwner = b.Project!.CreatedByUserId,
            })
            .FirstOrDefaultAsync(ct);
        // A pull request build already reports on the pull request itself, and
        // a build outside a pipeline (a GitHub release) has no state to change.
        if (build?.PipelineId is not { } pipelineId
            || build.Trigger == ProjectBuildTrigger.PullRequest
            || build.Status is not (ProjectBuildStatus.Ready or ProjectBuildStatus.Failed)
            // Refused because the pipeline no longer asked for it (#1112): nothing broke.
            || ProjectBuildService.AutomationRefusals.Contains(build.FailureMessage))
        {
            return;
        }

        // The finished build of the same kind before this one, by finish time:
        // after a restart, builds can finish out of the order they started in.
        var finishedAt = build.FinishedAt ?? DateTime.MaxValue;
        // A build on push is the pipeline's ordinary build started another way, so it
        // is compared with the manual builds and they with it (#1079).
        string[] sameKind = build.Trigger is ProjectBuildTrigger.Manual or ProjectBuildTrigger.Push
            ? [ProjectBuildTrigger.Manual, ProjectBuildTrigger.Push]
            : [build.Trigger];
        var refusals = ProjectBuildService.AutomationRefusals;
        var previous = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId == pipelineId
                && sameKind.Contains(b.Trigger)
                && b.BcTarget == build.BcTarget
                && b.Id != build.Id
                && b.FinishedAt != null
                && (b.FinishedAt < finishedAt || (b.FinishedAt == finishedAt && b.Id < build.Id))
                && (b.Status == ProjectBuildStatus.Ready || b.Status == ProjectBuildStatus.Failed)
                && (b.FailureMessage == null || !refusals.Contains(b.FailureMessage)))
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
        // Whoever pressed Build, the authors of its commits, and the followers who develop
        // the solution (#1463, #1464). A build on push or a nightly check runs as whoever
        // last saved the pipeline, who did not start it, so it is told only as a follower.
        // A nightly check fails because Business Central moved, not because of the
        // commits since the last good build, so their authors are left out of it.
        var pressedBuild = build.Trigger == ProjectBuildTrigger.Manual ? build.StartedByUserId : null;
        List<int> authors = nightly ? [] : await CommitAuthorsAsync(build.Id, ct);
        var followers = await _follows.ListFollowerIdsAsync(build.ProjectId, NotificationAudience.Builds, ct);
        var recipients = await NotificationAudience.RecipientsAsync(
            _db, build.ProjectId, [pressedBuild, .. authors.Select(a => (int?)a)], followers, build.SolutionOwner, ct);
        if (recipients.Count == 0) return;

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
                string.IsNullOrWhiteSpace(failure) ? null : NotificationText.FirstLine(failure),
                buildPath,
                build.SolutionName),
            (email, token) => BuildNotificationEmail.RenderAsync(
                email.Renderer, email.Recipient.DisplayName, email.OrganizationName, build.SolutionName, build.PipelineName,
                failed, nightly, target, build.BcVersion, failure, email.ItemUrl, email.SettingsUrl, token),
            ProjectId: build.ProjectId),
            ct);
    }

    /// <summary>
    /// The people whose commits are in build <paramref name="buildId"/>: the commits since
    /// its pipeline's last successful build, which are what can have broken it, or, once
    /// it works again, what broke it and what fixed it. An author is matched by the email
    /// Git recorded, to an account with that address or, for GitHub's private
    /// <c>id+login@users.noreply.github.com</c> addresses, to whoever linked that GitHub
    /// account. Authors who match nobody are skipped; the access re-check still applies.
    /// </summary>
    private async Task<List<int>> CommitAuthorsAsync(int buildId, CancellationToken ct)
    {
        var emails = await _db.OeProjectBuildCommits.AsNoTracking()
            .Where(c => c.ProjectBuildId == buildId && c.AuthorEmail != null)
            .Select(c => c.AuthorEmail!)
            .Distinct()
            .ToListAsync(ct);
        if (emails.Count == 0) return [];

        var gitHubIds = emails
            .Select(e => GitHubNoReplyEmail().Match(e))
            .Where(m => m.Success)
            .Select(m => m.Groups["id"].Value)
            .ToList();
        var addresses = emails.Select(e => e.ToLowerInvariant()).ToList();

        // The organisation's query filter keeps both reads to its own people.
        var byEmail = await _db.Users.AsNoTracking()
            .Where(u => addresses.Contains(u.Email.ToLower()))
            .Select(u => u.Id)
            .ToListAsync(ct);
        List<int> byGitHub = gitHubIds.Count == 0 ? [] : await _db.UserExternalLogins.AsNoTracking()
            .Where(l => l.Provider == GitHubAccessService.ProviderName && gitHubIds.Contains(l.Subject))
            .Select(l => l.UserId)
            .ToListAsync(ct);
        return byEmail.Concat(byGitHub).Distinct().ToList();
    }

    /// <summary>GitHub's private commit address, which carries the account's numeric id.</summary>
    [GeneratedRegex(@"^(?<id>\d+)\+[^@]+@users\.noreply\.github\.com$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GitHubNoReplyEmail();

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
            .Select(r => string.IsNullOrWhiteSpace(r.Message) ? r.AppName : $"{r.AppName}: {NotificationText.FirstLine(r.Message)}")
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
}
