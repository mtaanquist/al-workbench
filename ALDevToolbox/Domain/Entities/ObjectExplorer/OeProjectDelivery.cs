using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Delivery;

namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One run of a <see cref="OeReleasePipeline"/> — the analogue of a
/// <see cref="OeProjectBuild"/> for the publish side. Created when a user releases a
/// specific build to the release pipeline's target environment, for now or for a
/// later time; <see cref="DeliveryScheduler"/> enqueues it when it is due and the
/// worker then uploads, installs, and polls each app through the Business Central
/// App Management API. The target details (environment, schedule, sync mode) are
/// <em>snapshotted</em> at creation so later edits to the release pipeline don't
/// rewrite history. Org-scoped via the standard query filter. See
/// <c>.design/saas-delivery.md</c> ("Delivery").
/// </summary>
public class OeProjectDelivery
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The project (customer) — denormalised from the release pipeline so the worker can resolve the BC credentials without a join. Rides the project's lifecycle.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>The release pipeline this is a run of (target + modes source).</summary>
    public int ReleasePipelineId { get; set; }
    public OeReleasePipeline? ReleasePipeline { get; set; }

    /// <summary>The build whose <c>.app</c> artifacts are published. Restricted: a build that's been released can't be hard-removed out from under its delivery history.</summary>
    public int ProjectBuildId { get; set; }
    public OeProjectBuild? ProjectBuild { get; set; }

    /// <summary>
    /// The user who triggered the release. The worker runs under this user's captured
    /// identity. Nullable (<c>ON DELETE SET NULL</c>) so a delivery outlives the account.
    /// </summary>
    public int? TriggeredByUserId { get; set; }
    public User? TriggeredByUser { get; set; }

    // ── Snapshot of the target at creation (immune to later release-pipeline edits) ──

    /// <summary>The target environment name (keys the App Management API URL).</summary>
    public string EnvironmentName { get; set; } = string.Empty;

    /// <summary>
    /// When BC installs the upload (App Management <c>deploymentSchedule</c>): the wire
    /// value actually sent, one of <see cref="BcDeploymentSchedule.All"/>. A pipeline set
    /// to <see cref="BcDeploymentSchedule.OurDeliveryWindow"/> records
    /// <see cref="BcDeploymentSchedule.Immediate"/> here, and says so in
    /// <see cref="ScheduledByDeliveryWindow"/>.
    /// </summary>
    public string DeploymentSchedule { get; set; } = BcDeploymentSchedule.Immediate;

    /// <summary>
    /// True when the release pipeline was set to install in the environment's delivery
    /// window when this was scheduled, so the time was the pipeline's rule rather than a
    /// schedule Business Central applies. Read with <see cref="ScheduledOutsideWindow"/>:
    /// both true means the person releasing overrode the rule (for example with "Now").
    /// A snapshot like the rest of this block, because the pipeline can be edited later.
    /// </summary>
    public bool ScheduledByDeliveryWindow { get; set; }

    /// <summary>The schema-sync mode (App Management <c>syncMode</c>). One of <see cref="BcSyncMode"/>.</summary>
    public string SchemaSyncMode { get; set; } = BcSyncMode.Add;

    // ── Schedule + lifecycle ──

    /// <summary>The UTC instant the delivery is due to run. "Now" for an immediate release; a future instant for a scheduled one (the <c>DeliveryScheduler</c> enqueues it when due).</summary>
    public DateTime ScheduledFor { get; set; }

    /// <summary>
    /// True when the chosen <see cref="ScheduledFor"/> falls <em>outside</em> the target
    /// environment's update window (or it's an immediate release to an environment that
    /// has a window) — i.e. the user overrode the safe default. Recorded for the audit
    /// trail and surfaced in history; the window is a default, not a lock. False when
    /// there's no window or the time is inside it. See <c>.design/saas-delivery.md</c>.
    /// </summary>
    public bool ScheduledOutsideWindow { get; set; }

    /// <summary>Set when the worker atomically claims the row (status <c>scheduled</c> → <c>claimed</c>), after which it's no longer cancellable.</summary>
    public DateTime? ClaimedAt { get; set; }

    /// <summary>Set when the publish actually starts (first upload).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// When the delivery moved from uploading to installing: the first app's upload was
    /// accepted and Business Central began installing it. Apps go one at a time (upload,
    /// install, then the next), so with several apps later uploads follow this moment -
    /// it marks the first hand-over, not the last byte. Null for a delivery that never
    /// got that far, for one handed to Business Central's own schedule (nothing installs
    /// while we watch), and for rows written before it was recorded (#929).
    /// </summary>
    public DateTime? InstallStartedAt { get; set; }

    /// <summary>Set when the delivery reaches a terminal state (<c>deployed</c> / <c>failed</c> / <c>cancelled</c>).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>
    /// Who cancelled the delivery, when it was cancelled from a scheduled state. Null for
    /// every other outcome, for a delivery cancelled before this was recorded (#929), and
    /// once the account is gone (<c>ON DELETE SET NULL</c>).
    /// </summary>
    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }

    /// <summary>
    /// Why a prepared release (#934) was set aside, for a <see cref="ProjectDeliveryStatus.Dismissed"/>
    /// row: the reason the person who dismissed it gave (null when they gave none), or
    /// "Replaced by build #N" when a newer build replaced it. Who dismissed it is
    /// <see cref="CancelledByUserId"/>; a replacement has nobody behind it. Null on every
    /// other row. Stored rather than read back from the log, which is for reading only.
    /// </summary>
    public string? DismissReason { get; set; }

    /// <summary>
    /// True when a new build started this deployment on its own, through a deployment
    /// pipeline set to deploy to a sandbox without approval (#1096). The run refuses it
    /// unless the environment is still a sandbox when it is about to upload.
    /// </summary>
    public bool DeployedWithoutApproval { get; set; }

    /// <summary>
    /// The newer build that replaced this prepared release before anyone approved it
    /// (#934), for a <see cref="ProjectDeliveryStatus.Dismissed"/> row; null when a person
    /// dismissed it, and on every other row. A plain id, not a foreign key: it is a fact
    /// about history, and the build it names may be removed later.
    /// </summary>
    public int? ReplacedByProjectBuildId { get; set; }

    /// <summary>Lifecycle state. See <see cref="ProjectDeliveryStatus"/>.</summary>
    public string Status { get; set; } = ProjectDeliveryStatus.Scheduled;

    /// <summary>A short, secret-free reason when the delivery failed as a whole.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// A secret-free, human-readable log of the publish run (the per-step outcomes and
    /// trimmed API responses) for diagnostics. Never contains the token or the client
    /// secret. Null until the run starts.
    /// </summary>
    public string? DiagnosticsLog { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>Per-app outcomes, in publish order.</summary>
    public ICollection<OeProjectDeliveryResult> Results { get; set; } = new List<OeProjectDeliveryResult>();
}

/// <summary>
/// The lifecycle states a <see cref="OeProjectDelivery"/> moves through:
/// <c>scheduled → claimed → uploading → installing → deployed | handed_off | failed</c>,
/// plus <c>scheduled → cancelled</c>, and for a prepared release
/// <c>proposed → scheduled</c> (approved) or <c>proposed → dismissed</c> (dismissed or
/// replaced). The transitions out of <c>scheduled</c> and <c>proposed</c> are atomic
/// compare-and-set so a claim and a cancel, or an approval and a replacement, can't both win.
/// </summary>
public static class ProjectDeliveryStatus
{
    /// <summary>
    /// Prepared by the pipeline when a new build succeeded, waiting for a person to
    /// approve it (#934). Nothing has been sent and nothing will be until someone
    /// approves it: the scheduler never enqueues this state. Approving moves it to
    /// <see cref="Scheduled"/>; dismissing it, or a newer build replacing it, moves it to
    /// <see cref="Dismissed"/>.
    /// </summary>
    public const string Proposed = "proposed";

    /// <summary>
    /// A prepared release that was set aside before anyone approved it (#934): a person
    /// dismissed it, or a newer build replaced it. Terminal, and distinct from
    /// <see cref="Cancelled"/> because it never was a release - nothing was ever
    /// scheduled or sent. <see cref="OeProjectDelivery.DismissReason"/> says why.
    /// </summary>
    public const string Dismissed = "dismissed";

    /// <summary>Created and due; the worker hasn't claimed it yet. The only cancellable state.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>The worker has taken the row; it's committed to running.</summary>
    public const string Claimed = "claimed";

    /// <summary>Uploading the <c>.app</c> bytes to the environment.</summary>
    public const string Uploading = "uploading";

    /// <summary>The apps are uploaded; BC is installing them (the deployment-status poll).</summary>
    public const string Installing = "installing";

    /// <summary>Every app installed successfully.</summary>
    public const string Deployed = "deployed";

    /// <summary>The delivery failed. <see cref="OeProjectDelivery.FailureMessage"/> says why.</summary>
    public const string Failed = "failed";

    /// <summary>Cancelled before a worker claimed it.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// Business Central accepted the upload and scheduled it for a later window, so
    /// nothing further happens on our side — the install runs (or doesn't) inside BC.
    /// Terminal for that reason, not because we saw it succeed: only an
    /// <see cref="BcDeploymentSchedule.Immediate"/> delivery is watched to
    /// <see cref="Deployed"/>. Cancelling a handed-off install means cancelling it in
    /// Business Central.
    /// </summary>
    public const string HandedOff = "handed_off";

    /// <summary>The states from which no further work happens.</summary>
    public static bool IsTerminal(string status) => status is Deployed or Failed or Cancelled or HandedOff or Dismissed;
}
