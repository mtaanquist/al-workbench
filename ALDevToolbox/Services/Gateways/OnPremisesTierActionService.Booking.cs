using System.Globalization;
using System.Text.Json;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>A restart booked for later and not yet run (#1788).</summary>
/// <param name="RunAt">When it runs, UTC.</param>
public sealed record ServiceTierBooking(int Id, ServiceTierAction Action, DateTime RunAt, string? BookedBy, DateTime BookedAt);

/// <summary>What booking a restart needs to say before it is booked.</summary>
/// <param name="Plan">What the restart does, as the immediate one's confirmation says it.</param>
/// <param name="TimeZone">
/// The gateway computer's time zone (a Windows id) that <paramref name="ChangeHours"/> and
/// the time picked are read in; null when an older gateway did not say, and the time is UTC.
/// </param>
/// <param name="ChangeHours">The customer's change hours for the tiers it restarts (<c>20:00-06:00</c>); null when any time is allowed.</param>
/// <param name="DefaultRunAt">The time the form starts on, UTC: the change hours' next opening, else the next full hour.</param>
public sealed record ServiceTierBookingPlan(ServiceTierActionPlan Plan, string? TimeZone, string? ChangeHours, DateTime DefaultRunAt);

/// <summary>How running a booked restart went.</summary>
public enum ServiceTierBookingRun
{
    /// <summary>The command is with the gateway.</summary>
    Sent,

    /// <summary>Something else is working on the tiers; it is tried again on the next sweep.</summary>
    Waiting,

    /// <summary>It was refused and is recorded as failed; the person who booked it is to be told.</summary>
    Failed,

    /// <summary>It had already run or been cancelled.</summary>
    Gone,
}

/// <summary>
/// Booking a restart of an on-premises service tier, or of its database's restart group, for
/// a later time (#1788). The booking is a pending <see cref="OeEnvironmentUpgradeAction"/>
/// with no command; <see cref="ServiceTierBookingWorker"/> sweeps the table and sends it when
/// it is due, as the person who booked it, through the same path as an immediate restart, so
/// everything is checked again then. See <c>.design/on-prem-gateway.md</c>, "Service tier
/// actions, as built".
/// </summary>
public sealed partial class OnPremisesTierActionService
{
    /// <summary>How far ahead a restart can be booked.</summary>
    internal static readonly TimeSpan MaxBookingAhead = TimeSpan.FromDays(30);

    /// <summary>How soon a booking can be for: a time that has passed by the time it is saved is no booking.</summary>
    internal static readonly TimeSpan MinBookingAhead = TimeSpan.FromMinutes(2);

    /// <summary>How long past its time a booking waits for something else on its tiers to finish before it is given up.</summary>
    internal static readonly TimeSpan BookingGrace = TimeSpan.FromHours(2);

    private static bool IsBookable(ServiceTierAction action) => action is ServiceTierAction.Restart or ServiceTierAction.RestartAll;

    /// <summary>What booking <paramref name="action"/> on <paramref name="environmentId"/> would do; null when it is not a linked on-premises environment.</summary>
    public async Task<ServiceTierBookingPlan?> GetBookingPlanAsync(int environmentId, ServiceTierAction action, CancellationToken ct = default)
    {
        if (!IsBookable(action)) return null;
        var env = await EnvironmentAsync(environmentId, ct);
        if (env is null) return null;
        await _access.EnsureCanViewAsync(env.ProjectId, ct);
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct);
        if (facts is null || facts.EnvironmentUnlinked) return null;
        var plan = await PlanAsync(env, facts, action, forDialog: true, ct, booking: true);
        var zone = GatewayZone(facts);
        var hours = ChangeHoursFor(facts, plan.Tiers);
        return new ServiceTierBookingPlan(plan, zone?.Id, hours?.Text, DefaultRunAt(zone, hours));
    }

    /// <summary>
    /// Books <paramref name="action"/> (a restart of the tier, or of every tier on its database)
    /// for <paramref name="runAtUtc"/>. Returns the history row's id.
    /// </summary>
    /// <exception cref="ProjectAccessDeniedException">The person may not manage the Solution.</exception>
    /// <exception cref="PlanValidationException">
    /// Keyed <c>action</c>, <c>runAt</c> (too soon, too far ahead, or outside the customer's
    /// change hours) or <c>environment</c>. Nothing is booked.
    /// </exception>
    public async Task<int> BookAsync(int environmentId, ServiceTierAction action, DateTime runAtUtc, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; service tier booking made outside an authenticated request.");
        if (!IsBookable(action))
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["action"] = "Only a restart can be booked for later." });
        }
        var env = await EnvironmentAsync(environmentId, ct) ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["environment"] = "That environment no longer exists, or is not an on-premises one.",
        });
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);

        var now = Now;
        runAtUtc = DateTime.SpecifyKind(runAtUtc, DateTimeKind.Utc);
        if (runAtUtc < now + MinBookingAhead)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["runAt"] = "Pick a time at least a few minutes from now. To restart now, use Restart." });
        }
        if (runAtUtc > now + MaxBookingAhead)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["runAt"] = "Pick a time within the next 30 days." });
        }

        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That environment no longer exists." });
        var plan = await PlanAsync(env, facts, action, forDialog: false, ct, booking: true);
        if (plan.Refusal is { } refusal)
        {
            throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = refusal.Replace(" Nothing was sent.", string.Empty, StringComparison.Ordinal) });
        }
        // Refused now rather than when it comes due, when nobody is watching.
        // Every tier it touches, not only the first: a group whose tiers set different
        // hours would otherwise be accepted now and refused when it comes due.
        if (GatewayZone(facts) is { } zone
            && AllChangeHours(facts, plan.Tiers).FirstOrDefault(h =>
                !h.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(runAtUtc, zone)))) is { } hours)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["runAt"] = $"The customer's gateway allows changes only from {hours.StartText} to {hours.EndText}, its local time. Pick a time in that window.",
            });
        }

        var (_, kind, words) = Describe(action);
        var userId = _orgContext.CurrentUserId;
        var actor = await AuditActor.ResolveAsync(_db, userId, ct);
        var gatewayId = facts.GatewayId!.Value;
        OeEnvironmentUpgradeAction row;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // The same lock as sending, so two people booking at once end with one booking.
                await LockTierActionsAsync(gatewayId, ct);
                if (await BookedQuery(environmentId).AnyAsync(ct))
                {
                    throw new PlanValidationException(new Dictionary<string, string>
                    {
                        ["environment"] = "A restart is already booked for this environment. Cancel it first to book another time.",
                    });
                }
                row = new OeEnvironmentUpgradeAction
                {
                    OrganizationId = orgId,
                    ProjectId = env.ProjectId,
                    EnvironmentId = environmentId,
                    Kind = kind,
                    Status = UpgradeActionStatus.Pending,
                    RequestedByUserId = userId,
                    RequestedBy = actor,
                    RequestedAt = now,
                    ExecuteAfter = runAtUtc,
                };
                _db.OeEnvironmentUpgradeActions.Add(row);
                _db.AuditLog.Add(BookingAudit(orgId, environmentId, env, actor, userId, now, new Dictionary<string, object?>
                {
                    ["Action"] = $"Book: {words}",
                    ["Project"] = plan.SolutionName,
                    ["Name"] = env.Name,
                    ["ServiceTier"] = facts.TierName,
                    ["ServiceTiers"] = plan.Tiers,
                    ["RunAt"] = runAtUtc,
                }));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }
        _logger.LogInformation(
            "{Action} booked on environment {EnvironmentId} ({Tier}) for {RunAt:o} by user {UserId}, history row {ActionId}.",
            action, environmentId, facts.TierName, runAtUtc, userId, row.Id);
        return row.Id;
    }

    /// <summary>Cancels a booked restart that has not run. Anyone who manages the Solution may.</summary>
    /// <exception cref="PlanValidationException">Keyed <c>environment</c> when it has already run or been cancelled.</exception>
    public async Task CancelBookingAsync(int environmentId, int actionId, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; service tier booking cancelled outside an authenticated request.");
        var env = await EnvironmentAsync(environmentId, ct) ?? throw new PlanValidationException(new Dictionary<string, string>
        {
            ["environment"] = "That environment no longer exists, or is not an on-premises one.",
        });
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);
        var userId = _orgContext.CurrentUserId;
        var actor = await AuditActor.ResolveAsync(_db, userId, ct);
        var now = Now;
        var gatewayId = (await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct))?.GatewayId;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // The lock sending takes, so a cancel and a send cannot both win: whichever
                // gets it second finds the row already claimed.
                if (gatewayId is { } id) await LockTierActionsAsync(id, ct);
                var cancelled = await BookedQuery(environmentId).Where(a => a.Id == actionId)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.Status, UpgradeActionStatus.Cancelled)
                        .SetProperty(a => a.CancelledBy, actor)
                        .SetProperty(a => a.CancelledByUserId, userId)
                        .SetProperty(a => a.CancelledAt, now), ct);
                if (cancelled == 0)
                {
                    throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That booked restart has already run or been cancelled." });
                }
                _db.AuditLog.Add(BookingAudit(orgId, environmentId, env, actor, userId, now, new Dictionary<string, object?>
                {
                    ["Action"] = "Cancel booked restart",
                    ["Project"] = env.ProjectName,
                    ["Name"] = env.Name,
                    ["UpgradeActionId"] = actionId,
                }));
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }
        _logger.LogInformation("Booked restart {ActionId} on environment {EnvironmentId} cancelled by user {UserId}.", actionId, environmentId, userId);
    }

    /// <summary>
    /// Sends a booked restart now: when it comes due (the worker, as the person who booked it)
    /// or when someone presses Run now. Everything is checked again, as for an immediate
    /// restart. A refusal is recorded on the booking as failed; something else still working
    /// on the tiers leaves it waiting until <see cref="BookingGrace"/> past its time.
    /// </summary>
    /// <param name="byWorker">
    /// True from the worker: a person who may no longer manage the Solution is recorded as a
    /// failure rather than thrown, and a busy tier waits rather than refusing.
    /// </param>
    /// <exception cref="ProjectAccessDeniedException">From Run now, when the person may not manage the Solution.</exception>
    /// <exception cref="PlanValidationException">From Run now, when it cannot be sent; the booking stays as it was.</exception>
    public async Task<ServiceTierBookingRun> RunBookingAsync(int actionId, bool byWorker, CancellationToken ct = default)
    {
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; booked restart run outside an organisation scope.");
        var booking = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId && a.Status == UpgradeActionStatus.Pending && a.SentAt == null
                        && (a.Kind == UpgradeActionKind.RestartServiceTier || a.Kind == UpgradeActionKind.RestartServiceTiers))
            .Select(a => new { a.Id, a.EnvironmentId, a.Kind, a.ExecuteAfter, a.RequestedByUserId })
            .FirstOrDefaultAsync(ct);
        if (booking is null)
        {
            if (!byWorker) throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That booked restart has already run or been cancelled." });
            return ServiceTierBookingRun.Gone;
        }
        var action = booking.Kind == UpgradeActionKind.RestartServiceTiers ? ServiceTierAction.RestartAll : ServiceTierAction.Restart;

        var env = await EnvironmentAsync(booking.EnvironmentId, ct);
        if (env is null)
        {
            if (!byWorker) throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That environment is no longer an on-premises one." });
            return await FailBookingAsync(actionId, "The environment is no longer an on-premises one, so the restart wasn't sent.", ct);
        }
        var overdue = Now > booking.ExecuteAfter + BookingGrace;
        if (byWorker)
        {
            // A disabled account keeps its role and teams, so being able to manage the
            // Solution is not enough on its own (#1235).
            if (!await _db.Users.AsNoTracking().AnyAsync(u => u.Id == booking.RequestedByUserId && u.Status == UserStatus.Active, ct)
                || !await _access.CanManageAsync(env.ProjectId, env.CreatedBy, ct))
            {
                return await FailBookingAsync(actionId, "The person who booked this no longer manages this Solution, so the restart wasn't sent.", ct);
            }
            // Not sent hours late, when people may be working: the worker was off or the
            // app was down at the booked time.
            if (overdue)
            {
                return await FailBookingAsync(actionId, "AL Workbench couldn't send it within two hours of the booked time, so the restart wasn't sent. Book it again or restart now.", ct);
            }
        }
        else
        {
            await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);
        }

        var facts = await OnPremisesDeployFacts.LoadAsync(_db, booking.EnvironmentId, ct);
        if (facts is null)
        {
            if (!byWorker) throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That environment no longer exists." });
            return await FailBookingAsync(actionId, "The environment no longer exists, so the restart wasn't sent.", ct);
        }
        var plan = await PlanAsync(env, facts, action, forDialog: false, ct, booking: true);
        if (plan.Refusal is { } refusal)
        {
            if (!byWorker) throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = refusal });
            return await FailBookingAsync(actionId, refusal.Replace(" Nothing was sent.", string.Empty, StringComparison.Ordinal) + " The restart wasn't sent.", ct);
        }
        try
        {
            await SendAsync(orgId, env, facts, plan, action, actionId, ct);
            return ServiceTierBookingRun.Sent;
        }
        catch (PlanValidationException ex) when (byWorker)
        {
            if (ex.Errors.TryGetValue(BusyKey, out var busy))
            {
                if (!overdue) return ServiceTierBookingRun.Waiting;
                return await FailBookingAsync(actionId, $"{busy.Replace("; try again when it has finished.", string.Empty, StringComparison.Ordinal)} for over two hours past the booked time, so the restart wasn't sent.", ct);
            }
            return await FailBookingAsync(actionId, string.Join(" ", ex.Errors.Values), ct);
        }
    }

    /// <summary>
    /// Booked restarts the gateway has since failed or refused, sent in the last two days and
    /// not yet told: each is marked failed with how it went, so the worker tells the person
    /// who booked it once. Returns their ids.
    /// </summary>
    public async Task<List<int>> SettleFailedBookingsAsync(CancellationToken ct = default)
    {
        var since = Now - TimeSpan.FromDays(2);
        var rows = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => (a.Kind == UpgradeActionKind.RestartServiceTier || a.Kind == UpgradeActionKind.RestartServiceTiers)
                        && a.Status == UpgradeActionStatus.Sent
                        && a.SentAt != null && a.SentAt > since
                        // Booked, not pressed: an immediate restart runs at the time it was asked for.
                        && a.ExecuteAfter > a.RequestedAt
                        && a.GatewayCommandId != null)
            .Join(_db.GatewayCommands.AsNoTracking()
                    .Where(c => c.Status == GatewayCommandStatus.Failed || c.Status == GatewayCommandStatus.Expired),
                a => a.GatewayCommandId, c => (long?)c.Id,
                (a, c) => new { a.Id, Command = new ServiceTierActionOutcome.CommandFacts(c.Id, c.Status, c.FailureMessage, c.ResultJson, c.ParametersJson, c.FinishedAt, c.GatewayId), a.Kind })
            .ToListAsync(ct);
        var settled = new List<int>();
        foreach (var row in rows)
        {
            var outcome = ServiceTierActionOutcome.Apply(
                new UpgradeActionRow(row.Id, 0, 0, row.Kind, UpgradeActionStatus.Sent, string.Empty, Now, Now, Now, null, null, null,
                    GatewayCommandId: row.Command.Id),
                row.Command).Outcome;
            var changed = await _db.OeEnvironmentUpgradeActions
                .Where(a => a.Id == row.Id && a.Status == UpgradeActionStatus.Sent)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                    .SetProperty(a => a.Outcome, outcome), ct);
            if (changed > 0) settled.Add(row.Id);
        }
        return settled;
    }

    /// <summary>The due booked restarts of the organisation in scope, oldest first, with who booked each.</summary>
    internal async Task<List<(int Id, int? RequestedBy)>> DueBookingsAsync(CancellationToken ct = default)
    {
        var now = Now;
        var rows = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => (a.Kind == UpgradeActionKind.RestartServiceTier || a.Kind == UpgradeActionKind.RestartServiceTiers)
                        && a.Status == UpgradeActionStatus.Pending && a.SentAt == null && a.ExecuteAfter <= now
                        && a.Environment!.Kind == OeEnvironmentKind.OnPremises)
            .OrderBy(a => a.ExecuteAfter).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.RequestedByUserId })
            .ToListAsync(ct);
        return rows.Select(r => (r.Id, r.RequestedByUserId)).ToList();
    }

    private IQueryable<OeEnvironmentUpgradeAction> BookedQuery(int environmentId) =>
        _db.OeEnvironmentUpgradeActions.Where(a => a.EnvironmentId == environmentId
                                                   && (a.Kind == UpgradeActionKind.RestartServiceTier || a.Kind == UpgradeActionKind.RestartServiceTiers)
                                                   && a.Status == UpgradeActionStatus.Pending && a.SentAt == null);

    private async Task<List<ServiceTierBooking>> BookingsAsync(int environmentId, CancellationToken ct) =>
        (await BookedQuery(environmentId).AsNoTracking()
            .OrderBy(a => a.ExecuteAfter)
            .Select(a => new { a.Id, a.Kind, a.ExecuteAfter, a.RequestedBy, a.RequestedAt })
            .ToListAsync(ct))
        .Select(a => new ServiceTierBooking(a.Id, a.Kind == UpgradeActionKind.RestartServiceTiers ? ServiceTierAction.RestartAll : ServiceTierAction.Restart,
            a.ExecuteAfter, a.RequestedBy, a.RequestedAt))
        .ToList();

    private async Task<ServiceTierBookingRun> FailBookingAsync(int actionId, string outcome, CancellationToken ct)
    {
        var changed = await _db.OeEnvironmentUpgradeActions
            .Where(a => a.Id == actionId && a.Status == UpgradeActionStatus.Pending && a.SentAt == null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                .SetProperty(a => a.Outcome, outcome), ct);
        if (changed == 0) return ServiceTierBookingRun.Gone;
        _logger.LogInformation("Booked restart {ActionId} was not sent: {Outcome}", actionId, outcome);
        return ServiceTierBookingRun.Failed;
    }

    private static AuditLogEntry BookingAudit(int orgId, int environmentId, EnvironmentRow env, string actor, int? userId, DateTime now,
        Dictionary<string, object?> snapshot) => new()
    {
        Timestamp = now,
        ChangedBy = actor,
        ChangedByUserId = userId,
        OrganizationId = orgId,
        EntityType = AuditEntityType.ProjectEnvironment,
        EntityId = environmentId,
        Action = AuditAction.Updated,
        EntityName = env.Name,
        SnapshotJson = JsonSerializer.Serialize(snapshot, PersistenceJson.Options),
    };

    /// <summary>The gateway computer's time zone, when its settings report named one this server knows.</summary>
    private static TimeZoneInfo? GatewayZone(OnPremisesDeployFacts facts)
    {
        if (facts.Policy?.TimeZone is not { Length: > 0 } id) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    /// <summary>
    /// The change hours that hold for every tier in <paramref name="tiers"/>: one window when
    /// they all set the same one (the usual case, from the file's defaults); the first tier's
    /// when they differ, which the booking is checked against too. Null when none sets any.
    /// </summary>
    private static ChangeHours? ChangeHoursFor(OnPremisesDeployFacts facts, IReadOnlyList<string> tiers)
    {
        if (facts.Policy is not { } policy) return null;
        return tiers.Select(t => PolicyEntry(policy, t)?.ChangeHours)
            .Select(ChangeHours.Parse)
            .FirstOrDefault(h => h is not null);
    }

    private static IEnumerable<ChangeHours> AllChangeHours(OnPremisesDeployFacts facts, IReadOnlyList<string> tiers) =>
        facts.Policy is not { } policy
            ? []
            : tiers.Select(t => ChangeHours.Parse(PolicyEntry(policy, t)?.ChangeHours)).OfType<ChangeHours>();

    private DateTime DefaultRunAt(TimeZoneInfo? zone, ChangeHours? hours)
    {
        var now = Now;
        if (zone is null || hours is null)
        {
            var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(1);
            return next < now + MinBookingAhead ? next.AddHours(1) : next;
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
        var opening = local.Date + hours.Start.ToTimeSpan();
        if (opening <= local + MinBookingAhead) opening = opening.AddDays(1);
        // The night the clocks go forward skips an hour; an opening inside it starts when the clocks resume.
        if (zone.IsInvalidTime(opening)) opening = opening.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(opening, DateTimeKind.Unspecified), zone);
    }

    /// <summary>A tier's <c>change_hours</c> as the gateway reports them (<c>20:00-06:00</c>), read as the gateway reads them.</summary>
    internal sealed record ChangeHours(TimeOnly Start, TimeOnly End)
    {
        public string StartText => Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        public string EndText => End.ToString("HH:mm", CultureInfo.InvariantCulture);
        public string Text => $"{StartText}-{EndText}";

        /// <summary>From the start, up to but not including the end; a window past midnight wraps.</summary>
        public bool Contains(TimeOnly time) => Start < End ? time >= Start && time < End : time >= Start || time < End;

        public static ChangeHours? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split('-', 2, StringSplitOptions.TrimEntries);
            return parts.Length == 2
                   && TimeOnly.TryParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                   && TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)
                   && start != end
                ? new ChangeHours(start, end)
                : null;
        }
    }
}
