namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// The outcome of publishing one <c>.app</c> within a <see cref="OeProjectDelivery"/> —
/// the per-app analogue of <see cref="OeProjectBuildResult"/>. Records the app's
/// identity, the App Management operation id the install created for it, the
/// deployment result, and a short secret-free message. Org-scoped (denormalised from the parent
/// delivery). See <c>.design/saas-delivery.md</c> ("Delivery").
/// </summary>
public class OeProjectDeliveryResult
{
    public int Id { get; set; }

    /// <summary>Owning organisation (denormalised from the delivery). EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public int ProjectDeliveryId { get; set; }
    public OeProjectDelivery? ProjectDelivery { get; set; }

    /// <summary>Publish order (dependency order), 0-based — so the history reads in the order the apps were sent.</summary>
    public int Ordering { get; set; }

    /// <summary>
    /// The extension's app.json <c>id</c> (GUID string). Business Central reads it out
    /// of the uploaded package and returns it on the install operation, so it's known
    /// from the upload onwards — which is what lets the poll ask about <em>this</em>
    /// app rather than matching on a name two extensions might share. Null before the
    /// upload / on an early failure.
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>The extension's display name (app.json <c>name</c>).</summary>
    public string AppName { get; set; } = string.Empty;

    /// <summary>The published version (app.json <c>version</c>).</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>
    /// The version of this app the environment had installed when the run started, read
    /// from the installed-apps list the run takes before its first upload (matched on the
    /// app id, falling back to the name). Null when the app was not installed yet, when
    /// the run never got as far as that read, and for rows written before it was recorded
    /// (#929) - a page shows nothing rather than guessing.
    /// </summary>
    public string? PreviousVersion { get; set; }

    /// <summary>
    /// The App Management operation Business Central created for this app's install —
    /// what the run polls, and what identifies the install in the admin center
    /// afterwards. Null before the upload / on an early failure.
    /// </summary>
    public Guid? OperationId { get; set; }

    /// <summary>Per-app lifecycle. See <see cref="ProjectDeliveryResultStatus"/>.</summary>
    public string Status { get; set; } = ProjectDeliveryResultStatus.Pending;

    /// <summary>A short, secret-free message — the BC deployment status detail, or the failure reason.</summary>
    public string? Message { get; set; }

    /// <summary>When this app's upload began. Null for an app that was never attempted (pending, skipped) and for rows written before it was recorded.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When this app reached its outcome (completed, failed, or handed to Business Central's schedule). Null while it is still going and for rows written before it was recorded.</summary>
    public DateTime? FinishedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>The per-app states within a delivery.</summary>
public static class ProjectDeliveryResultStatus
{
    /// <summary>Queued behind earlier apps; not started.</summary>
    public const string Pending = "pending";

    /// <summary>The <c>.app</c> bytes are being uploaded.</summary>
    public const string Uploading = "uploading";

    /// <summary>Uploaded; BC is installing it.</summary>
    public const string Installing = "installing";

    /// <summary>Uploaded and accepted, but BC will install it in a later window rather than now.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Installed successfully.</summary>
    public const string Completed = "completed";

    /// <summary>This app failed to upload or install.</summary>
    public const string Failed = "failed";

    /// <summary>Not attempted because an earlier app in the run failed.</summary>
    public const string Skipped = "skipped";

    /// <summary>
    /// Was uploading or installing when the run stopped (a shutdown or restart), so whether
    /// Business Central installed it is not known (#1115).
    /// </summary>
    public const string Unconfirmed = "unconfirmed";
}
