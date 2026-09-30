using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using Microsoft.EntityFrameworkCore;
using ALDevToolbox.Services.Workers;

namespace ALDevToolbox.Services.ObjectExplorer.Bc;

/// <summary>
/// Fires the upgrade actions the team booked for a later slot — "tonight at 20:00",
/// agreed with the customer that morning. See <c>.design/saas-delivery.md</c> and issue
/// #657.
///
/// <para><b>It polls the table rather than draining a channel.</b> Every other worker
/// here reads an in-process queue, and that is right for work handed over seconds ago.
/// A slot booked for tonight has to survive this afternoon's deploy, so the pending row
/// in Postgres <em>is</em> the queue and this sweeps it every
/// <see cref="PollInterval"/>.</para>
///
/// <para><b>Each row runs as the person who asked for it.</b> The ambient org scope
/// carries the requester's user id (the <c>DeliveryWorker</c> precedent), which buys two
/// things for free: the audit row names them rather than "unknown", and the
/// environment-updates grant is re-checked at fire time — somebody taken off the upgrade
/// team during the afternoon does not get their evening slot fired anyway.</para>
///
/// <para>One row's failure never stops the sweep: a customer whose credentials expired
/// lands as failed with the reason in the feed, and the next environment is tried.</para>
/// </summary>
public sealed class UpgradeActionWorker : BackgroundService
{
    /// <summary>How often the table is swept for due rows. A slot is a time of day, not a stopwatch.</summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgradeActionWorker> _logger;
    private readonly WorkerHeartbeat _heartbeat;

    public UpgradeActionWorker(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<UpgradeActionWorker> logger,
        WorkerHeartbeatRegistry heartbeats)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        // Polls every 30 seconds and is idle most of the time. A sweep sends at most one
        // booked app install (an upload or an AppSource update) in all - not per
        // organisation - and waits for it, so
        // the active budget is one install plus the rest of a sweep; a batch spreads
        // over as many sweeps as it has apps. The heartbeat is ticked on every poll of
        // that install (see RunOneAsync), so the idle ceiling stays the usual few
        // minutes and still catches a loop that has stopped.
        _heartbeat = heartbeats.Register(nameof(UpgradeActionWorker),
            maxActiveDuration: ProjectConnectionService.DefaultUploadPollTimeout + SweepSlack,
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

    /// <summary>What a sweep may spend beyond one install: the platform-update rows of every organisation, and the reads around them.</summary>
    private static readonly TimeSpan SweepSlack = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long past an install's wait a claimed-but-unsettled predecessor is still
    /// believed to be mid-install before its batch stops waiting on it.
    /// </summary>
    private static readonly TimeSpan StaleClaimSlack = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let startup migrations + seed finish before the first sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var recovered = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            _heartbeat.Tick();
            try
            {
                _heartbeat.BeginActive();
                try
                {
                    if (!recovered)
                    {
                        // Nothing of ours is running yet, so anything found mid-send was
                        // orphaned by a restart. Once only.
                        await ForEachOrgAsync(FailInterruptedAsync, stoppingToken).ConfigureAwait(false);
                        recovered = true;
                    }
                    // Two passes. Every organisation's platform-update moves first - a
                    // date the customer agreed, seconds each - then one booked app install
                    // in all (an upload or an AppSource update), which waits for it to
                    // finish. So no organisation's agreed slot waits behind another's
                    // ten-minute install, a sweep costs at most one install, and two
                    // installs never run at once: that can deadlock on Business Central's
                    // own bookkeeping table.
                    await ForEachOrgAsync((org, sys, ct) => RunDueActionsAsync(org, sys, ct, SweepPass.PlatformUpdates), stoppingToken).ConfigureAwait(false);
                    var uploadSent = false;
                    await ForEachOrgAsync(async (org, sys, ct) =>
                    {
                        if (uploadSent) return;
                        uploadSent = await RunDueActionsAsync(org, sys, ct, SweepPass.OneUpload).ConfigureAwait(false) > 0;
                    }, stoppingToken).ConfigureAwait(false);
                }
                finally { _heartbeat.EndActive(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpgradeActionWorker sweep threw; will retry on the next poll.");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Runs <paramref name="perOrg"/> once per active organisation, inside that org's
    /// <see cref="AmbientOrganizationScope"/> so the EF query filter behaves exactly as
    /// it would in a request. The active-org enumeration is the one cross-org read; the
    /// organisations table carries no tenant filter, so it needs no bypass.
    /// </summary>
    private async Task ForEachOrgAsync(Func<int, bool, CancellationToken, Task> perOrg, CancellationToken ct)
    {
        // IsSystem travels with the id so the per-org identity carries the org's real
        // flag rather than a hard-coded false (issue #694).
        List<(int Id, bool IsSystem)> orgs;
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            // The org enumeration; per-org work then runs inside that org's
            // AmbientOrganizationScope.
            var rows = await db.Organizations.AsNoTracking()
                .Where(o => !o.IsPending)
                .Select(o => new { o.Id, o.IsSystem })
                .ToListAsync(ct).ConfigureAwait(false);
            orgs = rows.Select(o => (o.Id, o.IsSystem)).ToList();
        }

        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                await perOrg(orgId, isSystem, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpgradeActionWorker failed for org {OrgId}.", orgId);
            }
        }
    }

    /// <summary>Which of a sweep's two passes a call makes; <see cref="Both"/> is the one-call form a test drives.</summary>
    internal enum SweepPass { PlatformUpdates, OneUpload, Both }

    /// <summary>
    /// Fires the due rows of one org: every platform-update move, then at most one app
    /// install - an upload or an AppSource update (the rest of a batch, or the org's
    /// other installs, wait for the next sweep). Internal so a test can drive one sweep against a seeded database without
    /// the hosted-service loop. Returns how many rows were sent; rows only settled on the
    /// way (a dependent skipped because the app before it failed) do not count, so the
    /// sweep does not stop on one.
    /// </summary>
    internal async Task<int> RunDueActionsAsync(int orgId, bool isSystem, CancellationToken ct, SweepPass pass = SweepPass.Both)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var uploads = pass != SweepPass.PlatformUpdates;
        var updates = pass != SweepPass.OneUpload;

        List<DueAction> due;
        using (AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem)))
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            due = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.Status == UpgradeActionStatus.Pending
                            && a.SentAt == null
                            && a.ExecuteAfter <= now)
                .Where(a => a.Kind == UpgradeActionKind.UploadApp || a.Kind == UpgradeActionKind.UpdateApp ? uploads : updates)
                // Platform-update moves first: they are a date the customer agreed and
                // take seconds, and must not queue behind an app install that takes minutes.
                .OrderBy(a => a.Kind == UpgradeActionKind.UploadApp || a.Kind == UpgradeActionKind.UpdateApp)
                .ThenBy(a => a.ExecuteAfter)
                .ThenBy(a => a.BatchOrder)
                .ThenBy(a => a.Id)
                .Select(a => new DueAction(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.RequestedByUserId, a.TargetVersion,
                    a.PackageFileName ?? a.AppName, a.BatchId, a.BatchOrder))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var sent = 0;
        foreach (var action in due)
        {
            if (ct.IsCancellationRequested) break;
            var outcome = await RunOneAsync(orgId, isSystem, action, ct).ConfigureAwait(false);
            if (outcome != RunOutcome.Sent) continue;
            sent++;
            // One app install per sweep: it waited for its install, up to ten minutes. A
            // batch resumes on the next sweep, where the gate below finds its predecessor
            // settled.
            if (UpgradeActionService.IsAppInstall(action.Kind)) break;
        }

        if (sent > 0)
        {
            _logger.LogInformation("UpgradeActionWorker ran {Count} scheduled upgrade action(s) for org {OrgId}.", sent, orgId);
        }
        return sent;
    }

    /// <summary>What one due row came to: left alone, settled without a send, or sent.</summary>
    private enum RunOutcome { Skipped, Settled, Sent }

    /// <summary>
    /// Claims one row, performs it, and writes down what happened. Skipped when the
    /// claim was lost — somebody cancelled it in the seconds between the sweep's read and
    /// this call, and their cancel stands — or when its turn in a batch has not come.
    /// </summary>
    private async Task<RunOutcome> RunOneAsync(int orgId, bool isSystem, DueAction action, CancellationToken ct)
    {
        // The requester is the actor: the audit row names them, and the grant is
        // re-checked as theirs at fire time.
        var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
            orgId, isSystem, action.RequestedByUserId);
        using var ambient = AmbientOrganizationScope.Enter(identity);
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // A row in a multi-app upload waits its turn: the one before it must have
        // finished installing (the sweep runs them in order, so within one sweep it
        // has), and if that one failed this one is not tried - its dependency may be the
        // very thing that did not go in. A cancelled predecessor is the person's choice
        // and does not stop the rest.
        if (action.BatchId is { } batch && action.BatchOrder is { } order)
        {
            var earlier = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.BatchId == batch && a.BatchOrder < order)
                .OrderBy(a => a.BatchOrder)
                .Select(a => new { a.Status, a.SentAt, a.PackageFileName })
                .ToListAsync(ct).ConfigureAwait(false);
            // A predecessor still pending is either waiting its turn or mid-install. One
            // claimed longer ago than an install can take is a row whose settling write
            // was lost; it is not waited on for ever - its dependents go, and Business
            // Central refuses them by name if the app it needed is not there.
            var staleBefore = _clock.GetUtcNow().UtcDateTime - (ProjectConnectionService.DefaultUploadPollTimeout + StaleClaimSlack);
            if (earlier.Any(e => e.Status == UpgradeActionStatus.Pending && (e.SentAt == null || e.SentAt > staleBefore)))
            {
                return RunOutcome.Skipped;
            }
            if (earlier.FirstOrDefault(e => e.Status == UpgradeActionStatus.Failed) is { } blocked)
            {
                _logger.LogInformation(
                    "Upload booking {ActionId} ({FileName}) is not tried: {Blocked} before it in the same batch failed.",
                    action.Id, action.Label, blocked.PackageFileName);
                var skippedAt = _clock.GetUtcNow().UtcDateTime;
                var skipped = await db.OeEnvironmentUpgradeActions
                    .Where(a => a.Id == action.Id && a.Status == UpgradeActionStatus.Pending && a.SentAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                        .SetProperty(a => a.SentAt, skippedAt)
                        .SetProperty(a => a.PackageContent, (byte[]?)null)
                        .SetProperty(a => a.Outcome,
                            $"{action.Label} wasn't installed, because {blocked.PackageFileName} before it in the same upload didn't install."),
                        ct).ConfigureAwait(false);
                return skipped > 0 ? RunOutcome.Settled : RunOutcome.Skipped;
            }
        }

        // Claim first, and this compare-and-set is what beats a racing cancel: stamping
        // sent_at while the row is still pending takes it out of both the due query and
        // the cancel's own WHERE clause, so exactly one of the two wins.
        var claimedAt = _clock.GetUtcNow().UtcDateTime;
        var claimed = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == action.Id
                        && a.Status == UpgradeActionStatus.Pending
                        && a.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.SentAt, claimedAt), ct).ConfigureAwait(false);
        if (claimed == 0)
        {
            _logger.LogInformation(
                "Upgrade action {ActionId} was cancelled before it could run; leaving it alone.", action.Id);
            return RunOutcome.Skipped;
        }

        UpgradeActionStatus status;
        string outcome;
        // Read before the send, so nothing after a write that landed can turn it into a failure.
        var environmentName = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == action.EnvironmentId)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        try
        {
            var actions = scope.ServiceProvider.GetRequiredService<UpgradeActionService>();
            if (action.Kind == UpgradeActionKind.UploadApp)
            {
                // The package is read here, not in the sweep: fifty megabytes per booking
                // has no place in a list of what is due.
                var package = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                    .Where(a => a.Id == action.Id)
                    .Select(a => a.PackageContent)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                if (package is null || package.Length == 0 || action.Label is null)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["App"] = "The app file was no longer stored with the booking.",
                    });
                }
                // The operation is stamped on the row as soon as Business Central accepts
                // the package, so a restart during the poll can tell "installing without
                // us" from "never sent" - see FailInterruptedAsync. The stamp must never
                // turn a live install into a failure: a lost write is logged and the poll
                // goes on, and a shutdown between acceptance and the stamp still stamps.
                var result = await actions.RunUploadAsync(action.ProjectId, action.EnvironmentId, action.Label, package,
                    (operation, _) => StampOperationAsync(db, action.Id, operation),
                    ct, progress: _heartbeat.Tick).ConfigureAwait(false);
                if (result.Completed)
                {
                    status = UpgradeActionStatus.Sent;
                    outcome = UpgradeActionService.SuccessOutcome(action.Kind, action.TargetVersion, environmentName, action.Label);
                }
                else if (result.IsUnconfirmed)
                {
                    // Business Central has the app and was installing it; only the answer
                    // is missing. Recorded as sent, as a restart records it, so the rest
                    // of the batch goes on - a dependent that needed it is refused by
                    // Business Central if it did not land.
                    status = UpgradeActionStatus.Sent;
                    outcome = $"{action.Label} was uploaded. {result.Message}";
                }
                else
                {
                    // Business Central took the file and then reported the install failed,
                    // in its own words.
                    status = UpgradeActionStatus.Failed;
                    outcome = UpgradeActionService.FailureOutcome(action.Kind,
                        result.Message ?? "Business Central didn't finish the install.", action.TargetVersion, action.Label);
                }
            }
            else if (action.Kind == UpgradeActionKind.UpdateApp)
            {
                // What the person agreed to at booking time; the send re-reads the waiting
                // updates and refuses if Business Central has moved on since.
                var booked = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                    .Where(a => a.Id == action.Id)
                    .Select(a => new { a.BcAppId, a.PrerequisiteAppIds })
                    .FirstAsync(ct).ConfigureAwait(false);
                if (booked.BcAppId is not { } appId || action.TargetVersion is null)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["App"] = "The booking no longer said which app and version to update to.",
                    });
                }
                // The operation is stamped as for an upload, so a restart mid-update
                // records it as sent, unconfirmed, rather than never sent.
                var result = await actions.RunUpdateAsync(action.ProjectId, action.EnvironmentId, appId, action.TargetVersion,
                    booked.PrerequisiteAppIds ?? new List<Guid>(),
                    (operation, _) => StampOperationAsync(db, action.Id, operation),
                    ct, progress: _heartbeat.Tick).ConfigureAwait(false);
                (status, outcome) = result.Completed
                    ? (UpgradeActionStatus.Sent, UpgradeActionService.SuccessOutcome(action.Kind, action.TargetVersion, environmentName, action.Label))
                    : result.IsUnconfirmed
                        ? (UpgradeActionStatus.Sent, $"{action.Label} was sent for update to {action.TargetVersion}. {result.Message}")
                        : (UpgradeActionStatus.Failed, UpgradeActionService.FailureOutcome(action.Kind,
                            result.Message ?? "Business Central didn't finish the update.", action.TargetVersion, action.Label));
            }
            else
            {
                await actions.RunAsync(action.ProjectId, action.EnvironmentId, action.Kind, action.TargetVersion, ct).ConfigureAwait(false);
                status = UpgradeActionStatus.Sent;
                outcome = UpgradeActionService.SuccessOutcome(action.Kind, action.TargetVersion, environmentName, action.Label);
            }
        }
        catch (PlanValidationException ex)
        {
            // The live re-validation the Stage 3 writes do: the update was applied or
            // withdrawn during the afternoon, the environment is busy, the credentials
            // were rotated. All already in plain words.
            status = UpgradeActionStatus.Failed;
            outcome = UpgradeActionService.FailureOutcome(action.Kind,
                ex.Errors.Values.FirstOrDefault() ?? "Business Central refused the change.", action.TargetVersion, action.Label);
        }
        catch (ProjectAccessDeniedException)
        {
            status = UpgradeActionStatus.Failed;
            outcome = action.Kind switch
            {
                UpgradeActionKind.UploadApp => "The person who booked this no longer had permission to manage this customer, so the app wasn't installed.",
                UpgradeActionKind.UpdateApp => "The person who booked this no longer had permission to manage this customer, so the app wasn't updated.",
                _ => "The person who booked this no longer had permission to change this customer's update dates, so it wasn't run.",
            };
        }
        catch (Exception ex)
        {
            // Never raw exception text in the feed — the detail goes to the log.
            _logger.LogError(ex,
                "Upgrade action {ActionId} on environment {EnvironmentId} (project {ProjectId}) threw.",
                action.Id, action.EnvironmentId, action.ProjectId);
            status = UpgradeActionStatus.Failed;
            outcome = UpgradeActionService.FailureOutcome(action.Kind, "Business Central didn't accept the change.", action.TargetVersion, action.Label);
        }

        // The package goes with the outcome, whichever way it went: a sent one is with
        // Business Central now, and a failed one is not retried (see FailInterruptedAsync).
        var finishedAt = _clock.GetUtcNow().UtcDateTime;
        await db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == action.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, status)
                .SetProperty(a => a.Outcome, outcome)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.SentAt, finishedAt), ct).ConfigureAwait(false);
        return RunOutcome.Sent;
    }

    /// <summary>
    /// Records the operation Business Central answered an install with on the booking,
    /// before it is polled. Must never turn a live install into a failure: a lost write is
    /// logged and the poll goes on, and a shutdown between acceptance and the stamp still
    /// stamps. The app id is only filled in when the row has none, so a booked update
    /// keeps the app it was booked for.
    /// </summary>
    private async Task StampOperationAsync(AppDbContext db, int actionId, BcAppOperation operation)
    {
        try
        {
            await db.OeEnvironmentUpgradeActions
                .Where(a => a.Id == actionId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.BcAppId, a => a.BcAppId ?? operation.AppId)
                    .SetProperty(a => a.BcOperationId, operation.Id), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Couldn't record operation {OperationId} on booking {ActionId}; the install goes on without it.", operation.Id, actionId);
        }
    }

    /// <summary>
    /// Fails any action left claimed-but-unfinished by a restart. Called once per org on
    /// the first sweep, when nothing of ours is running, so it can never trip a live one.
    /// The row is failed rather than retried: we know the send started and not whether it
    /// landed, and repeating "start the update now" on a guess is not a safe default.
    /// </summary>
    internal async Task FailInterruptedAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // An upload or AppSource update Business Central had already accepted went on
        // installing without us: it is recorded as sent, unconfirmed, and the rest of its batch still goes -
        // a dependent that needed it is refused by Business Central if it did not land.
        // (The operation ids are on the row; re-polling them after a restart is the
        // obvious next step, not taken yet.)
        var unconfirmed = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Status == UpgradeActionStatus.Pending && a.SentAt != null && a.BcOperationId != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Sent)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.Outcome, a => a.Kind == UpgradeActionKind.UpdateApp
                    ? (a.AppName ?? "The app") + " was sent for update, but the workbench restarted before it could confirm the update finished. Check the environment's installed apps."
                    : (a.PackageFileName ?? "The app")
                        + " was uploaded, but the workbench restarted before it could confirm the install finished. Check the environment's installed apps."),
                ct).ConfigureAwait(false);

        const string uploadOutcome =
            "The workbench restarted before the app could be sent to Business Central. Upload it again if you still need it.";
        const string outcome =
            "The workbench restarted while this was being sent, so we can't say whether it reached Business Central. "
            + "Check the environment, then schedule it again if you need to.";
        var failed = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Status == UpgradeActionStatus.Pending && a.SentAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.Outcome, a => a.Kind == UpgradeActionKind.UploadApp ? uploadOutcome : outcome), ct).ConfigureAwait(false);

        if (unconfirmed > 0 || failed > 0)
        {
            _logger.LogWarning(
                "A restart interrupted {Unconfirmed} upload(s) mid-install (recorded as sent, unconfirmed) and {Failed} action(s) mid-send (failed) in org {OrgId}.",
                unconfirmed, failed, orgId);
        }

        // Belt and braces for the packages: every settled write above clears its own, but
        // a restart between the send and that write, or a row settled by a path added
        // later, must not leave a 50 MB file behind for good. Only a pending row may
        // hold one.
        var swept = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Status != UpgradeActionStatus.Pending && a.PackageContent != null)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.PackageContent, (byte[]?)null), ct).ConfigureAwait(false);
        if (swept > 0)
        {
            _logger.LogInformation("Dropped the stored package from {Count} settled upload booking(s) in org {OrgId}.", swept, orgId);
        }
    }

    /// <summary>
    /// One due row, read outside the per-action scope so the sweep holds no context open
    /// while it works. <see cref="Label"/> is what the history calls it: an upload's file
    /// name, or an AppSource update's app name.
    /// </summary>
    private sealed record DueAction(
        int Id, int ProjectId, int EnvironmentId, UpgradeActionKind Kind, int? RequestedByUserId, string? TargetVersion,
        string? Label, Guid? BatchId, int? BatchOrder);
}
