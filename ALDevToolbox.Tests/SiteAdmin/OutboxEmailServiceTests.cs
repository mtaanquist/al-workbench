using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// The routing rule in <see cref="OutboxEmailService"/>: which messages are
/// queued for delivery-with-retries, and which are still handed to SMTP on the
/// request thread. Issue #790.
/// </summary>
public sealed class OutboxEmailServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingEmailService _inner = new();

    public void Dispose() => _db.Dispose();

    private OutboxEmailService NewService() =>
        new(_inner,
            new EmailOutbox(_db.NewContextFactory(), _db.DataProtectionProvider, _clock, NullLogger<EmailOutbox>.Instance),
            _db.OrgContext);

    private async Task<List<EmailOutboxMessage>> QueuedAsync()
    {
        await using var ctx = _db.NewContext();
        return await ctx.EmailOutboxMessages.AsNoTracking().OrderBy(m => m.Id).ToListAsync();
    }

    [Theory]
    [InlineData(EmailPurpose.SignupVerification)]
    [InlineData(EmailPurpose.SignupPendingNotice)]
    [InlineData(EmailPurpose.SignupDecision)]
    [InlineData(EmailPurpose.PasswordReset)]
    [InlineData(EmailPurpose.MagicLink)]
    [InlineData(EmailPurpose.Invite)]
    [InlineData(EmailPurpose.EmailChangeConfirmation)]
    public async Task Most_messages_are_queued_rather_than_sent_on_the_request_thread(EmailPurpose purpose)
    {
        await NewService().SendAsync("user@cronus.com", new EmailContent("Subject", "<p>Body</p>"), purpose);

        _inner.Sent.Should().BeEmpty();
        var queued = await QueuedAsync();
        queued.Should().ContainSingle();
        queued[0].Purpose.Should().Be(purpose);
        queued[0].Status.Should().Be(EmailOutboxStatus.Pending);
    }

    [Theory]
    [InlineData(EmailPurpose.MfaCode)]
    [InlineData(EmailPurpose.SiteAdminTest)]
    public async Task A_message_someone_is_waiting_on_still_goes_out_inline(EmailPurpose purpose)
    {
        await NewService().SendAsync("user@cronus.com", new EmailContent("Subject", "<p>Body</p>"), purpose);

        // Both of these report the outcome to the person who triggered them, so
        // a poll interval of delay would be the wrong trade.
        _inner.Sent.Should().ContainSingle().Which.Purpose.Should().Be(purpose);
        (await QueuedAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_purpose_is_routed_one_way_or_the_other()
    {
        // Guards the pair of theories above against a new purpose slipping in
        // unconsidered: adding one fails here until it is routed deliberately.
        foreach (var purpose in Enum.GetValues<EmailPurpose>())
        {
            var inlineBefore = _inner.Sent.Count;
            var queuedBefore = (await QueuedAsync()).Count;

            await NewService().SendAsync("user@cronus.com", new EmailContent("Subject", "<p>Body</p>"), purpose);

            var inlineAfter = _inner.Sent.Count;
            var queuedAfter = (await QueuedAsync()).Count;
            (inlineAfter - inlineBefore + (queuedAfter - queuedBefore))
                .Should().Be(1, "{0} must be either sent inline or queued, and not both or neither", purpose);
            EmailPurposes.SendsInline(purpose).Should().Be(inlineAfter > inlineBefore);
        }
    }

    [Fact]
    public async Task A_queued_message_records_the_organisation_the_request_acted_for()
    {
        await NewService().SendAsync("user@cronus.com", new EmailContent("Subject", "<p>Body</p>"), EmailPurpose.Invite);

        (await QueuedAsync())[0].OrganizationId.Should().Be(TestDb.DefaultOrgId);
    }

    [Fact]
    public async Task A_queued_message_from_a_pre_auth_flow_has_no_organisation()
    {
        _db.OrgContext.CurrentOrganizationId = null;
        try
        {
            await NewService().SendAsync("user@cronus.com", new EmailContent("Subject", "<p>Body</p>"), EmailPurpose.PasswordReset);
            (await QueuedAsync())[0].OrganizationId.Should().BeNull();
        }
        finally
        {
            _db.OrgContext.CurrentOrganizationId = TestDb.DefaultOrgId;
        }
    }

    [Fact]
    public async Task Whether_email_is_configured_is_still_answered_by_the_transport()
    {
        // Callers use this to decide whether to offer an email-driven flow at
        // all. Queueing mail that has no server to go to would only fill the
        // outbox with rows no retry can clear.
        _inner.Configured = false;
        (await NewService().IsConfiguredAsync()).Should().BeFalse();

        _inner.Configured = true;
        (await NewService().IsConfiguredAsync()).Should().BeTrue();
    }

    private sealed class RecordingEmailService : IEmailService
    {
        public bool Configured { get; set; } = true;
        public List<(string To, string Subject, string Body, EmailPurpose Purpose)> Sent { get; } = [];

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(Configured);

        public Task SendAsync(
            string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
            return Task.CompletedTask;
        }
    }
}
