using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// "Install these apps on this environment": everything a deployment run asks of the
/// environment it installs to, behind one interface so the orchestration in
/// <see cref="DeliveryService"/> - claiming, the per-app result rows, dependency order,
/// skipping what is already installed, stopping at the first failure, the delivery
/// window, prepared deployments and approvals, one deployment per environment at a time -
/// stays one flow whatever is on the other end.
/// <para>
/// Two implementations clear CLAUDE.md's interface bar, as <c>IOffsiteStorageProvider</c>
/// did: <see cref="AdminCenterDeliveryTarget"/> (Business Central online, through the
/// Admin Center API) and <see cref="GatewayDeliveryTarget"/> (a linked on-premises
/// service tier, through its gateway, #1367). <see cref="DeliveryTargetFactory"/> picks one
/// per environment from <see cref="OeProjectEnvironment.Kind"/>. See
/// <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// </para>
/// <para>
/// One instance serves one environment for one run (or one request), and is not shared.
/// Every method that reaches the environment throws <see cref="DeliveryTargetException"/>
/// with a sentence fit for the person when the environment refuses or can't be reached;
/// the shared code decides what that failure means for the deployment. The Admin Center's
/// <see cref="BcApiException"/> is one; the gateway's target throws the base type.
/// </para>
/// <para>
/// The app and operation shapes that cross it (<see cref="BcInstalledApp"/>,
/// <see cref="BcAppOperation"/>) are the Admin Center's, kept rather than replaced by
/// neutral records: the gateway's target fills the fields it has and leaves the rest
/// empty, and the shared code reads only the id, name and version of an installed app and
/// the id and read-back version of an operation. A third kind of target is the time to
/// revisit that (recorded in <c>.design/on-prem-gateway.md</c>).
/// </para>
/// </summary>
public interface IDeliveryTarget
{
    /// <summary>
    /// What this kind of environment can do, so the shared code asks rather than guesses.
    /// Fixed for the implementation: it never depends on a call having been made.
    /// </summary>
    DeliveryTargetCapabilities Capabilities { get; }

    /// <summary>
    /// Gets ready to call the environment: for the Admin Center, signing in; for the
    /// gateway, checking the gateway is approved and online. Called once before anything
    /// else; its failure is the connection's own sentence (not set up, secret expired, the
    /// gateway was offline), which is what the person has to fix.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>
    /// Called before each app after the first. An install can take minutes, so whatever
    /// <see cref="ConnectAsync"/> obtained may be close to running out (#1113).
    /// </summary>
    Task RenewAsync(CancellationToken ct);

    /// <summary>
    /// The environment as it is right now, re-read before the first byte is sent: hours
    /// may have passed since the deployment was scheduled. Throws when it can't be read;
    /// a failed read is not evidence the environment is unhealthy.
    /// </summary>
    Task<DeliveryTargetStatus> ReadStatusAsync(CancellationToken ct);

    /// <summary>
    /// The apps installed on the environment now, read once per run. The shared code
    /// matches them against the build to skip what is already there (#931), refuse a
    /// downgrade, and record what each app is moving from.
    /// </summary>
    Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(CancellationToken ct);

    /// <summary>
    /// Told once, before the first <see cref="StartInstallAsync"/>, every app the run is
    /// going to send, in installation order, with what applies to all of them. Not called
    /// when nothing is left to send.
    /// <para>
    /// Null means "go on": the run sends each app with <see cref="StartInstallAsync"/> and
    /// waits for it, which is what the Admin Center does. A <see cref="DeliveryHandOff"/>
    /// means the target has taken the whole batch and finishes it later, on its own time:
    /// the gateway installs the build as one command, which can run for hours (#1367). The
    /// run then records the hand-off and ends, and <c>DeliveryService.FollowGatewayDeliveriesAsync</c>
    /// finishes the deployment when the command does, so no worker lane waits on it.
    /// </para>
    /// <para>A target may refuse the batch with <see cref="DeliveryTargetRefusedException"/>,
    /// which the run records as the first app's refusal rather than as a failure.</para>
    /// </summary>
    Task<DeliveryHandOff?> PrepareInstallsAsync(DeliveryInstallBatch batch, CancellationToken ct);

    /// <summary>
    /// Sends one app and starts its install. Returns as soon as the environment has taken
    /// it, with the operation still running; <see cref="WaitForInstallAsync"/> waits for it.
    /// With a deferred <paramref name="schedule"/> (only when
    /// <see cref="DeliveryTargetCapabilities.DeferredSchedules"/>) nothing is left to wait
    /// for: the environment installs it on its own schedule. The package is one of the
    /// batch given to <see cref="PrepareInstallsAsync"/>; a target that already has the
    /// file never reads its content.
    /// </summary>
    Task<BcAppOperation> StartInstallAsync(
        DeliveryPackage package, string schedule, string schemaSyncMode, CancellationToken ct);

    /// <summary>
    /// Waits for an install <see cref="StartInstallAsync"/> started to finish, checking
    /// every <paramref name="pollDelay"/> for up to <paramref name="timeout"/>. Never
    /// throws for the install's own failure: that is the outcome. Lines the environment
    /// reports while it works go to <paramref name="onProgress"/>, for the deployment's log.
    /// The gateway's target waits for its command rather than the timeout: a data upgrade
    /// may take hours, and the command queue fails a command whose gateway goes quiet.
    /// </summary>
    Task<DeliveryInstallOutcome> WaitForInstallAsync(
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout,
        Func<IReadOnlyList<string>, CancellationToken, Task>? onProgress, CancellationToken ct);

    /// <summary>
    /// Who is signed in, for the "are you sure" before an install that runs right away.
    /// Answered as an <see cref="OpenSessionsCheck"/> rather than a list of sessions,
    /// because the two kinds know different things: the Admin Center lists each session
    /// with its user, the gateway's inventory counts sessions per service tier (#1367).
    /// </summary>
    Task<OpenSessionsCheck> CheckOpenSessionsAsync(CancellationToken ct);

    /// <summary>
    /// The installs the environment is holding for a later update. Only on a target with
    /// <see cref="DeliveryTargetCapabilities.DeferredSchedules"/>.
    /// </summary>
    Task<IReadOnlyList<BcScheduledPteOperation>> ListHeldInstallsAsync(CancellationToken ct);

    /// <summary>
    /// Cancels one install the environment is holding for <paramref name="schedule"/>. Only
    /// on a target with <see cref="DeliveryTargetCapabilities.DeferredSchedules"/>.
    /// </summary>
    Task CancelHeldInstallAsync(Guid appId, string version, string schedule, CancellationToken ct);
}

/// <summary>
/// The exception the delivery seam promises: the environment refused, or could not be
/// reached, and the message is a sentence for the person. The Admin Center's
/// <see cref="BcApiException"/> derives from it; the gateway's target throws it as it is.
/// </summary>
public class DeliveryTargetException : Exception
{
    public DeliveryTargetException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The target's own rules refused the batch before anything was sent, for a reason the
/// person fixes outside the environment (the customer's gateway settings). Recorded as the
/// first app's refusal, not as a failure of the environment.
/// </summary>
public sealed class DeliveryTargetRefusedException : DeliveryTargetException
{
    public DeliveryTargetRefusedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// A batch the target took whole and finishes later (#1367): the gateway command it was
/// queued as. The run stores it on the deployment and ends; the deployment is finished
/// from the command's result.
/// </summary>
public sealed record DeliveryHandOff(long GatewayCommandId);

/// <summary>
/// One app of a deployment, as the target is handed it: which build artifact it is, what
/// the build says it is, and how to read its content when the target needs the bytes.
/// </summary>
/// <param name="ArtifactId">The build artifact (<c>oe_project_build_artifacts.id</c>).</param>
/// <param name="AppId">The app id the build stamped on the artifact; null for an artifact written before ids were.</param>
/// <param name="Size">The artifact's size in bytes, for the log.</param>
/// <param name="ReadContentAsync">Loads the <c>.app</c> itself, for a target that sends the bytes.</param>
public sealed record DeliveryPackage(
    int ArtifactId,
    string FileName,
    Guid? AppId,
    string AppName,
    string AppVersion,
    long Size,
    Func<CancellationToken, Task<byte[]>> ReadContentAsync);

/// <summary>Everything a run will send, for <see cref="IDeliveryTarget.PrepareInstallsAsync"/>.</summary>
/// <param name="Packages">The apps to send, in installation order (dependencies first).</param>
/// <param name="SchemaSyncMode">The deployment's sync mode, one of <c>BcSyncMode</c>.</param>
/// <param name="UnpublishPrevious">On-premises: remove each app's previous version once it is replaced.</param>
/// <param name="RequestedByUserId">The person the deployment runs as, recorded on what is queued.</param>
/// <param name="StartedByAgent">An AI assistant started the deployment.</param>
/// <param name="WindowEndsAtUtc">The end of the delivery window the deployment runs in, when it runs in one: nothing may start after it.</param>
public sealed record DeliveryInstallBatch(
    IReadOnlyList<DeliveryPackage> Packages,
    string SchemaSyncMode,
    bool UnpublishPrevious,
    int? RequestedByUserId,
    bool StartedByAgent,
    DateTime? WindowEndsAtUtc = null);

/// <summary>
/// What a kind of delivery target can do.
/// </summary>
/// <param name="DeferredSchedules">
/// The target can hold an uploaded app for a later platform update (Business Central's
/// "next minor/major update" and its own update window). Where it can, a deployment may
/// end <see cref="ProjectDeliveryStatus.HandedOff"/>,
/// its held installs can be listed and cancelled, and a move of a held deployment is
/// possible. On-premises has none of this: only "now" and the delivery window.
/// </param>
public sealed record DeliveryTargetCapabilities(bool DeferredSchedules);

/// <summary>
/// The environment a target is for: the delivery's snapshot name within its solution,
/// and the row that name resolves to when there still is one.
/// </summary>
/// <param name="EnvironmentId">The environment row, when one still holds the name.</param>
/// <param name="ApplicationFamily">The family as the Admin Center last reported it, verbatim; null when unknown.</param>
public sealed record DeliveryTargetEnvironment(
    int ProjectId, string Name, OeEnvironmentKind Kind, int? EnvironmentId = null, string? ApplicationFamily = null);

/// <summary>
/// The environment as <see cref="IDeliveryTarget.ReadStatusAsync"/> found it.
/// </summary>
/// <param name="Exists">False when the environment is gone; <paramref name="Refusal"/> then says so.</param>
/// <param name="Type">Sandbox or Production, as the environment reports it now.</param>
/// <param name="Status">The status to record on the environment row, as the environment reports it.</param>
/// <param name="Refusal">Why it can't take an install now, in the target's own words; null when it can.</param>
/// <param name="ReadLive">
/// True when the status was read from the environment just now, so it is news worth
/// writing back to the environment row. The gateway's target answers from the inventory
/// the row was itself written from, so it says false and nothing is written back.
/// </param>
public sealed record DeliveryTargetStatus(bool Exists, string? Type, string? Status, string? Refusal, bool ReadLive = true)
{
    /// <summary>The environment is no longer there.</summary>
    public static DeliveryTargetStatus Missing(string refusal) => new(false, null, null, refusal);
}

/// <summary>How one install ended, as <see cref="IDeliveryTarget.WaitForInstallAsync"/> reports it.</summary>
/// <param name="Completed">A clean yes: the install was seen to finish.</param>
/// <param name="Failure">Set when the environment reported the install as failed, so the delivery's line is built from its code.</param>
/// <param name="Raw">The environment's text as it came, for the log.</param>
public sealed record DeliveryInstallOutcome(bool Completed, string? Message, BcFailureDetail? Failure = null, string? Raw = null)
{
    /// <summary>
    /// The finer answer, for a target that knows more than yes or no. The Admin Center
    /// only ever says installed or failed; the gateway also says an app was already on
    /// this version, was refused before anything ran, or was not started.
    /// </summary>
    public DeliveryAppOutcome Outcome { get; init; } = Completed ? DeliveryAppOutcome.Installed : DeliveryAppOutcome.Failed;

    /// <summary>The version the app was upgraded from, when the target reports one.</summary>
    public string? FromVersion { get; init; }

    /// <summary>Whether the target restarted its service tiers after the apps; null when it has none to restart.</summary>
    public bool? TiersRestarted { get; init; }
}

/// <summary>How one app of a deployment ended, in the target's terms.</summary>
public enum DeliveryAppOutcome
{
    /// <summary>Installed or upgraded.</summary>
    Installed,
    /// <summary>A step failed.</summary>
    Failed,
    /// <summary>Refused before anything ran for it: not the app declared, or not allowed by the customer's settings.</summary>
    Refused,
    /// <summary>This version was already installed; nothing was done (#931).</summary>
    AlreadyInstalled,
    /// <summary>Not started, because another app of the same command was refused or failed.</summary>
    Skipped,
    /// <summary>The target lost sight of it after it may have started: whether it went in is not known.</summary>
    Unconfirmed,
}
