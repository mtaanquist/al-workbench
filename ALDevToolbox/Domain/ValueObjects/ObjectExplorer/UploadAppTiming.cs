namespace ALDevToolbox.Domain.ValueObjects.ObjectExplorer;

/// <summary>
/// When apps somebody was handed - or an AppSource update (issue #1001) - should install
/// on an environment. All four are bookings the workbench sends itself to run now when
/// the slot arrives - never a schedule handed to Business Central - so several apps go
/// in order, each after the one before it has finished installing, and no two installs
/// overlap. The rows live in <c>oe_environment_upgrade_actions</c> and
/// <c>UpgradeActionWorker</c> sends them. See <c>.design/saas-delivery.md</c>, "Uploading
/// apps" and "Updating an AppSource app".
/// </summary>
public enum UploadAppTiming
{
    /// <summary>A slot that has already come: the worker's next sweep sends it. Anyone working in the environment may be interrupted.</summary>
    Now,

    /// <summary>
    /// At the next opening of Microsoft's update window, from the hours mirrored on the
    /// environment (<c>OeProjectEnvironment.BcUpdateWindowStart/End</c>). Refused until
    /// those have been read from the admin centre.
    /// </summary>
    BcUpdateWindow,

    /// <summary>
    /// At the next opening of the delivery window agreed with the customer
    /// (<c>OeProjectEnvironment.UpdateWindowStart/End</c>). Falls back to
    /// <see cref="BcUpdateWindow"/> when the environment has no delivery window, and
    /// books now when the window is open at the moment of asking.
    /// </summary>
    DeliveryWindow,

    /// <summary>At a wall-clock time the person picked, read in the organisation's display zone.</summary>
    AtTime,
}
