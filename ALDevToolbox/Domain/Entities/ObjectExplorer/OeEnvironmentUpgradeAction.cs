using ALDevToolbox.Services.ObjectExplorer.Delivery;
namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// One thing the upgrade team did — or asked to have done later — to a customer's
/// Business Central environment: move its platform update out to the latest date
/// Microsoft allows, or start that update straight away. These rows <em>are</em> the
/// per-environment activity feed; there is no second table behind it, and no
/// <c>AuditInterceptor</c> entry for them either (this table is itself a log). See
/// <c>.design/saas-delivery.md</c> and issue #657.
///
/// <para>Two shapes share the row. An action the person asked for <em>immediately</em>
/// is performed on the request thread and lands here already
/// <see cref="UpgradeActionStatus.Sent"/> or <see cref="UpgradeActionStatus.Failed"/> —
/// there is nothing to cancel, because it already happened. An action scheduled for an
/// agreed slot ("tonight at 20:00") lands <see cref="UpgradeActionStatus.Pending"/> with
/// <see cref="ExecuteAfter"/> set, and <c>UpgradeActionWorker</c> fires it when it comes
/// due. Cancel works right up until the worker claims the row.</para>
/// </summary>
public class OeEnvironmentUpgradeAction
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The customer. Denormalised from the environment so the worker resolves credentials without a join, and so the feed can name the customer.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>The environment this acts on. The feed is keyed by it.</summary>
    public int EnvironmentId { get; set; }
    public OeProjectEnvironment? Environment { get; set; }

    /// <summary>Which move this is.</summary>
    public UpgradeActionKind Kind { get; set; }

    /// <summary>
    /// The platform version a <see cref="UpgradeActionKind.SelectVersion"/> action set as
    /// the environment's next update (e.g. <c>29.2</c>), or the app version a booked
    /// <see cref="UpgradeActionKind.UpdateApp"/> takes its app to. Null for every other
    /// kind, which act on whatever update is already chosen. Kept on the row because the
    /// history has to say which version was asked for, and a booked row has to carry it to
    /// the worker.
    /// </summary>
    public string? TargetVersion { get; set; }

    /// <summary>
    /// The planned upgrade this action was run from, or null for an ad hoc action from the
    /// fleet table or an environment's page. <c>ON DELETE SET NULL</c>, though an upgrade
    /// that has sent anything cannot be deleted; the null is for the organisation going.
    /// The derived line states read only the actions carrying their upgrade's id. Issue #984.
    /// </summary>
    public int? UpgradeId { get; set; }
    public OeEnvironmentUpgrade? Upgrade { get; set; }

    /// <summary>
    /// The file name of a booked upload (<see cref="UpgradeActionKind.UploadApp"/>), as the
    /// person chose it. Kept after the send so the history can still name the file; null
    /// for every other kind.
    /// </summary>
    public string? PackageFileName { get; set; }

    /// <summary>
    /// The <c>.app</c> bytes of a booked upload, held only until the row settles: Business
    /// Central has no "at this time" schedule, so the workbench keeps the package and the
    /// worker sends it when the slot arrives. Cleared by whichever write settles the row
    /// (sent, failed, skipped or cancelled), and swept by the worker's first pass after a
    /// restart, so a settled row never carries a package. Null for every other kind.
    /// </summary>
    public byte[]? PackageContent { get; set; }

    /// <summary>
    /// The app and operation ids Business Central returned when it accepted the package,
    /// stamped the moment the upload call returns and before the install is polled. They
    /// are what tells a restart that the app is with Business Central (the install went
    /// on without us, unconfirmed) from a row the restart caught before anything was
    /// sent. A booked <see cref="UpgradeActionKind.UpdateApp"/> carries its app id from
    /// the moment it is booked, since that is what it updates, and gets the operation id
    /// the same way an upload does. Null until then and for every other kind.
    /// </summary>
    public Guid? BcAppId { get; set; }
    public Guid? BcOperationId { get; set; }

    /// <summary>
    /// True while an install Business Central accepted still owes the history an answer:
    /// the row is recorded as sent, but a restart or a poll that gave up (a run of API
    /// errors, or the wait running out) means nobody saw it finish. The worker asks
    /// Business Central once more from <see cref="BcAppId"/> and <see cref="BcOperationId"/>,
    /// settles the row from what it hears, and clears this whatever the answer, so a row
    /// is re-checked once and never keeps the worker busy for ever. The rest of the row's
    /// batch waits for that answer. False for every other row.
    /// </summary>
    public bool ConfirmationDue { get; set; }

    /// <summary>
    /// Groups the rows of one multi-app upload: several apps booked together, one row
    /// each, sent one after another in <see cref="BatchOrder"/>. The worker waits for
    /// each install to finish before starting the next, because two installs running at
    /// once can deadlock on Business Central's own bookkeeping table, and a failed one
    /// stops the rest of its batch. Null for every other kind and for a single upload.
    /// </summary>
    public Guid? BatchId { get; set; }

    /// <summary>This row's place in its batch, from zero, in dependency order. Null with <see cref="BatchId"/>.</summary>
    public int? BatchOrder { get; set; }

    /// <summary>
    /// The AppSource app's name as Business Central listed it when a
    /// <see cref="UpgradeActionKind.UpdateApp"/> was booked ("Continia Core"), so the
    /// Scheduled installs list and the history can name it without asking Business
    /// Central again. Null for every other kind, and for the update records written
    /// before updates could be booked.
    /// </summary>
    public string? AppName { get; set; }

    /// <summary>
    /// The apps a booked <see cref="UpgradeActionKind.UpdateApp"/> was agreed to bring
    /// along: the prerequisites Business Central listed at booking time, which the person
    /// was shown before confirming. The worker re-reads the waiting updates before sending
    /// and refuses if Business Central now asks for an app outside this set, so nobody's
    /// agreement covers an app they never saw. Empty for an app that waited for nothing;
    /// null for every other kind.
    /// </summary>
    public List<Guid>? PrerequisiteAppIds { get; set; }

    /// <summary>Where the action has got to. See <see cref="UpgradeActionStatus"/>.</summary>
    public UpgradeActionStatus Status { get; set; } = UpgradeActionStatus.Pending;

    /// <summary>Who asked for it. Nullable (<c>ON DELETE SET NULL</c>) so the feed outlives the account.</summary>
    public int? RequestedByUserId { get; set; }
    public User? RequestedByUser { get; set; }

    /// <summary>
    /// The requester in the audit log's <c>"display name &lt;email&gt;"</c> form, copied
    /// in at request time. Denormalised on purpose: the feed still says who did this
    /// after the account is gone or renamed, which is the whole point of a history.
    /// </summary>
    public string RequestedBy { get; set; } = string.Empty;

    /// <summary>When it was asked for (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>
    /// The UTC instant the action is due to fire — the slot the person picked, converted
    /// from the customer's own time zone. Equal to <see cref="RequestedAt"/> for an
    /// action performed immediately, so the feed can order and read both kinds alike.
    /// </summary>
    public DateTime ExecuteAfter { get; set; }

    /// <summary>When the change actually reached Business Central (or failed trying). Null while pending or cancelled.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// What happened, in the words the feed shows: the success detail, or the plain-words
    /// reason it did not work (a vanished update, a blocked environment, credentials the
    /// customer has since rotated). Never raw exception text.
    /// </summary>
    public string? Outcome { get; set; }

    public int? CancelledByUserId { get; set; }
    public User? CancelledByUser { get; set; }

    /// <summary>Who cancelled it, in the same denormalised form as <see cref="RequestedBy"/>.</summary>
    public string? CancelledBy { get; set; }

    public DateTime? CancelledAt { get; set; }

    // Concurrency: no version column, deliberately. The one race on this table is the
    // worker claiming a row while somebody cancels it, and both sides settle it with the
    // same conditional compare-and-set DeliveryService.RunDeliveryAsync uses — an
    // UPDATE ... WHERE status = 'Pending' AND sent_at IS NULL, where whoever the database
    // reports one affected row to has won. That is one statement, no retry loop, and it
    // beats a token for this shape: with xmin the loser would have to re-read and decide
    // what the new state means, which is exactly the question the WHERE clause answers.
    // Nothing else here is read-modify-write, so nothing else needs guarding.
}

/// <summary>
/// What was done to an environment: the moves the upgrade team makes on its platform
/// update, and the one-off writes recorded beside them. Stored as text
/// (<c>HasConversion&lt;string&gt;()</c>) like <see cref="ProjectVisibility"/>, so the
/// column reads plainly and a third kind never renumbers the existing rows.
/// </summary>
public enum UpgradeActionKind
{
    /// <summary>Move the update's date out to the latest Business Central still allows.</summary>
    PushDateToLatest,

    /// <summary>Start the update as soon as Business Central will take it, ignoring the environment's update window.</summary>
    RunNow,

    /// <summary>
    /// An AppSource app updated to the version Business Central has waiting for it,
    /// booked from the environment's page like an upload: a <c>Pending</c> row carrying
    /// the app (<see cref="OeEnvironmentUpgradeAction.BcAppId"/>,
    /// <see cref="OeEnvironmentUpgradeAction.AppName"/>), the version
    /// (<see cref="OeEnvironmentUpgradeAction.TargetVersion"/>) and the prerequisites the
    /// person agreed to (<see cref="OeEnvironmentUpgradeAction.PrerequisiteAppIds"/>),
    /// which the worker sends when it comes due and waits on to the end, taking its turn
    /// with the uploads. Rows written before updates could be booked (issue #1001) are
    /// records only, already <c>Sent</c>, with what moved in <c>Outcome</c>.
    /// </summary>
    UpdateApp,

    /// <summary>
    /// An extension package somebody was handed, booked to be installed on the
    /// environment: always a <c>Pending</c> row carrying the package
    /// (<see cref="OeEnvironmentUpgradeAction.PackageContent"/>) that the worker sends
    /// when it comes due - "now" being a slot that has already come - and waits on to
    /// the end. Several apps booked together share a <see cref="OeEnvironmentUpgradeAction.BatchId"/>.
    /// </summary>
    UploadApp,

    /// <summary>
    /// A deleted environment was asked to come back, while Business Central was still
    /// keeping it. Recorded like <see cref="UpdateApp"/> — never booked, written already
    /// <c>Sent</c>. Worth a line of its own because it is the one action that undoes
    /// somebody else's deletion, and the history is where that is answered for.
    /// </summary>
    RecoverEnvironment,

    /// <summary>
    /// A copy of the environment was asked for, under a new name. Recorded like
    /// <see cref="UpdateApp"/> — never booked, written already <c>Sent</c>, and on the
    /// <em>source</em> environment, which is the one that existed when it was asked for
    /// and the one somebody later asks where the sandbox came from.
    /// </summary>
    CopyEnvironment,

    /// <summary>
    /// A session signed in to the environment was ended. Recorded like
    /// <see cref="UpdateApp"/> — never booked, written already <c>Sent</c>. The session
    /// list itself is never stored (see <c>.design/environment-updates.md</c>,
    /// "Sessions"), so this line is the only lasting record that somebody was signed out,
    /// and its <see cref="OeEnvironmentUpgradeAction.Outcome"/> names who and what they
    /// were running.
    /// </summary>
    CancelSession,

    /// <summary>
    /// The environment's next platform update was set to a chosen version
    /// (<see cref="OeEnvironmentUpgradeAction.TargetVersion"/>), from the Upgrades page's
    /// fleet action. Business Central keeps or assigns the date inside that version's
    /// rollout; nothing about the date is sent. Issue #960.
    /// </summary>
    SelectVersion,
}

/// <summary>
/// Where an upgrade action has got to: <c>Pending → Sent | Failed | Cancelled</c>. An
/// immediate action skips <c>Pending</c> and is written in its terminal state. Stored as
/// text, like <see cref="UpgradeActionKind"/>.
/// </summary>
public enum UpgradeActionStatus
{
    /// <summary>Scheduled for a future slot and not yet claimed by the worker. The only cancellable state.</summary>
    Pending,

    /// <summary>The change reached Business Central.</summary>
    Sent,

    /// <summary>Business Central refused it, or it could no longer be done. <see cref="OeEnvironmentUpgradeAction.Outcome"/> says why.</summary>
    Failed,

    /// <summary>Cancelled before it fired.</summary>
    Cancelled,
}
