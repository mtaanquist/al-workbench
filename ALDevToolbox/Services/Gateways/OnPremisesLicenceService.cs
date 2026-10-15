using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>The licence a service tier runs, as the gateway last read it.</summary>
/// <param name="ReadAt">When the gateway last read it.</param>
/// <param name="ParseProblems">What could not be read from the licence, one sentence a line; null when nothing.</param>
public sealed record InstalledLicence(
    string? Licensee,
    string? SerialNumber,
    DateOnly? ExpiresOn,
    DateTime ReadAt,
    string? ParseProblems,
    IReadOnlyList<InstalledLicenceRange> Ranges)
{
    /// <summary>Days from <paramref name="today"/> until it expires; null when it states no expiry.</summary>
    public int? DaysLeft(DateOnly today) => ExpiresOn is { } expires ? expires.DayNumber - today.DayNumber : null;

    /// <summary>True when it expires within <see cref="OnPremisesLicenceService.ExpiryWarningDays"/> of <paramref name="today"/>, or has expired.</summary>
    public bool ExpiresSoon(DateOnly today) => DaysLeft(today) is { } days && days <= OnPremisesLicenceService.ExpiryWarningDays;
}

/// <summary>One licensed object range.</summary>
public sealed record InstalledLicenceRange(string ObjectType, int FromId, int ToId);

/// <summary>Where the last licence upload's import stands.</summary>
public enum LicenceImportState
{
    /// <summary>Queued; the gateway has not picked it up yet.</summary>
    Waiting,
    /// <summary>The gateway is importing it, or restarting the service tiers.</summary>
    Running,
    /// <summary>Imported, and every service tier was restarted.</summary>
    Imported,
    /// <summary>Imported, but the restart or the reading afterwards had a problem.</summary>
    ImportedWithProblems,
    /// <summary>Not imported; nothing was restarted.</summary>
    Failed,
}

/// <summary>A licence someone uploaded, with how its import went.</summary>
/// <param name="Message">What went wrong, or what to check, when the import did not simply succeed.</param>
public sealed record LicenceUpload(
    int Id,
    string FileName,
    int Size,
    string LicenceTarget,
    string? UploadedBy,
    DateTime UploadedAt,
    LicenceImportState State,
    string? Message);

/// <summary>What the environment page shows about licences.</summary>
/// <param name="Installed">The licence the linked service tier runs; null when the gateway has not read one.</param>
/// <param name="LastUpload">The newest upload for this environment; null when there has been none.</param>
/// <param name="CanManage">Whether the person may upload a licence or download an uploaded one.</param>
public sealed record OnPremisesLicenceView(InstalledLicence? Installed, LicenceUpload? LastUpload, bool CanManage);

/// <summary>
/// What uploading a licence to one environment will do, as the upload dialog states it
/// before anything is sent: where it is imported, on which tier, which format that tier
/// takes, and which tiers restart.
/// </summary>
/// <param name="LicenceTarget">The database's <c>licence_target</c>; null when the settings name none.</param>
/// <param name="Where">Where the licence goes, in words ("the application database").</param>
/// <param name="ImportTier">The tier that imports it, <c>SERVER / INSTANCE</c>.</param>
/// <param name="Version">The importing tier's Business Central version; null when not known.</param>
/// <param name="Format">The format it imports, <c>.flf</c> or <c>.bclicense</c>; null when the version is not known.</param>
/// <param name="Restart">The tiers that restart afterwards; null when the environment is not linked.</param>
/// <param name="Refusal">Why a licence cannot be imported here at all; null when it can.</param>
public sealed record LicenceImportPlan(
    string? LicenceTarget,
    string Where,
    string ImportTier,
    Version? Version,
    string? Format,
    RestartGroupPlan? Restart,
    string? Refusal);

/// <summary>A licence file to hand back to the person downloading it.</summary>
public sealed record LicenceDownload(string FileName, byte[] Content);

/// <summary>
/// Uploading a new Business Central licence to an on-premises environment and importing it
/// on the service tiers through the gateway (#1369), and what the environment page shows
/// about the licence the tiers run. See <c>.design/on-prem-gateway.md</c>, "Licences".
///
/// <para>The licence follows the Solution: whoever may see the Solution sees the installed
/// licence and the last upload; only people who can manage it upload or download.
/// Everything the gateway would refuse is refused here first, in plainer words, and the
/// <c>shared_restart</c> rule (<see cref="RestartGroupPlan.Refusal"/>) is enforced only
/// here, since only AL Workbench knows which Solutions share a tier. Runs under the query
/// filter throughout; no <c>IgnoreQueryFilters()</c>.</para>
///
/// <para>The upload row and the gateway command are written in one transaction, and the
/// row is never changed afterwards, so its one audit row is the upload. How the import
/// went is read from the command.</para>
/// </summary>
public sealed class OnPremisesLicenceService
{
    /// <summary>How far ahead the page marks a licence as expiring soon; the first expiry notice goes out then too.</summary>
    public const int ExpiryWarningDays = 30;

    /// <summary>The permission a tier needs to import a licence.</summary>
    public const string LicencePermission = "licence";

    /// <summary>The permission each tier of the restart group needs.</summary>
    public const string RestartPermission = "restart";

    /// <summary>The file types the upload takes, mirrored as the file input's <c>accept</c>.</summary>
    public const string Accept = GatewayLicenceFormats.Flf + "," + GatewayLicenceFormats.BcLicense;

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly ProjectAccess _access;
    private readonly GatewayCommandService _commands;
    private readonly TimeProvider _clock;
    private readonly ILogger<OnPremisesLicenceService> _logger;

    public OnPremisesLicenceService(
        AppDbContext db,
        IOrganizationContext orgContext,
        ProjectAccess access,
        GatewayCommandService commands,
        TimeProvider clock,
        ILogger<OnPremisesLicenceService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _access = access;
        _commands = commands;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The installed licence and the last upload for an on-premises environment, or null
    /// when there is no such environment. Throws <see cref="ProjectAccessDeniedException"/>
    /// when the person may not see its Solution.
    /// </summary>
    public async Task<OnPremisesLicenceView?> GetViewAsync(int environmentId, CancellationToken ct = default)
    {
        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new { e.ProjectId, e.GatewayInstanceId, CreatedBy = e.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (env is null) return null;
        await _access.EnsureCanViewAsync(env.ProjectId, ct);

        InstalledLicence? installed = null;
        if (env.GatewayInstanceId is { } instanceId)
        {
            var licence = await _db.GatewayInstanceLicences.AsNoTracking()
                .Where(l => l.InstanceId == instanceId)
                .Select(l => new
                {
                    l.Licensee, l.SerialNumber, l.ExpiresOn, l.ReadAt, l.ParseProblems,
                    Ranges = l.Ranges
                        .OrderBy(r => r.ObjectType).ThenBy(r => r.FromId)
                        .Select(r => new InstalledLicenceRange(r.ObjectType, r.FromId, r.ToId))
                        .ToList(),
                })
                .FirstOrDefaultAsync(ct);
            if (licence is not null)
            {
                installed = new InstalledLicence(licence.Licensee, licence.SerialNumber, licence.ExpiresOn, licence.ReadAt,
                    licence.ParseProblems, licence.Ranges);
            }
        }

        var upload = await _db.OeProjectLicenses.AsNoTracking()
            .Where(l => l.ProjectEnvironmentId == environmentId)
            .OrderByDescending(l => l.UploadedAt).ThenByDescending(l => l.Id)
            .Select(l => new
            {
                l.Id, l.FileName, l.Size, l.LicenceTarget, l.UploadedAt, l.GatewayCommandId,
                UploadedBy = l.UploadedByUser == null ? null : l.UploadedByUser.DisplayName,
            })
            .FirstOrDefaultAsync(ct);
        LicenceUpload? last = null;
        if (upload is not null)
        {
            var (state, message) = await ImportStateAsync(upload.GatewayCommandId, ct);
            last = new LicenceUpload(upload.Id, upload.FileName, upload.Size, upload.LicenceTarget, upload.UploadedBy,
                upload.UploadedAt, state, message);
        }

        var canManage = await _access.CanManageAsync(env.ProjectId, env.CreatedBy, ct);
        return new OnPremisesLicenceView(installed, last, canManage);
    }

    /// <summary>
    /// What importing a licence on <paramref name="environmentId"/> will do, or null when it
    /// is not an on-premises environment. Read for the dialog; the upload asks again.
    /// </summary>
    public async Task<LicenceImportPlan?> GetImportPlanAsync(int environmentId, CancellationToken ct = default)
    {
        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct);
        return facts is null ? null : await PlanAsync(facts, forDialog: true, ct);
    }

    /// <summary>
    /// Stores the licence file and queues its import on the environment's service tiers:
    /// the gateway imports it on the publishing tier, restarts the restart group and reads
    /// the tiers again. Returns the upload's id.
    /// </summary>
    /// <exception cref="ProjectAccessDeniedException">The person may not manage the Solution.</exception>
    /// <exception cref="PlanValidationException">
    /// Keyed <c>file</c> for the file itself (empty, too large, not a licence, or the wrong
    /// format for the tier's version) and <c>environment</c> for anything about where it goes.
    /// Nothing is stored or sent.
    /// </exception>
    public async Task<int> UploadAndImportAsync(int environmentId, string fileName, byte[] content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("No organization in scope; licence upload made outside an authenticated request.");

        var env = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Id == environmentId && e.Kind == OeEnvironmentKind.OnPremises)
            .Select(e => new { e.ProjectId, e.Name, CreatedBy = e.Project!.CreatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (env is null)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["environment"] = "That environment no longer exists, or is not an on-premises one.",
            });
        }
        await _access.EnsureCanManageAsync(env.ProjectId, env.CreatedBy, ct);

        var facts = await OnPremisesDeployFacts.LoadAsync(_db, environmentId, ct)
            ?? throw new PlanValidationException(new Dictionary<string, string> { ["environment"] = "That environment no longer exists." });
        var plan = await PlanAsync(facts, forDialog: false, ct);

        var name = Path.GetFileName((fileName ?? string.Empty).Trim());
        var errors = new Dictionary<string, string>();
        if (name.Length == 0 || name.Length > GatewayCommandService.MaxFileNameLength)
        {
            errors["file"] = "Choose the licence file to upload.";
        }
        else if (content.Length == 0)
        {
            errors["file"] = $"{name} is empty. Choose the licence file again.";
        }
        else if (content.Length > GatewayLicenceFormats.MaxBytes)
        {
            errors["file"] = $"{name} is larger than {GatewayLicenceFormats.MaxBytes / (1024 * 1024)} MB. A Business Central licence is a few kilobytes: check that this is the licence file.";
        }
        else if (GatewayLicenceFormats.FormatOf(name) is null)
        {
            errors["file"] = GatewayLicenceFormats.Refusal(name, plan.Version)!;
        }
        else if (plan.Version is not null && GatewayLicenceFormats.Refusal(name, plan.Version) is { } format)
        {
            errors["file"] = format;
        }
        if (plan.Refusal is not null)
        {
            errors["environment"] = plan.Refusal;
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var (machine, instance) = facts.PublishingTier;
        var target = plan.LicenceTarget!;
        var parameters = new GatewayImportLicenceParameters
        {
            Machine = machine,
            Instance = instance,
            // Only a tenant import names the tenant; the other targets are server-wide.
            Tenant = target == GatewayLicenceTargets.Tenant ? facts.TenantId ?? OnPremisesEnvironmentService.DefaultTenant : null,
            LicenceTarget = target,
        };
        var userId = _orgContext.CurrentUserId;
        var now = Now;

        // The command and the upload that records it become visible together: a crash in
        // between leaves neither, so no licence is imported that the page cannot name.
        long commandId;
        OeProjectLicense row;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                commandId = await _commands.EnqueueAsync(new GatewayCommandRequest(
                    facts.GatewayId!.Value,
                    GatewayVerb.ImportLicence,
                    parameters.ToParameters(),
                    RequestedByUserId: userId,
                    Files: [new GatewayCommandFileInput(name, content)]), ct);
            }
            catch (PlanValidationException ex)
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    ["environment"] = string.Join(" ", ex.Errors.Values) + " Nothing was sent.",
                });
            }
            row = new OeProjectLicense
            {
                OrganizationId = orgId,
                ProjectId = env.ProjectId,
                ProjectEnvironmentId = environmentId,
                FileName = name,
                Content = content,
                Size = content.Length,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(content)),
                LicenceTarget = target,
                UploadedByUserId = userId,
                UploadedAt = now,
                GatewayCommandId = commandId,
            };
            _db.OeProjectLicenses.Add(row);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        _commands.Wake(facts.GatewayId.Value);

        _logger.LogInformation(
            "Licence {FileName} ({Size} bytes) uploaded to environment {EnvironmentId} as upload {LicenceId}; import-licence command {CommandId} queued for {Machine} / {Instance} into {Target}.",
            name, content.Length, environmentId, row.Id, commandId, machine, instance, target);
        return row.Id;
    }

    /// <summary>
    /// An uploaded licence file, for someone who can manage its Solution; null when there is
    /// no such upload. Throws <see cref="ProjectAccessDeniedException"/> otherwise.
    /// </summary>
    public async Task<LicenceDownload?> DownloadAsync(int licenceId, CancellationToken ct = default)
    {
        var row = await _db.OeProjectLicenses.AsNoTracking()
            .Where(l => l.Id == licenceId)
            .Select(l => new { l.ProjectId, CreatedBy = l.Project!.CreatedByUserId, l.FileName, l.Content })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;
        await _access.EnsureCanManageAsync(row.ProjectId, row.CreatedBy, ct);
        return new LicenceDownload(row.FileName, row.Content);
    }

    /// <summary>
    /// The plan for the environment <paramref name="facts"/> were read for. Every refusal
    /// is checked, in the order the gateway would check it, so the first thing the person
    /// is told is the first thing to fix.
    /// </summary>
    private async Task<LicenceImportPlan> PlanAsync(OnPremisesDeployFacts facts, bool forDialog, CancellationToken ct)
    {
        var (machine, instance) = facts.PublishingTier;
        var tierName = $"{machine} / {instance}";
        var database = facts.Policy is { State: GatewayPolicyStates.Valid }
            ? facts.Policy.Databases.FirstOrDefault(d => Same(d.PublishOn.Machine, machine) && Same(d.PublishOn.Instance, instance))
            : null;
        var target = database?.LicenceTarget is { Length: > 0 } t && GatewayLicenceTargets.All.Contains(t) ? t : null;
        var tenant = facts.TenantId ?? OnPremisesEnvironmentService.DefaultTenant;
        var where = Where(target, tenant);

        var publishing = facts.GatewayId is not { } gatewayId ? null : await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.GatewayId == gatewayId && i.MissingSince == null)
            .Where(i => i.Machine.ToLower() == machine.ToLower() && i.Name.ToLower() == instance.ToLower())
            .Select(i => new { i.Version, i.State })
            .FirstOrDefaultAsync(ct);
        var version = ParseVersion(publishing?.Version);
        var format = version is null ? null : GatewayLicenceFormats.For(version.Major);
        var restart = await RestartGroupPlan.LoadAsync(_db, facts, ct);

        var refusal = Refusal();
        return new LicenceImportPlan(target, where, tierName, version, format, restart, refusal);

        string? Refusal()
        {
            if (facts.EnvironmentUnlinked)
            {
                return $"{facts.EnvironmentName} is no longer linked to a service tier. An administrator can link it again on the solution's Environments tab.";
            }
            if (facts.InstanceMissing)
            {
                return $"The gateway no longer reports {facts.TierName}. Check the service tier with the customer's IT.";
            }
            if (facts.TenantMissing)
            {
                return $"The gateway no longer reports the tenant {facts.TenantId} on {facts.TierName}. Check the tenant with the customer's IT.";
            }
            switch (facts.GatewayStatusAt(Now))
            {
                case GatewayStatus.Online:
                    break;
                case GatewayStatus.Revoked:
                    return $"The gateway {facts.GatewayName} has been revoked. An administrator can set up a gateway under Administration → Gateways.";
                case GatewayStatus.WaitingForApproval:
                    return $"The gateway {facts.GatewayName} is waiting for an administrator to approve it under Administration → Gateways.";
                default:
                    return "The gateway is offline. Check that the gateway service is running"
                        + (string.IsNullOrWhiteSpace(facts.GatewayMachine) ? "" : $" on {facts.GatewayMachine}")
                        + ", then try again.";
            }
            var notAllowed = OnPremisesDeployFacts.NotAllowed;
            if (facts.Policy is null)
            {
                return "The gateway has not reported its settings yet, so where a licence would go is not known. Wait a minute for its next check-in, then try again.";
            }
            if (facts.Policy.State != GatewayPolicyStates.Valid)
            {
                return facts.Policy.State switch
                {
                    GatewayPolicyStates.Unsafe =>
                        $"{notAllowed}: the gateway's program files can be changed by people who are not administrators, so it refuses every change until the customer's IT fixes that.",
                    GatewayPolicyStates.Invalid =>
                        $"{notAllowed}: the settings file on the gateway's server has mistakes, so the gateway only reads until the customer's IT corrects it.",
                    _ => $"{notAllowed}: the gateway has no settings file, so it allows nothing yet. Ask the customer's IT to set it up.",
                };
            }
            var entry = facts.PublishingPolicy;
            if (entry is null)
            {
                return $"{notAllowed}: {tierName} is not one of the service tiers the gateway may change. Ask the customer's IT to add it.";
            }
            if (target is null)
            {
                return $"{notAllowed}: they do not say where licences are imported for the database {tierName} runs on. Ask the customer's IT to set that up.";
            }
            if (!entry.Allow.Contains(LicencePermission, StringComparer.OrdinalIgnoreCase))
            {
                return $"{notAllowed}: licences may not be imported on {tierName}. Ask the customer's IT to allow it.";
            }
            if (target == GatewayLicenceTargets.Tenant && !entry.Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))
            {
                return $"{notAllowed}: {tierName} may not act on the tenant {tenant}. Ask the customer's IT to allow it.";
            }
            IReadOnlyList<GatewayPolicyTier> group = database is { Restart.Count: > 0 }
                ? database.Restart
                : [new GatewayPolicyTier { Machine = machine, Instance = instance }];
            var notRestartable = group
                .Where(g => facts.Policy.Instances.FirstOrDefault(i => Same(i.Machine, g.Machine) && Same(i.Name, g.Instance)) is not { } p
                            || !p.Allow.Contains(RestartPermission, StringComparer.OrdinalIgnoreCase))
                .Select(g => $"{g.Machine} / {g.Instance}")
                .ToList();
            if (notRestartable.Count > 0)
            {
                return $"{notAllowed}: {string.Join(", ", notRestartable)} may not be restarted, and a new licence only takes effect when the service tiers restart. Ask the customer's IT to allow it.";
            }
            if (publishing?.State is { Length: > 0 } state && !string.Equals(state.Trim(), "Running", StringComparison.OrdinalIgnoreCase))
            {
                return $"The gateway last read {tierName} as {BcEnvironmentStatus.StatusWord(state).ToLowerInvariant()}, so it can't import a licence. Start the service tier, then try again.";
            }
            if (version is null)
            {
                return $"The Business Central version of {tierName} is not known yet, so which licence format it takes cannot be checked. Wait for the gateway's next reading, then try again.";
            }
            // The restart would reach other Solutions' environments, which the customer's
            // settings must say is allowed. Only AL Workbench knows which Solutions are
            // linked where, so it is refused here and never reaches the gateway. A Tenant
            // import is no exception: the tiers restart for every tenant on them.
            return forDialog ? restart?.DialogRefusal : restart?.Refusal;
        }
    }

    /// <summary>Where a licence goes, in words, for the dialog and the page.</summary>
    public static string Where(string? target, string tenant) => target switch
    {
        GatewayLicenceTargets.NavDatabase => "the application database",
        GatewayLicenceTargets.Master => "the master database, for every tenant",
        GatewayLicenceTargets.Default => "the server's default database",
        GatewayLicenceTargets.Tenant => $"the tenant {tenant} only",
        _ => "nowhere yet",
    };

    /// <summary>How the import a command carries went, from the command row.</summary>
    private async Task<(LicenceImportState State, string? Message)> ImportStateAsync(long? commandId, CancellationToken ct)
    {
        if (commandId is not { } id) return (LicenceImportState.Failed, "The import was never sent.");
        var command = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.Status, c.FailureMessage, c.ResultJson })
            .FirstOrDefaultAsync(ct);
        if (command is null) return (LicenceImportState.Failed, "The gateway's record of the import is gone.");
        var result = ParseResult(command.ResultJson);
        return command.Status switch
        {
            GatewayCommandStatus.Queued or GatewayCommandStatus.Claimed => (LicenceImportState.Waiting, null),
            GatewayCommandStatus.Running => (LicenceImportState.Running, null),
            GatewayCommandStatus.Succeeded when result is { Imported: true, Restart.Restarted: true } && result.Refreshed
                => (LicenceImportState.Imported, null),
            GatewayCommandStatus.Succeeded when result is { Imported: true, Restart.Restarted: true }
                => (LicenceImportState.ImportedWithProblems, result.RefreshMessage),
            GatewayCommandStatus.Succeeded or GatewayCommandStatus.Failed when result is { Imported: true }
                => (LicenceImportState.ImportedWithProblems, command.FailureMessage ?? RestartProblem(result.Restart)),
            GatewayCommandStatus.Succeeded => (LicenceImportState.Imported, null),
            _ => (LicenceImportState.Failed, command.FailureMessage ?? "The import did not finish."),
        };
    }

    private static string? RestartProblem(GatewayRestartGroupResult? restart)
    {
        if (restart is null) return "The service tiers were not restarted, so they keep the old licence until they next start.";
        if (restart.Message is { Length: > 0 } message) return message;
        var failed = restart.Tiers.Where(t => t.Outcome != GatewayRestartTierOutcomes.Restarted)
            .Select(t => $"{t.Machine} / {t.Instance}" + (t.Message is { Length: > 0 } m ? $": {m}" : ""))
            .ToList();
        return failed.Count == 0 ? null : "Not every service tier restarted. " + string.Join(" ", failed);
    }

    private static GatewayImportLicenceResult? ParseResult(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return GatewayImportLicenceResult.FromJson(JsonNode.Parse(json) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A version as the gateway reports it (<c>25.0.12345.0</c>, or with a prefix such as
    /// <c>W1 25.0...</c>); null when there is no version in it.
    /// </summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var part in text.Split([' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (Version.TryParse(part, out var version)) return version;
            if (int.TryParse(part, out var major) && major > 0) return new Version(major, 0);
        }
        return null;
    }

    private static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
