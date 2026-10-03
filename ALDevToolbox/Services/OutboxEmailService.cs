using ALDevToolbox.Domain.Entities;

namespace ALDevToolbox.Services;

/// <summary>
/// Which messages are handed straight to SMTP instead of going through the
/// outbox. The test is not "how important is this mail" but "is a person
/// waiting on this exact send, right now, with the failure in front of them":
/// for those, a queue adds latency and hides an answer the caller already
/// shows. Everything else gains from being retried.
/// </summary>
public static class EmailPurposes
{
    /// <summary>
    /// True when <paramref name="purpose"/> is sent inline.
    ///
    /// <para><see cref="EmailPurpose.MfaCode"/>: the user is staring at a code
    /// entry box, and the flow already redirects to an error when the send
    /// fails. A poll interval of delay is the one thing this message cannot
    /// afford.</para>
    ///
    /// <para><see cref="EmailPurpose.SiteAdminTest"/>: its entire job is
    /// answering "does SMTP work right now", and it reports the exception text
    /// back to the operator. Queueing it would replace that answer with "we'll
    /// let you know", which is the opposite of a test.</para>
    /// </summary>
    public static bool SendsInline(EmailPurpose purpose) =>
        purpose is EmailPurpose.MfaCode or EmailPurpose.SiteAdminTest;
}

/// <summary>
/// The <see cref="IEmailService"/> the app actually injects: writes the message
/// to <see cref="EmailOutbox"/> and lets <see cref="EmailOutboxScheduler"/>
/// deliver it, so a transport failure is retried and visible instead of
/// vanishing into a log warning. Issue #790.
///
/// <para>
/// This decorates rather than replaces <see cref="SmtpEmailService"/>, which
/// keeps its "failures throw" contract and still does every send — just from
/// the drain rather than from the request thread. Callers are unaffected: a
/// throw from here means the message could not be *queued*, which is as much a
/// failed send as a refused connection, so the catch each flow already has
/// still says the right thing.
/// </para>
///
/// <para>
/// What callers do lose is the transport failure itself. That is the point: the
/// enumeration-resistant flows must not tell the user whether their reset mail
/// went out, and nothing was telling the operator either. Now the row is in
/// <c>email_outbox</c> and on /site-admin/email either way. The purposes in
/// <see cref="EmailPurposes.SendsInline"/> keep the old behaviour.
/// </para>
/// </summary>
public sealed class OutboxEmailService : IEmailService
{
    private readonly IEmailService _inner;
    private readonly EmailOutbox _outbox;
    private readonly IOrganizationContext _orgContext;

    /// <param name="inner">
    /// The transport, which in the app is always <see cref="SmtpEmailService"/>.
    /// Registration passes it explicitly rather than letting the container
    /// resolve <see cref="IEmailService"/> — that would resolve to this class
    /// and recurse.
    /// </param>
    public OutboxEmailService(IEmailService inner, EmailOutbox outbox, IOrganizationContext orgContext)
    {
        _inner = inner;
        _outbox = outbox;
        _orgContext = orgContext;
    }

    /// <summary>
    /// Still answers for SMTP itself, not for the queue. Callers use this to
    /// decide whether to offer an email-driven flow at all, and queueing mail
    /// that has no server to go to would only fill the outbox with rows no
    /// retry can clear.
    /// </summary>
    public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => _inner.IsConfiguredAsync(ct);

    public async Task SendAsync(
        string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
    {
        if (EmailPurposes.SendsInline(purpose))
        {
            await _inner.SendAsync(toEmail, content, purpose, ct);
            return;
        }

        // Null on the pre-auth flows, which have no organisation yet. It is a
        // label on the operator's list, never a fence - see EmailOutboxMessage.
        await _outbox.EnqueueAsync(toEmail, content, purpose, _orgContext.CurrentOrganizationId, ct);
    }
}
