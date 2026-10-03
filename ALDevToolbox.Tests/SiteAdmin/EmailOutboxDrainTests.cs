using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services;
using ALDevToolbox.Tests.Auth;
using ALDevToolbox.Tests.Infrastructure;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ALDevToolbox.Tests.SiteAdmin;

/// <summary>
/// What the drain does with one message. These go through
/// <see cref="EmailOutboxScheduler.SendOneAsync"/> rather than the hosted
/// service's loop: the loop resolves the concrete, sealed
/// <see cref="SmtpEmailService"/> on purpose (resolving
/// <see cref="IEmailService"/> would hand it the outbox decorator and queue the
/// message straight back), so this is the seam that lets a fake transport stand
/// in for a live SMTP server.
/// </summary>
public sealed class EmailOutboxDrainTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 15, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private EmailOutbox NewOutbox() =>
        new(_db.NewContextFactory(), _db.DataProtectionProvider, _clock, NullLogger<EmailOutbox>.Instance);

    private async Task<EmailOutboxMessage> RowAsync(int id)
    {
        await using var ctx = _db.NewContext();
        return await ctx.EmailOutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
    }

    private async Task<EmailOutboxMessage> QueueOneAsync(EmailOutbox outbox)
    {
        await outbox.EnqueueAsync(
            "user@cronus.com", new EmailContent("Reset your password", "<p>link</p>"), EmailPurpose.PasswordReset, null);
        return (await outbox.DueAsync(1)).Single();
    }

    [Fact]
    public async Task A_delivered_message_is_marked_sent_and_loses_its_body()
    {
        var outbox = NewOutbox();
        var transport = new FakeTransport();
        var message = await QueueOneAsync(outbox);

        await EmailOutboxScheduler.SendOneAsync(outbox, transport, message, default, default);

        transport.Sent.Should().ContainSingle().Which.Body.Should().Be("<p>link</p>");
        var row = await RowAsync(message.Id);
        row.Status.Should().Be(EmailOutboxStatus.Sent);
        row.BodyEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task A_delivered_message_with_a_plain_text_part_sends_both_and_drops_both()
    {
        var outbox = NewOutbox();
        var transport = new FakeTransport();
        await outbox.EnqueueAsync(
            "user@cronus.com", new EmailContent("Reset your password", "<p>link</p>", "link"),
            EmailPurpose.PasswordReset, null);
        var message = (await outbox.DueAsync(1)).Single();

        await EmailOutboxScheduler.SendOneAsync(outbox, transport, message, default, default);

        transport.Contents.Should().ContainSingle().Which.TextBody.Should().Be("link");
        var row = await RowAsync(message.Id);
        row.BodyEncrypted.Should().BeNull();
        row.TextBodyEncrypted.Should().BeNull();
    }

    [Fact]
    public async Task A_refused_send_is_recorded_and_left_to_retry()
    {
        var outbox = NewOutbox();
        var transport = new FakeTransport { Throw = () => new IOException("Connection refused") };
        var message = await QueueOneAsync(outbox);

        await EmailOutboxScheduler.SendOneAsync(outbox, transport, message, default, default);

        var row = await RowAsync(message.Id);
        row.Status.Should().Be(EmailOutboxStatus.Pending);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Be("Connection refused");
    }

    [Fact]
    public async Task A_body_that_cannot_be_read_is_given_up_on_immediately()
    {
        // What a replaced Data Protection key ring looks like from the drain.
        // Seven more attempts would not decrypt it.
        var outbox = NewOutbox();
        var transport = new FakeTransport();
        var message = await QueueOneAsync(outbox);
        message.BodyEncrypted = "not-really-ciphertext";

        await EmailOutboxScheduler.SendOneAsync(outbox, transport, message, default, default);

        transport.Sent.Should().BeEmpty();
        var row = await RowAsync(message.Id);
        row.Status.Should().Be(EmailOutboxStatus.Failed);
        row.AttemptCount.Should().Be(1);
        row.LastError.Should().Contain("could not be read");
    }

    [Fact]
    public async Task A_shutdown_mid_send_leaves_the_message_pending_for_the_next_start()
    {
        var outbox = NewOutbox();
        using var shutdown = new CancellationTokenSource();
        var transport = new FakeTransport { Throw = () => { shutdown.Cancel(); return new OperationCanceledException(); } };
        var message = await QueueOneAsync(outbox);

        var act = () => EmailOutboxScheduler.SendOneAsync(
            outbox, transport, message, shutdown.Token, shutdown.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var row = await RowAsync(message.Id);
        row.Status.Should().Be(EmailOutboxStatus.Pending);
        row.AttemptCount.Should().Be(0, "a shutdown is not an attempt the message should be charged for");
    }

    [Fact]
    public async Task The_sweep_deadline_is_an_ordinary_failed_attempt_not_a_shutdown()
    {
        // The deadline cancels the send token but not the host's. That must back
        // the message off like any other failure rather than escaping the loop.
        var outbox = NewOutbox();
        using var deadline = new CancellationTokenSource();
        var transport = new FakeTransport { Throw = () => { deadline.Cancel(); return new OperationCanceledException(); } };
        var message = await QueueOneAsync(outbox);

        await EmailOutboxScheduler.SendOneAsync(outbox, transport, message, default, deadline.Token);

        var row = await RowAsync(message.Id);
        row.Status.Should().Be(EmailOutboxStatus.Pending);
        row.AttemptCount.Should().Be(1);
    }

    private sealed class FakeTransport : IEmailService
    {
        public List<(string To, string Subject, string Body, EmailPurpose Purpose)> Sent { get; } = [];
        public List<EmailContent> Contents { get; } = [];

        /// <summary>Set to make the next send fail with this exception.</summary>
        public Func<Exception>? Throw { get; set; }

        public Task<bool> IsConfiguredAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task SendAsync(
            string toEmail, EmailContent content, EmailPurpose purpose, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw();
            Sent.Add((toEmail, content.Subject, content.HtmlBody, purpose));
            Contents.Add(content);
            return Task.CompletedTask;
        }
    }
}
