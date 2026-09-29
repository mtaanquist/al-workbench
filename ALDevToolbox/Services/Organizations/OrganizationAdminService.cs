using System.Text.RegularExpressions;
using ALDevToolbox.Data;
using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.Tools;
using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Account;
using ALDevToolbox.Services.Mcp;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace ALDevToolbox.Services.Organizations;

/// <summary>
/// Admin-facing view of an organisation's Microsoft sign-in settings.
/// Carries a flag for whether a client secret is stored rather than the
/// secret itself.
/// </summary>
public sealed record OrgEntraView(
    bool Enabled,
    IReadOnlyList<string> AllowedTenantIds,
    string? ClientId,
    bool HasClientSecret,
    bool DeploymentAppConfigured,
    LocalLoginPolicy LocalLoginPolicy);

/// <summary>
/// Input for <see cref="OrganizationAdminService.SaveEntraAsync"/>. An empty
/// <see cref="ClientSecret"/> leaves the stored secret untouched; set
/// <see cref="ClearClientSecret"/> to wipe it. Clearing the client id clears
/// the paired secret with it.
/// </summary>
public sealed record OrgEntraInput(
    bool Enabled,
    IReadOnlyList<string> AllowedTenantIds,
    string? ClientId,
    string? ClientSecret,
    bool ClearClientSecret,
    LocalLoginPolicy LocalLoginPolicy = LocalLoginPolicy.AllowAll);

/// <summary>
/// Per-organisation administrative toggles and the email-domain allow-list.
/// Split out of <see cref="OrganizationConfigService"/> (which now sticks to
/// reading/writing the generation config) so the dependencies these actions
/// need — <see cref="IMcpAvailability"/> for the MCP opt-out and
/// <see cref="AuthService"/> for the strong-auth self-check — don't have to be
/// dragged through the config-read path (and the unauthenticated signup flow
/// that touches it). Delegates cache invalidation back to
/// <see cref="OrganizationConfigService.InvalidateCache"/>.
/// </summary>
/// <summary>What Administration → Identity shows outside the Microsoft sign-in form.</summary>
public sealed record OrgIdentityView(
    string Name,
    string Slug,
    bool RequireStrongAuth,
    bool AutoJoinVerifiedDomainUsers,
    bool AdminHasEntraLink,
    int MembersTotal,
    int MembersWithoutEntraLink,
    string? DisplayTimeZoneId = null);

/// <summary>
/// The delivery windows a newly discovered environment starts with, one pair per
/// environment type (issue #962). A pair of nulls means "any time". Clock digits in
/// the customer's zone, copied as they are onto the new environment.
/// </summary>
public sealed record DefaultDeliveryWindows(
    TimeOnly? ProductionStart,
    TimeOnly? ProductionEnd,
    TimeOnly? SandboxStart,
    TimeOnly? SandboxEnd)
{
    public static readonly DefaultDeliveryWindows None = new(null, null, null, null);
}

/// <summary>The current org's tool switches, for Administration → Tools.</summary>
public sealed record OrgToolsView(bool McpEnabled, HashSet<ToolKey> DisabledTools, HashSet<ToolKey> StepUpTools);

public sealed class OrganizationAdminService
{
    private static readonly Regex DomainRegex = new(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9\-]*[a-z0-9])?)+$", RegexOptions.Compiled);

    /// <summary>Data Protection purpose string for per-org Entra client secrets.</summary>
    public const string EntraClientSecretProtectionPurpose = "ALDevToolbox.OrganizationSettings.EntraClientSecret";

    private readonly AppDbContext _db;
    private readonly IOrganizationContext _orgContext;
    private readonly IMcpAvailability _mcpAvailability;
    private readonly AuthService _auth;
    private readonly OrganizationConfigService _config;
    private readonly IDataProtector _entraSecretProtector;
    private readonly ILogger<OrganizationAdminService> _logger;
    private readonly ALDevToolbox.Services.Tools.ToolEnablement _tools;
    private readonly IHttpContextAccessor _http;

    public OrganizationAdminService(
        AppDbContext db,
        IOrganizationContext orgContext,
        IMcpAvailability mcpAvailability,
        AuthService auth,
        OrganizationConfigService config,
        IDataProtectionProvider protectionProvider,
        ALDevToolbox.Services.Tools.ToolEnablement tools,
        IHttpContextAccessor http,
        ILogger<OrganizationAdminService> logger)
    {
        _tools = tools;
        _http = http;
        _db = db;
        _orgContext = orgContext;
        _mcpAvailability = mcpAvailability;
        _auth = auth;
        _config = config;
        _entraSecretProtector = protectionProvider.CreateProtector(EntraClientSecretProtectionPurpose);
        _logger = logger;
    }

    private int RequireOrganizationId() => _orgContext.CurrentOrganizationId
        ?? throw new InvalidOperationException("No organization in scope; service mutation called outside an authenticated request.");

    /// <summary>Lists the email domains claimed by the current organisation.</summary>
    public Task<List<OrganizationEmailDomain>> ListEmailDomainsAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        return _db.OrganizationEmailDomains
            .AsNoTracking()
            .Where(d => d.OrganizationId == orgId)
            .OrderBy(d => d.Domain)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Claims a new email domain for the current organisation. Domains are
    /// globally unique so a successful add blocks every other org from
    /// claiming the same domain — a friendly error surfaces if it's already
    /// taken (whether by this org or another).
    /// </summary>
    public async Task AddEmailDomainAsync(string domain, CancellationToken ct = default)
    {
        var normalised = NormaliseDomain(domain);
        if (!DomainRegex.IsMatch(normalised) || normalised.Length > 253)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = "Enter a valid domain like 'cronus.com'.",
            });
        }

        var orgId = RequireOrganizationId();
        var existing = await _db.OrganizationEmailDomains
            // Fence category 6 (uniqueness probe): email domains are unique deployment-wide, so
            // the claim check has to see past the filter; only the owning org id is read, to
            // choose between the two error messages below.
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.Domain == normalised, ct);
        if (existing is not null)
        {
            var msg = existing.OrganizationId == orgId
                ? "That domain is already on the list."
                : "That domain is claimed by another organisation.";
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["Domain"] = msg,
            });
        }

        _db.OrganizationEmailDomains.Add(new OrganizationEmailDomain
        {
            OrganizationId = orgId,
            Domain = normalised,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} claimed email domain {Domain}.", orgId, normalised);
    }

    /// <summary>Removes one of the current organisation's email-domain claims.</summary>
    public async Task RemoveEmailDomainAsync(int domainId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        // Query filter scopes to the current org, so cross-org IDs return null.
        var row = await _db.OrganizationEmailDomains.FirstOrDefaultAsync(d => d.Id == domainId, ct);
        if (row is null) return;
        _db.OrganizationEmailDomains.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} released email domain {Domain}.", orgId, row.Domain);
    }

    /// <summary>
    /// Toggles the per-org MCP opt-out. Refuses when the site-wide
    /// <see cref="IMcpAvailability"/> is off — the UI also disables the
    /// checkbox in that case but the service enforces it independently so a
    /// forged POST can't flip a hidden setting.
    /// </summary>
    public async Task SetMcpEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        if (!_mcpAvailability.IsEnabled)
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["McpEnabled"] = "MCP is disabled site-wide. Ask a site admin to enable it first.",
            });
        }
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        if (org.McpEnabled == enabled) return;
        org.McpEnabled = enabled;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Org {OrgId} set MCP enabled = {Enabled}.", orgId, enabled);
    }

    /// <summary>
    /// Replaces the set of tools this organisation has switched off (stored as
    /// <see cref="ToolKey"/> names on <see cref="Organization.DisabledTools"/>).
    /// MCP is excluded — it's toggled through <see cref="SetMcpEnabledAsync"/> —
    /// and the value is de-duplicated. A site-disabled tool left in the set is
    /// harmless (it's hidden by the site toggle regardless), but the org Tools
    /// page only offers site-enabled tools so it won't add one.
    /// </summary>
    public async Task SetDisabledToolsAsync(IEnumerable<ToolKey> disabled, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        var normalised = ToolCatalog.Format(disabled.Where(k => k != ToolKey.Mcp).Distinct());
        // Order-insensitive no-op check so saving an unchanged form is free.
        if (org.DisabledTools.ToHashSet().SetEquals(normalised)) return;
        org.DisabledTools = normalised;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Org {OrgId} set disabled tools = [{Tools}].", orgId, string.Join(',', normalised));
    }

    /// <summary>
    /// Replaces the set of tools this organisation wants a recent second factor
    /// for (<see cref="Organization.StepUpTools"/>). Any tool may be listed:
    /// its pages go behind the route gate (MCP's own page included), and its
    /// writing MCP tools follow it through <see cref="McpToolDescriptor.Tool"/>.
    /// Takes effect for signed-in members on their next cookie refresh; MCP,
    /// OAuth consent and the action-level checks read the row directly.
    /// Changing the set while any tool is marked, before or after, needs a
    /// fresh session itself: otherwise a stale admin session could simply
    /// switch the rule off.
    /// </summary>
    public async Task SetStepUpToolsAsync(IEnumerable<ToolKey> stepUp, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.FirstAsync(o => o.Id == orgId, ct);
        var normalised = ToolCatalog.Format(stepUp.Distinct());
        if (org.StepUpTools.ToHashSet().SetEquals(normalised)) return;
        if ((org.StepUpTools.Count > 0 || normalised.Count > 0) && !_tools.IsFresh(_http.HttpContext?.User))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["StepUp"] = $"Confirm it's you before changing this: open {Endpoints.StepUpEndpoints.Path} in a new tab, confirm, then reload and save again.",
            });
        }
        org.StepUpTools = normalised;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Org {OrgId} set step-up tools = [{Tools}].", orgId, string.Join(',', normalised));
    }

    /// <summary>
    /// Flips the per-org strong-auth requirement. The toggling admin must
    /// themselves satisfy the requirement when turning it on, so an admin
    /// can't accidentally lock themselves out by saving the form before
    /// enrolling a TOTP / passkey. Disabling is unconditional.
    /// </summary>
    public async Task SetRequireStrongAuthAsync(bool enabled, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var userId = _orgContext.CurrentUserId
            ?? throw new InvalidOperationException("No user in scope; SetRequireStrongAuthAsync called outside an authenticated request.");

        if (enabled && !await _auth.HasStrongAuthAsync(userId, ct))
        {
            throw new PlanValidationException(new Dictionary<string, string>
            {
                ["RequireStrongAuth"] = "Set up an authenticator app, email codes or a passkey on your own account before turning this on — otherwise your next request would lock you out of the admin tools.",
            });
        }

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        if (row.RequireStrongAuth == enabled) return;
        row.RequireStrongAuth = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation("Org {OrgId} set require_strong_auth = {Enabled}.", orgId, enabled);
    }

    /// <summary>
    /// Toggles whether a visitor who verifies an email at one of this org's
    /// claimed domains joins as an Active member immediately
    /// (<see cref="OrganizationSettings.AutoJoinVerifiedDomainUsers"/>) instead
    /// of landing Pending for admin approval. No self-lockout guard is needed —
    /// unlike strong-auth this can't lock the acting admin out.
    /// </summary>
    public async Task SetAutoJoinVerifiedDomainUsersAsync(bool enabled, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        if (row.AutoJoinVerifiedDomainUsers == enabled) return;
        row.AutoJoinVerifiedDomainUsers = enabled;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation("Org {OrgId} set auto_join_verified_domain_users = {Enabled}.", orgId, enabled);
    }

    /// <summary>
    /// Sets the zone every time in the app is shown in for this organisation
    /// (<see cref="OrganizationSettings.DisplayTimeZoneId"/>, issue #942). Null,
    /// blank or <c>UTC</c> clears it back to the default, UTC. Anything else must
    /// be a zone this host's time zone database knows, or it is refused with a
    /// field error under <c>DisplayTimeZoneId</c>. Audited like every other
    /// settings column, through the interceptor.
    /// </summary>
    public async Task SetDisplayTimeZoneAsync(string? zoneId, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var trimmed = zoneId?.Trim();
        string? normalised = null;
        if (!string.IsNullOrEmpty(trimmed) && !string.Equals(trimmed, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(trimmed, out _))
            {
                throw new PlanValidationException(new Dictionary<string, string>
                {
                    [nameof(OrganizationSettings.DisplayTimeZoneId)] =
                        $"'{trimmed}' is not a time zone we recognise. Pick one from the list, e.g. Europe/Copenhagen.",
                });
            }
            normalised = trimmed;
        }

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        if (row.DisplayTimeZoneId == normalised) return;
        row.DisplayTimeZoneId = normalised;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation("Org {OrgId} set display_time_zone_id = {ZoneId}.", orgId, normalised ?? "UTC");
    }

    /// <summary>
    /// Sets the delivery windows new Production and Sandbox environments start with
    /// (issue #962). Each pair is both-or-neither, the same rule as an environment's own
    /// window editor; a half-filled pair is refused under
    /// <c>DefaultDeliveryWindowProduction</c> or <c>DefaultDeliveryWindowSandbox</c> and
    /// nothing is saved. Only discovery reads it, and only when it creates a row: no
    /// existing environment changes. Audited through the interceptor like the other
    /// settings columns. See <c>.design/saas-delivery.md</c> ("Update window").
    /// </summary>
    public async Task SetDefaultDeliveryWindowsAsync(DefaultDeliveryWindows windows, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var errors = new Dictionary<string, string>();
        if (windows.ProductionStart is null != (windows.ProductionEnd is null))
        {
            errors["DefaultDeliveryWindowProduction"] = "Set both a start and an end time, or clear both for 'any time'.";
        }
        if (windows.SandboxStart is null != (windows.SandboxEnd is null))
        {
            errors["DefaultDeliveryWindowSandbox"] = "Set both a start and an end time, or clear both for 'any time'.";
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        if (row.DefaultDeliveryWindowProductionStart == windows.ProductionStart
            && row.DefaultDeliveryWindowProductionEnd == windows.ProductionEnd
            && row.DefaultDeliveryWindowSandboxStart == windows.SandboxStart
            && row.DefaultDeliveryWindowSandboxEnd == windows.SandboxEnd)
        {
            return;
        }
        row.DefaultDeliveryWindowProductionStart = windows.ProductionStart;
        row.DefaultDeliveryWindowProductionEnd = windows.ProductionEnd;
        row.DefaultDeliveryWindowSandboxStart = windows.SandboxStart;
        row.DefaultDeliveryWindowSandboxEnd = windows.SandboxEnd;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation(
            "Org {OrgId} set default delivery windows: Production {ProductionStart}-{ProductionEnd}, Sandbox {SandboxStart}-{SandboxEnd}.",
            orgId, windows.ProductionStart, windows.ProductionEnd, windows.SandboxStart, windows.SandboxEnd);
    }

    /// <summary>
    /// The current org's per-tool state for Administration → Tools: whether MCP
    /// is on, and the stored disabled-tool set (kept whole, including tools that
    /// are hidden site-wide, so a later site re-enable keeps this org's choice).
    /// </summary>
    public async Task<OrgToolsView> GetToolsViewAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new { o.McpEnabled, o.DisabledTools, o.StepUpTools })
            .FirstAsync(ct);
        return new OrgToolsView(org.McpEnabled, ToolCatalog.ParseDisabled(org.DisabledTools), ToolCatalog.ParseKeys(org.StepUpTools));
    }

    /// <summary>
    /// The rest of what Administration → Identity opens with: the org's name and
    /// slug, the two org-wide auth toggles, and the blast-radius numbers for the
    /// Microsoft-only policy (how many active members would lose password
    /// sign-in, and whether any active admin could still get in with Microsoft).
    /// </summary>
    public async Task<OrgIdentityView> GetIdentityViewAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var org = await _db.Organizations.AsNoTracking().FirstAsync(o => o.Id == orgId, ct);
        var settings = await _db.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => new { s.RequireStrongAuth, s.AutoJoinVerifiedDomainUsers, s.DisplayTimeZoneId })
            .FirstOrDefaultAsync(ct);

        // Query filters scope all three of these to this org. The provider
        // predicate keeps GitHub account links (issue #621), which share this
        // table but sign nobody in, out of the Microsoft counts.
        var adminHasEntraLink = await _db.UserExternalLogins
            .AnyAsync(l => l.Provider == EntraSignInService.ProviderName
                && l.User!.Role == UserRole.Admin && l.User.Status == UserStatus.Active, ct);
        var membersTotal = await _db.Users.CountAsync(u => u.Status == UserStatus.Active, ct);
        var linkedMembers = await _db.UserExternalLogins
            .Where(l => l.Provider == EntraSignInService.ProviderName && l.User!.Status == UserStatus.Active)
            .Select(l => l.UserId).Distinct().CountAsync(ct);

        return new OrgIdentityView(
            Name: org.Name,
            Slug: org.Slug,
            RequireStrongAuth: settings?.RequireStrongAuth ?? false,
            AutoJoinVerifiedDomainUsers: settings?.AutoJoinVerifiedDomainUsers ?? false,
            AdminHasEntraLink: adminHasEntraLink,
            MembersTotal: membersTotal,
            MembersWithoutEntraLink: Math.Max(0, membersTotal - linkedMembers),
            DisplayTimeZoneId: settings?.DisplayTimeZoneId);
    }

    /// <summary>Loads the current org's Microsoft sign-in settings for the admin form.</summary>
    public async Task<OrgEntraView> GetEntraViewAsync(CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var row = await _db.OrganizationSettings.AsNoTracking()
            .Where(s => s.OrganizationId == orgId)
            .Select(s => new { s.EntraEnabled, s.EntraAllowedTenantIds, s.EntraClientId, s.EntraClientSecretEncrypted, s.LocalLoginPolicy })
            .FirstOrDefaultAsync(ct);
        // Surfaced so the admin form can say whether the shared registration
        // exists before the admin ships a sign-in button that can't work.
        var deploymentApp = await _db.SystemSettings.AsNoTracking()
            .AnyAsync(s => s.Id == 1 && s.EntraClientId != null, ct);
        return new OrgEntraView(
            Enabled: row?.EntraEnabled ?? false,
            AllowedTenantIds: row?.EntraAllowedTenantIds ?? new List<string>(),
            ClientId: row?.EntraClientId,
            HasClientSecret: !string.IsNullOrEmpty(row?.EntraClientSecretEncrypted),
            DeploymentAppConfigured: deploymentApp,
            LocalLoginPolicy: row?.LocalLoginPolicy ?? LocalLoginPolicy.AllowAll);
    }

    /// <summary>
    /// Saves the org's Microsoft sign-in settings. Tenant ids must be GUIDs
    /// (stored lowercased, de-duplicated). Turning sign-in on requires at
    /// least one tenant id and a usable app registration — either this org's
    /// own client id or the deployment-wide one — so an admin can't enable a
    /// sign-in button that could never work. The tenant allow-list is the
    /// security boundary for multi-tenant registrations (see issue #552);
    /// this method only stores it, the sign-in callback enforces it.
    /// </summary>
    public async Task SaveEntraAsync(OrgEntraInput input, CancellationToken ct = default)
    {
        var orgId = RequireOrganizationId();
        var errors = new Dictionary<string, string>();

        var tenantIds = new List<string>();
        foreach (var raw in input.AllowedTenantIds)
        {
            var trimmed = (raw ?? string.Empty).Trim().ToLowerInvariant();
            if (trimmed.Length == 0) continue;
            if (!Guid.TryParse(trimmed, out _))
            {
                errors["EntraAllowedTenantIds"] = $"'{trimmed}' is not a tenant ID. Enter the Directory (tenant) ID - a GUID like 00000000-0000-0000-0000-000000000000.";
                break;
            }
            if (!tenantIds.Contains(trimmed)) tenantIds.Add(trimmed);
        }

        var clientId = string.IsNullOrWhiteSpace(input.ClientId) ? null : input.ClientId.Trim().ToLowerInvariant();
        if (clientId is not null && !Guid.TryParse(clientId, out _))
        {
            errors["EntraClientId"] = "Enter the app registration's Application (client) ID - a GUID like 00000000-0000-0000-0000-000000000000.";
        }
        if (clientId is null && !string.IsNullOrEmpty(input.ClientSecret))
        {
            errors["EntraClientSecret"] = "Enter the Application (client) ID before saving a client secret.";
        }

        if (input.Enabled)
        {
            if (tenantIds.Count == 0)
            {
                errors.TryAdd("EntraAllowedTenantIds", "Add at least one tenant ID before turning Microsoft sign-in on - without one, nobody could sign in.");
            }
            if (clientId is null)
            {
                // SystemSettings is deliberately outside the org query filter,
                // so this read needs no IgnoreQueryFilters.
                var hasDeploymentApp = await _db.SystemSettings.AsNoTracking()
                    .AnyAsync(s => s.Id == 1 && s.EntraClientId != null, ct);
                if (!hasDeploymentApp)
                {
                    errors.TryAdd("EntraEnabled", "Microsoft sign-in isn't configured for this deployment yet. Ask a site admin to set it up, or enter your own app registration below.");
                }
            }
        }
        if (input.LocalLoginPolicy == LocalLoginPolicy.EntraOnly)
        {
            if (!input.Enabled)
            {
                errors.TryAdd("LocalLoginPolicy", "Turn Microsoft sign-in on before making it the only way in.");
            }
            else
            {
                // Lockout guard, same spirit as the last-admin guards: the
                // policy refuses to flip until at least one admin has a
                // working Microsoft link. SiteAdmin password login survives
                // regardless as break-glass (enforced in AuthService).
                var adminHasLink = await _db.UserExternalLogins
                    .AnyAsync(l => l.Provider == EntraSignInService.ProviderName
                        && l.User!.OrganizationId == orgId
                        && l.User.Role == UserRole.Admin
                        && l.User.Status == UserStatus.Active, ct);
                if (!adminHasLink)
                {
                    errors.TryAdd("LocalLoginPolicy", "Before requiring Microsoft sign-in, at least one admin must have connected a Microsoft account - otherwise everyone could be locked out. Sign in with Microsoft once, or connect it from your account page, then try again.");
                }
            }
        }
        if (errors.Count > 0) throw new PlanValidationException(errors);

        var row = await _db.OrganizationSettings.FirstOrDefaultAsync(s => s.OrganizationId == orgId, ct);
        if (row is null)
        {
            row = new OrganizationSettings { OrganizationId = orgId };
            _db.OrganizationSettings.Add(row);
        }
        row.EntraEnabled = input.Enabled;
        row.LocalLoginPolicy = input.LocalLoginPolicy;
        row.EntraAllowedTenantIds = tenantIds;
        row.EntraClientId = clientId;
        if (clientId is null || input.ClearClientSecret)
        {
            row.EntraClientSecretEncrypted = null;
        }
        else if (!string.IsNullOrEmpty(input.ClientSecret))
        {
            row.EntraClientSecretEncrypted = _entraSecretProtector.Protect(input.ClientSecret);
        }
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _config.InvalidateCache(orgId);
        _logger.LogInformation(
            "Org {OrgId} saved Entra settings (enabled={Enabled}, tenants={TenantCount}, own_app={OwnApp}).",
            orgId, row.EntraEnabled, tenantIds.Count, clientId is not null);
    }

    private static string NormaliseDomain(string? input)
    {
        var trimmed = (input ?? string.Empty).Trim().ToLowerInvariant();
        if (trimmed.StartsWith('@')) trimmed = trimmed[1..];
        return trimmed;
    }
}
