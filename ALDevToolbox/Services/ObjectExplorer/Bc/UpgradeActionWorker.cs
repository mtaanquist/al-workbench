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
        // Polls every 30 seconds and is idle most of the time; a sweep that is still
        // running after 15 minutes is wedged on somebody's tenant.
        _heartbeat = heartbeats.Register(nameof(UpgradeActionWorker),
            // A sweep that sends a multi-app upload waits for each install to finish -
            // up to ten minutes apiece - so a batch of a vendor's suite is a long sweep,
            // not a stalled one.
            maxActiveDuration: TimeSpan.FromMinutes(90),
            maxIdleSilence: TimeSpan.FromMinutes(5));
    }

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
                    await ForEachOrgAsync(RunDueActionsAsync, stoppingToken).ConfigureAwait(false);
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

    /// <summary>
    /// Fires every action of one org whose slot has arrived. Internal so a test can drive
    /// one sweep against a seeded database without the hosted-service loop. Returns how
    /// many rows were run.
    /// </summary>
    internal async Task<int> RunDueActionsAsync(int orgId, bool isSystem, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

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
                .OrderBy(a => a.ExecuteAfter)
                .ThenBy(a => a.BatchOrder)
                .ThenBy(a => a.Id)
                .Select(a => new DueAction(a.Id, a.ProjectId, a.EnvironmentId, a.Kind, a.RequestedByUserId, a.TargetVersion,
                    a.PackageFileName, a.BatchId, a.BatchOrder))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var ran = 0;
        foreach (var action in due)
        {
            if (ct.IsCancellationRequested) break;
            if (await RunOneAsync(orgId, isSystem, action, ct).ConfigureAwait(false)) ran++;
        }

        if (ran > 0)
        {
            _logger.LogInformation("UpgradeActionWorker ran {Count} scheduled upgrade action(s) for org {OrgId}.", ran, orgId);
        }
        return ran;
    }

    /// <summary>
    /// Claims one row, performs it, and writes down what happened. Returns false when the
    /// claim was lost — somebody cancelled it in the seconds between the sweep's read and
    /// this call, and their cancel stands.
    /// </summary>
    private async Task<bool> RunOneAsync(int orgId, bool isSystem, DueAction action, CancellationToken ct)
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
                .Select(a => new { a.Status, a.PackageFileName })
                .ToListAsync(ct).ConfigureAwait(false);
            if (earlier.Any(e => e.Status == UpgradeActionStatus.Pending))
            {
                return false;
            }
            if (earlier.FirstOrDefault(e => e.Status == UpgradeActionStatus.Failed) is { } blocked)
            {
                var skippedAt = _clock.GetUtcNow().UtcDateTime;
                var skipped = await db.OeEnvironmentUpgradeActions
                    .Where(a => a.Id == action.Id && a.Status == UpgradeActionStatus.Pending && a.SentAt == null)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                        .SetProperty(a => a.SentAt, skippedAt)
                        .SetProperty(a => a.PackageContent, (byte[]?)null)
                        .SetProperty(a => a.Outcome,
                            $"{action.PackageFileName} wasn't installed, because {blocked.PackageFileName} before it in the same upload didn't install."),
                        ct).ConfigureAwait(false);
                return skipped > 0;
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
            return false;
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
                if (package is null || package.Length == 0 || action.PackageFileName is null)
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["App"] = "The app file was no longer stored with the booking.",
                    });
                }
                var result = await actions.RunUploadAsync(action.ProjectId, action.EnvironmentId, action.PackageFileName, package, ct).ConfigureAwait(false);
                if (result.Completed)
                {
                    status = UpgradeActionStatus.Sent;
                    outcome = UpgradeActionService.SuccessOutcome(action.Kind, action.TargetVersion, environmentName, action.PackageFileName)
                        + (result.Message is null ? string.Empty : " " + result.Message);
                }
                else
                {
                    // Business Central took the file and then reported the install failed,
                    // in its own words (or ours, for a timeout).
                    status = UpgradeActionStatus.Failed;
                    outcome = UpgradeActionService.FailureOutcome(action.Kind,
                        result.Message ?? "Business Central didn't finish the install.", action.TargetVersion, action.PackageFileName);
                }
            }
            else
            {
                await actions.RunAsync(action.ProjectId, action.EnvironmentId, action.Kind, action.TargetVersion, ct).ConfigureAwait(false);
                status = UpgradeActionStatus.Sent;
                outcome = UpgradeActionService.SuccessOutcome(action.Kind, action.TargetVersion, environmentName, action.PackageFileName);
            }
        }
        catch (PlanValidationException ex)
        {
            // The live re-validation the Stage 3 writes do: the update was applied or
            // withdrawn during the afternoon, the environment is busy, the credentials
            // were rotated. All already in plain words.
            status = UpgradeActionStatus.Failed;
            outcome = UpgradeActionService.FailureOutcome(action.Kind,
                ex.Errors.Values.FirstOrDefault() ?? "Business Central refused the change.", action.TargetVersion, action.PackageFileName);
        }
        catch (ProjectAccessDeniedException)
        {
            status = UpgradeActionStatus.Failed;
            outcome = action.Kind == UpgradeActionKind.UploadApp
                ? "The person who booked this no longer had permission to manage this customer, so the app wasn't installed."
                : "The person who booked this no longer had permission to change this customer's update dates, so it wasn't run.";
        }
        catch (Exception ex)
        {
            // Never raw exception text in the feed — the detail goes to the log.
            _logger.LogError(ex,
                "Upgrade action {ActionId} on environment {EnvironmentId} (project {ProjectId}) threw.",
                action.Id, action.EnvironmentId, action.ProjectId);
            status = UpgradeActionStatus.Failed;
            outcome = UpgradeActionService.FailureOutcome(action.Kind, "Business Central didn't accept the change.", action.TargetVersion, action.PackageFileName);
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
        return true;
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

        const string outcome =
            "The workbench restarted while this was being sent, so we can't say whether it reached Business Central. "
            + "Check the environment, then schedule it again if you need to.";
        var failed = await db.OeEnvironmentUpgradeActions
            .Where(a => a.Status == UpgradeActionStatus.Pending && a.SentAt != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.Outcome, outcome), ct).ConfigureAwait(false);

        if (failed > 0)
        {
            _logger.LogWarning(
                "Failed {Count} upgrade action(s) in org {OrgId} that a restart interrupted mid-send.", failed, orgId);
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

    /// <summary>One due row, read outside the per-action scope so the sweep holds no context open while it works.</summary>
    private sealed record DueAction(
        int Id, int ProjectId, int EnvironmentId, UpgradeActionKind Kind, int? RequestedByUserId, string? TargetVersion,
        string? PackageFileName, Guid? BatchId, int? BatchOrder);
}
