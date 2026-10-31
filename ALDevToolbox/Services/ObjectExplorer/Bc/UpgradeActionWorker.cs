using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
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
    private readonly Operations.MaintenanceModeState? _maintenance;

    public UpgradeActionWorker(
        IServiceProvider services,
        TimeProvider clock,
        ILogger<UpgradeActionWorker> logger,
        WorkerHeartbeatRegistry heartbeats,
        // Optional so the tests that build the worker by hand keep compiling;
        // production DI always supplies the singleton.
        Operations.MaintenanceModeState? maintenance = null)
    {
        _services = services;
        _clock = clock;
        _logger = logger;
        _maintenance = maintenance;
        // Polls every 30 seconds and is idle most of the time. A sweep sends at most one
        // booked app install (an upload or an AppSource update), or asks again about one
        // nobody saw finish, in all - not per organisation - and waits for it, so
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

        // Per organisation, so one org whose recovery threw is tried again next sweep
        // rather than marked done with the rest (#1265).
        var recovered = new HashSet<int>();
        while (!stoppingToken.IsCancellationRequested)
        {
            _heartbeat.Tick();
            // Not while a restore runs or a failed one holds the site closed over a
            // database that may be half-loaded: a sweep installs apps and moves
            // update dates in customers' environments (#1321). Booked rows stay
            // pending and fire on the first sweep after the site reopens.
            if (_maintenance?.IsActive == true)
            {
                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                _heartbeat.BeginActive();
                try
                {
                    await ForEachOrgAsync(async (org, sys, ct) =>
                    {
                        if (recovered.Contains(org))
                        {
                            await SettleStaleClaimsAsync(org, sys, ct).ConfigureAwait(false);
                            return;
                        }
                        // Nothing of ours is running yet, so anything found mid-send was
                        // orphaned by a restart. Once per org, until it succeeds.
                        await FailInterruptedAsync(org, sys, ct).ConfigureAwait(false);
                        recovered.Add(org);
                    }, stoppingToken).ConfigureAwait(false);
                    // Two passes. Every organisation's platform-update moves first - a
                    // date the customer agreed, seconds each - then one booked app install
                    // in all (an upload or an AppSource update, or a second look at one
                    // nobody saw finish), which waits for it to finish. So no organisation's agreed slot waits behind another's
                    // ten-minute install, a sweep costs at most one install, and two
                    // installs never run at once: that can deadlock on Business Central's
                    // own bookkeeping table.
                    await ForEachOrgAsync((org, sys, ct) => RunDueActionsAsync(org, sys, ct, SweepPass.PlatformUpdates), stoppingToken).ConfigureAwait(false);
                    // Whoever checks an environment after its update hears when it is on
                    // the target version (#1047). A read, cheap when nothing is new.
                    await ForEachOrgAsync(NotifyReadyToCheckAsync, stoppingToken).ConfigureAwait(false);
                    await RunOneUploadAsync(stoppingToken).ConfigureAwait(false);
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
    private async Task ForEachOrgAsync(Func<int, bool, CancellationToken, Task> perOrg, CancellationToken ct) =>
        await RunForOrgsAsync(await ListOrgsAsync(ct).ConfigureAwait(false), perOrg, ct).ConfigureAwait(false);

    /// <summary>
    /// The active organisations. IsSystem travels with the id so the per-org identity
    /// carries the org's real flag rather than a hard-coded false (issue #694).
    /// </summary>
    private async Task<List<(int Id, bool IsSystem)>> ListOrgsAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // The org enumeration; per-org work then runs inside that org's
        // AmbientOrganizationScope.
        var rows = await db.Organizations.AsNoTracking()
            .Where(o => !o.IsPending)
            .Select(o => new { o.Id, o.IsSystem })
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(o => (o.Id, o.IsSystem)).ToList();
    }

    /// <summary>Runs <paramref name="perOrg"/> for each of <paramref name="orgs"/> in the order given; one org's failure is logged and the rest go on.</summary>
    private async Task RunForOrgsAsync(IEnumerable<(int Id, bool IsSystem)> orgs, Func<int, bool, CancellationToken, Task> perOrg, CancellationToken ct)
    {
        foreach (var (orgId, isSystem) in orgs)
        {
            try
            {
                await perOrg(orgId, isSystem, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down: not one org's failure, and the rest need not be tried.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UpgradeActionWorker failed for org {OrgId}.", orgId);
            }
        }
    }

    /// <summary>
    /// The sweep's one booked app install, across every organisation, taken in turns
    /// (#1539). Among the organisations with an install due (or one still owed a second
    /// look), the turn goes to the one whose most recent install was sent longest ago -
    /// one never served first - and only then to the longest-waiting due install, then the
    /// lowest id. So a twenty-app batch booked for one organisation's opening alternates
    /// with another organisation's install in the same window instead of running to the
    /// end first; it does not speed anything up. The order is read from the rows each
    /// sweep, so it survives a restart. Still one install in all - see the comment in
    /// <see cref="ExecuteAsync"/>. An organisation whose due rows all turn out to be
    /// waiting (a batch gate, a window that closed) passes the turn on. Internal so a test
    /// can drive the pass. True when one was sent.
    /// </summary>
    internal async Task<bool> RunOneUploadAsync(CancellationToken ct)
    {
        var orgs = await ListOrgsAsync(ct).ConfigureAwait(false);
        var turns = new Dictionary<int, InstallTurn>();
        await RunForOrgsAsync(orgs, async (org, sys, token) =>
        {
            if (await InstallTurnAsync(org, sys, token).ConfigureAwait(false) is { } turn) turns[org] = turn;
        }, ct).ConfigureAwait(false);

        var sent = false;
        await RunForOrgsAsync(
            orgs.Where(o => turns.ContainsKey(o.Id))
                .OrderBy(o => turns[o.Id].LastSentAt ?? DateTime.MinValue)
                .ThenBy(o => turns[o.Id].OldestDue)
                .ThenBy(o => o.Id),
            async (org, sys, token) =>
            {
                if (sent) return;
                sent = await RunDueActionsAsync(org, sys, token, SweepPass.OneUpload).ConfigureAwait(false) > 0;
            }, ct).ConfigureAwait(false);
        return sent;
    }

    /// <summary>Where one organisation stands for the install turn: when its last install went, and when its longest-waiting one came due.</summary>
    private sealed record InstallTurn(DateTime? LastSentAt, DateTime OldestDue);

    /// <summary>This org's place in the install turn, or null when it has nothing to send or ask about again.</summary>
    private async Task<InstallTurn?> InstallTurnAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var installs = db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Kind == UpgradeActionKind.UploadApp || a.Kind == UpgradeActionKind.UpdateApp);
        var oldestDue = await installs
            .Where(a => a.ConfirmationDue
                        || (a.Status == UpgradeActionStatus.Pending && a.SentAt == null && a.ExecuteAfter <= now))
            .MinAsync(a => (DateTime?)a.ExecuteAfter, ct).ConfigureAwait(false);
        if (oldestDue is not { } due) return null;
        var lastSent = await installs
            .Where(a => a.SentAt != null)
            .MaxAsync(a => a.SentAt, ct).ConfigureAwait(false);
        return new InstallTurn(lastSent, due);
    }

    /// <summary>Which of a sweep's two passes a call makes; <see cref="Both"/> is the one-call form a test drives.</summary>
    internal enum SweepPass { PlatformUpdates, OneUpload, Both }

    /// <summary>
    /// Fires the due rows of one org: every platform-update move, then at most one app
    /// install - an upload or an AppSource update (the rest of a batch, or the org's
    /// other installs, wait for the next sweep). An install Business Central accepted but
    /// nobody saw finish is asked about again in that install's place (see
    /// <see cref="ConfirmOneAsync"/>). Internal so a test can drive one sweep against a
    /// seeded database without the hosted-service loop. Returns how many rows were sent or
    /// asked about again; rows only settled on the way (a dependent skipped because the
    /// app before it failed) do not count, so the sweep does not stop on one.
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
                    a.PackageFileName ?? a.AppName, a.BatchId, a.BatchOrder, a.ScheduledByWindow))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var sent = 0;
        foreach (var action in due.Where(a => !UpgradeActionService.IsAppInstall(a.Kind)))
        {
            if (ct.IsCancellationRequested) break;
            if (await RunOneAsync(orgId, isSystem, action, ct).ConfigureAwait(false) == RunOutcome.Sent) sent++;
        }

        // One app install per sweep: it waits for its install, up to ten minutes. An
        // install nobody saw finish is asked about first and takes that turn, so its batch
        // hears how it ended before the next app goes. A batch resumes on the next sweep,
        // where the gate in RunOneAsync finds its predecessor settled.
        if (uploads && !ct.IsCancellationRequested)
        {
            if (await ConfirmOneAsync(orgId, isSystem, ct).ConfigureAwait(false))
            {
                sent++;
            }
            else
            {
                foreach (var action in due.Where(a => UpgradeActionService.IsAppInstall(a.Kind)))
                {
                    if (ct.IsCancellationRequested) break;
                    if (await RunOneAsync(orgId, isSystem, action, ct).ConfigureAwait(false) != RunOutcome.Sent) continue;
                    sent++;
                    break;
                }
            }
        }

        if (sent > 0)
        {
            _logger.LogInformation("UpgradeActionWorker ran {Count} scheduled upgrade action(s) for org {OrgId}.", sent, orgId);
        }
        return sent;
    }

    /// <summary>
    /// Tells the people checking environments on this org's planned upgrades which ones
    /// are now on the target version (#1047). Internal so a test can drive one pass.
    /// </summary>
    internal async Task<int> NotifyReadyToCheckAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Notifications.UpgradeCheckNotifier>()
            .NotifyReadyToCheckAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the person a booking runs as still has an active account. A row fires as
    /// its requester, and the access gates read their role and teams but not whether the
    /// account was disabled since, so this is asked first (#1235), the way a deployment
    /// asks it of the person it runs as. A row nobody is recorded against runs as nobody.
    /// </summary>
    private static async Task<bool> IsRequesterActiveAsync(AppDbContext db, int? userId, CancellationToken ct) =>
        userId is { } id
        && await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == id && u.Status == UserStatus.Active, ct).ConfigureAwait(false);

    /// <summary>
    /// The deliveries rule (#1124) for a booked install a window chose the time for: judged
    /// against the window the booking applied, as it is now on the environment: the
    /// delivery window for a DeliveryWindow row (Microsoft's, if the delivery window has
    /// since been removed - where such a booking made now would land), Microsoft's update
    /// window for a BcUpdateWindow row. When the window
    /// has closed, the row and the rest of its batch still waiting are moved to its next
    /// opening rather than sent. A time the person picked is never held back - those rows
    /// carry no window. Nothing is moved when no window is configured any more. The write
    /// is a compare-and-set on rows still pending, unsent and window-chosen, so a cancel
    /// or an "Install now" in between wins. True when the row was moved.
    /// </summary>
    private async Task<bool> MovedPastClosedWindowAsync(AppDbContext db, DueAction action, UploadAppTiming window, CancellationToken ct)
    {
        var env = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == action.EnvironmentId)
            .Select(e => new
            {
                e.Name, e.UpdateWindowStart, e.UpdateWindowEnd, e.UpdateWindowDays, e.Project!.BcTimeZone,
                e.BcUpdateWindowStart, e.BcUpdateWindowEnd, e.BcUpdateWindowTimeZoneIana,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (env is null) return false;

        TimeOnly? start, end;
        DeliveryDays days;
        TimeZoneInfo tz;
        if (window == UploadAppTiming.DeliveryWindow && UpdateWindow.IsConfigured(env.UpdateWindowStart, env.UpdateWindowEnd))
        {
            (start, end, days, tz) = (env.UpdateWindowStart, env.UpdateWindowEnd, env.UpdateWindowDays,
                UpdateWindow.ResolveTimeZone(env.BcTimeZone));
        }
        else if (UpdateWindow.IsConfigured(env.BcUpdateWindowStart, env.BcUpdateWindowEnd))
        {
            // Microsoft's window is expressed in its own zone, as the booking read it.
            (start, end, days, tz) = (env.BcUpdateWindowStart, env.BcUpdateWindowEnd, DeliveryDays.Every,
                UpdateWindow.ResolveTimeZone(env.BcUpdateWindowTimeZoneIana ?? env.BcTimeZone));
        }
        else
        {
            return false;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        if (UpdateWindow.IsWithin(start, end, tz, now, days)) return false;

        var next = UpdateWindow.NextOpeningUtc(start, end, tz, now, days);
        var moved = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Status == UpgradeActionStatus.Pending && a.SentAt == null
                        && a.ScheduledByWindow != null && a.ExecuteAfter < next)
            .Where(a => action.BatchId != null ? a.BatchId == action.BatchId : a.Id == action.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ExecuteAfter, next), ct).ConfigureAwait(false);
        if (moved > 0)
        {
            _logger.LogInformation(
                "Booked install {ActionId} ({FileName}) on {Environment} came up after its window closed; {Moved} row(s) moved to the next opening, {NextOpening}.",
                action.Id, action.Label, env.Name, moved, next);
        }
        return moved > 0;
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

        // A row in a batch - a multi-app upload, or several AppSource updates booked
        // together - waits its turn: the one before it must have finished installing
        // (the sweep runs them in order, so within one sweep it has), and if that one
        // failed this one is not tried - its dependency may be the very thing that did
        // not go in. An upload cannot tell which of the files before it it needs, so any
        // failure stops it; an update can, so only a failed app it waits for does. A
        // cancelled predecessor is the person's choice and does not stop the rest.
        if (action.BatchId is { } batch && action.BatchOrder is { } order)
        {
            var earlier = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.BatchId == batch && a.BatchOrder < order)
                .OrderBy(a => a.BatchOrder)
                .Select(a => new { a.Status, a.SentAt, a.PackageFileName, a.AppName, a.BcAppId, a.PrerequisiteAppIds, a.ConfirmationDue })
                .ToListAsync(ct).ConfigureAwait(false);
            // One Business Central accepted but nobody saw finish is asked about before
            // its dependents go, so a failure found that way still stops them. The sweep
            // already sees to that (ConfirmOneAsync takes the install turn while any row
            // is marked); this holds the rule here too, where the batch rules live.
            if (earlier.Any(e => e.ConfirmationDue))
            {
                return RunOutcome.Skipped;
            }
            // A predecessor still pending is either waiting its turn or mid-install. One
            // claimed longer ago than an install can take is a row whose settling write
            // was lost; it is not waited on for ever - its dependents go, and Business
            // Central refuses them by name if the app it needed is not there.
            var staleBefore = _clock.GetUtcNow().UtcDateTime - (ProjectConnectionService.DefaultUploadPollTimeout + StaleClaimSlack);
            if (earlier.Any(e => e.Status == UpgradeActionStatus.Pending && (e.SentAt == null || e.SentAt > staleBefore)))
            {
                return RunOutcome.Skipped;
            }
            var waitsFor = action.Kind == UpgradeActionKind.UpdateApp
                ? await db.OeEnvironmentUpgradeActions.AsNoTracking()
                    .Where(a => a.Id == action.Id)
                    .Select(a => a.PrerequisiteAppIds)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false) ?? new List<Guid>()
                : null;
            // An update also stops when one before it failed while bringing along an app
            // this one waits for too: asking Business Central for the same app again
            // would only fail the same way.
            if (earlier.FirstOrDefault(e => e.Status == UpgradeActionStatus.Failed
                    && (waitsFor is null
                        || (e.BcAppId is { } failedApp && waitsFor.Contains(failedApp))
                        || (e.PrerequisiteAppIds != null && e.PrerequisiteAppIds.Any(waitsFor.Contains)))) is { } blocked)
            {
                var blockedName = blocked.PackageFileName ?? blocked.AppName ?? "the app";
                _logger.LogInformation(
                    "Booked install {ActionId} ({FileName}) is not tried: {Blocked} before it in the same batch failed.",
                    action.Id, action.Label, blockedName);
                var skippedOutcome = waitsFor is null
                    ? $"{action.Label} wasn't installed, because {blockedName} before it in the same upload didn't install."
                    : blocked.BcAppId is { } blockedApp && waitsFor.Contains(blockedApp)
                    ? $"{action.Label} wasn't updated, because {blockedName}, which it waits for, wasn't updated."
                    : $"{action.Label} wasn't updated, because the update of {blockedName} before it failed, and it brings along an app both of them wait for.";
                var skippedAt = _clock.GetUtcNow().UtcDateTime;
                var skipped = await db.OeEnvironmentUpgradeActions
                    .Where(a => a.Id == action.Id && a.Status == UpgradeActionStatus.Pending && a.SentAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                        .SetProperty(a => a.SentAt, skippedAt)
                        .SetProperty(a => a.PackageContent, (byte[]?)null)
                        .SetProperty(a => a.Outcome, skippedOutcome),
                        ct).ConfigureAwait(false);
                if (skipped == 0) return RunOutcome.Skipped;
                await NotifyAsync(scope, action.Id, unconfirmed: false).ConfigureAwait(false);
                return RunOutcome.Settled;
            }
        }

        // A window chose this install's time, so it runs inside that window or waits for
        // the next opening (#1539): a batch or a queue of other bookings can outlast the
        // window, and an install is not sent into the customer's working day for that.
        if (action.ScheduledByWindow is { } window
            && UpgradeActionService.IsAppInstall(action.Kind)
            && await MovedPastClosedWindowAsync(db, action, window, ct).ConfigureAwait(false))
        {
            return RunOutcome.Skipped;
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
        // Set when Business Central took the app but the install was not seen to finish:
        // a later sweep asks it once more (ConfirmOneAsync).
        var confirmationDue = false;
        // Read before the send, so nothing after a write that landed can turn it into a failure.
        var environmentName = await db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == action.EnvironmentId)
            .Select(e => e.Name)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        try
        {
            // A disabled account runs nothing, booked before it was disabled or not (#1235).
            if (!await IsRequesterActiveAsync(db, action.RequestedByUserId, ct).ConfigureAwait(false))
            {
                throw new ProjectAccessDeniedException();
            }
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
                    outcome = UnconfirmedOutcome(action.Kind, action.Label, action.TargetVersion, result.Message);
                    confirmationDue = true;
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
                        ? (UpgradeActionStatus.Sent, UnconfirmedOutcome(action.Kind, action.Label, action.TargetVersion, result.Message))
                        : (UpgradeActionStatus.Failed, UpgradeActionService.FailureOutcome(action.Kind,
                            result.Message ?? "Business Central didn't finish the update.", action.TargetVersion, action.Label));
                confirmationDue = result.IsUnconfirmed;
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
        // An unconfirmed one is asked about again only when its operation was stamped:
        // without both ids there is nothing to ask about.
        var finishedAt = _clock.GetUtcNow().UtcDateTime;
        if (!await SettleAsync(db, action.Id, status, outcome, confirmationDue, finishedAt).ConfigureAwait(false))
        {
            // Still claimed and pending: a later sweep's SettleStaleClaimsAsync settles it.
            return RunOutcome.Sent;
        }
        // One left to be asked about again is announced by that second look instead.
        await NotifyAsync(scope, action.Id, confirmationDue).ConfigureAwait(false);
        return RunOutcome.Sent;
    }

    /// <summary>
    /// Writes how a sent row ended. Not cancellable, and tried a few times: the send has
    /// happened, and a row left claimed and pending cannot be cancelled by the person who
    /// booked it (#1265). Returns false when every attempt failed, which a later sweep's
    /// <see cref="SettleStaleClaimsAsync"/> then puts right.
    /// </summary>
    private async Task<bool> SettleAsync(
        AppDbContext db, int actionId, UpgradeActionStatus status, string outcome, bool confirmationDue, DateTime finishedAt)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.OeEnvironmentUpgradeActions
                    .Where(a => a.Id == actionId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, status)
                        .SetProperty(a => a.Outcome, outcome)
                        .SetProperty(a => a.PackageContent, (byte[]?)null)
                        .SetProperty(a => a.ConfirmationDue, a => confirmationDue && a.BcAppId != null && a.BcOperationId != null)
                        .SetProperty(a => a.SentAt, finishedAt), CancellationToken.None).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                _logger.LogWarning(ex, "Couldn't record how upgrade action {ActionId} ended (attempt {Attempt}); trying again.", actionId, attempt);
                await Task.Delay(TimeSpan.FromSeconds(attempt), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Couldn't record how upgrade action {ActionId} ended; a later sweep settles it.", actionId);
                return false;
            }
        }
    }

    /// <summary>
    /// Tells the person who booked <paramref name="actionId"/> how it went (issue #1046),
    /// in the scope that settled it. Not cancellable: the outcome is already written, and
    /// a shutdown in between would otherwise leave it unannounced for good. The notifier
    /// never throws.
    /// </summary>
    private static Task NotifyAsync(AsyncServiceScope scope, int actionId, bool unconfirmed) =>
        scope.ServiceProvider.GetRequiredService<Notifications.UpgradeActionNotifier>()
            .NotifyAsync(actionId, unconfirmed, CancellationToken.None);

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
    /// The per-sweep half of <see cref="FailInterruptedAsync"/>: settles rows claimed
    /// longer ago than an install can take. Sweeps run one after another, so a row that
    /// old is never one this process is still sending.
    /// </summary>
    internal Task SettleStaleClaimsAsync(int orgId, bool isSystem, CancellationToken ct) =>
        FailInterruptedAsync(orgId, isSystem, ct,
            _clock.GetUtcNow().UtcDateTime - (ProjectConnectionService.DefaultUploadPollTimeout + StaleClaimSlack));

    /// <summary>
    /// Settles any action left claimed-but-unfinished by a restart. Called once per org on
    /// the first sweep, when nothing of ours is running, so it can never trip a live one.
    /// An install Business Central had already accepted is recorded as sent and marked to
    /// be asked about again, which the same sweep's install pass does
    /// (<see cref="ConfirmOneAsync"/>). Anything else is failed rather than retried: we
    /// know the send started and not whether it landed, and repeating "start the update
    /// now" on a guess is not a safe default.
    /// <para>
    /// With <paramref name="claimedBefore"/> it settles only rows claimed before that
    /// instant: every sweep does this for rows claimed longer ago than an install can
    /// take, so a row whose settling write was lost is not left pending, and
    /// uncancellable, until the next restart (#1265).
    /// </para>
    /// </summary>
    internal async Task FailInterruptedAsync(int orgId, bool isSystem, CancellationToken ct, DateTime? claimedBefore = null)
    {
        using var ambient = AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem));
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        System.Linq.Expressions.Expression<Func<OeEnvironmentUpgradeAction, bool>> claimedFilter = claimedBefore is { } cutoff
            ? a => a.Status == UpgradeActionStatus.Pending && a.SentAt != null && a.SentAt < cutoff
            : a => a.Status == UpgradeActionStatus.Pending && a.SentAt != null;

        // An upload or AppSource update Business Central had already accepted went on
        // installing without us: it is recorded as sent, unconfirmed, and marked so the
        // install pass asks Business Central how it ended from the stored ids before the
        // rest of its batch goes. The wording below stands only when that cannot happen
        // (no app id was stamped) or until it does.
        // Read first, so each person hears how their interrupted change was left.
        var interrupted = await db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(claimedFilter)
            .Select(a => a.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var unconfirmed = await db.OeEnvironmentUpgradeActions
            .Where(claimedFilter).Where(a => a.BcOperationId != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Sent)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.ConfirmationDue, a => a.BcAppId != null)
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
            .Where(claimedFilter)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.Outcome, a => a.Kind == UpgradeActionKind.UploadApp ? uploadOutcome : outcome), ct).ConfigureAwait(false);

        if (unconfirmed > 0 || failed > 0)
        {
            _logger.LogWarning(
                "A restart interrupted {Unconfirmed} install(s) Business Central had accepted (recorded as sent, to be checked again) and {Failed} action(s) mid-send (failed) in org {OrgId}.",
                unconfirmed, failed, orgId);
        }
        foreach (var id in interrupted)
        {
            // A failed one says so on its row; a sent one here was never confirmed.
            await NotifyAsync(scope, id, unconfirmed: true).ConfigureAwait(false);
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
    /// Asks Business Central how one accepted-but-unconfirmed install ended - the oldest
    /// row of this org marked <see cref="OeEnvironmentUpgradeAction.ConfirmationDue"/> -
    /// and settles the row as <see cref="RunOneAsync"/> would have: sent on success,
    /// failed in Business Central's words, and still sent, unconfirmed, only when no answer
    /// can be had. The mark is cleared whatever the answer, so a row is asked about once.
    /// Returns whether a row was asked about, which takes the sweep's one install turn.
    /// </summary>
    private async Task<bool> ConfirmOneAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        ConfirmAction? row;
        using (AmbientOrganizationScope.Enter(
            AmbientOrganizationScope.OrganizationIdentity.ForOrganization(orgId, isSystem)))
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            row = await db.OeEnvironmentUpgradeActions.AsNoTracking()
                .Where(a => a.ConfirmationDue)
                .OrderBy(a => a.SentAt)
                .ThenBy(a => a.Id)
                .Select(a => new ConfirmAction(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.RequestedByUserId, a.TargetVersion,
                    a.Kind == UpgradeActionKind.UpdateApp ? a.AppName : a.PackageFileName, a.BcAppId, a.BcOperationId))
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }
        if (row is null) return false;

        // As the requester, as the send was: the audit trail and the access check are theirs.
        var identity = AmbientOrganizationScope.OrganizationIdentity.ForOrganization(
            orgId, isSystem, row.RequestedByUserId);
        using var ambient = AmbientOrganizationScope.Enter(identity);
        await using var actionScope = _services.CreateAsyncScope();
        var actionDb = actionScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var label = row.Label ?? "The app";

        UpgradeActionStatus? status = null;
        string? outcome = null;
        var answered = false;
        if (row.AppId is { } appId && row.OperationId is { } operationId)
        {
            var environmentName = await actionDb.OeProjectEnvironments.AsNoTracking()
                .Where(e => e.Id == row.EnvironmentId)
                .Select(e => e.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            try
            {
                // Asked as the person who booked it, so only while their account is active (#1235).
                if (!await IsRequesterActiveAsync(actionDb, row.RequestedByUserId, ct).ConfigureAwait(false))
                {
                    throw new ProjectAccessDeniedException();
                }
                var actions = actionScope.ServiceProvider.GetRequiredService<UpgradeActionService>();
                var result = await actions.ConfirmInstallAsync(row.ProjectId, row.EnvironmentId, appId, operationId, ct, _heartbeat.Tick)
                    .ConfigureAwait(false);
                answered = !result.IsUnconfirmed;
                (status, outcome) = result.Completed
                    ? (UpgradeActionStatus.Sent, UpgradeActionService.SuccessOutcome(row.Kind, row.TargetVersion, environmentName, label))
                    : result.IsUnconfirmed
                        ? (UpgradeActionStatus.Sent, UnconfirmedOutcome(row.Kind, label, row.TargetVersion, result.Message))
                        : (UpgradeActionStatus.Failed, UpgradeActionService.FailureOutcome(row.Kind,
                            result.Message ?? (row.Kind == UpgradeActionKind.UpdateApp
                                ? "Business Central didn't finish the update."
                                : "Business Central didn't finish the install."), row.TargetVersion, label));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down: the mark stays, and the first sweep after the restart asks again.
                throw;
            }
            catch (PlanValidationException ex)
            {
                // The environment or its credentials are gone. That says nothing about the
                // install, which Business Central ran regardless.
                _logger.LogWarning("Couldn't check booking {ActionId} again: {Reason}", row.Id, ex.Errors.Values.FirstOrDefault());
                (status, outcome) = (UpgradeActionStatus.Sent, UnconfirmedOutcome(row.Kind, label, row.TargetVersion,
                    "The workbench couldn't reach the environment afterwards to check whether it finished, so it wasn't confirmed here."));
            }
            catch (ProjectAccessDeniedException)
            {
                (status, outcome) = (UpgradeActionStatus.Sent, UnconfirmedOutcome(row.Kind, label, row.TargetVersion,
                    "The person who booked it no longer had permission to manage this customer, so the workbench didn't check whether it finished."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Checking booking {ActionId} again on environment {EnvironmentId} (project {ProjectId}) threw.",
                    row.Id, row.EnvironmentId, row.ProjectId);
                (status, outcome) = (UpgradeActionStatus.Sent, UnconfirmedOutcome(row.Kind, label, row.TargetVersion,
                    "The workbench couldn't ask Business Central afterwards whether it finished, so it wasn't confirmed here."));
            }
        }

        // Only a row still marked is settled, and the mark goes in the same write. A row
        // with nothing to ask about keeps what it says and just loses the mark.
        var marked = actionDb.OeEnvironmentUpgradeActions.Where(a => a.Id == row.Id && a.ConfirmationDue);
        int settled;
        if (status is { } settledStatus && outcome is not null)
        {
            settled = await marked.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ConfirmationDue, false)
                .SetProperty(a => a.Status, settledStatus)
                .SetProperty(a => a.Outcome, outcome), ct).ConfigureAwait(false);
            _logger.LogInformation("Booking {ActionId} was checked again with Business Central: {Status}.", row.Id, settledStatus);
        }
        else
        {
            settled = await marked.ExecuteUpdateAsync(s => s.SetProperty(a => a.ConfirmationDue, false), ct).ConfigureAwait(false);
        }
        // Unconfirmed unless Business Central answered either way. Only the pass that
        // cleared the mark tells anyone, so a row settled elsewhere is not announced twice.
        if (settled > 0)
        {
            await NotifyAsync(actionScope, row.Id, unconfirmed: !answered).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// What the history says about an install Business Central accepted but nobody saw
    /// finish: that it was sent, then the poller's caveat or the reason it could not ask.
    /// </summary>
    private static string UnconfirmedOutcome(UpgradeActionKind kind, string? label, string? targetVersion, string? detail) =>
        kind == UpgradeActionKind.UpdateApp
            ? $"{label} was sent for update to {targetVersion}. {detail}"
            : $"{label} was uploaded. {detail}";

    /// <summary>An accepted install still to be asked about, read outside the scope that asks.</summary>
    private sealed record ConfirmAction(
        int Id, int ProjectId, int EnvironmentId, UpgradeActionKind Kind, int? RequestedByUserId, string? TargetVersion,
        string? Label, Guid? AppId, Guid? OperationId);

    /// <summary>
    /// One due row, read outside the per-action scope so the sweep holds no context open
    /// while it works. <see cref="Label"/> is what the history calls it: an upload's file
    /// name, or an AppSource update's app name.
    /// </summary>
    private sealed record DueAction(
        int Id, int ProjectId, int EnvironmentId, UpgradeActionKind Kind, int? RequestedByUserId, string? TargetVersion,
        string? Label, Guid? BatchId, int? BatchOrder, UploadAppTiming? ScheduledByWindow);
}
