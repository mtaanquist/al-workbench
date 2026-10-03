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
