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

    /// <summary>
    /// Whether the gateway was online the last time the command sweep looked, so each
    /// change is announced to the organisation's Admins once: true once it has been
    /// heard from, false after it went quiet for three heartbeats. Null until it first
    /// connects - a gateway still being set up is not "offline". Written by the sweep
    /// with a compare-and-set, never by a person, so it is not audited.
    /// </summary>
    public bool? NotifiedOnline { get; set; }

    /// <summary>
    /// What the gateway's local policy file allows, as the gateway last reported it
    /// (<c>GatewayPolicyReport</c> from the contracts, as JSON). Customer IT owns that
    /// file; this copy is for display only, and the gateway enforces the file whatever
    /// this says. Null until a gateway that reports it has sent a heartbeat. Written
    /// with the heartbeat's <c>ExecuteUpdateAsync</c>, so not audited.
    /// </summary>
    public string? EffectivePolicyJson { get; set; }

    /// <summary>When <see cref="EffectivePolicyJson"/> was last received.</summary>
    public DateTime? PolicyReportedAt { get; set; }

    /// <summary>
    /// True when the gateway's latest report was too large to store or to send, so
    /// <see cref="EffectivePolicyJson"/> is an older one and must not be shown as what
    /// the gateway enforces now. Cleared by the next full report.
    /// </summary>
    public bool PolicyStale { get; set; }

    /// <summary>Stamped on revoke. A revoked gateway's next request is refused; there is no un-revoke.</summary>
    public DateTime? RevokedAt { get; set; }
}
