using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Entities.ObjectExplorer;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.ObjectExplorer.Bc;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Gateways;

/// <summary>
/// What an on-premises environment row knows about the service tier behind it, from the
/// on-premises mirror. Read with the environment, through the Solution's visibility, so
/// whoever may see the Solution sees the tier's facts; the gateway's own page stays
/// Admin only.
/// </summary>
/// <param name="PolicyEnvironment">The policy file's word for the tier: <c>production</c>, <c>test</c> or <c>development</c>.</param>
/// <param name="WebClientUrl">The environment's web client address, as the inventory wrote it.</param>
/// <param name="ReadAt">When the gateway last read the tier; null when it never has.</param>
/// <param name="InstanceMissingSince">Set when the gateway stopped reporting the tier.</param>
public sealed record OnPremisesTierFacts(
    int GatewayId,
    string GatewayName,
    GatewayState GatewayState,
    DateTime? GatewayLastSeenAt,
    DateTime? GatewayRevokedAt,
    int InstanceId,
    string Machine,
    string InstanceName,
    string TenantId,
    string PolicyEnvironment,
    bool? Multitenant,
    string? DatabaseServer,
    string? DatabaseInstance,
    string? DatabaseName,
    string? ServiceState,
    int? SessionCount,
    DateTime ReportedAt,
    DateTime? ReadAt,
    DateTime? InstanceMissingSince,
    string? ReadError,
    DateOnly? LicenceExpiresOn,
    string? Licensee,
    string? WebClientUrl)
{
    /// <summary>
    /// How long the mirror may go unread before the page says so. The gateway reads every
    /// 15 minutes; an hour is four rounds missed, past any slow server or retry.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>The gateway's state at <paramref name="now"/>, as the Gateways page words it.</summary>
    public GatewayStatus GatewayStatusAt(DateTime now) =>
        GatewayService.StatusOf(GatewayState, GatewayLastSeenAt, GatewayRevokedAt, now);

    /// <summary>True when the gateway is not online at <paramref name="now"/>.</summary>
    public bool IsGatewayOffline(DateTime now) => GatewayStatusAt(now) != GatewayStatus.Online;

    /// <summary>
    /// True when what the page shows is older than <see cref="StaleAfter"/>, or the gateway
    /// no longer reports the tier at all. Either way the facts are the last ones known.
    /// </summary>
    public bool IsStale(DateTime now) =>
        InstanceMissingSince is not null || ReadAt is not { } read || now - read > StaleAfter;

    /// <summary>The policy file's word as copy: "Production", "Test" or "Development".</summary>
    public string EnvironmentLabel => OnPremisesEnvironmentService.EnvironmentLabel(PolicyEnvironment);

    /// <summary><c>SERVER / INSTANCE</c>, how customer IT names a tier.</summary>
    public string TierName => $"{Machine} / {InstanceName}";

    /// <summary><c>SERVER\INSTANCE / Database</c>, or null when the configuration was never read.</summary>
    public string? Database => DatabaseName is null && DatabaseServer is null
        ? null
        : $"{DatabaseServer}{(string.IsNullOrEmpty(DatabaseInstance) ? string.Empty : "\\" + DatabaseInstance)} / {DatabaseName}";
}

/// <summary>One service tier and tenant an Admin can link, as the link dialog lists it.</summary>
/// <param name="LinkedToProjectName">The Solution it is linked to already; null when it is free.</param>
/// <param name="LinkedToThisSolution">True when it is linked to the Solution the dialog is for.</param>
public sealed record LinkableTier(
    int GatewayId,
    string GatewayName,
    GatewayStatus GatewayStatus,
    int InstanceId,
    string Machine,
    string InstanceName,
    string PolicyEnvironment,
    string? Version,
    bool Multitenant,
    string TenantId,
    string? LinkedToProjectName,
    bool LinkedToThisSolution)
{
    public bool IsFree => LinkedToProjectName is null;

    public string EnvironmentLabel => OnPremisesEnvironmentService.EnvironmentLabel(PolicyEnvironment);
}

/// <summary>What the Admin picked in the link dialog.</summary>
public sealed record LinkTierInput(int? InstanceId, string? TenantId, string? Name);

/// <summary>
/// Links a service tier an on-premises gateway reports to a Solution, which makes it an
/// <see cref="OeProjectEnvironment"/> with <see cref="OeEnvironmentKind.OnPremises"/>, and
/// keeps linked rows in step with the mirror. See <c>.design/on-prem-gateway.md</c>, "An
/// on-premises environment is an OeProjectEnvironment row".
///
/// <para><b>Admin only.</b> Linking and unlinking re-check that the caller is an org Admin
/// here, not only on the page: a tier on a shared hosting gateway may serve several
/// customers, and only an Admin decides which Solution may deploy to, restart or
/// relicense it. Both are audited (the interceptor writes a row for an on-premises
/// environment born or linked again, or unlinked).</para>
///
/// <para>Runs under the query filter throughout; no <c>IgnoreQueryFilters()</c>.</para>
/// </summary>
public sealed class OnPremisesEnvironmentService
{
    /// <summary>Longest environment name; the column is this wide. Mirrored as <c>maxlength</c> in the link dialog.</summary>
    public const int MaxNameLength = 200;

    /// <summary>A single-tenant server's one tenant, which is how it is stored so the unique index cannot be dodged.</summary>
    public const string DefaultTenant = "default";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly TimeProvider _clock;
    private readonly ILogger<OnPremisesEnvironmentService> _logger;

    public OnPremisesEnvironmentService(
        AppDbContext db,
        IOrganizationContext orgContext,
        TimeProvider clock,
        ILogger<OnPremisesEnvironmentService> logger)
    {
        _db = db;
        _orgContext = orgContext;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>True when the caller may link and unlink service tiers: an org Admin, or a SiteAdmin inside an organisation.</summary>
    public async Task<bool> CanLinkAsync(CancellationToken ct = default)
    {
        try
        {
            await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every service tier and tenant the organisation's approved gateways report, for the
    /// link dialog of <paramref name="projectId"/>, with the Solution each is linked to
    /// already. Admin only. Tiers the gateway no longer reports, and revoked gateways, are
    /// left out: neither can be linked.
    /// </summary>
    public async Task<List<LinkableTier>> ListLinkableTiersAsync(int projectId, CancellationToken ct = default)
    {
        await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
        var now = Now;
        var instances = await _db.GatewayInstances.AsNoTracking()
            .Where(i => i.MissingSince == null
                        && i.Gateway!.RevokedAt == null
                        && i.Gateway.State == GatewayState.Approved)
            .Select(i => new
            {
                i.Id, i.GatewayId, GatewayName = i.Gateway!.Name, i.Gateway.State, i.Gateway.LastSeenAt, i.Gateway.RevokedAt,
                i.Machine, i.Name, i.Environment, i.Version, i.Multitenant,
                Tenants = i.Tenants.Where(t => t.MissingSince == null).Select(t => t.TenantId).ToList(),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var linked = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises && e.MissingSince == null && e.GatewayInstanceId != null)
            .Select(e => new { e.GatewayInstanceId, e.TenantId, e.ProjectId, ProjectName = e.Project!.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var rows = new List<LinkableTier>();
        foreach (var i in instances)
        {
            // A single-tenant server always has its one tenant, read or not.
            var tenants = i.Tenants.Count > 0 || i.Multitenant == true ? i.Tenants : [DefaultTenant];
            foreach (var tenant in tenants)
            {
                var link = linked.FirstOrDefault(l => l.GatewayInstanceId == i.Id && l.TenantId is not null && SameTenant(l.TenantId, tenant));
                rows.Add(new LinkableTier(
                    i.GatewayId, i.GatewayName, GatewayService.StatusOf(i.State, i.LastSeenAt, i.RevokedAt, now),
                    i.Id, i.Machine, i.Name, i.Environment, i.Version, i.Multitenant == true, tenant,
                    link?.ProjectName, link?.ProjectId == projectId));
            }
        }
        return rows
            .OrderBy(r => r.GatewayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Machine, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.TenantId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Links a service tier and tenant to <paramref name="projectId"/> under the name
    /// given, and returns the environment's id. Linking a tier this Solution had before
    /// revives that row, so its deployment pipelines and history come back with it. Admin
    /// only.
    /// </summary>
    /// <exception cref="PlanValidationException">Keyed <c>HostingType</c>, <c>InstanceId</c>, <c>TenantId</c> or <c>Name</c>.</exception>
    public async Task<int> LinkAsync(int projectId, LinkTierInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
        var orgId = _orgContext.CurrentOrganizationId
            ?? throw new InvalidOperationException("Linking a service tier needs an authenticated request.");

        var project = await _db.OeProjects.AsNoTracking()
            .Where(p => p.Id == projectId && p.DeletedAt == null)
            .Select(p => new { p.Id, p.Name, p.HostingType })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw Invalid("Name", "This Solution no longer exists.");
        if (!OeProject.IsOnPremisesHosting(project.HostingType))
        {
            throw Invalid("HostingType",
                "This Solution is set to run in Microsoft's cloud. Change where it is hosted on its Customer tab first, then link its service tiers.");
        }

        var errors = new Dictionary<string, string>();
        var name = input.Name?.Trim();
        if (string.IsNullOrEmpty(name)) errors["Name"] = "Give the environment a name, e.g. Production.";
        else if (name.Length > MaxNameLength) errors["Name"] = $"Keep the name under {MaxNameLength} characters.";

        GatewayInstance? instance = null;
        if (input.InstanceId is not { } instanceId)
        {
            errors["InstanceId"] = "Choose a service tier.";
        }
        else
        {
            instance = await _db.GatewayInstances
                .Include(i => i.Gateway)
                .Include(i => i.Tenants)
                .FirstOrDefaultAsync(i => i.Id == instanceId, ct).ConfigureAwait(false);
            if (instance is null || instance.Gateway is null || instance.Gateway.RevokedAt is not null)
            {
                errors["InstanceId"] = "That service tier is no longer reported by a gateway. Choose another.";
                instance = null;
            }
            else if (instance.Gateway.State != GatewayState.Approved)
            {
                errors["InstanceId"] = "That gateway is waiting for an Admin to approve it. Approve it under Administration → Gateways first.";
                instance = null;
            }
            else if (instance.MissingSince is not null)
            {
                errors["InstanceId"] = "The gateway no longer reports that service tier. Choose another, or check the gateway's policy file.";
                instance = null;
            }
        }

        var tenantId = string.IsNullOrWhiteSpace(input.TenantId) ? null : input.TenantId.Trim();
        GatewayInstanceTenant? tenant = null;
        if (instance is not null)
        {
            if (instance.Multitenant == true)
            {
                tenant = tenantId is null ? null
                    : instance.Tenants.FirstOrDefault(t => t.MissingSince is null && SameTenant(t.TenantId, tenantId));
                if (tenant is null) errors["TenantId"] = "Choose one of the tenants this service tier reports.";
            }
            else
            {
                // A single-tenant server has one tenant, stored as "default" whatever was sent.
                tenant = instance.Tenants.FirstOrDefault(t => SameTenant(t.TenantId, DefaultTenant));
                tenantId = DefaultTenant;
            }
            if (tenant is not null) tenantId = tenant.TenantId;
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var current = await _db.OeProjectEnvironments.AsNoTracking()
            .Where(e => e.Kind == OeEnvironmentKind.OnPremises && e.MissingSince == null
                        && e.GatewayInstanceId == instance!.Id && e.TenantId != null)
            .Select(e => new { e.Id, e.ProjectId, e.Name, e.TenantId, ProjectName = e.Project!.Name })
            .ToListAsync(ct).ConfigureAwait(false);
        if (current.FirstOrDefault(e => SameTenant(e.TenantId!, tenantId!)) is { } taken)
        {
            throw Invalid("InstanceId", taken.ProjectId == projectId
                ? $"This service tier is already linked to this Solution, as {taken.Name}."
                : $"This service tier is already linked to {taken.ProjectName}. Unlink it there first; one service tier and tenant belongs to one Solution.");
        }

        var rows = await _db.OeProjectEnvironments
            .Where(e => e.ProjectId == projectId)
            .ToListAsync(ct).ConfigureAwait(false);
        var revived = rows.FirstOrDefault(e => e.Kind == OeEnvironmentKind.OnPremises
                                               && e.GatewayInstanceId == instance!.Id
                                               && e.TenantId is not null && SameTenant(e.TenantId, tenantId!));
        if (rows.Any(e => e != revived && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("Name", $"This Solution already has an environment called {name}. Choose another name.");
        }

        var now = Now;
        var row = revived;
        if (row is null)
        {
            row = new OeProjectEnvironment
            {
                OrganizationId = orgId,
                ProjectId = projectId,
                Kind = OeEnvironmentKind.OnPremises,
                GatewayInstanceId = instance!.Id,
                TenantId = tenantId,
            };
            _db.OeProjectEnvironments.Add(row);
        }
        row.Name = name!;
        row.TenantId = tenantId;
        row.MissingSince = null;
        Apply(row, instance!, now);
        if (tenant is not null)
        {
            if (tenant.Id != 0) await _db.Entry(tenant).Collection(t => t.Apps).LoadAsync(ct).ConfigureAwait(false);
            var existing = row.Id == 0
                ? []
                : await _db.OeEnvironmentApps.Where(a => a.EnvironmentId == row.Id).ToListAsync(ct).ConfigureAwait(false);
            SyncApps(_db, row, tenant, existing, now);
        }

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Another Admin linked the same tier, or used the name, a moment earlier.
            throw Invalid("InstanceId", "This service tier or name was taken a moment ago. Reload and try again.");
        }

        _logger.LogInformation(
            "User {UserId} linked service tier {InstanceId} tenant {TenantId} to project {ProjectId} as environment {EnvironmentId} ({EnvironmentName}); revived {Revived}.",
            _orgContext.CurrentUserId, instance!.Id, tenantId, projectId, row.Id, row.Name, revived is not null);
        return row.Id;
    }

    /// <summary>
    /// Unlinks an on-premises environment. The row stays, stamped
    /// <see cref="OeProjectEnvironment.MissingSince"/> ("no longer linked"), because a
    /// deployment pipeline's foreign key to it is <c>Restrict</c>; its installed-apps
    /// mirror goes, so the Solution's modules fall back to what was typed. Admin only.
    /// </summary>
    /// <exception cref="PlanValidationException">Keyed <c>Environment</c> when there is no such linked tier.</exception>
    public async Task UnlinkAsync(int projectId, int environmentId, CancellationToken ct = default)
    {
        await GatewayService.EnsureAdminAsync(_db, _orgContext, ct).ConfigureAwait(false);
        var row = await _db.OeProjectEnvironments
            .FirstOrDefaultAsync(e => e.Id == environmentId && e.ProjectId == projectId
                                      && e.Kind == OeEnvironmentKind.OnPremises, ct).ConfigureAwait(false)
            ?? throw Invalid("Environment", "That service tier is not linked to this Solution.");
        if (row.MissingSince is not null) return;

        row.MissingSince = Now;
        // Removed in the same save as the stamp, so the two cannot come apart.
        _db.OeEnvironmentApps.RemoveRange(
            await _db.OeEnvironmentApps.Where(a => a.EnvironmentId == row.Id).ToListAsync(ct).ConfigureAwait(false));
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "User {UserId} unlinked environment {EnvironmentId} ({EnvironmentName}) from project {ProjectId}.",
            _orgContext.CurrentUserId, row.Id, row.Name, projectId);
    }

    // ---------------------------------------------------------------- keeping linked rows in step

    /// <summary>
    /// Writes what the mirror knows about <paramref name="instance"/> onto a linked row:
    /// type, version, status and web client address, so the pages that read those need
    /// no branching. The type is the policy file's: <c>production</c> is
    /// <c>Production</c>, <c>test</c> and <c>development</c> are <c>Sandbox</c>, the
    /// values the existing checks compare against. A running tier reads as
    /// <see cref="BcEnvironmentStatus.Active"/> for the same reason; any other state is
    /// kept as the server said it.
    /// </summary>
    internal static void Apply(OeProjectEnvironment row, GatewayInstance instance, DateTime now)
    {
        row.Type = TypeFor(instance.Environment);
        row.Version = Cut(instance.Version, 50);
        row.Status = instance.State is null
            ? null
            : string.Equals(instance.State.Trim(), "Running", StringComparison.OrdinalIgnoreCase)
                ? BcEnvironmentStatus.Active
                : Cut(instance.State.Trim(), 50);
        row.StatusFetchedAt = instance.ReadAt;
        row.WebClientLoginUrl = WebClientUrl(instance, row.TenantId);
        row.FetchedAt = now;
    }

    /// <summary>
    /// Rewrites a linked row's <see cref="OeEnvironmentApp"/> mirror from what the tenant
    /// has installed, so module versions and installed apps read as they do online. Only
    /// what changed is written. A tenant whose apps were never read, or read as none,
    /// leaves the mirror alone, as an online read with no apps does: a tenant always has
    /// the base application.
    /// </summary>
    internal static void SyncApps(AppDbContext db, OeProjectEnvironment row, GatewayInstanceTenant tenant,
        List<OeEnvironmentApp> existing, DateTime now)
    {
        if (tenant.AppsReadAt is null) return;
        var installed = new Dictionary<Guid, GatewayInstanceApp>();
        foreach (var app in tenant.Apps.Where(a => a.MissingSince is null && a.IsInstalled != false))
        {
            if (!Guid.TryParse(app.AppId, out var appId)) continue;
            if (installed.TryGetValue(appId, out var other))
            {
                // Two versions of one app: the installed one, else the newer.
                var better = (app.IsInstalled == true) != (other.IsInstalled == true)
                    ? app.IsInstalled == true
                    : ProjectConnectionService.CompareVersions(app.Version, other.Version) > 0;
                if (!better) continue;
            }
            installed[appId] = app;
        }
        if (installed.Count == 0) return;

        var fetchedAt = tenant.AppsReadAt ?? now;
        foreach (var gone in existing.Where(e => !installed.ContainsKey(e.AppId)))
        {
            db.OeEnvironmentApps.Remove(gone);
        }
        foreach (var (appId, app) in installed)
        {
            var mirror = existing.FirstOrDefault(e => e.AppId == appId);
            if (mirror is null)
            {
                mirror = new OeEnvironmentApp { OrganizationId = row.OrganizationId, Environment = row, AppId = appId };
                db.OeEnvironmentApps.Add(mirror);
            }
            var name = Cut(app.Name, 250) ?? string.Empty;
            var publisher = Cut(app.Publisher, 250) ?? string.Empty;
            var version = Cut(app.Version, 50) ?? string.Empty;
            if (mirror.Name != name) mirror.Name = name;
            if (mirror.Publisher != publisher) mirror.Publisher = publisher;
            if (mirror.Version != version) mirror.Version = version;
            if (mirror.FetchedAt != fetchedAt) mirror.FetchedAt = fetchedAt;
        }
    }

    /// <summary><c>production</c> is Production; <c>test</c> and <c>development</c> are Sandbox.</summary>
    internal static string TypeFor(string? policyEnvironment) =>
        string.Equals(policyEnvironment, "production", StringComparison.OrdinalIgnoreCase) ? "Production" : "Sandbox";

    /// <summary>The policy file's word as on-premises copy shows it.</summary>
    public static string EnvironmentLabel(string? policyEnvironment) =>
        (policyEnvironment ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "test" => "Test",
            "development" => "Development",
            _ => "Production",
        };

    /// <summary>The tier's public web address, with the tenant named on a multitenant server.</summary>
    private static string? WebClientUrl(GatewayInstance instance, string? tenantId)
    {
        if (string.IsNullOrWhiteSpace(instance.PublicWebBaseUrl)) return null;
        var url = instance.PublicWebBaseUrl.Trim();
        if (instance.Multitenant == true && tenantId is not null && !SameTenant(tenantId, DefaultTenant))
        {
            url += (url.Contains('?') ? "&" : "?") + "tenant=" + Uri.EscapeDataString(tenantId);
        }
        return Cut(url, 500);
    }

    private static bool SameTenant(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? Cut(string? value, int max) =>
        value is null ? null : value.Length > max ? value[..max] : value;

    private static PlanValidationException Invalid(string field, string message) =>
        new(new Dictionary<string, string> { [field] = message });
}
