namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// When an app somebody was handed should install on an environment. Two of these are
/// Business Central's own schedules (<see cref="Now"/> and <see cref="BcUpdateWindow"/>,
/// sent as <see cref="BcDeploymentSchedule.Immediate"/> and
/// <see cref="BcDeploymentSchedule.UpdateWindow"/>); the other two are ours. Business
/// Central cannot be told "at 20:00 on Thursday", so <see cref="DeliveryWindow"/> and
/// <see cref="AtTime"/> keep the package in <c>oe_environment_upgrade_actions</c> and
/// <c>UpgradeActionWorker</c> sends it as Immediate when the slot arrives. See
/// <c>.design/saas-delivery.md</c>, "Uploading an app".
/// </summary>
public enum UploadAppTiming
{
    /// <summary>Straight away; anyone working in the environment may be interrupted.</summary>
    Now,

    /// <summary>Handed to Business Central to install in Microsoft's update window, at a moment it picks.</summary>
    BcUpdateWindow,

    /// <summary>
    /// At the next opening of the delivery window agreed with the customer
    /// (<c>OeProjectEnvironment.UpdateWindowStart/End</c>). Falls back to
    /// <see cref="BcUpdateWindow"/> when the environment has no delivery window, and sends
    /// right away when the window is open at the moment of asking.
    /// </summary>
    DeliveryWindow,

    /// <summary>At a wall-clock time the person picked, read in the customer's time zone.</summary>
    AtTime,
}
