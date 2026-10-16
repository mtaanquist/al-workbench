using System.Buffers.Binary;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Domain.ValueObjects.ObjectExplorer;
using ALDevToolbox.Services.Gateways;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALDevToolbox.Services.ObjectExplorer.Import;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.ObjectExplorer.Delivery;

/// <summary>
/// The <see cref="IDeliveryTarget"/> for a linked on-premises service tier: the build goes
/// to the tier's gateway as one <c>publish-install</c> command, and the gateway publishes,
/// syncs and installs each app in order on the database's publishing tier (#1375).
/// Everything this target reads about the tier - its state, its apps, its sessions, what
/// the customer's gateway settings allow - comes from the inventory the gateway pushes,
/// never from the tier itself. Built per environment by <see cref="DeliveryTargetFactory"/>,
/// never DI-registered. See <c>.design/on-prem-gateway.md</c>, "Deploying to on-premises".
/// <para>
/// The shared run is per app (start one, wait for it, next), and the gateway is per
/// command, which may run for hours. So the two do not wait together:
/// <see cref="PrepareInstallsAsync"/> queues the one command with every app still to send
/// and hands the run off (<see cref="DeliveryHandOff"/>); the run records the command on
/// the deployment and ends, freeing its worker; and
/// <c>DeliveryService.FollowGatewayDeliveriesAsync</c> copies the command's progress into
/// the deployment's log and finishes it from the command's result, app by app, through
/// <see cref="OutcomeFor"/>. <see cref="StartInstallAsync"/> and
/// <see cref="WaitForInstallAsync"/> are therefore never called.
/// </para>
/// </summary>
public sealed class GatewayDeliveryTarget : IDeliveryTarget
{
    /// <summary>
    /// The longest a deployment follows one command. The command queue normally ends it
    /// long before: a command nobody collects expires, and one whose gateway goes quiet is
    /// failed. This only stops a deployment being followed for ever with the sweep switched off.
    /// </summary>
    internal static readonly TimeSpan HardWaitLimit = TimeSpan.FromHours(24);

    private readonly DeliveryTargetEnvironment _environment;
    private readonly AppDbContext _db;
    private readonly GatewayCommandService _commands;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private OnPremisesDeployFacts? _facts;
    private long? _commandId;

    public GatewayDeliveryTarget(
        DeliveryTargetEnvironment environment,
        AppDbContext db,
        GatewayCommandService commands,
        TimeProvider clock,
        ILogger logger)
    {
        _environment = environment;
        _db = db;
        _commands = commands;
        _clock = clock;
        _logger = logger;
    }

    public DeliveryTargetCapabilities Capabilities => DeliveryTargetFactory.OnPremisesCapabilities;

    /// <summary>The command this run queued, once it has.</summary>
    public long? CommandId => _commandId;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private OnPremisesDeployFacts Facts => _facts
        ?? throw new InvalidOperationException("ConnectAsync must be called before the environment is.");

    /// <summary>
    /// Reads the tier and its gateway, and refuses at once when the gateway cannot take a
    /// command: a due deployment whose gateway is offline fails now, with "The gateway was
    /// offline", rather than waiting for a gateway that may not come back in its window.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        var facts = _environment.EnvironmentId is { } id ? await OnPremisesDeployFacts.LoadAsync(_db, id, ct) : null;
        if (facts is null || facts.EnvironmentUnlinked)
        {
            throw new DeliveryTargetException(
                $"{_environment.Name} is no longer linked to a service tier, so nothing was sent. An administrator can link it again on the solution's Environments tab.");
        }
        _facts = facts;
        switch (facts.GatewayStatusAt(Now))
        {
            case GatewayStatus.Online:
                return;
            case GatewayStatus.Revoked:
                throw new DeliveryTargetException(
                    $"The gateway {facts.GatewayName} has been revoked, so nothing was sent. An administrator can set up a gateway under Administration → Gateways.");
            case GatewayStatus.WaitingForApproval:
                throw new DeliveryTargetException(
                    $"The gateway {facts.GatewayName} is waiting for an administrator to approve it under Administration → Gateways, so nothing was sent.");
            default:
                throw new DeliveryTargetException(OfflineRefusal(facts.GatewayMachine ?? facts.GatewayName));
        }
    }

    /// <summary>Why a deployment whose gateway is offline was not sent, naming where to look.</summary>
    internal static string OfflineRefusal(string? machine) =>
        GatewayCommandService.OfflineMessage.TrimEnd('.')
        + ", so nothing was sent. Check that the gateway service is running"
        + (string.IsNullOrWhiteSpace(machine) ? "" : $" on {machine}")
        + ", then deploy again.";

    /// <summary>Nothing to renew: the gateway signs its own requests.</summary>
    public Task RenewAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// The tier as the gateway last read it, with what its settings allow. Read from the
    /// inventory the environment row is itself written from, so nothing is written back.
    /// </summary>
    public async Task<DeliveryTargetStatus> ReadStatusAsync(CancellationToken ct)
    {
        var facts = Facts;
        if (facts.InstanceMissing)
        {
            return DeliveryTargetStatus.Missing(
                $"The gateway no longer reports {facts.TierName}, so nothing was sent. Check the service tier with the customer's IT.");
        }
        if (facts.TenantMissing)
        {
            return DeliveryTargetStatus.Missing(
                $"The gateway no longer reports the tenant {facts.TenantId} on {facts.TierName}, so nothing was sent. Check the tenant with the customer's IT.");
        }
        var refusal = facts.PolicyRefusal;
        if (refusal is null && facts.InstanceState is { Length: > 0 } state
            && !string.Equals(state.Trim(), "Running", StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"The gateway last read {facts.TierName} as {BcEnvironmentStatus.StatusWord(state).ToLowerInvariant()}, so it can't take an install. Start the service tier, then deploy again.";
        }
        // The restart after the install would reach other solutions' environments, which
        // the customer's settings must say is allowed (#1368, shared_restart). Only AL
        // Workbench knows which solutions are linked where, so it is checked here.
        if (refusal is null)
        {
            refusal = (await RestartGroupPlan.LoadAsync(_db, facts, ct))?.Refusal;
        }
        return new DeliveryTargetStatus(true, facts.EnvironmentType, facts.EnvironmentStatus, refusal, ReadLive: false);
    }

    /// <summary>
    /// The apps the tenant has installed, as the gateway last read them. The gateway reads
    /// them again itself before it changes anything, so a stale list costs at most a
    /// skipped app the gateway would have found installed anyway.
    /// </summary>
    public async Task<IReadOnlyList<BcInstalledApp>> ListInstalledAppsAsync(CancellationToken ct)
    {
        var facts = Facts;
        var tenant = (facts.TenantId ?? OnPremisesEnvironmentService.DefaultTenant).ToLower();
        var rows = await _db.GatewayInstanceApps.AsNoTracking()
            .Where(a => a.Tenant!.InstanceId == facts.InstanceId
                        && a.Tenant.TenantId.ToLower() == tenant
                        && a.MissingSince == null
                        && a.IsInstalled == true)
            .Select(a => new { a.AppId, a.Name, a.Publisher, a.Version, a.Scope })
            .ToListAsync(ct);
        return rows
            .Where(a => Guid.TryParse(a.AppId, out _))
            .Select(a => new BcInstalledApp(Guid.Parse(a.AppId), a.Name, a.Publisher, a.Version, "Installed",
                string.Equals(a.Scope, "Tenant", StringComparison.OrdinalIgnoreCase) ? "tenant" : a.Scope ?? string.Empty,
                CanBeUninstalled: false, LastOperationId: null, LastUpdateAttemptResult: string.Empty))
            .ToList();
    }

    /// <summary>
    /// Queues the one <c>publish-install</c> command for every app the run sends, in the
    /// order given. Each file is sent by reference to its build artifact; the command
    /// declares each app's id, publisher and version, read from the file's manifest, so the
    /// gateway can check the file is the app it was told about.
    /// </summary>
    public async Task<DeliveryHandOff?> PrepareInstallsAsync(DeliveryInstallBatch batch, CancellationToken ct)
    {
        var facts = Facts;
        if (batch.Packages.Count == 0) return null;
        // Asked again now, not only when the pipeline was saved: a deployment in the
        // delivery window may have been made days before the customer's IT turned Force
        // sync off. Said as the refusal it is, and nothing is sent.
        if (BcSyncMode.Normalize(batch.SchemaSyncMode) == BcSyncMode.ForceSync && !facts.AllowForceSync)
        {
            throw new DeliveryTargetRefusedException(ForceSyncRefusedAtClaim(facts.TierName));
        }

        var files = new List<GatewayCommandFileInput>();
        var apps = new List<GatewayPublishInstallApp>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < batch.Packages.Count; i++)
        {
            var package = batch.Packages[i];
            var label = $"{package.AppName} {package.AppVersion}";
            // One app at a time, so a large build never sits in memory whole.
            var bytes = await package.ReadContentAsync(ct);
            AppManifest? manifest;
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                manifest = AppPackageReader.TryReadManifest(stream);
            }
            if (manifest is null)
            {
                throw new DeliveryTargetException(
                    $"{label} can't be read as an app file (it may be encrypted), so the gateway can't check it. Nothing was sent.");
            }
            if (package.AppId is { } declared && declared != manifest.AppId)
            {
                throw new DeliveryTargetException(
                    $"{label} in the build has the app id {declared}, but the file says {manifest.AppId}. Nothing was sent.");
            }
            // The gateway sees each file by name: two apps of one build may share one.
            var file = string.IsNullOrWhiteSpace(package.FileName) ? $"app-{i + 1}.app" : package.FileName.Trim();
            if (!names.Add(file)) file = $"{i + 1}-{file}";
            names.Add(file);
            files.Add(GatewayCommandFileInput.FromArtifact(file, package.ArtifactId));
            apps.Add(new GatewayPublishInstallApp
            {
                File = file,
                Id = manifest.AppId,
                Name = package.AppName,
                Publisher = manifest.Publisher,
                Version = package.AppVersion,
                Signed = CarriesSignature(bytes),
            });
        }

        var (machine, instance) = facts.PublishingTier;
        var parameters = new GatewayPublishInstallParameters
        {
            Machine = machine,
            Instance = instance,
            // Left out on a single-tenant server, where the gateway uses "default".
            Tenant = facts.Multitenant ? facts.TenantId : null,
            SyncMode = BcSyncMode.Normalize(batch.SchemaSyncMode) == BcSyncMode.ForceSync ? GatewaySyncModes.ForceSync : GatewaySyncModes.Add,
            UnpublishPrevious = batch.UnpublishPrevious,
            Apps = apps,
        };
        try
        {
            _commandId = await _commands.EnqueueAsync(new GatewayCommandRequest(
                facts.GatewayId!.Value,
                GatewayVerb.PublishInstall,
                parameters.ToParameters(),
                WindowEndsAt: batch.WindowEndsAtUtc,
                RequestedByUserId: batch.RequestedByUserId,
                StartedByAgent: batch.StartedByAgent,
                Files: files), ct);
        }
        catch (PlanValidationException ex)
        {
            throw new DeliveryTargetException(string.Join(" ", ex.Errors.Values) + " Nothing was sent.", ex);
        }
        _logger.LogInformation(
            "Queued publish-install command {CommandId} for {Env} on gateway {GatewayId} ({Machine} / {Instance}) with {AppCount} app(s).",
            _commandId, _environment.Name, facts.GatewayId, machine, instance, apps.Count);
        return new DeliveryHandOff(_commandId.Value, facts.GatewayId.Value);
    }

    /// <summary>Why a deployment set to Force sync was not sent, when the tier's settings no longer allow it.</summary>
    internal static string ForceSyncRefusedAtClaim(string tierName) =>
        $"{OnPremisesDeployFacts.NotAllowed}: {tierName} does not allow Force sync, so nothing was sent.";

    /// <summary>
    /// Whether an <c>.app</c> carries anything after its package, which is where a
    /// signature goes: a 40-byte header (<c>NAVX</c>, header length, format version,
    /// package id, package length as 64 bits, <c>NAVX</c>), then the package. A claim the
    /// gateway checks against the file; false for anything it cannot read.
    /// </summary>
    internal static bool CarriesSignature(byte[] bytes)
    {
        const int header = 40;
        if (bytes.Length < header) return false;
        var span = bytes.AsSpan();
        if (!span[..4].SequenceEqual("NAVX"u8) || !span[36..40].SequenceEqual("NAVX"u8)) return false;
        var packageLength = BinaryPrimitives.ReadUInt64LittleEndian(span[28..36]);
        return packageLength < (ulong)(bytes.Length - header);
    }

    /// <summary>Never called: <see cref="PrepareInstallsAsync"/> hands the run off with the whole batch.</summary>
    public Task<BcAppOperation> StartInstallAsync(
        DeliveryPackage package, string schedule, string schemaSyncMode, CancellationToken ct) =>
        throw new InvalidOperationException("The gateway installs the whole batch as one command; the run is handed off instead.");

    /// <summary>Never called: the deployment is finished from the command's result by DeliveryService.</summary>
    public Task<DeliveryInstallOutcome> WaitForInstallAsync(
        BcAppOperation started, TimeSpan pollDelay, TimeSpan timeout,
        Func<IReadOnlyList<string>, CancellationToken, Task>? onProgress, CancellationToken ct) =>
        throw new InvalidOperationException("The gateway installs the whole batch as one command; the run is handed off instead.");

    /// <summary>
    /// The command's log lines without the workbench's time stamp, which the deployment's
    /// own log puts back: "2026-10-15 09:12:01Z text" becomes "text".
    /// </summary>
    internal static List<string> GatewayLines(string log)
    {
        var lines = new List<string>();
        foreach (var raw in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.TrimEnd('\r');
            // "yyyy-MM-dd HH:mm:ssZ " is 21 characters.
            if (line.Length > 21 && line[4] == '-' && line[10] == ' ' && line[19] == 'Z' && line[20] == ' ')
            {
                line = line[21..];
            }
            if (!string.IsNullOrWhiteSpace(line)) lines.Add(line.Trim());
        }
        return lines;
    }

    /// <summary>
    /// One app's outcome from the finished command. The gateway lists every app it was
    /// given, in order. A command with no result for the app is read by how far it got:
    /// one the gateway never collected (expired, taken back) changed nothing, so its message
    /// is the first app's failure; one refused before the verb ran says "Nothing was
    /// changed." and is a refusal; any other one the gateway had collected may have
    /// installed some of the apps before it stopped, so each is unconfirmed, not failed.
    /// </summary>
    internal static DeliveryInstallOutcome OutcomeFor(GatewayCommandProgress final, int index, string appVersion)
    {
        var result = GatewayPublishInstallResult.FromJson(final.Result);
        var restarted = result?.Restarted;
        var commandFailure = string.IsNullOrWhiteSpace(final.FailureMessage) ? null : final.FailureMessage.Trim();
        var app = result is not null && index < result.Apps.Count ? result.Apps[index] : null;
        if (app is null)
        {
            var nothingChanged = commandFailure?.EndsWith(NothingWasChanged, StringComparison.Ordinal) == true;
            if (final.LeftQueue && !nothingChanged)
            {
                var why = commandFailure is null ? "" : " " + (commandFailure.EndsWith('.') ? commandFailure : commandFailure + ".");
                return new DeliveryInstallOutcome(false, UnconfirmedAppMessage + why, Raw: final.Result?.ToJsonString())
                { Outcome = DeliveryAppOutcome.Unconfirmed, TiersRestarted = restarted };
            }
            if (nothingChanged && GatewayRefusalAdvice.IsSettingsRefusal(commandFailure))
            {
                return new DeliveryInstallOutcome(false, commandFailure, Raw: final.Result?.ToJsonString())
                { Outcome = DeliveryAppOutcome.Refused, TiersRestarted = restarted };
            }
            var failure = commandFailure ?? "The gateway reported the deployment as failed without saying why.";
            return new DeliveryInstallOutcome(false, failure, Raw: final.Result?.ToJsonString()) { TiersRestarted = restarted };
        }

        var signer = app.SignerSubject is { Length: > 0 } subject ? $" Signed by {subject}." : string.Empty;
        var message = string.IsNullOrWhiteSpace(app.Message) ? null : app.Message.Trim();
        return app.Outcome switch
        {
            GatewayPublishInstallOutcomes.Installed =>
                new DeliveryInstallOutcome(true, (message ?? "Installed.") + signer) { TiersRestarted = restarted },
            GatewayPublishInstallOutcomes.Upgraded =>
                new DeliveryInstallOutcome(true,
                    (message ?? (app.FromVersion is { Length: > 0 } from ? $"Upgraded from {from}." : "Upgraded.")) + signer)
                { FromVersion = app.FromVersion, TiersRestarted = restarted },
            GatewayPublishInstallOutcomes.AlreadyInstalled =>
                new DeliveryInstallOutcome(false, message ?? $"Already on {appVersion}.")
                { Outcome = DeliveryAppOutcome.AlreadyInstalled, TiersRestarted = restarted },
            GatewayPublishInstallOutcomes.Refused =>
                new DeliveryInstallOutcome(false, message ?? commandFailure ?? "The gateway refused this app.", Raw: final.Result?.ToJsonString())
                { Outcome = DeliveryAppOutcome.Refused, TiersRestarted = restarted },
            GatewayPublishInstallOutcomes.Skipped =>
                new DeliveryInstallOutcome(false, message ?? "Not started, because another app of this deployment did not go through.")
                { Outcome = DeliveryAppOutcome.Skipped, TiersRestarted = restarted },
            _ =>
                new DeliveryInstallOutcome(false, message ?? commandFailure ?? "The gateway reported this app as failed.", Raw: final.Result?.ToJsonString())
                { TiersRestarted = restarted },
        };
    }

    /// <summary>How the gateway ends a message about a command that ran nothing.</summary>
    internal const string NothingWasChanged = "Nothing was changed.";

    /// <summary>An app whose fate the gateway did not report, though it may have started on it.</summary>
    internal const string UnconfirmedAppMessage =
        "Not confirmed: the gateway stopped before it said what became of this app. Check the environment's apps after the gateway's next inventory.";

    /// <summary>
    /// The sessions open across every service tier on the environment's database, as the
    /// gateways last counted them: a session on any of them sees the new version once it is
    /// installed. Tiers are matched on the database's server, instance and name, however
    /// each gateway spelled them (<see cref="DatabaseKey"/>). A tier whose gateway is not
    /// online is left out, since its count may be hours old, and the question says how
    /// many were. On a multitenant server only the environment's own tenant is counted.
    /// </summary>
    public async Task<OpenSessionsCheck> CheckOpenSessionsAsync(CancellationToken ct)
    {
        var facts = Facts;
        var now = Now;
        var tiers = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.MissingSince == null || i.Id == facts.InstanceId)
            .Select(i => new
            {
                i.Id, i.DatabaseServer, i.DatabaseInstance, i.DatabaseName, i.SessionCount,
                GatewayMachine = i.Gateway!.MachineName,
                GatewayState = i.Gateway.State,
                GatewayLastSeenAt = i.Gateway.LastSeenAt,
                GatewayRevokedAt = i.Gateway.RevokedAt,
                TenantSessions = i.Tenants
                    .Where(t => t.MissingSince == null)
                    .Select(t => new { t.TenantId, t.SessionCount })
                    .ToList(),
            })
            .ToListAsync(ct);
        var linked = tiers.FirstOrDefault(t => t.Id == facts.InstanceId);
        if (linked is null)
        {
            return OpenSessionsCheck.Unknown(_environment.Name, "The gateway no longer reports this service tier.");
        }
        var key = DatabaseKey.For(linked.DatabaseServer, linked.DatabaseInstance, linked.DatabaseName, linked.GatewayMachine);
        var sharing = key is null
            ? [linked]
            : tiers.Where(t => t.Id == linked.Id
                || DatabaseKey.For(t.DatabaseServer, t.DatabaseInstance, t.DatabaseName, t.GatewayMachine) == key).ToList();
        bool Online(int id) => sharing.First(t => t.Id == id) is var t
            && GatewayService.StatusOf(t.GatewayState, t.GatewayLastSeenAt, t.GatewayRevokedAt, now) == GatewayStatus.Online;
        // The linked tier's own gateway is online, or the run would not have got this far.
        var counted = sharing.Where(t => t.Id == linked.Id || Online(t.Id)).ToList();
        var offline = sharing.Count - counted.Count;

        var tenant = facts.TenantId ?? OnPremisesEnvironmentService.DefaultTenant;
        int? total = null;
        foreach (var tier in counted)
        {
            int? count = facts.Multitenant
                ? tier.TenantSessions.FirstOrDefault(t => Same(t.TenantId, tenant))?.SessionCount
                : tier.SessionCount;
            if (count is { } n) total = (total ?? 0) + n;
        }
        return total is { } sessions
            ? OpenSessionsCheck.OnPremises(_environment.Name, sessions, counted.Count, offline)
            : OpenSessionsCheck.Unknown(_environment.Name, "The gateway hasn't counted the sessions on these service tiers yet.");
    }

    public Task<IReadOnlyList<BcScheduledPteOperation>> ListHeldInstallsAsync(CancellationToken ct) =>
        throw new InvalidOperationException("An on-premises service tier holds no installs for a later update.");

    public Task CancelHeldInstallAsync(Guid appId, string version, string schedule, CancellationToken ct) =>
        throw new InvalidOperationException("An on-premises service tier holds no installs for a later update.");

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
