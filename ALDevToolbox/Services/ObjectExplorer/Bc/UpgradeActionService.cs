using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// The request side of the upgrade actions in <c>oe_environment_upgrade_actions</c>: ask
/// for one of the three platform-update moves (move the date, start the update, change
/// the version) now or at an agreed slot, cancel one that
/// has not fired yet, and read an environment's history. The table doubles as the
/// per-environment activity feed — there is no second log. See
/// <c>.design/saas-delivery.md</c> and issue #657.
///
/// <para><b>Immediate is a direct send.</b> "As soon as possible" calls
/// <see cref="ProjectConnectionService"/> on the request thread, exactly as the fleet
/// page always did, and the row is written in its finished state. There is no worker hop
/// and nothing to cancel, because by the time the row exists the change has already
/// reached (or been refused by) Business Central. Only a future slot becomes a
/// <see cref="UpgradeActionStatus.Pending"/> row, which
/// <see cref="UpgradeActionWorker"/> fires when it comes due.</para>
///
/// <para><b>Nothing is enqueued for a scheduled action.</b> The worker finds due rows by
/// polling the table, so a slot agreed for tonight survives a restart this afternoon —
/// an in-memory channel would not.</para>
/// </summary>
public sealed class UpgradeActionService
{
    /// <summary>
    /// How far in the past a chosen slot may land before it is refused. A minute of
    /// slack, because "now" travels between the person's clock, the page, and this
    /// method — not a grace period.
    /// </summary>
    private static readonly TimeSpan PastSlack = TimeSpan.FromMinutes(1);

    /// <summary>How many entries one environment's feed shows. A history, not an archive.</summary>
    public const int FeedLimit = 50;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly ProjectConnectionService _connections;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgradeActionService> _logger;

    public UpgradeActionService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        ProjectConnectionService connections,
        TimeProvider clock,
        ILogger<UpgradeActionService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _connections = connections;
        _clock = clock;
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; an upgrade action was requested outside an authenticated request.");

    // ── Requesting ──────────────────────────────────────────────────────

    /// <summary>
    /// Asks for one platform-update move on one environment. With no
    /// <paramref name="executeAt"/> the move happens right now and the returned row is
    /// already <see cref="UpgradeActionStatus.Sent"/>; with a future
    /// <paramref name="executeAt"/> the row is <see cref="UpgradeActionStatus.Pending"/>
    /// and the worker fires it then.
    ///
    /// <para>A refusal from the immediate send (no update waiting, the date is already at
    /// the latest, the connection needs attention) is recorded as a
    /// <see cref="UpgradeActionStatus.Failed"/> row <em>and</em> rethrown, so the fleet
    /// page still shows the same per-row message it always did while the feed keeps the
    /// whole story — including the attempts that came to nothing.</para>
    /// </summary>
    /// <param name="targetVersion">
    /// The version a <see cref="UpgradeActionKind.SelectVersion"/> sets as the next update
    /// (e.g. <c>29.2</c>). Required for that kind and refused for every other, which act
    /// on the update already chosen.
    /// </param>
    /// <param name="upgradeId">
    /// The planned upgrade the action is run from (issue #984), or null for an ad hoc
    /// action. When given, the upgrade must be open and have this environment on it; the id
    /// is stored on the row, which is what the upgrade's derived line states read and what
    /// lets the environment's history say which wave an action belonged to.
    /// </param>
    public async Task<UpgradeActionRow> ScheduleUpgradeActionAsync(
        int projectId,
        int environmentId,
        UpgradeActionKind kind,
        DateTimeOffset? executeAt,
        CancellationToken ct = default,
        string? targetVersion = null,
        int? upgradeId = null)
    {
        var orgId = RequireOrganizationId();
        targetVersion = ValidateKind(kind, targetVersion);
        await _access.EnsureCanManageEnvironmentUpdatesAsync(projectId, ct).ConfigureAwait(false);

        // The environment has to belong to the project the gate was checked against —
        // otherwise a request could name someone else's environment with a project it is
        // allowed to act on.
        var exists = await _db.OeProjectEnvironments.AsNoTracking()
            .AnyAsync(e => e.Id == environmentId && e.ProjectId == projectId, ct).ConfigureAwait(false);
        if (!exists)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Environment"] = "That environment no longer exists. Refresh the list and try again.",
            });
        }

        if (upgradeId is { } fromUpgrade)
        {
            // An action run from an upgrade must be for one of its lines, and the upgrade
            // must still be open: a closed one is the record of a finished wave, and
            // crediting it with a new action would rewrite that record.
            var onOpenUpgrade = await _db.OeEnvironmentUpgradeLines.AsNoTracking()
                .AnyAsync(l => l.UpgradeId == fromUpgrade && l.EnvironmentId == environmentId
                               && l.Upgrade!.ClosedAt == null, ct).ConfigureAwait(false);
            if (!onOpenUpgrade)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["Upgrade"] = "This environment isn't on that upgrade any more, or the upgrade has been marked done. Reload the upgrade and try again.",
                });
            }
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var requestedBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);

        var action = new OeEnvironmentUpgradeAction
        {
            OrganizationId = orgId,
            ProjectId = projectId,
            EnvironmentId = environmentId,
            Kind = kind,
            TargetVersion = targetVersion,
            UpgradeId = upgradeId,
            RequestedByUserId = _orgContext.CurrentUserId,
            RequestedBy = requestedBy,
            RequestedAt = now,
        };

        if (executeAt is { } slot)
        {
            var slotUtc = slot.UtcDateTime;
            if (slotUtc < now - PastSlack)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["ExecuteAt"] = "Pick a time that hasn't happened yet.",
                });
            }

            action.Status = UpgradeActionStatus.Pending;
            action.ExecuteAfter = slotUtc;
            _db.OeEnvironmentUpgradeActions.Add(action);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _logger.LogInformation(
                "User {UserId} scheduled a {Kind} on environment {EnvironmentId} (project {ProjectId}, upgrade {UpgradeId}) for {ExecuteAfter}.",
                _orgContext.CurrentUserId, kind, environmentId, projectId, upgradeId, slotUtc);
            return UpgradeActionRow.From(action);
        }

        // Immediate: do the work here, then write down what happened. The name is read
        // first, so nothing after a write that landed can turn it into a failure.
        var environmentName = await EnvironmentNameAsync(environmentId, ct).ConfigureAwait(false);
        action.ExecuteAfter = now;
        action.SentAt = now;
        try
        {
            await RunAsync(projectId, environmentId, kind, targetVersion, ct).ConfigureAwait(false);
            action.Status = UpgradeActionStatus.Sent;
            action.Outcome = SuccessOutcome(kind, targetVersion, environmentName);
        }
        catch (PlanValidationException ex)
        {
            action.Status = UpgradeActionStatus.Failed;
            action.Outcome = FailureOutcome(kind,
                ex.Errors.Values.FirstOrDefault() ?? "Business Central refused the change.", targetVersion);
            _db.OeEnvironmentUpgradeActions.Add(action);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            throw;
        }

        _db.OeEnvironmentUpgradeActions.Add(action);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return UpgradeActionRow.From(action);
    }

    /// <summary>
    /// Cancels a scheduled action that hasn't fired. Refuses anything already sent,
    /// failed or cancelled, and — the race that matters — anything the worker has
    /// already claimed: the claim stamps <c>sent_at</c> while the row is still pending,
    /// so this compare-and-set finds nothing to update and the person is told the action
    /// already ran rather than being shown a cancel that quietly did nothing.
    /// </summary>
    public async Task CancelUpgradeActionAsync(int actionId, CancellationToken ct = default)
    {
        RequireOrganizationId();

        var action = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => new { a.Id, a.ProjectId, a.Status, a.Kind, OwnerId = a.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = "That scheduled action no longer exists.",
            });

        // The gate matches the one that made the booking: an upload is a manager's write
        // (the same check as installing it), the platform-update moves are the update
        // team's. Whoever may book it may call it off.
        if (action.Kind == UpgradeActionKind.UploadApp)
        {
            await _access.EnsureCanManageAsync(action.ProjectId, action.OwnerId, ct).ConfigureAwait(false);
        }
        else
        {
            await _access.EnsureCanManageEnvironmentUpdatesAsync(action.ProjectId, ct).ConfigureAwait(false);
        }

        if (action.Status != UpgradeActionStatus.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = AlreadyOverMessage(action.Status),
            });
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var cancelledBy = await AuditActor.ResolveAsync(_db, _orgContext.CurrentUserId, ct).ConfigureAwait(false);
        var cancelledByUserId = _orgContext.CurrentUserId;

        var updated = await _db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == actionId
                        && a.Status == UpgradeActionStatus.Pending
                        && a.SentAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Cancelled)
                // A cancelled upload has nothing left to send, so its package goes now.
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.CancelledAt, now)
                .SetProperty(a => a.CancelledBy, cancelledBy)
                .SetProperty(a => a.CancelledByUserId, cancelledByUserId), ct).ConfigureAwait(false);

        if (updated == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = "This action has already run, so there is nothing left to cancel.",
            });
        }

        _logger.LogInformation(
            "User {UserId} cancelled upgrade action {ActionId} on project {ProjectId}.",
            _orgContext.CurrentUserId, actionId, action.ProjectId);
    }

    /// <summary>
    /// Sends a booked upload now instead of waiting for its slot: the "Install now" on
    /// the environment's Scheduled installs list. Claims the row with the same
    /// compare-and-set the worker uses, so a sweep that picks the row up in the same
    /// second sends it once, not twice, and a cancel that got there first stands. A
    /// refusal from Business Central is recorded as a failed row <em>and</em> rethrown,
    /// as an immediate action's is, so the page shows the reason and the history keeps it.
    /// </summary>
    public async Task RunUploadNowAsync(int actionId, CancellationToken ct = default)
    {
        RequireOrganizationId();

        var booking = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => new { a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status, a.PackageFileName })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = "That booked install no longer exists.",
            });

        if (booking.Kind != UpgradeActionKind.UploadApp || booking.PackageFileName is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = "Only an app booked for a later install can be installed now.",
            });
        }
        if (booking.Status != UpgradeActionStatus.Pending)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = AlreadyOverMessage(booking.Status),
            });
        }

        // The send below re-checks access through ResolveEnvironmentAsync, but the claim
        // is a write of its own and must not happen for someone who may not send.
        var ownerId = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == booking.ProjectId)
            .Select(p => p.CreatedByUserId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        await _access.EnsureCanManageAsync(booking.ProjectId, ownerId, ct).ConfigureAwait(false);

        var claimedAt = _clock.GetUtcNow().UtcDateTime;
        var claimed = await _db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == actionId
                        && a.Status == UpgradeActionStatus.Pending
                        && a.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SentAt, claimedAt), ct).ConfigureAwait(false);
        if (claimed == 0)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Action"] = "This install has already started or been cancelled, so there is nothing left to do.",
            });
        }

        var package = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId)
            .Select(a => a.PackageContent)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var environmentName = await EnvironmentNameAsync(booking.EnvironmentId, ct).ConfigureAwait(false);

        UpgradeActionStatus status;
        string outcome;
        PlanValidationException? refusal = null;
        try
        {
            if (package is null || package.Length == 0)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["App"] = "The app file was no longer stored with the booking.",
                });
            }
            await _connections.SendBookedUploadAsync(
                booking.ProjectId, booking.EnvironmentId, package, booking.PackageFileName, ct).ConfigureAwait(false);
            status = UpgradeActionStatus.Sent;
            outcome = SuccessOutcome(UpgradeActionKind.UploadApp, environmentName: environmentName, fileName: booking.PackageFileName);
        }
        catch (PlanValidationException ex)
        {
            status = UpgradeActionStatus.Failed;
            outcome = FailureOutcome(UpgradeActionKind.UploadApp,
                ex.Errors.Values.FirstOrDefault() ?? "Business Central refused the app.", fileName: booking.PackageFileName);
            refusal = ex;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The row is claimed, so it must be settled here or it sits half-sent until the
            // worker's next restart sweep. The detail goes to the log, not the feed.
            _logger.LogError(ex, "Sending booked upload {ActionId} now threw.", actionId);
            await _db.OeEnvironmentUpgradeActions
                .Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                    .SetProperty(a => a.Outcome, FailureOutcome(UpgradeActionKind.UploadApp, "Business Central didn't accept the app.", fileName: booking.PackageFileName))
                    .SetProperty(a => a.PackageContent, (byte[]?)null), ct).ConfigureAwait(false);
            throw;
        }

        var finishedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == actionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.Outcome, outcome)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.SentAt, finishedAt), ct).ConfigureAwait(false);

        _logger.LogInformation(
            "User {UserId} sent booked upload {ActionId} ({FileName}) to environment {EnvironmentId} now: {Status}.",
            _orgContext.CurrentUserId, actionId, booking.PackageFileName, booking.EnvironmentId, status);
        if (refusal is not null) throw refusal;
    }

    // ── Reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// One environment's history, newest first, capped at <see cref="FeedLimit"/>.
    /// Reading it needs only project visibility: the feed answers "what has been done to
    /// this customer?", which anyone who can see the customer may ask. Acting on it still
    /// needs the environment-updates grant.
    /// </summary>
    public async Task<List<UpgradeActionRow>> ListEnvironmentActivityAsync(
        int projectId, int environmentId, CancellationToken ct = default)
    {
        await _access.EnsureCanViewAsync(projectId, ct).ConfigureAwait(false);

        return await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.EnvironmentId == environmentId)
            .OrderByDescending(a => a.RequestedAt)
            .ThenByDescending(a => a.Id)
            .Take(FeedLimit)
            .Select(a => new UpgradeActionRow(
                a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status,
                a.RequestedBy, a.RequestedAt, a.ExecuteAfter, a.SentAt, a.Outcome,
                a.CancelledBy, a.CancelledAt, a.TargetVersion,
                a.UpgradeId, a.Upgrade != null ? a.Upgrade.Name : null, a.PackageFileName))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every action still waiting to fire across the projects this caller can see — what
    /// puts a "1 scheduled" marker on a fleet row, so reloading the page doesn't lose the
    /// slots someone just booked. One query for the whole fleet, joined through
    /// <see cref="ProjectAccess.VisibleProjectPredicate"/> like the fleet list itself.
    /// </summary>
    public async Task<List<UpgradeActionRow>> ListPendingAsync(CancellationToken ct = default)
    {
        var snapshot = await _access.GetSnapshotAsync(ct).ConfigureAwait(false);
        var visible = ProjectAccess.VisibleProjectPredicate(snapshot);

        return await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Status == UpgradeActionStatus.Pending)
            .Where(a => _db.OeProjects.Where(visible)
                .Any(p => p.Id == a.ProjectId && p.DeletedAt == null))
            .OrderBy(a => a.ExecuteAfter)
            .Select(a => new UpgradeActionRow(
                a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status,
                a.RequestedBy, a.RequestedAt, a.ExecuteAfter, a.SentAt, a.Outcome,
                a.CancelledBy, a.CancelledAt, a.TargetVersion,
                a.UpgradeId, a.Upgrade != null ? a.Upgrade.Name : null, a.PackageFileName))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    // ── Shared with the worker ──────────────────────────────────────────

    /// <summary>
    /// Performs one action against Business Central through the Stage 3 writes, which
    /// re-read the environment live, re-validate, re-mirror the cached row and record the
    /// audit entry. Used by both the immediate path here and
    /// <see cref="UpgradeActionWorker"/>, so a slot fired tonight does exactly what
    /// pressing the button would have done.
    /// </summary>
    internal Task RunAsync(
        int projectId, int environmentId, UpgradeActionKind kind, string? targetVersion, CancellationToken ct) =>
        kind switch
        {
            UpgradeActionKind.PushDateToLatest => _connections.PushUpdateDateToLatestAsync(projectId, environmentId, ct),
            UpgradeActionKind.RunNow => _connections.RunUpdateNowAsync(projectId, environmentId, ct),
            // SelectTargetVersionAsync re-reads the offer list live and refuses a version
            // no longer offered, so a mirror a sweep old can't send a stale pick.
            UpgradeActionKind.SelectVersion => _connections.SelectTargetVersionAsync(
                projectId, environmentId,
                targetVersion ?? throw new InvalidOperationException("A version change was run without a target version."),
                ct),
            _ => throw new InvalidOperationException($"{kind} is recorded, never run."),
        };

    /// <summary>
    /// Sends a booked upload (<see cref="UpgradeActionKind.UploadApp"/> with a package on
    /// the row) when its slot arrives. Only the worker calls this: an upload asked for
    /// there and then never becomes a booking, and goes through
    /// <see cref="ProjectConnectionService.InstallUploadedAppAsync"/> whole.
    /// </summary>
    internal Task<BcAppOperation> RunUploadAsync(
        int projectId, int environmentId, string fileName, byte[] package, CancellationToken ct) =>
        _connections.SendBookedUploadAsync(projectId, environmentId, package, fileName, ct);

    /// <summary>
    /// The kinds this service runs, and the version rule between them: a version change
    /// must say which version, and the date moves must not (they act on the update
    /// already chosen, so a version on them would be a claim the history can't honour).
    /// Returns the version trimmed, or null.
    /// </summary>
    private static string? ValidateKind(UpgradeActionKind kind, string? targetVersion)
    {
        var version = string.IsNullOrWhiteSpace(targetVersion) ? null : targetVersion.Trim();
        switch (kind)
        {
            case UpgradeActionKind.SelectVersion when version is null:
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["TargetVersion"] = "Choose the version to update to.",
                });
            case UpgradeActionKind.SelectVersion when version.Length > 32:
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["TargetVersion"] = "That isn't a Business Central version.",
                });
            case UpgradeActionKind.SelectVersion:
                return version;
            case UpgradeActionKind.PushDateToLatest or UpgradeActionKind.RunNow when version is not null:
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["TargetVersion"] = "Only a version change takes a version; moving or starting an update acts on the one already chosen.",
                });
            case UpgradeActionKind.PushDateToLatest or UpgradeActionKind.RunNow:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind,
                    "Only moving the date, starting the update and changing the version can be requested here.");
        }
    }

    private async Task<string?> EnvironmentNameAsync(int environmentId, CancellationToken ct) =>
        await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// What the feed says about an action that worked. Plain words; the date moves name no
    /// version because they act on whichever is chosen, while a version change says which
    /// version it set, since that is the whole of what it did.
    /// </summary>
    internal static string SuccessOutcome(UpgradeActionKind kind, string? targetVersion = null, string? environmentName = null, string? fileName = null) =>
        kind switch
        {
            UpgradeActionKind.PushDateToLatest => "The update date was moved out to the latest Business Central allows.",
            UpgradeActionKind.UploadApp => environmentName is { Length: > 0 }
                ? $"{fileName} was sent to {environmentName}, and Business Central started installing it."
                : $"{fileName} was sent, and Business Central started installing it.",
            UpgradeActionKind.SelectVersion => environmentName is { Length: > 0 }
                ? $"Set the next version to {targetVersion} on {environmentName}."
                : $"Set the next version to {targetVersion}.",
            _ => "Business Central was told to start the update, ignoring the environment's update window.",
        };

    /// <summary>
    /// What the feed says about an action that didn't work. The feed is read days later,
    /// so it has to be written in the past tense: the refusals themselves are worded for
    /// somebody standing at the form ("...right now. Try again once it finishes."), and
    /// left as they are they would claim, a week on, that an environment is still busy.
    ///
    /// <para>So the entry leads with what did not happen, and the refusal follows as what
    /// we were told <em>at the time</em> — which stays true however long ago it was.
    /// Advice aimed at the person standing at the form is dropped for the same reason:
    /// "try again once it finishes" is an instruction, and a history entry is not the
    /// place to be given one.</para>
    /// </summary>
    internal static string FailureOutcome(UpgradeActionKind kind, string reason, string? targetVersion = null, string? fileName = null)
    {
        var lead = kind switch
        {
            UpgradeActionKind.PushDateToLatest => "The update date wasn't moved.",
            UpgradeActionKind.UploadApp => $"{fileName} wasn't installed.",
            UpgradeActionKind.SelectVersion => $"The next version wasn't changed to {targetVersion}.",
            _ => "The update didn't start.",
        };
        var trimmed = WithoutLiveAdvice(reason).Trim();
        return trimmed.Length == 0 ? lead : $"{lead} Reason given at the time: {trimmed}";
    }

    /// <summary>
    /// Drops a trailing "Try again ..." / "Reopen ..." / "Re-enter ..." sentence from a
    /// refusal. Those are things to do <em>now</em>, and the feed is a record of what
    /// happened. Only a whole trailing sentence is removed, so a refusal that never had
    /// one comes back untouched.
    /// </summary>
    private static string WithoutLiveAdvice(string reason)
    {
        var sentences = reason.Split(". ", StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length < 2) return reason;

        var last = sentences[^1].TrimStart();
        var isAdvice = last.StartsWith("Try again", StringComparison.OrdinalIgnoreCase)
            || last.StartsWith("Reopen", StringComparison.OrdinalIgnoreCase)
            || last.StartsWith("Re-enter", StringComparison.OrdinalIgnoreCase)
            || last.StartsWith("Refresh", StringComparison.OrdinalIgnoreCase);
        if (!isAdvice) return reason;

        return string.Join(". ", sentences[..^1]).TrimEnd() is var kept && kept.EndsWith('.') ? kept : kept + ".";
    }

    private static string AlreadyOverMessage(UpgradeActionStatus status) => status switch
    {
        UpgradeActionStatus.Cancelled => "This action was already cancelled.",
        UpgradeActionStatus.Sent => "This action has already run, so there is nothing left to cancel.",
        _ => "This action has already finished, so there is nothing left to cancel.",
    };
}

/// <summary>
/// One entry in an environment's activity feed: what was asked for, by whom, when it is
/// due or was sent, and how it went.
/// </summary>
/// <param name="UpgradeId">The planned upgrade the action was run from; null for an ad hoc one (issue #984).</param>
/// <param name="UpgradeName">That upgrade's name ("28.5 in November 2026"), so the history can say which wave; null with it.</param>
public sealed record UpgradeActionRow(
    int Id,
    int ProjectId,
    int EnvironmentId,
    UpgradeActionKind Kind,
    UpgradeActionStatus Status,
    string RequestedBy,
    DateTime RequestedAt,
    DateTime ExecuteAfter,
    DateTime? SentAt,
    string? Outcome,
    string? CancelledBy,
    DateTime? CancelledAt,
    string? TargetVersion = null,
    int? UpgradeId = null,
    string? UpgradeName = null,
    /// <summary>The file a booked or sent upload carried, so the feed can name it. Null for every other kind.</summary>
    string? PackageFileName = null)
{
    /// <summary>True while the action is still waiting for its slot — the only state with a Cancel.</summary>
    public bool IsPending => Status == UpgradeActionStatus.Pending;

    /// <summary>
    /// True when somebody booked this for a later slot rather than running it there and
    /// then. An immediate action fires when it is asked for, so its two timestamps match;
    /// a booking's fire time is deliberately later. The tolerance absorbs the moment
    /// between composing the row and saving it.
    ///
    /// <para>The distinction is what an entry's headline turns on: "Booked the update"
    /// and "Started the update" are opposite claims, and a booking that has been
    /// cancelled must still read as a booking.</para>
    /// </summary>
    public bool IsBooking => ExecuteAfter > RequestedAt.AddSeconds(30);

    /// <summary>
    /// The requester's name on its own. <see cref="RequestedBy"/> is the audit log's
    /// <c>"name &lt;email&gt;"</c> form, which is right for a log and noisy in a sentence
    /// somebody reads; the email goes on the tooltip instead.
    /// </summary>
    public string RequestedByName => NameOf(RequestedBy);

    /// <summary>The requester's email, or null when the stored string carries none.</summary>
    public string? RequestedByEmail => EmailOf(RequestedBy);

    /// <summary>The canceller's name on its own, or null when nobody cancelled this.</summary>
    public string? CancelledByName => CancelledBy is { Length: > 0 } who ? NameOf(who) : null;

    /// <summary>The canceller's email, or null.</summary>
    public string? CancelledByEmail => CancelledBy is { Length: > 0 } who ? EmailOf(who) : null;

    private static string NameOf(string actor)
    {
        var bracket = actor.IndexOf(" <", StringComparison.Ordinal);
        return bracket > 0 ? actor[..bracket] : actor;
    }

    private static string? EmailOf(string actor)
    {
        var bracket = actor.IndexOf(" <", StringComparison.Ordinal);
        if (bracket < 0 || !actor.EndsWith('>')) return null;
        return actor[(bracket + 2)..^1];
    }

    internal static UpgradeActionRow From(OeEnvironmentUpgradeAction a) => new(
        a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.Status,
        a.RequestedBy, a.RequestedAt, a.ExecuteAfter, a.SentAt, a.Outcome,
        a.CancelledBy, a.CancelledAt, a.TargetVersion, a.UpgradeId, a.Upgrade?.Name);
}
