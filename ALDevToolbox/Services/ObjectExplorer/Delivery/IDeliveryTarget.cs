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
/// Admin Center API) and the on-premises gateway's (#1367).
/// <see cref="DeliveryTargetFactory"/> picks one per environment from
/// <see cref="OeProjectEnvironment.Kind"/>. See <c>.design/on-prem-gateway.md</c>,
/// "Deploying to on-premises".
/// </para>
/// <para>
/// One instance serves one environment for one run (or one request), and is not shared.
/// Every method that reaches the environment throws <see cref="BcApiException"/> with a
/// sentence fit for the person when the environment refuses or can't be reached; the
/// shared code decides what that failure means for the deployment.
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
    /// Gets ready to call the environment: for the Admin Center, signing in. Called once
    /// before anything else; its failure is the connection's own sentence (not set up,
    /// secret expired), which is what the person has to fix.
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
    /// Sends one app and starts its install. Returns as soon as the environment has taken
    /// it, with the operation still running; <see cref="WaitForInstallAsync"/> waits for it.
    /// With a deferred <paramref name="schedule"/> (only when
    /// <see cref="DeliveryTargetCapabilities.DeferredSchedules"/>) nothing is left to wait
    /// for: the environment installs it on its own schedule.
    /// </summary>
    Task<BcAppOperation> StartInstallAsync(
        byte[] package, string fileName, string schedule, string schemaSyncMode, CancellationToken ct);

    /// <summary>
    /// Waits for an install <see cref="StartInstallAsync"/> started to finish, checking
    /// every <paramref name="pollDelay"/> for up to <paramref name="timeout"/>. Never
    /// throws for the install's own failure: that is the outcome.
    /// </summary>
    Task<DeliveryInstallOutcome> WaitForInstallAsync(
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout, CancellationToken ct);

    /// <summary>Who is signed in to the environment, for the "are you sure" before an install that runs right away.</summary>
    Task<IReadOnlyList<BcSession>> ListSessionsAsync(CancellationToken ct);

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
public sealed record DeliveryTargetStatus(bool Exists, string? Type, string? Status, string? Refusal)
{
    /// <summary>The environment is no longer there.</summary>
    public static DeliveryTargetStatus Missing(string refusal) => new(false, null, null, refusal);
}

/// <summary>How one install ended, as <see cref="IDeliveryTarget.WaitForInstallAsync"/> reports it.</summary>
/// <param name="Completed">A clean yes: the install was seen to finish.</param>
/// <param name="Failure">Set when the environment reported the install as failed, so the delivery's line is built from its code.</param>
/// <param name="Raw">The environment's text as it came, for the log.</param>
public sealed record DeliveryInstallOutcome(bool Completed, string? Message, BcFailureDetail? Failure = null, string? Raw = null);
