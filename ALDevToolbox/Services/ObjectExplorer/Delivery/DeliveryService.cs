using System.Text.Json.Serialization;
using System.Text;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Notifications;
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

    /// <summary>
    /// Asks Business Central who is signed in to the deployment pipeline's target
    /// environment, for the "are you sure" a person sees before a build that installs
    /// right away. Gated like deploying itself (owner or org Admin), since only someone who
    /// may deploy is asked. Read live, never cached and never stored.
    /// <para>
    /// Business Central failing to answer is not a reason to stop somebody deploying, so
    /// it comes back as <see cref="OpenSessionsCheck.Failure"/> for the page to show rather
    /// than as an exception. A pipeline that no longer exists throws
    /// <see cref="PlanValidationException"/>; not being allowed to deploy throws
    /// <see cref="ProjectAccessDeniedException"/>.
    /// </para>
    /// </summary>
    public async Task<OpenSessionsCheck> CheckOpenSessionsAsync(int releasePipelineId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var rp = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == releasePipelineId && r.DeletedAt == null)
            .Select(r => new
            {
                r.ProjectId,
                OwnerId = r.Project!.CreatedByUserId,
                EnvName = r.ProjectEnvironment!.Name,
                r.ProjectEnvironment.ApplicationFamily,
            })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("ReleasePipeline", "This deployment pipeline no longer exists.");
        await _access.EnsureCanManageAsync(rp.ProjectId, rp.OwnerId, ct);

        // The person is waiting in a dialog for this answer, so it gets a short leash rather
        // than the HTTP client's own timeout.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OpenSessionsTimeout);

        BcDeliveryContext bc;
        try
        {
            bc = await _tokens.AcquireDeliveryContextAsync(rp.ProjectId, timeout.Token);
        }
        catch (BcApiException ex)
        {
            // The connection's own sentence (not set up, secret expired) says what to fix.
            _logger.LogWarning("Couldn't sign in to check the sessions on {Env} before deploying through pipeline {ReleasePipelineId}: {Message}",
                rp.EnvName, releasePipelineId, ex.Message);
            return OpenSessionsCheck.Unknown(rp.EnvName, ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TookTooLong();
        }

        try
        {
            var sessions = await _admin.ListSessionsAsync(bc.AccessToken, rp.ApplicationFamily, rp.EnvName, timeout.Token);
            return OpenSessionsCheck.From(rp.EnvName, sessions);
        }
        catch (BcApiException ex)
        {
            _logger.LogWarning("Couldn't read the sessions on {Env} before deploying through pipeline {ReleasePipelineId}: {Message}",
                rp.EnvName, releasePipelineId, ex.Message);
            return OpenSessionsCheck.Unknown(rp.EnvName, "Business Central didn't answer.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TookTooLong();
        }

        OpenSessionsCheck TookTooLong()
        {
            _logger.LogWarning("Reading the sessions on {Env} before deploying through pipeline {ReleasePipelineId} timed out.",
                rp.EnvName, releasePipelineId);
            return OpenSessionsCheck.Unknown(rp.EnvName, "Business Central took too long to answer.");
        }
    }

    /// <summary>How long <see cref="CheckOpenSessionsAsync"/> waits for Business Central. Shortened by tests.</summary>
    internal TimeSpan OpenSessionsTimeout { get; set; } = TimeSpan.FromSeconds(15);

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
                r.RestrictBranch,
                r.AllowedBranch,
                r.ProjectEnvironmentId,
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
            .Select(b => new { b.Id, b.ProjectId, b.PipelineId, b.Status, b.GithubReleaseTag, b.BcTarget, b.Branch })
            .FirstOrDefaultAsync(ct)
            ?? throw Validation("Build", "That build no longer exists.");

        // A preview build is a check: compiled against a Business Central version the
        // customers don't run yet, from manifests that still name the current one, so
        // it would install on today's environment while built for tomorrow's. Refused
        // here, the one place every deployment passes through - the dialog, an
        // approval, a prepared deployment and the agent's deploy_build alike. See
        // .design/object-explorer-project-builds.md, "Building against the next version".
        if (ProjectBuildTarget.IsPreview(build.BcTarget))
        {
            throw Validation("Build",
                "That is a preview build. It was compiled against an upcoming Business Central version to check for breaking changes, so it can't be deployed.");
        }

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

        // The branch rule: a test branch's build can't reach the environment by way of a
        // build pipeline whose branch was changed. Only a build pipeline's builds carry a
        // branch; a pipeline that installs GitHub releases has none to compare. See
        // .design/saas-delivery.md, "Which branch may reach an environment".
        if (rp.ArtifactSource == ReleaseArtifactSource.Build
            && rp.RestrictBranch
            && !DeploymentBranchRule.Allows(rp.AllowedBranch, build.Branch))
        {
            throw Validation("Build",
                $"Build #{build.Id} was built from {DeploymentBranchRule.Describe(build.Branch)}, but this deployment pipeline only deploys builds from "
                + $"{DeploymentBranchRule.Describe(rp.AllowedBranch)}. Deploy a build made from that branch, or edit this deployment pipeline to allow {DeploymentBranchRule.Describe(build.Branch)}.");
        }

        var artifacts = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == build.Id)
            .OrderBy(a => a.Id)
            .Select(a => new ReleaseApp(a.AppName, a.AppVersion, a.AppId, a.CarriedFromBuildId != null))
            .ToListAsync(ct);
        if (artifacts.Count == 0)
        {
            throw Validation("Build", "That build has no deliverable apps to publish.");
        }

        // Business Central never installs an older version of an app over a newer one.
        // The environment's app list as last read says whether that would happen, so the
        // refusal comes now, in words, rather than from the upload hours later. The run
        // checks again against the live list before anything is sent.
        if (await OlderThanInstalledAsync(rp.ProjectEnvironmentId, artifacts, ct) is { } older)
        {
            throw Validation("Build",
                $"{older.App.AppName} {older.App.AppVersion} in build #{build.Id} is older than {older.Installed}, which is already installed in {rp.EnvName}. "
                + "Business Central won't replace an app with an older version. " + RaiseVersionAdvice
                + $" If {rp.EnvName} has changed recently, refresh the environments on the solution's Business Central page and try again.");
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

    /// <summary>What to do about an app older than the installed one, for both refusals.</summary>
    private const string RaiseVersionAdvice =
        "Raise the version in app.json, or turn on \"Add the build number to each app's version\" on the build pipeline, then deploy a new build.";

    /// <summary>One app a deployment will install, in the build's order.</summary>
    /// <param name="Carried">Carried over unchanged from an earlier build (#1094): an installed newer version is left alone rather than refused.</param>
    private sealed record ReleaseApp(string AppName, string AppVersion, string? AppId = null, bool Carried = false);

    /// <summary>
    /// The first app of <paramref name="apps"/> that the environment's mirrored app list
    /// says is installed at a higher version, with that version; null when none is.
    /// An app with no id (an artifact retained before ids were stamped) can't be matched
    /// and is left to the run's live check.
    /// </summary>
    private async Task<(ReleaseApp App, string Installed)?> OlderThanInstalledAsync(
        int environmentId, IReadOnlyList<ReleaseApp> apps, CancellationToken ct)
    {
        var installed = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => a.EnvironmentId == environmentId)
            .Select(a => new { a.AppId, a.Version })
            .ToListAsync(ct);
        foreach (var app in apps)
        {
            if (app.Carried || !Guid.TryParse(app.AppId, out var id)) continue;
            var on = installed.FirstOrDefault(a => a.AppId == id)?.Version;
            if (!string.IsNullOrWhiteSpace(on) && ProjectConnectionService.CompareVersions(on, app.AppVersion) > 0)
            {
                return (app, on);
            }
        }
        return null;
    }

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
    /// Pull-request builds and preview builds are never prepared. Returns the ids of the
    /// deployments it prepared, so the people who approve them can be told (#1036).
    /// </summary>
    public async Task<List<int>> ProposeReleasesForBuildAsync(int projectBuildId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var build = await _db.OeProjectBuilds.AsNoTracking()
            .Where(b => b.Id == projectBuildId)
            .Select(b => new { b.Id, b.PipelineId, b.Status, b.Trigger, b.BcTarget })
            .FirstOrDefaultAsync(ct);
        if (build?.PipelineId is not { } buildPipelineId
            || build.Status != ProjectBuildStatus.Ready
            || build.Trigger == ProjectBuildTrigger.PullRequest
            || ProjectBuildTarget.IsPreview(build.BcTarget))
        {
            return [];
        }

        var pipelineIds = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.DeletedAt == null
                        && r.PrepareReleaseOnNewBuild
                        && r.ArtifactSource == ReleaseArtifactSource.Build
                        && r.BuildPipelineId == buildPipelineId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(ct);

        var prepared = new List<int>();
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
            // The newer build's request replaces theirs; the old one has nothing left to approve.
            await NotificationSubject.MarkDoneAsync(
                _db, waiting.Select(w => NotificationSubject.Delivery(w.Id)).ToList(), DateTime.UtcNow, _logger, ct);

            var delivery = await WriteDeliveryAsync(orgId, plan, plan.RuleTime(DateTime.UtcNow), forceSyncOnce: false, proposed: true, ct);
            prepared.Add(delivery.Id);
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

        await NotificationSubject.MarkDoneAsync(_db, [NotificationSubject.Delivery(deliveryId)], now, _logger, ct);

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
        await NotificationSubject.MarkDoneAsync(_db, [NotificationSubject.Delivery(deliveryId)], now, _logger, ct);
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
    /// What the Reschedule dialog may offer for one waiting delivery - <em>scheduled</em>,
    /// or handed to Business Central and still held there: the environment's delivery
    /// window (when it has one), and whether the build can wait for Business Central's
    /// next minor or major update - it can't when it has several apps (Business Central
    /// picks the order) or an app the environment doesn't have yet (the API refuses those
    /// timings for a first install). Null when the delivery is gone or there is nothing
    /// waiting to move; <see cref="WhyNotReschedulableAsync"/> says which. Access-gated
    /// like the reschedule itself.
    /// </summary>
    public async Task<RescheduleOptions?> GetRescheduleOptionsAsync(int deliveryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var info = await RescheduleInfoAsync(deliveryId, ct);
        if (info is null || info.Status is not (ProjectDeliveryStatus.Scheduled or ProjectDeliveryStatus.HandedOff)) return null;
        await _access.EnsureCanManageAsync(info.ProjectId, info.OwnerId, ct);
        if (info.Status == ProjectDeliveryStatus.HandedOff && (await HeldAppAsync(info, ct)).App is null) return null;

        var tz = UpdateWindow.ResolveTimeZone(info.TimeZone);
        var hasWindow = UpdateWindow.IsConfigured(info.WindowStart, info.WindowEnd);
        DateTime? nextOpening = hasWindow
            ? UpdateWindow.NextOpeningUtc(info.WindowStart, info.WindowEnd, tz, DateTime.UtcNow)
            : null;
        var current = CurrentTiming(info.DeploymentSchedule, info.ScheduledByDeliveryWindow, info.ScheduledOutsideWindow, hasWindow);
        var update = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == info.EnvironmentId)
            .Select(e => new { e.BcNextUpdateVersion, e.BcNextUpdateType, e.BcNextUpdateDate })
            .FirstOrDefaultAsync(ct);
        var syncMode = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId).Select(d => d.SchemaSyncMode).FirstOrDefaultAsync(ct);
        return new RescheduleOptions(
            deliveryId, info.ReleasePipelineId, info.EnvironmentName, info.ScheduledFor, current,
            info.TimeZone, info.WindowStart, info.WindowEnd, nextOpening,
            await WhyNotLaterUpdateAsync(info, ct))
        {
            BuildId = info.ProjectBuildId,
            NextUpdateVersion = update?.BcNextUpdateVersion,
            NextUpdateType = update?.BcNextUpdateType,
            NextUpdateDate = update?.BcNextUpdateDate,
            ForceSync = BcSyncMode.Normalize(syncMode) == BcSyncMode.ForceSync,
            HeldByBusinessCentral = info.Status == ProjectDeliveryStatus.HandedOff,
        };
    }

    /// <summary>
    /// Why <see cref="GetRescheduleOptionsAsync"/> came back empty for a delivery, in a
    /// sentence for the page.
    /// </summary>
    public async Task<string> WhyNotReschedulableAsync(int deliveryId, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var info = await RescheduleInfoAsync(deliveryId, ct);
        if (info is null) return "That deployment no longer exists.";
        await _access.EnsureCanViewAsync(info.ProjectId, ct);
        return info.Status switch
        {
            ProjectDeliveryStatus.HandedOff => (await HeldAppAsync(info, ct)).Why
                ?? "That deployment can be rescheduled now. Try again.",
            ProjectDeliveryStatus.Scheduled => "That deployment can be rescheduled now. Try again.",
            _ when ProjectDeliveryStatus.IsTerminal(info.Status) => "That deployment has finished or was cancelled, so there is nothing waiting to reschedule.",
            _ => "That deployment has already started, so it can no longer be rescheduled.",
        };
    }

    /// <summary>
    /// Moves a delivery that hasn't installed yet to another timing: right away, a picked
    /// time, the environment's next delivery window, or Business Central's next minor or
    /// major update. The last two change what is sent: the apps go up right away and
    /// Business Central installs them with that update, as a pipeline set to that timing
    /// would. Recomputes the outside-window audit flag. Access-gated and step-up-gated, as
    /// deploying is. Returns the id of the delivery that now carries the deployment.
    /// <para>
    /// A <em>scheduled</em> delivery is moved in place (atomic on <c>scheduled</c>) and
    /// queued at once when the new time is now or past. A delivery already
    /// <em>handed to Business Central</em> can't be moved there - the admin API can only
    /// cancel a held install - so it is replaced (#1097): a new delivery of the same build
    /// is written first, Business Central's copy is cancelled, and only then does the new
    /// one take the timing. If Business Central refuses the cancel, the new delivery is
    /// removed again and nothing has changed. The old delivery is marked cancelled, with
    /// a line in its log saying which deployment took its place.
    /// </para>
    /// Throws if the delivery has started, is gone, or the timing isn't possible for it.
    /// </summary>
    /// <param name="atUtc">The picked time for <see cref="RescheduleTiming.AtTime"/>; ignored otherwise.</param>
    public async Task<int> RescheduleDeliveryAsync(int deliveryId, RescheduleTiming timing, DateTime? atUtc = null, CancellationToken ct = default)
    {
        RequireOrganizationId();
        var info = await RescheduleInfoAsync(deliveryId, ct)
            ?? throw Validation("Delivery", "That delivery no longer exists.");
        await _access.EnsureCanManageAsync(info.ProjectId, info.OwnerId, ct);
        // Rescheduling decides when the customer's Business Central credential is spent,
        // as deploying does, so it takes the same step-up rule.
        await _tools.EnsureStepUpAsync(Domain.Tools.ToolKey.Releases, ct);

        if (info.Status == ProjectDeliveryStatus.HandedOff)
        {
            return await MoveHeldDeliveryAsync(info, timing, atUtc, ct);
        }
        if (info.Status != ProjectDeliveryStatus.Scheduled)
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be rescheduled.");
        }

        var now = DateTime.UtcNow;
        var t = await ResolveTimingAsync(info, timing, atUtc, now, ct);
        if (!await ApplyTimingAsync(deliveryId, t, now, ct))
        {
            throw Validation("Delivery", "This delivery has already started and can no longer be rescheduled.");
        }
        await EnqueueIfDueAsync(deliveryId, info, t.When, now, ct);
        _logger.LogInformation("Rescheduled delivery {DeliveryId} to {Timing} ({Schedule}, {ScheduledFor:o}).",
            deliveryId, timing, t.Schedule, t.When);
        return deliveryId;
    }

    /// <summary>
    /// The pipeline deployments waiting to install on one environment, for its Scheduled
    /// installs card (#1097): every <c>scheduled</c> delivery whose pipeline targets it,
    /// soonest first, and the handed-off ones Business Central may still be holding, so the
    /// card can offer Reschedule beside the matching install Business Central lists. A
    /// held one is only a candidate (one app, the newest run of its pipeline to reach
    /// Business Central); the card shows it only when Business Central lists that version.
    /// </summary>
    public async Task<List<WaitingDeployment>> ListWaitingDeploymentsAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct);
        var envName = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.ProjectId == projectId)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct);
        if (envName is null) return new List<WaitingDeployment>();

        // A delivery keeps the environment it was made for; the pipeline may have moved on since.
        var rows = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.ProjectId == projectId
                && d.ReleasePipeline!.ProjectEnvironmentId == environmentId
                && d.EnvironmentName == envName
                && (d.Status == ProjectDeliveryStatus.Scheduled || d.Status == ProjectDeliveryStatus.HandedOff))
            .Select(d => new
            {
                d.Id, d.ReleasePipelineId, PipelineName = d.ReleasePipeline!.Name, d.ProjectBuildId,
                d.Status, d.ScheduledFor, d.DeploymentSchedule,
                Held = d.Results
                    .Where(r => r.Status == ProjectDeliveryResultStatus.Scheduled)
                    .Select(r => new { r.AppId, r.AppVersion })
                    .ToList(),
            })
            .ToListAsync(ct);

        // Only the newest run of a pipeline that reached Business Central can still be held there.
        var newestHandedOff = rows
            .Where(r => r.Status == ProjectDeliveryStatus.HandedOff)
            .GroupBy(r => r.ReleasePipelineId)
            .Select(g => g.MaxBy(r => r.Id)!.Id)
            .ToHashSet();
        return rows
            .Where(r => r.Status == ProjectDeliveryStatus.Scheduled
                || (newestHandedOff.Contains(r.Id) && r.Held.Count == 1 && Guid.TryParse(r.Held[0].AppId, out _)))
            .Select(r => new WaitingDeployment(r.Id, r.ReleasePipelineId, r.PipelineName, r.ProjectBuildId,
                r.Status == ProjectDeliveryStatus.HandedOff, r.ScheduledFor, r.DeploymentSchedule,
                r.Status == ProjectDeliveryStatus.HandedOff ? Guid.Parse(r.Held[0].AppId!) : null,
                r.Status == ProjectDeliveryStatus.HandedOff ? r.Held[0].AppVersion : null))
            .OrderBy(r => r.HeldByBusinessCentral).ThenBy(r => r.ScheduledFor).ThenBy(r => r.DeliveryId)
            .ToList();
    }

    /// <summary>A timing worked out for one delivery: when we send, what we send, and the two window flags.</summary>
    private sealed record ResolvedTiming(DateTime When, string Schedule, bool ByWindow, bool OutsideWindow);

    /// <summary>
    /// Works <paramref name="timing"/> out for the delivery, refusing what can't be done:
    /// a picked time that has gone, a window the environment doesn't have, a Business
    /// Central update the build can't wait for.
    /// </summary>
    private async Task<ResolvedTiming> ResolveTimingAsync(
        RescheduleInfo info, RescheduleTiming timing, DateTime? atUtc, DateTime now, CancellationToken ct)
    {
        var tz = UpdateWindow.ResolveTimeZone(info.TimeZone);
        var hasWindow = UpdateWindow.IsConfigured(info.WindowStart, info.WindowEnd);

        DateTime when;
        var schedule = BcDeploymentSchedule.Immediate;
        var byWindow = false;
        switch (timing)
        {
            case RescheduleTiming.Now:
                when = now;
                break;
            case RescheduleTiming.AtTime:
                when = atUtc is { } at
                    ? DateTime.SpecifyKind(at, DateTimeKind.Utc)
                    : throw Validation("ScheduledFor", "Pick a date and time.");
                // A time that has gone would run it now without the "Now" choice's checks.
                if (when < now - ReschedulePastSlack)
                {
                    throw Validation("ScheduledFor", "Pick a time that hasn't happened yet, or choose Now to deploy right away.");
                }
                break;
            case RescheduleTiming.DeliveryWindow:
                if (!hasWindow)
                {
                    throw Validation("Timing",
                        $"{info.EnvironmentName} has no delivery window. Set one on the environment, or pick a time.");
                }
                when = UpdateWindow.NextOpeningUtc(info.WindowStart, info.WindowEnd, tz, now);
                byWindow = true;
                break;
            case RescheduleTiming.NextMinorUpdate:
            case RescheduleTiming.NextMajorUpdate:
                if (await WhyNotLaterUpdateAsync(info, ct) is { } why)
                {
                    throw Validation("Timing", why);
                }
                // Sent now; Business Central holds it until its update.
                when = now;
                schedule = timing == RescheduleTiming.NextMinorUpdate
                    ? BcDeploymentSchedule.NextMinorUpdate
                    : BcDeploymentSchedule.NextMajorUpdate;
                break;
            default:
                throw Validation("Timing", "Choose when the deployment should install.");
        }

        // Only our own time can be outside the delivery window: a later Business Central
        // update installs on Microsoft's schedule, not ours.
        var outsideWindow = schedule == BcDeploymentSchedule.Immediate
            && hasWindow
            && !UpdateWindow.IsWithin(info.WindowStart, info.WindowEnd, tz, when);
        return new ResolvedTiming(when, schedule, byWindow, outsideWindow);
    }

    /// <summary>Writes a timing onto a delivery still <c>scheduled</c>; false when it has started meanwhile.</summary>
    private async Task<bool> ApplyTimingAsync(int deliveryId, ResolvedTiming t, DateTime now, CancellationToken ct) =>
        await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ScheduledFor, t.When)
                .SetProperty(d => d.DeploymentSchedule, t.Schedule)
                .SetProperty(d => d.ScheduledByDeliveryWindow, t.ByWindow)
                .SetProperty(d => d.ScheduledOutsideWindow, t.OutsideWindow)
                .SetProperty(d => d.UpdatedAt, now), ct) > 0;

    private async Task EnqueueIfDueAsync(int deliveryId, RescheduleInfo info, DateTime when, DateTime now, CancellationToken ct)
    {
        if (when > now) return;
        await _queue.EnqueueAsync(new DeliveryJob(deliveryId,
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
                info.OrganizationId, _orgContext.IsSystemOrganization, _orgContext.CurrentUserId ?? info.TriggeredByUserId)), ct);
    }

    /// <summary>
    /// Replaces a delivery Business Central is holding for a later update with a new
    /// delivery of the same build on <paramref name="timing"/>. See
    /// <see cref="RescheduleDeliveryAsync"/> for the order and why.
    /// </summary>
    private async Task<int> MoveHeldDeliveryAsync(RescheduleInfo info, RescheduleTiming timing, DateTime? atUtc, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var t = await ResolveTimingAsync(info, timing, atUtc, now, ct);
        if (t.Schedule == BcDeploymentSchedule.Normalize(info.DeploymentSchedule))
        {
            throw Validation("Timing", "Business Central is already holding it for that update. Pick another time.");
        }

        var (app, notHeld) = await HeldAppAsync(info, ct);
        if (app is not { } held)
        {
            throw Validation("Delivery", notHeld!);
        }

        // Claimed first, so two people moving it at once can't both cancel and redeploy:
        // only one of them turns it from handed off to cancelled.
        var cancelledBy = _orgContext.CurrentUserId;
        if (!await SetHeldStatusAsync(info.DeliveryId, ProjectDeliveryStatus.HandedOff, ProjectDeliveryStatus.Cancelled, cancelledBy, now, CancellationToken.None))
        {
            throw Validation("Delivery", "Someone else has just moved or cancelled this deployment. Close this and check its status.");
        }

        int replacementId;
        try
        {
            // Written far enough out that nothing runs it before it takes the new timing, so
            // a refusal here (the build's branch, an app older than the environment's) leaves
            // Business Central's copy alone.
            replacementId = await CreateDeliveryAsync(
                info.ReleasePipelineId, info.ProjectBuildId, now.AddDays(1),
                forceSyncOnce: BcSyncMode.Normalize(info.SchemaSyncMode) == BcSyncMode.ForceSync, ct);
        }
        catch
        {
            await SetHeldStatusAsync(info.DeliveryId, ProjectDeliveryStatus.Cancelled, ProjectDeliveryStatus.HandedOff, null, null, CancellationToken.None);
            throw;
        }

        var schedule = BcDeploymentSchedule.Normalize(info.DeploymentSchedule) ?? info.DeploymentSchedule;
        var family = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == info.EnvironmentId)
            .Select(e => e.ApplicationFamily)
            .FirstOrDefaultAsync(CancellationToken.None)
            ?? BcConstants.DefaultApplicationFamily;
        string? token = null;
        try
        {
            token = (await _tokens.AcquireDeliveryContextAsync(info.ProjectId, ct)).AccessToken;
            await _apps.RemoveScheduledPteVersionAsync(token, family, info.EnvironmentName, held.AppId, held.Version, schedule, ct);
        }
        catch (Exception ex)
        {
            // A refusal means Business Central still has its copy. Anything else (a timeout,
            // the page closing) may have reached it or not, so ask it before deciding: with
            // its copy gone the move has to finish, or nothing would install at all.
            var stillHeld = ex is BcApiException || token is null
                || await StillHeldAsync(token, family, info.EnvironmentName, held) != false;
            if (stillHeld)
            {
                await _db.OeProjectDeliveryResults.Where(r => r.ProjectDeliveryId == replacementId).ExecuteDeleteAsync(CancellationToken.None);
                await _db.OeProjectDeliveries.Where(d => d.Id == replacementId).ExecuteDeleteAsync(CancellationToken.None);
                await SetHeldStatusAsync(info.DeliveryId, ProjectDeliveryStatus.Cancelled, ProjectDeliveryStatus.HandedOff, null, null, CancellationToken.None);
                _panelCache.Invalidate(info.ProjectId, info.EnvironmentId);
                if (ex is not BcApiException bcEx) throw;
                _logger.LogWarning(bcEx, "Business Central refused to cancel the install held for delivery {DeliveryId}; nothing was moved.", info.DeliveryId);
                throw Validation("Delivery",
                    $"Business Central didn't cancel the copy it is holding, so nothing was changed. It may have installed it already: refresh {info.EnvironmentName} and check. ({bcEx.Message})");
            }
            _logger.LogWarning(ex, "Cancelling the install held for delivery {DeliveryId} failed, but Business Central no longer holds it; finishing the move.", info.DeliveryId);
        }
        _panelCache.Invalidate(info.ProjectId, info.EnvironmentId);

        // Business Central has dropped its copy, so from here the move has to land whatever
        // happens to the request: every write ignores its cancellation.
        var line = LogLine($"Moved: Business Central's copy was cancelled, and deployment #{replacementId} of the same build replaces this one.");
        await _db.OeProjectDeliveries
            .Where(d => d.Id == info.DeliveryId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.FinishedAt, now)
                .SetProperty(d => d.DiagnosticsLog, d => (d.DiagnosticsLog ?? string.Empty) + line), CancellationToken.None);
        await _db.OeProjectDeliveryResults
            .Where(r => r.Id == held.ResultId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ProjectDeliveryResultStatus.Skipped)
                .SetProperty(r => r.Message, $"Moved to deployment #{replacementId} before Business Central installed it.")
                .SetProperty(r => r.UpdatedAt, now), CancellationToken.None);

        if (!await ApplyTimingAsync(replacementId, t, now, CancellationToken.None))
        {
            // Only someone cancelling the replacement in the moment it existed gets here.
            _logger.LogWarning("Delivery {ReplacementId}, which replaces {DeliveryId}, was no longer scheduled when it took its timing.", replacementId, info.DeliveryId);
        }
        await EnqueueIfDueAsync(replacementId, info, t.When, now, CancellationToken.None);
        _logger.LogInformation(
            "Moved delivery {DeliveryId}, held by Business Central for {OldSchedule}, to delivery {ReplacementId} ({Timing}, {Schedule}, {ScheduledFor:o}).",
            info.DeliveryId, info.DeploymentSchedule, replacementId, timing, t.Schedule, t.When);
        return replacementId;
    }

    /// <summary>
    /// Turns a held delivery between handed off and cancelled; false when it wasn't in
    /// <paramref name="from"/>. Cancelling records who; turning it back clears that. The
    /// finish time is left for the move to set once it lands, so a move turned back keeps
    /// the time it was handed off.
    /// </summary>
    private async Task<bool> SetHeldStatusAsync(int deliveryId, string from, string to, int? byUserId, DateTime? at, CancellationToken ct)
    {
        var query = _db.OeProjectDeliveries.Where(d => d.Id == deliveryId && d.Status == from);
        var changed = to == ProjectDeliveryStatus.Cancelled
            ? await query.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, to)
                .SetProperty(d => d.CancelledByUserId, byUserId)
                .SetProperty(d => d.UpdatedAt, at!.Value), ct)
            : await query.ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, to)
                .SetProperty(d => d.CancelledByUserId, (int?)null)
                .SetProperty(d => d.UpdatedAt, DateTime.UtcNow), ct);
        return changed > 0;
    }

    /// <summary>
    /// Whether Business Central still lists the held version, after a cancel whose answer
    /// was lost. Null when it can't be asked either.
    /// </summary>
    private async Task<bool?> StillHeldAsync(string token, string family, string environmentName, HeldApp held)
    {
        try
        {
            var waiting = await _apps.ListScheduledPteOperationsAsync(token, family, environmentName, CancellationToken.None);
            return waiting.Any(w => w.AppId == held.AppId
                && string.Equals(w.TargetAppVersion, held.Version, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't read Business Central's scheduled installs for {Environment} after a failed cancel.", environmentName);
            return null;
        }
    }

    /// <summary>How far in the past a picked time may be and still count as "now": the minute the person spent in the dialog.</summary>
    private static readonly TimeSpan ReschedulePastSlack = TimeSpan.FromMinutes(1);

    /// <summary>What a reschedule reads about a delivery, its pipeline and its environment.</summary>
    private sealed record RescheduleInfo(
        int DeliveryId, int OrganizationId, int? TriggeredByUserId, int ProjectId, int? OwnerId, int ReleasePipelineId,
        int ProjectBuildId, int EnvironmentId, string EnvironmentName, string Status, DateTime ScheduledFor,
        string DeploymentSchedule, bool ScheduledByDeliveryWindow, bool ScheduledOutsideWindow,
        string? TimeZone, TimeOnly? WindowStart, TimeOnly? WindowEnd, string SchemaSyncMode);

    private async Task<RescheduleInfo?> RescheduleInfoAsync(int deliveryId, CancellationToken ct)
    {
        var d = await _db.OeProjectDeliveries.AsNoTracking()
            .Where(d => d.Id == deliveryId)
            .Select(d => new
            {
                d.OrganizationId, d.TriggeredByUserId, d.ProjectId,
                OwnerId = d.ReleasePipeline!.Project!.CreatedByUserId,
                d.ReleasePipelineId, d.ProjectBuildId,
                PipelineEnvironmentId = d.ReleasePipeline.ProjectEnvironmentId,
                d.EnvironmentName, d.Status, d.ScheduledFor, d.DeploymentSchedule,
                d.ScheduledByDeliveryWindow, d.ScheduledOutsideWindow, d.SchemaSyncMode,
                TimeZone = d.ReleasePipeline.Project.BcTimeZone,
            })
            .FirstOrDefaultAsync(ct);
        if (d is null) return null;

        // The environment the delivery installs to is its snapshot name, as the run resolves
        // it - not whatever the pipeline was pointed at since.
        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.ProjectId == d.ProjectId && e.Name == d.EnvironmentName)
            .OrderBy(e => e.Id == d.PipelineEnvironmentId ? 0 : 1)
            .Select(e => new { e.Id, e.UpdateWindowStart, e.UpdateWindowEnd })
            .FirstOrDefaultAsync(ct);

        return new RescheduleInfo(
            deliveryId, d.OrganizationId, d.TriggeredByUserId, d.ProjectId, d.OwnerId, d.ReleasePipelineId,
            d.ProjectBuildId, env?.Id ?? d.PipelineEnvironmentId, d.EnvironmentName, d.Status, d.ScheduledFor,
            d.DeploymentSchedule, d.ScheduledByDeliveryWindow, d.ScheduledOutsideWindow,
            d.TimeZone, env?.UpdateWindowStart, env?.UpdateWindowEnd, d.SchemaSyncMode);
    }

    /// <summary>The one app a handed-off delivery left with Business Central, as the move cancels it.</summary>
    private sealed record HeldApp(int ResultId, Guid AppId, string Version);

    /// <summary>
    /// The app Business Central is still holding for a handed-off delivery, or why there is
    /// none to move. A handed-off row never changes again once written, so "still held" is
    /// read from what came after it: a later run of the same pipeline replaced Business
    /// Central's copy, and the environment showing that version (or newer) installed means
    /// the update has been and gone. Several apps aren't moved: cancelling them one by one
    /// can leave half a build held. Nor is one whose pipeline now deploys somewhere else:
    /// its replacement would go there instead.
    /// </summary>
    private async Task<(HeldApp? App, string? Why)> HeldAppAsync(RescheduleInfo info, CancellationToken ct)
    {
        const string goneWhy = "Business Central isn't holding this deployment any more: it has been installed or replaced by a later one.";
        var held = await _db.OeProjectDeliveryResults.AsNoTracking()
            .Where(r => r.ProjectDeliveryId == info.DeliveryId && r.Status == ProjectDeliveryResultStatus.Scheduled)
            .Select(r => new { r.Id, r.AppId, r.AppVersion })
            .ToListAsync(ct);
        if (held.Count != 1 || !Guid.TryParse(held[0].AppId, out var appId) || string.IsNullOrWhiteSpace(held[0].AppVersion))
        {
            return (null, "This deployment can't be moved from here. Cancel it under Scheduled installs on the environment, then deploy the build again.");
        }

        var later = await _db.OeProjectDeliveries.AsNoTracking()
            .AnyAsync(d => d.ReleasePipelineId == info.ReleasePipelineId && d.Id > info.DeliveryId
                && (d.Status == ProjectDeliveryStatus.HandedOff || d.Status == ProjectDeliveryStatus.Deployed
                    || d.Status == ProjectDeliveryStatus.Claimed || d.Status == ProjectDeliveryStatus.Uploading
                    || d.Status == ProjectDeliveryStatus.Installing), ct);
        if (later) return (null, goneWhy);

        var installed = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => a.EnvironmentId == info.EnvironmentId && a.AppId == appId)
            .Select(a => a.Version)
            .FirstOrDefaultAsync(ct);
        if (Version.TryParse(installed, out var have) && Version.TryParse(held[0].AppVersion, out var want) && have >= want)
        {
            return (null, goneWhy);
        }

        var pipelineEnv = await _db.OeReleasePipelines.AsNoTracking()
            .Where(r => r.Id == info.ReleasePipelineId)
            .Select(r => r.ProjectEnvironment!.Name)
            .FirstOrDefaultAsync(ct);
        if (!string.Equals(pipelineEnv, info.EnvironmentName, StringComparison.Ordinal))
        {
            return (null, $"Its deployment pipeline no longer deploys to {info.EnvironmentName}. Cancel the install under Scheduled installs on {info.EnvironmentName}, then deploy the build again.");
        }
        return (new HeldApp(held[0].Id, appId, held[0].AppVersion), null);
    }

    /// <summary>
    /// Why the build can't wait for Business Central's next minor or major update, or null
    /// when it can. The same two rules the deploy and the run apply: one app only, and only
    /// an app the environment already has (as its app list was last read; the run checks
    /// the live list again).
    /// </summary>
    private async Task<string?> WhyNotLaterUpdateAsync(RescheduleInfo info, CancellationToken ct)
    {
        var apps = await _db.OeProjectBuildArtifacts.AsNoTracking()
            .Where(a => a.ProjectBuildId == info.ProjectBuildId)
            .Select(a => new { a.AppName, a.AppId })
            .ToListAsync(ct);
        if (apps.Count > 1)
        {
            return $"This build has {apps.Count} apps, and Business Central chooses the order it installs them in when they wait for a later update. Pick a time or the delivery window instead.";
        }
        var installed = await _db.OeEnvironmentApps.AsNoTracking()
            .Where(a => a.EnvironmentId == info.EnvironmentId)
            .Select(a => new { a.AppId, a.Name })
            .ToListAsync(ct);
        // An artifact kept from before app ids were recorded is matched by name, as the run does.
        var missing = apps
            .Where(a => Guid.TryParse(a.AppId, out var id)
                ? installed.All(i => i.AppId != id)
                : installed.All(i => !string.Equals(i.Name, a.AppName, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.AppName)
            .ToList();
        return missing.Count > 0
            ? $"{string.Join(", ", missing)} isn't installed on {info.EnvironmentName} yet, and Business Central only waits for a later update with an app that's already there. Pick a time or the delivery window instead."
            : null;
    }

    /// <summary>The timing a scheduled delivery is on now, for the dialog's first choice.</summary>
    private static RescheduleTiming CurrentTiming(string schedule, bool byWindow, bool outsideWindow, bool hasWindow) =>
        BcDeploymentSchedule.Normalize(schedule) switch
        {
            BcDeploymentSchedule.NextMinorUpdate => RescheduleTiming.NextMinorUpdate,
            BcDeploymentSchedule.NextMajorUpdate => RescheduleTiming.NextMajorUpdate,
            _ when byWindow && !outsideWindow && hasWindow => RescheduleTiming.DeliveryWindow,
            _ => RescheduleTiming.AtTime,
        };

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
    /// retried. Returns the ids it failed, so the people behind them can be told (#1036).
    /// </summary>
    public async Task<List<int>> FailInterruptedDeliveriesAsync(CancellationToken ct = default)
    {
        var orphans = await _db.OeProjectDeliveries
            .Where(d => d.Status == ProjectDeliveryStatus.Claimed
                        || d.Status == ProjectDeliveryStatus.Uploading
                        || d.Status == ProjectDeliveryStatus.Installing)
            .Include(d => d.Results)
            .ToListAsync(ct);
        if (orphans.Count == 0) return [];

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
        return orphans.Select(d => d.Id).ToList();
    }

    // ── Run (worker entry) ────────────────────────────────────────────────────

    /// <summary>How early the worker may take a delivery: enough for the scheduler's poll and clock drift, far less than any reschedule.</summary>
    private static readonly TimeSpan ClaimEarlySlack = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Claims the delivery (atomic <c>scheduled → claimed</c>) and runs the publish.
    /// Returns false if the row was already taken or cancelled, or is not due yet - a job
    /// still waiting in the queue for a delivery that was since rescheduled to later must
    /// not run it; the scheduler queues it again when it is due - and true once this call
    /// ran it. All failures are recorded on the row; this method does not throw on a publish
    /// failure.
    /// </summary>
    public async Task<bool> RunDeliveryAsync(int deliveryId, CancellationToken ct = default)
    {
        var claimedAt = DateTime.UtcNow;
        var dueBy = claimedAt + ClaimEarlySlack;
        var claimed = await _db.OeProjectDeliveries
            .Where(d => d.Id == deliveryId && d.Status == ProjectDeliveryStatus.Scheduled && d.ScheduledFor <= dueBy)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, ProjectDeliveryStatus.Claimed)
                .SetProperty(d => d.ClaimedAt, claimedAt)
                .SetProperty(d => d.UpdatedAt, claimedAt), ct);
        if (claimed == 0)
        {
            _logger.LogInformation("Delivery {DeliveryId} was already claimed, cancelled or moved to later; skipping.", deliveryId);
            return false;
        }

        var delivery = await _db.OeProjectDeliveries
            .Include(d => d.Results.OrderBy(r => r.Ordering))
            .FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is null)
        {
            _logger.LogWarning("Delivery {DeliveryId} vanished after being claimed.", deliveryId);
            return false;
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
        return true;
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
            .Select(a => new { a.Id, a.FileName, a.AppId, a.CarriedFromBuildId })
            .ToListAsync(ct);
        // An app the build carried over unchanged from an earlier build (#1094). Where the
        // environment already has it, or something newer from another pipeline, or has it
        // waiting for an update, it is left alone: nothing about it changed in this build.
        bool Carried(int i) => i < artifacts.Count && artifacts[i].CarriedFromBuildId is not null;

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

        // Business Central never replaces an app with an older version. Caught when the
        // deployment was made if the environment's app list said so; this is the live list,
        // which also catches an install that happened since. Checked for every app before
        // the first upload, so a refusal never leaves the build half installed.
        for (var i = 0; i < ordered.Count; i++)
        {
            // Matched on the app id only: a name could be another publisher's app.
            if (appIds[i] is null || Carried(i)
                || InstalledMatch(i)?.Version is not { Length: > 0 } newerOn
                || ProjectConnectionService.CompareVersions(newerOn, ordered[i].AppVersion) <= 0)
            {
                continue;
            }
            var olderRefusal = $"{ordered[i].AppName} {ordered[i].AppVersion} is older than {newerOn}, which is already installed in {delivery.EnvironmentName}. "
                + "Business Central won't replace an app with an older version. " + RaiseVersionAdvice;
            ordered[i].Status = ProjectDeliveryResultStatus.Failed;
            ordered[i].FinishedAt = DateTime.UtcNow;
            ordered[i].UpdatedAt = ordered[i].FinishedAt!.Value;
            ordered[i].Message = olderRefusal;
            await FailAsync(delivery, log, olderRefusal, ct);
            return;
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
            if (Carried(i) && InstalledMatch(i)?.Version is { Length: > 0 } newerOn
                && ProjectConnectionService.CompareVersions(newerOn, result.AppVersion) > 0)
            {
                result.Status = ProjectDeliveryResultStatus.Skipped;
                result.Message = $"Unchanged; {delivery.EnvironmentName} already has the newer {newerOn}.";
                result.UpdatedAt = DateTime.UtcNow;
                Append(log, $"Skipped {label}: unchanged in this build, and {delivery.EnvironmentName} already has {newerOn}.");
                await SaveResultAsync(delivery, log, ct);
                continue;
            }

            if (AlreadyWaiting(waiting, appIds[i], result, delivery) is { } waitingRefusal)
            {
                if (Carried(i))
                {
                    result.Status = ProjectDeliveryResultStatus.Skipped;
                    result.Message = "Unchanged; this version is already waiting for the update.";
                    result.UpdatedAt = DateTime.UtcNow;
                    Append(log, $"Skipped {label}: unchanged in this build and already waiting on {delivery.EnvironmentName}.");
                    await SaveResultAsync(delivery, log, ct);
                    continue;
                }
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
    /// Polls one install operation until it reports a terminal state or the per-app
    /// timeout elapses - the shared <see cref="BcAppOperationPoller"/>, which booked
    /// uploads use too, mapped onto this delivery's outcome shape.
    /// </summary>
    private async Task<DeploymentOutcome> PollUntilTerminalAsync(
        BcDeliveryContext bc, string family, OeProjectDelivery delivery, BcAppOperation started, CancellationToken ct)
    {
        var result = await BcAppOperationPoller.PollUntilTerminalAsync(
            _apps, bc.AccessToken, family, delivery.EnvironmentName, started, PollDelay, PollTimeoutPerApp, ct);
        // A delivery needs a clean yes: an install it could not see finish (no id, a run
        // of failed polls, the wait ran out) is not one it reports as done. A missing app
        // id is the one caveat it has always carried as a success.
        var completed = result.Completed || (result.IsUnconfirmed && started.AppId is null);
        return new DeploymentOutcome(completed, result.Message, result.Failure, result.Raw);
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

/// <summary>When a rescheduled deployment should install. See <see cref="DeliveryService.RescheduleDeliveryAsync"/>.</summary>
public enum RescheduleTiming
{
    /// <summary>Right away: for an urgent fix.</summary>
    Now,

    /// <summary>At a time the person picks.</summary>
    AtTime,

    /// <summary>At the next opening of the environment's delivery window.</summary>
    DeliveryWindow,

    /// <summary>Uploaded right away; Business Central installs it with its next minor update.</summary>
    NextMinorUpdate,

    /// <summary>Uploaded right away; Business Central installs it with its next major update.</summary>
    NextMajorUpdate,
}

/// <summary>
/// What the Reschedule dialog can offer for one scheduled deployment.
/// </summary>
/// <param name="CurrentTiming">The timing it is on now, to open the dialog on.</param>
/// <param name="TimeZone">The customer's IANA zone, which the delivery window and a picked time are read in.</param>
/// <param name="NextWindowOpeningUtc">The next opening of the delivery window; null when the environment has none.</param>
/// <param name="LaterUpdateUnavailable">Why the next minor/major update can't be chosen; null when it can.</param>
public sealed record RescheduleOptions(
    int DeliveryId,
    int ReleasePipelineId,
    string EnvironmentName,
    DateTime ScheduledFor,
    RescheduleTiming CurrentTiming,
    string? TimeZone,
    TimeOnly? WindowStart,
    TimeOnly? WindowEnd,
    DateTime? NextWindowOpeningUtc,
    string? LaterUpdateUnavailable)
{
    /// <summary>The build the deployment installs.</summary>
    public int BuildId { get; init; }

    /// <summary>The environment's next Business Central update as last read: its version, "major"/"minor" as Microsoft spells it, and its date when one is set.</summary>
    public string? NextUpdateVersion { get; init; }
    public string? NextUpdateType { get; init; }
    public DateTime? NextUpdateDate { get; init; }

    /// <summary>True when the deployment installs with Force sync, which can drop data.</summary>
    public bool ForceSync { get; init; }

    /// <summary>
    /// True when Business Central already holds the apps for its next update: moving it
    /// cancels that copy and uploads the build again, as a new deployment.
    /// </summary>
    public bool HeldByBusinessCentral { get; init; }
}

/// <summary>
/// A pipeline deployment waiting to install on an environment: booked here
/// (<paramref name="HeldByBusinessCentral"/> false, runs at <paramref name="ScheduledFor"/>)
/// or handed to Business Central for a later update, holding <paramref name="AppId"/> at
/// <paramref name="AppVersion"/>.
/// </summary>
public sealed record WaitingDeployment(
    int DeliveryId,
    int ReleasePipelineId,
    string PipelineName,
    int BuildId,
    bool HeldByBusinessCentral,
    DateTime ScheduledFor,
    string DeploymentSchedule,
    Guid? AppId,
    string? AppVersion);

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
