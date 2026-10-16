namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// A request token's <c>jti</c>, kept until the token could no longer be accepted
/// anyway, so a replayed request is refused even across an app restart. The unique
/// index on (gateway, jti) is the check: inserting a seen one fails.
/// </summary>
public class GatewaySeenToken
{
    public long Id { get; set; }

    public int OrganizationId { get; set; }

    public int GatewayId { get; set; }
    public Gateway? Gateway { get; set; }

    public string TokenId { get; set; } = string.Empty;

    /// <summary>The token's expiry plus the clock skew allowance; the row can go after this.</summary>
    public DateTime ExpiresAt { get; set; }
}
