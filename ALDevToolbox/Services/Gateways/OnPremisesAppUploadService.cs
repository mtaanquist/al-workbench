using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Delivery;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>One <c>.app</c> file chosen for an on-premises upload.</summary>
public sealed record OnPremisesUploadFile(byte[] Bytes, string FileName);

/// <summary>One app of an on-premises upload, read from its manifest, in the order it installs.</summary>
public sealed record OnPremisesUploadApp(string FileName, Guid AppId, string Name, string Publisher, string Version, bool Signed, int Size);

/// <summary>
/// What uploading to an on-premises environment would do, before anything is stored or
/// sent (#1786): the apps in install order, and why it would be refused.
/// </summary>
/// <param name="Apps">The apps in dependency order; empty when no files were given or one could not be read.</param>
/// <param name="Refusal">Why it cannot be done, said for the person; null when it can.</param>
/// <param name="TimeZone">The gateway computer's time zone (a Windows id), which <paramref name="ChangeHours"/> are in; null when the gateway did not say.</param>
/// <param name="ChangeHours">The customer's change hours for the tiers the install restarts (<c>20:00-06:00</c>); null when any time is allowed.</param>
/// <param name="DefaultRunAt">The time a booking starts on, UTC: the change hours' next opening, else the next full hour.</param>
/// <param name="InsideChangeHoursNow">False when the customer's change hours are known and it is outside them now, so installing now would be refused.</param>
public sealed record OnPremisesUploadPlan(
    string EnvironmentName,
    bool IsProduction,
    string TierName,
    IReadOnlyList<OnPremisesUploadApp> Apps,
    string? Refusal,
    string? TimeZone,
    string? ChangeHours,
    DateTime DefaultRunAt,
    bool InsideChangeHoursNow);

/// <summary>What <see cref="OnPremisesAppUploadService.InstallAsync"/> did: how many apps, and when they install (null for now).</summary>
public sealed record OnPremisesUploadOutcome(int Count, DateTime? RunsAtUtc);

/// <summary>
/// Uploading apps by hand to an on-premises environment, through its gateway (#1786): for
/// an app another company sent, which no pipeline here builds. Installed now, or booked for
/// a later time. Several apps go in dependency order, read from their manifests, in one
/// <c>publish-install</c> command, as a pipeline deployment sends them, so the gateway
/// checks, installs and restarts them the same way.
///
/// <para>Everything that can be known before anything is stored is checked here and said
/// as a refusal: the customer's gateway settings (publishing, publishers, unsigned apps,
/// change hours, the shared restart), a file that cannot be read, and a dependency that is
/// neither in the upload nor installed. The gateway checks it all again when it runs.</para>
///
/// <para>Each app is an <see cref="UpgradeActionKind.UploadApp"/> history row, as online, so
/// the Scheduled installs list, Install now and Cancel work unchanged. A booking holds the
/// file on the row with no command; <see cref="ServiceTierBookingWorker"/> sends it when it
/// is due, as the person who booked it. A sent row keeps its command's id, and how it went
/// is read from the command (<see cref="OnPremisesUploadResult"/>). See
/// <c>.design/on-prem-gateway.md</c>, "Uploading apps by hand, as built (#1786)".</para>
/// </summary>
public sealed class OnPremisesAppUploadService
{
    /// <summary>The most apps one upload takes, as online.</summary>
    public const int MaxApps = ProjectConnectionService.MaxUploadBatch;

    /// <summary>The largest app taken, as online.</summary>
    public const int MaxAppBytes = BcAppManagementClient.MaxAppBytes;

    /// <summary>How soon a booking can be for.</summary>
    internal static readonly TimeSpan MinBookingAhead = OnPremisesTierActionService.MinBookingAhead;

    /// <summary>How far ahead an upload can be booked.</summary>
    internal static readonly TimeSpan MaxBookingAhead = OnPremisesTierActionService.MaxBookingAhead;

    /// <summary>How long past its time a booked upload may still be sent; later than that it is given up, as a booked restart is.</summary>
    internal static readonly TimeSpan BookingGrace = OnPremisesTierActionService.BookingGrace;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly GatewayCommandService _commands;
    private readonly TimeProvider _clock;
    private readonly ILogger<OnPremisesAppUploadService> _logger;

    public OnPremisesAppUploadService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        GatewayCommandService commands,
        TimeProvider clock,
        ILogger<OnPremisesAppUploadService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _commands = commands;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private sealed record EnvironmentRow(int ProjectId, string Name, string Type, int? CreatedBy);

    private async Task<EnvironmentRow?> EnvironmentAsync(int environmentId, CancellationToken ct) =>
        await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new EnvironmentRow(e.ProjectId, e.Name, e.Type, e.Project!.CreatedByUserId))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// What uploading <paramref name="files"/> to <paramref name="environmentId"/> would do;
    /// with no files, only whether it can be done at all and when a booking would start.
    /// Null when it is not an on-premises environment. Needs the right to manage the Solution.
    /// </summary>
    public async Task<OnPremisesUploadPlan?> PlanAsync(int environmentId, IReadOnlyList<OnPremisesUploadFile> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var env = await EnvironmentAsync(environmentId, ct);
        if (env is null) return null;
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct);
        if (facts is null) return null;
        return await PlanAsync(env, facts, files, ct);
    }

    private async Task<OnPremisesUploadPlan> PlanAsync(EnvironmentRow env, OnPremisesDeployFacts facts, IReadOnlyList<OnPremisesUploadFile> files, CancellationToken ct)
    {
        var group = facts.EnvironmentUnlinked ? null : await RestartGroupPlan.LoadAsync(_db, facts, ct);
        var tiers = group?.Tiers is { Count: > 0 } t ? t : [facts.TierName];
        var zone = OnPremisesTierActionService.GatewayZone(facts);
        var hours = OnPremisesTierActionService.AllChangeHours(facts, tiers).ToList();
        var insideNow = zone is null || hours.All(h => h.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(Now, zone))));
        var defaultRunAt = OnPremisesTierActionService.DefaultRunAt(Now, zone, hours.FirstOrDefault());

        var (apps, fileRefusal) = ReadApps(files);
        var refusal = ServerRefusal(facts, group) ?? fileRefusal;
        if (refusal is null && apps.Count > 0)
        {
            refusal = PolicyRefusalFor(facts, apps) ?? await MissingDependencyAsync(facts, files, apps, ct);
        }
        return new OnPremisesUploadPlan(env.Name, IsProduction(env.Type), facts.TierName, apps, refusal,
            zone?.Id, hours.FirstOrDefault()?.Text, defaultRunAt, insideNow);
    }

    /// <summary>
    /// Uploads <paramref name="files"/> to <paramref name="environmentId"/>: sent to the
    /// gateway now when <paramref name="runAtUtc"/> is null, otherwise booked for that time.
    /// </summary>
    /// <exception cref="ProjectAccessDeniedException">The person may not manage the Solution.</exception>
    /// <exception cref="PlanValidationException">
    /// Keyed <c>App</c> (the files), <c>runAt</c> (the time) or <c>environment</c> (the
    /// customer's settings, the service tier). Nothing is stored or sent.
    /// </exception>
    public async Task<OnPremisesUploadOutcome> InstallAsync(
        int environmentId, IReadOnlyList<OnPremisesUploadFile> files, DateTime? runAtUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; app upload made outside an authenticated request.");
        if (files.Count == 0) throw Refused("App", "Choose at least one extension package - a file ending in .app.");
        if (files.Count > MaxApps) throw Refused("App", $"Choose at most {MaxApps} apps at a time.");

        var env = await EnvironmentAsync(environmentId, ct) ?? throw Refused("environment", "That environment no longer exists, or is not an on-premises one.");
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct)
            ?? throw Refused("environment", "That environment no longer exists.");

        var now = Now;
        if (runAtUtc is { } at)
        {
            at = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            runAtUtc = at;
            if (at < now + MinBookingAhead) throw Refused("runAt", "Pick a time at least a few minutes from now, or install now.");
            if (at > now + MaxBookingAhead) throw Refused("runAt", "Pick a time within the next 30 days.");
        }

        var plan = await PlanAsync(env, facts, files, ct);
        if (plan.Refusal is { } refusal)
        {
            throw Refused(plan.Apps.Count == 0 && ServerRefusal(facts, null) is null ? "App" : "environment", refusal);
        }
        var zone = OnPremisesTierActionService.GatewayZone(facts);
        var when = runAtUtc ?? now;
        if (zone is not null && plan.ChangeHours is not null)
        {
            var group = await RestartGroupPlan.LoadAsync(_db, facts, ct);
            var tiers = group?.Tiers is { Count: > 0 } t ? t : [facts.TierName];
            var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(when, zone));
            if (OnPremisesTierActionService.AllChangeHours(facts, tiers).FirstOrDefault(h => !h.Contains(local)) is { } hours)
            {
                throw Refused(runAtUtc is null ? "environment" : "runAt", runAtUtc is null
                    ? $"The customer's gateway allows changes only from {hours.StartText} to {hours.EndText}, its local time. Book the install for a time in that window."
                    : $"The customer's gateway allows changes only from {hours.StartText} to {hours.EndText}, its local time. Pick a time in that window.");
            }
        }

        var byName = files.ToDictionary(f => CleanName(f.FileName), f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var userId = _orgContext.CurrentUserId;
        var actor = await AuditActor.ResolveAsync(_db, userId, ct);
        var batchId = plan.Apps.Count > 1 ? Guid.NewGuid() : (Guid?)null;
        var rows = plan.Apps.Select((app, i) => new OeEnvironmentUpgradeAction
        {
            OrganizationId = orgId,
            ProjectId = env.ProjectId,
            EnvironmentId = environmentId,
            Kind = UpgradeActionKind.UploadApp,
            Status = UpgradeActionStatus.Pending,
            RequestedByUserId = userId,
            RequestedBy = actor,
            RequestedAt = now,
            ExecuteAfter = when,
            PackageFileName = app.FileName,
            AppName = app.Name,
            BcAppId = app.AppId,
            TargetVersion = app.Version,
            PackageContent = byName[app.FileName],
            BatchId = batchId,
            BatchOrder = batchId is null ? null : i,
        }).ToList();

        if (runAtUtc is not null)
        {
            _db.OeEnvironmentUpgradeActions.AddRange(rows);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            finally
            {
                // Up to ten packages: once written they are the worker's, and the page's context
                // would otherwise hold them for the life of the circuit (#1265).
                foreach (var row in rows) _db.Entry(row).State = EntityState.Detached;
            }
            _logger.LogInformation(
                "User {UserId} booked {Count} app(s) ({Files}) for on-premises environment {EnvironmentId} at {RunAt:o}.",
                userId, rows.Count, string.Join(", ", plan.Apps.Select(a => a.FileName)), environmentId, runAtUtc);
            return new OnPremisesUploadOutcome(rows.Count, runAtUtc);
        }

        // Now: the command and the history rows become visible together.
        foreach (var row in rows)
        {
            row.Status = UpgradeActionStatus.Sent;
            row.SentAt = now;
            row.PackageContent = null;
        }
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                var commandId = await EnqueueAsync(facts, plan.Apps, byName, userId, ct);
                foreach (var row in rows) row.GatewayCommandId = commandId;
                _db.OeEnvironmentUpgradeActions.AddRange(rows);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }
        _commands.Wake(facts.GatewayId!.Value);
        _logger.LogInformation(
            "User {UserId} sent {Count} app(s) ({Files}) to on-premises environment {EnvironmentId} ({Tier}).",
            userId, rows.Count, string.Join(", ", plan.Apps.Select(a => a.FileName)), environmentId, facts.TierName);
        return new OnPremisesUploadOutcome(rows.Count, null);
    }

    /// <summary>
    /// Sends a booked upload's batch, every app of it still waiting, when it comes due (the
    /// worker, as the person who booked it). Everything is checked again from the stored
    /// files. A refusal fails every waiting app of the batch, with the reason.
    /// </summary>
    public async Task<ServiceTierBookingRun> RunBookingAsync(int actionId, CancellationToken ct = default)
    {
        var first = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Id == actionId && a.Kind == UpgradeActionKind.UploadApp
                        && a.Status == UpgradeActionStatus.Pending && a.SentAt == null
                        && a.Environment!.Kind == OeEnvironmentKind.OnPremises)
            .Select(a => new { a.Id, a.EnvironmentId, a.BatchId, a.ExecuteAfter, a.RequestedByUserId })
            .FirstOrDefaultAsync(ct);
        if (first is null) return ServiceTierBookingRun.Gone;

        IQueryable<OeEnvironmentUpgradeAction> Batch() => _db.OeEnvironmentUpgradeActions
            .Where(a => a.Status == UpgradeActionStatus.Pending && a.SentAt == null && a.Kind == UpgradeActionKind.UploadApp)
            .Where(a => first.BatchId != null ? a.BatchId == first.BatchId : a.Id == first.Id);

        async Task<ServiceTierBookingRun> Fail(string outcome)
        {
            var failed = await Batch().ExecuteUpdateAsync(u => u
                .SetProperty(a => a.Status, UpgradeActionStatus.Failed)
                .SetProperty(a => a.PackageContent, (byte[]?)null)
                .SetProperty(a => a.Outcome, outcome), ct);
            if (failed == 0) return ServiceTierBookingRun.Gone;
            _logger.LogInformation("Booked upload {ActionId} was not sent: {Outcome}", actionId, outcome);
            return ServiceTierBookingRun.Failed;
        }

        var env = await EnvironmentAsync(first.EnvironmentId, ct);
        if (env is null) return await Fail("The environment is no longer an on-premises one, so the app wasn't installed.");
        // A disabled account keeps its role and teams, so being able to manage the Solution is not enough on its own.
        if (!await _db.Users.AsNoTracking().AnyAsync(u => u.Id == first.RequestedByUserId && u.Status == UserStatus.Active, ct)
            || !await _access.CanManageAsync(env.ProjectId, env.CreatedBy, ct))
        {
            return await Fail("The person who booked this no longer manages this Solution, so the app wasn't installed.");
        }
        if (Now > first.ExecuteAfter + BookingGrace)
        {
            return await Fail("AL Workbench couldn't send it within two hours of the booked time, so the app wasn't installed. Upload it again.");
        }
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, first.EnvironmentId, ct);
        if (facts is null) return await Fail("The environment no longer exists, so the app wasn't installed.");

        var stored = await Batch().AsNoTracking()
            .OrderBy(a => a.BatchOrder ?? 0).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.PackageFileName, a.PackageContent })
            .ToListAsync(ct);
        if (stored.Count == 0) return ServiceTierBookingRun.Gone;
        var files = stored.Where(s => s.PackageContent is not null && s.PackageFileName is not null)
            .Select(s => new OnPremisesUploadFile(s.PackageContent!, s.PackageFileName!)).ToList();
        var plan = await PlanAsync(env, facts, files, ct);
        if (plan.Refusal is { } refusal)
        {
            return await Fail(refusal.Replace(" Nothing was sent.", string.Empty, StringComparison.Ordinal) + " The app wasn't installed.");
        }
        if (!plan.InsideChangeHoursNow)
        {
            return await Fail("It was due outside the customer's change hours, so the app wasn't installed.");
        }

        var byName = files.ToDictionary(f => CleanName(f.FileName), f => f.Bytes, StringComparer.OrdinalIgnoreCase);
        var ids = stored.Select(s => s.Id).ToList();
        var now = Now;
        long commandId;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                // Claimed first, compare-and-set: a cancel that got here first leaves fewer
                // rows, and the batch is sent as it stands now.
                var claimed = await Batch().Where(a => ids.Contains(a.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(a => a.SentAt, now), ct);
                if (claimed == 0)
                {
                    await tx.RollbackAsync(ct);
                    return ServiceTierBookingRun.Gone;
                }
                var still = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
                    .Where(a => ids.Contains(a.Id) && a.SentAt == now && a.Status == UpgradeActionStatus.Pending)
                    .Select(a => a.PackageFileName!)
                    .ToListAsync(ct);
                var apps = plan.Apps.Where(a => still.Contains(a.FileName, StringComparer.OrdinalIgnoreCase)).ToList();
                commandId = await EnqueueAsync(facts, apps, byName, first.RequestedByUserId, ct);
                await _db.OeEnvironmentUpgradeActions
                    .Where(a => ids.Contains(a.Id) && a.SentAt == now && a.Status == UpgradeActionStatus.Pending)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.Status, UpgradeActionStatus.Sent)
                        .SetProperty(a => a.GatewayCommandId, commandId)
                        .SetProperty(a => a.PackageContent, (byte[]?)null), ct);
                await tx.CommitAsync(ct);
            }
            catch (PlanValidationException ex)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                return await Fail(string.Join(" ", ex.Errors.Values));
            }
            catch
            {
                _db.ChangeTracker.Clear();
                throw;
            }
        }
        _commands.Wake(facts.GatewayId!.Value);
        _logger.LogInformation("Booked upload {ActionId} sent to on-premises environment {EnvironmentId} as command {CommandId}.",
            actionId, first.EnvironmentId, commandId);
        return ServiceTierBookingRun.Sent;
    }

    /// <summary>
    /// Booked uploads the gateway has since failed or let expire, sent in the last two days
    /// and not yet told: each app is marked failed with how it went, so the worker tells the
    /// person who booked it once. Returns one id per batch, to tell them once per upload.
    /// </summary>
    public async Task<List<int>> SettleFailedBookingsAsync(CancellationToken ct = default)
    {
        var since = Now - TimeSpan.FromDays(2);
        var rows = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Kind == UpgradeActionKind.UploadApp
                        && a.Status == UpgradeActionStatus.Sent
                        && a.SentAt != null && a.SentAt > since
                        // Booked, not sent at once: the person who pressed Install now sees how it goes.
                        && a.ExecuteAfter > a.RequestedAt
                        && a.GatewayCommandId != null
                        && a.Environment!.Kind == OeEnvironmentKind.OnPremises)
            .Join(_db.GatewayCommands.AsNoTracking()
                    .Where(c => c.Status == GatewayCommandStatus.Failed || c.Status == GatewayCommandStatus.Expired),
                a => a.GatewayCommandId, c => (long?)c.Id,
                (a, c) => new { a.Id, a.Kind, a.PackageFileName, a.BatchOrder, a.SentAt, Command = new ServiceTierActionOutcome.CommandFacts(c.Id, c.Status, c.FailureMessage, c.ResultJson, c.ParametersJson, c.FinishedAt, c.GatewayId) })
            .ToListAsync(ct);
        var told = new List<int>();
        foreach (var batch in rows.GroupBy(r => r.Command.Id))
        {
            var settled = new List<int>();
            foreach (var row in batch)
            {
                var read = OnPremisesUploadResult.Apply(
                    new UpgradeActionRow(row.Id, 0, 0, row.Kind, UpgradeActionStatus.Sent, string.Empty, Now, Now, row.SentAt, null, null, null,
                        PackageFileName: row.PackageFileName, BatchOrder: row.BatchOrder, GatewayCommandId: row.Command.Id),
                    row.Command);
                var changed = await _db.OeEnvironmentUpgradeActions
                    .Where(a => a.Id == row.Id && a.Status == UpgradeActionStatus.Sent)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(a => a.Status, read.Status == UpgradeActionStatus.Sent ? UpgradeActionStatus.Sent : UpgradeActionStatus.Failed)
                        .SetProperty(a => a.Outcome, read.Outcome), ct);
                if (changed > 0 && read.Status != UpgradeActionStatus.Sent) settled.Add(row.Id);
            }
            if (settled.Count > 0) told.Add(settled.Min());
        }
        return told;
    }

    /// <summary>The due booked uploads of the organisation in scope, one per batch, oldest first, with who booked each.</summary>
    internal async Task<List<(int Id, int? RequestedBy)>> DueBookingsAsync(CancellationToken ct = default)
    {
        var now = Now;
        var rows = await _db.OeEnvironmentUpgradeActions.AsNoTracking()
            .Where(a => a.Kind == UpgradeActionKind.UploadApp
                        && a.Status == UpgradeActionStatus.Pending && a.SentAt == null && a.ExecuteAfter <= now
                        && a.Environment!.Kind == OeEnvironmentKind.OnPremises)
            .OrderBy(a => a.ExecuteAfter).ThenBy(a => a.BatchOrder ?? 0).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.BatchId, a.RequestedByUserId })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => r.BatchId?.ToString() ?? $"#{r.Id}")
            .Select(g => (g.First().Id, g.First().RequestedByUserId))
            .ToList();
    }

    // ── Checks ──────────────────────────────────────────────────────────

    /// <summary>Why nothing can be published on this environment's tier at all, whatever the files; null when it can.</summary>
    private static string? ServerRefusal(OnPremisesDeployFacts facts, RestartGroupPlan? group)
    {
        if (facts.EnvironmentUnlinked) return "This environment is no longer linked to a service tier, so apps can't be installed on it.";
        if (facts.InstanceMissing) return $"The gateway no longer reports {facts.TierName}. Check the service tier with the customer's IT.";
        if (facts.TenantMissing) return $"The gateway no longer reports the tenant {facts.TenantId} on {facts.TierName}. Check the tenant with the customer's IT.";
        if (facts.PolicyRefusal is { } policy) return policy.Replace(" Nothing was sent.", string.Empty, StringComparison.Ordinal);
        if (facts.InstanceState is { Length: > 0 } state && !string.Equals(state.Trim(), "Running", StringComparison.OrdinalIgnoreCase))
        {
            return $"The gateway last read {facts.TierName} as {BcEnvironmentStatus.StatusWord(state).ToLowerInvariant()}, so it can't take an install. Start the service tier, then try again.";
        }
        return group?.RefusalFor("install", forDialog: true);
    }

    /// <summary>The apps read from their manifests, in dependency order, or why one could not be taken.</summary>
    private static (List<OnPremisesUploadApp> Apps, string? Refusal) ReadApps(IReadOnlyList<OnPremisesUploadFile> files)
    {
        if (files.Count > MaxApps) return ([], $"Choose at most {MaxApps} apps at a time.");
        var read = new List<ReadApp>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            ArgumentNullException.ThrowIfNull(file.Bytes);
            var name = CleanName(file.FileName);
            if (!name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return ([], $"{(name.Length == 0 ? "One of the files" : name)} isn't an extension package - choose files ending in .app.");
            }
            if (!names.Add(name)) return ([], $"Two of the files are called {name}. Rename one and choose them again.");
            if (file.Bytes.Length == 0) return ([], $"{name} is empty.");
            if (file.Bytes.Length > MaxAppBytes) return ([], $"{name} is over {MaxAppBytes / (1024 * 1024)} MB, the largest app AL Workbench takes.");
            AppManifest? manifest;
            using (var stream = new MemoryStream(file.Bytes, writable: false))
            {
                manifest = AppPackageReader.TryReadManifest(stream);
            }
            if (manifest is null)
            {
                return ([], $"{name} can't be read as an app file (it may be protected), so the customer's gateway can't check it. Ask whoever sent it for an unprotected copy.");
            }
            read.Add(new ReadApp(new OnPremisesUploadApp(name, manifest.AppId, manifest.Name, manifest.Publisher, manifest.Version,
                GatewayDeliveryTarget.CarriesSignature(file.Bytes), file.Bytes.Length), manifest));
        }
        if (read.GroupBy(r => r.App.AppId).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return ([], $"{twice.First().App.Name} is chosen twice. Choose one version of it.");
        }
        var ordered = DependencyOrder.Sort(read,
            r => r.App.AppId.ToString(),
            r => r.Manifest.Dependencies.Select(d => d.AppId.ToString()));
        return (ordered.Select(r => r.App).ToList(), null);
    }

    private sealed record ReadApp(OnPremisesUploadApp App, AppManifest Manifest);

    /// <summary>The customer's publisher allow-list and unsigned rule, for each app; null when they all pass.</summary>
    private static string? PolicyRefusalFor(OnPremisesDeployFacts facts, IReadOnlyList<OnPremisesUploadApp> apps)
    {
        if (facts.PublishingPolicy is not { } policy) return null;
        foreach (var app in apps)
        {
            if (policy.Publishers is { } publishers && !publishers.Contains(app.Publisher.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                return $"{OnPremisesDeployFacts.NotAllowed}: apps from {app.Publisher} may not be installed on {facts.TierName}. Ask the customer's IT to allow that publisher.";
            }
            if (!app.Signed && !policy.AllowUnsigned)
            {
                return $"{OnPremisesDeployFacts.NotAllowed}: {app.Name} isn't signed, and {facts.TierName} takes only signed apps. Ask whoever sent it for a signed copy.";
            }
        }
        return null;
    }

    /// <summary>
    /// The first dependency that is neither in the upload nor installed on the tenant at the
    /// version asked for, said as a refusal; null when every one is there, or when the
    /// gateway has not reported the tenant's apps, since a check against nothing would refuse everything.
    /// </summary>
    private async Task<string?> MissingDependencyAsync(OnPremisesDeployFacts facts, IReadOnlyList<OnPremisesUploadFile> files, IReadOnlyList<OnPremisesUploadApp> apps, CancellationToken ct)
    {
        var tenant = (facts.TenantId ?? OnPremisesEnvironmentService.DefaultTenant).ToLower();
        var installed = (await _db.GatewayInstanceApps.AsNoTracking()
                .Where(a => a.Tenant!.InstanceId == facts.InstanceId
                            && a.Tenant.TenantId.ToLower() == tenant
                            && a.MissingSince == null
                            && a.IsInstalled == true)
                .Select(a => new { a.AppId, a.Version })
                .ToListAsync(ct))
            .Where(a => Guid.TryParse(a.AppId, out _))
            .GroupBy(a => Guid.Parse(a.AppId))
            .ToDictionary(g => g.Key, g => g.First().Version);
        if (installed.Count == 0) return null;
        var uploading = apps.ToDictionary(a => a.AppId, a => a.Version);

        foreach (var file in files)
        {
            AppManifest? manifest;
            using (var stream = new MemoryStream(file.Bytes, writable: false))
            {
                manifest = AppPackageReader.TryReadManifest(stream);
            }
            if (manifest is null) continue;
            foreach (var dep in manifest.Dependencies)
            {
                var have = uploading.GetValueOrDefault(dep.AppId) ?? installed.GetValueOrDefault(dep.AppId);
                if (have is not null && AtLeast(have, dep.Version)) continue;
                return have is null
                    ? $"{manifest.Name} needs {dep.Name} by {dep.Publisher}, which isn't installed on {facts.EnvironmentName} or in this upload. Add it to the upload, or install it first."
                    : $"{manifest.Name} needs {dep.Name} {dep.Version} or later, and {facts.EnvironmentName} has {have}. Add the newer {dep.Name} to the upload.";
            }
        }
        return null;
    }

    private static bool AtLeast(string have, string need) =>
        !Version.TryParse(have, out var h) || !Version.TryParse(need, out var n) || h >= n;

    // ── Sending ─────────────────────────────────────────────────────────

    /// <summary>Queues the one <c>publish-install</c> command for <paramref name="apps"/>, in that order, with the files inline.</summary>
    private async Task<long> EnqueueAsync(OnPremisesDeployFacts facts, IReadOnlyList<OnPremisesUploadApp> apps,
        IReadOnlyDictionary<string, byte[]> byName, int? userId, CancellationToken ct)
    {
        var (machine, instance) = facts.PublishingTier;
        var parameters = new GatewayPublishInstallParameters
        {
            Machine = machine,
            Instance = instance,
            // Left out on a single-tenant server, where the gateway uses "default".
            Tenant = facts.Multitenant ? facts.TenantId : null,
            SyncMode = GatewaySyncModes.Add,
            // The previous version is unpublished only where the customer allows it; left
            // published otherwise, which the gateway accepts.
            UnpublishPrevious = facts.PublishingPolicy?.Allow.Contains("unpublish", StringComparer.OrdinalIgnoreCase) == true,
            Apps = apps.Select(a => new GatewayPublishInstallApp
            {
                File = a.FileName,
                Id = a.AppId,
                Name = a.Name,
                Publisher = a.Publisher,
                Version = a.Version,
                Signed = a.Signed,
            }).ToList(),
        };
        try
        {
            return await _commands.EnqueueAsync(new GatewayCommandRequest(
                facts.GatewayId!.Value,
                GatewayVerb.PublishInstall,
                parameters.ToParameters(),
                RequestedByUserId: userId,
                Files: apps.Select(a => new GatewayCommandFileInput(a.FileName, byName[a.FileName])).ToList()), ct);
        }
        catch (PlanValidationException ex)
        {
            throw Refused("environment", string.Join(" ", ex.Errors.Values) + " Nothing was sent.");
        }
    }

    private static string CleanName(string? fileName) => Path.GetFileName((fileName ?? string.Empty).Trim());

    private static bool IsProduction(string type) => string.Equals(type, "Production", StringComparison.OrdinalIgnoreCase);

    private static PlanValidationException Refused(string key, string message) =>
        new(new Dictionary<string, string> { [key] = message });
}
