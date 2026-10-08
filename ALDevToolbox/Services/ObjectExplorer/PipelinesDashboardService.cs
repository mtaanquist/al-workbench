using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Projects;
using ALDevToolbox.Services.Organizations;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer;

/// <summary>
/// Everything the Pipelines dashboard (<c>/pipelines</c>, issue #955) shows, in one call:
/// the seven tile numbers with the facts under them, what needs attention, and a merged
/// timeline of builds and deployments. Read-only, and gated like the two lists it sits
/// above: every row comes from a solution <see cref="ProjectAccess.VisibleProjectPredicate"/>
/// lets the caller see, so a Private solution the caller is not on contributes nothing.
///
/// <para>The deployment side is <see cref="ReleasePipelineService.ListReleasePipelineOverviewAsync"/>
/// and <see cref="DeliveryFeedService.ListRecentAsync"/>, which already answer "what is
/// shipping, what failed, what is waiting" and "what happened lately"; the build side is
/// a few grouped queries of its own, because <c>ArtifactService.ListPipelinesAsync</c>
/// reads every build of every pipeline to find the newest. Facts only: no durations and
/// no forecasts. See <c>.design/saas-delivery.md</c>, "Pipelines dashboard".</para>
/// </summary>
public sealed class PipelinesDashboardService
{
    /// <summary>How many rows the merged activity timeline holds.</summary>
    public const int ActivityRows = 10;

    /// <summary>How far ahead a Business Central secret's expiry counts as needing attention.</summary>
    public static readonly TimeSpan SecretWarning = TimeSpan.FromDays(14);

    private readonly AppDbContext _db;
    private readonly ProjectAccess _access;
    private readonly ReleasePipelineService _releases;
    private readonly DeliveryFeedService _feed;
    private readonly BuildFreshnessService _freshness;
    private readonly DisplayTimeZone _zone;
    private readonly TimeProvider _clock;

    public PipelinesDashboardService(
        AppDbContext db,
        ProjectAccess access,
        ReleasePipelineService releases,
        DeliveryFeedService feed,
        BuildFreshnessService freshness,
        DisplayTimeZone zone,
        TimeProvider clock)
    {
        _db = db;
        _access = access;
        _releases = releases;
        _feed = feed;
        _freshness = freshness;
        _zone = zone;
        _clock = clock;
    }

    /// <summary>
    /// Assembles the page. "Today" is the organisation's day, in the zone it shows times
    /// in, so "3 builds today" agrees with the timestamps beside it; "this week" is the
    /// last seven days.
    /// </summary>
    public async Task<PipelinesDashboardData> GetAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var todayStart = await StartOfTodayAsync(now, ct);
        var weekStart = now.AddDays(-7);

        var snapshot = await _access.GetSnapshotAsync(ct);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        // ── Build side ─────────────────────────────────────────────────────────
        var pipelines = await _db.OePipelines.AsNoTracking()
            .Where(p => p.DeletedAt == null)
            .Where(p => _db.OeProjects.Where(visible).Any(v => v.Id == p.ProjectId))
            .Select(p => new { p.Id, p.Name, p.ProjectId, ProjectName = p.Project!.Name, p.PreviewCheck, Disabled = p.DisabledAt != null })
            .ToListAsync(ct);
        var pipelineIds = pipelines.Select(p => p.Id).ToList();
        var pipelineById = pipelines.ToDictionary(p => p.Id);

        var builds = _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.PipelineId != null && pipelineIds.Contains(b.PipelineId!.Value));

        // The newest build of each pipeline: what "failed builds" counts, the same
        // question the Builds list's Failed tab asks. The nightly preview check's
        // builds are counted on their own below, not as the pipeline's latest.
        var latest = pipelineIds.Count == 0
            ? []
            : await builds
                .Where(b => b.BcTarget == ProjectBuildTarget.Current)
                .GroupBy(b => b.PipelineId!.Value)
                .Select(g => g
                    .OrderByDescending(b => b.StartedAt)
                    .ThenByDescending(b => b.Id)
                    .Select(b => new
                    {
                        b.Id,
                        PipelineId = b.PipelineId!.Value,
                        b.Status,
                        b.Branch,
                        b.StartedAt,
                        b.FinishedAt,
                        b.FailureMessage,
                    })
                    .First())
                .ToListAsync(ct);

        // Pipelines whose newest preview check against either upcoming version did not
        // pass (#994): a failed build, or a ready one with an extension that did not
        // compile.
        // Only pipelines that still run the check, as the Builds list does: a check
        // turned off is not failing any more, and a disabled pipeline runs no check (#1131).
        var checkedPipelineIds = pipelines.Where(p => p.PreviewCheck && !p.Disabled).Select(p => p.Id).ToList();
        var previewChecks = checkedPipelineIds.Count == 0
            ? []
            : await builds
                .Where(b => b.BcTarget != ProjectBuildTarget.Current && checkedPipelineIds.Contains(b.PipelineId!.Value))
                // The newest per pipeline and target, picked in SQL (#1138).
                .GroupBy(b => new { PipelineId = b.PipelineId!.Value, b.BcTarget })
                .Select(g => g
                    .OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id)
                    .Select(b => new { b.Id, PipelineId = b.PipelineId!.Value, b.BcTarget, b.Status, b.ReleaseId, b.StartedAt })
                    .First())
                .ToListAsync(ct);
        var checkedReleaseIds = previewChecks
            .Where(b => b.Status == ProjectBuildStatus.Ready && b.ReleaseId != null)
            .Select(b => b.ReleaseId!.Value)
            .ToList();
        var releasesWithFailures = checkedReleaseIds.Count == 0
            ? []
            : (await _db.OeProjectBuildResults.AsNoTracking()
                .Where(r => checkedReleaseIds.Contains(r.ReleaseId) && r.Status == ProjectBuildResultStatus.Failed)
                .Select(r => r.ReleaseId)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();
        var failedPreviewPipelines = previewChecks
            .Where(b => b.Status == ProjectBuildStatus.Failed
                        || (b.ReleaseId is { } r && releasesWithFailures.Contains(r)))
            .Select(b => b.PipelineId)
            .ToHashSet();

        var counts = pipelineIds.Count == 0
            ? null
            : await builds
                .Where(b => b.StartedAt >= weekStart && b.BcTarget == ProjectBuildTarget.Current)
                .GroupBy(_ => 1)
                .Select(g => new { Week = g.Count(), Today = g.Count(b => b.StartedAt >= todayStart) })
                .FirstOrDefaultAsync(ct);

        var recentBuilds = pipelineIds.Count == 0
            ? []
            : await builds
                .OrderByDescending(b => b.FinishedAt ?? b.StartedAt)
                .ThenByDescending(b => b.Id)
                .Take(ActivityRows)
                .Select(b => new
                {
                    b.Id,
                    PipelineId = b.PipelineId!.Value,
                    b.Status,
                    b.Branch,
                    b.Trigger,
                    b.BcTarget,
                    b.PullRequestNumber,
                    b.StartedAt,
                    b.FinishedAt,
                    b.FailureMessage,
                    StartedBy = b.StartedByUser != null ? b.StartedByUser.DisplayName : null,
                    AppCount = b.Artifacts.Count,
                })
                .ToListAsync(ct);

        var newest = latest.OrderByDescending(b => b.StartedAt).ThenByDescending(b => b.Id).FirstOrDefault();
        var lastBuild = newest is null
            ? null
            : new PipelinesLastBuild(newest.Id, newest.PipelineId, pipelineById[newest.PipelineId].Name, newest.Branch, newest.StartedAt);

        // ── Deployment side ────────────────────────────────────────────────────
        var targets = await _releases.ListReleasePipelineOverviewAsync(ct);

        var deploymentsToday = targets.Count == 0
            ? 0
            : await _db.OeProjectDeliveries.AsNoTracking()
                .Where(d => d.StartedAt >= todayStart)
                .Where(d => _db.OeProjects.Where(visible).Any(p => p.Id == d.ProjectId && p.DeletedAt == null))
                .CountAsync(ct);

        var shipping = targets
            .Where(t => t.LiveDelivery is not null)
            .OrderBy(t => t.LiveDelivery!.StartedAt ?? DateTime.MaxValue)
            .Select(t => new PipelinesShipping(
                t.Id, t.ProjectName, t.EnvironmentName,
                t.LiveDelivery!.CurrentApp, t.LiveDelivery.AppCount))
            .ToList();
        var waiting = targets.Where(t => t.ProposedDelivery is not null).ToList();

        // ── Needs attention ────────────────────────────────────────────────────
        var attention = new List<PipelinesAttentionItem>();

        foreach (var b in latest.Where(b => b.Status == ProjectBuildStatus.Failed))
        {
            var p = pipelineById[b.PipelineId];
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.FailedBuild, b.FinishedAt ?? b.StartedAt, p.ProjectId, p.ProjectName,
                PipelineId: p.Id, PipelineName: p.Name, BuildId: b.Id, Branch: b.Branch,
                Detail: b.FailureMessage));
        }

        // Build pipelines whose branch has moved past their last successful build (#964),
        // unless a build of theirs is already queued or running - that build is the answer.
        var inFlight = latest
            .Where(b => b.Status is ProjectBuildStatus.Queued or ProjectBuildStatus.Building)
            .Select(b => b.PipelineId)
            .ToHashSet();
        var readyToBuild = 0;
        foreach (var f in pipelineIds.Count == 0 ? [] : await _freshness.ListAsync(ct))
        {
            var summary = PipelineFreshnessSummary.From(f);
            if (!summary.IsAhead || inFlight.Contains(f.PipelineId) || !pipelineById.TryGetValue(f.PipelineId, out var p)) continue;
            readyToBuild++;
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.BranchAhead, summary.PushedAt, p.ProjectId, p.ProjectName,
                PipelineId: p.Id, PipelineName: p.Name, BuildId: summary.LastBuildId, Branch: summary.Branch,
                Freshness: summary));
        }

        foreach (var t in targets.Where(t => t.LiveDelivery is null && t.LastDelivery?.Status == ProjectDeliveryStatus.Failed))
        {
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.FailedDeployment, t.LastDelivery!.At, t.ProjectId, t.ProjectName,
                PipelineId: t.Id, PipelineName: t.Name, EnvironmentName: t.EnvironmentName,
                Detail: t.LastDelivery.FailedAppName));
        }

        foreach (var t in waiting)
        {
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.WaitingForApproval, t.ProposedDelivery!.PreparedAt, t.ProjectId, t.ProjectName,
                PipelineId: t.Id, PipelineName: t.Name, BuildId: t.ProposedDelivery.BuildId,
                EnvironmentName: t.EnvironmentName));
        }

        await AddEnvironmentProblemsAsync(targets, attention, ct);
        await AddExpiringSecretsAsync(targets, now, attention, ct);

        attention = attention
            .OrderBy(a => a.Kind is PipelinesAttentionKind.SecretExpiring or PipelinesAttentionKind.SharedSecretExpiring ? 2 : a.At is null ? 1 : 0)
            .ThenByDescending(a => a.At)
            .ThenBy(a => a.ExpiresAt)
            .ThenBy(a => a.ProjectName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ── Recent activity ────────────────────────────────────────────────────
        var activity = new List<PipelinesActivityItem>();
        foreach (var b in recentBuilds)
        {
            var p = pipelineById[b.PipelineId];
            var (kind, at) = b.Status switch
            {
                ProjectBuildStatus.Ready => (PipelinesActivityKind.BuildSucceeded, b.FinishedAt ?? b.StartedAt),
                ProjectBuildStatus.Failed => (PipelinesActivityKind.BuildFailed, b.FinishedAt ?? b.StartedAt),
                _ => (PipelinesActivityKind.BuildRunning, b.StartedAt),
            };
            // The nightly preview check runs as a person, but nobody pressed anything:
            // the pipeline is the actor, and its name says which check it was.
            var previewCheck = ProjectBuildTarget.IsPreview(b.BcTarget);
            // A build on push likewise: the push started it, not the person it runs as.
            var actor = b.Trigger == ProjectBuildTrigger.PullRequest ? PipelinesActor.PullRequest
                : previewCheck || b.Trigger == ProjectBuildTrigger.Push ? PipelinesActor.Pipeline
                : b.StartedBy is not null ? PipelinesActor.Person
                : PipelinesActor.Unknown;
            activity.Add(new PipelinesActivityItem(
                kind, at, actor, previewCheck ? null : b.StartedBy, p.ProjectId, p.ProjectName,
                BuildId: b.Id, PipelineId: p.Id,
                PipelineName: previewCheck ? $"{p.Name} ({ProjectBuildTarget.Label(b.BcTarget)} check)" : p.Name,
                Branch: b.Branch,
                PullRequestNumber: b.PullRequestNumber, AppCount: b.AppCount, Detail: b.FailureMessage));
        }

        if (targets.Count > 0)
        {
            // Twice the rows, because dismissed prepared deployments are dropped below:
            // one never became a deployment, so it is not something that happened.
            var liveById = targets.Where(t => t.LiveDelivery is not null)
                .ToDictionary(t => t.LiveDelivery!.DeliveryId, t => t.LiveDelivery!);
            var deliveries = await _feed.ListRecentAsync(null, null, ActivityRows * 2, ct);
            foreach (var d in deliveries.Where(d => d.Status != ProjectDeliveryStatus.Dismissed))
            {
                var kind = d.Status switch
                {
                    ProjectDeliveryStatus.Proposed => PipelinesActivityKind.DeploymentPrepared,
                    ProjectDeliveryStatus.Scheduled => PipelinesActivityKind.DeploymentScheduled,
                    ProjectDeliveryStatus.Deployed => PipelinesActivityKind.DeploymentDeployed,
                    ProjectDeliveryStatus.HandedOff => PipelinesActivityKind.DeploymentHandedOff,
                    ProjectDeliveryStatus.Failed => PipelinesActivityKind.DeploymentFailed,
                    ProjectDeliveryStatus.Cancelled => PipelinesActivityKind.DeploymentCancelled,
                    _ => PipelinesActivityKind.DeploymentRunning,
                };
                var at = kind switch
                {
                    PipelinesActivityKind.DeploymentPrepared or PipelinesActivityKind.DeploymentScheduled => d.CreatedAt,
                    PipelinesActivityKind.DeploymentRunning => d.StartedAt ?? d.CreatedAt,
                    _ => d.FinishedAt ?? d.StartedAt ?? d.CreatedAt,
                };
                var actor = kind == PipelinesActivityKind.DeploymentPrepared ? PipelinesActor.Pipeline
                    : d.TriggeredByName is not null ? PipelinesActor.Person
                    : PipelinesActor.Unknown;
                liveById.TryGetValue(d.Id, out var live);
                activity.Add(new PipelinesActivityItem(
                    kind, at, actor, d.TriggeredByName, d.ProjectId, d.ProjectName,
                    BuildId: d.BuildId, ReleasePipelineId: d.ReleasePipelineId, EnvironmentName: d.EnvironmentName,
                    CurrentApp: live?.CurrentApp ?? 0, AppCount: live?.AppCount ?? 0,
                    FailedAppName: d.Apps.FirstOrDefault(a => a.Status == ProjectDeliveryResultStatus.Failed)?.AppName,
                    Detail: d.FailureMessage));
            }
        }

        activity = activity
            .OrderByDescending(a => a.At)
            .ThenByDescending(a => a.BuildId)
            .Take(ActivityRows)
            .ToList();

        return new PipelinesDashboardData(
            BuildPipelineCount: pipelines.Count,
            LastBuild: lastBuild,
            BuildsThisWeek: counts?.Week ?? 0,
            BuildsToday: counts?.Today ?? 0,
            FailedBuildPipelines: latest.Where(b => b.Status == ProjectBuildStatus.Failed).Select(b => b.PipelineId)
                .Union(failedPreviewPipelines).Count(),
            FailedPreviewBuildPipelines: failedPreviewPipelines.Count,
            ReadyToBuild: readyToBuild,
            DeploymentPipelineCount: targets.Count,
            LastDeploymentAt: targets.Where(t => t.LastDelivery is not null).Select(t => (DateTime?)t.LastDelivery!.At).Max(),
            ShippingNow: shipping,
            FailedDeploymentPipelines: targets.Count(t => t.LiveDelivery is null && t.LastDelivery?.Status == ProjectDeliveryStatus.Failed),
            WaitingForApproval: waiting.Count,
            OldestWaitingAt: waiting.Select(t => (DateTime?)t.ProposedDelivery!.PreparedAt).Min(),
            DeploymentsToday: deploymentsToday,
            Attention: attention,
            Activity: activity);
    }

    /// <summary>
    /// Deployment pipelines aimed at an environment that is gone, being deleted, or failed
    /// in Business Central: each refuses deployments until someone re-points it. The time
    /// is when the mirror noticed it missing, or when Business Central began deleting it;
    /// a failed environment has no such moment and goes without one.
    /// </summary>
    private async Task AddEnvironmentProblemsAsync(
        List<ReleasePipelineRow> targets, List<PipelinesAttentionItem> attention, CancellationToken ct)
    {
        var broken = targets.Where(t => t.EnvironmentProblem is not null).ToList();
        if (broken.Count == 0) return;

        var environmentIds = broken.Select(t => t.ProjectEnvironmentId).Distinct().ToList();
        var since = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => environmentIds.Contains(e.Id))
            .Select(e => new { e.Id, At = e.MissingSince ?? e.SoftDeletedOn })
            .ToDictionaryAsync(e => e.Id, e => e.At, ct);

        foreach (var t in broken)
        {
            var kind = t.EnvironmentMissing ? PipelinesAttentionKind.EnvironmentMissing
                : BcEnvironmentStatus.Classify(t.EnvironmentStatus) == BcEnvironmentReadiness.Deleting
                    ? PipelinesAttentionKind.EnvironmentDeleting
                    : PipelinesAttentionKind.EnvironmentFailed;
            attention.Add(new PipelinesAttentionItem(
                kind, since.GetValueOrDefault(t.ProjectEnvironmentId), t.ProjectId, t.ProjectName,
                PipelineId: t.Id, PipelineName: t.Name, EnvironmentName: t.EnvironmentName));
        }
    }

    /// <summary>
    /// Business Central client secrets that lapse within <see cref="SecretWarning"/> (or
    /// already have), on solutions with a deployment pipeline: a deployment scheduled past
    /// that date fails. A solution with its own app registration gets a row of its own;
    /// the ones on the organisation's shared registration share one row, since rotating
    /// that one secret fixes them all. Reads the expiry date and whether a client id is
    /// set, never a secret.
    /// </summary>
    private async Task AddExpiringSecretsAsync(
        List<ReleasePipelineRow> targets, DateTime now, List<PipelinesAttentionItem> attention, CancellationToken ct)
    {
        var projectIds = targets.Select(t => t.ProjectId).Distinct().ToList();
        if (projectIds.Count == 0) return;

        var solutions = await _db.OeProjects.AsNoTracking()
            .Where(p => projectIds.Contains(p.Id) && p.DeletedAt == null && p.BcTenantId != null)
            .Select(p => new
            {
                p.Id,
                p.Name,
                Own = p.BcClientId != null,
                OwnExpires = p.BcClientSecretExpiresAt,
                SharedExpires = _db.OrganizationSettings
                    .Where(o => o.OrganizationId == p.OrganizationId && o.BcClientId != null)
                    .Select(o => o.BcClientSecretExpiresAt)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        var horizon = now + SecretWarning;
        foreach (var s in solutions.Where(s => s.Own && s.OwnExpires is { } e && e <= horizon))
        {
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.SecretExpiring, null, s.Id, s.Name, ExpiresAt: s.OwnExpires));
        }

        var shared = solutions
            .Where(s => !s.Own && s.SharedExpires is { } e && e <= horizon)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (shared.Count > 0)
        {
            attention.Add(new PipelinesAttentionItem(
                PipelinesAttentionKind.SharedSecretExpiring, null, shared[0].Id, shared[0].Name,
                ExpiresAt: shared[0].SharedExpires, SolutionCount: shared.Count));
        }
    }

    /// <summary>Midnight today in the organisation's display zone, as a UTC instant.</summary>
    private async Task<DateTime> StartOfTodayAsync(DateTime nowUtc, CancellationToken ct)
    {
        var zone = await _zone.EnsureLoadedAsync(ct);
        var midnight = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone).Date, DateTimeKind.Unspecified);
        // A zone whose clocks jump at midnight skips it; the day then starts an hour on.
        if (zone.IsInvalidTime(midnight)) midnight = midnight.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
    }
}

/// <summary>The Pipelines dashboard, assembled. Counts cover only the solutions the caller can see.</summary>
/// <param name="LastBuild">The newest build of any build pipeline, or null when none has run.</param>
/// <param name="FailedBuildPipelines">Build pipelines whose newest build failed, or whose nightly preview check did not pass.</param>
/// <param name="FailedPreviewBuildPipelines">
/// Of <paramref name="FailedBuildPipelines"/>, those whose preview check did not pass (#994):
/// counted separately because a red next-version check is the signal it exists to give,
/// not a broken customer build.
/// </param>
/// <param name="LastDeploymentAt">When the newest finished deployment finished, or null.</param>
/// <param name="ShippingNow">Deployments running this moment, the longest-running first.</param>
/// <param name="FailedDeploymentPipelines">Deployment pipelines whose last deployment failed and nothing is running now.</param>
/// <param name="WaitingForApproval">Deployments prepared from a new build and waiting for a person (#934).</param>
/// <param name="DeploymentsToday">Deployments that started sending apps today.</param>
public sealed record PipelinesDashboardData(
    int BuildPipelineCount,
    PipelinesLastBuild? LastBuild,
    int BuildsThisWeek,
    int BuildsToday,
    int FailedBuildPipelines,
    int ReadyToBuild,
    int DeploymentPipelineCount,
    DateTime? LastDeploymentAt,
    List<PipelinesShipping> ShippingNow,
    int FailedDeploymentPipelines,
    int WaitingForApproval,
    DateTime? OldestWaitingAt,
    int DeploymentsToday,
    List<PipelinesAttentionItem> Attention,
    List<PipelinesActivityItem> Activity,
    int FailedPreviewBuildPipelines = 0);

/// <summary>The newest build: which one, on which pipeline and branch (null for a manual build), and when it started.</summary>
public sealed record PipelinesLastBuild(int BuildId, int PipelineId, string PipelineName, string? Branch, DateTime At);

/// <summary>A deployment running now: where to, and which app of how many it is on (0 of 0 before the first upload).</summary>
public sealed record PipelinesShipping(int ReleasePipelineId, string ProjectName, string EnvironmentName, int CurrentApp, int AppCount);

/// <summary>What a "Needs attention" row is about.</summary>
public enum PipelinesAttentionKind
{
    FailedBuild,
    /// <summary>A build pipeline's branch has moved past its last successful build (#964).</summary>
    BranchAhead,
    FailedDeployment,
    WaitingForApproval,
    EnvironmentMissing,
    EnvironmentDeleting,
    EnvironmentFailed,
    SecretExpiring,
    SharedSecretExpiring,
}

/// <summary>
/// One "Needs attention" row, as facts; the page words it. Which fields are set depends on
/// <paramref name="Kind"/>: <paramref name="PipelineId"/> is the build pipeline for a failed
/// build and the deployment pipeline for every deployment kind.
/// </summary>
/// <param name="At">When it happened, for the relative time; null when there is no such moment.</param>
/// <param name="Detail">The failure message of a build, or the name of the app a deployment failed on.</param>
/// <param name="ExpiresAt">When the secret lapses, for the two secret kinds.</param>
/// <param name="SolutionCount">How many solutions share the organisation's secret, for <see cref="PipelinesAttentionKind.SharedSecretExpiring"/>.</param>
/// <param name="Freshness">What moved on the branch, for <see cref="PipelinesAttentionKind.BranchAhead"/>.</param>
public sealed record PipelinesAttentionItem(
    PipelinesAttentionKind Kind,
    DateTime? At,
    int ProjectId,
    string ProjectName,
    int? PipelineId = null,
    string? PipelineName = null,
    int? BuildId = null,
    string? Branch = null,
    string? EnvironmentName = null,
    string? Detail = null,
    DateTime? ExpiresAt = null,
    int SolutionCount = 0,
    PipelineFreshnessSummary? Freshness = null);

/// <summary>What one row of the activity timeline records.</summary>
public enum PipelinesActivityKind
{
    BuildRunning,
    BuildSucceeded,
    BuildFailed,
    DeploymentPrepared,
    DeploymentScheduled,
    DeploymentRunning,
    DeploymentDeployed,
    DeploymentHandedOff,
    DeploymentFailed,
    DeploymentCancelled,
}

/// <summary>
/// Who is behind a timeline row: a person, a pull request (the only build that starts
/// without one), the deployment pipeline itself preparing a deployment, or nobody we
/// still know of (an account since removed).
/// </summary>
public enum PipelinesActor { Person, PullRequest, Pipeline, Unknown }

/// <summary>
/// One row of the merged timeline, at its latest moment: a build that is running shows
/// when it started, a finished one when it finished; a deployment likewise.
/// </summary>
/// <param name="PipelineId">The build pipeline, for a build row.</param>
/// <param name="ReleasePipelineId">The deployment pipeline, for a deployment row.</param>
/// <param name="AppCount">Apps a finished build produced, or apps a running deployment sends.</param>
/// <param name="CurrentApp">The app a running deployment is on, 1-based; 0 otherwise.</param>
public sealed record PipelinesActivityItem(
    PipelinesActivityKind Kind,
    DateTime At,
    PipelinesActor Actor,
    string? ActorName,
    int ProjectId,
    string ProjectName,
    int BuildId,
    int? PipelineId = null,
    string? PipelineName = null,
    string? Branch = null,
    int? PullRequestNumber = null,
    int? ReleasePipelineId = null,
    string? EnvironmentName = null,
    int AppCount = 0,
    int CurrentApp = 0,
    string? FailedAppName = null,
    string? Detail = null);
