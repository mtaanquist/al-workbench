using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALWorkbench.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>One service tier on a gateway's page.</summary>
public sealed record GatewayInstanceRow(
    int Id,
    string Machine,
    string Name,
    string Environment,
    string? State,
    string? Version,
    string? ServiceState,
    string? DatabaseServer,
    string? DatabaseInstance,
    string? DatabaseName,
    bool? Multitenant,
    int TenantCount,
    int AppCount,
    int? SessionCount,
    bool HasLicence,
    string? Licensee,
    DateOnly? LicenceExpiresOn,
    DateTime ReportedAt,
    DateTime? ReadAt,
    DateTime? MissingSince,
    string? ReadError,
    IReadOnlyDictionary<string, string> Unavailable)
{
    /// <summary><c>SERVER\INSTANCE / Database</c>, or null when the configuration was never read.</summary>
    public string? Database => DatabaseName is null && DatabaseServer is null
        ? null
        : $"{DatabaseServer}{(string.IsNullOrEmpty(DatabaseInstance) ? string.Empty : "\\" + DatabaseInstance)} / {DatabaseName}";
}

/// <summary>
/// Another post from the same gateway created the same new row at the same moment.
/// The endpoint answers 409 and the gateway sends the report again.
/// </summary>
public sealed class GatewayInventoryConflictException : Exception
{
    public GatewayInventoryConflictException(int gatewayId, Exception inner)
        : base($"Another report from gateway {gatewayId} added the same service tier, tenant or app at the same moment. Send this one again.", inner)
    {
    }
}

/// <summary>A gateway's page: the gateway, the tiers it reports, and whether a refresh is on its way.</summary>
/// <param name="RefreshRequestedAt">When the refresh still waiting for the gateway was asked for; null when none is.</param>
public sealed record GatewayInventoryView(
    GatewaySummary Gateway,
    IReadOnlyList<GatewayInstanceRow> Instances,
    DateTime? RefreshRequestedAt);

/// <summary>
/// The on-premises mirror: stores what a gateway's inventory reports
/// (<c>POST /gateway/inventory</c>) and reads it back for the gateway's page. See
/// <c>.design/on-prem-gateway.md</c>, "Data model" and "Inventory, as built".
///
/// <para><b>Stable upsert.</b> A tier is found by gateway + machine + instance name,
/// a tenant by its id within the tier, an app by its id and version within the
/// tenant, all compared without regard to case. Rows keep their ids across reports;
/// one that stops being reported is stamped <c>missing_since</c> and cleared when it
/// comes back, never deleted. A list the gateway cut to size
/// (<c>tenantsTruncated</c>, <c>appsTruncated</c>) marks nothing missing, since what
/// was cut is unknown, and a tenant whose apps are not in a report keeps the ones
/// stored. App rows are only written when something about them changed, so a
/// report every 15 minutes is a handful of updates, not a rewrite.</para>
///
/// <para><b>Bounds.</b> A report over <see cref="GatewayInventoryLimits"/> is refused
/// whole with a field-keyed message. The gateway cuts its lists before sending, so
/// only a gateway that does not can send one; refusing is clearer than storing part
/// of it silently.</para>
///
/// <para>Runs under the query filter throughout: the gateway's requests carry its
/// organisation claim, and the Admin reads run in the Admin's request. Nothing here is
/// audited; the rows are what a server reported, not anyone's decision.</para>
/// </summary>
public sealed class GatewayInventoryService
{
    /// <summary>Longest machine or instance name stored.</summary>
    public const int MaxNameLength = 255;

    /// <summary>Longest app id or version stored: a GUID is 36 characters, a version four numbers.</summary>
    public const int MaxAppKeyLength = 64;

    /// <summary>Longest PowerShell version word stored ("5.1", "7"); the column is this wide.</summary>
    public const int MaxPowerShellLength = 10;

    /// <summary>Every other text field is cut to this on the way in, whatever the gateway sent.</summary>
    private static string? Text(string? value) => Cut(value, GatewayInventoryLimits.MaxTextChars);

    private static readonly JsonSerializerOptions Json = new();

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly GatewayCommandService _commands;
    private readonly TimeProvider _clock;
    private readonly ILogger<GatewayInventoryService> _logger;

    public GatewayInventoryService(
        AppDbContext db,
        IOrganizationContext orgContext,
        GatewayCommandService commands,
        TimeProvider clock,
        ILogger<GatewayInventoryService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _commands = commands;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; gateway inventory call made outside an authenticated request.");

    // ---------------------------------------------------------------- the gateway's side

    /// <summary>
    /// Stores one inventory report from the calling gateway. False when the gateway is
    /// gone or revoked.
    /// </summary>
    /// <exception cref="PlanValidationException">The report is a schema this AL Workbench does not know, or over a limit.</exception>
    /// <exception cref="GatewayInventoryConflictException">A concurrent report created the same new row first; nothing was saved.</exception>
    public async Task<bool> ApplyReportAsync(int gatewayId, GatewayInventoryReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        var orgId = RequireOrganizationId();
        Validate(report);

        var live = await _db.Gateways.AsNoTracking()
            .AnyAsync(g => g.Id == gatewayId && g.RevokedAt == null, ct).ConfigureAwait(false);
        if (!live) return false;

        var now = Now;
        var instances = await _db.GatewayInstances
            .Where(i => i.GatewayId == gatewayId)
            .Include(i => i.Tenants)
            .Include(i => i.Licence)
            .ToListAsync(ct).ConfigureAwait(false);

        var applied = new List<GatewayInstance>();
        foreach (var reported in report.Instances)
        {
            var row = instances.FirstOrDefault(i => SameName(i.Machine, reported.Machine) && SameName(i.Name, reported.Name));
            if (row is null)
            {
                row = new GatewayInstance
                {
                    OrganizationId = orgId,
                    GatewayId = gatewayId,
                    FirstReportedAt = now,
                };
                _db.GatewayInstances.Add(row);
                instances.Add(row);
            }
            await ApplyInstanceAsync(orgId, row, reported.Machine.Trim(), reported.Name.Trim(), reported, now, ct).ConfigureAwait(false);
            applied.Add(row);
        }
        await SyncLinkedEnvironmentsAsync(applied, now, ct).ConfigureAwait(false);

        if (report.Roster is { } roster)
        {
            foreach (var row in instances.Where(i => i.MissingSince is null))
            {
                if (!roster.Any(t => SameName(t.Machine, row.Machine) && SameName(t.Instance, row.Name))) row.MissingSince = now;
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Two posts for the same gateway both created the same new tier, tenant
            // or app. Nothing of this one was kept; sent again, it finds the row the
            // other one made and updates it.
            throw new GatewayInventoryConflictException(gatewayId, ex);
        }
        _logger.LogInformation(
            "Gateway {GatewayId} reported {Instances} service tier(s) ({Roster} in its roster).",
            gatewayId, report.Instances.Count, report.Roster?.Count);
        return true;
    }

    private async Task ApplyInstanceAsync(int orgId, GatewayInstance row, string machine, string name,
        GatewayInstanceInventory reported, DateTime now, CancellationToken ct)
    {
        row.Machine = machine;
        row.Name = name;
        row.Environment = reported.Environment is "production" or "test" or "development" ? reported.Environment : "production";
        row.ReportedAt = now;
        row.MissingSince = null;
        row.PowerShell = reported.PowerShell;
        if (reported.Error is not null)
        {
            // Nothing could be read: keep the last known facts and say why.
            row.ReadError = Text(reported.Error);
            row.UnavailableJson = "{}";
            return;
        }

        row.ReadError = null;
        row.ReadAt = now;
        row.UnavailableJson = JsonSerializer.Serialize(
            (reported.Unavailable ?? new Dictionary<string, string>())
                .Where(u => !Blank(u.Key))
                .GroupBy(u => Text(u.Key)!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Text(g.First().Value) ?? string.Empty, StringComparer.Ordinal),
            Json);

        // What is true right now: overwritten, null meaning it could not be read.
        row.State = Text(reported.State);
        row.Version = Text(reported.Version);
        row.ServiceState = Text(reported.Service?.State);
        row.ServiceStartMode = Text(reported.Service?.StartMode);
        row.ServiceAccount = Text(reported.Service?.Account);
        row.SessionCount = reported.SessionCount;
        row.SessionUsersJson = reported.SessionUsers is null
            ? null
            : JsonSerializer.Serialize(reported.SessionUsers.Where(u => !Blank(u)).Select(Text), Json);

        // What changes slowly: kept when this report could not read it.
        if (reported.Configuration is { } configuration)
        {
            row.DatabaseServer = Text(configuration.DatabaseServer);
            row.DatabaseInstance = Text(configuration.DatabaseInstance);
            row.DatabaseName = Text(configuration.DatabaseName);
            row.Multitenant = configuration.Multitenant;
            row.ClientServicesPort = configuration.ClientServicesPort;
            row.SoapServicesPort = configuration.SoapServicesPort;
            row.ODataServicesPort = configuration.ODataServicesPort;
            row.ManagementServicesPort = configuration.ManagementServicesPort;
            row.DeveloperServicesPort = configuration.DeveloperServicesPort;
            row.PublicWebBaseUrl = Text(configuration.PublicWebBaseUrl);
        }
        if (reported.Tenants is { } tenants)
        {
            row.TenantsTruncated = reported.TenantsTruncated;
            await ApplyTenantsAsync(orgId, row, tenants, reported.TenantsTruncated, now, ct).ConfigureAwait(false);
        }
        if (reported.Licence is { } licence)
        {
            await ApplyLicenceAsync(orgId, row, licence, now, ct).ConfigureAwait(false);
        }
    }

    private async Task ApplyTenantsAsync(int orgId, GatewayInstance instance, IReadOnlyList<GatewayTenantInventory> tenants,
        bool truncated, DateTime now, CancellationToken ct)
    {
        foreach (var reported in tenants)
        {
            var id = reported.Id.Trim();
            var tenant = instance.Tenants.FirstOrDefault(t => SameName(t.TenantId, id));
            if (tenant is null)
            {
                tenant = new GatewayInstanceTenant { OrganizationId = orgId, TenantId = id };
                instance.Tenants.Add(tenant);
            }
            tenant.TenantId = id;
            tenant.State = Text(reported.State);
            tenant.DatabaseName = Text(reported.DatabaseName);
            tenant.SessionCount = reported.SessionCount;
            tenant.ReadAt = now;
            tenant.MissingSince = null;
            if (reported.Apps is { } apps)
            {
                tenant.AppsTruncated = reported.AppsTruncated;
                tenant.AppsReadAt = now;
                await ApplyAppsAsync(orgId, tenant, apps, reported.AppsTruncated, now, ct).ConfigureAwait(false);
            }
        }
        if (truncated) return;
        foreach (var tenant in instance.Tenants.Where(t => t.MissingSince is null))
        {
            if (!tenants.Any(t => SameName(t.Id.Trim(), tenant.TenantId))) tenant.MissingSince = now;
        }
    }

    private async Task ApplyAppsAsync(int orgId, GatewayInstanceTenant tenant, IReadOnlyList<GatewayAppInventory> apps,
        bool truncated, DateTime now, CancellationToken ct)
    {
        if (tenant.Id != 0)
        {
            await _db.Entry(tenant).Collection(t => t.Apps).LoadAsync(ct).ConfigureAwait(false);
        }
        var seen = new List<GatewayInstanceApp>();
        foreach (var reported in apps)
        {
            var appId = reported.AppId.Trim();
            var version = reported.Version.Trim();
            var app = tenant.Apps.FirstOrDefault(a => SameName(a.AppId, appId) && SameName(a.Version, version));
            if (app is null)
            {
                app = new GatewayInstanceApp { OrganizationId = orgId, AppId = appId, Version = version };
                tenant.Apps.Add(app);
            }
            else if (seen.Contains(app))
            {
                continue;
            }
            seen.Add(app);
            // Assigned only when different, so an unchanged app is not an UPDATE.
            Set(app.Name, Text(reported.Name), v => app.Name = v);
            Set(app.Publisher, Text(reported.Publisher), v => app.Publisher = v);
            SetNullable(app.DataVersion, Cut(reported.DataVersion, MaxAppKeyLength), v => app.DataVersion = v);
            SetNullable(app.Scope, Cut(reported.Scope, 20), v => app.Scope = v);
            SetNullable(app.SyncState, Cut(reported.SyncState, 40), v => app.SyncState = v);
            if (app.IsInstalled != reported.IsInstalled) app.IsInstalled = reported.IsInstalled;
            if (app.IsPublished != reported.IsPublished) app.IsPublished = reported.IsPublished;
            if (app.MissingSince is not null) app.MissingSince = null;
        }
        if (truncated) return;
        foreach (var app in tenant.Apps.Where(a => a.MissingSince is null && !seen.Contains(a)))
        {
            app.MissingSince = now;
        }
    }

    private async Task ApplyLicenceAsync(int orgId, GatewayInstance instance, GatewayLicenceInventory reported, DateTime now, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reported.Text)));
        var licence = instance.Licence;
        if (licence is not null && licence.TextSha256 == hash)
        {
            licence.ReadAt = now;
            return;
        }
        if (licence is null)
        {
            licence = new GatewayInstanceLicence { OrganizationId = orgId };
            instance.Licence = licence;
        }
        else
        {
            // A new licence: its ranges replace the old ones whole (a range has no
            // identity anything else points at); the cleared ones are deleted as orphans.
            await _db.Entry(licence).Collection(l => l.Ranges).LoadAsync(ct).ConfigureAwait(false);
            licence.Ranges.Clear();
        }
        var parsed = GatewayLicenceParser.Parse(reported.Text);
        licence.Text = reported.Text;
        licence.TextSha256 = hash;
        licence.Truncated = reported.Truncated;
        licence.Licensee = parsed.Licensee;
        licence.SerialNumber = parsed.SerialNumber;
        licence.ExpiresOn = parsed.ExpiresOn;
        licence.ParseProblems = parsed.Problems.Count == 0 ? null : string.Join("\n", parsed.Problems);
        licence.ReadAt = now;
        foreach (var range in parsed.Ranges)
        {
            licence.Ranges.Add(new GatewayInstanceLicenceRange
            {
                OrganizationId = orgId,
                ObjectType = range.ObjectType,
                FromId = range.FromId,
                ToId = range.ToId,
                Permissions = range.Permissions,
            });
        }
    }

    /// <summary>
    /// Brings every Solution environment linked to one of <paramref name="applied"/> in
    /// line with what was just stored: type, version, status, web client address, and its
    /// installed apps (#1366). In the same save as the report, so a linked environment
    /// never says something the mirror does not. A tier new in this report cannot be
    /// linked yet, so only stored ones are looked up.
    /// </summary>
    private async Task SyncLinkedEnvironmentsAsync(List<GatewayInstance> applied, DateTime now, CancellationToken ct)
    {
        var ids = applied.Where(i => i.Id != 0).Select(i => i.Id).Distinct().ToList();
        if (ids.Count == 0) return;
        var linked = await _db.OeProjectEnvironments
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises && e.MissingSince == null
                        && e.GatewayInstanceId != null && ids.Contains(e.GatewayInstanceId.Value))
            .ToListAsync(ct).ConfigureAwait(false);
        if (linked.Count == 0) return;

        var linkedIds = linked.Select(e => e.Id).ToList();
        var mirrors = (await _db.OeEnvironmentApps
                .Where(a => linkedIds.Contains(a.EnvironmentId))
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(a => a.EnvironmentId);
        foreach (var environment in linked)
        {
            var instance = applied.First(i => i.Id == environment.GatewayInstanceId);
            var tenant = instance.Tenants.FirstOrDefault(t => environment.TenantId is not null && SameName(t.TenantId, environment.TenantId));
            // A tenant the gateway stopped reporting: its environment keeps its last facts,
            // which the readers then show as no longer reported (EnvironmentQueries.StillReported),
            // rather than having the server's newer version written onto a tenant that is gone.
            if (tenant?.MissingSince is not null) continue;
            OnPremisesEnvironmentService.Apply(environment, instance, now);
            if (tenant is null) continue;
            var apps = _db.Entry(tenant).Collection(t => t.Apps);
            if (tenant.Id != 0 && !apps.IsLoaded) await apps.LoadAsync(ct).ConfigureAwait(false);
            OnPremisesEnvironmentService.SyncApps(_db, environment, tenant, mirrors[environment.Id].ToList(), now);
        }
    }

    /// <summary>The limits, checked before anything is written. Field-keyed for the gateway's log.</summary>
    internal static void Validate(GatewayInventoryReport report)
    {
        var errors = new Dictionary<string, string>();
        if (report.SchemaVersion != GatewayInventoryReport.CurrentSchemaVersion)
        {
            errors["schemaVersion"] = $"This AL Workbench reads inventory reports of version {GatewayInventoryReport.CurrentSchemaVersion}, not {report.SchemaVersion}. Update the gateway, or AL Workbench.";
            throw new PlanValidationException(errors);
        }
        if (report.Instances is null)
        {
            errors["instances"] = "The report has no list of service tiers; send an empty list when there is none.";
        }
        var instances = report.Instances ?? [];
        if (report.Roster is { Count: > GatewayInventoryLimits.MaxRosterTiers })
        {
            errors["roster"] = $"A report may list at most {GatewayInventoryLimits.MaxRosterTiers} service tiers.";
        }
        if (report.Roster?.Any(t => t is null || Blank(t.Machine) || Blank(t.Instance)) == true)
        {
            errors["roster"] = "Every service tier in the roster needs a machine and an instance name.";
        }
        if (instances.Count > GatewayInventoryLimits.MaxInstancesPerReport)
        {
            errors["instances"] = $"A report may carry at most {GatewayInventoryLimits.MaxInstancesPerReport} service tiers.";
        }
        foreach (var instance in instances)
        {
            if (instance is null || Blank(instance.Machine) || Blank(instance.Name)
                || instance.Machine.Trim().Length > MaxNameLength || instance.Name.Trim().Length > MaxNameLength)
            {
                errors["instances"] = $"Every service tier needs a machine and an instance name of at most {MaxNameLength} characters.";
                break;
            }
            var tier = $"{instance.Machine} / {instance.Name}";
            if (instance.Tenants is { Count: > GatewayInventoryLimits.MaxTenantsPerInstance })
            {
                errors["tenants"] = $"{tier} reports more than {GatewayInventoryLimits.MaxTenantsPerInstance} tenants. The gateway should send only the first ones, marked as cut.";
            }
            foreach (var tenant in instance.Tenants ?? [])
            {
                if (tenant is null || Blank(tenant.Id) || tenant.Id.Trim().Length > MaxNameLength)
                {
                    errors["tenants"] = $"{tier} reports a tenant without an id, or with one longer than {MaxNameLength} characters.";
                    break;
                }
                if (tenant.Apps is { Count: > GatewayInventoryLimits.MaxAppsPerTenant })
                {
                    errors["apps"] = $"{tier} reports more than {GatewayInventoryLimits.MaxAppsPerTenant} apps for tenant {tenant.Id}. The gateway should send only the first ones, marked as cut.";
                }
                if (tenant.Apps?.Any(a => a is null || Blank(a.AppId) || Blank(a.Version)
                        || a.AppId.Trim().Length > MaxAppKeyLength || a.Version.Trim().Length > MaxAppKeyLength) == true)
                {
                    errors["apps"] = $"{tier} reports an app for tenant {tenant.Id} without an id and version, or with one longer than {MaxAppKeyLength} characters.";
                }
            }
            if (instance.SessionUsers is { Count: > GatewayInventoryLimits.MaxSessionUsers })
            {
                errors["sessionUsers"] = $"{tier} reports more than {GatewayInventoryLimits.MaxSessionUsers} signed-in users.";
            }
            if (instance.PowerShell is { Length: > MaxPowerShellLength })
            {
                errors["powerShell"] = $"{tier} names its PowerShell in more than {MaxPowerShellLength} characters; send \"5.1\" or \"7\".";
            }
            if (instance.Licence is { Text: null })
            {
                errors["licence"] = $"{tier} reports licence information without its text; leave the licence out when it was not read.";
            }
            else if (instance.Licence is { } licence && licence.Text.Length > GatewayInventoryLimits.MaxLicenceTextChars)
            {
                errors["licence"] = $"{tier} reports licence information longer than {GatewayInventoryLimits.MaxLicenceTextChars} characters. The gateway should cut it, marked as cut.";
            }
            if (instance.Unavailable is { Count: > GatewayInventoryLimits.MaxUnavailable })
            {
                errors["unavailable"] = $"{tier} lists more than {GatewayInventoryLimits.MaxUnavailable} parts it could not read.";
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);
    }

    // ---------------------------------------------------------------- Admin reads and actions

    /// <summary>A gateway's page. Null when there is no such gateway in the organisation. Admin only.</summary>
    public async Task<GatewayInventoryView?> GetViewAsync(int gatewayId, CancellationToken ct = default)
    {
        await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
        var now = Now;
        var gateway = await _db.Gateways.AsNoTracking()
            .Where(g => g.Id == gatewayId)
            .Select(g => new
            {
                g.Id, g.Name, g.MachineName, g.State, g.KeyFingerprint, g.RegisteredAt, g.KeyRegisteredAt,
                g.LastSeenAt, g.ReportedVersion, g.ApprovedAt, g.RevokedAt,
                ApprovedBy = g.ApprovedByUser == null ? null : g.ApprovedByUser.DisplayName,
            })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (gateway is null) return null;
        var summary = new GatewaySummary(
            gateway.Id, gateway.Name, gateway.MachineName,
            GatewayService.StatusOf(gateway.State, gateway.LastSeenAt, gateway.RevokedAt, now), gateway.KeyFingerprint,
            gateway.RegisteredAt, gateway.KeyRegisteredAt, gateway.LastSeenAt, gateway.ReportedVersion,
            gateway.ApprovedAt, gateway.ApprovedBy, gateway.RevokedAt);

        var rows = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.GatewayId == gatewayId)
            .OrderBy(i => i.MissingSince != null)
            .ThenBy(i => i.Machine)
            .ThenBy(i => i.Name)
            .Select(i => new
            {
                i.Id, i.Machine, i.Name, i.Environment, i.State, i.Version, i.ServiceState,
                i.DatabaseServer, i.DatabaseInstance, i.DatabaseName, i.Multitenant, i.SessionCount,
                i.ReportedAt, i.ReadAt, i.MissingSince, i.ReadError, i.UnavailableJson,
                Tenants = i.Tenants.Count(t => t.MissingSince == null),
                Apps = i.Tenants.Where(t => t.MissingSince == null)
                    .SelectMany(t => t.Apps)
                    .Count(a => a.MissingSince == null && a.IsInstalled != false),
                HasLicence = i.Licence != null,
                Licensee = i.Licence == null ? null : i.Licence.Licensee,
                ExpiresOn = i.Licence == null ? null : i.Licence.ExpiresOn,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var refresh = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId && c.Verb == GatewayVerb.RefreshInventory
                        && (c.Status == GatewayCommandStatus.Queued || c.Status == GatewayCommandStatus.Claimed
                            || c.Status == GatewayCommandStatus.Running))
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => (DateTime?)c.CreatedAt)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return new GatewayInventoryView(
            summary,
            rows.Select(i => new GatewayInstanceRow(
                i.Id, i.Machine, i.Name, i.Environment, i.State, i.Version, i.ServiceState,
                i.DatabaseServer, i.DatabaseInstance, i.DatabaseName, i.Multitenant,
                i.Tenants, i.Apps, i.SessionCount, i.HasLicence, i.Licensee, i.ExpiresOn,
                i.ReportedAt, i.ReadAt, i.MissingSince, i.ReadError, ReadUnavailable(i.UnavailableJson))).ToList(),
            refresh);
    }

    /// <summary>
    /// Asks the gateway to read its tiers now (a <c>refresh-inventory</c> command for
    /// every tier its policy file lets it read). Admin only. When one is already
    /// waiting, nothing new is queued and its id is returned.
    /// </summary>
    /// <exception cref="PlanValidationException">Keyed <c>gateway</c> when the gateway is gone, revoked or not approved.</exception>
    public async Task<long> RequestRefreshAsync(int gatewayId, CancellationToken ct = default)
    {
        await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
        var open = await _db.GatewayCommands.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId && c.Verb == GatewayVerb.RefreshInventory
                        && (c.Status == GatewayCommandStatus.Queued || c.Status == GatewayCommandStatus.Claimed
                            || c.Status == GatewayCommandStatus.Running))
            .Select(c => (long?)c.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (open is { } existing) return existing;
        var id = await _commands.EnqueueAsync(new GatewayCommandRequest(
            gatewayId, GatewayVerb.RefreshInventory, RequestedByUserId: _orgContext.CurrentUserId), ct).ConfigureAwait(false);
        _logger.LogInformation("User {UserId} asked gateway {GatewayId} for a fresh inventory (command {CommandId}).",
            _orgContext.CurrentUserId, gatewayId, id);
        return id;
    }

    // ---------------------------------------------------------------- helpers

    private static IReadOnlyDictionary<string, string> ReadUnavailable(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;

    private static void Set(string current, string? value, Action<string> assign)
    {
        var next = value ?? string.Empty;
        if (!string.Equals(current, next, StringComparison.Ordinal)) assign(next);
    }

    private static void SetNullable(string? current, string? value, Action<string?> assign)
    {
        if (!string.Equals(current, value, StringComparison.Ordinal)) assign(value);
    }
}
