namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A one-time code an Admin hands to whoever installs a gateway. Shown once,
/// valid for an hour, used once, and stored only as a hash. See
/// <c>.design/on-prem-gateway.md</c>, "Registration and authentication".
/// </summary>
public class GatewayRegistrationCode
{
    public int Id { get; set; }

    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    /// <summary>Hex SHA-256 of the normalised code. The code itself is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public int? CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Stamped when the code is exchanged. Also the row's concurrency token, so two
    /// exchanges racing for one code cannot both succeed: the second save finds
    /// the column no longer null and fails.
    /// </summary>
    public DateTime? UsedAt { get; set; }

    /// <summary>
    /// Set for a rotation code: exchanging it replaces this gateway's key rather
    /// than registering a new gateway.
    /// </summary>
    public int? GatewayId { get; set; }
    public Gateway? Gateway { get; set; }
}
