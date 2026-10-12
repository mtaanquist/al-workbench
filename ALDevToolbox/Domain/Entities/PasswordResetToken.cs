namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Why a token in <c>password_reset_tokens</c> was issued. Added in P4.19 so
/// the magic-link flow can reuse the existing single-use-hashed-token table
/// instead of growing a parallel one — different lifetime, different consumer,
/// same storage contract.
/// </summary>
public enum TokenPurpose
{
    PasswordReset,
    MagicLogin,
    /// <summary>
    /// One-time 6-digit code emailed during the login MFA challenge. Stored
    /// hash is <c>SHA-256(code + ":" + user_id)</c>; the user-id binding stops
    /// the short numeric space being a global rainbow-table target.
    /// </summary>
    EmailMfaChallenge,
    /// <summary>
    /// Admin-initiated email change. The new address sits on
    /// <see cref="User.PendingEmail"/>; the token confirms ownership of that
    /// mailbox before the swap takes effect. 24-hour lifetime.
    /// </summary>
    EmailChangeConfirm,
    /// <summary>
    /// <see cref="EmailChangeConfirm"/> on a SiteAdmin's account, issued only
    /// when a SiteAdmin asked for it. Confirming an email change on a SiteAdmin
    /// requires this purpose, so a link issued any other way (an org Admin's
    /// request from before that was refused) can't complete the swap. Stored
    /// as its name in the string <c>purpose</c> column, so no schema change.
    /// </summary>
    SiteAdminEmailChangeConfirm,
}

/// <summary>
/// Single-use token issued by <c>/forgot-password</c> or <c>/login/magic</c>.
/// The plain-text token is sent in the email; only its SHA-256 hash is
/// persisted, so a database snapshot does not yield usable tokens.
/// <see cref="ConsumedAt"/> is stamped on first successful use;
/// <see cref="ExpiresAt"/> is one hour after issue for resets and 15 minutes
/// for magic-link logins (see <c>.design/milestones.md</c> P4.19).
/// </summary>
public class PasswordResetToken
{
    public int Id { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Hex-encoded SHA-256 of the token value sent to the user.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>What flow this token belongs to. Defaults to <see cref="TokenPurpose.PasswordReset"/> for legacy rows.</summary>
    public TokenPurpose Purpose { get; set; } = TokenPurpose.PasswordReset;

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }

    /// <summary>
    /// Count of wrong codes submitted against an <see cref="TokenPurpose.EmailMfaChallenge"/>
    /// row. Capped so the 6-digit challenge can't be brute-forced within its
    /// short lifetime; once it hits the cap the row is dead and the user must
    /// request a fresh challenge. Unused for the link-based purposes (the link
    /// token itself is high-entropy). #409
    /// </summary>
    public int FailedAttempts { get; set; }
}
