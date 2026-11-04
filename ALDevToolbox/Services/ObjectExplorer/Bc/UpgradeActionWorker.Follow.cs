using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Following a started platform update to its end (#1711). Business Central accepting
/// "start the update" says nothing about how the update goes: that is a separate
/// operation, created by Microsoft's scheduler only once it picks the update up, with no
/// link back to our request. Without this, a failure was only seen by somebody who had
/// the Upgrades page open at the time. See <c>.design/environment-updates.md</c>,
/// "Following a started update".
/// </summary>
public sealed partial class UpgradeActionWorker
{
    /// <summary>How long a start is followed. Past it the row is left as it is, untouched and unannounced.</summary>
    internal static readonly TimeSpan FollowFor = TimeSpan.FromDays(2);

    /// <summary>A start with no update operation after this long was never picked up, and is reported as such.</summary>
    internal static readonly TimeSpan NeverStartedAfter = TimeSpan.FromHours(24);

    /// <summary>How often one start's environment is asked about: an update takes tens of minutes, not seconds.</summary>
    internal static readonly TimeSpan FollowEvery = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Slack on "created after we sent it", for the gap between our clock and Microsoft's.
    /// Small, because the operation is only created once the scheduler picks the update up.
    /// </summary>
    internal static readonly TimeSpan OperationClockSlack = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after a finished update the next update's date is moved out. Just after the
    /// update, Business Central's own <c>SYSTEM</c> write sets the next version and its
    /// date; moving it before that would move nothing, or be overwritten.
    /// </summary>
    internal static readonly TimeSpan DateMoveAfterUpdate = TimeSpan.FromMinutes(5);

    /// <summary>The most starts one organisation has asked about per sweep, so a fleet-wide wave spreads over sweeps.</summary>
    internal const int FollowPerSweep = 20;

    /// <summary>
    /// One pass for one organisation: every sent start whose update has not been seen to
    /// end, not asked about in the last <see cref="FollowEvery"/>, is checked against its
    /// environment's operations. Internal so a test can drive one pass. Returns how many
    /// were asked about.
    /// </summary>
    internal async Task<int> FollowStartedUpdatesAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        List<FollowedStart> due;
        using (AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem)))
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var since = now - FollowFor;
            var askedBefore = now - FollowEvery;
            due = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.Kind == UpgradeActionKind.RunNow && a.Status == UpgradeActionStatus.Sent
                            && a.UpdateResult == null && a.SentAt != null && a.SentAt >= since
                            && (a.UpdateCheckedAt == null || a.UpdateCheckedAt <= askedBefore))
                .OrderBy(a => a.UpdateCheckedAt ?? DateTime.MinValue)
                .ThenBy(a => a.Id)
                .Take(FollowPerSweep)
                .Select(a => new FollowedStart(a.Id, a.ProjectId, a.EnvironmentId, a.RequestedByUserId, a.TargetVersion, a.SentAt!.Value,
                    // The next start sent on the same environment, if any: an update that
                    // began after it is that start's, not this one's.
                    db.OeEnvironmentUpgradeActions
                        .Where(n => n.EnvironmentId == a.EnvironmentId && n.Kind == UpgradeActionKind.RunNow
                                    && n.SentAt != null && n.SentAt > a.SentAt && n.Id != a.Id)
                        .Min(n => n.SentAt)))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        foreach (var start in due)
        {
            _heartbeat.Tick();
            await FollowOneAsync(orgId, isSystem, start, now, ct).ConfigureAwait(false);
        }
        return due.Count;
    }

    private async Task FollowOneAsync(int orgId, bool isSystem, FollowedStart start, DateTime now, CancellationToken ct)
    {
        // As the person who started it, as the start was: the read is gated on seeing the solution.
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem, start.RequestedByUserId));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unsettled = db.OeEnvironmentUpgradeActions.Where(a => a.Id == start.Id && a.UpdateResult == null);

        BcEnvironmentOperation? operation = null;
        var read = false;
        try
        {
            if (await IsRequesterActiveAsync(db, start.RequestedByUserId, ct).ConfigureAwait(false))
            {
                var connections = scope.ServiceProvider.GetRequiredService<ProjectConnectionService>();
                var operations = await connections.ListEnvironmentOperationsAsync(start.ProjectId, start.EnvironmentId, ct)
                    .ConfigureAwait(false);
                operation = MatchUpdateOperation(operations, start.SentAt, start.TargetVersion, start.NextStartAt);
                read = true;
            }
            else
            {
                _logger.LogInformation(
                    "Not following the update started by action {ActionId}: the person who started it no longer has an active account.",
                    start.Id);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is PlanValidationException or ProjectAccessDeniedException)
        {
            // The connection or the person's access is gone. That says nothing about the
            // update, so nothing is recorded; the row is asked about again later.
            _logger.LogInformation("Couldn't follow the update started by action {ActionId}: {Reason}",
                start.Id, ex is PlanValidationException pve ? pve.Errors.Values.FirstOrDefault() : "no access");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Following the update started by action {ActionId} on environment {EnvironmentId} threw.",
                start.Id, start.EnvironmentId);
        }

        var ending = Settle(operation, read, start, now);
        if (ending is null)
        {
            await unsettled.ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdateCheckedAt, now), ct).ConfigureAwait(false);
            return;
        }

        var (result, status, outcome) = ending.Value;

        // Re-read the environment so its version and next update say what the update did,
        // which is what turns the upgrade's line Updated and tells whoever checks it. A
        // success is only recorded once that read has worked: recorded without it, the
        // line would stop reading Booked while the mirror still showed the old version,
        // and read as if nothing had happened. A failure is recorded either way.
        if (result != PlatformUpdateResult.Superseded)
        {
            var refreshed = false;
            try
            {
                await scope.ServiceProvider.GetRequiredService<ProjectConnectionService>()
                    .RefreshEnvironmentAsync(start.ProjectId, start.EnvironmentId, ct).ConfigureAwait(false);
                refreshed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogInformation("Couldn't re-read environment {EnvironmentId} after its update ended: {Message}",
                    start.EnvironmentId, ex.Message);
            }
            if (!refreshed && result == PlatformUpdateResult.Succeeded)
            {
                await unsettled.ExecuteUpdateAsync(s => s.SetProperty(a => a.UpdateCheckedAt, now), ct).ConfigureAwait(false);
                return;
            }
        }

        Guid? operationId = Guid.TryParse(operation?.Id, out var parsed) ? parsed : null;
        var settled = await unsettled.ExecuteUpdateAsync(s => s
            .SetProperty(a => a.UpdateResult, result)
            .SetProperty(a => a.UpdateCheckedAt, now)
            .SetProperty(a => a.Status, status)
            .SetProperty(a => a.Outcome, outcome)
            .SetProperty(a => a.BcOperationId, a => operationId ?? a.BcOperationId), ct).ConfigureAwait(false);
        if (settled == 0) return;

        _logger.LogInformation("The update started by action {ActionId} on environment {EnvironmentId} ended: {Result}.",
            start.Id, start.EnvironmentId, result);

        if (result is PlatformUpdateResult.Failed or PlatformUpdateResult.NeverStarted)
        {
            await scope.ServiceProvider.GetRequiredService<Notifications.UpgradeActionNotifier>()
                .NotifyUpdateEndedAsync(start.Id, CancellationToken.None).ConfigureAwait(false);
        }
        else if (result == PlatformUpdateResult.Succeeded)
        {
            // The ending is already recorded, so a failed booking can't be retried by
            // following again; it is logged, and the rest of the sweep carries on.
            try
            {
                await BookDateMoveAsync(db, orgId, start, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Couldn't book the date move on environment {EnvironmentId} after the update started by action {ActionId} finished.",
                    start.EnvironmentId, start.Id);
            }
        }
    }

    /// <summary>
    /// Books the next update's date out to the latest Business Central allows, a little
    /// after an update started from a planned upgrade has finished, when the upgrade asks
    /// for it (<see cref="OeEnvironmentUpgrade.MoveDateAfterUpdate"/>, on by default): the
    /// date move the ops team otherwise does by hand once a customer is updated. An ordinary
    /// pending <see cref="UpgradeActionKind.PushDateToLatest"/> row, marked
    /// <see cref="OeEnvironmentUpgradeAction.Automatic"/>, so it fires through the same path
    /// as a booked one - as the person who started the update, with the grant re-checked
    /// then - shows in the history, and can be cancelled until it runs. Not booked when
    /// something else is already waiting on the environment (whoever booked that has a
    /// plan), or when the update ended short of the upgrade's release.
    /// </summary>
    private async Task BookDateMoveAsync(AppDbContext db, int orgId, FollowedStart start, DateTime now, CancellationToken ct)
    {
        var started = await db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == start.Id && a.Upgrade != null && a.Upgrade.ClosedAt == null && a.Upgrade.MoveDateAfterUpdate)
            .Select(a => new { a.RequestedBy, a.UpgradeId, a.Upgrade!.TargetVersion })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (started is null) return;

        // An update that stopped short of the upgrade's release (28.3 on the way to 28.5)
        // leaves the target as the next update; moving that out would undo the plan.
        var version = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == start.EnvironmentId)
            .Select(e => e.Version)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (!EnvironmentUpgradeLineState.IsOnTarget(version, started.TargetVersion)) return;

        var waiting = await db.OeEnvironmentUpgradeActions.AsNoTracking()
            .AnyAsync(a => a.EnvironmentId == start.EnvironmentId && a.Status == UpgradeActionStatus.Pending, ct)
            .ConfigureAwait(false);
        if (waiting) return;

        db.OeEnvironmentUpgradeActions.Add(new OeEnvironmentUpgradeAction
        {
            OrganizationId = orgId,
            ProjectId = start.ProjectId,
            EnvironmentId = start.EnvironmentId,
            Kind = UpgradeActionKind.PushDateToLatest,
            Status = UpgradeActionStatus.Pending,
            Automatic = true,
            UpgradeId = started.UpgradeId,
            RequestedByUserId = start.RequestedByUserId,
            RequestedBy = started.RequestedBy,
            RequestedAt = now,
            ExecuteAfter = now + DateMoveAfterUpdate,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Booked the next update's date on environment {EnvironmentId} out to the latest allowed, after the update started by action {ActionId}.",
            start.EnvironmentId, start.Id);
    }

    /// <summary>
    /// What one look at the operations settles, or null when the update is still to end:
    /// no operation yet (and the day not up), one still running, or no answer at all.
    /// </summary>
    internal static (PlatformUpdateResult Result, UpgradeActionStatus Status, string Outcome)? Settle(
        BcEnvironmentOperation? operation, bool read, FollowedStart start, DateTime now)
    {
        if (operation is null)
        {
            // Only an answer that found nothing counts: failed reads say nothing about the update.
            if (!read) return null;
            // Started again before Business Central began this one: whatever runs now is
            // the later start's, and that one is followed and reported in its own right.
            if (start.NextStartAt is not null)
            {
                return (PlatformUpdateResult.Superseded, UpgradeActionStatus.Sent,
                    "Started again before Business Central began the update, so the later start is the one followed.");
            }
            if (now - start.SentAt < NeverStartedAfter) return null;
            return (PlatformUpdateResult.NeverStarted, UpgradeActionStatus.Failed,
                $"Business Central accepted the start, but hadn't begun the update{To(start.TargetVersion)} a day later. "
                + "Check the environment's update in the admin centre, then start it again if it is still waiting.");
        }

        var to = To(UpdateOperationVersion(operation) ?? start.TargetVersion);
        // Only the statuses known to be final settle anything. Any other - running,
        // queued, scheduled, or one Microsoft adds later - is read as still going: a live
        // update reported as failed would be worse than one reported late.
        switch (operation.Status?.Trim().ToLowerInvariant())
        {
            case "succeeded":
                return (PlatformUpdateResult.Succeeded, UpgradeActionStatus.Sent,
                    $"Business Central finished the update{to}.");
            case "canceled" or "cancelled":
                return (PlatformUpdateResult.Failed, UpgradeActionStatus.Failed, $"Business Central cancelled the update{to}.");
            case "skipped":
                return (PlatformUpdateResult.Failed, UpgradeActionStatus.Failed, $"Business Central skipped the update{to}.");
            case "failed":
                var reason = string.IsNullOrWhiteSpace(operation.ErrorMessage) ? null : operation.ErrorMessage.Trim();
                return (PlatformUpdateResult.Failed, UpgradeActionStatus.Failed, reason is null
                    ? $"The update{to} failed in Business Central. See the environment's operations for details."
                    : $"The update{to} failed in Business Central. Reason given at the time: {reason}");
            default:
                return null;
        }
    }

    private static string To(string? version) => string.IsNullOrWhiteSpace(version) ? string.Empty : $" to {version}";

    /// <summary>
    /// The update operation a start set off, out of the environment's operations: an
    /// <c>Update</c> going to the version that was started, that began after the start was
    /// sent (less <see cref="OperationClockSlack"/>) - created then, or started then, which
    /// covers an update Business Central had already queued before the start pulled it
    /// forward - and before the next start on the environment, if there is one; the
    /// earliest such. Without a next start, an update still waiting to begin counts
    /// whenever it was created. One update runs on an environment at a time, so that is
    /// specific enough without an id to match on. An operation that names no version is
    /// taken on time alone, and so is every one when the start recorded no version (an ad
    /// hoc start from the fleet table).
    /// </summary>
    internal static BcEnvironmentOperation? MatchUpdateOperation(
        IEnumerable<BcEnvironmentOperation> operations, DateTime sentAtUtc, string? targetVersion, DateTime? nextStartUtc = null)
    {
        var after = Utc(sentAtUtc) - OperationClockSlack;
        DateTimeOffset? before = nextStartUtc is { } next ? Utc(next) - OperationClockSlack : null;
        var target = string.IsNullOrWhiteSpace(targetVersion) ? null : UpgradeFleetService.MajorMinor(targetVersion);
        return operations
            .Where(o => string.Equals(o.Type?.Trim(), "Update", StringComparison.OrdinalIgnoreCase))
            .Where(o => target is null || UpdateOperationVersion(o) is not { } version
                        || ProjectConnectionService.CompareVersions(UpgradeFleetService.MajorMinor(version), target) == 0)
            .Select(o => (Operation: o, Began: Began(o, after)))
            .Where(o => o.Began is { } began
                ? began >= after && (before is null || began < before)
                : before is null && o.Operation.StartedOn is null && BcEnvironmentOperationDisplay.IsOpen(o.Operation))
            .OrderBy(o => o.Began ?? DateTimeOffset.MaxValue)
            .Select(o => o.Operation)
            .FirstOrDefault();

        static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        // When the operation began for our purposes: when it was created, if that was
        // after the start; else when it started running. Null for one created earlier
        // that hasn't started, which only an open, unstarted update can match.
        static DateTimeOffset? Began(BcEnvironmentOperation o, DateTimeOffset after) =>
            o.CreatedOn is { } created && created >= after ? created : o.StartedOn;
    }

    /// <summary>
    /// The version an update operation goes to. Microsoft documents no fixed name for it,
    /// so the names seen on update operations are tried in turn; null when none is there.
    /// </summary>
    internal static string? UpdateOperationVersion(BcEnvironmentOperation operation) =>
        new[] { "destinationVersion", "targetVersion", "toVersion", "newVersion" }
            .Select(operation.Parameter)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>A sent start still being followed, read outside the scope that asks about it.</summary>
    internal sealed record FollowedStart(
        int Id, int ProjectId, int EnvironmentId, int? RequestedByUserId, string? TargetVersion, DateTime SentAt,
        DateTime? NextStartAt = null);
}
