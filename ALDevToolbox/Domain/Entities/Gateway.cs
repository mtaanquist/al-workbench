namespace ALDevToolbox.Domain.Entities;

/// <summary>Whether an Admin has let a gateway in yet.</summary>
public enum GatewayState
{
    /// <summary>
    /// Registered, or registered again with a new key, and waiting for an Admin to
    /// compare its key fingerprint and approve it. It may heartbeat; it receives no
    /// commands and no files.
    /// </summary>
    Pending,

    /// <summary>An Admin compared the fingerprint and approved this key.</summary>
    Approved,
}

/// <summary>
/// An on-premises gateway: the Windows service in a customer's network that lets
/// AL Workbench manage Business Central service tiers there. Org-scoped like every
/// other editable table.
///
/// <para>The credential is the public half of a key pair the gateway generated
/// itself. Nothing in this row can be used to act as the gateway: each request
/// carries a short-lived token the gateway signs, which is checked against
/// <see cref="PublicKey"/>. See <c>.design/on-prem-gateway.md</c>,
/// "Registration and authentication".</para>
/// </summary>
public class Gateway
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>What the Gateways page calls it. Starts as the machine name; an Admin may rename it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The gateway's ECDSA P-256 public key, as DER <c>SubjectPublicKeyInfo</c>.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>
    /// <c>GatewayKeyFingerprint</c> of <see cref="PublicKey"/>, stored so the list
    /// does not recompute it. The gateway prints the same value when it registers.
    /// </summary>
    public string KeyFingerprint { get; set; } = string.Empty;

    public GatewayState State { get; set; } = GatewayState.Pending;

    /// <summary>When the gateway first registered. A key rotation does not move it.</summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>When the current key was registered: the first registration, or the latest rotation.</summary>
    public DateTime KeyRegisteredAt { get; set; }

    /// <summary>The Admin who approved the current key. Cleared when the key is replaced.</summary>
    public int? ApprovedByUserId { get; set; }
    public User? ApprovedByUser { get; set; }

    /// <summary>When the current key was approved. Cleared when the key is replaced.</summary>
    public DateTime? ApprovedAt { get; set; }

    /// <summary>
    /// The last heartbeat. Written with <c>ExecuteUpdateAsync</c>, so a heartbeat
    /// a minute does not put a row a minute into the audit log.
    /// </summary>
    public DateTime? LastSeenAt { get; set; }

    /// <summary>The gateway program's version, as it last reported it.</summary>
    public string? ReportedVersion { get; set; }

    /// <summary>The Windows computer name the gateway runs on, as it last reported it.</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Stamped on revoke. A revoked gateway's next request is refused; there is no un-revoke.</summary>
    public DateTime? RevokedAt { get; set; }
}
