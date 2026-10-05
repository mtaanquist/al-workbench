namespace ALDevToolbox.Domain.Entities;

/// <summary>
/// Which flow an email came from. Every <c>IEmailService.SendAsync</c> call
/// names one, for two reasons: it is what an operator reads in the SiteAdmin
/// list when a send fails ("a reset link is stuck" beats "an email is stuck"),
/// and it is what decides whether the message goes through the outbox at all
/// (see <see cref="Services.EmailPurposes.SendsInline"/>).
/// </summary>
public enum EmailPurpose
{
    /// <summary>Verify-your-address link and code, step 1 of the email-first signup.</summary>
    SignupVerification,

    /// <summary>"Someone wants to join" notice to an organisation's admins.</summary>
    SignupPendingNotice,

    /// <summary>"You're in" / "declined" notice to the person who signed up.</summary>
    SignupDecision,

    /// <summary>Password reset link.</summary>
    PasswordReset,

    /// <summary>Single-use sign-in link.</summary>
    MagicLink,

    /// <summary>Admin-issued invite link.</summary>
    Invite,

    /// <summary>Confirm-your-new-address link, sent to the new address.</summary>
    EmailChangeConfirmation,

    /// <summary>Six-digit two-factor code. Sent inline — someone is waiting on it.</summary>
    MfaCode,

    /// <summary>The "does SMTP work" probe on the SiteAdmin email settings. Sent inline.</summary>
    SiteAdminTest,

    /// <summary>A build notification: a build pipeline failed or recovered. See <c>.design/notifications.md</c>.</summary>
    BuildNotification,

    /// <summary>A deployment notification: waiting for approval, deployed or failed.</summary>
    DeploymentNotification,

    /// <summary>A daily or weekly digest of notifications. See <c>.design/notifications.md</c>.</summary>
    NotificationDigest,

    /// <summary>An upgrade notification: a scheduled change on an environment ran, failed, or could not be confirmed.</summary>
    UpgradeNotification,
    /// <summary>A solution notification: an update was scheduled or moved on a solution the person follows.</summary>
    SolutionNotification,
}

/// <summary>Where an outbox row is in its life.</summary>
public enum EmailOutboxStatus
{
    /// <summary>Queued, and due for a send attempt at <see cref="EmailOutboxMessage.NextAttemptAt"/>.</summary>
    Pending,

    /// <summary>Handed to the SMTP server. Not the same as "the recipient got it" — see issue #790.</summary>
    Sent,

    /// <summary>Given up on after <see cref="Services.EmailOutbox.MaxAttempts"/> tries. Needs an operator.</summary>
    Failed,
}

/// <summary>
/// One transactional email waiting to be sent, or the record of one that could
/// not be. Exists so a failed send is visible and retried rather than
/// disappearing into a log warning: the three enumeration-resistant flows
/// (signup verification, forgot password, magic link) must return the same
/// response whether the send worked or not, which used to leave an operator
/// with no way to find out that SMTP had stopped working either. See issue #790.
///
/// <para>
/// <b>No tenant query filter.</b> The row is written from pre-auth flows that
/// have no organisation in scope — a signup verification exists before the
/// account does — and is read by a SiteAdmin console that is cross-org by
/// definition. So it sits outside the multi-tenant filter, the same posture as
/// <see cref="LoginAttempt"/> and <see cref="PendingSignup"/>, and with no
/// filter on the table its reads must not carry an <c>IgnoreQueryFilters()</c>
/// bypass. <see cref="OrganizationId"/> is recorded when the sending request
/// had one, but it is a label for the operator's list, never a fence:
/// nothing scopes a read by it. Do not "fix" this into a filtered entity
/// without re-reading how the drain worker and the pre-auth senders use it.
/// </para>
///
/// <para>
/// <b>Bodies hold live credentials.</b> A queued reset, invite or magic-link
/// body carries a working token — unlike <c>password_reset_tokens</c>, which
/// stores only a hash, so a read of that table yields nothing usable. That is
/// why <see cref="BodyEncrypted"/> is Data-Protection ciphertext (same key ring
/// as the SMTP password), why sent rows are pruned within a day, and why the
/// body is dropped the moment a row is sent or given up on. Only rows still
/// trying to deliver hold a secret, and <see cref="Services.EmailOutbox.PendingRetention"/>
/// bounds how long that lasts - including for a message the drain never reached,
/// which is written off rather than delivered days late carrying a link that
/// expired. The one case nothing bounds is the drain not running at all
/// (<c>DISABLE_EMAIL_OUTBOX_SCHEDULER=1</c>, or a long outage): queued bodies
/// then wait until it runs again.
/// </para>
/// </summary>
public class EmailOutboxMessage
{
    public int Id { get; set; }

    /// <summary>Recipient address, exactly as the sending flow resolved it.</summary>
    public string ToEmail { get; set; } = string.Empty;

    /// <summary>Which flow queued this, so the operator's list says more than "an email failed".</summary>
    public EmailPurpose Purpose { get; set; }

    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Data-Protection ciphertext of the HTML body. Null once the row is
    /// <see cref="EmailOutboxStatus.Failed"/>: the token inside has expired by
    /// then, so keeping it is exposure without value.
    /// </summary>
    public string? BodyEncrypted { get; set; }

    /// <summary>
    /// Data-Protection ciphertext of the plain-text alternative, under the same
    /// purpose as <see cref="BodyEncrypted"/> and dropped at the same moments,
    /// since it carries the same links. Null for an email built without one.
    /// </summary>
    public string? TextBodyEncrypted { get; set; }

    public EmailOutboxStatus Status { get; set; } = EmailOutboxStatus.Pending;

    /// <summary>Send attempts made so far. Drives the backoff and the give-up point.</summary>
    public int AttemptCount { get; set; }

    /// <summary>Message from the most recent failure. What the SiteAdmin list shows.</summary>
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>When the drain may next try this row. Set ahead on each failure.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Stamped when SMTP accepted the message.</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>
    /// The organisation the sending request acted for, when it had one. Null for
    /// the pre-auth flows. Informational only — see the type remarks.
    /// </summary>
    public int? OrganizationId { get; set; }
}
