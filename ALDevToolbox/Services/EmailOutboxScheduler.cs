using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services;

/// <summary>
/// Drains <see cref="EmailOutbox"/>: sends what is due, backs off what fails,
/// and prunes what is past its retention. Issue #790.
///
/// <para>
/// A table sweep rather than a channel, because the work has to survive a
/// deploy — an unsent password reset that a restart dropped would put us back
/// where we started. The poll interval is the worst-case delay a queued message
/// sees, so it is short; the messages that cannot afford even that are sent
/// inline and never reach this loop (see <see cref="EmailPurposes.SendsInline"/>).
/// Opt out with <c>DISABLE_EMAIL_OUTBOX_SCHEDULER=1</c>.
/// </para>
///
/// <para>
/// The drain assumes it is the only one running, which holds for the one-app-container
/// deployment in <c>.design/architecture.md</c>: rows are taken by a plain read
/// rather than claimed, so a second instance would send some messages twice.
/// Give it a claim step before running two.
/// </para>
/// </summary>
public sealed class EmailOutboxScheduler : PolledScheduler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    /// <summary>How often the retention delete runs. The drain runs every poll.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    /// <summary>Messages one sweep will try. The rest wait for the next poll.</summary>
    private const int BatchSize = 25;

    /// <summary>
    /// Ceiling on one sweep. Each send is capped by
    /// <see cref="SmtpEmailService.SendTimeout"/>, but a whole batch of slow
    /// ones still adds up, and a tick that outruns its heartbeat bounds makes
    /// /healthz/workers report this worker stalled with the wrong reason -
    /// during exactly the outage it exists to work through. Whatever is left
    /// when the deadline lands is still pending and comes round next poll.
    /// </summary>
    private static readonly TimeSpan SweepDeadline = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailOutboxScheduler> _logger;
    private DateTimeOffset? _lastPruneUtc;

    public EmailOutboxScheduler(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<EmailOutboxScheduler> logger,
        WorkerHeartbeatRegistry heartbeats)
        : base(logger, heartbeats, nameof(EmailOutboxScheduler),
            pollInterval: PollInterval,
            // Idle silence must sit ABOVE the longest legitimate tick: the base
            // class ticks the heartbeat once per loop and does not exempt an
            // active tick, so a shorter idle bound is the one that fires, and it
            // reports "gone quiet" when the worker is in fact busy. Both bounds
            // clear SweepDeadline plus a poll.
            maxActiveDuration: TimeSpan.FromMinutes(6),
            maxIdleSilence: TimeSpan.FromMinutes(10),
            disableEnvVar: "DISABLE_EMAIL_OUTBOX_SCHEDULER")
    {
        _services = services;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task TickAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var outbox = scope.ServiceProvider.GetRequiredService<EmailOutbox>();
        var smtp = scope.ServiceProvider.GetRequiredService<SmtpEmailService>();

        // The deadline is the sweep's own, not the host's: cancelling it must
        // not read as shutdown, because the two mean different things to
        // SendOneAsync below.
        using var deadline = new CancellationTokenSource(SweepDeadline);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        // Before anything is sent: write off what has sat here too long. After a
        // long outage the queue holds messages whose links expired days ago, and
        // delivering those is worse than not delivering them.
        await outbox.ExpireStaleAsync(ct);

        foreach (var message in await outbox.DueAsync(BatchSize, linked.Token))
        {
            if (deadline.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "The email sweep hit its {Deadline:N0} minute deadline; the rest stay queued for the next poll.",
                    SweepDeadline.TotalMinutes);
                break;
            }
            ct.ThrowIfCancellationRequested();
            await SendOneAsync(outbox, smtp, message, ct, linked.Token);
        }

        await PruneIfDueAsync(outbox, ct);
    }

    /// <summary>
    /// One message: read the body, hand it to <paramref name="transport"/>, and
    /// record how that went. Split out of the loop so the branches can be tested
    /// against a fake transport - the drain deliberately holds the concrete
    /// <see cref="SmtpEmailService"/> (resolving <see cref="IEmailService"/>
    /// would give it the outbox decorator and queue the message again), and that
    /// class is sealed, so without this seam nothing here is reachable from a
    /// test without a live SMTP server.
    /// </summary>
    /// <param name="shutdownToken">
    /// The host's. Cancelling it leaves the row pending for the next start.
    /// </param>
    /// <param name="sendToken">
    /// The host's plus this sweep's deadline. Cancelling it because the deadline
    /// landed is an ordinary failed attempt, so it backs off like any other.
    /// </param>
    internal static async Task SendOneAsync(
        EmailOutbox outbox,
        IEmailService transport,
        EmailOutboxMessage message,
        CancellationToken shutdownToken,
        CancellationToken sendToken)
    {
        var content = outbox.TryReadContent(message);
        if (content is null)
        {
            // Unreadable ciphertext, or a body already dropped. Neither gets
            // better by trying again.
            await outbox.RecordFailureAsync(
                message.Id,
                "The message body could not be read, so it can never be sent. This usually means the Data Protection key ring was replaced.",
                permanent: true,
                shutdownToken);
            return;
        }

        try
        {
            await transport.SendAsync(message.ToEmail, content, message.Purpose, sendToken);
            // Not the send token: a shutdown landing in the gap between a
            // delivered message and this update would leave the row pending and
            // send it a second time on the next start. The write is one indexed
            // UPDATE, comfortably inside the host's shutdown timeout.
            await outbox.MarkSentAsync(message.Id, CancellationToken.None);
        }
        catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
        {
            // Shutdown mid-sweep: leave the row pending for the next start.
            throw;
        }
        catch (Exception ex)
        {
            await outbox.RecordFailureAsync(message.Id, ex.Message, permanent: false, shutdownToken);
        }
    }

    private async Task PruneIfDueAsync(EmailOutbox outbox, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        if (_lastPruneUtc is { } last && now - last < PruneInterval) return;

        var (sent, failed) = await outbox.PruneAsync(ct);
        _lastPruneUtc = now;
        if (sent > 0 || failed > 0)
        {
            _logger.LogInformation(
                "Pruned {Sent} sent and {Failed} given-up email(s) from the outbox.", sent, failed);
        }
    }
}
