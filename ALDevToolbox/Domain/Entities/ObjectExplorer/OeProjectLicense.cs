namespace ALDevToolbox.Domain.Entities.ObjectExplorer;

/// <summary>
/// A licence file someone uploaded to a Solution to import on one of its on-premises
/// environments (#1369): the file as uploaded, who sent it and when, and the gateway
/// command that imports it. One row per upload, written once in the same transaction that
/// queues the command and never changed afterwards, so its audit row is the upload itself.
/// What the service tier actually runs is the mirror's licence
/// (<see cref="GatewayInstanceLicence"/>), read again by the gateway after the import.
/// Follows the Solution's visibility; only people who can manage the Solution upload or
/// download it. Org-scoped. See <c>.design/on-prem-gateway.md</c>, "Licences".
/// </summary>
public class OeProjectLicense
{
    public int Id { get; set; }

    /// <summary>Owning organisation. EF query filter scopes reads to it.</summary>
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>The Solution the licence belongs to. Rides its lifecycle.</summary>
    public int ProjectId { get; set; }
    public OeProject? Project { get; set; }

    /// <summary>
    /// The on-premises environment it was uploaded for. Nullable (<c>ON DELETE SET NULL</c>)
    /// so the upload outlives an environment row, which is not normally deleted anyway.
    /// </summary>
    public int? ProjectEnvironmentId { get; set; }
    public OeProjectEnvironment? ProjectEnvironment { get; set; }

    /// <summary>The file's name as uploaded, e.g. <c>CRONUS.bclicense</c>.</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>The file, at most <c>GatewayLicenceFormats.MaxBytes</c>.</summary>
    public byte[] Content { get; set; } = [];

    public int Size { get; set; }

    /// <summary>SHA-256 of <see cref="Content"/>, lower-case hex: what the gateway checks the download against.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>
    /// Where it was imported: the database's <c>licence_target</c> as the person confirmed
    /// it (<c>NavDatabase</c>, <c>Master</c>, <c>Default</c> or <c>Tenant</c>).
    /// </summary>
    public string LicenceTarget { get; set; } = string.Empty;

    /// <summary>
    /// The tenant it was imported into when <see cref="LicenceTarget"/> is <c>Tenant</c>;
    /// null for the server-wide targets. Kept as sent, so the page names the tenant the
    /// licence went to even if the environment is later linked to another one.
    /// </summary>
    public string? Tenant { get; set; }

    /// <summary>Who uploaded it. Nullable (<c>ON DELETE SET NULL</c>) so the upload outlives the account.</summary>
    public int? UploadedByUserId { get; set; }
    public User? UploadedByUser { get; set; }

    public DateTime UploadedAt { get; set; }

    /// <summary>
    /// The <c>import-licence</c> command that imports it. A plain id, as on a deployment: the
    /// command is history, and a command row is never removed on its own.
    /// </summary>
    public long? GatewayCommandId { get; set; }
}
