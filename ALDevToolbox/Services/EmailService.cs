using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Operations;

namespace ALDevToolbox.Services;

/// <summary>Outbound email primitive used for password reset and signup workflows.</summary>
public interface IEmailService
{
    /// <summary>True when SMTP can be resolved (either via system_settings or env vars). Pages can use this to render a "ask an admin" hint.</summary>
    Task<bool> IsConfiguredAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends an email, or queues it to be sent. Which one depends on
    /// <paramref name="purpose"/>: the implementation the app injects is
    /// <see cref="OutboxEmailService"/>, which hands a couple of purposes
    /// straight to SMTP and writes the rest to <c>email_outbox</c> for
    /// <see cref="EmailOutboxScheduler"/> to deliver and retry (issue #790).
    ///
    /// <para>
    /// Throws when SMTP is not configured, and when the send (or the queue
    /// write) fails. Every caller catches and logs rather than rethrowing, so a
    /// throw does not by itself reach the user - see the remarks on
    /// <see cref="SmtpEmailService"/> for what each flow shows.
    /// </para>
    /// </summary>
    /// <param name="purpose">
    /// The flow this message belongs to. Required, not inferred: it decides
    /// whether the message is queued, and it is what an operator reads when a
    /// send fails.
    /// </param>
    Task SendAsync(
        string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default);
}

/// <summary>
/// The subject-and-HTML form of <see cref="IEmailService.SendAsync"/>, for the
/// emails still built by <see cref="EmailTemplates"/>. They have no plain-text
/// part. Goes away once every email is rendered through the shared layout
/// (issue #1029).
/// </summary>
public static class EmailServiceExtensions
{
    public static Task SendAsync(
        this IEmailService email, string toEmail, string subject, string htmlBody, EmailPurpose purpose,
        CancellationToken ct = default)
        => email.SendAsync(toEmail, new EmailContent(subject, htmlBody), purpose, ct);
}

/// <summary>
/// MailKit-based <see cref="IEmailService"/>. Resolves SMTP settings via
/// <see cref="SystemSettingsService"/> with a hybrid path (DB row preferred,
/// env vars as fallback). Updates to the SiteAdmin SMTP form take effect on
/// the next request without a restart.
///
/// <para>
/// This is the transport, not the service the app injects: DI gives callers
/// <see cref="OutboxEmailService"/>, which queues most messages and leaves this
/// class to be driven by <see cref="EmailOutboxScheduler"/>. Only the purposes
/// in <see cref="EmailPurposes.SendsInline"/> reach it from a request thread.
/// </para>
///
/// <para>
/// Failures throw, and the caller decides what that means. For a queued message
/// the caller is the drain, which backs off and retries, and a give-up lands on
/// /site-admin/email for an operator to find (issue #790). For an inline one it
/// is the flow itself: the email-MFA paths redirect to an error, and the
/// SiteAdmin test email reports the exception text, because both exist to tell
/// someone right now whether the send worked.
/// </para>
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    /// <summary>
    /// Ceiling on a single send, connect included. Short enough that a batch of
    /// them still fits inside the drain's own deadline, and that an inline
    /// send fails while the person who triggered it is still watching.
    /// </summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly SystemSettingsService _settings;
    private readonly ILogger<SmtpEmailService> _logger;

    // Cache the resolved value for the request lifetime so an
    // `IsConfiguredAsync()`-then-`SendAsync()` pair doesn't double-hit the
    // database. The service is registered scoped, so the cache lives exactly
    // one request — settings updates land on the next request.
    private bool _resolved;
    private ResolvedSmtpSettings? _cached;

    public SmtpEmailService(SystemSettingsService settings, ILogger<SmtpEmailService> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        await ResolveOnceAsync(ct) is not null;

    public async Task SendAsync(
        string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
    {
        var resolved = await ResolveOnceAsync(ct);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                "Email is not configured. Set SMTP via /site-admin/settings or the SMTP_* env vars before triggering email-driven flows.");
        }

        var message = BuildMessage(resolved, toEmail, content);

        using var client = new SmtpClient
        {
            // MailKit's default is two minutes, applied to the connect and to
            // every command read. A relay that accepts the connection and then
            // says nothing would hold this for that long per message - which is
            // a stalled drain when the outbox sends a batch, and two minutes of
            // a person staring at a code box on the inline paths.
            Timeout = (int)SendTimeout.TotalMilliseconds,
        };
        var secure = resolved.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto;
        await client.ConnectAsync(resolved.Host, resolved.Port, secure, ct);
        if (!string.IsNullOrEmpty(resolved.User))
        {
            await client.AuthenticateAsync(resolved.User, resolved.Password ?? string.Empty, ct);
        }
        await client.SendAsync(message, ct);

        // Past this point the server has the message. A relay that drops the
        // socket rather than answering QUIT must not turn a delivered email into
        // a failure, because the caller's answer to a failure is to send it
        // again. Disposing the client closes the socket either way.
        try
        {
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disconnecting from the mail server failed after the message was accepted.");
        }

        _logger.LogInformation("Sent {Purpose} email to {To} subject {Subject}.", purpose, toEmail, content.Subject);
    }

    /// <summary>
    /// Shapes the outgoing message. Split out of <see cref="SendAsync"/> so the
    /// headers and body can be asserted without an SMTP server: a MimeKit change
    /// that alters address or subject encoding still sends successfully and
    /// surfaces only as mail that renders wrong, which no smoke test catches.
    /// Transport (connect, authenticate, send) stays in <see cref="SendAsync"/>
    /// and is still only exercised against a real server.
    /// </summary>
    internal static MimeMessage BuildMessage(ResolvedSmtpSettings resolved, string toEmail, EmailContent content)
    {
        var message = new MimeMessage();
        var fromAddress = MailboxAddress.Parse(resolved.From);
        if (!string.IsNullOrWhiteSpace(resolved.FromName))
        {
            // Pair the configured display name with the resolved address so
            // recipients see "AL Workbench <noreply@…>" rather than the bare
            // address acting as its own name.
            fromAddress = new MailboxAddress(resolved.FromName, fromAddress.Address);
        }
        message.From.Add(fromAddress);
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = content.Subject;
        // With a plain-text part the body is multipart/alternative, text first:
        // RFC 2046 orders the parts from least to most preferred, so a client
        // that can show HTML picks the last one.
        message.Body = content.TextBody is null
            ? new TextPart("html") { Text = content.HtmlBody }
            : new BodyBuilder { TextBody = content.TextBody, HtmlBody = content.HtmlBody }.ToMessageBody();
        return message;
    }

    private async Task<ResolvedSmtpSettings?> ResolveOnceAsync(CancellationToken ct)
    {
        if (_resolved) return _cached;
        _cached = await _settings.ResolveSmtpAsync(ct);
        _resolved = true;
        return _cached;
    }
}

/// <summary>
/// The account emails still built as bare HTML strings. New emails are Razor
/// components in <c>Components/Email/</c> rendered by
/// <see cref="Email.EmailRenderer"/> (see <c>.design/email.md</c>); these move
/// onto that layout in issue #1029, and the class goes with them.
/// </summary>
public static class EmailTemplates
{
    public static (string Subject, string HtmlBody) ForgotPassword(string displayName, string resetUrl)
        => ("Reset your password",
            $"<p>Hi {Html(displayName)},</p>"
            + "<p>Someone (hopefully you) asked to reset your AL Workbench password. "
            + $"Use this link within the next hour to choose a new one:</p>"
            + $"<p><a href=\"{Html(resetUrl)}\">{Html(resetUrl)}</a></p>"
            + "<p>If you didn't request this, you can ignore this message and your password stays unchanged.</p>");

    public static (string Subject, string HtmlBody) SignupPending(string adminName, string requesterEmail, string orgName, string adminUsersUrl)
        => ($"New signup pending in {Subject(orgName)}",
            $"<p>Hi {Html(adminName)},</p>"
            + $"<p><strong>{Html(requesterEmail)}</strong> has asked to join <strong>{Html(orgName)}</strong>. "
            + "They can't sign in until you approve.</p>"
            + $"<p><a href=\"{Html(adminUsersUrl)}\">Review pending users</a></p>");

    public static (string Subject, string HtmlBody) SignupDecided(string displayName, string orgName, bool approved, string loginUrl)
        => approved
            ? ($"You're in: {Subject(orgName)}",
                $"<p>Hi {Html(displayName)},</p>"
                + $"<p>Your signup for <strong>{Html(orgName)}</strong> has been approved. You can now sign in:</p>"
                + $"<p><a href=\"{Html(loginUrl)}\">{Html(loginUrl)}</a></p>")
            : ($"Signup declined: {Subject(orgName)}",
                $"<p>Hi {Html(displayName)},</p>"
                + $"<p>Your signup request for <strong>{Html(orgName)}</strong> has been declined. "
                + "If you think this is a mistake, please reach out to the organisation's administrator directly.</p>");

    public static (string Subject, string HtmlBody) Invite(
        string invitingAdminName, string orgName, string roleLabel, string? welcomeMessage, string acceptUrl)
    {
        var welcomeBlock = string.IsNullOrWhiteSpace(welcomeMessage)
            ? string.Empty
            : $"<blockquote style=\"border-left: 3px solid #ccc; padding-left: 0.75em; margin: 1em 0; color: #444;\">{Html(welcomeMessage)}</blockquote>";
        return ($"You're invited to {Subject(orgName)} on AL Workbench",
            $"<p>Hi,</p>"
            + $"<p><strong>{Html(invitingAdminName)}</strong> has invited you to join "
            + $"<strong>{Html(orgName)}</strong> on AL Workbench as a <strong>{Html(roleLabel)}</strong>.</p>"
            + welcomeBlock
            + $"<p>Use this link within the next 7 days to set a full name and password:</p>"
            + $"<p><a href=\"{Html(acceptUrl)}\">{Html(acceptUrl)}</a></p>"
            + $"<p>If you weren't expecting this invitation, you can ignore this message.</p>");
    }

    public static (string Subject, string HtmlBody) SignupVerification(string verifyUrl, string code)
        => ("Verify your email for AL Workbench",
            // No display name is known yet — the account doesn't exist.
            "<p>Hi,</p>"
            + "<p>Someone (hopefully you) started signing up for AL Workbench with this email "
            + "address. Confirm it's yours to continue — this link and code are valid for the next "
            + "30 minutes:</p>"
            + $"<p><a href=\"{Html(verifyUrl)}\">{Html(verifyUrl)}</a></p>"
            + "<p>Or enter this code on the signup page: "
            + $"<strong style=\"font-size: 1.5em; letter-spacing: 0.15em;\">{Html(code)}</strong></p>"
            + "<p>If you didn't start a signup, you can ignore this message — no account is created until "
            + "the address is confirmed.</p>");

    public static (string Subject, string HtmlBody) MagicLink(string displayName, string magicUrl)
        => ("Your AL Workbench sign-in link",
            $"<p>Hi {Html(displayName)},</p>"
            + "<p>Use this single-use link to sign in to AL Workbench. "
            + "It's valid for the next 15 minutes:</p>"
            + $"<p><a href=\"{Html(magicUrl)}\">{Html(magicUrl)}</a></p>"
            + "<p>If you didn't request this link, you can ignore this email.</p>");

    public static (string Subject, string HtmlBody) MfaEmailCode(string displayName, string code)
        => ("Your AL Workbench sign-in code",
            $"<p>Hi {Html(displayName)},</p>"
            + $"<p>Your verification code is <strong style=\"font-size: 1.5em; letter-spacing: 0.15em;\">{Html(code)}</strong></p>"
            + "<p>It expires in 10 minutes. If you didn't request it, ignore this email and change your password.</p>");

    public static (string Subject, string HtmlBody) EmailChangeConfirm(string displayName, string confirmUrl)
        => ("Confirm your new AL Workbench email",
            $"<p>Hi {Html(displayName)},</p>"
            + "<p>An administrator changed the email address on your AL Workbench account to this one. "
            + "Click below within 24 hours to confirm. Until then, sign-in still uses your old address.</p>"
            + $"<p><a href=\"{Html(confirmUrl)}\">{Html(confirmUrl)}</a></p>"
            + "<p>If you weren't expecting this, ignore the message — the change won't take effect.</p>");

    private static string Html(string value)
        => System.Net.WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>
    /// Sanitises a free-text value before it's interpolated into a mail
    /// Subject. MimeKit already strips CR/LF on the Subject setter so this is
    /// not the header-injection guard — it's the same defensive pass the
    /// HTML bodies get via <see cref="Html"/>, so the one place a value reaches
    /// the Subject isn't the lone unsanitised surface. Collapses control
    /// characters (newlines, tabs) to spaces and trims. #417
    /// </summary>
    private static string Subject(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var chars = value.Select(c => char.IsControl(c) ? ' ' : c).ToArray();
        return new string(chars).Trim();
    }
}
