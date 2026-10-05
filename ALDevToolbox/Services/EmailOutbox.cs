using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services;

/// <summary>
/// The store behind <see cref="OutboxEmailService"/> and
/// <see cref="EmailOutboxScheduler"/>: queues a message, hands the drain the
/// ones that are due, and records how each attempt went. Issue #790.
///
/// <para>
/// Every method opens its own short-lived context through the scoped
/// <see cref="IDbContextFactory{TContext}"/> rather than sharing the caller's
/// <see cref="AppDbContext"/>. An enqueue happens mid-request, and saving on the
/// request's context would flush whatever else that request had tracked but not
/// yet saved — a queued email must never commit someone's half-finished edit.
/// (<see cref="AuditService"/> uses the factory for a different reason; both are
/// the sanctioned exception to "services share the scoped context", not a free
/// choice.) The cost is that the row is not written in the caller's
/// transaction: a send queued for work that then rolls back still goes out.
/// Making those atomic means moving the enqueue inside each caller's
/// transaction, which is a bigger change than #790 asked for.
/// </para>
/// </summary>
public sealed class EmailOutbox
{
    /// <summary>
    /// Data Protection purpose for outbox bodies. Its own purpose, like every
    /// other secret we encrypt, so ciphertext minted here can never be
    /// unprotected as an SMTP password and vice versa.
    /// </summary>
    public const string BodyProtectionPurpose = "ALDevToolbox.EmailOutbox.Body";

    /// <summary>
    /// Attempts before a message is given up on. Paired with
    /// <see cref="Backoff"/> the seven waits sum to 173 minutes, so a message is
    /// tried across a little under three hours - long enough to outlast a relay
    /// restart or a credential rotated and fixed the same morning. Past that the
    /// tokens most of these messages carry have expired anyway, so retrying is
    /// only noise in front of the operator's real problem.
    /// </summary>
    public const int MaxAttempts = 8;

    /// <summary>
    /// Wait after each failed attempt, indexed by attempts already made. The
    /// last entry repeats for every attempt beyond it.
    /// </summary>
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
    ];

    /// <summary>
    /// How long a sent row is kept. Short on purpose: a delivered body may still
    /// hold a live token, and once the mail is out the row's only job is to say
    /// "this went" for a little while.
    /// </summary>
    public static readonly TimeSpan SentRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// How long a given-up row is kept. Long, because it is the evidence an
    /// operator came to look for; its body is dropped when it fails, so the row
    /// holds no secret by then.
    /// </summary>
    public static readonly TimeSpan FailedRetention = TimeSpan.FromDays(30);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IDataProtector _protector;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmailOutbox> _logger;

    public EmailOutbox(
        IDbContextFactory<AppDbContext> dbFactory,
        IDataProtectionProvider protection,
        TimeProvider clock,
        ILogger<EmailOutbox> logger)
    {
        _dbFactory = dbFactory;
        _protector = protection.CreateProtector(BodyProtectionPurpose);
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Queues a message for the next drain. Throws if the row can't be written —
    /// the caller's existing catch then reports what it always reported, because
    /// an email that could not even be queued really did fail to send.
    /// </summary>
    public async Task EnqueueAsync(
        string toEmail,
        EmailContent content,
        EmailPurpose purpose,
        int? organizationId,
        CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.EmailOutboxMessages.Add(new EmailOutboxMessage
        {
            ToEmail = toEmail,
            Subject = content.Subject,
            BodyEncrypted = _protector.Protect(content.HtmlBody),
            TextBodyEncrypted = content.TextBody is null ? null : _protector.Protect(content.TextBody),
            Purpose = purpose,
            Status = EmailOutboxStatus.Pending,
            CreatedAt = now,
            // Due immediately: the scheduler's poll interval is the only delay.
            NextAttemptAt = now,
            OrganizationId = organizationId,
        });
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Queued {Purpose} email to {To}.", purpose, toEmail);
    }

    /// <summary>
    /// How long a message may sit unsent before it is written off. The drain
    /// gives up on a message it is actively trying within about four hours, so
    /// a row older than this means the drain was not running at all - the
    /// container was down, or the sweep was switched off. Two reasons not to
    /// simply send it when the drain comes back: the token it carries expired
    /// long ago, so the recipient would get a dead link, and until then the row
    /// is sitting on a live secret nothing is bounding.
    /// </summary>
    public static readonly TimeSpan PendingRetention = TimeSpan.FromHours(24);

    /// <summary>
    /// The pending messages due now, oldest first. Capped so one sweep can't sit
    /// on the SMTP connection indefinitely; the rest come round on the next poll.
    /// </summary>
    public async Task<List<EmailOutboxMessage>> DueAsync(int batchSize, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.EmailOutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == EmailOutboxStatus.Pending && m.NextAttemptAt <= now)
            .OrderBy(m => m.NextAttemptAt)
            .ThenBy(m => m.Id)
            .Take(batchSize)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Decrypts a queued message's bodies, or returns null when the ciphertext
    /// can't be read — a key ring restored without <c>app-keys</c>, say. Null
    /// means the message is unsendable rather than temporarily stuck, so the
    /// drain gives up on it instead of retrying seven more times. A row queued
    /// without a plain-text part comes back with <see cref="EmailContent.TextBody"/> null.
    /// </summary>
    public EmailContent? TryReadContent(EmailOutboxMessage message)
    {
        if (string.IsNullOrEmpty(message.BodyEncrypted)) return null;
        try
        {
            var html = _protector.Unprotect(message.BodyEncrypted);
            var text = message.TextBodyEncrypted is null ? null : _protector.Unprotect(message.TextBodyEncrypted);
            return new EmailContent(message.Subject, html, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not decrypt the body of outbox message {Id}.", message.Id);
            return null;
        }
    }

    /// <summary>Records a successful hand-off to SMTP and drops the body.</summary>
    public async Task MarkSentAsync(int id, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.EmailOutboxMessages
            // Still pending, not merely this id: a row written off as stale (or,
            // if anyone ever runs two drains, sent by the other) must not be
            // flipped back to Sent carrying the other outcome's error text.
            .Where(m => m.Id == id && m.Status == EmailOutboxStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Sent)
                .SetProperty(m => m.SentAt, now)
                .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1)
                // The mail is out; keeping a copy of the token it carried is
                // exposure with nothing left to gain.
                .SetProperty(m => m.BodyEncrypted, (string?)null)
                .SetProperty(m => m.TextBodyEncrypted, (string?)null),
                ct);
    }

    /// <summary>
    /// Records a failed attempt: schedules the next try, or gives up when the
    /// attempts run out (or when <paramref name="permanent"/> says trying again
    /// cannot help). A given-up row keeps its error and loses its body.
    /// </summary>
    public async Task RecordFailureAsync(int id, string error, bool permanent = false, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var message = await db.EmailOutboxMessages.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null) return;

        message.AttemptCount++;
        message.LastError = Truncate(error);
        if (permanent || message.AttemptCount >= MaxAttempts)
        {
            message.Status = EmailOutboxStatus.Failed;
            message.BodyEncrypted = null;
            message.TextBodyEncrypted = null;
            _logger.LogError(
                "Giving up on the {Purpose} email to {To} after {Attempts} attempt(s): {Error}",
                message.Purpose, message.ToEmail, message.AttemptCount, message.LastError);
        }
        else
        {
            message.NextAttemptAt = now + DelayFor(message.AttemptCount);
            _logger.LogWarning(
                "Attempt {Attempts} to send the {Purpose} email to {To} failed; retrying at {NextAttempt:o}. {Error}",
                message.AttemptCount, message.Purpose, message.ToEmail, message.NextAttemptAt, message.LastError);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Writes off messages that have sat unsent past <see cref="PendingRetention"/>,
    /// dropping their bodies. Returns how many. Note the limit of this as a
    /// safety net: it runs from the drain, so the one case it cannot cover is
    /// the drain never running at all.
    /// </summary>
    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - PendingRetention;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var expired = await db.EmailOutboxMessages
            .Where(m => m.Status == EmailOutboxStatus.Pending && m.CreatedAt < cutoff)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Status, EmailOutboxStatus.Failed)
                .SetProperty(m => m.BodyEncrypted, (string?)null)
                .SetProperty(m => m.TextBodyEncrypted, (string?)null)
                .SetProperty(m => m.LastError,
                    "Never sent: this sat in the queue for more than a day, by which time the link it carried had expired. Sending was probably switched off or the site was down."),
                ct);
        if (expired > 0)
        {
            _logger.LogWarning(
                "Wrote off {Count} email(s) that sat unsent for more than {Hours} hours.",
                expired, PendingRetention.TotalHours);
        }
        return expired;
    }

    /// <summary>
    /// Deletes rows past their retention. Returns the two counts so the sweep
    /// can log something worth reading.
    /// </summary>
    public async Task<(int Sent, int Failed)> PruneAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var sentCutoff = now - SentRetention;
        var failedCutoff = now - FailedRetention;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sent = await db.EmailOutboxMessages
            // By sent_at, not created_at: a message queued at noon and delivered
            // at three would otherwise be deleted 21 hours after it went out,
            // and SnapshotAsync counts the last day's sends by sent_at - so the
            // header's "is mail working at all" figure would quietly under-report.
            .Where(m => m.Status == EmailOutboxStatus.Sent && m.SentAt < sentCutoff)
            .ExecuteDeleteAsync(ct);
        var failed = await db.EmailOutboxMessages
            .Where(m => m.Status == EmailOutboxStatus.Failed && m.CreatedAt < failedCutoff)
            .ExecuteDeleteAsync(ct);
        return (sent, failed);
    }

    /// <summary>
    /// What /site-admin/email shows: everything that has been given up on,
    /// everything still waiting, and how much went out in the last day. Bodies
    /// are never read here — an operator needs to know that a reset link is
    /// stuck, not what the link is.
    /// </summary>
    public async Task<EmailOutboxSnapshot> SnapshotAsync(int limit = 100, CancellationToken ct = default)
    {
        var since = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(1);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // One query per status, each with its own limit. A single query over
        // both and a split afterwards looks tidier and is wrong: pending rows
        // are always newer than the failures an operator came to find, so a
        // burst of them would take every slot and the page would render the
        // reassuring "nothing has failed" state over a table full of failures.
        var failed = await ListAsync(db, EmailOutboxStatus.Failed, limit, ct);
        var waiting = await ListAsync(db, EmailOutboxStatus.Pending, limit, ct);

        var sentLastDay = await db.EmailOutboxMessages
            .AsNoTracking()
            .CountAsync(m => m.Status == EmailOutboxStatus.Sent && m.SentAt >= since, ct);

        // Totals, so a truncated list can say it is truncated rather than
        // quietly under-reporting.
        var failedTotal = await db.EmailOutboxMessages
            .AsNoTracking().CountAsync(m => m.Status == EmailOutboxStatus.Failed, ct);
        var waitingTotal = await db.EmailOutboxMessages
            .AsNoTracking().CountAsync(m => m.Status == EmailOutboxStatus.Pending, ct);

        return new EmailOutboxSnapshot(failed, waiting, sentLastDay, failedTotal, waitingTotal);
    }

    /// <summary>
    /// One status's rows, newest first, with the organisation label. Left-joined
    /// to organizations; neither table is scoped by the tenant filter, and the
    /// caller is SiteAdmin-only, so there is nothing here to bypass.
    /// </summary>
    private static Task<List<EmailOutboxRow>> ListAsync(
        AppDbContext db, EmailOutboxStatus status, int limit, CancellationToken ct) =>
        (from m in db.EmailOutboxMessages.AsNoTracking()
         where m.Status == status
         orderby m.CreatedAt descending, m.Id descending
         join o in db.Organizations.AsNoTracking() on m.OrganizationId equals o.Id into orgs
         from org in orgs.DefaultIfEmpty()
         select new EmailOutboxRow(
             m.Id, m.ToEmail, m.Purpose, m.Status, m.AttemptCount, m.LastError,
             m.CreatedAt, m.NextAttemptAt, org == null ? null : org.Name))
        .Take(limit)
        .ToListAsync(ct);

    /// <summary>Wait before the next attempt, given how many have been made.</summary>
    internal static TimeSpan DelayFor(int attemptsMade) =>
        Backoff[Math.Clamp(attemptsMade - 1, 0, Backoff.Length - 1)];

    /// <summary>
    /// Keeps one enormous SMTP error (a server that answers with a wall of text)
    /// from becoming the biggest column in the table.
    /// </summary>
    private static string Truncate(string error) =>
        error.Length <= 1000 ? error : error[..1000] + "...";
}

/// <summary>One outbox row as the SiteAdmin list shows it. No body, ever.</summary>
public sealed record EmailOutboxRow(
    int Id,
    string ToEmail,
    EmailPurpose Purpose,
    EmailOutboxStatus Status,
    int AttemptCount,
    string? LastError,
    DateTime CreatedAt,
    DateTime NextAttemptAt,
    string? OrganizationName);

/// <summary>
/// The outbox as /site-admin/email reads it. The two lists are capped; the
/// totals beside them are not, so the page can say when it is showing a slice.
/// </summary>
public sealed record EmailOutboxSnapshot(
    IReadOnlyList<EmailOutboxRow> Failed,
    IReadOnlyList<EmailOutboxRow> Waiting,
    int SentLastDay,
    int FailedTotal,
    int WaitingTotal);
