using System.Text.Json.Serialization;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// Creates and runs <see cref="OeProjectDelivery"/> runs — the publish side of SaaS
/// delivery. A deployment of a successful build to a deployment pipeline's target is created
/// here (access-gated, target snapshotted) and enqueued to <see cref="DeliveryQueue"/>;
/// <see cref="DeliveryWorker"/> then calls <see cref="RunDeliveryAsync"/>, which claims
/// the row and drives the per-app upload → install → poll flow through the
/// <see cref="IBcAppManagementClient"/> seam. Every failure is captured onto the row
/// (never thrown out of the worker). The BC secret never passes through here — only a
/// bearer token from <see cref="ProjectConnectionService"/>. See
/// <c>.design/saas-delivery.md</c> ("Publish flow", "Services &amp; seams").
/// </summary>
public sealed class DeliveryService
{
    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly IDeliveryTokenSource _tokens;
    private readonly IBcAppManagementClient _apps;
    private readonly IBcAdminClient _admin;
    private readonly DeliveryQueue _queue;
    private readonly Bc.BcPanelCache _panelCache;
    private readonly ILogger<DeliveryService> _logger;
    private readonly ALDevToolbox.Services.Tools.ToolEnablement _tools;

    public DeliveryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        IDeliveryTokenSource tokens,
        IBcAppManagementClient apps,
        IBcAdminClient admin,
        DeliveryQueue queue,
        Bc.BcPanelCache panelCache,
        ALDevToolbox.Services.Tools.ToolEnablement tools,
        ILogger<DeliveryService> logger)
    {
        _tools = tools;
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _tokens = tokens;
        _apps = apps;
        _admin = admin;
        _queue = queue;
        _panelCache = panelCache;
        _logger = logger;
    }

    /// <summary>How long to wait between install-operation polls. Shortened by tests.</summary>
    internal TimeSpan PollDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for one app's install before giving up. Shortened by tests.</summary>
    internal TimeSpan PollTimeoutPerApp { get; set; } = TimeSpan.FromMinutes(10);

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; delivery called outside an authenticated request.");

    // ── Create + enqueue ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a delivery of <paramref name="projectBuildId"/> through
    /// <paramref name="releasePipelineId"/> to run immediately (the "Deploy now" path).
    /// Thin wrapper over <see cref="ScheduleDeliveryAsync"/> with <c>scheduledFor = now</c>.
    /// </summary>
    public Task<int> ReleaseBuildNowAsync(int releasePipelineId, int projectBuildId, CancellationToken ct = default)
        => ScheduleDeliveryAsync(releasePipelineId, projectBuildId, DateTime.UtcNow, ct);

    /// <summary>
    /// Creates a delivery of <paramref name="projectBuildId"/> through
    /// <paramref name="releasePipelineId"/>, scheduled for <paramref name="scheduledForUtc"/>.
    /// Validates access (the project owner / org Admin), that the build is a successful
    /// build of the deployment pipeline's build pipeline with deliverables, that the target
    /// environment can take an install, and that the pipeline's install timing is one
    /// Business Central still accepts. Snapshots the target so later edits don't
    /// rewrite history, and records whether the chosen time is <em>outside</em> the
    /// environment's update window (the audited override). A delivery due now/in the past
    /// is enqueued immediately; a future one is left for <see cref="DeliveryScheduler"/> to
    /// enqueue when due. Returns the new delivery id. Throws
    /// <see cref="PlanValidationException"/> on a bad request,
    /// <see cref="ProjectAccessDeniedException"/> when not permitted.
    /// </summary>
    public Task<int> ScheduleDeliveryAsync(int releasePipelineId, int projectBuildId, DateTime scheduledForUtc, CancellationToken ct = default)
        => CreateDeliveryAsync(releasePipelineId, projectBuildId, scheduledForUtc, forceSyncOnce: false, ct);

    /// <summary>
    /// Deploys a failed delivery's build again, now, through the same deployment pipeline
    /// (#931). With <paramref name="forceSyncOnce"/> the new delivery snapshots
    /// <see cref="BcSyncMode.ForceSync"/> as its own schema sync mode; the pipeline's
    /// setting is not touched, so the deployment after this one is back on the pipeline's
    /// mode. Everything else - access, the environment gate, the build and timing checks -
    /// is the ordinary deployment's. Apps the environment already has at the build's version
    /// are skipped by the run, which is what makes a retry after a partial failure safe.
    /// The Production acknowledgement is the dialog's, as it is for every deployment. Throws
    /// <see cref="PlanValidationException"/> when the delivery is missing or did not fail.
    /// </summary>
    public async Task<int> ReleaseAgainAsync(int deliveryId, bool forceSyncOnce, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var failed = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ReleasePipelineId, d.ProjectBuildId, d.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That deployment no longer exists.");
        if (failed.Status != ProjectDeliveryStatus.Failed)
        {
            throw Validation("Delivery", "Only a failed deployment can be deployed again.");
        }
        return await CreateDeliveryAsync(failed.ReleasePipelineId, failed.ProjectBuildId, DateTime.UtcNow, forceSyncOnce, ct);
    }

    private async Task<int> CreateDeliveryAsync(int releasePipelineId, int projectBuildId, DateTime scheduledForUtc, bool forceSyncOnce, CancellationToken ct)
    {
        var orgId = RequireOrganizationId();
        // Deploying spends the customer's Business Central credential, and it
        // can be started from the build pipeline's page as well as the
        // deployment pipeline's, so the step-up rule for Deployment pipelines
        // is checked here rather than trusted to the route gate.
        await _tools.EnsureStepUpAsync(Domain.Tools.ToolKey.Releases, ct);
        scheduledForUtc = DateTime.SpecifyKind(scheduledForUtc, DateTimeKind.Utc);

        var plan = await ResolveReleaseAsync(releasePipelineId, projectBuildId, checkAccess: true, ct);
        var delivery = await WriteDeliveryAsync(orgId, plan, scheduledForUtc, forceSyncOnce, proposed: false, ct);

        // Due now (or in the past) → enqueue immediately so "Deploy now" is snappy;
        // a future delivery is left for the DeliveryScheduler to enqueue when due.
        if (scheduledForUtc <= delivery.CreatedAt)
        {
            await _queue.EnqueueAsync(new DeliveryJob(delivery.Id, AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "capturing identity for a delivery")), ct);
        }

        _logger.LogInformation(
            "Created delivery {DeliveryId}: build {BuildId} → deployment pipeline {ReleasePipelineId} ({Env}) for {ScheduledFor:o}, {AppCount} app(s){Override}{ForceOnce}.",
            delivery.Id, plan.BuildId, plan.Id, plan.EnvName, scheduledForUtc, plan.Artifacts.Count,
            delivery.ScheduledOutsideWindow ? " (outside the update window)" : "",
            forceSyncOnce ? " (Force sync, this deployment only)" : "");
        return delivery.Id;
    }

    /// <summary>
    /// Writes one delivery of a resolved deployment, with a pending row per app. A proposed
    /// one (#934) has nobody behind it yet - the person who approves it becomes its
    /// triggering user - and opens its log with the line that says where it came from.
    /// </summary>
    private async Task<OeProjectDelivery> WriteDeliveryAsync(
        int orgId, ReleasePlan plan, DateTime scheduledForUtc, bool forceSyncOnce, bool proposed, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var delivery = new OeProjectDelivery
        {
            OrganizationId = orgId,
            ProjectId = plan.ProjectId,
            ReleasePipelineId = plan.Id,
            ProjectBuildId = plan.BuildId,
            TriggeredByUserId = proposed ? null : _orgContext.CurrentUserId,
            EnvironmentName = plan.EnvName,
            DeploymentSchedule = plan.WireSchedule,
            ScheduledByDeliveryWindow = plan.ByDeliveryWindow,
            // A one-time Force sync lives on this delivery only; the pipeline keeps its mode.
            SchemaSyncMode = forceSyncOnce ? BcSyncMode.ForceSync : plan.SchemaSyncMode,
            ScheduledFor = scheduledForUtc,
            // Audit the override: a window exists and the chosen time falls outside it.
            ScheduledOutsideWindow = plan.IsOutsideWindow(scheduledForUtc),
            Status = proposed ? ProjectDeliveryStatus.Proposed : ProjectDeliveryStatus.Scheduled,
            DiagnosticsLog = proposed ? LogLine(DeliveryProposalLog.Prepared(plan.BuildId)) : null,
            CreatedAt = now,
            UpdatedAt = now,
        };
        for (var i = 0; i < plan.Artifacts.Count; i++)
        {
            delivery.Results.Add(new OeProjectDeliveryResult
            {
                OrganizationId = orgId,
                Ordering = i,
                AppName = plan.Artifacts[i].AppName,
                AppVersion = plan.Artifacts[i].AppVersion,
                Status = ProjectDeliveryResultStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        _db.OeProjectDeliveries.Add(delivery);
        await _db.SaveChangesAsync(ct);
        return delivery;
    }

    /// <summary>
    /// Everything a deployment of <paramref name="projectBuildId"/> through
    /// <paramref name="releasePipelineId"/> needs, checked the way every release is: the
    /// pipeline and its target still exist and can take an install, the build is a
    /// successful build of this pipeline's source with apps in it, and the pipeline's
    /// timing and schema sync are values Business Central still accepts. Shared by a
    /// hand-made deployment, an approval of a prepared one, and the preparing itself, so the
    /// three can never disagree about what is releasable. Throws
    /// <see cref="PlanValidationException"/>; with <paramref name="checkAccess"/> also
    /// <see cref="ProjectAccessDeniedException"/>.
    /// </summary>
    private async Task<ReleasePlan> ResolveReleaseAsync(int releasePipelineId, int projectBuildId, bool checkAccess, CancellationToken ct, bool checkEnvironmentStatus = true)
    {
        var rp = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new
            {
                r.Id,
                r.ProjectId,
                r.BuildPipelineId,
                r.ArtifactSource,
                r.DeploymentSchedule,
                r.SchemaSyncMode,
                OwnerId = r.Project!.CreatedByUserId,
                TimeZone = r.Project.BcTimeZone,
                EnvName = r.ProjectEnvironment!.Name,
                EnvMissing = r.ProjectEnvironment.MissingSince != null,
                EnvStatus = r.ProjectEnvironment.Status,
                WindowStart = r.ProjectEnvironment.UpdateWindowStart,
                WindowEnd = r.ProjectEnvironment.UpdateWindowEnd,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("ReleasePipeline", "This deployment pipeline no longer exists.");

        if (checkAccess)
        {
            await _access.EnsureCanManageAsync(rp.ProjectId, rp.OwnerId, ct);
        }

        if (rp.EnvMissing)
        {
            throw Validation("ProjectEnvironment",
                "This deployment pipeline's target environment is no longer present in Business Central. Refresh the environments and try again.");
        }
        // The cached status catches the obvious cases at the point the user is looking
        // at the screen. The live re-read before the upload (see PublishAsync) is the
        // one that catches an update that lands between scheduling and running.
        // Preparing a deployment skips it: an environment busy with an update when a build
        // lands is ready again long before anyone approves, and the approval checks again.
        if (checkEnvironmentStatus && BcEnvironmentStatus.RefusalMessage(rp.EnvName, rp.EnvStatus) is { } statusRefusal)
        {
            throw Validation("ProjectEnvironment", statusRefusal);
        }

        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == projectBuildId)
            .Select(b => new { b.Id, b.ProjectId, b.PipelineId, b.Status, b.GithubReleaseTag })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Build", "That build no longer exists.");

        // Which builds this pipeline may publish depends on where it draws its apps
        // from. A build pipeline's own runs, or - for a Release-sourced pipeline - a
        // build staged from one of the repository's GitHub releases: no pipeline of its
        // own, and the tag it came from recorded on it. See
        // .design/github-integration-phase2.md (#632).
        var acceptable = build.ProjectId == rp.ProjectId
            && (rp.ArtifactSource == ReleaseArtifactSource.GithubRelease
                ? build.PipelineId is null && build.GithubReleaseTag is not null
                : build.PipelineId == rp.BuildPipelineId);
        if (!acceptable)
        {
            throw Validation("Build", rp.ArtifactSource == ReleaseArtifactSource.GithubRelease
                ? "That build didn't come from one of this deployment pipeline's GitHub releases."
                : "That build isn't from this deployment pipeline's build pipeline.");
        }
        if (build.Status != ProjectBuildStatus.Ready)
        {
            throw Validation("Build", "Only a successful build can be deployed.");
        }

        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == build.Id)
            .OrderBy(a => a.Id)
            .Select(a => new ReleaseApp(a.AppName, a.AppVersion))
            .ToListAsync(ct);
        if (artifacts.Count == 0)
        {
            throw Validation("Build", "That build has no deliverable apps to publish.");
        }

        // A deployment pipeline saved before the move to the App Management API stores the
        // old wording ("Current Version", "Force Sync"), which that API rejects outright.
        // Refuse here with something the user can act on rather than letting the upload
        // fail hours later inside the worker.
        // A pipeline set to the environment's delivery window sends Immediate: the
        // window decided the time, and Business Central installs on arrival.
        var wireSchedule = BcDeploymentSchedule.ToWire(rp.DeploymentSchedule);
        if (wireSchedule is null)
        {
            throw Validation("DeploymentSchedule",
                "This deployment pipeline's install timing is no longer a valid option. Open the deployment pipeline, choose when installs should run, and save it.");
        }
        if (!BcSyncMode.IsValid(rp.SchemaSyncMode))
        {
            throw Validation("SchemaSyncMode",
                "This deployment pipeline's schema sync setting is no longer a valid option. Open the deployment pipeline, choose a schema sync setting, and save it.");
        }
        // Business Central decides the order it installs a window's queue in; our order
        // only decides the order things were uploaded. With one app that's harmless, with
        // several it can install a dependent before its dependency.
        if (artifacts.Count > 1 && BcDeploymentSchedule.IsDeferred(wireSchedule))
        {
            throw Validation("DeploymentSchedule",
                $"This build has {artifacts.Count} apps, and Business Central chooses the order it installs them in when they wait for a later update. "
                + "Set this deployment pipeline to install right away, or deploy the apps one at a time.");
        }

        return new ReleasePlan(
            rp.Id, rp.ProjectId, rp.EnvName, wireSchedule,
            BcDeploymentSchedule.IsOurDeliveryWindow(rp.DeploymentSchedule), rp.SchemaSyncMode,
            UpdateWindow.ResolveTimeZone(rp.TimeZone), rp.WindowStart, rp.WindowEnd, build.Id, artifacts);
    }

    /// <summary>One app a deployment will install, in the build's order.</summary>
    private sealed record ReleaseApp(string AppName, string AppVersion);

    /// <summary>A deployment checked and ready to be written: see <see cref="ResolveReleaseAsync"/>.</summary>
    private sealed record ReleasePlan(
        int Id, int ProjectId, string EnvName, string WireSchedule, bool ByDeliveryWindow, string SchemaSyncMode,
        TimeZoneInfo Tz, TimeOnly? WindowStart, TimeOnly? WindowEnd, int BuildId, List<ReleaseApp> Artifacts)
    {
        /// <summary>True when a window is set and <paramref name="utc"/> falls outside it: the audited override.</summary>
        public bool IsOutsideWindow(DateTime utc) =>
            UpdateWindow.IsConfigured(WindowStart, WindowEnd) && !UpdateWindow.IsWithin(WindowStart, WindowEnd, Tz, utc);

        /// <summary>
        /// When the pipeline's own rule puts a deployment made at <paramref name="fromUtc"/>:
        /// the next opening of the delivery window for a pipeline set to it, else right
        /// away. This is what a prepared deployment is scheduled for, and what an approval
        /// schedules it for, so it runs the way the deploy dialog's prefill would have.
        /// </summary>
        public DateTime RuleTime(DateTime fromUtc) => ByDeliveryWindow
            ? UpdateWindow.NextOpeningUtc(WindowStart, WindowEnd, Tz, fromUtc)
            : fromUtc;
    }

    // ── Prepared deployments (#934) ──────────────────────────────────────────────

    /// <summary>The longest reason a person can give for dismissing a prepared deployment.</summary>
    public const int DismissReasonMaxLength = 500;

    /// <summary>
    /// Prepares a deployment of <paramref name="projectBuildId"/> through every deployment
    /// pipeline that draws from its build pipeline and has "Prepare a deployment when a new
    /// build succeeds" on: a <see cref="ProjectDeliveryStatus.Proposed"/> delivery with
    /// the build's apps and the time the pipeline's rule gives, and nothing sent. A
    /// proposal still waiting on an older build is replaced (dismissed, with the newer
    /// build recorded on it), so a pipeline never holds a queue of them. A pipeline
    /// the build can't be deployed through - its environment gone, its settings no longer
    /// valid - is skipped with a warning in the log, never an error: the build is fine.
    /// Called by the build worker under the build's own organisation once it is ready;
    /// needs no access check, because preparing sends nothing and approving checks.
    /// Pull-request builds are never prepared. Returns how many were prepared.
    /// </summary>
    public async Task<int> ProposeReleasesForBuildAsync(int projectBuildId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == projectBuildId)
            .Select(b => new { b.Id, b.PipelineId, b.Status, b.Trigger })
            .FirstOrDefaultAsync(ct);
        if (build?.PipelineId is not { } buildPipelineId
            || build.Status != ProjectBuildStatus.Ready
            || build.Trigger == ProjectBuildTrigger.PullRequest)
        {
            return 0;
        }

        var pipelineIds = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.DeletedAt == null
                        && r.PrepareReleaseOnNewBuild
                        && r.ArtifactSource == ReleaseArtifactSource.Build
                        && r.BuildPipelineId == buildPipelineId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var prepared = 0;
        foreach (var releasePipelineId in pipelineIds)
        {
            var waiting = await _db.OeProjectDeliveries.AsNoTracking()
                .Where(d => d.ReleasePipelineId == releasePipelineId && d.Status == ProjectDeliveryStatus.Proposed)
                .Select(d => new { d.Id, d.ProjectBuildId })
                .ToListAsync(ct);
            // The same build twice, or a newer one already waiting: nothing to do.
            if (waiting.Any(w => w.ProjectBuildId >= build.Id)) continue;

            ReleasePlan plan;
            try
            {
                plan = await ResolveReleaseAsync(releasePipelineId, build.Id, checkAccess: false, ct, checkEnvironmentStatus: false);
            }
            catch (PlanValidationException ex)
            {
                _logger.LogWarning(
                    "Not preparing a deployment of build {BuildId} through deployment pipeline {ReleasePipelineId}: {Reason}",
                    build.Id, releasePipelineId, string.Join(" ", ex.Errors.Values));
                continue;
            }

            var replacedLine = LogLine(DeliveryProposalLog.Replaced(build.Id));
            foreach (var old in waiting)
            {
                var now = DateTime.UtcNow;
                var replacedBy = build.Id;
                var replacedReason = DeliveryProposalLog.ReplacedReason(build.Id);
                await _db.OeProjectDeliveries
                    .Where(d => d.Id == old.Id && d.Status == ProjectDeliveryStatus.Proposed)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, ProjectDeliveryStatus.Dismissed)
                        .SetProperty(d => d.DismissReason, replacedReason)
                        .SetProperty(d => d.ReplacedByProjectBuildId, replacedBy)
                        .SetProperty(d => d.FinishedAt, now)
                        .SetProperty(d => d.DiagnosticsLog, d => (d.DiagnosticsLog ?? string.Empty) + replacedLine)
                        .SetProperty(d => d.UpdatedAt, now), ct);
                await MarkAppsNotSentAsync(old.Id, $"Not sent: build #{build.Id} replaced this deployment.", ct);
            }

            var delivery = await WriteDeliveryAsync(orgId, plan, plan.RuleTime(DateTime.UtcNow), forceSyncOnce: false, proposed: true, ct);
            prepared++;
            _logger.LogInformation(
                "Prepared delivery {DeliveryId}: build {BuildId} → deployment pipeline {ReleasePipelineId} ({Env}), waiting for approval; replaced {Replaced} older.",
                delivery.Id, build.Id, releasePipelineId, plan.EnvName, waiting.Count);
        }
        return prepared;
    }

    /// <summary>
    /// Approves a prepared deployment (atomic <c>proposed → scheduled</c>). From here it is
    /// an ordinary deployment: every check a hand-made one has is made again now, the
    /// pipeline's settings are snapshotted as they are now, the approver becomes the
    /// person it runs as, and it is scheduled by the pipeline's rule from now - the next
    /// opening of the delivery window, or right away. The Production acknowledgement is
    /// the page's, as it is for every deployment. Throws <see cref="PlanValidationException"/>
    /// when it is no longer waiting (replaced by a newer build, or dismissed) or can't be
    /// deployed, <see cref="ProjectAccessDeniedException"/> when not permitted.
    /// </summary>
    public async Task ApproveProposalAsync(int deliveryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var proposal = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ReleasePipelineId, d.ProjectBuildId, d.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That deployment no longer exists.");

        // Access first, so somebody who may not approve learns that rather than the state.
        var plan = await ResolveReleaseAsync(proposal.ReleasePipelineId, proposal.ProjectBuildId, checkAccess: true, ct);
        if (proposal.Status != ProjectDeliveryStatus.Proposed)
        {
            throw Validation("Delivery", NoLongerWaiting);
        }

        var now = DateTime.UtcNow;
        var when = plan.RuleTime(now);
        var outsideWindow = plan.IsOutsideWindow(when);
        var line = LogLine(DeliveryProposalLog.Approved(await CurrentUserNameAsync(ct)));
        var userId = _orgContext.CurrentUserId;

        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Proposed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Scheduled)
                .SetProperty(d => d.TriggeredByUserId, userId)
                .SetProperty(d => d.EnvironmentName, plan.EnvName)
                .SetProperty(d => d.DeploymentSchedule, plan.WireSchedule)
                .SetProperty(d => d.ScheduledByDeliveryWindow, plan.ByDeliveryWindow)
                .SetProperty(d => d.SchemaSyncMode, plan.SchemaSyncMode)
                .SetProperty(d => d.ScheduledFor, when)
                .SetProperty(d => d.ScheduledOutsideWindow, outsideWindow)
                .SetProperty(d => d.DiagnosticsLog, d => (d.DiagnosticsLog ?? string.Empty) + line)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", NoLongerWaiting);
        }

        if (when <= now)
        {
            await _queue.EnqueueAsync(new DeliveryJob(deliveryId, AmbientOrganizationScope.OrganizationIdentity.FromContext(_orgContext, "capturing identity for a delivery")), ct);
        }
        _logger.LogInformation("Approved prepared delivery {DeliveryId}, scheduled for {ScheduledFor:o}.", deliveryId, when);
    }

    /// <summary>
    /// Dismisses a prepared deployment (atomic <c>proposed → dismissed</c>), recording who
    /// did it (<see cref="OeProjectDelivery.CancelledByUserId"/>) and, when they gave one,
    /// why (<see cref="OeProjectDelivery.DismissReason"/>); the log gets a line too, for
    /// reading. Nothing was sent, so nothing needs undoing. Throws
    /// <see cref="PlanValidationException"/> when it is no longer waiting or the reason is
    /// too long, <see cref="ProjectAccessDeniedException"/> when not permitted.
    /// </summary>
    public async Task DismissProposalAsync(int deliveryId, string? reason, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var why = string.IsNullOrWhiteSpace(reason) ? null : OneLine(reason).Trim();
        if (why is { Length: > DismissReasonMaxLength })
        {
            throw Validation("Reason", $"Keep the reason to {DismissReasonMaxLength} characters or fewer.");
        }

        var owner = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ProjectId, d.Status, OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That deployment no longer exists.");
        await _access.EnsureCanManageAsync(owner.ProjectId, owner.OwnerId, ct);
        if (owner.Status != ProjectDeliveryStatus.Proposed)
        {
            throw Validation("Delivery", NoLongerWaiting);
        }

        var line = LogLine(DeliveryProposalLog.Dismissed(await CurrentUserNameAsync(ct), why));
        var userId = _orgContext.CurrentUserId;
        var now = DateTime.UtcNow;
        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Proposed)
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Dismissed)
                .SetProperty(d => d.CancelledByUserId, userId)
                .SetProperty(d => d.DismissReason, why)
                .SetProperty(d => d.FinishedAt, now)
                .SetProperty(d => d.DiagnosticsLog, d => (d.DiagnosticsLog ?? string.Empty) + line)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", NoLongerWaiting);
        }
        await MarkAppsNotSentAsync(deliveryId, "Not sent: the deployment was dismissed.", ct);
        _logger.LogInformation("Dismissed prepared delivery {DeliveryId}.", deliveryId);
    }

    /// <summary>
    /// A prepared deployment that was set aside never reaches its apps, so they stop reading
    /// "Pending" - which says they are still on their way - and say why instead.
    /// </summary>
    private async Task MarkAppsNotSentAsync(int deliveryId, string message, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await _db.OeProjectDeliveryResults
            .Where(r => r.ProjectDeliveryId == deliveryId && r.Status == ProjectDeliveryResultStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, ProjectDeliveryResultStatus.Skipped)
                .SetProperty(r => r.Message, message)
                .SetProperty(r => r.UpdatedAt, now), ct);
    }

    private const string NoLongerWaiting =
        "This deployment is no longer waiting for approval. A newer build may have replaced it, or someone else approved or dismissed it.";

    /// <summary>
    /// Cancels a <em>scheduled</em> delivery (atomic <c>scheduled → cancelled</c>). Access-gated.
    /// Throws <see cref="PlanValidationException"/> if it's already been claimed (the
    /// "cancellable until a worker picks it up" guarantee) or no longer exists.
    /// </summary>
    public async Task CancelDeliveryAsync(int deliveryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var owner = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ProjectId, OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That delivery no longer exists.");
        await _access.EnsureCanManageAsync(owner.ProjectId, owner.OwnerId, ct);

        var now = DateTime.UtcNow;
        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Cancelled)
                .SetProperty(d => d.CancelledByUserId, _orgContext.CurrentUserId)
                .SetProperty(d => d.FinishedAt, now)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be cancelled.");
        }
        _logger.LogInformation("Cancelled delivery {DeliveryId}.", deliveryId);
    }

    /// <summary>
    /// Moves a <em>scheduled</em> delivery to a new time (atomic on <c>scheduled</c>),
    /// recomputing the outside-window audit flag. Access-gated. Throws if the delivery has
    /// already started or no longer exists. Enqueues immediately if the new time is now/past.
    /// </summary>
    public async Task RescheduleDeliveryAsync(int deliveryId, DateTime newScheduledForUtc, CancellationToken ct = default)
    {
        RequireOrganizationId();
        newScheduledForUtc = DateTime.SpecifyKind(newScheduledForUtc, DateTimeKind.Utc);

        var info = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new
            {
                d.OrganizationId,
                d.TriggeredByUserId,
                d.ProjectId,
                OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId,
                TimeZone = d.ReleasePipeline.Project.BcTimeZone,
                WindowStart = d.ReleasePipeline.ProjectEnvironment!.UpdateWindowStart,
                WindowEnd = d.ReleasePipeline.ProjectEnvironment.UpdateWindowEnd,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Delivery", "That delivery no longer exists.");
        await _access.EnsureCanManageAsync(info.ProjectId, info.OwnerId, ct);

        var tz = UpdateWindow.ResolveTimeZone(info.TimeZone);
        var outsideWindow = UpdateWindow.IsConfigured(info.WindowStart, info.WindowEnd)
            && !UpdateWindow.IsWithin(info.WindowStart, info.WindowEnd, tz, newScheduledForUtc);

        var now = DateTime.UtcNow;
        var changed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScheduledFor, newScheduledForUtc)
                .SetProperty(d => d.ScheduledOutsideWindow, outsideWindow)
                .SetProperty(d => d.UpdatedAt, now), ct);
        if (changed == 0)
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be rescheduled.");
        }

        if (newScheduledForUtc <= now)
        {
            await _queue.EnqueueAsync(new DeliveryJob(deliveryId,
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
                    info.OrganizationId, _orgContext.IsSystemOrganization, info.TriggeredByUserId)), ct);
        }
        _logger.LogInformation("Rescheduled delivery {DeliveryId} to {ScheduledFor:o}.", deliveryId, newScheduledForUtc);
    }

    // ── Scheduler sweep helpers (called per-org under an AmbientOrganizationScope) ──

    /// <summary>
    /// Enqueues every <c>scheduled</c> delivery in the current org whose time has come
    /// (<see cref="OeProjectDelivery.ScheduledFor"/> ≤ <paramref name="nowUtc"/>). Org-scoped
    /// via the query filter (the scheduler sets the ambient org). Re-enqueuing a row the
    /// worker hasn't claimed yet is a no-op (the queue dedupes by id). Returns the count.
    /// </summary>
    public async Task<int> EnqueueDueDeliveriesAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var due = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Status == ProjectDeliveryStatus.Scheduled && d.ScheduledFor <= nowUtc)
            .Select(d => new { d.Id, d.OrganizationId, d.TriggeredByUserId })
            .ToListAsync(ct);

        foreach (var d in due)
        {
            await _queue.EnqueueAsync(new DeliveryJob(d.Id,
                AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
                    d.OrganizationId, _orgContext.IsSystemOrganization, d.TriggeredByUserId)), ct);
        }
        return due.Count;
    }

    /// <summary>
    /// Fails every delivery in the current org left in a non-terminal <em>in-progress</em>
    /// state (<c>claimed</c>/<c>uploading</c>/<c>installing</c>) — orphaned when the process
    /// died mid-publish. Called once per org on the scheduler's first sweep after startup,
    /// when nothing is running yet, so it never trips an actively-running delivery. The
    /// publish isn't safely resumable (partial uploads to BC), so these are failed, not
    /// retried. Returns the count.
    /// </summary>
    public async Task<int> FailInterruptedDeliveriesAsync(CancellationToken ct = default)
    {
        var orphans = await _db.OeProjectDeliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Claimed
                        || d.Status == ProjectDeliveryStatus.Uploading
                        || d.Status == ProjectDeliveryStatus.Installing)
            .Include(d => d.Results)
            .ToListAsync(ct);
        if (orphans.Count == 0) return 0;

        var now = DateTime.UtcNow;
        foreach (var d in orphans)
        {
            d.Status = ProjectDeliveryStatus.Failed;
            d.FailureMessage = "The deployment was interrupted by a restart. Deploy the build again.";
            d.FinishedAt = now;
            d.UpdatedAt = now;
            foreach (var r in d.Results.Where(r => r.Status is ProjectDeliveryResultStatus.Pending
                                                    or ProjectDeliveryResultStatus.Uploading
                                                    or ProjectDeliveryResultStatus.Installing))
            {
                r.Status = ProjectDeliveryResultStatus.Skipped;
                r.UpdatedAt = now;
            }
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogWarning("Failed {Count} delivery(ies) interrupted by a restart.", orphans.Count);
        return orphans.Count;
    }

    // ── Run (worker entry) ────────────────────────────────────────────────────

    /// <summary>
    /// Claims the delivery (atomic <c>scheduled → claimed</c>) and runs the publish.
    /// Returns quietly if the row was already taken or cancelled. All failures are
    /// recorded on the row; this method does not throw on a publish failure.
    /// </summary>
    public async Task RunDeliveryAsync(int deliveryId, CancellationToken ct = default)
    {
        var claimedAt = DateTime.UtcNow;
        var claimed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed)
                .SetProperty(d => d.ClaimedAt, claimedAt)
                .SetProperty(d => d.UpdatedAt, claimedAt), ct);
        if (claimed == 0)
        {
            _logger.LogInformation("Delivery {DeliveryId} was already claimed or cancelled; skipping.", deliveryId);
            return;
        }

        var delivery = await _db.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null)
        {
            _logger.LogWarning("Delivery {DeliveryId} vanished after being claimed.", deliveryId);
            return;
        }

        // A deployment that was prepared and approved (#934) already carries those lines;
        // the run writes after them rather than over them.
        var log = new StringBuilder(delivery.DiagnosticsLog ?? string.Empty);
        try
        {
            await PublishAsync(delivery, log, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await FailAsync(delivery, log, "The delivery was interrupted while the app was shutting down.", ct);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delivery {DeliveryId} failed unexpectedly.", deliveryId);
            await FailAsync(delivery, log, "The delivery failed unexpectedly. " + Short(ex.Message), ct);
        }
    }

    /// <summary>
    /// Reads the environment's installed apps once, then uploads each app in stored
    /// (dependency) order. An immediate install is polled to a terminal state; a deferred
    /// one is handed to Business Central and the delivery ends at
    /// <see cref="ProjectDeliveryStatus.HandedOff"/>.
    /// </summary>
    private async Task PublishAsync(OeProjectDelivery delivery, StringBuilder log, CancellationToken ct)
    {
        BcDeliveryContext bc;
        try
        {
            bc = await _tokens.AcquireDeliveryContextAsync(delivery.ProjectId, ct);
        }
        catch (BcApiException ex)
        {
            await FailAsync(delivery, log, ex.Message, ct);
            return;
        }

        // Whatever the API called this environment's family, verbatim. Every App
        // Management URL is built from it.
        var family = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == delivery.ProjectId && e.Name == delivery.EnvironmentName)
            .Select(e => e.ApplicationFamily)
            .FirstOrDefaultAsync(ct)
            ?? BcConstants.DefaultApplicationFamily;

        // The environment was fine when this delivery was scheduled; hours may have
        // passed. Re-read it live before the first byte goes up — a platform update that
        // landed in between is exactly what this catches.
        if (await EnvironmentBlockedAsync(delivery, bc.AccessToken, ct) is { } blocked)
        {
            Append(log, blocked);
            await FailAsync(delivery, log, blocked, ct);
            return;
        }

        var now = DateTime.UtcNow;
        delivery.Status = ProjectDeliveryStatus.Uploading;
        delivery.StartedAt = now;
        delivery.UpdatedAt = now;
        Append(log, $"Publishing {delivery.Results.Count} app(s) to {delivery.EnvironmentName}.");
        if (BcSyncMode.Normalize(delivery.SchemaSyncMode) == BcSyncMode.ForceSync)
        {
            // Said once, up front: a reader of the log after a dropped column needs to see
            // it was asked for, and whether the pipeline asks for it every time (#931).
            var pipelineMode = await _db.OeReleasePipelines.AsNoTracking()
                .Where(r => r.Id == delivery.ReleasePipelineId)
                .Select(r => r.SchemaSyncMode)
                .FirstOrDefaultAsync(ct);
            Append(log, BcSyncMode.Normalize(pipelineMode) == BcSyncMode.ForceSync
                ? "Schema sync: Force sync."
                : "Schema sync: Force sync, this deployment only.");
        }
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);

        // Ordered artifacts line up 1:1 with the ordered result rows (both came from the
        // build's artifacts ordered by id at creation — that id order *is* the build's
        // dependency order, so nothing is re-sorted here). Load each blob only when it's
        // that app's turn, so we never hold every .app in memory at once.
        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == delivery.ProjectBuildId)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.FileName, a.AppId })
            .ToListAsync(ct);

        // One read of what's already installed. The API only accepts a deferred schedule
        // for an app it already knows, so a first-time upload has to be caught before
        // it's sent — a 400 from BC wouldn't say which rule it broke.
        IReadOnlyList<BcInstalledApp> installed;
        try
        {
            installed = await _apps.ListInstalledAppsAsync(bc.AccessToken, family, delivery.EnvironmentName, ct);
        }
        catch (BcApiException ex)
        {
            await FailAsync(delivery, log, $"Couldn't read the apps installed on {delivery.EnvironmentName}. " + Short(ex.Message), ct);
            return;
        }

        // Each result's app id as the build's artifact knows it (results and artifacts
        // line up by position). Business Central keys everything on the id: two apps can
        // share a name across publishers, and a renamed app keeps its id. The name is
        // only the fallback for an artifact written before the id was stamped on it.
        var ordered = delivery.Results.OrderBy(r => r.Ordering).ToList();
        var appIds = ordered
            .Select((r, i) => i < artifacts.Count && Guid.TryParse(artifacts[i].AppId, out var parsed) ? parsed : (Guid?)null)
            .ToList();
        BcInstalledApp? InstalledMatch(int i) => appIds[i] is { } id
            ? installed.FirstOrDefault(a => a.AppId == id)
            : installed.FirstOrDefault(a => string.Equals(a.Name, ordered[i].AppName, StringComparison.OrdinalIgnoreCase));

        // What each app is moving from, while the answer is at hand: the page reads it
        // back as "2.2.0.104 to 2.3.0.118", and after a failure it is the version the
        // environment was left on.
        for (var i = 0; i < ordered.Count; i++)
        {
            var match = InstalledMatch(i);
            ordered[i].PreviousVersion = string.IsNullOrWhiteSpace(match?.Version) ? null : match.Version;
        }

        // "Next minor/major update" is an instruction to bump an app that's already
        // there. Business Central refuses it for an app it has never seen, so a
        // first-time deployment has to go in right away.
        if (BcDeploymentSchedule.RequiresInstalledApp(delivery.DeploymentSchedule))
        {
            var missing = ordered
                .Where((r, i) => InstalledMatch(i) is null)
                .Select(r => r.AppName)
                .ToList();
            if (missing.Count > 0)
            {
                await FailAsync(delivery, log,
                    $"{string.Join(", ", missing)} isn't installed on {delivery.EnvironmentName} yet, and Business Central only accepts "
                    + "\"next minor update\" or \"next major update\" for an app that's already there. Install it right away first.", ct);
                return;
            }
        }

        // Business Central holds one version of an app per schedule, and answers a second
        // upload of a version it is already holding with a bare 400. Read what is waiting
        // once, so that case can be refused below with a sentence that says what to do.
        IReadOnlyList<BcScheduledPteOperation> waiting = [];
        if (BcDeploymentSchedule.IsDeferred(delivery.DeploymentSchedule))
        {
            try
            {
                waiting = await _apps.ListScheduledPteOperationsAsync(bc.AccessToken, family, delivery.EnvironmentName, ct);
            }
            catch (BcApiException ex)
            {
                // Not a reason to stop: this read only buys a clearer message, and the
                // upload itself reports a real conflict.
                Append(log, $"Couldn't read the installs already waiting on {delivery.EnvironmentName}; going ahead. " + Short(ex.Message));
            }
        }

        var results = ordered;
        var failedIndex = -1;
        // Whether anything went up. A run where every app was already on its version
        // hands nothing to Business Central, so it ends as deployed, not handed off.
        var uploadedAny = false;
        // The one line the delivery carries when Business Central reported the failure;
        // the detail (its message) stays on the app, so the two never say the same twice.
        string? failureLine = null;
        // A refusal is already the whole sentence; it isn't prefixed like an app's failure.
        string? refusal = null;

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            if (failedIndex >= 0)
            {
                result.Status = ProjectDeliveryResultStatus.Skipped;
                result.UpdatedAt = DateTime.UtcNow;
                continue;
            }

            var label = $"{result.AppName} {result.AppVersion}";
            if (i >= artifacts.Count)
            {
                failedIndex = i;
                result.Status = ProjectDeliveryResultStatus.Failed;
                result.FinishedAt = DateTime.UtcNow;
                result.Message = "The build's deliverables changed under the delivery.";
                Append(log, $"FAILED {label}: deliverable missing.");
                break;
            }

            // Business Central refuses a version it already has, so an app the environment
            // is already on is left alone and the run goes on (#931). That is what makes
            // "Deploy again" after a partial failure safe: the apps that went in the
            // first time are not sent twice.
            if (InstalledMatch(i)?.Version is { Length: > 0 } onVersion
                && string.Equals(onVersion, result.AppVersion, StringComparison.OrdinalIgnoreCase))
            {
                result.Status = ProjectDeliveryResultStatus.Skipped;
                result.Message = $"Already on {onVersion}.";
                result.UpdatedAt = DateTime.UtcNow;
                Append(log, $"Skipped {label}: {delivery.EnvironmentName} already has this version.");
                await SaveResultAsync(delivery, log, ct);
                continue;
            }

            if (AlreadyWaiting(waiting, appIds[i], result, delivery) is { } waitingRefusal)
            {
                failedIndex = i;
                refusal = waitingRefusal;
                result.Status = ProjectDeliveryResultStatus.Failed;
                result.FinishedAt = DateTime.UtcNow;
                result.UpdatedAt = result.FinishedAt.Value;
                result.Message = waitingRefusal;
                Append(log, waitingRefusal);
                break;
            }

            try
            {
                var artifact = artifacts[i];
                var bytes = await _db.OeProjectBuildArtifacts.AsNoTracking()
                    .Where(a => a.Id == artifact.Id)
                    .Select(a => a.Content)
                    .FirstAsync(ct);

                result.Status = ProjectDeliveryResultStatus.Uploading;
                result.StartedAt = DateTime.UtcNow;
                result.UpdatedAt = result.StartedAt.Value;
                Append(log, $"Uploading {label} ({bytes.Length} bytes)...");
                await SaveResultAsync(delivery, log, ct);

                var operation = await _apps.InstallPteAsync(
                    bc.AccessToken, family, delivery.EnvironmentName,
                    bytes, artifact.FileName,
                    delivery.DeploymentSchedule, delivery.SchemaSyncMode,
                    // No language is sent. The workbench has no concept of one, and
                    // guessing "en-US" would set the install locale wrong for (say) a
                    // Danish customer; BC applies its own default until a deployment
                    // pipeline can say what the language should be.
                    languageId: string.Empty,
                    installOrUpdateNeededDependencies: true,
                    ct);

                uploadedAny = true;
                result.OperationId = operation.Id;
                result.AppId = operation.AppId?.ToString();
                result.UpdatedAt = DateTime.UtcNow;

                // BC read the version out of the package it just accepted. If that isn't
                // the version this delivery promised, something other than this build's
                // artifact is going in and the history would misreport it.
                if (!string.IsNullOrEmpty(operation.TargetAppVersion)
                    && !string.Equals(operation.TargetAppVersion, result.AppVersion, StringComparison.OrdinalIgnoreCase))
                {
                    failedIndex = i;
                    result.Status = ProjectDeliveryResultStatus.Failed;
                    result.FinishedAt = DateTime.UtcNow;
                    result.Message = $"Business Central read version {operation.TargetAppVersion} from the uploaded app, but this build says {result.AppVersion}.";
                    Append(log, $"FAILED {label}: {result.Message}");
                    await SaveResultAsync(delivery, log, ct);
                    break;
                }

                if (BcDeploymentSchedule.IsDeferred(delivery.DeploymentSchedule))
                {
                    // Nothing left to watch: BC runs this in its own window, and no poll
                    // of ours would ever see it go terminal.
                    result.Status = ProjectDeliveryResultStatus.Scheduled;
                    result.FinishedAt = DateTime.UtcNow;
                    Append(log, $"Business Central has scheduled {label} (operation {operation.Id}).");
                    await SaveResultAsync(delivery, log, ct);
                    continue;
                }

                result.Status = ProjectDeliveryResultStatus.Installing;
                result.UpdatedAt = DateTime.UtcNow;
                delivery.Status = ProjectDeliveryStatus.Installing;
                delivery.InstallStartedAt ??= result.UpdatedAt;
                delivery.UpdatedAt = DateTime.UtcNow;
                Append(log, $"Installing {label} (operation {operation.Id})...");
                await SaveResultAsync(delivery, log, ct);

                var outcome = await PollUntilTerminalAsync(bc, family, delivery, operation, ct);
                if (outcome.Completed)
                {
                    result.Status = ProjectDeliveryResultStatus.Completed;
                    result.Message = outcome.Message;
                    Append(log, $"Installed {label}.");
                }
                else
                {
                    failedIndex = i;
                    result.Status = ProjectDeliveryResultStatus.Failed;
                    result.Message = outcome.Message;
                    // The log keeps Business Central's text whole, wrapper and JSON included:
                    // it is what support needs, and what the page parses back (#930).
                    if (outcome.Failure is { } reported)
                    {
                        Append(log, $"FAILED {label}: {BcFailureText.ForLog(reported, outcome.Raw)}");
                        failureLine = BcFailureText.WhatHappened(reported.Code, label);
                    }
                    else
                    {
                        Append(log, $"FAILED {label}: {outcome.Message}");
                    }
                }
                result.FinishedAt = DateTime.UtcNow;
                result.UpdatedAt = result.FinishedAt.Value;
                await SaveResultAsync(delivery, log, ct);
            }
            catch (BcApiException ex)
            {
                failedIndex = i;
                result.Status = ProjectDeliveryResultStatus.Failed;
                // A refused upload can carry the same Data Plane Admin Service text as a
                // failed install; when it names a code, say it the same way.
                var refused = BcFailureText.Parse(ex.Message);
                if (refused.Code.Length > 0)
                {
                    result.Message = BcFailureText.AppMessage(refused);
                    failureLine = BcFailureText.WhatHappened(refused.Code, label);
                }
                else
                {
                    result.Message = Short(ex.Message);
                }
                result.FinishedAt = DateTime.UtcNow;
                result.UpdatedAt = result.FinishedAt.Value;
                Append(log, $"FAILED {label}: {OneLine(ex.Message)}");
            }
        }

        var endNow = DateTime.UtcNow;
        if (failedIndex >= 0)
        {
            var failed = results[failedIndex];
            // The branches that stop the loop with a break leave the apps after the
            // failed one pending; they didn't get there either.
            foreach (var r in results.Skip(failedIndex + 1).Where(r => r.Status == ProjectDeliveryResultStatus.Pending))
            {
                r.Status = ProjectDeliveryResultStatus.Skipped;
                r.UpdatedAt = endNow;
            }
            delivery.Status = ProjectDeliveryStatus.Failed;
            delivery.FailureMessage = refusal ?? failureLine ?? $"{failed.AppName} {failed.AppVersion} failed: {failed.Message}";
            Append(log, "Delivery failed.");
        }
        else if (!uploadedAny)
        {
            delivery.Status = ProjectDeliveryStatus.Deployed;
            Append(log, $"Every app was already on this version in {delivery.EnvironmentName}; nothing to install.");
        }
        else if (BcDeploymentSchedule.IsDeferred(delivery.DeploymentSchedule))
        {
            // Uploaded and accepted, but the install happens on Business Central's
            // schedule. We're no longer driving it, so this is as far as the delivery goes.
            delivery.Status = ProjectDeliveryStatus.HandedOff;
            Append(log, "Business Central has accepted the apps and will install them on its own schedule.");
        }
        else
        {
            delivery.Status = ProjectDeliveryStatus.Deployed;
            Append(log, "Delivery complete.");
        }
        delivery.FinishedAt = endNow;
        delivery.UpdatedAt = endNow;
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);

        // We have just changed what this customer's environment has installed or has
        // queued to install, so the cached panel is now wrong — and a consultant checking
        // "did my deployment land?" is exactly who would read it next. A delivery names its
        // environment rather than carrying its id, so the whole project's entries go.
        _panelCache.InvalidateProject(delivery.ProjectId);
    }

    /// <summary>
    /// The refusal for an app whose exact version Business Central is already holding for
    /// this delivery's schedule (#937), or null when it isn't. Matched on the app id, the
    /// name only when the artifact has none. An entry whose schedule didn't come back is
    /// left alone: without knowing it is the same schedule, the upload may well be fine.
    /// </summary>
    private static string? AlreadyWaiting(
        IReadOnlyList<BcScheduledPteOperation> waiting, Guid? appId, OeProjectDeliveryResult result, OeProjectDelivery delivery)
    {
        var schedule = BcDeploymentSchedule.Normalize(delivery.DeploymentSchedule);
        var held = waiting.Any(op =>
            op.Status is not (BcAppOperationStatus.Succeeded or BcAppOperationStatus.Failed
                or BcAppOperationStatus.Canceled or BcAppOperationStatus.Skipped)
            && op.ScheduleKind is { } kind && kind == schedule
            && string.Equals(op.TargetAppVersion, result.AppVersion, StringComparison.OrdinalIgnoreCase)
            && (appId is { } id
                ? op.AppId == id
                : string.Equals(op.Name, result.AppName, StringComparison.OrdinalIgnoreCase)));
        if (!held) return null;

        var when = schedule switch
        {
            BcDeploymentSchedule.NextMinorUpdate => "the next minor update",
            BcDeploymentSchedule.NextMajorUpdate => "the next major update",
            _ => "the Business Central update window",
        };
        return $"{result.AppName} {result.AppVersion} is already waiting for {when} on {delivery.EnvironmentName}; cancel it there first.";
    }

    /// <summary>
    /// Re-reads the target environment from the Admin Center API and returns a refusal
    /// message when it can't take an install right now — the claim-time half of the
    /// status gate (the other half runs in <see cref="ScheduleDeliveryAsync"/>). Returns
    /// null when the delivery may proceed, including when the API can't be read: a
    /// transport failure here isn't evidence the environment is unhealthy, and the
    /// upload that follows will surface a real problem with a better message. The fresh
    /// status is written back to the environment row so the project page doesn't keep
    /// showing the stale one the delivery just contradicted.
    /// </summary>
    private async Task<string?> EnvironmentBlockedAsync(OeProjectDelivery delivery, string token, CancellationToken ct)
    {
        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == delivery.ProjectId && e.Name == delivery.EnvironmentName)
            .Select(e => new { e.Id, e.ApplicationFamily })
            .FirstOrDefaultAsync(ct);

        BcEnvironment? live;
        try
        {
            live = await _admin.GetEnvironmentAsync(token, env?.ApplicationFamily, delivery.EnvironmentName, ct);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("Delivery {DeliveryId}: couldn't re-read environment {Env} before publishing: {Message}.",
                delivery.Id, delivery.EnvironmentName, ex.Message);
            return null;
        }

        if (live is null)
        {
            return $"Business Central no longer has an environment called '{delivery.EnvironmentName}'. Refresh the environments on the solution's Business Central page.";
        }

        if (env is not null)
        {
            var stamped = DateTime.UtcNow;
            await _db.OeProjectEnvironments.Where(e => e.Id == env.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, live.Status)
                    .SetProperty(e => e.StatusFetchedAt, stamped), ct);
        }

        var refusal = BcEnvironmentStatus.RefusalMessage(delivery.EnvironmentName, live.Status);
        if (refusal is not null)
        {
            _logger.LogWarning("Delivery {DeliveryId} refused: environment {Env} is {Status}.",
                delivery.Id, delivery.EnvironmentName, live.Status);
        }
        return refusal;
    }

    /// <summary>
    /// Polls one install operation by id until it reports a terminal state or the per-app
    /// timeout elapses. Keyed on the operation and app ids Business Central returned from
    /// the upload, so two extensions that happen to share a display name can't be mistaken
    /// for each other.
    /// </summary>
    private async Task<DeploymentOutcome> PollUntilTerminalAsync(
        BcDeliveryContext bc, string family, OeProjectDelivery delivery, BcAppOperation started, CancellationToken ct)
    {
        if (started.AppId is not { } appId)
        {
            // Without an app id there's nothing to poll. The upload was accepted, so
            // don't call it a failure — say what's unverified and let the consultant look.
            return new DeploymentOutcome(true, "Business Central accepted the upload but didn't say which app it was, so the install wasn't confirmed here.");
        }

        var deadline = DateTime.UtcNow + PollTimeoutPerApp;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var operation = await _apps.GetAppOperationAsync(
                bc.AccessToken, family, delivery.EnvironmentName, appId, started.Id, ct);

            switch (operation?.Status)
            {
                case BcAppOperationStatus.Succeeded:
                    return new DeploymentOutcome(true, null);
                case BcAppOperationStatus.Failed:
                    return DescribeFailure(operation);
                case BcAppOperationStatus.Canceled:
                    return new DeploymentOutcome(false, "The install was cancelled in Business Central.");
                case BcAppOperationStatus.Skipped:
                    return new DeploymentOutcome(false, "Business Central skipped the install.");
                // Scheduled / Running / Unknown, and a not-yet-visible operation → keep polling.
            }

            if (DateTime.UtcNow > deadline)
            {
                return new DeploymentOutcome(false, "Timed out waiting for the install to finish.");
            }
            await Task.Delay(PollDelay, ct);
        }
    }

    /// <summary>
    /// Turns a failed operation into what the history stores (#930): the app's message is
    /// the code's sentence followed by Business Central's own message, verbatim, and the raw
    /// text goes to the log. The codes lead because <see cref="BcAppOperation.ErrorMessage"/>
    /// comes back in the <em>environment's</em> language - shown, never branched on. The
    /// codes the client already read win over the ones parsed from the text.
    /// </summary>
    private static DeploymentOutcome DescribeFailure(BcAppOperation operation)
    {
        var parsed = BcFailureText.Parse(operation.ErrorMessage);
        var detail = parsed with
        {
            Code = string.IsNullOrEmpty(operation.ErrorCode) ? parsed.Code : operation.ErrorCode,
            InnerCode = string.IsNullOrEmpty(operation.InnerErrorCode) ? parsed.InnerCode : operation.InnerErrorCode,
        };
        var raw = string.IsNullOrWhiteSpace(operation.ErrorMessage) ? null : operation.ErrorMessage;
        return new DeploymentOutcome(false, BcFailureText.AppMessage(detail), detail, raw);
    }

    /// <param name="Failure">Set when Business Central reported the install as failed, so the delivery's line is built from its code.</param>
    /// <param name="Raw">Business Central's text as it came, for the log.</param>
    private sealed record DeploymentOutcome(bool Completed, string? Message, BcFailureDetail? Failure = null, string? Raw = null);

    /// <summary>A multi-line response folded onto one log line, so the page reads the log a line at a time.</summary>
    private static string OneLine(string text) =>
        string.Join(" ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

    // ── Reads (for delivery history) ──────────────────────────────────────────

    /// <summary>A deployment pipeline's deliveries, newest first, without the per-app rows or blobs.</summary>
    public async Task<List<OeProjectDelivery>> ListDeliveriesAsync(int releasePipelineId, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(releasePipelineId, ct);
        return await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == releasePipelineId)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// A deployment pipeline's deliveries, newest first, each with its per-app result rows
    /// (ordered) — for the delivery-history UI. The result rows carry no blobs, so this
    /// stays cheap. Triggering-user display names are resolved alongside.
    /// </summary>
    public Task<List<DeliveryHistoryRow>> ListDeliveryHistoryAsync(int releasePipelineId, CancellationToken ct = default)
        => ListDeliveryHistoryAsync(releasePipelineId, int.MaxValue, ct);

    /// <summary>
    /// The newest <paramref name="limit"/> deliveries of a deployment pipeline, the way
    /// <see cref="ListDeliveryHistoryAsync(int, CancellationToken)"/> reads them, for a
    /// page that shows the latest few and extends the list on request. Each row carries
    /// its per-pipeline <see cref="DeliveryHistoryRow.Number"/> (1 for the pipeline's
    /// first deployment), so a row whose number is above 1 says there are older ones.
    /// </summary>
    public async Task<List<DeliveryHistoryRow>> ListDeliveryHistoryAsync(int releasePipelineId, int limit, CancellationToken ct = default)
    {
        await EnsureCanViewReleasePipelineAsync(releasePipelineId, ct);
        var query = _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ReleasePipelineId == releasePipelineId);

        // "Deployment 49" is display-only: the position in this pipeline's history, not a
        // stored column. Counting once and numbering down from it is exact as long as
        // the order below is total, hence the id tie-break.
        var total = await query.CountAsync(ct);
        if (total == 0) return new List<DeliveryHistoryRow>();

        var rows = await query
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Take(Math.Max(1, limit))
            .Select(d => new DeliveryHistoryRow(
                d.Id,
                d.ProjectBuildId,
                d.Status,
                d.EnvironmentName,
                d.ScheduledFor,
                d.ScheduledOutsideWindow,
                d.CreatedAt,
                d.StartedAt,
                d.FinishedAt,
                d.FailureMessage,
                d.TriggeredByUser != null ? d.TriggeredByUser.DisplayName : null,
                d.Results.OrderBy(r => r.Ordering)
                    .Select(r => new DeliveryAppRow(r.AppName, r.AppVersion, r.Status, r.Message)
                    {
                        AppId = r.AppId,
                        OperationId = r.OperationId,
                        PreviousVersion = r.PreviousVersion,
                        StartedAt = r.StartedAt,
                        FinishedAt = r.FinishedAt,
                    })
                    .ToList())
            {
                ClaimedAt = d.ClaimedAt,
                InstallStartedAt = d.InstallStartedAt,
                DeploymentSchedule = d.DeploymentSchedule,
                SchemaSyncMode = d.SchemaSyncMode,
                CancelledByName = d.CancelledByUser != null ? d.CancelledByUser.DisplayName : null,
                DismissReason = d.DismissReason,
                ReplacedByBuildId = d.ReplacedByProjectBuildId,
                BuildBranch = d.ProjectBuild != null ? d.ProjectBuild.Branch : null,
                BuildReleaseTag = d.ProjectBuild != null ? d.ProjectBuild.GithubReleaseTag : null,
                DiagnosticsLog = d.DiagnosticsLog,
            })
            .ToListAsync(ct);

        for (var i = 0; i < rows.Count; i++)
        {
            rows[i] = rows[i] with { Number = total - i };
        }
        return rows;
    }

    /// <summary>
    /// Why the skipped apps in one delivery were skipped, for the deployment page's per-app
    /// rows. A run skips every app after the first failure whether or not it needed the
    /// failed one, so a row may only say "because it depends on" when the build's own
    /// manifests show that it does - directly or through another skipped app. The
    /// manifests are read from the stored <c>.app</c> files one at a time, only for the
    /// skipped apps, and only when asked (a row being opened), never on the list read.
    /// Returns null when the delivery has no failed app to point at (a run refused before
    /// its first upload skips everything with no app at fault) or does not exist.
    /// </summary>
    public async Task<DeliverySkipReasons?> GetSkipReasonsAsync(int deliveryId, CancellationToken ct = default)
    {
        var delivery = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new { d.ProjectId, d.ProjectBuildId })
            .FirstOrDefaultAsync(ct);
        if (delivery is null) return null;
        await _access.EnsureCanViewAsync(delivery.ProjectId, ct);

        var results = await _db.OeProjectDeliveryResults.AsNoTracking()
            .Where(r => r.ProjectDeliveryId == deliveryId)
            .OrderBy(r => r.Ordering)
            .Select(r => new { r.Ordering, r.Status, r.AppId, r.AppName, r.AppVersion })
            .ToListAsync(ct);
        var failed = results.FirstOrDefault(r => r.Status == ProjectDeliveryResultStatus.Failed);
        if (failed is null) return null;

        // Results line up with the build's artifacts by position: both were taken from
        // the artifacts ordered by id when the delivery was created.
        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == delivery.ProjectBuildId)
            .OrderBy(a => a.Id)
            .Select(a => new { a.Id, a.AppId })
            .ToListAsync(ct);

        // The failed app's id as the manifests know it (the artifact's, stamped from its
        // own manifest), and as Business Central reported it on the upload if it got
        // that far. The two agree in practice; either is enough to match a dependency.
        var blocked = new HashSet<Guid>();
        if (failed.Ordering < artifacts.Count && Guid.TryParse(artifacts[failed.Ordering].AppId, out var fromManifest))
        {
            blocked.Add(fromManifest);
        }
        if (Guid.TryParse(failed.AppId, out var fromUpload))
        {
            blocked.Add(fromUpload);
        }

        var dependents = new HashSet<int>();
        var readable = blocked.Count > 0;
        foreach (var skipped in results.Where(r => r.Status == ProjectDeliveryResultStatus.Skipped && r.Ordering > failed.Ordering))
        {
            if (!readable || skipped.Ordering >= artifacts.Count) { readable = false; break; }
            var bytes = await _db.OeProjectBuildArtifacts.AsNoTracking()
                .Where(a => a.Id == artifacts[skipped.Ordering].Id)
                .Select(a => a.Content)
                .FirstAsync(ct);
            var manifest = await Import.AppPackageReader.TryReadManifestAsync(bytes, ct);
            if (manifest is null) { readable = false; break; }

            // Dependency order means anything this app needs came before it, so one pass
            // in order carries a transitive dependency through.
            if (manifest.Dependencies.Any(dep => blocked.Contains(dep.AppId)))
            {
                dependents.Add(skipped.Ordering);
                blocked.Add(manifest.AppId);
            }
        }

        return new DeliverySkipReasons(failed.AppName, failed.AppVersion, readable ? dependents : null);
    }

    /// <summary>
    /// Gates a deployment-pipeline-keyed delivery read on its project's visibility —
    /// deliveries inherit it like everything else under a project. One that doesn't
    /// exist passes; the read below returns nothing on its own.
    /// </summary>
    private async Task EnsureCanViewReleasePipelineAsync(int releasePipelineId, CancellationToken ct)
    {
        var projectId = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId)
            .Select(r => (int?)r.ProjectId)
            .FirstOrDefaultAsync(ct);
        if (projectId is { } id) await _access.EnsureCanViewAsync(id, ct);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task SaveResultAsync(OeProjectDelivery delivery, StringBuilder log, CancellationToken ct)
    {
        delivery.DiagnosticsLog = log.ToString();
        await _db.SaveChangesAsync(ct);
    }

    private async Task FailAsync(OeProjectDelivery delivery, StringBuilder log, string message, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        delivery.Status = ProjectDeliveryStatus.Failed;
        delivery.FailureMessage = message;
        delivery.FinishedAt = now;
        delivery.UpdatedAt = now;
        Append(log, "Delivery failed: " + message);
        delivery.DiagnosticsLog = log.ToString();
        // Any app still pending/uploading didn't get there.
        foreach (var r in delivery.Results.Where(r => r.Status is ProjectDeliveryResultStatus.Pending
                                                       or ProjectDeliveryResultStatus.Uploading
                                                       or ProjectDeliveryResultStatus.Installing))
        {
            r.Status = ProjectDeliveryResultStatus.Skipped;
            r.UpdatedAt = now;
        }
        await _db.SaveChangesAsync(ct);
    }

    private static void Append(StringBuilder log, string line) => log.Append(LogLine(line));

    /// <summary>One line of a delivery's log as the run writes it: <c>HH:mm:ss  message</c>, UTC.</summary>
    private static string LogLine(string line) =>
        DateTime.UtcNow.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + line + Environment.NewLine;

    /// <summary>The acting person's name for a history line, or "someone" once there is none to read.</summary>
    private async Task<string> CurrentUserNameAsync(CancellationToken ct)
    {
        var userId = _orgContext.CurrentUserId;
        if (userId is null) return "someone";
        var name = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? "someone" : name.Trim();
    }

    private static string Short(string message) => message.Length > 300 ? message[..300] : message;

    private static PlanValidationException Validation(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}

/// <summary>
/// The lines a prepared deployment (#934) writes into its delivery's log: where it came
/// from, and who approved or dismissed it or which build replaced it. Written for a
/// person reading the log, and never read back: what became of a prepared deployment is
/// its status, <see cref="OeProjectDelivery.CancelledByUserId"/>,
/// <see cref="OeProjectDelivery.DismissReason"/> and
/// <see cref="OeProjectDelivery.ReplacedByProjectBuildId"/>.
/// </summary>
public static class DeliveryProposalLog
{
    public static string Prepared(int buildId) =>
        $"Prepared from build #{buildId} when it succeeded. Nothing is sent until someone approves it.";

    public static string Approved(string who) => $"Approved by {who}.";

    public static string Dismissed(string who, string? reason) =>
        reason is null ? $"Dismissed by {who}." : $"Dismissed by {who}: {reason}";

    public static string Replaced(int newerBuildId) =>
        $"{ReplacedReason(newerBuildId)} before anyone approved it.";

    /// <summary>What <see cref="OeProjectDelivery.DismissReason"/> holds for a replacement.</summary>
    public static string ReplacedReason(int newerBuildId) => $"Replaced by build #{newerBuildId}";
}

/// <summary>A delivery for the history list, with its per-app rows resolved for display.</summary>
public sealed record DeliveryHistoryRow(
    int Id,
    int ProjectBuildId,
    string Status,
    string EnvironmentName,
    DateTime ScheduledFor,
    bool ScheduledOutsideWindow,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? FailureMessage,
    string? TriggeredByName,
    IReadOnlyList<DeliveryAppRow> Apps)
{
    /// <summary>The deployment's position in its pipeline's history, 1 for the first. Display only (#929); not stored.</summary>
    public int Number { get; init; }

    /// <summary>When the worker took the row.</summary>
    public DateTime? ClaimedAt { get; init; }

    /// <summary>When the first app's upload was accepted and installing began. Null for rows written before it was recorded.</summary>
    public DateTime? InstallStartedAt { get; init; }

    /// <summary>The install timing the delivery was made with (snapshot).</summary>
    public string DeploymentSchedule { get; init; } = string.Empty;

    /// <summary>The schema-sync mode the delivery was made with (snapshot).</summary>
    public string SchemaSyncMode { get; init; } = string.Empty;

    /// <summary>Who cancelled it. Null unless cancelled, and for cancellations before this was recorded.</summary>
    public string? CancelledByName { get; init; }

    /// <summary>The branch the deployed build was made from, when it was built here.</summary>
    public string? BuildBranch { get; init; }

    /// <summary>The GitHub release tag the build was staged from, for a release-sourced pipeline.</summary>
    public string? BuildReleaseTag { get; init; }

    /// <summary>The run's secret-free log, <c>HH:mm:ss  message</c> lines in UTC. Null until the run starts.</summary>
    /// <summary>
    /// The run's own log, for the page's diagnostics block. Kept out of the MCP
    /// serialisation: list_deployments would otherwise carry every run's log on
    /// every call, and an assistant that needs it has the failure text and the
    /// per-app results already.
    /// </summary>
    [JsonIgnore]
    public string? DiagnosticsLog { get; init; }

    /// <summary>True while the delivery is still working (so the page keeps polling).</summary>
    public bool IsLive => !ProjectDeliveryStatus.IsTerminal(Status);

    /// <summary>True while a worker is actively publishing — the page polls only for these (a far-future scheduled row needn't poll).</summary>
    public bool IsActive => Status is ProjectDeliveryStatus.Claimed or ProjectDeliveryStatus.Uploading or ProjectDeliveryStatus.Installing;

    /// <summary>True for a delivery waiting for its scheduled time — the cancellable/reschedulable state.</summary>
    public bool IsScheduled => Status == ProjectDeliveryStatus.Scheduled;

    /// <summary>A deployment the pipeline prepared from a new build, waiting for someone to approve it (#934).</summary>
    [JsonIgnore]
    public bool IsProposed => Status == ProjectDeliveryStatus.Proposed;

    /// <summary>
    /// A prepared deployment set aside before anyone approved it (#934): dismissed by a
    /// person (<see cref="CancelledByName"/>) or replaced by a newer build
    /// (<see cref="ReplacedByBuildId"/>). It never ran, so it is history rather than "the
    /// last deployment".
    /// </summary>
    [JsonIgnore]
    public bool IsDismissed => Status == ProjectDeliveryStatus.Dismissed;

    /// <summary>For a dismissed prepared deployment: the reason given, or "Replaced by build #N". Null otherwise, or when no reason was given.</summary>
    public string? DismissReason { get; init; }

    /// <summary>For a prepared deployment a newer build replaced: that build's id. Null otherwise.</summary>
    public int? ReplacedByBuildId { get; init; }
}

/// <summary>One app's outcome within a delivery, for the history's per-app breakdown.</summary>
public sealed record DeliveryAppRow(string AppName, string AppVersion, string Status, string? Message)
{
    /// <summary>The app id Business Central read from the upload. Null before the upload.</summary>
    public string? AppId { get; init; }

    /// <summary>The Business Central install operation, for finding the install in the admin center.</summary>
    public Guid? OperationId { get; init; }

    /// <summary>The version installed before the run. Null when the app was new to the environment, or the row predates the recording.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>When this app's upload began.</summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>When this app reached its outcome.</summary>
    public DateTime? FinishedAt { get; init; }
}

/// <summary>
/// Why a delivery's skipped apps were skipped: the app that failed, and which of the
/// skipped apps (by publish order) depend on it. <see cref="DependentOrderings"/> is null
/// when the build's manifests could not be read, and the page then says only that the
/// app was skipped after the failure.
/// </summary>
public sealed record DeliverySkipReasons(string FailedAppName, string FailedAppVersion, IReadOnlySet<int>? DependentOrderings);
